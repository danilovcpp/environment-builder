using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Initialization;

public sealed class BaselinePersistenceTests
{
    [Fact]
    public async Task An_unsent_pending_step_can_resume_without_replaying_an_ambiguous_dispatch()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        var environment = fixture.Example(); await fixture.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken);
        var journal = new DataApplicationStore(fixture.Worker);
        var responses = new Responses([new(204, true, true, null)]);
        var session = await Session.CreateAsync(fixture, environment, responses);
        var application = environment.Baseline.Applications[0];
        var key = new DataApplicationKey(environment.Id, application.ComponentId, session.Plan.DataGenerationId, session.Plan.BaselineHash, CanonicalJson.Hash(application), CanonicalJson.Hash(application.Initializer), application.Initializer.Steps[0].Id);
        await journal.SaveAsync(new(key, StepState.Pending, 0, null, null, null, null, null), TestContext.Current.CancellationToken);
        await new BaselineExecutor(journal, fixture.Clock).ExecuteAsync(environment, environment.Components[0], session, TestContext.Current.CancellationToken);
        Assert.Single(responses.Keys); Assert.Equal(StepState.Succeeded, (await journal.ReadAsync(key, TestContext.Current.CancellationToken))!.State);
    }
    [Fact]
    public async Task Safe_transient_retry_keeps_the_same_key_and_both_attempts_before_durable_success()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        var environment = fixture.Example(); await fixture.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken);
        var journal = new DataApplicationStore(fixture.Worker);
        var responses = new Responses([new(503, false, true, "status-or-assertion-mismatch") { RetryAfter = TimeSpan.Zero }, new(204, true, true, null)]);
        var session = await Session.CreateAsync(fixture, environment, responses);
        var executor = new BaselineExecutor(journal, fixture.Clock);
        await executor.ExecuteAsync(environment, environment.Components[0], session, TestContext.Current.CancellationToken);
        Assert.Equal(2, responses.Keys.Count); Assert.Single(responses.Keys.Distinct());
        Assert.Equal(5, await fixture.CountAsync("InitializationAttempts"));
        await executor.ExecuteAsync(environment, environment.Components[0], session, TestContext.Current.CancellationToken);
        Assert.Equal(2, responses.Keys.Count);
    }

    [Fact]
    public async Task Unsafe_lost_response_is_unknown_and_no_ordinary_start_replays_it()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        IsolatedEnvironment environment = fixture.Example();
        var application = environment.Baseline.Applications[0];
        var initializer = application.Initializer with { Steps = [application.Initializer.Steps[0] with { Retry = new("none", 1) }] };
        EnvironmentComponent component = environment.Components[0] with { DefinitionSnapshot = environment.Components[0].DefinitionSnapshot with { Initialization = initializer } };
        environment = environment with { Components = [component], Baseline = new([application with { Initializer = initializer }]) };
        await fixture.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken);
        var responses = new Responses([new(null, false, false, "http-transport-unavailable")]);
        var session = await Session.CreateAsync(fixture, environment, responses);
        var executor = new BaselineExecutor(new DataApplicationStore(fixture.Worker), fixture.Clock);
        await Assert.ThrowsAsync<UnresolvedEffectException>(() => executor.ExecuteAsync(environment, component, session, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnresolvedEffectException>(() => executor.ExecuteAsync(environment, component, session, TestContext.Current.CancellationToken));
        Assert.Single(responses.Keys);
    }

    [Fact]
    public async Task Migration_from_v1_preserves_success_markers_without_attempt_history()
    {
        using IsolatedTestStore isolated = IsolatedTestStore.Create();
        var directory = new AppDataDirectory(isolated.DataDirectory);
        await using SingleWriterLease lease = await SingleWriterLease.AcquireAsync(directory);
        var worker = new SqliteWorker(directory, lease); await worker.Ready;
        var reader = new Infrastructure.Imports.StrictDocumentReader();
        IsolatedEnvironment environment = reader.Read<IsolatedEnvironment>(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Contracts/examples/environment.json"), TestContext.Current.CancellationToken));
        await new EnvironmentStore(worker, reader, new SystemClock()).SaveNewAsync(environment, TestContext.Current.CancellationToken);
        Guid generation = await worker.ReadAsync(connection =>
        { using var command = connection.CreateCommand(); command.CommandText = "SELECT id FROM DataGenerations"; return Guid.Parse((string)command.ExecuteScalar()!); }, TestContext.Current.CancellationToken);
        var application = environment.Baseline.Applications[0]; var step = application.Initializer.Steps[0];
        var key = new DataApplicationKey(environment.Id, application.ComponentId, generation, CanonicalJson.Hash(environment.Baseline), CanonicalJson.Hash(application), CanonicalJson.Hash(application.Initializer), step.Id);
        var journal = new DataApplicationStore(worker);
        var record = new DataApplicationRecord(key, StepState.Pending, 0, null, null, null, null, null);
        await journal.SaveAsync(record, TestContext.Current.CancellationToken);
        await journal.SaveAsync(record with { State = StepState.Started, Attempt = 1, StartedAt = DateTimeOffset.UtcNow }, TestContext.Current.CancellationToken);
        await journal.SaveAsync(record with { State = StepState.Succeeded, Attempt = 1, CompletedAt = DateTimeOffset.UtcNow, AcceptedStatus = 204, AssertionsPassed = true }, TestContext.Current.CancellationToken);
        // Reconstruct the previous database schema in this isolated fixture, leaving authoritative records intact.
        await worker.CommitAsync("v1-fixture", (connection, transaction) =>
        { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "DROP TABLE InitializationAttempts; DROP TABLE ComponentLibrarySelections; UPDATE AppMetadata SET value='1' WHERE key='databaseSchemaVersion'; PRAGMA user_version=1"; return command.ExecuteNonQuery(); }, TestContext.Current.CancellationToken);
        await worker.DisposeAsync();
        await using var reopened = new SqliteWorker(directory, lease); await reopened.Ready;
        Assert.Equal(StepState.Succeeded, (await new DataApplicationStore(reopened).ReadAsync(key, TestContext.Current.CancellationToken))!.State);
        Assert.Equal(3L, await reopened.ReadAsync(connection =>
        { using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version"; return (long)command.ExecuteScalar()!; }, TestContext.Current.CancellationToken));
    }

    private sealed class Responses(IEnumerable<InitializationResponse> responses) : IInitializationTransport
    {
        private readonly Queue<InitializationResponse> results = new(responses);
        public List<string> Keys { get; } = [];
        public Task<InitializationResponse> SendAsync(Guid component, InitializationStep step, string key, Stream? body, CancellationToken token)
        { Keys.Add(key); return Task.FromResult(results.Dequeue()); }
    }
    private sealed class Session(IsolatedEnvironment environment, Guid generation, IInitializationTransport transport) : IStartSession
    {
        public Task ValidateDataGenerationAsync(CancellationToken token) => Task.CompletedTask;
        public OperationPlan Plan { get; } = new(Guid.NewGuid(), environment.Id, 1, CanonicalJson.Hash(environment), generation, CanonicalJson.Hash(environment.Baseline), [], [], [], "fixture");
        public PreviewSnapshot Snapshot => throw new NotSupportedException();
        public IInitializationTransport Transport => transport;
        public static async Task<Session> CreateAsync(FoundationStore fixture, IsolatedEnvironment environment, IInitializationTransport transport)
        {
            Guid generation = await fixture.Worker.ReadAsync(connection =>
            { using var command = connection.CreateCommand(); command.CommandText = "SELECT id FROM DataGenerations"; return Guid.Parse((string)command.ExecuteScalar()!); }, TestContext.Current.CancellationToken);
            return new(environment, generation, transport);
        }
        public Task PrepareImagesAsync(OperationExecution execution, CancellationToken token) => throw new NotSupportedException();
        public Task<ComponentObservation> StartComponentAsync(EnvironmentComponent component, OperationExecution execution, CancellationToken token) => throw new NotSupportedException();
        public Task<ComponentObservation> WaitAsync(EnvironmentComponent component, string condition, CancellationToken token) => throw new NotSupportedException();
        public Task<Stream?> OpenBodyAsync(BaselineApplication application, InitializationStep step, CancellationToken token) => Task.FromResult<Stream?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
