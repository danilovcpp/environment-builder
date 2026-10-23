using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.SeedPacks;

public sealed record InitializationDefinition(string Kind, int SchemaVersion, Guid Id, string Version,
    ContractCompatibility TargetContract, ImmutableArray<InitializationStep> Steps);
public sealed record InitializationStep(string Id, string Title, string EndpointId, string Method, string Path,
    ImmutableDictionary<string, Value> Headers, InitializationBody Body, InitializationExpectation Expect,
    int TimeoutSeconds, RetryPolicy Retry);
public sealed record InitializationBody(string Kind)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JsonElement? Value { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SectionId { get; init; }
}
public sealed record InitializationExpectation(ImmutableArray<int> Statuses, ImmutableArray<JsonAssertion> Assertions);
public sealed record JsonAssertion(string Pointer, [property: JsonPropertyName("equals")] JsonElement ExpectedValue);
public sealed record RetryPolicy(string Mode, int MaxAttempts)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SafetyEvidence { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? IdempotencyHeader { get; init; }
}
