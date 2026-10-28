using System.Collections.Immutable;
using System.Text.Json;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Infrastructure.Docker;
using EnvironmentBuilder.Infrastructure.Persistence;

namespace EnvironmentBuilder.IntegrationTests.Support;

// Stateful Engine double: mutations change inspect/list responses, including stopped consumers.
internal sealed class EditFixture : ICleanupRuntime, IAsyncDisposable
{
    private OperationCoordinator coordinator = null!;
    private OperationPlan scope = null!;
    private readonly Guid foreignEnvironment = Guid.NewGuid();
    public FoundationStore Store { get; private set; } = null!;
    public IsolatedEnvironment Environment { get; private set; } = null!;
    public ImmutableArray<OwnedResource> Resources { get; private set; } = [];
    public ResourceLedgerStore Ledger => new(Store.Worker);
    public EditEnvironmentService Service { get; private set; } = null!;
    public HashSet<Guid> Absent { get; } = [];
    public List<IReadOnlyList<string>> Mutations { get; } = [];
    public string? Fault { get; set; }
    public string? AfterConfirmationFault { get; set; }
    public string? FaultBoundary { get; set; }
    public int Preparations { get; private set; }
    public int Confirmations { get; private set; }
    public bool ForeignIntact { get; private set; } = true;
    public Func<Task>? BeforeRemoval { get; set; }
    public Action? AfterRemoval { get; set; }
    public Action? DuringConfirmation { get; set; }
    private static string ContainerId => new('a', 64);
    private static string ForeignId => new('f', 64);
    private static string SecondId => new('b', 64);

    public static async Task<EditFixture> CreateAsync(bool empty = false)
    {
        var fixture = new EditFixture();
        fixture.Store = await FoundationStore.CreateAsync(point => { if (fixture.FaultBoundary == point) { throw new IOException("injected " + point); } });
        fixture.Environment = fixture.Store.Example();
        await fixture.Store.Environments.SaveNewAsync(fixture.Environment, TestContext.Current.CancellationToken);
        Guid generation = await fixture.GenerationAsync();
        Guid installation = await fixture.Store.Worker.ReadAsync(connection =>
        { using var command = connection.CreateCommand(); command.CommandText = "SELECT value FROM AppMetadata WHERE key='installationId'"; return Guid.Parse((string)command.ExecuteScalar()!); }, TestContext.Current.CancellationToken);
        Operation origin = await fixture.Store.CreateOperationAsync(fixture.Environment);
        await fixture.Store.Operations.SaveAsync(origin with { State = OperationState.Succeeded }, TestContext.Current.CancellationToken);
        var attributes = ImmutableDictionary<string, string>.Empty.Add("engineId", "engine-one");
        OwnedResource Resource(ResourceKind kind, string name, string identity) => new(Guid.NewGuid(), installation, fixture.Environment.Id,
            kind == ResourceKind.Network ? null : fixture.Environment.Components[0].Id, kind, 1, generation, name, attributes, identity,
            kind == ResourceKind.Volume ? new("2026-10-28T00:00:00Z", "local", "local", ImmutableDictionary<string, string>.Empty) : null, [], true, origin.Id, false);
        if (!empty)
        {
            fixture.Resources = [Resource(ResourceKind.Container, "selected-container", ContainerId), Resource(ResourceKind.Volume, "selected-data", "selected-data"), Resource(ResourceKind.Network, "selected-network", new string('c', 64))];
            foreach (OwnedResource resource in fixture.Resources) { await fixture.Ledger.SaveAsync(resource, "created", null, TestContext.Current.CancellationToken); }
        }
        fixture.scope = new(Guid.NewGuid(), fixture.Environment.Id, 1, CanonicalJson.Hash(fixture.Environment), generation, CanonicalJson.Hash(fixture.Environment.Baseline), fixture.Resources, [], [], "test") { EngineId = "engine-one", Endpoint = "npipe://local-engine" };
        fixture.coordinator = new(fixture.Store.Operations, fixture.Store.Clock, () => true);
        var persistence = new EditFinalization(fixture.Store.Worker, fixture.Store.Clock);
        fixture.Service = new(fixture.Store.Environments, fixture, persistence, new(persistence), fixture.coordinator, fixture.Store.Clock);
        return fixture;
    }

