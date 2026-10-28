using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Initialization;
using EnvironmentBuilder.Infrastructure.Persistence;

namespace EnvironmentBuilder.IntegrationTests.Support;

// Cleanup uses the real exact-ID Docker removal adapter through EditFixture. Temporary runtime
// and HTTP responses model independent resource/data state, with the real SQLite journal.
internal sealed class DataResetFixture : IRefreshRuntime, IStopRuntime, IInputResolver, ISeedPackStore, IAsyncDisposable
{
    public EditFixture Edit { get; private set; } = null!;
    public IsolatedEnvironment Environment => Edit.Environment;
    public List<string> Events { get; } = [];
    public int BusinessCount { get; private set; } = 11;
    public int ExpectedCount { get; private set; }
    public int SecondEnvironmentCount { get; } = 17;
    public int ForeignCount { get; } = 23;
    public int HttpCalls { get; private set; }
    public bool Running { get; private set; }
    public string? Fault { get; set; }
    public bool CancelAtFault { get; set; }
    public int Confirmations { get; private set; }
    public List<DataApplicationKey> OldApplications { get; } = [];
    public void Cancel() => cancellation.Cancel();
    private IsolatedEnvironment secondEnvironment = null!;
    private ImmutableArray<OwnedResource> temporary = [];
    private VerifiedSeedPack? pack;
    private readonly CancellationTokenSource cancellation = new();
    public StopEnvironmentService Stop => new(Edit.Coordinator, this, Edit.Store.Clock);
    public ClearEnvironmentDataService Clear { get; private set; } = null!;
    public RefreshEnvironmentDataService Restore { get; private set; } = null!;

    public static async Task<DataResetFixture> CreateAsync(string baseline = "mandatory")
    {
        var fixture = new DataResetFixture();
        fixture.Edit = await EditFixture.CreateAsync(configure: environment =>
        {
            if (baseline == "empty")
            {
                var component = environment.Components[0];
                return environment with { Components = [component with { DefinitionSnapshot = component.DefinitionSnapshot with { Initialization = null } }], Baseline = new([]) };
            }
            if (baseline == "pack")
            {
                string root = Path.Combine(AppContext.BaseDirectory, "Contracts/examples/seedpack");
                SeedPack manifest = fixture.EditReader<SeedPack>(Path.Combine(root, "manifest.json"));
                InitializationDefinition initializer = fixture.EditReader<InitializationDefinition>(Path.Combine(root, "initializers/requests.json"));
                fixture.pack = new(manifest, ImmutableDictionary<string, InitializationDefinition>.Empty.Add("initializers/requests.json", initializer));
                return environment with { Baseline = new(environment.Baseline.Applications.Add(new(environment.Components[0].Id, initializer, new(manifest.Id, manifest.Version, manifest.ContentHash)))) };
            }
            return environment;
        });
        fixture.ExpectedCount = baseline == "empty" ? 0 : baseline == "pack" ? 5 : 3;
        fixture.secondEnvironment = fixture.Environment with { Id = fixture.Edit.SecondEnvironmentId, Name = "Independent running copy" };
        await fixture.Edit.Store.Environments.SaveNewAsync(fixture.secondEnvironment, TestContext.Current.CancellationToken);
        var journal = new DataApplicationStore(fixture.Edit.Store.Worker);
        Guid oldGeneration = await fixture.Edit.GenerationAsync();
        foreach (var application in fixture.Environment.Baseline.Applications)
        {
            foreach (var step in application.Initializer.Steps)
            {
                var key = new DataApplicationKey(fixture.Environment.Id, application.ComponentId, oldGeneration, CanonicalJson.Hash(fixture.Environment.Baseline), CanonicalJson.Hash(application), CanonicalJson.Hash(application.Initializer), step.Id);
                fixture.OldApplications.Add(key);
                var record = new DataApplicationRecord(key, StepState.Pending, 0, null, null, null, null, null);
                await journal.SaveAsync(record, TestContext.Current.CancellationToken);
                record = record with { State = StepState.Started, Attempt = 1, StartedAt = fixture.Edit.Store.Clock.UtcNow };
                await journal.SaveAsync(record, TestContext.Current.CancellationToken);
                await journal.SaveAsync(record with { State = StepState.Succeeded, CompletedAt = fixture.Edit.Store.Clock.UtcNow, AcceptedStatus = step.Expect.Statuses[0], AssertionsPassed = true }, TestContext.Current.CancellationToken);
            }
        }
        fixture.Edit.AfterRemoval = () =>
        {
            if (fixture.Edit.Resources.Where(r => r.Kind == ResourceKind.Volume).All(r => fixture.Edit.Absent.Contains(r.ResourceId))) { fixture.BusinessCount = 0; }
        };
        var persistence = new DataResetFinalization(fixture.Edit.Store.Worker, fixture.Edit.Store.Clock);
        fixture.Clear = new(fixture.Edit, new(new EditFinalization(fixture.Edit.Store.Worker, fixture.Edit.Store.Clock)), persistence, fixture.Edit.Coordinator, fixture.Edit.Store.Clock);
        fixture.Restore = new(fixture.Clear, new(fixture, fixture, fixture), new(new DataApplicationStore(fixture.Edit.Store.Worker), fixture.Edit.Store.Clock), fixture, fixture.Edit.Coordinator, fixture.Edit.Store.Clock);
        return fixture;
    }
    private T EditReader<T>(string path) where T : class => new EnvironmentBuilder.Infrastructure.Imports.StrictDocumentReader().Read<T>(File.ReadAllBytes(path));

