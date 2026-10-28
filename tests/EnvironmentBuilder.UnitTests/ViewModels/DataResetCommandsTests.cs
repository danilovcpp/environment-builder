using System.Collections.Immutable;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.UnitTests.Operations;

namespace EnvironmentBuilder.UnitTests.ViewModels;

public sealed class DataResetCommandsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Declined_or_stale_confirmation_keeps_data_commands_at_zero_effects(bool stale)
    {
        var runtime = new RuntimeFixture(); await using var coordinator = runtime.Coordinator();
        var reset = new ResetRuntime(runtime) { Stale = stale, Decline = !stale };
        var commands = reset.Commands(coordinator);
        var handle = await commands.ClearAsync(runtime.Environment(), TestContext.Current.CancellationToken); await handle.Completion;
        Assert.Equal(stale ? OperationState.Failed : OperationState.Cancelled, handle.Current.State);
        Assert.Equal(1, reset.Confirmations); Assert.Equal(0, reset.Commits); Assert.Empty(runtime.Events);
        Assert.Contains("ревизия", reset.Shown!.Consequences); Assert.Null(reset.Shown.Candidate);
    }

    [Fact]
    public async Task Partial_restore_stays_visible_and_requires_a_separate_explicit_stop()
    {
        var runtime = new RuntimeFixture { LoseHttpResponse = true }; await using var coordinator = runtime.Coordinator();
        var reset = new ResetRuntime(runtime); var commands = reset.Commands(coordinator);
        var environment = runtime.Environment();
        var handle = await commands.RestoreAsync(environment, TestContext.Current.CancellationToken); await handle.Completion;
        Assert.Equal(OperationState.NeedsAttention, handle.Current.State); Assert.Same(handle, commands.Get(environment.Id));
        Assert.True(handle.Current.DataResetCompleted); Assert.Equal(1, reset.Commits);
        Assert.Contains("start", runtime.Events); Assert.DoesNotContain("stop", runtime.Events);
        Assert.Equal(0, reset.StopPreparations);
        var stop = await commands.StopAsync(environment, TestContext.Current.CancellationToken); await stop.Completion;
        Assert.Equal(OperationState.Succeeded, stop.Current.State); Assert.Equal("stop", stop.Current.Kind); Assert.Equal(1, reset.StopPreparations);
    }

    [Fact]
    public async Task Missing_inputs_disable_restoration_but_do_not_prevent_clear_or_observation()
    {
        var runtime = new RuntimeFixture(); await using var coordinator = runtime.Coordinator();
        var reset = new ResetRuntime(runtime) { MissingInput = true }; var commands = reset.Commands(coordinator);
        Assert.Contains("missing-secret", await commands.CheckRestoreInputsAsync(runtime.Environment(), TestContext.Current.CancellationToken));
        var handle = await commands.RestoreAsync(runtime.Environment(), TestContext.Current.CancellationToken); await handle.Completion;
        Assert.Equal(OperationState.Failed, handle.Current.State); Assert.Equal(0, reset.Confirmations); Assert.Empty(runtime.Events);
        var clear = await commands.ClearAsync(runtime.Environment(), TestContext.Current.CancellationToken); await clear.Completion;
        Assert.Equal(OperationState.Succeeded, clear.Current.State);
    }

    [Fact]
    public async Task Observation_command_only_reads_state_and_never_confirms_or_mutates_data()
    {
        var runtime = new RuntimeFixture(); await using var coordinator = runtime.Coordinator();
        var reset = new ResetRuntime(runtime); var commands = reset.Commands(coordinator); var source = new ReadOnlyObservation();
        await using var vm = new InspectViewModel(source, new EmptyStreams(), commands, (action, _) => { action(); return Task.CompletedTask; });
        await vm.OpenAsync(runtime.Environment(), TestContext.Current.CancellationToken);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(source.Reads >= 2); Assert.Empty(runtime.Events); Assert.Empty(runtime.Applications); Assert.Equal(0, reset.Confirmations); Assert.Equal(0, reset.Commits);
        Assert.True(vm.ClearDataCommand.CanExecute(null)); Assert.True(vm.RestoreBaselineCommand.CanExecute(null));
    }

    [Fact]
    public async Task Data_commands_have_distinct_consequences_and_require_confirmed_quiescence()
    {
        var fixture = new RuntimeFixture(); await using var coordinator = fixture.Coordinator();
        var commands = new EnvironmentCommands(new(coordinator, fixture, new(fixture, fixture), fixture), coordinator, new(), fixture, new(coordinator, fixture, fixture));
        var environment = fixture.Environment();
        Assert.NotEqual(EnvironmentCommands.ObservationLabel, EnvironmentCommands.ClearLabel);
        Assert.NotEqual(EnvironmentCommands.ClearLabel, EnvironmentCommands.RestoreLabel);
        Assert.Contains("перечитывает", EnvironmentCommands.ObservationExplanation);
        Assert.Contains("удал", EnvironmentCommands.ClearExplanation);
        Assert.Contains("останов", EnvironmentCommands.RestoreExplanation);
        Assert.NotNull(commands.DataResetDisabledReason(environment.Id, null));
        var observed = new EnvironmentObservation(environment.Id, Guid.NewGuid(), fixture.UtcNow, fixture.UtcNow, "current", [], [], null);
        Assert.Null(commands.DataResetDisabledReason(environment.Id, observed));
        Assert.NotNull(commands.DataResetDisabledReason(environment.Id, observed with { Freshness = "stale" }));
        var container = new OwnedResource(Guid.NewGuid(), Guid.NewGuid(), environment.Id, environment.Components[0].Id, ResourceKind.Container, 1,
            Guid.NewGuid(), "test", ImmutableDictionary<string, string>.Empty, "id", null, [], true, Guid.NewGuid(), false);
        var actual = new ResourceEvidence(ResourceKind.Container, "test", "id", ResourceOwnershipVerifier.Labels(container), null, [], true, "running");
        observed = observed with { Resources = [new(container, actual, new("id", true), fixture.UtcNow)] };
        Assert.NotNull(commands.DataResetDisabledReason(environment.Id, observed));
        Assert.Null(commands.StopDisabledReason(environment.Id, observed));
        observed = observed with { Resources = [new(container, actual with { State = "exited" }, new("id", false, "ownership"), fixture.UtcNow)] };
        Assert.NotNull(commands.DataResetDisabledReason(environment.Id, observed));
        Assert.Empty(fixture.Events);
    }

    private sealed class ResetRuntime(RuntimeFixture runtime) : ICleanupRuntime, IRefreshRuntime, IStopRuntime, IEditPersistence, IDataResetPersistence, IInputResolver, ISeedPackStore, IDestructiveInteractions
    {
        private readonly Guid generation = Guid.NewGuid();
        public bool Decline { get; init; }
        public bool Stale { get; init; }
        public bool MissingInput { get; init; }
        public int Confirmations { get; private set; }
        public int Commits { get; private set; }
        public int StopPreparations { get; private set; }
        public OperationPlan? Shown { get; private set; }
        public EnvironmentCommands Commands(OperationCoordinator coordinator)
        {
            var clear = new ClearEnvironmentDataService(this, new(this), this, coordinator, runtime);
            var restore = new RefreshEnvironmentDataService(clear, new(this, this, this), new(runtime, runtime), this, coordinator, runtime);
            return new(new(coordinator, runtime, new(runtime, runtime), runtime), coordinator, new(), runtime, new(coordinator, this, runtime),
                clear: clear, restore: restore, destructive: this, inputs: this, packs: this);
        }
        Task<IStopSession> IStopRuntime.PrepareAsync(IsolatedEnvironment environment, Guid operationId, int timeoutSeconds, CancellationToken token)
        { StopPreparations++; return runtime.PrepareAsync(environment, operationId, timeoutSeconds, token); }
        public Task<ConfirmationRecord?> ConfirmAsync(OperationPlan plan, CancellationToken token)
        { Confirmations++; Shown = plan; return Task.FromResult<ConfirmationRecord?>(Decline ? null : new(Stale ? "stale" : plan.Hash, runtime.UtcNow)); }
        public Task<IReadOnlyList<ReadinessIssue>> CheckAsync(IReadOnlyList<InputReference> references, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ReadinessIssue>>(MissingInput ? [new("missing-secret", "/environment", "Unavailable")] : []);
        public Task<VerifiedSeedPack?> FindAsync(SeedPackReference reference, CancellationToken token) => Task.FromResult<VerifiedSeedPack?>(null);
        public Task InvalidateAsync(OperationPlan plan, Guid operationId, CancellationToken token) => Task.CompletedTask;
        public Task FinalizeAsync(Operation operation, CancellationToken token) { Commits++; return Task.CompletedTask; }
        public Task<ICleanupSession> PrepareAsync(IsolatedEnvironment environment, Guid operationId, CancellationToken token) =>
            Task.FromResult<ICleanupSession>(new Cleanup(environment, new(operationId, environment.Id, 1, CanonicalJson.Hash(environment), generation, CanonicalJson.Hash(environment.Baseline), [], [], [], "cleanup") { EngineId = "engine", Endpoint = "local" }));
        public async Task<IStartSession> PrepareAsync(IsolatedEnvironment environment, Guid operationId, Guid next, CancellationToken token)
        {
            var session = await runtime.PrepareAsync(environment, operationId, token);
            return new Temporary(session, session.Plan with { DataGenerationId = next, EngineId = "engine", Endpoint = "local" });
        }
        private sealed class Cleanup(IsolatedEnvironment environment, OperationPlan plan) : ICleanupSession
        {
            public OperationPlan Scope => plan;
            public Task<EnvironmentObservation> ReadAsync(CancellationToken token) => Task.FromResult(new EnvironmentObservation(environment.Id, Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "current", [], [], null));
            public Task RevalidateAsync(CancellationToken token) => Task.CompletedTask;
            public Task RemoveAsync(OperationPlan reviewed, ImmutableArray<string> removed, OwnedResource resource, OperationExecution execution, CancellationToken token) => throw new InvalidOperationException("Empty scope");
        }
        private sealed class Temporary(IStartSession session, OperationPlan plan) : IStartSession
        {
            public PreviewSnapshot Snapshot => session.Snapshot;
            public OperationPlan Plan => plan;
            public IInitializationTransport Transport => session.Transport;
            public Task ValidateDataGenerationAsync(CancellationToken token) => session.ValidateDataGenerationAsync(token);
            public Task PrepareImagesAsync(OperationExecution execution, CancellationToken token) => session.PrepareImagesAsync(execution, token);
            public Task<ComponentObservation> StartComponentAsync(EnvironmentComponent component, OperationExecution execution, CancellationToken token) => session.StartComponentAsync(component, execution, token);
            public Task<ComponentObservation> WaitAsync(EnvironmentComponent component, string condition, CancellationToken token) => session.WaitAsync(component, condition, token);
            public Task<System.IO.Stream?> OpenBodyAsync(BaselineApplication application, InitializationStep step, CancellationToken token) => session.OpenBodyAsync(application, step, token);
            public ValueTask DisposeAsync() => session.DisposeAsync();
        }
    }
    private sealed class ReadOnlyObservation : IEnvironmentObservationSource
    {
        public int Reads { get; private set; }
        public Task<EnvironmentObservation> ReadAsync(IsolatedEnvironment environment, Guid session, EnvironmentObservation? previous, CancellationToken token)
        { Reads++; return Task.FromResult(new EnvironmentObservation(environment.Id, session, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "current", [], [], null)); }
    }
    private sealed class EmptyStreams : IInspectionStreamsFactory, IInspectionStreams
    {
        public IInspectionStreams Create(IsolatedEnvironment environment) => this;
        public Task<InspectionStreamsSnapshot> ReadAsync(EnvironmentObservation observation, Guid? component, bool paused, CancellationToken token) =>
            Task.FromResult(new InspectionStreamsSnapshot([], 0, 0, "unavailable", new Dictionary<Guid, IReadOnlyDictionary<string, Measurement>>()));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
