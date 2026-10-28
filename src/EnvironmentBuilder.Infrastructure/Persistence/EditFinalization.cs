using System.Text.Json;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using Microsoft.Data.Sqlite;

namespace EnvironmentBuilder.Infrastructure.Persistence;

public sealed class EditFinalization(SqliteWorker worker, IClock clock) : IEditPersistence
{
    public Task InvalidateAsync(OperationPlan plan, Guid operationId, CancellationToken token) => worker.CommitAsync("generation-invalidation", (connection, transaction) =>
    {
        RequireOriginal(connection, transaction, plan);
        int changed = Sql.Execute(connection, transaction, "UPDATE DataGenerations SET validity='invalidated',invalidatedAt=$at,reason='destructive-edit',operationId=$operation WHERE id=$id AND environmentId=$environment AND validity='active'",
            ("$id", plan.DataGenerationId.ToString()), ("$environment", plan.EnvironmentId.ToString()), ("$operation", operationId.ToString()), ("$at", Sql.Instant(clock.UtcNow)));
        if (changed != 1) { throw new PreflightBlockedException("data-generation-not-active"); }
        return true;
    }, token);

    public async Task FinalizeAsync(Operation operation, CancellationToken token)
    {
        OperationPlan plan = operation.Plan;
        IsolatedEnvironment candidate = plan.Candidate ?? throw new InvalidOperationException("An edit candidate is required.");
        EnvironmentValidator.Validate(candidate);
        if (operation.Kind != "edit" || operation.State != OperationState.Succeeded || operation.Confirmation?.PlanHash != plan.Hash ||
            candidate.Id != operation.EnvironmentId || candidate.Revision != checked(operation.OriginalRevision + 1) || plan.NextDataGenerationId is null || plan.NextDataGenerationId == Guid.Empty || operation.Remnants.Length != 0)
        { throw new InvalidOperationException("Verified edit finalization is required."); }
        try
        {
            await worker.CommitAsync("edit-finalization", (connection, transaction) =>
            {
                RequireOriginal(connection, transaction, plan);
                using (var command = Sql.Command(connection, transaction, "SELECT json,confirmationHash FROM Operations WHERE id=$id", ("$id", operation.Id.ToString())))
                using (var row = command.ExecuteReader())
                {
                    if (!row.Read()) { throw new InvalidOperationException("Durable operation is required."); }
                    Operation saved = JsonSerializer.Deserialize<Operation>(row.GetString(0), CanonicalJson.Options)!;
                    if (saved.Plan.Hash != plan.Hash || row.IsDBNull(1) || row.GetString(1) != plan.Hash) { throw new InvalidOperationException("Durable confirmed candidate is required."); }
                }
                RequireProof(connection, transaction, operation.Id, "verify-cleanup", "verify-absence", plan.Hash);
                foreach (OwnedResource resource in plan.Resources)
                {
                    RequireProof(connection, transaction, operation.Id, "remove-" + resource.ResourceId, "remove-" + resource.Kind.ToString().ToLowerInvariant(), resource.ActualDockerId!);
                    using var ledger = Sql.Command(connection, transaction, "SELECT json FROM ResourceLedger WHERE resourceId=$id AND environmentId=$environment", ("$id", resource.ResourceId.ToString()), ("$environment", operation.EnvironmentId.ToString()));
                    if (ledger.ExecuteScalar() is not string json || CanonicalJson.Hash(JsonSerializer.Deserialize<OwnedResource>(json, CanonicalJson.Options)) != CanonicalJson.Hash(resource))
                    { throw new InvalidOperationException("Resource scope changed before finalization."); }
                }
                using (var count = Sql.Command(connection, transaction, "SELECT count(*) FROM ResourceLedger WHERE environmentId=$id", ("$id", operation.EnvironmentId.ToString())))
                { if ((long)count.ExecuteScalar()! != plan.Resources.Length) { throw new InvalidOperationException("Resource scope grew before finalization."); } }
                Sql.Execute(connection, transaction, "UPDATE DataGenerations SET validity='retired' WHERE id=$id", ("$id", plan.DataGenerationId.ToString()));
                Guid next = plan.NextDataGenerationId.Value;
                Sql.Execute(connection, transaction, "INSERT INTO DataGenerations(id,environmentId,baselineHash,validity,allocatedAt,operationId) VALUES($id,$environment,$baseline,'active',$at,$operation)",
                    ("$id", next.ToString()), ("$environment", candidate.Id.ToString()), ("$baseline", CanonicalJson.Hash(candidate.Baseline)), ("$at", Sql.Instant(clock.UtcNow)), ("$operation", operation.Id.ToString()));
                Sql.Execute(connection, transaction, "UPDATE Environments SET revision=$revision,json=$json,hash=$hash,name=$name,description=$description,componentTypes=$types,generationId=$generation,savedAt=$at WHERE id=$id",
                    ("$id", candidate.Id.ToString()), ("$revision", candidate.Revision), ("$json", JsonSerializer.Serialize(candidate, CanonicalJson.Options)), ("$hash", CanonicalJson.Hash(candidate)),
                    ("$name", candidate.Name), ("$description", candidate.Description), ("$types", string.Join(',', candidate.Components.Select(c => c.DefinitionSnapshot.ComponentType).Distinct())), ("$generation", next.ToString()), ("$at", Sql.Instant(clock.UtcNow)));
                Sql.Execute(connection, transaction, "DELETE FROM PackageReferences WHERE ownerKind='environment' AND ownerId=$id", ("$id", candidate.Id.ToString()));
                EnvironmentStore.SaveReferences(connection, transaction, "environment", candidate.Id, candidate.Baseline.Applications);
                Sql.Execute(connection, transaction, "DELETE FROM ResourceLedger WHERE environmentId=$id", ("$id", candidate.Id.ToString()));
                Sql.Execute(connection, transaction, "UPDATE Operations SET state='Succeeded',json=$json,completedAt=$at,resultCode=NULL WHERE id=$id", ("$id", operation.Id.ToString()), ("$json", JsonSerializer.Serialize(operation, CanonicalJson.Options)), ("$at", Sql.Instant(operation.CompletedAt!.Value)));
                Sql.Execute(connection, transaction, "DELETE FROM PackageReferences WHERE ownerKind='operation' AND ownerId=$id", ("$id", operation.Id.ToString()));
                return true;
            }, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not OperationCanceledException)
        {
            // A lost commit acknowledgement is resolved from durable state, never by repeating cleanup.
            bool committed = await worker.ReadAsync(connection =>
            {
                using var command = Sql.Command(connection, null, "SELECT EXISTS(SELECT 1 FROM Environments e JOIN Operations o ON o.environmentId=e.id WHERE e.id=$environment AND e.revision=$revision AND e.hash=$hash AND e.generationId=$generation AND o.id=$operation AND o.state='Succeeded')",
                    ("$environment", candidate.Id.ToString()), ("$revision", candidate.Revision), ("$hash", CanonicalJson.Hash(candidate)), ("$generation", plan.NextDataGenerationId.Value.ToString()), ("$operation", operation.Id.ToString()));
                return (long)command.ExecuteScalar()! == 1;
            }, CancellationToken.None).ConfigureAwait(false);
            if (!committed) { throw; }
        }
    }

    private static void RequireOriginal(SqliteConnection connection, SqliteTransaction transaction, OperationPlan plan)
    {
        using var command = Sql.Command(connection, transaction, "SELECT revision,hash,generationId FROM Environments WHERE id=$id", ("$id", plan.EnvironmentId.ToString()));
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetInt64(0) != plan.Revision || row.GetString(1) != plan.ConfigurationHash || row.GetString(2) != plan.DataGenerationId.ToString())
        { throw new PreflightBlockedException("saved-configuration-changed"); }
    }

    private static void RequireProof(SqliteConnection connection, SqliteTransaction transaction, Guid operation, string step, string action, string target)
    {
        using var command = Sql.Command(connection, transaction, "SELECT resultJson FROM OperationSteps WHERE operationId=$id AND stepId=$step AND action=$action AND target=$target AND state='Succeeded'",
            ("$id", operation.ToString()), ("$step", step), ("$action", action), ("$target", target));
        if (command.ExecuteScalar() is not string json || JsonSerializer.Deserialize<EffectObservation>(json, CanonicalJson.Options) is not { Verified: true } proof || proof.ActualIdentity != target)
        { throw new InvalidOperationException("Complete durable absence evidence is required."); }
    }
}
