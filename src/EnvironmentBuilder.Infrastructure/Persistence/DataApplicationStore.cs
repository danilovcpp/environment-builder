using EnvironmentBuilder.Core.Operations;
using Microsoft.Data.Sqlite;

namespace EnvironmentBuilder.Infrastructure.Persistence;

public sealed class DataApplicationStore(SqliteWorker worker)
{
    public Task InvalidateAsync(Guid generationId, Guid operationId, string reason, DateTimeOffset at, CancellationToken cancellationToken) =>
        worker.CommitAsync("generation-invalidation", (connection, transaction) =>
        {
            int changed = Sql.Execute(connection, transaction, "UPDATE DataGenerations SET validity='invalidated',invalidatedAt=$at,reason=$reason,operationId=$operation WHERE id=$id AND validity='active'",
                ("$id", generationId.ToString()), ("$operation", operationId.ToString()), ("$reason", reason), ("$at", Sql.Instant(at)));
            if (changed != 1) { throw new InvalidOperationException("An active generation is required."); }
            return true;
        }, cancellationToken);

    public Task SaveAsync(DataApplicationRecord record, CancellationToken cancellationToken) => worker.CommitAsync("application", (connection, transaction) =>
    {
        DataApplicationKey key = record.Key;
        using SqliteCommand environment = Sql.Command(connection, transaction, "SELECT json FROM Environments WHERE id=$id", ("$id", key.EnvironmentId.ToString()));
        string? environmentJson = environment.ExecuteScalar() as string;
        if (environmentJson is null || !System.Text.Json.JsonSerializer.Deserialize<Core.Environments.IsolatedEnvironment>(environmentJson, Core.Environments.CanonicalJson.Options)!.Components.Any(c => c.Id == key.ComponentId))
        { throw new InvalidOperationException("Initialization target is not part of the saved environment."); }
        using SqliteCommand generation = Sql.Command(connection, transaction, "SELECT validity FROM DataGenerations WHERE id=$id AND environmentId=$environment AND baselineHash=$hash",
            ("$id", key.DataGenerationId.ToString()), ("$environment", key.EnvironmentId.ToString()), ("$hash", key.BaselineHash));
        if (!Equals(generation.ExecuteScalar(), "active")) { throw new InvalidOperationException("Success markers require the matching active generation."); }
        (string, object?)[] parameters = KeyParameters(key);
        using SqliteCommand prior = Sql.Command(connection, transaction, "SELECT state,attempt FROM DataApplicationRecords WHERE " + KeyPredicate, parameters);
        using SqliteDataReader row = prior.ExecuteReader();
        string? previous = row.Read() ? row.GetString(0) : null;
        int attempt = previous is null ? 0 : row.GetInt32(1);
        row.Close();
        bool allowed = previous switch
        {
            null => record.State == StepState.Pending && record.Attempt == 0,
            "Pending" => record.State == StepState.Started && record.Attempt == 1,
            "Started" => record.State is StepState.Succeeded or StepState.Failed or StepState.Unknown && record.Attempt == attempt,
            _ => false
        };
        if (!allowed) { throw new InvalidOperationException("Invalid initialization record transition; replay requires an explicit policy decision."); }
        Sql.Execute(connection, transaction,
            "INSERT INTO DataApplicationRecords VALUES($environment,$component,$generation,$baseline,$application,$initializer,$step,$state,$key,$attempt,$started,$completed,$status,$assertions,$error) ON CONFLICT(environmentId,componentId,generationId,baselineHash,applicationHash,initializerHash,stepId) DO UPDATE SET state=$state,attempt=$attempt,startedAt=$started,completedAt=$completed,acceptedStatus=$status,assertionsPassed=$assertions,errorCode=$error",
            [.. parameters, ("$state", record.State.ToString()), ("$key", key.StableIdempotencyKey), ("$attempt", record.Attempt),
                ("$started", record.StartedAt is null ? null : Sql.Instant(record.StartedAt.Value)), ("$completed", record.CompletedAt is null ? null : Sql.Instant(record.CompletedAt.Value)),
                ("$status", record.AcceptedStatus), ("$assertions", record.AssertionsPassed), ("$error", record.ErrorCode)]);
        return true;
    }, cancellationToken);

    public Task<bool> HasSuccessAsync(DataApplicationKey key, CancellationToken cancellationToken) => worker.ReadAsync(connection =>
    {
        using SqliteCommand command = Sql.Command(connection, null,
            "SELECT EXISTS(SELECT 1 FROM DataApplicationRecords WHERE " + KeyPredicate + " AND state='Succeeded' AND generationId IN (SELECT id FROM DataGenerations WHERE validity='active'))", KeyParameters(key));
        return (long)command.ExecuteScalar()! == 1;
    }, cancellationToken);

    private const string KeyPredicate = "environmentId=$environment AND componentId=$component AND generationId=$generation AND baselineHash=$baseline AND applicationHash=$application AND initializerHash=$initializer AND stepId=$step";
    private static (string, object?)[] KeyParameters(DataApplicationKey key) =>
        [("$environment", key.EnvironmentId.ToString()), ("$component", key.ComponentId.ToString()), ("$generation", key.DataGenerationId.ToString()),
            ("$baseline", key.BaselineHash), ("$application", key.ApplicationHash), ("$initializer", key.InitializerHash), ("$step", key.StepId)];
}
