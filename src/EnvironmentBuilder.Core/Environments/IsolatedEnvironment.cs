using System.Collections.Immutable;
using System.Text.Json.Serialization;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.Core.Environments;

// Служебные timestamps, observation, draft UI и runtime-результаты не создают конфигурационных изменений.
public sealed record IsolatedEnvironment(string Kind, int SchemaVersion, Guid Id, long Revision, string Name,
    ImmutableArray<EnvironmentComponent> Components, ImmutableArray<ComponentDependency> Dependencies, DataBaseline Baseline)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Description { get; init; }
}
