using System.IO;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.UnitTests.Operations;

public sealed class CoordinatorTests
{
    [Fact]
    public async Task A_failed_terminal_commit_never_advertises_success_or_allows_a_second_operation()
    {
        var journal = new MemoryJournal { FailTerminalWrites = true };
        await using var coordinator = new OperationCoordinator(journal, new SystemClock(), () => true);
        Operation operation = NewOperation();
        OperationHandle handle = coordinator.Submit(operation, _ => Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => handle.Completion);
        Assert.Equal(OperationState.NeedsAttention, handle.Current.State);
        Assert.Throws<InvalidOperationException>(() => coordinator.Submit(operation with { Id = Guid.NewGuid() }, _ => Task.CompletedTask));
    }

    [Fact]
    public async Task Duplicate_submission_returns_existing_operation_and_other_environments_are_independent()
    {
        var journal = new MemoryJournal();
        await using var coordinator = new OperationCoordinator(journal, new SystemClock(), () => true);
        var firstStarted = Signal();
        var secondStarted = Signal();
        var release = Signal();
        Operation operation = NewOperation();
        OperationHandle first = coordinator.Submit(operation, async _ => { firstStarted.SetResult(); await release.Task; });
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Same(first, coordinator.Submit(operation with { Id = Guid.NewGuid() }, _ => throw new InvalidOperationException("duplicate")));
        OperationHandle second = coordinator.Submit(NewOperation(), async _ => { secondStarted.SetResult(); await release.Task; });
        await secondStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        release.SetResult();
        await Task.WhenAll(first.Completion, second.Completion);
        Assert.Equal(OperationState.Succeeded, first.Current.State);
        Assert.Equal(OperationState.Succeeded, second.Current.State);
    }

    [Fact]
    public async Task Cancellation_waits_for_the_current_durable_safe_point_and_prevents_the_next_effect()
    {
        var journal = new MemoryJournal();
        await using var coordinator = new OperationCoordinator(journal, new SystemClock(), () => true);
        var started = Signal();
        var release = Signal();
        Operation operation = NewOperation();
        int effects = 0;
        OperationHandle handle = coordinator.Submit(operation, async execution =>
        {
            await execution.RunStepAsync(new OperationStep(operation.Id, "first", "create", "one"), async token =>
            {
                Assert.False(token.CanBeCanceled);
                effects++;
                started.SetResult();
                await release.Task;
            }, _ => Task.FromResult(new EffectObservation("actual-one", true)));
            await execution.RunStepAsync(new OperationStep(operation.Id, "second", "create", "two"),
                _ => { effects++; return Task.CompletedTask; }, _ => Task.FromResult(new EffectObservation("actual-two", true)));
        });
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await coordinator.RequestCancelAsync(operation.EnvironmentId, TestContext.Current.CancellationToken);
        Assert.False(handle.Completion.IsCompleted);
        release.SetResult();
        await handle.Completion;
        Assert.Equal(1, effects);
        Assert.Equal(StepState.Succeeded, journal.Steps["first"]);
        Assert.Equal(OperationState.Cancelled, handle.Current.State);
    }

    [Fact]
    public async Task A_lost_result_stops_scheduling_and_blocks_a_new_operation_until_reconciliation()
    {
        var journal = new MemoryJournal { FailResults = true };
        await using var coordinator = new OperationCoordinator(journal, new SystemClock(), () => true);
        Operation operation = NewOperation();
        int effects = 0;
        OperationHandle handle = coordinator.Submit(operation, async execution =>
        {
            for (int i = 0; i < 2; i++)
            {
                await execution.RunStepAsync(new OperationStep(operation.Id, "step" + i, "create", "target"),
                    _ => { effects++; return Task.CompletedTask; }, _ => Task.FromResult(new EffectObservation("actual-id", true)));
            }
        });
        await handle.Completion;
        Assert.Equal(1, effects);
        Assert.Equal(OperationState.NeedsAttention, handle.Current.State);
        Assert.Throws<InvalidOperationException>(() => coordinator.Submit(operation with { Id = Guid.NewGuid() }, _ => Task.CompletedTask));
    }

    [Fact]
    public async Task Pending_close_and_writer_loss_block_admission()
    {
        bool writable = true;
        await using var coordinator = new OperationCoordinator(new MemoryJournal(), new SystemClock(), () => writable);
        await coordinator.PrepareCloseAsync(false, TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => coordinator.Submit(NewOperation(), _ => Task.CompletedTask));
        coordinator.ReturnFromClose();
        writable = false;
        Assert.Throws<InvalidOperationException>(() => coordinator.Submit(NewOperation(), _ => Task.CompletedTask));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Operation NewOperation()
    {
        Guid environment = Guid.NewGuid();
        var plan = new OperationPlan(Guid.NewGuid(), environment, 1, "configuration", Guid.NewGuid(), "baseline", [], [], [], "fixture");
        return new Operation(Guid.NewGuid(), environment, "start", OperationState.Planned, DateTimeOffset.UtcNow, 1, "configuration", plan);
    }

    private sealed class MemoryJournal : IOperationStore
    {
        public bool FailResults { get; init; }
        public bool FailTerminalWrites { get; init; }
        public Dictionary<string, StepState> Steps { get; } = [];
        public Task WriteIntentAsync(OperationStep step, CancellationToken cancellationToken) { lock (Steps) { Steps[step.StepId] = StepState.Started; } return Task.CompletedTask; }
        public Task WriteResultAsync(OperationStep step, EffectObservation result, CancellationToken cancellationToken)
        {
            if (FailResults) { throw new IOException("result fault"); }
            lock (Steps) { Steps[step.StepId] = result.Verified ? StepState.Succeeded : StepState.Unknown; }
            return Task.CompletedTask;
        }
        public Task SaveAsync(Operation operation, CancellationToken cancellationToken)
        {
            if (FailTerminalWrites && operation.State == OperationState.Succeeded) { throw new IOException("terminal commit fault"); }
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<Operation>> ReadUnresolvedAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Operation>>([]);
    }
}
