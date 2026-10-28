using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.UnitTests.Environments;

namespace EnvironmentBuilder.UnitTests.Operations;

public sealed class StartWorkflowTests
{
    [Fact]
    public async Task Declining_input_review_has_zero_effects_and_duplicate_clicks_keep_the_operation_id()
    {
        var fixture = new RuntimeFixture();
        await using var coordinator = fixture.Coordinator();
        IsolatedEnvironment environment = fixture.Environment();
        var service = new StartEnvironmentService(coordinator, fixture, new(fixture, fixture), fixture);
        var review = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        OperationHandle first = service.Start(environment, null, (_, _, _) => { entered.SetResult(); return review.Task; });
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Same(first, service.Start(environment, null, (_, _, _) => throw new InvalidOperationException("duplicate")));
        Assert.Empty(fixture.Events);
        review.SetResult(false); await first.Completion;
        Assert.Equal(OperationState.Cancelled, first.Current.State); Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task Runtime_readiness_precedes_mandatory_baseline_and_durable_success_is_not_replayed()
    {
        var fixture = new RuntimeFixture();
        await using var coordinator = fixture.Coordinator();
        var environment = fixture.Environment();
        var service = new StartEnvironmentService(coordinator, fixture, new(fixture, fixture), fixture);
        var first = service.Start(environment, null, (_, _, _) => Task.FromResult(true)); await first.Completion;
        Assert.Equal(OperationState.Succeeded, first.Current.State);
        Assert.Equal(new[] { "images", "start", "readiness", "http:bootstrap" }, fixture.Events);
        Assert.Equal(StepState.Succeeded, fixture.Applications.Values.Single().State);
        fixture.Events.Clear();
        var second = service.Start(environment, null, (_, _, _) => Task.FromResult(true)); await second.Completion;
        Assert.Equal(OperationState.Succeeded, second.Current.State);
        Assert.DoesNotContain("http:bootstrap", fixture.Events);
    }

    [Fact]
    public async Task Intent_failure_forbids_mutation_and_result_failure_stops_subsequent_stages()
    {
        foreach (bool failIntent in new[] { true, false })
        {
            var fixture = new RuntimeFixture { FailIntent = failIntent, FailResult = !failIntent };
            await using var coordinator = fixture.Coordinator();
            var handle = new StartEnvironmentService(coordinator, fixture, new(fixture, fixture), fixture)
                .Start(fixture.Environment(), null, (_, _, _) => Task.FromResult(true));
            await handle.Completion;
            Assert.Equal(OperationState.NeedsAttention, handle.Current.State);
            Assert.Equal(failIntent ? 0 : 1, fixture.Events.Count);
            Assert.DoesNotContain("start", fixture.Events);
        }
    }

    [Fact]
    public async Task Unknown_unsafe_initialization_blocks_dependents_and_preserves_started_components()
    {
        var fixture = new RuntimeFixture { LoseHttpResponse = true };
        await using var coordinator = fixture.Coordinator();
        var environment = fixture.Environment();
        EnvironmentComponent first = environment.Components[0];
        EnvironmentComponent dependent = first with { Id = Guid.NewGuid(), DefinitionSnapshot = first.DefinitionSnapshot with { Initialization = null } };
        environment = environment with { Components = [first, dependent], Dependencies = [new(dependent.Id, first.Id, null, "ready", "Needs initialized data")] };
        var handle = new StartEnvironmentService(coordinator, fixture, new(fixture, fixture), fixture).Start(environment, null, (_, _, _) => Task.FromResult(true));
        await handle.Completion;
        Assert.Equal(OperationState.NeedsAttention, handle.Current.State);
        Assert.Single(fixture.Events, e => e == "start");
        Assert.Equal(StepState.Unknown, fixture.Applications.Values.Single().State);
        Assert.DoesNotContain("stop", fixture.Events);
    }

    [Fact]
    public async Task A_known_preflight_block_is_a_durable_failure_and_does_not_require_runtime_reconciliation()
    {
        var fixture = new RuntimeFixture { PreflightBlock = "docker-unavailable" };
        await using var coordinator = fixture.Coordinator();
        var environment = fixture.Environment();
        var service = new StartEnvironmentService(coordinator, fixture, new(fixture, fixture), fixture);
        var handle = service.Start(environment, null, (_, _, _) => throw new InvalidOperationException("Review must not be reached."));
        await handle.Completion;
        Assert.Equal(OperationState.Failed, handle.Current.State); Assert.Equal("docker-unavailable", handle.Current.ResultCode);
        Assert.Empty(fixture.Events); Assert.Null(coordinator.AdmissionBlockReason(environment.Id));
    }
}

internal sealed class RuntimeFixture : IStartRuntime, IStopRuntime, IDataApplicationJournal, IOperationStore, IInitializationTransport, IClock, IRuntimeInteractions, IPreviewInputCapture
{
    private readonly Guid generation = Guid.NewGuid();
    public List<string> Events { get; } = [];
    public Dictionary<DataApplicationKey, DataApplicationRecord> Applications { get; } = [];
    public bool FailIntent { get; init; }
    public bool FailResult { get; init; }
    public bool LoseHttpResponse { get; init; }
    public bool InvalidateAfterSend { get; init; }
    public bool GenerationInvalidated { get; set; }
    public string? PreflightBlock { get; init; }
    public CloseOperationsDecision CloseDecision { get; set; } = CloseOperationsDecision.Return;
    public IReadOnlyList<InputDelta>? LastDelta { get; private set; }
    public Func<Operation, Task>? BeforeSave { get; init; }
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public OperationCoordinator Coordinator() => new(this, this, () => true);
    public IsolatedEnvironment Environment()
    {
        ComponentDefinition definition = AuthoringContractTests.Definition() with { DefaultPorts = [], Readiness = new("running", 10), Initialization = null };
        var initializer = new InitializationDefinition("initialization", 1, Guid.NewGuid(), "1.0.0", new(definition.Contract.Id, [definition.Contract.Version]),
            [new("bootstrap", "Bootstrap", definition.Endpoints[0].Id, "POST", "/bootstrap", ImmutableDictionary<string, Value>.Empty, new("none"), new([204], []), 5, new("none", 1))]);
        definition = definition with { Initialization = initializer };
        var draft = EnvironmentDraft.Create(); draft.AddComponent(definition, [definition]); draft.Update(draft.Snapshot with { Name = "Start fixture" });
        return draft.Snapshot;
    }
    public Task<IStartSession> PrepareAsync(IsolatedEnvironment environment, Guid operation, CancellationToken token) =>
        PreflightBlock is null ? Task.FromResult<IStartSession>(new Session(this, environment, generation)) : Task.FromException<IStartSession>(new PreflightBlockedException(PreflightBlock));
    public Task<IStopSession> PrepareAsync(IsolatedEnvironment environment, Guid operation, int timeoutSeconds, CancellationToken token) => Task.FromResult<IStopSession>(new StopSession(this, environment, generation));
    private sealed class StopSession(RuntimeFixture fixture, IsolatedEnvironment environment, Guid generation) : IStopSession
    {
        public OperationPlan Plan { get; } = new(Guid.NewGuid(), environment.Id, environment.Revision, CanonicalJson.Hash(environment), generation, CanonicalJson.Hash(environment.Baseline), [], [], ["stop"], "fixture");
        public Task<EnvironmentObservation> ReadAsync(CancellationToken token) => Task.FromResult(new EnvironmentObservation(environment.Id, Guid.NewGuid(), fixture.UtcNow, fixture.UtcNow, "current", [], [], null));
        public Task StopAsync(OwnedResource container, OperationExecution execution, CancellationToken token) => Task.CompletedTask;
    }
    public Task<DataApplicationRecord?> ReadAsync(DataApplicationKey key, CancellationToken token) => Task.FromResult(Applications.GetValueOrDefault(key));
    public Task SaveAsync(DataApplicationRecord record, CancellationToken token) { Applications[record.Key] = record; return Task.CompletedTask; }
    public Task WriteIntentAsync(OperationStep step, CancellationToken token) => FailIntent ? Task.FromException(new IOException("intent fault")) : Task.CompletedTask;
    public Task WriteResultAsync(OperationStep step, EffectObservation result, CancellationToken token) => FailResult ? Task.FromException(new IOException("result fault")) : Task.CompletedTask;
    public Task SaveAsync(Operation operation, CancellationToken token) => BeforeSave?.Invoke(operation) ?? Task.CompletedTask;
    public Task<IReadOnlyList<Operation>> ReadUnresolvedAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<Operation>>([]);
    public Task<InitializationResponse> SendAsync(Guid component, InitializationStep step, string key, Stream? body, CancellationToken token)
    { Events.Add("http:" + step.Id); GenerationInvalidated |= InvalidateAfterSend; return Task.FromResult(LoseHttpResponse ? new InitializationResponse(null, false, false, "lost-response") : new(204, true, true, null)); }
    public Task<bool> ConfirmInputsAsync(IReadOnlyList<InputDelta> delta, OperationPlan plan, CancellationToken token) { LastDelta = delta; return Task.FromResult(true); }
    public Task<CloseOperationsDecision> DecideCloseAsync(int count, CancellationToken token) => Task.FromResult(CloseDecision);
    public Task<PreviewSnapshot> CaptureAsync(IsolatedEnvironment environment, CancellationToken token) => Task.FromResult(Snapshot(environment));
    private static PreviewSnapshot Snapshot(IsolatedEnvironment environment) => new(environment.Id, 1, CanonicalJson.Hash(environment), JsonSerializer.Serialize(environment, CanonicalJson.Options), "compose-v1", CanonicalJson.Hash(environment.Baseline), [], [], [], []);
    private sealed class Session(RuntimeFixture fixture, IsolatedEnvironment environment, Guid generation) : IStartSession
    {
        public Task ValidateDataGenerationAsync(CancellationToken token) => fixture.GenerationInvalidated ? Task.FromException(new UnresolvedEffectException("data-generation-invalid")) : Task.CompletedTask;
        public PreviewSnapshot Snapshot { get; } = RuntimeFixture.Snapshot(environment);
        public OperationPlan Plan { get; } = new(Guid.NewGuid(), environment.Id, 1, CanonicalJson.Hash(environment), generation, CanonicalJson.Hash(environment.Baseline), [], [], [], "fixture");
        public IInitializationTransport Transport => fixture;
        public Task PrepareImagesAsync(OperationExecution execution, CancellationToken token) => execution.RunStepAsync(new(Plan.Id, "images", "prepare", "fixture"), _ =>
        { fixture.Events.Add("images"); return Task.CompletedTask; }, _ => Task.FromResult(new EffectObservation("image", true, null)));
        public Task<ComponentObservation> StartComponentAsync(EnvironmentComponent component, OperationExecution execution, CancellationToken token)
        { fixture.Events.Add("start"); return Task.FromResult(Observation(component.Id)); }
        public Task<ComponentObservation> WaitAsync(EnvironmentComponent component, string condition, CancellationToken token)
        { fixture.Events.Add("readiness"); return Task.FromResult(Observation(component.Id) with { RuntimeReady = true }); }
        public Task<Stream?> OpenBodyAsync(BaselineApplication application, InitializationStep step, CancellationToken token) => Task.FromResult<Stream?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private static ComponentObservation Observation(Guid id) => new(id, new string('a', 64), "running", "notConfigured", [], ImmutableDictionary<string, Measurement>.Empty);
    }
}
