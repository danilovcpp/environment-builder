using System.Collections.Immutable;
using System.Text.Json.Serialization;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.Core.Environments;

public sealed record ComponentDefinition(string Kind, int SchemaVersion, Guid Id, string Version, string Name,
    string ComponentType, ComponentContract Contract, ComponentSource DefaultSource,
    ImmutableArray<ParameterDefinition> ParameterDefinitions, ImmutableDictionary<string, Value> DefaultEnvironment,
    ImmutableArray<Port> DefaultPorts, ImmutableArray<Storage> DefaultStorage, ImmutableArray<Endpoint> Endpoints,
    Readiness Readiness, ImmutableArray<DependencyRequirement> DependencyRequirements, InitializationDefinition? Initialization)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Description { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Icon { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Color { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LogoUrl { get; init; }
}