    public Task<EditEnvironmentResult> SaveAsync(IsolatedEnvironment candidate) => SaveAsync(candidate, TestContext.Current.CancellationToken);
    public Task<EditEnvironmentResult> SaveAsync(IsolatedEnvironment candidate, CancellationToken token) => Service.SaveAsync(Environment, candidate, ConfirmAsync, token);

    private async Task<ConfirmationRecord?> ConfirmAsync(OperationPlan plan, CancellationToken token)
    {
        Confirmations++;
        DuringConfirmation?.Invoke();
        if (AfterConfirmationFault is not null) { Fault = AfterConfirmationFault; }
        if (Fault == "revision-drift")
        {
            await Store.Worker.CommitAsync("external-edit", (connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE Environments SET revision=revision+1 WHERE id=$id"; command.Parameters.AddWithValue("$id", Environment.Id.ToString()); command.ExecuteNonQuery(); return true;
            }, token);
        }
        return Fault == "cancel" ? null : new(Fault == "stale-token" ? "obsolete" : plan.Hash, Store.Clock.UtcNow);
    }

    public Task<ICleanupSession> PrepareAsync(IsolatedEnvironment environment, Guid operationId, CancellationToken token)
    {
        Preparations++;
        var docker = new FakeDocker(RunAsync);
        Task Revalidate(CancellationToken safe)
        {
            safe.ThrowIfCancellationRequested();
            if (Fault is "engine-drift" or "ledger-drift" or "revision-drift") { throw new PreflightBlockedException("stale-plan"); }
            return Task.CompletedTask;
        }
        return Task.FromResult<ICleanupSession>(new DockerCleanupSession(environment, operationId, scope with { Id = operationId }, docker, Revalidate));
    }

    private async Task<DockerResult> RunAsync(IReadOnlyList<string> args)
    {
        if (args[1] == "rm")
        {
            if (BeforeRemoval is not null) { await BeforeRemoval(); }
            Mutations.Add(args);
            OwnedResource? target = Resources.SingleOrDefault(r => r.ActualDockerId == args[^1]);
            if (target is null) { ForeignIntact = false; throw new InvalidOperationException("Removal escaped exact scope."); }
            if (target.Kind == ResourceKind.Volume && Fault == "volume-in-use") { return new(1, "", "in use"); }
            if (Fault != "removal-still-present") { Absent.Add(target.ResourceId); }
            AfterRemoval?.Invoke(); return new(0, args[^1], "");
        }
        if (args[0] == "container")
        {
            if (args[1] == "ls") { return new(0, string.Join('\n', Containers().Select(c => c.GetProperty("Id").GetString())), ""); }
            return new(0, JsonSerializer.Serialize(Containers().Where(c => args.Skip(2).Contains(c.GetProperty("Id").GetString()))), "");
        }
        ResourceKind kind = args[0] == "volume" ? ResourceKind.Volume : ResourceKind.Network;
        OwnedResource? resource = Resources.SingleOrDefault(r => r.Kind == kind && !Absent.Contains(r.ResourceId));
        if (args[1] == "ls")
        {
            var names = resource is null ? new List<string>() : [resource.Name];
            if ((kind == ResourceKind.Volume && Fault == "unexpected-volume") || (kind == ResourceKind.Network && Fault == "unexpected-network")) { names.Add("unledgered"); }
            return new(0, string.Join('\n', names.Select(n => JsonSerializer.Serialize(n))), "");
        }
        if (resource is null) { return new(1, "", "absent"); }
        if (kind == ResourceKind.Volume)
        {
            var labels = ResourceOwnershipVerifier.Labels(resource);
            if (Fault == "copied-volume-labels") { labels = labels.SetItem("io.environmentbuilder.resource", Guid.NewGuid().ToString()); }
            return new(0, JsonSerializer.Serialize(new[] { new
            {
                Name = resource.Name, Labels = labels,
                CreatedAt = Fault == "volume-evidence-missing" ? null : Fault == "volume-created" ? "2026-10-28T01:00:00Z" : resource.Volume!.CreatedAt,
                Driver = Fault == "volume-driver" ? "other" : "local", Scope = Fault == "volume-scope" ? "global" : "local",
                Options = Fault == "volume-options" ? new Dictionary<string, string> { ["device"] = "/foreign" } : []
            } }), "");
        }
        return new(0, JsonSerializer.Serialize(new[] { new
        {
            Name = resource.Name, Id = resource.ActualDockerId, Labels = ResourceOwnershipVerifier.Labels(resource),
            Driver = Fault == "network-driver" ? "overlay" : "bridge", Scope = Fault == "network-scope" ? "swarm" : "local",
            Containers = Fault == "foreign-network-attachment" ? new Dictionary<string, object> { [ForeignId] = new { } } : []
        } }), "");
    }

