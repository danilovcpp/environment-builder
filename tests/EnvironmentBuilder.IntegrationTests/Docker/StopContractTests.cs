using System.Collections.Immutable;
using System.Text.Json;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Infrastructure.Docker;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Docker;

public sealed class StopContractTests
{
    [Fact]
    public async Task Stop_uses_exact_owned_ids_reverse_dependencies_and_preserves_durable_data()
    {
        await using var store = await FoundationStore.CreateAsync();
        var draft = EnvironmentDraft.Open(store.Example());
        var definition = draft.Snapshot.Components[0].DefinitionSnapshot;
        draft.AddComponent(definition, [definition]); draft.AddComponent(definition, [definition]);
        var components = draft.Snapshot.Components.Select(c => c with { Ports = c.Ports.Select(p => p with { HostPort = null }).ToImmutableArray() }).ToImmutableArray();
        IsolatedEnvironment environment = draft.Snapshot with { Components = components, Dependencies = [new(components[2].Id, components[1].Id, null, "running", "test"), new(components[1].Id, components[0].Id, null, "running", "test")] };
        await store.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken);
        (Guid installation, Guid generation) = await store.Worker.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT (SELECT value FROM AppMetadata WHERE key='installationId'),generationId FROM Environments";
            using var row = command.ExecuteReader(); Assert.True(row.Read()); return (Guid.Parse(row.GetString(0)), Guid.Parse(row.GetString(1)));
        }, TestContext.Current.CancellationToken);
        var origin = await store.CreateOperationAsync(environment);
        var fake = new StopDockerFixture(environment, installation, generation, origin.Id, includeLeftover: true) { ExitCode = 137 };
        var ledger = new ResourceLedgerStore(store.Worker);
        foreach (var resource in fake.Resources) { await ledger.SaveAsync(resource, null, null, TestContext.Current.CancellationToken); }
        var applications = new DataApplicationStore(store.Worker);
        var application = environment.Baseline.Applications[0];
        var key = new DataApplicationKey(environment.Id, application.ComponentId, generation, CanonicalJson.Hash(environment.Baseline), CanonicalJson.Hash(application), CanonicalJson.Hash(application.Initializer), application.Initializer.Steps[0].Id);
        await applications.SaveAsync(new(key, StepState.Pending, 0, null, null, null, null, null), TestContext.Current.CancellationToken);
        await applications.SaveAsync(new(key, StepState.Started, 1, store.Clock.UtcNow, null, null, null, null), TestContext.Current.CancellationToken);
        await applications.SaveAsync(new(key, StepState.Succeeded, 1, store.Clock.UtcNow, store.Clock.UtcNow, 204, true, null), TestContext.Current.CancellationToken);
        string ledgerBefore = CanonicalJson.Hash(await ledger.ReadAsync(environment.Id, TestContext.Current.CancellationToken));
        long attemptsBefore = await store.CountAsync("InitializationAttempts");
        var before = await store.CountAsync("DataGenerations");
        await using var coordinator = new OperationCoordinator(store.Operations, store.Clock, () => true);
        var service = new StopEnvironmentService(coordinator, fake, store.Clock);
        var stopped = service.Stop(environment, 17); await stopped.Completion;
        Assert.Equal(OperationState.Succeeded, stopped.Current.State);
        Assert.Equal(new[] { fake.Resources[2].ActualDockerId, fake.Resources[1].ActualDockerId, fake.Resources[0].ActualDockerId, fake.Resources[3].ActualDockerId }, fake.Stopped);
        Assert.Equal(17, fake.Timeout);
        Assert.All(fake.Mutations, args => Assert.Equal(new[] { "container", "stop", "--timeout", "17", args[^1] }, args));
        Assert.Equal(before, await store.CountAsync("DataGenerations"));
        Assert.Equal(CanonicalJson.Hash(environment), CanonicalJson.Hash((await store.Environments.ListAsync(TestContext.Current.CancellationToken)).Single().Document!));
        Assert.Equal(ledgerBefore, CanonicalJson.Hash(await ledger.ReadAsync(environment.Id, TestContext.Current.CancellationToken)));
        Assert.Equal(attemptsBefore, await store.CountAsync("InitializationAttempts")); Assert.True(await applications.HasSuccessAsync(key, TestContext.Current.CancellationToken));
        await store.Worker.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT resultJson FROM OperationSteps WHERE operationId=$operation"; command.Parameters.AddWithValue("$operation", stopped.Current.Id.ToString());
            using var rows = command.ExecuteReader(); int count = 0;
            while (rows.Read()) { Assert.Equal(137, JsonSerializer.Deserialize<EffectObservation>(rows.GetString(0), CanonicalJson.Options)!.ContainerExitCode); count++; }
            Assert.Equal(4, count); return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("unknown")]
    [InlineData("leftover")]
    [InlineData("copied-labels")]
    [InlineData("engine-drift")]
    [InlineData("timeout")]
    [InlineData("unledger")]
    public async Task Incomplete_or_conflicting_post_inspection_never_confirms_stop(string fault)
    {
        await using var store = await FoundationStore.CreateAsync();
        var environment = store.Example(); await store.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken);
        var fake = new StopDockerFixture(environment) { Fault = fault };
        await using var coordinator = new OperationCoordinator(store.Operations, store.Clock, () => true);
        var handle = new StopEnvironmentService(coordinator, fake, store.Clock).Stop(environment); await handle.Completion;
        Assert.NotEqual(OperationState.Succeeded, handle.Current.State);
        Assert.DoesNotContain(fake.Mutations, args => args.Contains("rm") || args.Contains("remove") || args.Contains("kill"));
    }

    [Fact]
    public async Task Intent_failure_prevents_stop_and_result_failure_stops_scheduling()
    {
        foreach (string boundary in new[] { "intent:beforeCommit", "result:beforeCommit" })
        {
            bool armed = false;
            await using var store = await FoundationStore.CreateAsync(point => { if (armed && point == boundary) { throw new IOException("injected"); } });
            var environment = store.Example(); await store.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken);
            var fake = new StopDockerFixture(environment); armed = true;
            await using var coordinator = new OperationCoordinator(store.Operations, store.Clock, () => true);
            var handle = new StopEnvironmentService(coordinator, fake, store.Clock).Stop(environment); await handle.Completion;
            Assert.Equal(OperationState.NeedsAttention, handle.Current.State);
            Assert.Equal(boundary.StartsWith("intent", StringComparison.Ordinal) ? 0 : 1, fake.Stopped.Count);
        }
    }

    internal sealed class StopDockerFixture : EnvironmentBuilder.Core.Abstractions.IStopRuntime
    {
        private readonly IsolatedEnvironment environment;
        private int reads;
        public ImmutableArray<OwnedResource> Resources { get; }
        public List<string> Stopped { get; } = [];
        public List<IReadOnlyList<string>> Mutations { get; } = [];
        public string? Fault { get; init; }
        public int Timeout { get; private set; }
        public int ExitCode { get; init; }
        public StopDockerFixture(IsolatedEnvironment environment, Guid? installation = null, Guid? generation = null, Guid? origin = null, bool includeLeftover = false)
        {
            this.environment = environment;
            Resources = environment.Components.Select((c, i) => new OwnedResource(Guid.NewGuid(), installation ?? Guid.NewGuid(), environment.Id, c.Id, ResourceKind.Container, 1,
                generation ?? Guid.NewGuid(), "container-" + i, ImmutableDictionary<string, string>.Empty, new string((char)('a' + i), 64), null, [], true, origin ?? Guid.NewGuid(), false)).ToImmutableArray();
            if (includeLeftover) { Resources = Resources.Add(Resources[0] with { ResourceId = Guid.NewGuid(), ComponentId = Guid.NewGuid(), Name = "owned-leftover", ActualDockerId = new string('d', 64) }); }
        }
        public Task<EnvironmentBuilder.Core.Abstractions.IStopSession> PrepareAsync(IsolatedEnvironment document, Guid operation, int timeoutSeconds, CancellationToken token)
        {
            Timeout = timeoutSeconds;
            var plan = new OperationPlan(Guid.NewGuid(), document.Id, document.Revision, CanonicalJson.Hash(document), Guid.NewGuid(), CanonicalJson.Hash(document.Baseline), Resources, [], ["stop"], "Stop preserves data");
            var docker = new ObservationContractTests.FakeDocker(args =>
            {
                if (args.Take(2).SequenceEqual(new[] { "container", "stop" }))
                {
                    Mutations.Add(args); Stopped.Add(args[^1]);
                    if (Fault == "timeout") { throw new TimeoutException("client timeout after dispatch"); }
                    return Task.FromResult(new DockerResult(0, args[^1], ""));
                }
                if (args.Contains("ls")) { reads++; return Task.FromResult(new DockerResult(0, string.Join('\n', Resources.Select(r => r.ActualDockerId).Concat(Fault == "unledger" ? [new string('f', 64)] : [])), "")); }
                if (Fault == "disconnect" && Stopped.Count > 0) { throw new IOException("disconnected"); }
                var containers = Resources.Select(r => new
                {
                    Id = r.ActualDockerId,
                    Name = "/" + r.Name,
                    Config = new { Labels = ResourceOwnershipVerifier.Labels(r) },
                    State = new { Status = Stopped.Contains(r.ActualDockerId!) ? Fault == "unknown" ? "unknown" : "exited" : "running", ExitCode, OOMKilled = false },
                    Mounts = Array.Empty<object>(),
                    NetworkSettings = new { Ports = new Dictionary<string, object>(), Networks = new Dictionary<string, object>() }
                }).ToList();
                if (Fault == "copied-labels") { containers[0] = containers[0] with { Id = new string('f', 64) }; }
                if (Fault == "leftover" && reads > 1 && Stopped.Count == Resources.Length) { containers[0] = containers[0] with { State = new { Status = "restarting", ExitCode, OOMKilled = false } }; }
                if (Fault == "unledger") { containers.Add(containers[0] with { Id = new string('f', 64), Name = "/unledger" }); }
                return Task.FromResult(new DockerResult(0, JsonSerializer.Serialize(containers), ""));
            });
            return Task.FromResult<EnvironmentBuilder.Core.Abstractions.IStopSession>(new DockerStopSession(environment, operation, plan, timeoutSeconds, docker,
                _ => Fault == "engine-drift" && Stopped.Count > 0 ? Task.FromException(new IOException("engine changed")) : Task.CompletedTask));
        }
    }
}
