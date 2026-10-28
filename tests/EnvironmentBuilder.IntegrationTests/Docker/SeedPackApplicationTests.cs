using System.Collections.Immutable;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Imports;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.IntegrationTests.SeedPacks;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Docker;

public sealed class SeedPackApplicationTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("cancel")]
    [InlineData("running")]
    public async Task Unknown_refresh_requires_stopped_state_and_a_new_confirmation_before_replacing_generation(string scenario)
    {
        await using var fixture = await DataResetFixture.CreateAsync();
        var environment = fixture.Environment;
        var oldKey = fixture.OldApplications[0];
        await fixture.Edit.Store.Worker.CommitAsync("interrupted-http-fixture", (connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE DataApplicationRecords SET state='Unknown' WHERE idempotencyKey=$key"; command.Parameters.AddWithValue("$key", oldKey.StableIdempotencyKey);
            return command.ExecuteNonQuery();
        }, TestContext.Current.CancellationToken);
        var plan = new OperationPlan(Guid.NewGuid(), environment.Id, 1, CanonicalJson.Hash(environment), oldKey.DataGenerationId, oldKey.BaselineHash, [], [], [], "interrupted initialization");
        var interrupted = new Operation(Guid.NewGuid(), environment.Id, "start", OperationState.NeedsAttention, fixture.Edit.Store.Clock.UtcNow, 1, plan.ConfigurationHash, plan);
        await fixture.Edit.Store.Operations.SaveAsync(interrupted, TestContext.Current.CancellationToken);
        await fixture.Edit.Coordinator.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(fixture.Edit.Coordinator.AdmissionBlockReason(environment.Id));
        if (scenario == "running") { fixture.Edit.Running = true; }
        var journal = new DataApplicationStore(fixture.Edit.Store.Worker);
        var restore = new RefreshEnvironmentDataService(fixture.Clear, new(fixture, fixture, fixture), new(journal, fixture.Edit.Store.Clock), fixture, fixture.Edit.Coordinator, fixture.Edit.Store.Clock, journal);
        int confirmations = 0;
        var handle = restore.RefreshAfterUnknown(environment, (confirmed, token) =>
        {
            confirmations++;
            return Task.FromResult<ConfirmationRecord?>(scenario == "cancel" ? null : new(confirmed.Hash, fixture.Edit.Store.Clock.UtcNow));
        });
        await handle.Completion;
        if (scenario == "success")
        {
            Assert.Equal(OperationState.Succeeded, handle.Current.State); Assert.Equal(1, confirmations);
            Assert.NotEqual(oldKey.DataGenerationId, await fixture.Edit.GenerationAsync()); Assert.False(fixture.Running);
            Assert.Equal(fixture.ExpectedCount, fixture.BusinessCount); Assert.Null(fixture.Edit.Coordinator.AdmissionBlockReason(environment.Id));
            Assert.Empty(await fixture.Edit.Store.Operations.ReadUnresolvedAsync(TestContext.Current.CancellationToken));
        }
        else
        {
            Assert.NotEqual(OperationState.Succeeded, handle.Current.State); Assert.Empty(fixture.Edit.Mutations);
            Assert.Equal(oldKey.DataGenerationId, await fixture.Edit.GenerationAsync()); Assert.NotNull(fixture.Edit.Coordinator.AdmissionBlockReason(environment.Id));
        }
    }
    [Fact]
    public async Task Two_packages_and_two_instances_have_independent_success_keys_and_skip_only_their_own_successes()
    {
        await using var fixture = await FoundationStore.CreateAsync();
        using var blobs = new ContentBlobStore(fixture.Directory, fixture.Worker);
        var store = new SeedPackStore(fixture.Worker, blobs, fixture.Reader, fixture.Clock);
        var importer = new SeedPackImporter(fixture.Reader, blobs, store);
        var first = await importer.ImportAsync(PackFixture.Create(fixture).Path, TestContext.Current.CancellationToken);
        var second = await importer.ImportAsync(PackFixture.Create(fixture, version: "2.0.0").Path, TestContext.Current.CancellationToken);
        IsolatedEnvironment environment = fixture.Example();
        var duplicate = environment.Components[0] with { Id = Guid.NewGuid(), Ports = [.. environment.Components[0].Ports.Select(p => p with { HostPort = null })] };
        environment = environment with { Components = [.. environment.Components, duplicate], Baseline = new([.. environment.Baseline.Applications, environment.Baseline.Applications[0] with { ComponentId = duplicate.Id }]) };
        var selection = new BaselineSelectionService(store);
        foreach (var pair in new[] { (environment.Components[0].Id, first), (environment.Components[0].Id, second), (duplicate.Id, first) })
        { environment = await selection.SelectAsync(environment, pair.Id, pair.Item2, "initializers/requests.json", TestContext.Current.CancellationToken); }
        Assert.Equal(5, environment.Baseline.Applications.Length);
        await fixture.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken);
        var journal = new DataApplicationStore(fixture.Worker);
        var transport = new Responses();
        var session = await Session.CreateAsync(fixture, environment, transport);
        var executor = new BaselineExecutor(journal, fixture.Clock);
        foreach (var component in environment.Components) { await executor.ExecuteAsync(environment, component, session, TestContext.Current.CancellationToken); }
        int sent = transport.Keys.Count;
        Assert.Equal(sent, transport.Keys.Distinct().Count()); Assert.Equal(5, sent);
        foreach (var component in environment.Components) { await executor.ExecuteAsync(environment, component, session, TestContext.Current.CancellationToken); }
        Assert.Equal(sent, transport.Keys.Count);
        Assert.Equal(1, Assert.Single(await fixture.Environments.ListAsync(TestContext.Current.CancellationToken)).Revision);
    }

    [Theory]
    [InlineData(false, InitializationDecision.Repeat)]
    [InlineData(false, InitializationDecision.AcceptVerified)]
    [InlineData(true, InitializationDecision.Repeat)]
    public async Task Lost_response_requires_explicit_unsafe_decision_and_preserves_the_full_key(bool safe, InitializationDecision decision)
    {
        await using var fixture = await FoundationStore.CreateAsync();
        var environment = fixture.Example();
        var original = environment.Baseline.Applications[0];
        var initializer = original.Initializer with { Steps = [original.Initializer.Steps[0] with { Retry = safe ? original.Initializer.Steps[0].Retry : new("none", 1) }] };
        environment = environment with { Components = [environment.Components[0] with { DefinitionSnapshot = environment.Components[0].DefinitionSnapshot with { Initialization = initializer } }], Baseline = new([original with { Initializer = initializer }]) };
        await fixture.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken);
        var journal = new DataApplicationStore(fixture.Worker);
        var transport = new Responses { LoseFirst = true };
        var session = await Session.CreateAsync(fixture, environment, transport);
        var executor = new BaselineExecutor(journal, fixture.Clock);
        if (safe)
        {
            await executor.ExecuteAsync(environment, environment.Components[0], session, TestContext.Current.CancellationToken);
            Assert.Equal(2, transport.Keys.Count); Assert.Single(transport.Keys.Distinct()); return;
        }
        await Assert.ThrowsAsync<UnresolvedEffectException>(() => executor.ExecuteAsync(environment, environment.Components[0], session, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnresolvedEffectException>(() => executor.ExecuteAsync(environment, environment.Components[0], session, TestContext.Current.CancellationToken));
        Assert.Single(transport.Keys);
        var failedStart = new Operation(Guid.NewGuid(), environment.Id, "start", OperationState.NeedsAttention, fixture.Clock.UtcNow, 1, CanonicalJson.Hash(environment), session.Plan);
        await fixture.Operations.SaveAsync(failedStart, TestContext.Current.CancellationToken);
        await using var coordinator = new OperationCoordinator(fixture.Operations, fixture.Clock, () => true);
        await coordinator.InitializeAsync(TestContext.Current.CancellationToken);
        var observations = new Observations();
        var service = new ResolveUnknownInitializationService(journal, observations, coordinator, fixture.Clock);
        var unknown = Assert.Single(await service.ListAsync(environment.Id, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => service.Resolve(environment, unknown, InitializationDecision.AcceptVerified, false));
        OperationHandle resolved = service.Resolve(environment, unknown, decision, true); await resolved.Completion;
        Assert.Equal(OperationState.Succeeded, resolved.Current.State); Assert.Equal(1, observations.Reads);
        Assert.Null(coordinator.AdmissionBlockReason(environment.Id)); Assert.Single(transport.Keys);
        await executor.ExecuteAsync(environment, environment.Components[0], session, TestContext.Current.CancellationToken);
        Assert.Equal(decision == InitializationDecision.Repeat ? 2 : 1, transport.Keys.Count);
        Assert.Single(transport.Keys.Distinct()); Assert.Empty(await service.ListAsync(environment.Id, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<Exception>(async () => { var stale = service.Resolve(environment, unknown, decision, true); await stale.Completion; if (stale.Current.State != OperationState.Succeeded) { throw new InvalidOperationException(); } });
    }

    [Theory]
    [InlineData("unknown-observation")]
    [InlineData("decision-commit")]
    [InlineData("decision-acknowledgement")]
    [InlineData("generation")]
    public async Task Unverified_stale_or_failed_durable_decisions_do_not_authorize_replay(string fault)
    {
        bool fail = false;
        await using var fixture = await FoundationStore.CreateAsync(point => { if (fail && point == (fault == "decision-acknowledgement" ? "initialization-decision:afterCommit" : "initialization-decision:beforeCommit")) { throw new IOException("injected sentinel-secret"); } });
        var environment = fixture.Example(); await fixture.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken);
        var journal = new DataApplicationStore(fixture.Worker); var session = await Session.CreateAsync(fixture, environment, new Responses());
        var application = environment.Baseline.Applications[0];
        var key = new DataApplicationKey(environment.Id, application.ComponentId, session.Plan.DataGenerationId, session.Plan.BaselineHash, CanonicalJson.Hash(application), CanonicalJson.Hash(application.Initializer), application.Initializer.Steps[0].Id);
        var record = new DataApplicationRecord(key, StepState.Pending, 0, null, null, null, null, null);
        await journal.SaveAsync(record, TestContext.Current.CancellationToken);
        record = record with { State = StepState.Started, Attempt = 1 }; await journal.SaveAsync(record, TestContext.Current.CancellationToken);
        record = record with { State = StepState.Unknown }; await journal.SaveAsync(record, TestContext.Current.CancellationToken);
        await using var coordinator = new OperationCoordinator(fixture.Operations, fixture.Clock, () => true);
        var service = new ResolveUnknownInitializationService(journal, new Observations { Unknown = fault == "unknown-observation" }, coordinator, fixture.Clock);
        if (fault == "generation") { await journal.InvalidateAsync(key.DataGenerationId, Guid.NewGuid(), "test", fixture.Clock.UtcNow, TestContext.Current.CancellationToken); }
        fail = fault is "decision-commit" or "decision-acknowledgement";
        var operation = service.Resolve(environment, record, InitializationDecision.Repeat, false); await operation.Completion;
        Assert.Equal(fault == "decision-acknowledgement", operation.Current.State == OperationState.Succeeded);
        Assert.Equal(fault == "decision-acknowledgement", await journal.IsReplayAuthorizedAsync(key, 1, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("sentinel", operation.Current.ResultCode ?? "");
    }

    private sealed class Responses : IInitializationTransport
    {
        public List<string> Keys { get; } = [];
        public bool LoseFirst { get; init; }
        public Task<InitializationResponse> SendAsync(Guid component, InitializationStep step, string key, Stream? body, CancellationToken token)
        { Keys.Add(key); return Task.FromResult(LoseFirst && Keys.Count == 1 ? new InitializationResponse(null, false, false, "http-transport-unavailable") : new(step.Expect.Statuses[0], true, true, null)); }
    }
    private sealed class Observations : IEnvironmentObservationSource
    {
        public bool Unknown { get; init; }
        public int Reads { get; private set; }
        public Task<EnvironmentObservation> ReadAsync(IsolatedEnvironment environment, Guid session, EnvironmentObservation? previous, CancellationToken token)
        { Reads++; return Task.FromResult(new EnvironmentObservation(environment.Id, session, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Unknown ? "unknown" : "current", [.. environment.Components.Select(c => new ComponentObservation(c.Id, "container", "running", "healthy", [], ImmutableDictionary<string, Measurement>.Empty))], [], null)); }
    }
    private sealed class Session(IsolatedEnvironment environment, Guid generation, IInitializationTransport transport, DataApplicationStore journal) : IStartSession
    {
        public OperationPlan Plan { get; } = new(Guid.NewGuid(), environment.Id, environment.Revision, CanonicalJson.Hash(environment), generation, CanonicalJson.Hash(environment.Baseline), [], [], [], "fixture");
        public PreviewSnapshot Snapshot => throw new NotSupportedException();
        public IInitializationTransport Transport => transport;
        public async Task ValidateDataGenerationAsync(CancellationToken token)
        { if (!await journal.IsActiveGenerationAsync(environment.Id, generation, Plan.BaselineHash, token)) { throw new PreflightBlockedException("data-generation-invalid"); } }
        public static async Task<Session> CreateAsync(FoundationStore fixture, IsolatedEnvironment environment, IInitializationTransport transport)
        {
            Guid generation = await fixture.Worker.ReadAsync(connection => { using var command = connection.CreateCommand(); command.CommandText = "SELECT generationId FROM Environments WHERE id=$id"; command.Parameters.AddWithValue("$id", environment.Id.ToString()); return Guid.Parse((string)command.ExecuteScalar()!); }, TestContext.Current.CancellationToken);
            return new(environment, generation, transport, new(fixture.Worker));
        }
        public Task PrepareImagesAsync(OperationExecution execution, CancellationToken token) => throw new NotSupportedException();
        public Task<ComponentObservation> StartComponentAsync(EnvironmentComponent component, OperationExecution execution, CancellationToken token) => throw new NotSupportedException();
        public Task<ComponentObservation> WaitAsync(EnvironmentComponent component, string condition, CancellationToken token) => throw new NotSupportedException();
        public Task<Stream?> OpenBodyAsync(BaselineApplication application, InitializationStep step, CancellationToken token) => Task.FromResult<Stream?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
