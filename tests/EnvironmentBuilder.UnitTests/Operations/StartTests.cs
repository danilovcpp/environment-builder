using System.Collections.Immutable;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.UnitTests.Environments;

namespace EnvironmentBuilder.UnitTests.Operations;

public sealed class StartTests
{
    [Fact]
    public void Only_effective_secret_references_are_resolved_and_literal_reference_like_text_is_preserved()
    {
        var definition = AuthoringContractTests.Definition() with
        { DefaultEnvironment = ImmutableDictionary<string, Value>.Empty.Add("TOKEN", new() { SecretRef = "secret://" + Guid.NewGuid() }) };
        var draft = EnvironmentDraft.Create(); draft.AddComponent(definition, [definition]);
        EnvironmentComponent component = draft.Snapshot.Components[0] with
        { Environment = ImmutableDictionary<string, Value>.Empty.Add("TOKEN", new() { Literal = "secret://this-is-literal-data" }) };
        Assert.Empty(EnvironmentSecrets.References(draft.Snapshot with { Components = [component] }));
    }
    [Theory]
    [InlineData("running", "running", "notConfigured", false, true)]
    [InlineData("healthy", "running", "notConfigured", false, false)]
    [InlineData("healthy", "running", "healthy", false, true)]
    [InlineData("ready", "running", "healthy", false, false)]
    [InlineData("ready", "running", "notConfigured", true, true)]
    [InlineData("running", "unknown", "healthy", true, false)]
    public void Dependency_conditions_distinguish_lifecycle_health_and_application_readiness(string condition, string status, string health, bool ready, bool expected) =>
        Assert.Equal(expected, ReadinessEvaluator.DependencySatisfied(condition, status, health, ready));

    [Fact]
    public void None_is_never_application_ready() => Assert.False(ReadinessEvaluator.ApplicationReady("none", true, true));
}
