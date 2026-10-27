using System.Collections.Immutable;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Operations;

public sealed class StartFaultTests
{
    [Theory]
    [InlineData("images", false)]
    [InlineData("runtime", false)]
    [InlineData("readiness", false)]
    [InlineData("baseline", false)]
    [InlineData("images", true)]
    [InlineData("runtime", true)]
    [InlineData("readiness", true)]
    [InlineData("baseline", true)]
    public async Task Failure_or_cancel_at_each_start_stage_records_durable_outcome_without_stop_or_cleanup(string stage, bool cancel)
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        IsolatedEnvironment environment = fixture.Example();
        var application = environment.Baseline.Applications[0];
        var initializer = application.Initializer with { Steps = application.Initializer.Steps.Select(s => s with { Retry = new RetryPolicy("none", 1) }).ToImmutableArray() };
        environment = environment with
        {
            Components = environment.Components.SetItem(0, environment.Components[0] with { DefinitionSnapshot = environment.Components[0].DefinitionSnapshot with { Initialization = initializer } }),
            Baseline = new(environment.Baseline.Applications.SetItem(0, application with { Initializer = initializer }))
        };
        await fixture.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken);
        await using var coordinator = new OperationCoordinator(fixture.Operations, fixture.Clock, () => true);
        Guid generation = await fixture.Worker.ReadAsync(connection =>
        { using var command = connection.CreateCommand(); command.CommandText = "SELECT id FROM DataGenerations"; return Guid.Parse((string)command.ExecuteScalar()!); }, TestContext.Current.CancellationToken);
        var runtime = new StageRuntime(generation, async current =>
        {
            if (current != stage) { return; }
            if (cancel) { await coordinator.RequestCancelAsync(environment.Id, CancellationToken.None); }
            else { throw new IOException("accepted-stage-fault"); }
        });
        var service = new StartEnvironmentService(coordinator, runtime, new(new DataApplicationStore(fixture.Worker), fixture.Clock), fixture.Clock);
        OperationHandle handle = service.Start(environment, null, (_, _, _) => Task.FromResult(true)); await handle.Completion;
        Assert.Equal(cancel ? OperationState.Cancelled : OperationState.NeedsAttention, handle.Current.State);
        Assert.Equal(runtime.Stages.IndexOf(stage) + 1, runtime.Stages.Count);
        Assert.DoesNotContain(runtime.Stages, s => s is "stop" or "cleanup");
        long unknownSteps = await fixture.Worker.ReadAsync(connection =>
        { using var command = connection.CreateCommand(); command.CommandText = "SELECT count(*) FROM OperationSteps WHERE state IN ('Started','Unknown')"; return (long)command.ExecuteScalar()!; }, TestContext.Current.CancellationToken);
        if (cancel) { Assert.Equal(0, unknownSteps); }
        if (stage == "baseline")
        {
            string state = await fixture.Worker.ReadAsync(connection =>
            { using var command = connection.CreateCommand(); command.CommandText = "SELECT state FROM DataApplicationRecords"; return (string)command.ExecuteScalar()!; }, TestContext.Current.CancellationToken);
            Assert.Equal(cancel ? "Succeeded" : "Unknown", state);
        }
    }
    [Fact]
    public async Task Failed_client_is_observed_and_its_actual_identity_is_durable_without_continuing_scheduling()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        var environment = fixture.Example(); await fixture.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken);
        Operation operation = await fixture.CreateOperationAsync(environment);
        int observations = 0;
        var runner = new DurableEffectRunner(fixture.Operations);
        StepState result = await runner.RunAsync(new(operation.Id, "create", "create-container", "fixture"), _ => throw new IOException("accepted then client failed"),
            _ => { observations++; return Task.FromResult(new EffectObservation("actual-created-container", true, null)); }, TestContext.Current.CancellationToken);
        Assert.Equal(StepState.Unknown, result); Assert.Equal(1, observations);
        string saved = await fixture.Worker.ReadAsync(connection =>
        { using var command = connection.CreateCommand(); command.CommandText = "SELECT resultJson FROM OperationSteps"; return (string)command.ExecuteScalar()!; }, TestContext.Current.CancellationToken);
        Assert.Contains("actual-created-container", saved); Assert.Contains("\"verified\":false", saved);
    }
    [Fact]
    public void Unknown_to_resolved_image_is_a_concrete_delta_and_missing_review_requires_confirmation()
    {
        var unknown = new PreviewSnapshot(Guid.NewGuid(), 1, "hash", "{}", "compose-v1", "baseline", [], [], [], [new("image", "unknown", null, null)]);
        var resolved = unknown with { Inputs = [new("image", "known", null, "sha256:pinned")] };
        Assert.NotEmpty(StartPreflight.Delta(unknown, resolved));
        Assert.NotEmpty(StartPreflight.Delta(null, resolved));
        Assert.Empty(StartPreflight.Delta(resolved, resolved));
    }

    private sealed class StageRuntime(Guid generation, Func<string, Task> reach) : IStartRuntime
    {
        public List<string> Stages { get; } = [];
        public Task<IStartSession> PrepareAsync(IsolatedEnvironment document, Guid operationId, CancellationToken token) => Task.FromResult<IStartSession>(new Session(this, operationId, document, generation));
        private async Task ReachedAsync(string stage) { Stages.Add(stage); await reach(stage); }
        private sealed class Session(StageRuntime owner, Guid operation, IsolatedEnvironment document, Guid generation) : IStartSession, IInitializationTransport
        {
            public PreviewSnapshot Snapshot { get; } = new(document.Id, 1, CanonicalJson.Hash(document), "{}", "compose-v1", CanonicalJson.Hash(document.Baseline), [], [], [], []);
            public OperationPlan Plan { get; } = new(Guid.NewGuid(), document.Id, 1, CanonicalJson.Hash(document), generation, CanonicalJson.Hash(document.Baseline), [], [], [], "fixture");
            public IInitializationTransport Transport => this;
            public Task PrepareImagesAsync(OperationExecution execution, CancellationToken token) => execution.RunStepAsync(new(operation, "images", "image-build", "fixture"), _ => owner.ReachedAsync("images"), _ => Task.FromResult(new EffectObservation("pinned-image", true, null)));
            public async Task<ComponentObservation> StartComponentAsync(EnvironmentComponent component, OperationExecution execution, CancellationToken token)
            {
                await execution.RunStepAsync(new(operation, "runtime", "container-create", component.Id.ToString()), _ => owner.ReachedAsync("runtime"), _ => Task.FromResult(new EffectObservation("owned-running-container", true, null)));
                return Observation(component.Id);
            }
            public async Task<ComponentObservation> WaitAsync(EnvironmentComponent component, string condition, CancellationToken token)
            { token.ThrowIfCancellationRequested(); await owner.ReachedAsync("readiness"); token.ThrowIfCancellationRequested(); return Observation(component.Id) with { RuntimeReady = true }; }
            public async Task<InitializationResponse> SendAsync(Guid component, InitializationStep step, string key, Stream? body, CancellationToken token)
            { await owner.ReachedAsync("baseline"); return new(204, true, true, null); }
            public Task<Stream?> OpenBodyAsync(BaselineApplication application, InitializationStep step, CancellationToken token) => Task.FromResult<Stream?>(null);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            private static ComponentObservation Observation(Guid id) => new(id, "owned-running-container", "running", "notConfigured", [], ImmutableDictionary<string, Measurement>.Empty);
        }
    }
}
