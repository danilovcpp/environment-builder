using System.Text.Json;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using Microsoft.Data.Sqlite;

namespace EnvironmentBuilder.Infrastructure.Persistence;

public sealed class DeleteFinalization(SqliteWorker worker, Core.Abstractions.IClock clock) : IDeletePersistence
{
    public Task BeginAsync(OperationPlan plan, Guid operationId, CancellationToken token) => worker.CommitAsync("delete-begin", (connection, transaction) =>
    {
        EditFinalization.RequireOriginal(connection, transaction, plan);
        using var confirmed = Sql.Command(connection, transaction, "SELECT confirmationHash FROM Operations WHERE id=$id AND kind='delete'", ("$id", operationId.ToString()));
        if (confirmed.ExecuteScalar() is not string hash || hash != plan.Hash) { throw new InvalidOperationException("Durable delete confirmation is required."); }
        Sql.Execute(connection, transaction, "UPDATE Environments SET deletionState='deleting' WHERE id=$id", ("$id", plan.EnvironmentId.ToString()));
        return true;
    }, token);

    public async Task FinalizeAsync(Operation operation, CancellationToken token)
    {
        OperationPlan plan = operation.Plan;
        if (operation.Kind != "delete" || operation.State != OperationState.Succeeded || operation.Confirmation?.PlanHash != plan.Hash ||
            plan.Candidate is not null || operation.Remnants.Length != 0 || !plan.Stages.Contains("close-description"))
        { throw new InvalidOperationException("Verified deletion is required."); }
        try
        {
            await worker.CommitAsync("delete-finalization", (connection, transaction) =>
            {
                EditFinalization.RequireOriginal(connection, transaction, plan);
                using (var durable = Sql.Command(connection, transaction, "SELECT confirmationHash FROM Operations WHERE id=$id AND kind='delete'", ("$id", operation.Id.ToString())))
                { if (durable.ExecuteScalar() is not string hash || hash != plan.Hash) { throw new InvalidOperationException("Durable delete confirmation is required."); } }
                EditFinalization.RequireProof(connection, transaction, operation.Id, "verify-cleanup", "verify-absence", plan.Hash);
                foreach (OwnedResource resource in plan.Resources)
                {
                    EditFinalization.RequireProof(connection, transaction, operation.Id, "remove-" + resource.ResourceId, "remove-" + resource.Kind.ToString().ToLowerInvariant(), resource.ActualDockerId!);
                    using var ledger = Sql.Command(connection, transaction, "SELECT json FROM ResourceLedger WHERE resourceId=$id AND environmentId=$environment",
                        ("$id", resource.ResourceId.ToString()), ("$environment", operation.EnvironmentId.ToString()));
                    if (ledger.ExecuteScalar() is not string json || CanonicalJson.Hash(JsonSerializer.Deserialize<OwnedResource>(json, CanonicalJson.Options)) != CanonicalJson.Hash(resource))
                    { throw new InvalidOperationException("Delete resource scope changed."); }
                }
                using (var count = Sql.Command(connection, transaction, "SELECT count(*) FROM ResourceLedger WHERE environmentId=$id", ("$id", operation.EnvironmentId.ToString())))
                { if ((long)count.ExecuteScalar()! != plan.Resources.Length) { throw new InvalidOperationException("Delete scope grew."); } }
                List<Operation> history = [];
                using (var command = Sql.Command(connection, transaction, "SELECT json FROM Operations WHERE environmentId=$id", ("$id", operation.EnvironmentId.ToString())))
                using (var rows = command.ExecuteReader())
                { while (rows.Read()) { history.Add(JsonSerializer.Deserialize<Operation>(rows.GetString(0), CanonicalJson.Options)!); } }
                // Only the verified scope resolves previous delete attempts. Other unresolved history must survive.
                foreach (Operation prior in history.Where(o => o.Id != operation.Id))
                {
                    using var unknown = Sql.Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM OperationSteps WHERE operationId=$id AND state IN ('Started','Unknown'))", ("$id", prior.Id.ToString()));
                    bool unresolved = prior.State is not (OperationState.Succeeded or OperationState.Failed or OperationState.Cancelled) || (long)unknown.ExecuteScalar()! == 1;
                    if (unresolved && (prior.Kind != "delete" || prior.Plan.ConfigurationHash != plan.ConfigurationHash ||
                        prior.Plan.EngineId != plan.EngineId || prior.Plan.Endpoint != plan.Endpoint || prior.Plan.DataGenerationId != plan.DataGenerationId ||
                        prior.Plan.Resources.Any(r => !plan.Resources.Any(current => CanonicalJson.Hash(current) == CanonicalJson.Hash(r)))))
                    { throw new PreflightBlockedException("unresolved-history-outside-delete-scope"); }
                }
                Sql.Execute(connection, transaction, "DELETE FROM PackageReferences WHERE (ownerKind='environment' AND ownerId=$id) OR (ownerKind='operation' AND ownerId IN (SELECT id FROM Operations WHERE environmentId=$id))", ("$id", operation.EnvironmentId.ToString()));
                Sql.Execute(connection, transaction, "DELETE FROM ResourceLedger WHERE environmentId=$id", ("$id", operation.EnvironmentId.ToString()));
                Sql.Execute(connection, transaction, "DELETE FROM DataApplicationRecords WHERE environmentId=$id", ("$id", operation.EnvironmentId.ToString()));
                Sql.Execute(connection, transaction, "DELETE FROM DataGenerations WHERE environmentId=$id", ("$id", operation.EnvironmentId.ToString()));
                foreach (Operation prior in history)
                {
                    Operation source = prior.Id == operation.Id ? operation : prior;
                    var summaryPlan = new OperationPlan(source.Plan.Id, source.EnvironmentId, source.OriginalRevision, source.ConfigurationHash,
                        Guid.Empty, "", [], [], ["closed"], "История закрытого окружения; ресурсы и ссылки освобождены.");
                    bool resolvedDelete = source.Kind == "delete";
                    Operation summary = source with
                    {
                        Plan = summaryPlan,
                        Confirmation = null,
                        InputReviewToken = null,
                        Remnants = [],
                        DeletedResources = [],
                        Progress = [],
                        State = resolvedDelete && source.State is not (OperationState.Failed or OperationState.Cancelled) ? OperationState.Succeeded : source.State,
                        CompletedAt = source.CompletedAt ?? clock.UtcNow,
                        ResultCode = resolvedDelete && source.Id != operation.Id ? "resolved-by-delete" : source.ResultCode
                    };
                    Sql.Execute(connection, transaction, "DELETE FROM OperationSteps WHERE operationId=$id", ("$id", source.Id.ToString()));
                    Sql.Execute(connection, transaction, "UPDATE Operations SET state=$state,planJson=$plan,candidateJson=NULL,confirmationHash=NULL,confirmedAt=NULL,json=$json,completedAt=$at,resultCode=$result WHERE id=$id",
                        ("$id", source.Id.ToString()), ("$state", summary.State.ToString()), ("$plan", JsonSerializer.Serialize(summaryPlan, CanonicalJson.Options)),
                        ("$json", JsonSerializer.Serialize(summary, CanonicalJson.Options)), ("$at", Sql.Instant(summary.CompletedAt!.Value)), ("$result", summary.ResultCode));
                }
                // A minimal closed row satisfies history FKs without retaining a live description or baseline.
                Sql.Execute(connection, transaction, "UPDATE Environments SET deletionState='closed',json='{}',hash='',description=NULL,componentTypes='',generationId='' WHERE id=$id", ("$id", operation.EnvironmentId.ToString()));
                return true;
            }, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not OperationCanceledException)
        {
            bool committed = await worker.ReadAsync(connection =>
            {
                using var command = Sql.Command(connection, null, "SELECT EXISTS(SELECT 1 FROM Environments e JOIN Operations o ON o.environmentId=e.id WHERE e.id=$environment AND e.deletionState='closed' AND o.id=$operation AND o.state='Succeeded')",
                    ("$environment", operation.EnvironmentId.ToString()), ("$operation", operation.Id.ToString()));
                return (long)command.ExecuteScalar()! == 1;
            }, CancellationToken.None).ConfigureAwait(false);
            if (!committed) { throw; }
        }
    }
}
