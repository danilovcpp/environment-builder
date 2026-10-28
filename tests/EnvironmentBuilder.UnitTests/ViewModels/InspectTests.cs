using System.Collections.Immutable;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.UnitTests.Operations;

namespace EnvironmentBuilder.UnitTests.ViewModels;

public sealed class InspectTests
{
    [Fact]
    public async Task Stop_availability_requires_current_owned_containers_and_refresh_remains_independent()
    {
        var fixture = new RuntimeFixture(); await using var coordinator = fixture.Coordinator();
        var commands = new EnvironmentCommands(new(coordinator, fixture, new(fixture, fixture), fixture), coordinator, new(), fixture, new(coordinator, fixture, fixture));
        var environment = fixture.Environment();
        Assert.NotNull(commands.StopDisabledReason(environment.Id, null));
        var container = new OwnedResource(Guid.NewGuid(), Guid.NewGuid(), environment.Id, environment.Components[0].Id, ResourceKind.Container, 1, Guid.NewGuid(), "test", ImmutableDictionary<string, string>.Empty, "id", null, [], true, Guid.NewGuid(), false);
        var actual = new ResourceEvidence(ResourceKind.Container, "test", "id", ResourceOwnershipVerifier.Labels(container), null, [], true, "running");
        var observation = new EnvironmentObservation(environment.Id, Guid.NewGuid(), fixture.UtcNow, fixture.UtcNow, "current", [], [new(container, actual, new("id", true), fixture.UtcNow)], null);
        Assert.Null(commands.StopDisabledReason(environment.Id, observation));
        Assert.NotNull(commands.StopDisabledReason(environment.Id, observation with { Freshness = "stale" }));
        Assert.NotNull(commands.StopDisabledReason(environment.Id, observation with { Resources = [] }));
        var stopped = await commands.StopAsync(environment, TestContext.Current.CancellationToken); await stopped.Completion;
        Assert.Equal(OperationState.Succeeded, stopped.Current.State);
        Assert.Equal("stop", commands.Get(environment.Id)!.Current.Kind);
    }

    [Fact]
    public async Task Invalidated_generation_disables_start_and_explains_recovery_while_refresh_stays_available()
    {
        var fixture = new RuntimeFixture(); await using var coordinator = fixture.Coordinator();
        var commands = new EnvironmentCommands(new(coordinator, fixture, new(fixture, fixture), fixture), coordinator, new(), fixture, new(coordinator, fixture, fixture));
        var streams = new Streams();
        await using var vm = new InspectViewModel(new InvalidatedSource(), streams, commands, (action, _) => { action(); return Task.CompletedTask; });
        await vm.OpenAsync(fixture.Environment(), TestContext.Current.CancellationToken);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.True(vm.RefreshCommand.CanExecute(null));
        Assert.Contains("baseline", vm.StartDisabledReason); Assert.Contains("потери данных", vm.Error);
        Assert.Contains("running", vm.QuiescenceStatus);
    }

    private sealed class InvalidatedSource : IEnvironmentObservationSource
    {
        public Task<EnvironmentObservation> ReadAsync(IsolatedEnvironment environment, Guid session, EnvironmentObservation? previous, CancellationToken token) =>
            Task.FromResult(new EnvironmentObservation(environment.Id, session, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "current",
                [new ComponentObservation(environment.Components[0].Id, "container", "running", "healthy", [], ImmutableDictionary<string, Measurement>.Empty) { Reason = "data-generation-invalid" }], [], null));
    }

