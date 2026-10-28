using System.Collections.Immutable;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.Operations;

public sealed class DeleteEnvironmentService(IEnvironmentStore environments, ICleanupRuntime runtime, IStopRuntime stopping,
    IDeletePersistence persistence, VerifiedCleanupExecutor cleanup, OperationCoordinator coordinator, IClock clock)
{
    public async Task<DeleteEnvironmentResult> DeleteAsync(IsolatedEnvironment environment,
        Func<OperationPlan, CancellationToken, Task<ConfirmationRecord?>> confirm, CancellationToken token, Action<OperationHandle>? submitted = null)
    {
        token.ThrowIfCancellationRequested();
        var saved = (await environments.ListAsync(token).ConfigureAwait(false)).SingleOrDefault(e => e.Id == environment.Id);
        if (saved?.Document is null || CanonicalJson.Hash(saved.Document) != CanonicalJson.Hash(environment))
        { return new(false, null, "saved-configuration-changed"); }
        Guid operationId = Guid.NewGuid();
        var initial = new OperationPlan(operationId, environment.Id, environment.Revision, CanonicalJson.Hash(environment), Guid.Empty,
            CanonicalJson.Hash(environment.Baseline), [], [], [], "Подготовка плана удаления.");
        var operation = new Operation(operationId, environment.Id, "delete", OperationState.Planned, clock.UtcNow, environment.Revision, initial.ConfigurationHash, initial);
        async Task ExecuteAsync(OperationExecution execution)
        {
            ICleanupSession session = await runtime.PrepareAsync(environment, operationId, execution.CancellationToken).ConfigureAwait(false);
            OperationPlan plan = DestructivePlanBuilder.BuildDeletion(environment, session.Scope, await session.ReadAsync(execution.CancellationToken).ConfigureAwait(false));
            if (saved.Deletion is not null) { RequireSameScope(saved.Deletion.Plan, plan); }
            await execution.SetPlanAsync(plan).ConfigureAwait(false);
            ConfirmationRecord? decision = await confirm(plan, execution.CancellationToken).ConfigureAwait(false);
            if (decision is null) { throw new OperationCanceledException("Confirmation declined."); }
            if (decision.PlanHash != plan.Hash) { throw new PreflightBlockedException("stale-plan"); }
            execution.CancellationToken.ThrowIfCancellationRequested();
            await session.RevalidateAsync(execution.CancellationToken).ConfigureAwait(false);
            DestructivePlanBuilder.RequireDeletionCurrent(plan, await session.ReadAsync(execution.CancellationToken).ConfigureAwait(false));
            await execution.ConfirmPlanAsync(decision).ConfigureAwait(false);
            await execution.SetCleanupAsync([], plan.Resources.Select(r => r.ActualDockerId!).ToImmutableArray()).ConfigureAwait(false);
            await persistence.BeginAsync(plan, operationId, execution.CancellationToken).ConfigureAwait(false);
            EnvironmentObservation actual = await session.ReadAsync(execution.CancellationToken).ConfigureAwait(false);
            DestructivePlanBuilder.RequireDeletionCurrent(plan, actual);
            if (!QuiescenceGuard.Check(actual).ConfirmedQuiescent)
            {
                IStopSession stop = await stopping.PrepareAsync(environment, operationId, 30, execution.CancellationToken).ConfigureAwait(false);
                RequireSameScope(plan, stop.Plan);
                DestructivePlanBuilder.RequireDeletionCurrent(plan, await stop.ReadAsync(execution.CancellationToken).ConfigureAwait(false));
                await StopEnvironmentService.ExecuteAsync(environment, stop, execution).ConfigureAwait(false);
            }
            await cleanup.ExecuteAsync(plan, operationId, session, execution).ConfigureAwait(false);
            await execution.CompleteAtomicallyAsync(persistence.FinalizeAsync, clock.UtcNow).ConfigureAwait(false);
        }
        OperationHandle handle = saved.Deletion is null ? coordinator.Submit(operation, ExecuteAsync) : coordinator.SubmitReconciliation(operation, ExecuteAsync);
        submitted?.Invoke(handle);
        Task cancellationWrite = Task.CompletedTask;
        using var registration = token.Register(() => cancellationWrite = coordinator.RequestCancelAsync(environment.Id, CancellationToken.None));
        try { await handle.Completion.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException && handle.Current.State == OperationState.NeedsAttention)
        { /* Durable evidence remains available even when publishing the terminal result failed. */ }
        await registration.DisposeAsync().ConfigureAwait(false);
        await cancellationWrite.ConfigureAwait(false);
        bool success = handle.Current.Kind == "delete" && handle.Current.State == OperationState.Succeeded;
        return new(success, handle.Current, handle.Current.ResultCode ?? (success ? null : handle.Current.State.ToString()));
    }

    private static void RequireSameScope(OperationPlan previous, OperationPlan current)
    {
        if (previous.EnvironmentId != current.EnvironmentId || previous.Revision != current.Revision || previous.ConfigurationHash != current.ConfigurationHash ||
            previous.EngineId != current.EngineId || previous.Endpoint != current.Endpoint || previous.DataGenerationId != current.DataGenerationId ||
            CanonicalJson.Hash(previous.Resources.OrderBy(r => r.ResourceId)) != CanonicalJson.Hash(current.Resources.OrderBy(r => r.ResourceId)))
        { throw new PreflightBlockedException("delete-scope-changed"); }
    }
}
