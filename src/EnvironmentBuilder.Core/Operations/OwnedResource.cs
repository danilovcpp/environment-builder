using System.Collections.Immutable;

namespace EnvironmentBuilder.Core.Operations;

public enum ResourceKind { Container, Network, Volume }
public sealed record OwnedResource(Guid ResourceId, Guid InstallationId, Guid EnvironmentId, Guid? ComponentId,
    ResourceKind Kind, long CreationRevision, Guid DataGenerationId, string Name,
    ImmutableDictionary<string, string> DesiredAttributes, string? ActualDockerId, VolumeIncarnation? Volume,
    ImmutableArray<string> Consumers, bool ConsumersKnown, Guid OperationId, bool Unresolved);
public sealed record VolumeIncarnation(string CreatedAt, string Driver, string Scope, ImmutableDictionary<string, string> Options);