    [Fact]
    public async Task Start_compares_the_preview_actually_shown_instead_of_recapturing_changed_inputs()
    {
        var fixture = new RuntimeFixture(); await using var coordinator = fixture.Coordinator();
        IsolatedEnvironment environment = fixture.Environment();
        var reviews = new PreviewReviewStore();
        PreviewSnapshot shown = await fixture.CaptureAsync(environment, TestContext.Current.CancellationToken);
        reviews.Remember(shown with { Inputs = [new("source", "known", "old-source-hash", null)] });
        var commands = new EnvironmentCommands(new(coordinator, fixture, new(fixture, fixture), fixture), coordinator, reviews, fixture, new(coordinator, fixture, fixture));
        var handle = await commands.StartAsync(environment, TestContext.Current.CancellationToken); await handle.Completion;
        Assert.NotNull(fixture.LastDelta);
        Assert.Contains(fixture.LastDelta, delta => delta.Reference == "source" && delta.Before.Contains("old-source-hash", StringComparison.Ordinal));
        reviews.Remember(shown with { EnvironmentId = Guid.NewGuid() }); Assert.Null(reviews.Find(environment.Id));
    }
    [Fact]
    public async Task Switching_environment_waits_for_old_poll_and_disposal_and_discards_late_results()
    {
        var fixture = new RuntimeFixture(); await using var coordinator = fixture.Coordinator();
        var commands = new EnvironmentCommands(new(coordinator, fixture, new(fixture, fixture), fixture), coordinator, new PreviewReviewStore(), fixture, new(coordinator, fixture, fixture));
        var source = new Source(); var streams = new Streams();
        await using var vm = new InspectViewModel(source, streams, commands, (action, _) => { action(); return Task.CompletedTask; });
        IsolatedEnvironment first = fixture.Environment(); IsolatedEnvironment second = first with { Id = Guid.NewGuid(), Name = "second" };
        await vm.OpenAsync(first, TestContext.Current.CancellationToken);
        await source.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task switching = vm.OpenAsync(second, TestContext.Current.CancellationToken);
        Assert.False(switching.IsCompleted); Assert.Equal(1, streams.Created);
        source.Release.SetResult(); await switching;
        Assert.Equal(1, streams.Disposed); Assert.Equal(2, streams.Created); Assert.Equal("second", vm.Name);
        Assert.DoesNotContain("late-first", vm.Status);
        await vm.CloseSessionAsync(); Assert.Equal(2, streams.Disposed);
    }
    [Fact]
    public void Aggregates_distinguish_missing_zero_partial_and_stale()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Null(Measurement.Sum([Measurement.Missing("%", now, "unavailable")], 1).Value);
        Assert.Equal(0, Measurement.Sum([new(0, "%", now)], 1).Value);
        var partial = Measurement.Sum([new(12, "%", now), Measurement.Missing("%", now, "stopped")], 2);
        Assert.Equal(12, partial.Value);
        Assert.Equal(1, partial.Measured);
        Assert.False(partial.Complete);
        Assert.Null(Measurement.Sum([new Measurement(12, "%", now) { Freshness = "stale" }], 1).Value);
    }
    private sealed class Source : IEnvironmentObservationSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int count;
        public async Task<EnvironmentObservation> ReadAsync(IsolatedEnvironment environment, Guid session, EnvironmentObservation? previous, CancellationToken token)
        {
            if (Interlocked.Increment(ref count) == 1) { Entered.SetResult(); await Release.Task; }
            return new(environment.Id, session, DateTimeOffset.UtcNow, null, "unknown", [], [], null);
        }
    }
    private sealed class Streams : IInspectionStreamsFactory
    {
        public int Created { get; private set; }
        public int Disposed { get; private set; }
        public IInspectionStreams Create(IsolatedEnvironment environment) { Created++; return new Session(this); }
        private sealed class Session(Streams owner) : IInspectionStreams
        {
            public Task<InspectionStreamsSnapshot> ReadAsync(EnvironmentObservation observation, Guid? selected, bool paused, CancellationToken token) =>
                Task.FromResult(new InspectionStreamsSnapshot([], 0, 0, "unavailable", new Dictionary<Guid, IReadOnlyDictionary<string, Measurement>>()));
            public ValueTask DisposeAsync() { owner.Disposed++; return ValueTask.CompletedTask; }
        }
    }
}
