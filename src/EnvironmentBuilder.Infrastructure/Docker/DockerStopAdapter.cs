using System.Collections.Immutable;
using System.Globalization;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Infrastructure.Persistence;

namespace EnvironmentBuilder.Infrastructure.Docker;

public sealed class DockerStopAdapter(AppDataDirectory directory, IEnvironmentStore environments, ResourceLedgerStore ledger, DockerProcessFactory processes, SqliteWorker worker) : IStopRuntime
{
    public async Task<IStopSession> PrepareAsync(IsolatedEnvironment environment, Guid operationId, int timeoutSeconds, CancellationToken token)
    {
        EnvironmentListItem saved = (await environments.ListAsync(token).ConfigureAwait(false)).Single(e => e.Id == environment.Id);
        if (saved.Document is null || CanonicalJson.Hash(saved.Document) != CanonicalJson.Hash(environment)) { throw new PreflightBlockedException("saved-configuration-changed"); }
        IDockerProcess docker = processes.Create(environment);
        var probe = new DockerCapabilityProbe(docker, directory);
        DockerCapabilities capability = await probe.ProbeAsync(token).ConfigureAwait(false);
        IReadOnlyList<OwnedResource> resources = await ledger.ReadAsync(environment.Id, token).ConfigureAwait(false);
        Guid generation = await worker.ReadAsync(connection =>
        {
            using var command = Sql.Command(connection, null, "SELECT generationId FROM Environments WHERE id=$id", ("$id", environment.Id.ToString()));
            return Guid.Parse((string)command.ExecuteScalar()!);
        }, token).ConfigureAwait(false);
        if (resources.Any(r => !r.DesiredAttributes.TryGetValue("engineId", out string? engine) || engine != capability.EngineId))
        { throw new PreflightBlockedException("resource-authority-changed"); }
        var plan = new OperationPlan(Guid.NewGuid(), environment.Id, environment.Revision, CanonicalJson.Hash(environment), generation,
            CanonicalJson.Hash(environment.Baseline), resources.ToImmutableArray(), [], ["stop", "verify-quiescence"],
            $"Graceful timeout {timeoutSeconds}s per container; Docker may terminate it after timeout. Containers, data and success markers are preserved.")
        { EngineId = capability.EngineId, Endpoint = capability.Endpoint };
        var session = new DockerStopSession(environment, operationId, plan, timeoutSeconds, docker, safe => probe.RequireEngineAsync(capability, safe));
        EnvironmentObservation actual = await session.ReadAsync(token).ConfigureAwait(false);
        if (actual.Components.Any(c => c.Drift is "unexpected-owned" or "foreign-conflict") || actual.Resources.Any(r => r.Ledger.Kind == ResourceKind.Container && !r.Ownership.Verified))
        { throw new PreflightBlockedException("stop-ownership-unverified"); }
        if (actual.Resources.Any(r => r.Actual is { Kind: ResourceKind.Container } container && container.State is not ("running" or "restarting" or "paused" or "created" or "exited")))
        { throw new PreflightBlockedException("stop-state-unknown"); }
        return session;
    }
}