    public async Task<Operation> RunAsync(bool refresh)
    {
        var handle = refresh ? Restore.Refresh(Environment, ConfirmAsync) : Clear.Clear(Environment, ConfirmAsync);
        using var registration = cancellation.Token.Register(() => _ = Edit.Coordinator.RequestCancelAsync(Environment.Id, CancellationToken.None));
        await handle.Completion;
        return handle.Current;
    }
    private Task<ConfirmationRecord?> ConfirmAsync(OperationPlan plan, CancellationToken token)
    {
        Confirmations++;
        return Task.FromResult<ConfirmationRecord?>(Edit.Fault == "cancel" ? null : new(Edit.Fault == "stale-token" ? "stale" : plan.Hash, Edit.Store.Clock.UtcNow));
    }
    public async Task AssertConfigurationAsync()
    {
        Assert.Equal(CanonicalJson.Hash(Environment), CanonicalJson.Hash(await Edit.SavedAsync()));
        var second = (await Edit.Store.Environments.ListAsync(TestContext.Current.CancellationToken)).Single(e => e.Id == secondEnvironment.Id).Document;
        Assert.Equal(CanonicalJson.Hash(secondEnvironment), CanonicalJson.Hash(second));
    }
    public Task<IReadOnlyList<ReadinessIssue>> CheckAsync(IReadOnlyList<InputReference> references, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<ReadinessIssue>>(Fault == "secret" ? [new("missing-secret", "/environment", "Unavailable secret")] : []);
    public Task<VerifiedSeedPack?> FindAsync(SeedPackReference reference, CancellationToken token)
    {
        if (Fault == "corrupt-pack") { throw new IOException("Unavailable immutable blob"); }
        return Task.FromResult(Fault == "missing-pack" ? null : Fault == "apply-contract" ? pack! with { Initializers = ImmutableDictionary<string, InitializationDefinition>.Empty } : pack);
    }
    public Task<IStartSession> PrepareAsync(IsolatedEnvironment environment, Guid operationId, Guid generation, CancellationToken token)
    {
        Events.Add("preflight");
        if (Fault == "provider") { throw new PreflightBlockedException("provider-unavailable"); }
        return Task.FromResult<IStartSession>(new StartSession(this, operationId, generation));
    }
    private void Stage(string name)
    {
        Events.Add(name);
        if (Fault != name) { return; }
        if (CancelAtFault) { cancellation.Cancel(); throw new OperationCanceledException(); }
        throw new UnresolvedEffectException("injected-" + name);
    }

    private sealed class StartSession : IStartSession
    {
        private readonly DataResetFixture fixture;
        private readonly HttpInitializationTransport http;
        public PreviewSnapshot Snapshot { get; }
        public OperationPlan Plan { get; }
        public IInitializationTransport Transport => http;
        public StartSession(DataResetFixture fixture, Guid operation, Guid generation)
        {
            this.fixture = fixture;
            var environment = fixture.Environment;
            Snapshot = new(environment.Id, environment.Revision, CanonicalJson.Hash(environment), JsonSerializer.Serialize(environment, CanonicalJson.Options), "compose-v1", CanonicalJson.Hash(environment.Baseline), [], [], [], []);
            Plan = new(operation, environment.Id, environment.Revision, CanonicalJson.Hash(environment), generation, CanonicalJson.Hash(environment.Baseline),
                fixture.Edit.Resources.Select(r => r with { ResourceId = Guid.NewGuid(), DataGenerationId = generation, ActualDockerId = null, Unresolved = true, OperationId = operation }).ToImmutableArray(), [], [], "temporary")
            { EngineId = "engine-one", Endpoint = "npipe://local-engine" };
            var handler = new FakeHttpMessageHandler();
            foreach (InitializationStep step in environment.Baseline.Applications.SelectMany(a => a.Initializer.Steps))
            {
                handler.Enqueue((_, _) =>
                {
                    fixture.Stage("http"); fixture.HttpCalls++;
                    fixture.BusinessCount += step.Body.Kind == "seedSection" ? 2 : 3;
                    return Task.FromResult(new HttpResponseMessage((HttpStatusCode)step.Expect.Statuses[0]) { Content = new StringContent("{\"accepted\":2}") });
                });
            }
            http = new(handler, (_, _) => { Assert.True(fixture.Running); return new Uri("http://127.0.0.1:49152/"); }, value => value.Literal!);
        }
        public async Task ValidateDataGenerationAsync(CancellationToken token)
        {
            if (fixture.Fault == "lost-volume" && fixture.Events.Contains("verified-stop")) { throw new UnresolvedEffectException("persisted-volume-missing"); }
            if (!await new DataApplicationStore(fixture.Edit.Store.Worker).IsActiveGenerationAsync(fixture.Environment.Id, Plan.DataGenerationId, Plan.BaselineHash, token))
            { throw new UnresolvedEffectException("data-generation-invalid"); }
        }
        public Task PrepareImagesAsync(OperationExecution execution, CancellationToken token) { fixture.Stage("images"); return Task.CompletedTask; }
        public async Task<ComponentObservation> StartComponentAsync(EnvironmentComponent component, OperationExecution execution, CancellationToken token)
        {
            fixture.Stage("start");
            await execution.RunStepAsync(new(Plan.Id, "temporary-start", "create", component.Id.ToString()), async safe =>
            {
                fixture.Running = true;
                fixture.temporary = Plan.Resources.Select(r => r with { ActualDockerId = r.Kind == ResourceKind.Volume ? r.Name : new string(r.Kind == ResourceKind.Container ? 'd' : 'e', 64), Unresolved = false }).ToImmutableArray();
                foreach (OwnedResource resource in fixture.temporary) { await fixture.Edit.Ledger.SaveAsync(resource, "created", null, safe); }
            }, _ => Task.FromResult(new EffectObservation(component.Id.ToString(), true))).ConfigureAwait(false);
            return Observation(component.Id);
        }
        public Task<ComponentObservation> WaitAsync(EnvironmentComponent component, string condition, CancellationToken token)
        { fixture.Stage("readiness"); return Task.FromResult(Observation(component.Id) with { RuntimeReady = true }); }
        public Task<Stream?> OpenBodyAsync(BaselineApplication application, InitializationStep step, CancellationToken token) => Task.FromResult<Stream?>(step.Body.Kind == "seedSection"
            ? File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Contracts/examples/seedpack/sections/requests.ndjson")) : null);
        public ValueTask DisposeAsync() { http.Dispose(); return ValueTask.CompletedTask; }
        private static ComponentObservation Observation(Guid component) => new(component, new string('d', 64), "running", "notConfigured", [new(8080, "tcp", "127.0.0.1", 49152)], ImmutableDictionary<string, Measurement>.Empty);
    }
    Task<IStopSession> IStopRuntime.PrepareAsync(IsolatedEnvironment environment, Guid operationId, int timeoutSeconds, CancellationToken token) => Task.FromResult<IStopSession>(new StopSession(this, operationId));
    private sealed class StopSession(DataResetFixture fixture, Guid operation) : IStopSession
    {
        public OperationPlan Plan { get; } = new(operation, fixture.Environment.Id, fixture.Environment.Revision, CanonicalJson.Hash(fixture.Environment), fixture.temporary[0].DataGenerationId,
            CanonicalJson.Hash(fixture.Environment.Baseline), fixture.temporary, [], [], "stop")
        { EngineId = "engine-one", Endpoint = "npipe://local-engine" };
        public Task StopAsync(OwnedResource container, OperationExecution execution, CancellationToken token)
        {
            fixture.Stage("stop");
            return execution.RunStepAsync(new(operation, "stop-" + container.ResourceId, "stop", container.ActualDockerId!), _ =>
            { if (fixture.Fault != "unknown-stop") { fixture.Running = false; } return Task.CompletedTask; }, _ => Task.FromResult(new EffectObservation(container.ActualDockerId, !fixture.Running)));
        }
        public Task<EnvironmentObservation> ReadAsync(CancellationToken token)
        {
            if (!fixture.Running) { fixture.Events.Add("verified-stop"); }
            return Task.FromResult(new EnvironmentObservation(fixture.Environment.Id, Guid.NewGuid(), fixture.Edit.Store.Clock.UtcNow, fixture.Edit.Store.Clock.UtcNow, "current", [],
                fixture.temporary.Select(r => new ResourceObservation(r, new(r.Kind, r.Name, r.ActualDockerId!, ResourceOwnershipVerifier.Labels(r), r.Volume, [], true,
                    r.Kind == ResourceKind.Container ? fixture.Running ? "running" : "exited" : "present"), new(r.ActualDockerId, true), fixture.Edit.Store.Clock.UtcNow)).ToImmutableArray(), null));
        }
    }
    public async ValueTask DisposeAsync() { cancellation.Dispose(); await Edit.DisposeAsync(); }
}
