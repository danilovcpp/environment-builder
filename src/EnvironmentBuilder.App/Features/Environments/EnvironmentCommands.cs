using CommunityToolkit.Mvvm.ComponentModel;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.App.Features.Environments;

// Application-owned handles survive route changes. Observation and cancellation remain independent.
public sealed class EnvironmentCommands(StartEnvironmentService start, OperationCoordinator coordinator, PreviewReviewStore reviews, IRuntimeInteractions interactions, StopEnvironmentService stop) : ObservableObject
{
    private readonly Dictionary<Guid, OperationHandle> handles = [];
    public OperationHandle? Get(Guid environment) => handles.GetValueOrDefault(environment);
    public string? DisabledReason(Guid environment) => coordinator.AdmissionBlockReason(environment);
    public QuiescenceResult Quiescence(Guid environment, EnvironmentObservation? observation) => QuiescenceGuard.Check(observation, coordinator.AdmissionBlockReason(environment));
    public string? StopDisabledReason(Guid environment, EnvironmentObservation? observation)
    {
        string? blocked = coordinator.AdmissionBlockReason(environment, stopping: true);
        if (blocked is not null) { return blocked; }
        if (observation is null || observation.Freshness != "current") { return "Обновите состояние Docker перед остановкой."; }
        if (observation.Components.Any(c => c.Drift is "unexpected-owned" or "foreign-conflict")) { return "Принадлежность контейнеров не подтверждена. Требуется сверка."; }
        if (observation.Resources.Any(r => r.Ledger.Kind == ResourceKind.Container && !r.Ownership.Verified)) { return "Принадлежность контейнеров не подтверждена. Требуется сверка."; }
        if (observation.Resources.Any(r => r.Actual is { Kind: ResourceKind.Container, State: "unknown" })) { return "Состояние контейнеров неизвестно. Обновите состояние Docker."; }
        return observation.Resources.Any(r => r.Ledger.Kind == ResourceKind.Container && r.Actual is not null && !QuiescenceGuard.IsStopped(r.Actual.State))
            ? null : "Нет работающих контейнеров для остановки.";
    }
    public Task<OperationHandle> StartAsync(IsolatedEnvironment environment, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Get(environment.Id) is { Completion.IsCompleted: false } active) { return Task.FromResult(active); }
        OperationHandle handle = start.Start(environment, reviews.Find(environment.Id), interactions.ConfirmInputsAsync);
        handles[environment.Id] = handle;
        return Task.FromResult(handle);
    }
    public Task CancelAsync(Guid environment, CancellationToken token) => coordinator.RequestCancelAsync(environment, token);
    public Task<OperationHandle> StopAsync(IsolatedEnvironment environment, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Get(environment.Id) is { Completion.IsCompleted: false } active) { return Task.FromResult(active); }
        OperationHandle handle = stop.Stop(environment);
        handles[environment.Id] = handle;
        return Task.FromResult(handle);
    }
}
