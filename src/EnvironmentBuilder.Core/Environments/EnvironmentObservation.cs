using System.Collections.Immutable;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.Core.Environments;

public sealed record PublishedPort(int ContainerPort, string Protocol, string HostIp, int HostPort);
public sealed record ComponentObservation(Guid ComponentId, string? ContainerId, string Status, string Health,
    ImmutableArray<PublishedPort> Ports, ImmutableDictionary<string, Measurement> Measurements)
{
    public bool RuntimeReady { get; init; }
    public bool ApplicationReady { get; init; }
    public string Initialization { get; init; } = "unverified";
    public string Drift { get; init; } = "expected";
    public string? Reason { get; init; }
}
public sealed record ResourceObservation(OwnedResource Ledger, ResourceEvidence? Actual, EffectObservation Ownership, DateTimeOffset LastCheckedAt);
public sealed record EnvironmentObservation(Guid EnvironmentId, Guid SessionId, DateTimeOffset CheckedAt, DateTimeOffset? LastSuccessfulAt,
    string Freshness, ImmutableArray<ComponentObservation> Components, ImmutableArray<ResourceObservation> Resources, string? Reason);
