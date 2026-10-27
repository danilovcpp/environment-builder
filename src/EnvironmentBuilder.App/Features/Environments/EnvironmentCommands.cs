using CommunityToolkit.Mvvm.ComponentModel;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.App.Features.Environments;

// Application-owned handles survive route changes. Observation and cancellation remain independent.
public sealed class EnvironmentCommands(StartEnvironmentService start, OperationCoordinator coordinator, PreviewReviewStore reviews, IRuntimeInteractions interactions) : ObservableObject
{
    private readonly Dictionary<Guid, OperationHandle> handles = [];
    public OperationHandle? Get(Guid environment) => handles.GetValueOrDefault(environment);
    public string? DisabledReason(Guid environment) => coordinator.AdmissionBlockReason(environment);
    public Task<OperationHandle> StartAsync(IsolatedEnvironment environment, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Get(environment.Id) is { Completion.IsCompleted: false } active) { return Task.FromResult(active); }
        OperationHandle handle = start.Start(environment, reviews.Find(environment.Id), interactions.ConfirmInputsAsync);
        handles[environment.Id] = handle;
        return Task.FromResult(handle);
    }
    public Task CancelAsync(Guid environment, CancellationToken token) => coordinator.RequestCancelAsync(environment, token);
}
