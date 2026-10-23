using System.Collections.Immutable;

namespace EnvironmentBuilder.Core.Environments;

public sealed record EnvironmentComponent(Guid Id, ComponentDefinition DefinitionSnapshot, ComponentSource Source,
    ImmutableDictionary<string, Value> Parameters, ImmutableDictionary<string, Value> Environment,
    ImmutableArray<Port> Ports, ImmutableArray<Storage> Storage);
