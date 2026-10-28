using System.Collections.Immutable;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.Operations;

public sealed record OperationPlan(Guid Id, Guid EnvironmentId, long Revision, string ConfigurationHash,
    Guid DataGenerationId, string BaselineHash, ImmutableArray<OwnedResource> Resources,
    ImmutableArray<InputFingerprint> Inputs, ImmutableArray<string> Stages, string Consequences,
    IsolatedEnvironment? Candidate = null)
{
    public ImmutableArray<EnvironmentBuilder.Core.SeedPacks.SeedPackReference> Packs { get; init; } = [];
    public string? Endpoint { get; init; }
    public string? EngineId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? EnvironmentName { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public Guid? NextDataGenerationId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? ObservedAt { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public ImmutableArray<ResourceEvidence>? ExpectedResources { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public string Hash => CanonicalJson.Hash(this);
}
public sealed record ConfirmationRecord(string PlanHash, DateTimeOffset ConfirmedAt);
public sealed record InputFingerprint(string Reference, string? Hash, string? RuntimeIdentity);
