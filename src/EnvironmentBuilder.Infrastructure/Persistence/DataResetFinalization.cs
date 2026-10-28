using System.Text.Json;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.Infrastructure.Persistence;

public sealed class DataResetFinalization(SqliteWorker worker, IClock clock) : IDataResetPersistence
{
    public async Task FinalizeAsync(Operation operation, CancellationToken token)
    {
        OperationPlan plan = operation.Plan;
        if (operation.Kind is not ("clear" or "refresh") || plan.Candidate is not null || operation.Confirmation?.PlanHash != plan.Hash ||
            plan.NextDataGenerationId is null || plan.NextDataGenerationId == Guid.Empty || plan.NextDataGenerationId == plan.DataGenerationId || operation.Remnants.Length != 0 ||
            (operation.Kind == "clear" ? operation.State != OperationState.Succeeded : !operation.DataResetCompleted || operation.State != OperationState.Running))
        { throw new InvalidOperationException("Confirmed complete data cleanup is required."); }
        try
        {
            await worker.CommitAsync("data-reset-finalization", (connection, transaction) =>
            {
                EditFinalization.RequireOriginal(connection, transaction, plan);
                using (var command = Sql.Command(connection, transaction, "SELECT json FROM Environments WHERE id=$id", ("$id", plan.EnvironmentId.ToString())))
                {
                    var environment = JsonSerializer.Deserialize<IsolatedEnvironment>((string)command.ExecuteScalar()!, CanonicalJson.Options)!;
                    if (CanonicalJson.Hash(environment.Baseline) != plan.BaselineHash) { throw new InvalidOperationException("Baseline identity changed before data reset."); }
                }
                using (var command = Sql.Command(connection, transaction, "SELECT json,confirmationHash FROM Operations WHERE id=$id", ("$id", operation.Id.ToString())))
                using (var row = command.ExecuteReader())
                {
                    if (!row.Read() || row.IsDBNull(1) || row.GetString(1) != plan.Hash ||
                        JsonSerializer.Deserialize<Operation>(row.GetString(0), CanonicalJson.Options) is not { } saved || saved.Kind != operation.Kind || saved.Plan.Hash != plan.Hash)
                    { throw new InvalidOperationException("Durable confirmation is required."); }
                }
                EditFinalization.RequireProof(connection, transaction, operation.Id, "verify-cleanup", "verify-absence", plan.Hash);
                foreach (OwnedResource resource in plan.Resources)
                {
                    EditFinalization.RequireProof(connection, transaction, operation.Id, "remove-" + resource.ResourceId, "remove-" + resource.Kind.ToString().ToLowerInvariant(), resource.ActualDockerId!);
                }
                using (var command = Sql.Command(connection, transaction, "SELECT json FROM ResourceLedger WHERE environmentId=$id", ("$id", plan.EnvironmentId.ToString())))
                using (var row = command.ExecuteReader())
                {
                    var resources = new List<OwnedResource>();
                    while (row.Read()) { resources.Add(JsonSerializer.Deserialize<OwnedResource>(row.GetString(0), CanonicalJson.Options)!); }
                    if (CanonicalJson.Hash(resources.OrderBy(r => r.ResourceId)) != CanonicalJson.Hash(plan.Resources.OrderBy(r => r.ResourceId)))
                    { throw new InvalidOperationException("Resource scope changed before data reset."); }
                }
                Sql.Execute(connection, transaction, "UPDATE DataGenerations SET validity='retired' WHERE id=$id", ("$id", plan.DataGenerationId.ToString()));
                Sql.Execute(connection, transaction, "INSERT INTO DataGenerations(id,environmentId,baselineHash,validity,allocatedAt,operationId) VALUES($id,$environment,$baseline,'active',$at,$operation)",
                    ("$id", plan.NextDataGenerationId.Value.ToString()), ("$environment", plan.EnvironmentId.ToString()), ("$baseline", plan.BaselineHash), ("$at", Sql.Instant(clock.UtcNow)), ("$operation", operation.Id.ToString()));
                // JSON, revision, summary indexes, baseline and environment package references stay authoritative.
                Sql.Execute(connection, transaction, "UPDATE Environments SET generationId=$generation WHERE id=$id", ("$id", plan.EnvironmentId.ToString()), ("$generation", plan.NextDataGenerationId.Value.ToString()));
                Sql.Execute(connection, transaction, "DELETE FROM ResourceLedger WHERE environmentId=$id", ("$id", plan.EnvironmentId.ToString()));
                Sql.Execute(connection, transaction, "UPDATE Operations SET state=$state,json=$json,completedAt=$at,resultCode=NULL WHERE id=$id",
                    ("$id", operation.Id.ToString()), ("$state", operation.State.ToString()), ("$json", JsonSerializer.Serialize(operation, CanonicalJson.Options)), ("$at", operation.CompletedAt is null ? null : Sql.Instant(operation.CompletedAt.Value)));
                if (operation.Kind == "clear") { Sql.Execute(connection, transaction, "DELETE FROM PackageReferences WHERE ownerKind='operation' AND ownerId=$id", ("$id", operation.Id.ToString())); }
                return true;
            }, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not OperationCanceledException)
        {
            bool committed = await worker.ReadAsync(connection =>
            {
                using var command = Sql.Command(connection, null, "SELECT o.json FROM Operations o JOIN Environments e ON e.id=o.environmentId WHERE o.id=$operation AND e.generationId=$generation AND e.revision=$revision AND e.hash=$hash",
                    ("$operation", operation.Id.ToString()), ("$generation", plan.NextDataGenerationId.Value.ToString()), ("$revision", plan.Revision), ("$hash", plan.ConfigurationHash));
                return command.ExecuteScalar() is string json && JsonSerializer.Deserialize<Operation>(json, CanonicalJson.Options) is { } saved &&
                    saved.Plan.Hash == plan.Hash && (operation.Kind == "clear" ? saved.State == OperationState.Succeeded : saved.DataResetCompleted);
            }, CancellationToken.None).ConfigureAwait(false);
            if (!committed) { throw; }
        }
    }
}
