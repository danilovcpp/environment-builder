using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.App.Shell;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.UnitTests.Operations;

namespace EnvironmentBuilder.UnitTests.ViewModels;

public sealed class ShutdownTests
{
    [Theory]
    [InlineData(CloseOperationsDecision.Wait)]
    [InlineData(CloseOperationsDecision.Cancel)]
    [InlineData(CloseOperationsDecision.Return)]
    public async Task Closing_choices_preserve_work_or_wait_for_the_current_durable_safe_point(CloseOperationsDecision decision)
    {
        var savingTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commitTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new RuntimeFixture
        {
            CloseDecision = decision,
            BeforeSave = async operation =>
            {
                if (decision != CloseOperationsDecision.Return && operation.State is OperationState.Succeeded or OperationState.Cancelled)
                { savingTerminal.TrySetResult(); await commitTerminal.Task; }
            }
        };
        await using var coordinator = fixture.Coordinator();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid environment = Guid.NewGuid();
        var plan = new OperationPlan(Guid.NewGuid(), environment, 1, "hash", Guid.NewGuid(), "baseline", [], [], [], "fixture");
        var operation = new Operation(Guid.NewGuid(), environment, "start", OperationState.Planned, DateTimeOffset.UtcNow, 1, "hash", plan);
        var handle = coordinator.Submit(operation, execution => execution.RunStepAsync(new(operation.Id, "create", "create", "fixture"), async token =>
        { Assert.False(token.CanBeCanceled); entered.SetResult(); await release.Task; }, _ => Task.FromResult(new EffectObservation("container", true, null))));
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var shutdown = new ShutdownCoordinator(coordinator, fixture);
        Task<bool> closing = shutdown.RequestAsync(_ => Task.FromResult(true), TestContext.Current.CancellationToken);
        if (decision == CloseOperationsDecision.Return)
        {
            Assert.False(await closing); Assert.Null(coordinator.AdmissionBlockReason(Guid.NewGuid())); Assert.False(handle.Completion.IsCompleted);
            release.SetResult(); await handle.Completion; Assert.Equal(OperationState.Succeeded, handle.Current.State); return;
        }
        Assert.False(closing.IsCompleted);
        Assert.NotNull(coordinator.AdmissionBlockReason(Guid.NewGuid()));
        release.SetResult();
        await savingTerminal.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(closing.IsCompleted);
        Assert.DoesNotContain(handle.Current.State, new[] { OperationState.Succeeded, OperationState.Cancelled });
        commitTerminal.SetResult(); Assert.True(await closing);
        Assert.Equal(decision == CloseOperationsDecision.Wait ? OperationState.Succeeded : OperationState.Cancelled, handle.Current.State);
    }

    [Fact]
    public async Task Dirty_stay_prevents_the_operations_dialog_and_return_preserves_admission()
    {
        var fixture = new RuntimeFixture(); await using var coordinator = fixture.Coordinator();
        var shutdown = new ShutdownCoordinator(coordinator, fixture);
        Assert.False(await shutdown.RequestAsync(_ => Task.FromResult(false), TestContext.Current.CancellationToken));
        Assert.Null(coordinator.AdmissionBlockReason(Guid.NewGuid()));
    }
}
