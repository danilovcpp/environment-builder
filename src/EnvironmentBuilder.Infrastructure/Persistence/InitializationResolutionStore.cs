using System.Text.Json;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Core.SeedPacks;
using Microsoft.Data.Sqlite;

namespace EnvironmentBuilder.Infrastructure.Persistence;

public sealed partial class DataApplicationStore
{
    public Task ValidateRefreshAsync(IsolatedEnvironment environment, Guid operation, CancellationToken token) => worker.ReadAsync(connection =>
    {
        using var saved = Sql.Command(connection, null, "SELECT hash FROM Environments WHERE id=$id", ("$id", environment.Id.ToString()));
        if (!Equals(saved.ExecuteScalar(), CanonicalJson.Hash(environment))) { throw new PreflightBlockedException("saved-configuration-changed"); }
        using var unknown = Sql.Command(connection, null, "SELECT EXISTS(SELECT 1 FROM DataApplicationRecords WHERE environmentId=$id AND state IN ('Started','Unknown','Failed') AND generationId IN (SELECT id FROM DataGenerations WHERE validity='active'))", ("$id", environment.Id.ToString()));
        if ((long)unknown.ExecuteScalar()! != 1) { throw new PreflightBlockedException("initialization-decision-stale"); }
        RequireInitializationOnly(connection, null, environment.Id, operation);
        return true;
    }, token);

