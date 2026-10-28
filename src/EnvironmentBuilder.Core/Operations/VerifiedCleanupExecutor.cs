using System.Collections.Immutable;
using EnvironmentBuilder.Core.Abstractions;

namespace EnvironmentBuilder.Core.Operations;

public sealed class VerifiedCleanupExecutor(IEditPersistence persistence)
{
    public async Task ExecuteAsync(OperationPlan plan, Guid operationId, ICleanupSession session, OperationExecution execution)
    {
        var removed = new List<string>();
        bool invalidated = false;
        await execution.SetCleanupAsync([], plan.Resources.Select(r => r.ActualDockerId!).ToImmutableArray()).ConfigureAwait(false);
        foreach (OwnedResource resource in plan.Resources.OrderBy(r => r.Kind switch { ResourceKind.Container => 0, ResourceKind.Volume => 1, _ => 2 }).ThenBy(r => r.ResourceId))
        {
            execution.CancellationToken.ThrowIfCancellationRequested();
            await session.RevalidateAsync(execution.CancellationToken).ConfigureAwait(false);
            var before = await session.ReadAsync(execution.CancellationToken).ConfigureAwait(false);
            DestructivePlanBuilder.RequireCurrent(plan, before, removed);
            if (before.Resources.Single(r => r.Ledger.ResourceId == resource.ResourceId).Actual is not null && !invalidated)
            {
                await persistence.InvalidateAsync(plan, operationId, execution.CancellationToken).ConfigureAwait(false);
                invalidated = true;
                await execution.MarkDestructiveAsync().ConfigureAwait(false);
            }
            await execution.ReportAsync(resource.ComponentId, "remove-" + resource.Kind.ToString().ToLowerInvariant()).ConfigureAwait(false);
            await session.RemoveAsync(plan, removed.ToImmutableArray(), resource, execution, execution.CancellationToken).ConfigureAwait(false);
            removed.Add(resource.ActualDockerId!);
            await execution.SetCleanupAsync(removed.ToImmutableArray(), plan.Resources.Select(r => r.ActualDockerId!).Except(removed, StringComparer.Ordinal).ToImmutableArray()).ConfigureAwait(false);
        }
        execution.CancellationToken.ThrowIfCancellationRequested();
        await execution.RunStepAsync(new(operationId, "verify-cleanup", "verify-absence", plan.Hash), _ => Task.CompletedTask, async safe =>
        {
            await session.RevalidateAsync(safe).ConfigureAwait(false);
            var final = await session.ReadAsync(safe).ConfigureAwait(false);
            DestructivePlanBuilder.RequireCurrent(plan, final, removed);
            bool absent = final.Resources.All(r => r.Actual is null);
            return new(plan.Hash, absent, absent ? null : "cleanup-not-absent");
        }).ConfigureAwait(false);
    }
}