    private List<JsonElement> Containers()
    {
        var all = new List<JsonElement>();
        object Container(string id, string name, ImmutableDictionary<string, string> labels, bool mounted, string state) => new
        {
            Id = id,
            Name = "/" + name,
            Config = new { Labels = labels },
            State = new { Status = state, ExitCode = 0, OOMKilled = false },
            Mounts = mounted ? new[] { new { Type = "volume", Name = "selected-data" } } : [],
            NetworkSettings = new { Ports = new Dictionary<string, object>(), Networks = mounted ? new Dictionary<string, object> { ["selected-network"] = new { NetworkID = new string('c', 64) } } : [] }
        };
        foreach (OwnedResource container in Resources.Where(r => r.Kind == ResourceKind.Container && !Absent.Contains(r.ResourceId)))
        {
            all.Add(JsonSerializer.SerializeToElement(Container(Fault == "copied-container-labels" ? new string('d', 64) : container.ActualDockerId!, container.Name, ResourceOwnershipVerifier.Labels(container), true, Fault is "running" or "state-drift" ? "running" : "exited")));
        }
        all.Add(JsonSerializer.SerializeToElement(Container(ForeignId, "selected-container-lookalike", ImmutableDictionary<string, string>.Empty, Fault is "foreign-stopped-consumer" or "consumer-drift", "exited")));
        all.Add(JsonSerializer.SerializeToElement(Container(SecondId, "second-copy", ImmutableDictionary<string, string>.Empty.Add("io.environmentbuilder.environment", foreignEnvironment.ToString()), false, "running")));
        if (Fault == "unexpected-owned") { all.Add(JsonSerializer.SerializeToElement(Container(new string('e', 64), "unledgered", ImmutableDictionary<string, string>.Empty.Add("io.environmentbuilder.environment", Environment.Id.ToString()), false, "exited"))); }
        return all;
    }

    public Task<Guid> GenerationAsync() => Store.Worker.ReadAsync(connection =>
    { using var command = connection.CreateCommand(); command.CommandText = "SELECT generationId FROM Environments WHERE id=$id"; command.Parameters.AddWithValue("$id", Environment.Id.ToString()); return Guid.Parse((string)command.ExecuteScalar()!); }, TestContext.Current.CancellationToken);
    public Task<string> ValidityAsync(Guid generation) => Store.Worker.ReadAsync(connection =>
    { using var command = connection.CreateCommand(); command.CommandText = "SELECT validity FROM DataGenerations WHERE id=$id"; command.Parameters.AddWithValue("$id", generation.ToString()); return (string)command.ExecuteScalar()!; }, TestContext.Current.CancellationToken);
    public async Task<IsolatedEnvironment> SavedAsync() => (await Store.Environments.ListAsync(TestContext.Current.CancellationToken)).Single().Document!;
    public async ValueTask DisposeAsync() { await coordinator.DisposeAsync(); await Store.DisposeAsync(); }
    private sealed class FakeDocker(Func<IReadOnlyList<string>, Task<DockerResult>> run) : IDockerProcess
    {
        public string Endpoint => "npipe://local-engine";
        public Task<DockerResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, Func<string, bool, Task>? output = null) { cancellationToken.ThrowIfCancellationRequested(); return run(arguments); }
    }
}
