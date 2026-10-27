using System.Text.Json;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using Microsoft.Data.Sqlite;

namespace EnvironmentBuilder.Infrastructure.Persistence;

public sealed class OperationStore(SqliteWorker worker, IClock clock) : IOperationStore
{
    public Task SaveAsync(Operation operation, CancellationToken cancellationToken)
    {
        if (operation.Plan.EnvironmentId != operation.EnvironmentId || operation.Plan.Revision != operation.OriginalRevision || operation.Plan.ConfigurationHash != operation.ConfigurationHash)
        { throw new InvalidOperationException("Operation plan identity mismatch."); }
        return worker.CommitAsync("operation", (connection, transaction) =>
        {
            string plan = JsonSerializer.Serialize(operation.Plan, CanonicalJson.Options);
            using SqliteCommand existing = Sql.Command(connection, transaction, "SELECT planJson FROM Operations WHERE id=$id", ("$id", operation.Id.ToString()));
            object? previousPlan = existing.ExecuteScalar();
            if (previousPlan is null)
            {
                using SqliteCommand environment = Sql.Command(connection, transaction, "SELECT revision,hash FROM Environments WHERE id=$id", ("$id", operation.EnvironmentId.ToString()));
                using SqliteDataReader row = environment.ExecuteReader();
                if (!row.Read() || row.GetInt64(0) != operation.OriginalRevision || row.GetString(1) != operation.ConfigurationHash)
                { throw new InvalidOperationException("Operation refers to a stale configuration."); }
            }
            if (previousPlan is string saved && CanonicalJson.Hash(saved) != CanonicalJson.Hash(plan))
            {
                OperationPlan prior = JsonSerializer.Deserialize<OperationPlan>(saved, CanonicalJson.Options)!;
                using SqliteCommand effects = Sql.Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM OperationSteps WHERE operationId=$id) OR EXISTS(SELECT 1 FROM Operations WHERE id=$id AND confirmationHash IS NOT NULL)", ("$id", operation.Id.ToString()));
                if (prior.DataGenerationId != Guid.Empty || operation.State != OperationState.AwaitingConfirmation || (long)effects.ExecuteScalar()! != 0)
                { throw new InvalidOperationException("A confirmed or executing operation plan is immutable."); }
            }
            Sql.Execute(connection, transaction,
                "INSERT INTO Operations(id,environmentId,kind,state,originalRevision,configurationHash,planJson,candidateJson,json,createdAt,completedAt,resultCode) VALUES($id,$environment,$kind,$state,$revision,$hash,$plan,$candidate,$json,$at,$completed,$result) ON CONFLICT(id) DO UPDATE SET state=$state,planJson=$plan,json=$json,completedAt=$completed,resultCode=$result",
                ("$id", operation.Id.ToString()), ("$environment", operation.EnvironmentId.ToString()), ("$kind", operation.Kind), ("$state", operation.State.ToString()),
                ("$revision", operation.OriginalRevision), ("$hash", operation.ConfigurationHash), ("$plan", plan),
                ("$candidate", operation.Plan.Candidate is null ? null : JsonSerializer.Serialize(operation.Plan.Candidate, CanonicalJson.Options)),
                ("$json", JsonSerializer.Serialize(operation, CanonicalJson.Options)), ("$at", Sql.Instant(operation.CreatedAt)),
                ("$completed", operation.CompletedAt is null ? null : Sql.Instant(operation.CompletedAt.Value)), ("$result", operation.ResultCode));
            if (operation.Confirmation is not null)
            {
                if (operation.Confirmation.PlanHash != operation.Plan.Hash) { throw new InvalidOperationException("Confirmation does not match the immutable plan."); }
                Sql.Execute(connection, transaction, "UPDATE Operations SET confirmationHash=$hash,confirmedAt=$at WHERE id=$id", ("$id", operation.Id.ToString()),
                    ("$hash", operation.Confirmation.PlanHash), ("$at", Sql.Instant(operation.Confirmation.ConfirmedAt)));
            }
            if (operation.Plan.Candidate is not null) { EnvironmentStore.SaveReferences(connection, transaction, "operation", operation.Id, operation.Plan.Candidate.Baseline.Applications); }
            foreach (var pack in operation.Plan.Packs) { EnvironmentStore.AddReference(connection, transaction, "operation", operation.Id, CanonicalJson.Hash(pack), pack); }
            return true;
        }, cancellationToken);
    }

    public Task ConfirmAsync(Operation operation, ConfirmationRecord confirmation, CancellationToken cancellationToken)
    {
        if (operation.Plan.Hash != confirmation.PlanHash) { throw new InvalidOperationException("Stale confirmation."); }
        return worker.CommitAsync("confirmation", (connection, transaction) =>
        {
            int changed = Sql.Execute(connection, transaction, "UPDATE Operations SET confirmationHash=$hash,confirmedAt=$at WHERE id=$id AND planJson=$plan AND state IN ('Planned','AwaitingConfirmation')",
                ("$hash", confirmation.PlanHash), ("$at", Sql.Instant(confirmation.ConfirmedAt)), ("$id", operation.Id.ToString()), ("$plan", JsonSerializer.Serialize(operation.Plan, CanonicalJson.Options)));
            if (changed != 1) { throw new InvalidOperationException("Plan is no longer awaiting confirmation."); }
            return true;
        }, cancellationToken);
    }

    public Task WriteIntentAsync(OperationStep step, CancellationToken cancellationToken) => worker.CommitAsync("intent", (connection, transaction) =>
    {
        int changed = Sql.Execute(connection, transaction,
            "INSERT INTO OperationSteps(operationId,stepId,ordinal,action,target,state,intentJson,startedAt) VALUES($id,$step,$ordinal,$action,$target,'Started',$intent,$at) ON CONFLICT(operationId,stepId) DO NOTHING",
            ("$id", step.OperationId.ToString()), ("$step", step.StepId), ("$ordinal", step.Ordinal), ("$action", step.Action), ("$target", step.Target),
            ("$intent", JsonSerializer.Serialize(step with { State = StepState.Started, StartedAt = clock.UtcNow }, CanonicalJson.Options)), ("$at", Sql.Instant(clock.UtcNow)));
        if (changed != 1) { throw new InvalidOperationException("Existing step requires reconciliation; replay is not implicit."); }
        return true;
    }, cancellationToken);

    public Task WriteResultAsync(OperationStep step, EffectObservation result, CancellationToken cancellationToken) => worker.CommitAsync("result", (connection, transaction) =>
    {
        int changed = Sql.Execute(connection, transaction, "UPDATE OperationSteps SET state=$state,resultJson=$result,completedAt=$at WHERE operationId=$id AND stepId=$step AND state='Started'",
            ("$state", result.Verified ? "Succeeded" : "Unknown"), ("$result", JsonSerializer.Serialize(result, CanonicalJson.Options)), ("$at", Sql.Instant(clock.UtcNow)),
            ("$id", step.OperationId.ToString()), ("$step", step.StepId));
        if (changed != 1) { throw new InvalidOperationException("A durable intent is required."); }
        return true;
    }, cancellationToken);

    public Task<IReadOnlyList<Operation>> ReadUnresolvedAsync(CancellationToken cancellationToken) => worker.ReadAsync<IReadOnlyList<Operation>>(connection =>
    {
        using SqliteCommand command = Sql.Command(connection, null, "SELECT json FROM Operations o WHERE state NOT IN ('Succeeded','Failed','Cancelled') OR EXISTS(SELECT 1 FROM OperationSteps s WHERE s.operationId=o.id AND s.state IN ('Started','Unknown')) OR EXISTS(SELECT 1 FROM ResourceLedger r WHERE r.operationId=o.id AND r.unresolved=1) ORDER BY createdAt");
        using SqliteDataReader rows = command.ExecuteReader();
        var result = new List<Operation>();
        while (rows.Read()) { result.Add(JsonSerializer.Deserialize<Operation>(rows.GetString(0), CanonicalJson.Options)!); }
        return result;
    }, cancellationToken);
}
