using System.Text.Json.Serialization;

namespace EnvironmentBuilder.Core.Environments;

public sealed record DependencyRequirement(string Id, Guid DefinitionId, string DefinitionVersion, string Condition, string Reason)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Optional { get; init; }
}
// Единственный сохранённый resolved graph — верхнеуровневый IsolatedEnvironment.dependencies.
public sealed record ComponentDependency(Guid DependentId, Guid RequiredId, string? RequirementId, string Condition, string Reason);