// The session exposes the real CLI contract to offline tests without bypassing the production capability probe.
public sealed class DockerStopSession(IsolatedEnvironment environment, Guid operationId, OperationPlan plan, int timeoutSeconds,
    IDockerProcess docker, Func<CancellationToken, Task> requireEngine) : IStopSession
{
    private readonly ResourceConsumerReader reader = new(docker);
    public OperationPlan Plan => plan;

    public async Task<EnvironmentObservation> ReadAsync(CancellationToken token)
    {
        await requireEngine(token).ConfigureAwait(false);
        var all = await reader.AllContainersAsync(token).ConfigureAwait(false);
        var resources = ImmutableArray.CreateBuilder<ResourceObservation>();
        var components = ImmutableArray.CreateBuilder<ComponentObservation>();
        string[] allowed = plan.Resources.Where(r => r.Kind == ResourceKind.Container && r.ActualDockerId is not null).Select(r => r.ActualDockerId!).ToArray();
        DateTimeOffset at = DateTimeOffset.UtcNow;
        foreach (OwnedResource resource in plan.Resources)
        {
            DockerResourceRead? actual = await reader.ReadAsync(resource, all, token).ConfigureAwait(false);
            // Absence is known from a complete list/inspect; it does not assert data integrity.
            EffectObservation ownership = actual is null && !resource.Unresolved && resource.ActualDockerId is not null
                ? new(null, true) : actual is null ? new(null, false, "resource-unresolved") : ResourceOwnershipVerifier.Verify(resource, actual.Evidence, allowed);
            resources.Add(new(resource, actual?.Evidence, ownership, at));
            if (actual?.Component is not null) { components.Add(actual.Component with { Drift = ownership.Verified ? "expected" : "foreign-conflict" }); }
        }
        foreach (var container in all)
        {
            var labels = container.GetProperty("Config").GetProperty("Labels");
            if (labels.ValueKind == System.Text.Json.JsonValueKind.Object && labels.TryGetProperty("io.environmentbuilder.environment", out var id) && id.GetString() == environment.Id.ToString() && !allowed.Contains(container.GetProperty("Id").GetString()))
            { components.Add(new(Guid.Empty, container.GetProperty("Id").GetString(), "unknown", "unknown", [], ImmutableDictionary<string, Measurement>.Empty) { Drift = "unexpected-owned" }); }
        }
        return new(environment.Id, operationId, at, at, "current", components.ToImmutable(), resources.ToImmutable(), null);
    }

    public async Task StopAsync(OwnedResource container, OperationExecution execution, CancellationToken token)
    {
        EnvironmentObservation before = await ReadAsync(token).ConfigureAwait(false);
        if (before.Components.Any(c => c.Drift is "unexpected-owned" or "foreign-conflict")) { throw new UnresolvedEffectException("stop-ownership-unverified"); }
        ResourceObservation resource = before.Resources.Single(r => r.Ledger.ResourceId == container.ResourceId);
        if (!resource.Ownership.Verified) { throw new UnresolvedEffectException("stop-ownership-unverified"); }
        if (resource.Actual is null || QuiescenceGuard.IsStopped(resource.Actual.State)) { return; }
        if (resource.Actual.State is not ("running" or "restarting" or "paused")) { throw new UnresolvedEffectException("stop-state-unknown"); }
        var step = new OperationStep(operationId, "stop-" + container.ResourceId, "stop-container", resource.Actual.Identity);
        await execution.RunStepAsync(step, async safe =>
        {
            // Durable intent precedes the final authority/identity check and the exact ID mutation.
            EnvironmentObservation current = await ReadAsync(safe).ConfigureAwait(false);
            ResourceObservation target = current.Resources.Single(r => r.Ledger.ResourceId == container.ResourceId);
            if (!target.Ownership.Verified || target.Actual?.Identity != step.Target || current.Components.Any(c => c.Drift is "unexpected-owned" or "foreign-conflict"))
            { throw new InvalidOperationException("Stop scope changed."); }
            ResourceConsumerReader.Require(await docker.RunAsync(["container", "stop", "--timeout", timeoutSeconds.ToString(CultureInfo.InvariantCulture), step.Target], safe).ConfigureAwait(false));
        }, async safe =>
        {
            EnvironmentObservation after = await ReadAsync(safe).ConfigureAwait(false);
            ResourceObservation target = after.Resources.Single(r => r.Ledger.ResourceId == container.ResourceId);
            bool verified = target.Ownership.Verified && (target.Actual is null || QuiescenceGuard.IsStopped(target.Actual.State));
            ComponentObservation? component = after.Components.FirstOrDefault(c => c.ContainerId == step.Target);
            return new EffectObservation(step.Target, verified, verified ? null : "stop-not-confirmed")
            { ContainerExitCode = component?.ExitCode, ContainerOomKilled = component?.OomKilled };
        }).ConfigureAwait(false);
    }
}
