using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.Operations;

public sealed record EditEnvironmentResult(bool Saved, IsolatedEnvironment? Environment, Operation? Operation, string? Code);

public sealed class EditEnvironmentService(IEnvironmentStore environments, ICleanupRuntime runtime, IEditPersistence persistence,
    VerifiedCleanupExecutor cleanup, OperationCoordinator coordinator, IClock clock) : IEnvironmentEditing
{
    public async Task<QuiescenceResult> CheckAsync(IsolatedEnvironment environment, CancellationToken token)
    {
        string? blocked = coordinator.AdmissionBlockReason(environment.Id);
        if (blocked is not null) { return new(false, blocked); }
        try
        {
            ICleanupSession session = await runtime.PrepareAsync(environment, Guid.NewGuid(), token).ConfigureAwait(false);
            return QuiescenceGuard.Check(await session.ReadAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException) { return new(false, "Подтвердить остановку и принадлежность ресурсов не удалось. Обновите состояние Docker."); }
    }

    public Task<EditEnvironmentResult> SaveAsync(IsolatedEnvironment original, IsolatedEnvironment candidate,
        Func<OperationPlan, CancellationToken, Task<ConfirmationRecord?>> confirm, CancellationToken token) => SaveAsync(original, candidate, confirm, token, null);

    public async Task<EditEnvironmentResult> SaveAsync(IsolatedEnvironment original, IsolatedEnvironment candidate,
        Func<OperationPlan, CancellationToken, Task<ConfirmationRecord?>> confirm, CancellationToken token, Action<OperationHandle>? submitted)
    {
        token.ThrowIfCancellationRequested();
        if (candidate.Id != original.Id || candidate.Revision != original.Revision) { throw new DocumentException("/id", "edit-identity-is-immutable"); }
        IsolatedEnvironment saved = (await environments.ListAsync(token).ConfigureAwait(false)).Single(e => e.Id == original.Id).Document ?? throw new PreflightBlockedException("saved-document-unreadable");
        if (CanonicalJson.Hash(saved) != CanonicalJson.Hash(original)) { return new(false, null, null, "saved-configuration-changed"); }
        if (CanonicalJson.Hash(original) == CanonicalJson.Hash(candidate)) { return new(true, saved, null, null); }
        foreach (EnvironmentComponent component in candidate.Components)
        {
            EnvironmentComponent? previous = original.Components.SingleOrDefault(c => c.Id == component.Id);
            if (previous is not null && previous.DefinitionSnapshot.Id != component.DefinitionSnapshot.Id) { throw new DocumentException("/components", "existing-instance-identity-is-immutable"); }
        }
        candidate = candidate with { Revision = checked(original.Revision + 1) };
        EnvironmentValidator.Validate(candidate);
        Guid operationId = Guid.NewGuid();
        var initial = new OperationPlan(operationId, original.Id, original.Revision, CanonicalJson.Hash(original), Guid.Empty,
            CanonicalJson.Hash(original.Baseline), [], [], [], "Подготовка плана изменения.", candidate);
        var operation = new Operation(operationId, original.Id, "edit", OperationState.Planned, clock.UtcNow, original.Revision, initial.ConfigurationHash, initial);
        OperationHandle handle = coordinator.Submit(operation, async execution =>
        {
            ICleanupSession session;
            try { session = await runtime.PrepareAsync(original, operationId, execution.CancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (PreflightBlockedException) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException) { throw new PreflightBlockedException("cleanup-preflight-unavailable"); }
            OperationPlan plan = DestructivePlanBuilder.Build(original, candidate, session.Scope, await session.ReadAsync(execution.CancellationToken).ConfigureAwait(false));
            await execution.SetPlanAsync(plan).ConfigureAwait(false);
            ConfirmationRecord? decision = await confirm(plan, execution.CancellationToken).ConfigureAwait(false);
            if (decision is null) { throw new OperationCanceledException("Confirmation declined."); }
            if (decision.PlanHash != plan.Hash) { throw new PreflightBlockedException("stale-plan"); }
            execution.CancellationToken.ThrowIfCancellationRequested();
            await session.RevalidateAsync(execution.CancellationToken).ConfigureAwait(false);
            DestructivePlanBuilder.RequireCurrent(plan, await session.ReadAsync(execution.CancellationToken).ConfigureAwait(false));
            await execution.ConfirmPlanAsync(decision).ConfigureAwait(false);
            await cleanup.ExecuteAsync(plan, operationId, session, execution).ConfigureAwait(false);
            await execution.CompleteAtomicallyAsync(persistence.FinalizeAsync, clock.UtcNow).ConfigureAwait(false);
        });
        submitted?.Invoke(handle);
        await AwaitCompletionAsync(handle, token).ConfigureAwait(false);
        bool success = handle.Current.State == OperationState.Succeeded && handle.Current.Kind == "edit" && handle.Current.Plan.Candidate?.Id == candidate.Id && CanonicalJson.Hash(handle.Current.Plan.Candidate) == CanonicalJson.Hash(candidate);
        return new(success, success ? handle.Current.Plan.Candidate : null, handle.Current, handle.Current.ResultCode ?? (success ? null : handle.Current.State == OperationState.Cancelled ? "cancelled" : "partial-cleanup"));
    }

    // Explicit finalization never repeats deletion. Fresh authority/scope/absence checks precede the DB commit.
    public async Task<EditEnvironmentResult> FinalizeAsync(Operation operation, CancellationToken token)
    {
        if (operation.Kind != "edit" || operation.State is not (OperationState.NeedsAttention or OperationState.PartiallyCompleted) || operation.Plan.Candidate is null || operation.Confirmation?.PlanHash != operation.Plan.Hash)
        { throw new PreflightBlockedException("edit-evidence-missing"); }
        token.ThrowIfCancellationRequested();
        var handle = coordinator.SubmitReconciliation(operation with { State = OperationState.Reconciling }, async execution =>
        {
            var original = (await environments.ListAsync(execution.CancellationToken).ConfigureAwait(false)).Single(e => e.Id == operation.EnvironmentId).Document ?? throw new PreflightBlockedException("saved-document-unreadable");
            ICleanupSession session = await runtime.PrepareAsync(original, operation.Id, execution.CancellationToken).ConfigureAwait(false);
            await session.RevalidateAsync(execution.CancellationToken).ConfigureAwait(false);
            if (session.Scope.EngineId != operation.Plan.EngineId || session.Scope.Endpoint != operation.Plan.Endpoint || session.Scope.DataGenerationId != operation.Plan.DataGenerationId ||
                CanonicalJson.Hash(session.Scope.Resources.OrderBy(r => r.ResourceId)) != CanonicalJson.Hash(operation.Plan.Resources.OrderBy(r => r.ResourceId))) { throw new PreflightBlockedException("stale-plan"); }
            var actual = await session.ReadAsync(execution.CancellationToken).ConfigureAwait(false);
            DestructivePlanBuilder.RequireCurrent(operation.Plan, actual, operation.DeletedResources);
            if (actual.Resources.Any(r => r.Actual is not null)) { throw new PreflightBlockedException("cleanup-not-absent"); }
            await execution.CompleteAtomicallyAsync(persistence.FinalizeAsync, clock.UtcNow).ConfigureAwait(false);
        });
        await AwaitCompletionAsync(handle, token).ConfigureAwait(false);
        bool saved = handle.Current.State == OperationState.Succeeded;
        return new(saved, saved ? handle.Current.Plan.Candidate : null, handle.Current, handle.Current.ResultCode);
    }

    private async Task AwaitCompletionAsync(OperationHandle handle, CancellationToken token)
    {
        Task cancellationWrite = Task.CompletedTask;
        using var registration = token.Register(() => cancellationWrite = coordinator.RequestCancelAsync(handle.Current.EnvironmentId, CancellationToken.None));
        try { await handle.Completion.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException && handle.Current.State == OperationState.NeedsAttention)
        { /* The failed completion is reported with its retained plan, effects and remnants. */ }
        await registration.DisposeAsync().ConfigureAwait(false);
        try { await cancellationWrite.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException && handle.Current.State == OperationState.NeedsAttention)
        { /* The coordinator keeps the write failure as a shutdown/recovery blocker. */ }
    }
}
