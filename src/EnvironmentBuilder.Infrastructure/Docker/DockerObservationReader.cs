using System.Collections.Immutable;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Initialization;
using EnvironmentBuilder.Infrastructure.Persistence;

namespace EnvironmentBuilder.Infrastructure.Docker;

public sealed class DockerObservationReader(DockerProcessFactory processes, ResourceLedgerStore ledger, DataApplicationStore applications, SqliteWorker worker) : IEnvironmentObservationSource
{
    public async Task<EnvironmentObservation> ReadAsync(IsolatedEnvironment environment, Guid sessionId, EnvironmentObservation? previous, CancellationToken token)
    {
        DateTimeOffset at = DateTimeOffset.UtcNow;
        try
        {
            IDockerProcess docker = processes.Create(environment);
            var reader = new ResourceConsumerReader(docker);
            var all = await reader.AllContainersAsync(token).ConfigureAwait(false);
            IReadOnlyList<OwnedResource> resources = await ledger.ReadAsync(environment.Id, token).ConfigureAwait(false);
            if (resources.Count > 0)
            {
                DockerResult info = await docker.RunAsync(["info", "--format", "{{json .ID}}"], token).ConfigureAwait(false);
                ResourceConsumerReader.Require(info);
                string engine = System.Text.Json.JsonSerializer.Deserialize<string>(info.Output)!;
                if (resources.Any(r => !r.DesiredAttributes.TryGetValue("engineId", out string? expected) || expected != engine)) { throw new IOException("Docker authority changed or is unknown."); }
            }
            string[] allowed = resources.Where(r => r.Kind == ResourceKind.Container && r.ActualDockerId is not null).Select(r => r.ActualDockerId!).ToArray();
            var observations = ImmutableArray.CreateBuilder<ResourceObservation>();
            var components = ImmutableArray.CreateBuilder<ComponentObservation>();
            foreach (OwnedResource resource in resources)
            {
                DockerResourceRead? actual = await reader.ReadAsync(resource, all, token).ConfigureAwait(false);
                EffectObservation ownership = actual is null ? new(null, false, "resource-missing") : ResourceOwnershipVerifier.Verify(resource, actual.Evidence, allowed);
                observations.Add(new(resource, actual?.Evidence, ownership, at));
                if (resource.Kind == ResourceKind.Volume && !ownership.Verified)
                {
                    bool active = await worker.ReadAsync(connection =>
                    { using var command = Sql.Command(connection, null, "SELECT EXISTS(SELECT 1 FROM DataGenerations WHERE id=$id AND validity='active')", ("$id", resource.DataGenerationId.ToString())); return (long)command.ExecuteScalar()! == 1; }, token).ConfigureAwait(false);
                    if (active) { await applications.InvalidateAsync(resource.DataGenerationId, resource.OperationId, ownership.ErrorCode!, at, token).ConfigureAwait(false); }
                }
                if (actual?.Component is not null)
                { components.Add(actual.Component with { Drift = ownership.Verified ? "expected" : "foreign-conflict", Reason = ownership.ErrorCode }); }
            }
            foreach (EnvironmentComponent component in environment.Components.Where(c => !components.Any(o => o.ComponentId == c.Id)))
            { components.Add(new(component.Id, null, "missing", "unknown", [], ImmutableDictionary<string, Measurement>.Empty) { Drift = "missing" }); }
            Guid? activeId = resources.FirstOrDefault()?.DataGenerationId;
            bool generationValid = await applications.IsActiveGenerationAsync(environment.Id, activeId, CanonicalJson.Hash(environment.Baseline), token).ConfigureAwait(false);
            foreach (EnvironmentComponent component in environment.Components)
            {
                int index = Enumerable.Range(0, components.Count).First(i => components[i].ComponentId == component.Id);
                ComponentObservation actual = components[index];
                Readiness readiness = component.DefinitionSnapshot.Readiness;
                bool ready = actual.Drift == "expected" && actual.Status == "running" && readiness.Kind switch
                { "running" => true, "dockerHealth" => actual.Health == "healthy", _ => false };
                if (actual.Drift == "expected" && actual.Status == "running" && readiness.Kind == "http")
                {
                    using var http = new HttpInitializationTransport((_, endpoint) => LoopbackEndpointResolver.Resolve(component.DefinitionSnapshot.Endpoints.Single(e => e.Id == endpoint), actual), value => value.Literal!);
                    var step = new InitializationStep("readiness", "Readiness", readiness.EndpointId!, "GET", readiness.Path!, ImmutableDictionary<string, Value>.Empty,
                        new("none"), new(Enumerable.Range(200, 100).ToImmutableArray(), []), Math.Min(5, readiness.TimeoutSeconds), new("none", 1));
                    InitializationResponse response = await http.SendAsync(component.Id, step, "", null, token).ConfigureAwait(false);
                    ready = response.OutcomeKnown && response.AssertionsPassed;
                }
                bool initialized = generationValid;
                Guid? generation = resources.FirstOrDefault()?.DataGenerationId;
                foreach (BaselineApplication application in environment.Baseline.Applications.Where(a => a.ComponentId == component.Id))
                {
                    foreach (InitializationStep step in application.Initializer.Steps)
                    {
                        if (generation is null || !await applications.HasSuccessAsync(new(environment.Id, component.Id, generation.Value, CanonicalJson.Hash(environment.Baseline), CanonicalJson.Hash(application), CanonicalJson.Hash(application.Initializer), step.Id), token).ConfigureAwait(false))
                        { initialized = false; }
                    }
                }
                components[index] = actual with
                {
                    RuntimeReady = ready,
                    ApplicationReady = ReadinessEvaluator.ApplicationReady(readiness.Kind, ready, initialized),
                    Initialization = initialized ? "succeeded-or-not-required" : "unverified",
                    Reason = generationValid ? actual.Reason : "data-generation-invalid"
                };
            }
            foreach (var container in all.Where(c => c.GetProperty("Config").GetProperty("Labels").ValueKind == System.Text.Json.JsonValueKind.Object &&
                c.GetProperty("Config").GetProperty("Labels").TryGetProperty("io.environmentbuilder.environment", out var id) && id.GetString() == environment.Id.ToString() && !allowed.Contains(c.GetProperty("Id").GetString())))
            { components.Add(new(Guid.Empty, container.GetProperty("Id").GetString(), "unknown", "unknown", [], ImmutableDictionary<string, Measurement>.Empty) { Drift = "unexpected-owned", Reason = "missing-ledger" }); }
            return new(environment.Id, sessionId, at, at, "current", components.ToImmutable(), observations.ToImmutable(), null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new(environment.Id, sessionId, at, previous?.LastSuccessfulAt, previous is null ? "unknown" : "stale",
                previous?.Components.Select(c => c with { Measurements = c.Measurements.ToImmutableDictionary(p => p.Key, p => p.Value with { Freshness = "stale" }) }).ToImmutableArray() ??
                environment.Components.Select(c => new ComponentObservation(c.Id, null, "unknown", "unknown", [], ImmutableDictionary<string, Measurement>.Empty)).ToImmutableArray(),
                previous?.Resources ?? [], "docker-observation-unavailable");
        }
    }
}