    public async Task FinishRefreshAsync(Operation operation, CancellationToken token)
    {
        try
        {
            await worker.CommitAsync("initialization-refresh-finalization", (connection, transaction) =>
            {
                using var active = Sql.Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM Environments e JOIN DataGenerations g ON g.id=e.generationId WHERE e.id=$id AND g.id=$next AND g.validity='active' AND g.baselineHash=$baseline AND e.hash=$hash)",
                    ("$id", operation.EnvironmentId.ToString()), ("$next", operation.Plan.NextDataGenerationId?.ToString()), ("$baseline", operation.Plan.BaselineHash), ("$hash", operation.ConfigurationHash));
                if (operation.Kind != "refresh" || operation.State != OperationState.Succeeded || !operation.DataResetCompleted || (long)active.ExecuteScalar()! != 1)
                { throw new InvalidOperationException("Verified refresh generation is required."); }
                CloseInitializationHistory(connection, transaction, operation.EnvironmentId, operation.Id, operation.CompletedAt!.Value);
                Sql.Execute(connection, transaction, "UPDATE Operations SET state='Succeeded',json=$json,completedAt=$at,resultCode=NULL WHERE id=$id",
                    ("$json", JsonSerializer.Serialize(operation, CanonicalJson.Options)), ("$at", Sql.Instant(operation.CompletedAt.Value)), ("$id", operation.Id.ToString()));
                Sql.Execute(connection, transaction, "DELETE FROM PackageReferences WHERE ownerKind='operation' AND ownerId=$id", ("$id", operation.Id.ToString()));
                return true;
            }, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not OperationCanceledException)
        { if (!await IsResolutionCommittedAsync(operation).ConfigureAwait(false)) { throw; } }
    }

    private Task<bool> IsResolutionCommittedAsync(Operation operation) => worker.ReadAsync(connection =>
    {
        using var command = Sql.Command(connection, null, "SELECT json FROM Operations WHERE id=$id AND state='Succeeded'", ("$id", operation.Id.ToString()));
        return command.ExecuteScalar() is string json && JsonSerializer.Deserialize<Operation>(json, CanonicalJson.Options) is { } saved && saved.Plan.Hash == operation.Plan.Hash;
    }, CancellationToken.None);

    private static void RequireInitializationOnly(SqliteConnection connection, SqliteTransaction? transaction, Guid environment, Guid resolution)
    {
        using var uncertain = Sql.Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM OperationSteps s JOIN Operations o ON o.id=s.operationId WHERE o.environmentId=$id AND s.state<>'Succeeded') OR EXISTS(SELECT 1 FROM ResourceLedger WHERE environmentId=$id AND unresolved=1) OR EXISTS(SELECT 1 FROM Operations WHERE environmentId=$id AND id<>$resolution AND state NOT IN ('Succeeded','Failed','Cancelled') AND kind NOT IN ('start','resolve-initialization'))",
            ("$id", environment.ToString()), ("$resolution", resolution.ToString()));
        if ((long)uncertain.ExecuteScalar()! != 0) { throw new PreflightBlockedException("runtime-effects-require-reconciliation"); }
    }
    private static void CloseInitializationHistory(SqliteConnection connection, SqliteTransaction transaction, Guid environment, Guid except, DateTimeOffset at)
    {
        using var starts = Sql.Command(connection, transaction, "SELECT json FROM Operations WHERE environmentId=$id AND kind IN ('start','resolve-initialization') AND id<>$resolution AND state NOT IN ('Succeeded','Failed','Cancelled')", ("$id", environment.ToString()), ("$resolution", except.ToString()));
        var old = new List<Operation>(); using (var rows = starts.ExecuteReader()) { while (rows.Read()) { old.Add(JsonSerializer.Deserialize<Operation>(rows.GetString(0), CanonicalJson.Options)!); } }
        foreach (var operation in old)
        {
            var closed = operation with { State = OperationState.Failed, CompletedAt = at, ResultCode = "initialization-explicitly-resolved" };
            Sql.Execute(connection, transaction, "UPDATE Operations SET state='Failed',completedAt=$at,resultCode=$code,json=$json WHERE id=$id", ("$at", Sql.Instant(at)), ("$code", closed.ResultCode), ("$json", JsonSerializer.Serialize(closed, CanonicalJson.Options)), ("$id", closed.Id.ToString()));
            Sql.Execute(connection, transaction, "DELETE FROM PackageReferences WHERE ownerKind='operation' AND ownerId=$id", ("$id", closed.Id.ToString()));
        }
    }
    public async Task<IReadOnlyList<DataApplicationRecord>> ListUnresolvedAsync(Guid environment, CancellationToken token) =>
        (await ReadRecordsAsync(environment, token).ConfigureAwait(false)).Where(r => r.State is StepState.Started or StepState.Unknown or StepState.Failed).ToArray();
    public Task<IReadOnlyList<DataApplicationRecord>> ReadRecordsAsync(Guid environment, CancellationToken token) => worker.ReadAsync<IReadOnlyList<DataApplicationRecord>>(connection =>
    {
        using var command = Sql.Command(connection, null, "SELECT componentId,generationId,baselineHash,applicationHash,initializerHash,stepId,state,attempt,startedAt,completedAt,acceptedStatus,assertionsPassed,errorCode FROM DataApplicationRecords WHERE environmentId=$id AND generationId IN (SELECT id FROM DataGenerations WHERE validity='active') ORDER BY startedAt", ("$id", environment.ToString()));
        using var rows = command.ExecuteReader(); var result = new List<DataApplicationRecord>();
        while (rows.Read())
        {
            var key = new DataApplicationKey(environment, Guid.Parse(rows.GetString(0)), Guid.Parse(rows.GetString(1)), rows.GetString(2), rows.GetString(3), rows.GetString(4), rows.GetString(5));
            result.Add(new(key, Enum.Parse<StepState>(rows.GetString(6)), rows.GetInt32(7),
                rows.IsDBNull(8) ? null : DateTimeOffset.Parse(rows.GetString(8), System.Globalization.CultureInfo.InvariantCulture),
                rows.IsDBNull(9) ? null : DateTimeOffset.Parse(rows.GetString(9), System.Globalization.CultureInfo.InvariantCulture),
                rows.IsDBNull(10) ? null : rows.GetInt32(10), rows.IsDBNull(11) ? null : rows.GetBoolean(11), rows.IsDBNull(12) ? null : rows.GetString(12)));
        }
        return result;
    }, token);

    public Task<bool> IsReplayAuthorizedAsync(DataApplicationKey key, int attempt, CancellationToken token) => worker.ReadAsync(connection =>
    {
        using var command = Sql.Command(connection, null, "SELECT EXISTS(SELECT 1 FROM InitializationAttempts WHERE idempotencyKey=$key AND attempt=$attempt AND state='RepeatAuthorized')",
            ("$key", key.StableIdempotencyKey), ("$attempt", attempt));
        return (long)command.ExecuteScalar()! == 1;
    }, token);

    public async Task ResolveAsync(IsolatedEnvironment environment, DataApplicationRecord expected, InitializationDecision decision, Operation resolution, CancellationToken token)
    {
        try
        {
            await worker.CommitAsync("initialization-decision", (connection, transaction) =>
        {
            if (decision is not (InitializationDecision.AcceptVerified or InitializationDecision.Repeat)) { throw new InvalidOperationException("Explicit initialization decision is required."); }
            EditFinalization.RequireOriginal(connection, transaction, resolution.Plan);
            DataApplicationKey key = expected.Key;
            BaselineApplication application = environment.Baseline.Applications.Single(a => a.ComponentId == key.ComponentId && CanonicalJson.Hash(a) == key.ApplicationHash && CanonicalJson.Hash(a.Initializer) == key.InitializerHash);
            _ = application.Initializer.Steps.Single(s => s.Id == key.StepId);
            if (key.BaselineHash != CanonicalJson.Hash(environment.Baseline) || key.DataGenerationId != resolution.Plan.DataGenerationId)
            { throw new InvalidOperationException("Baseline or generation changed."); }
            using (var generation = Sql.Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM Environments e JOIN DataGenerations g ON g.id=e.generationId WHERE e.id=$id AND g.id=$generation AND g.validity='active' AND g.baselineHash=$baseline)",
                ("$id", environment.Id.ToString()), ("$generation", key.DataGenerationId.ToString()), ("$baseline", key.BaselineHash)))
            { if ((long)generation.ExecuteScalar()! != 1) { throw new InvalidOperationException("Active generation changed."); } }
            // This decision cannot resolve an uncertain Docker effect or a different destructive operation.
            RequireInitializationOnly(connection, transaction, environment.Id, resolution.Id);
            int changed = Sql.Execute(connection, transaction, "UPDATE DataApplicationRecords SET state=$next,errorCode=$code,completedAt=$at WHERE " + KeyPredicate + " AND state=$previous AND attempt=$attempt AND state IN ('Started','Unknown','Failed')",
                [.. KeyParameters(key), ("$next", decision == InitializationDecision.Repeat ? "Pending" : "Succeeded"), ("$code", decision == InitializationDecision.Repeat ? "user-authorized-repeat" : "user-verified-result"),
                    ("$at", Sql.Instant(resolution.CompletedAt!.Value)), ("$previous", expected.State.ToString()), ("$attempt", expected.Attempt)]);
            if (changed != 1) { throw new InvalidOperationException("Initialization decision is stale."); }
            Sql.Execute(connection, transaction, "INSERT INTO InitializationAttempts VALUES($key,$attempt,$state,$json)",
                ("$key", key.StableIdempotencyKey), ("$attempt", expected.Attempt), ("$state", decision == InitializationDecision.Repeat ? "RepeatAuthorized" : "ResultAcknowledged"),
                ("$json", JsonSerializer.Serialize(new { key, expected.Attempt, Decision = decision, OperationId = resolution.Id, At = resolution.CompletedAt }, CanonicalJson.Options)));
            using var remaining = Sql.Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM DataApplicationRecords WHERE environmentId=$id AND generationId=$generation AND state IN ('Started','Unknown','Failed'))", ("$id", environment.Id.ToString()), ("$generation", key.DataGenerationId.ToString()));
            bool unresolved = (long)remaining.ExecuteScalar()! != 0;
            if (!unresolved)
            {
                CloseInitializationHistory(connection, transaction, environment.Id, resolution.Id, resolution.CompletedAt!.Value);
            }
            var final = resolution;
            Sql.Execute(connection, transaction, "UPDATE Operations SET state=$state,completedAt=$at,resultCode=$code,json=$json WHERE id=$id", ("$state", final.State.ToString()), ("$at", Sql.Instant(final.CompletedAt!.Value)), ("$code", final.ResultCode), ("$json", JsonSerializer.Serialize(final, CanonicalJson.Options)), ("$id", final.Id.ToString()));
            Sql.Execute(connection, transaction, "DELETE FROM PackageReferences WHERE ownerKind='operation' AND ownerId=$id", ("$id", final.Id.ToString()));
            return true;
        }, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not OperationCanceledException)
        {
            // Lost acknowledgement is resolved from the exact durable operation, never by replaying HTTP.
            bool committed = await IsResolutionCommittedAsync(resolution).ConfigureAwait(false);
            if (!committed) { throw; }
        }
    }
}
