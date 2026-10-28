using System.Collections.Immutable;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Infrastructure.Persistence;

namespace EnvironmentBuilder.Infrastructure.Docker;

public sealed class DockerRemovalAdapter(AppDataDirectory directory, IEnvironmentStore environments, ResourceLedgerStore ledger, DockerProcessFactory processes, SqliteWorker worker) : ICleanupRuntime
{
    public async Task<ICleanupSession> PrepareAsync(IsolatedEnvironment environment, Guid operationId, CancellationToken token)
    {
        IDockerProcess docker = processes.Create(environment);
        var probe = new DockerCapabilityProbe(docker, directory);
        DockerCapabilities capability = await probe.ProbeAsync(token).ConfigureAwait(false);
        IReadOnlyList<OwnedResource> resources = await ledger.ReadAsync(environment.Id, token).ConfigureAwait(false);
        Guid generation = await worker.ReadAsync(connection =>
        {
            using var command = Sql.Command(connection, null, "SELECT generationId FROM Environments WHERE id=$id", ("$id", environment.Id.ToString()));
            return Guid.Parse((string)command.ExecuteScalar()!);
        }, token).ConfigureAwait(false);
        var scope = new OperationPlan(operationId, environment.Id, environment.Revision, CanonicalJson.Hash(environment), generation,
            CanonicalJson.Hash(environment.Baseline), resources.ToImmutableArray(), [], [], "Удаление проверенных ресурсов.")
        { EngineId = capability.EngineId, Endpoint = capability.Endpoint };
        async Task Revalidate(CancellationToken safe)
        {
            await probe.RequireEngineAsync(capability, safe).ConfigureAwait(false);
            var saved = (await environments.ListAsync(safe).ConfigureAwait(false)).Single(e => e.Id == environment.Id);
            if (saved.Document is null || CanonicalJson.Hash(saved.Document) != scope.ConfigurationHash) { throw new PreflightBlockedException("saved-configuration-changed"); }
            var current = await ledger.ReadAsync(environment.Id, safe).ConfigureAwait(false);
            if (CanonicalJson.Hash(current.OrderBy(r => r.ResourceId)) != CanonicalJson.Hash(resources.OrderBy(r => r.ResourceId)) ||
                current.Any(r => !r.DesiredAttributes.TryGetValue("engineId", out string? engine) || engine != capability.EngineId))
            { throw new PreflightBlockedException("resource-authority-changed"); }
            Guid currentGeneration = await worker.ReadAsync(connection =>
            { using var command = Sql.Command(connection, null, "SELECT generationId FROM Environments WHERE id=$id", ("$id", environment.Id.ToString())); return Guid.Parse((string)command.ExecuteScalar()!); }, safe).ConfigureAwait(false);
            if (currentGeneration != generation) { throw new PreflightBlockedException("data-generation-changed"); }
        }
        await Revalidate(token).ConfigureAwait(false);
        return new DockerCleanupSession(environment, operationId, scope, docker, Revalidate);
    }
}

public sealed class DockerCleanupSession(IsolatedEnvironment environment, Guid operationId, OperationPlan scope,
    IDockerProcess docker, Func<CancellationToken, Task> revalidate) : ICleanupSession
{
    private readonly DockerStopSession observation = new(environment, operationId, scope, 30, docker, revalidate);
    public OperationPlan Scope => scope;
    public Task RevalidateAsync(CancellationToken token) => revalidate(token);

    public async Task<EnvironmentObservation> ReadAsync(CancellationToken token)
    {
        EnvironmentObservation actual = await observation.ReadAsync(token).ConfigureAwait(false);
        if (actual.Resources.Any(r => r.Actual is { Kind: ResourceKind.Network } network && (network.NetworkDriver != "bridge" || network.NetworkScope != "local")))
        { return actual with { Reason = "private-network-unverified" }; }
        foreach (ResourceKind kind in new[] { ResourceKind.Volume, ResourceKind.Network })
        {
            DockerResult listed = await docker.RunAsync([kind.ToString().ToLowerInvariant(), "ls", "--filter", "label=io.environmentbuilder.environment=" + environment.Id, "--format", "{{json .Name}}"], token).ConfigureAwait(false);
            ResourceConsumerReader.Require(listed);
            string[] names = listed.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => System.Text.Json.JsonSerializer.Deserialize<string>(line) ?? throw new IOException("Unknown resource identity.")).ToArray();
            if (names.Any(name => !scope.Resources.Any(r => r.Kind == kind && r.Name == name))) { return actual with { Reason = "unexpected-owned-resource" }; }
        }
        return actual;
    }

    public async Task RemoveAsync(OperationPlan plan, ImmutableArray<string> removed, OwnedResource resource, OperationExecution execution, CancellationToken token)
    {
        EnvironmentObservation before = await ReadAsync(token).ConfigureAwait(false);
        DestructivePlanBuilder.RequireCurrent(plan, before, removed);
        ResourceObservation target = before.Resources.Single(r => r.Ledger.ResourceId == resource.ResourceId);
        if (!target.Ownership.Verified) { throw new PreflightBlockedException("cleanup-ownership-unverified"); }
        var step = new OperationStep(operationId, "remove-" + resource.ResourceId, "remove-" + resource.Kind.ToString().ToLowerInvariant(), resource.ActualDockerId!);
        await execution.RunStepAsync(step, async safe =>
        {
            // Durable intent precedes the last authority, identity, stopped-state and consumer checks.
            EnvironmentObservation current = await ReadAsync(safe).ConfigureAwait(false);
            DestructivePlanBuilder.RequireCurrent(plan, current, removed);
            ResourceObservation exact = current.Resources.Single(r => r.Ledger.ResourceId == resource.ResourceId);
            if (!exact.Ownership.Verified) { throw new InvalidOperationException("Removal ownership changed."); }
            if (exact.Actual is null) { return; }
            if (exact.Actual.Identity != step.Target || (resource.Kind != ResourceKind.Container && exact.Actual.Consumers.Length != 0))
            { throw new InvalidOperationException("Removal identity or consumers changed."); }
            ResourceConsumerReader.Require(await docker.RunAsync([resource.Kind.ToString().ToLowerInvariant(), "rm", step.Target], safe).ConfigureAwait(false));
        }, async safe =>
        {
            EnvironmentObservation after = await ReadAsync(safe).ConfigureAwait(false);
            ResourceObservation exact = after.Resources.Single(r => r.Ledger.ResourceId == resource.ResourceId);
            bool absent = after.Freshness == "current" && after.Reason is null && exact.Ownership.Verified && exact.Actual is null;
            return new(step.Target, absent, absent ? null : "removal-not-confirmed");
        }).ConfigureAwait(false);
    }
}
