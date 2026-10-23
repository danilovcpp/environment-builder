using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.UnitTests.Environments;

public sealed class AuthoringContractTests
{
    [Fact]
    public void Dependency_materialization_shows_reasons_and_ambiguity_requires_an_explicit_instance()
    {
        ComponentDefinition target = Definition() with { DefaultPorts = [] };
        ComponentDefinition dependency = target with { Id = Guid.NewGuid(), Name = "Other", Initialization = null, DependencyRequirements = [] };
        var requirement = new DependencyRequirement("other", dependency.Id, dependency.Version, "running", "Required for this component");
        target = target with { DependencyRequirements = [requirement] };
        var draft = EnvironmentDraft.Create();
        draft.AddComponent(target, [target, dependency]);
        Assert.Equal(2, draft.Snapshot.Components.Length);
        Assert.Equal(requirement.Reason, Assert.Single(draft.Snapshot.Dependencies).Reason);
        var ambiguous = EnvironmentDraft.Create();
        ambiguous.AddComponent(dependency, [dependency]);
        ambiguous.AddComponent(dependency, [dependency]);
        ambiguous.AddComponent(target, [target, dependency]);
        var choice = Assert.Single(ambiguous.UnresolvedDependencies);
        Assert.Equal(2, choice.Candidates.Length);
        Assert.Empty(ambiguous.Snapshot.Dependencies);
        ambiguous.Bind(choice, choice.Candidates[1]);
        Assert.Equal(choice.Candidates[1], Assert.Single(ambiguous.Snapshot.Dependencies).RequiredId);
        ambiguous.Update(ambiguous.Snapshot with { Name = "Explicit graph" });
        EnvironmentValidator.Validate(ambiguous.Snapshot);
    }

    [Fact]
    public void Compose_uses_effective_values_escapes_dollars_and_isolates_storage_and_control_ports()
    {
        var draft = EnvironmentDraft.Create();
        ComponentDefinition definition = Definition();
        draft.AddComponent(definition, [definition]);
        var component = draft.Snapshot.Components[0];
        component = component with
        {
            Environment = component.Environment.Add("PLAIN", new Value { Literal = "${TOKEN}:\\value" }).Add("AUTH", new Value { SecretRef = "secret://" + Guid.NewGuid() }),
            Parameters = component.Parameters.SetItem("MaxItems", new Value { Literal = "12" }),
            Ports = [component.Ports[0] with { Access = "network" }],
            Storage = component.Storage.Add(new ReadOnlyInputStorage("fixtures", "/input", "local-path://fixtures"))
        };
        var environment = draft.Snapshot with { Name = "Scope", Components = [component] };
        ComposeModel model = ComposeCompiler.Compile(environment);
        var service = Assert.Single(model.Services);
        Assert.Equal("12", service.Environment["MAX_ITEMS"].Literal);
        Assert.Contains(service.Ports, p => p.HostIp == "0.0.0.0" && !p.AutomaticControl);
        Assert.Contains(service.Ports, p => p.HostIp == "127.0.0.1" && p.Published is null && p.AutomaticControl);
        using JsonDocument preview = JsonDocument.Parse(model.RenderPreview());
        var rendered = preview.RootElement.GetProperty("services").GetProperty(service.Key);
        Assert.Equal("$${TOKEN}:\\value", rendered.GetProperty("environment").GetProperty("PLAIN").GetString());
        Assert.StartsWith("<secret:secret://", rendered.GetProperty("environment").GetProperty("AUTH").GetString());
        Assert.True(rendered.GetProperty("volumes")[1].GetProperty("read_only").GetBoolean());
        Assert.Contains("v-" + component.Id.ToString("N"), model.RenderPreview());
        string runtime = model.Render(v => v.SecretRef is null ? v.Literal! : "resolved-secret", _ => "C:/private/$staged", _ => "C:/private/secret");
        Assert.Contains("resolved-secret", runtime);
        Assert.DoesNotContain("<secret:", runtime);
        Assert.NotEqual(model.ProjectName, ComposeCompiler.Compile(environment with { Id = Guid.NewGuid() }).ProjectName);
    }
    public static ComponentDefinition Definition() => JsonSerializer.Deserialize<ComponentDefinition>(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "Contracts/examples/component.json")), CanonicalJson.Options)!;

    [Fact]
    public void New_draft_materializes_defaults_and_compiles_deterministic_instance_scoped_compose()
    {
        var draft = EnvironmentDraft.Create();
        ComponentDefinition definition = Definition();
        draft.AddComponent(definition, [definition]);
        draft.Update(draft.Snapshot with { Name = "Offline" });
        EnvironmentValidator.Validate(draft.Snapshot);
        Assert.Equal(1, draft.Snapshot.Revision);
        var component = Assert.Single(draft.Snapshot.Components);
        Assert.NotEqual(definition.Id, component.Id);
        Assert.Equal(definition.DefaultSource, component.Source);
        Assert.Equal("1000", component.Parameters["MaxItems"].Literal);
        Assert.Single(draft.Snapshot.Baseline.Applications);
        ComposeModel model = ComposeCompiler.Compile(draft.Snapshot);
        Assert.Equal("eb-" + draft.Snapshot.Id.ToString("N"), model.ProjectName);
        Assert.Contains("c-" + component.Id.ToString("N"), model.RenderPreview());
        Assert.Contains("127.0.0.1", model.RenderPreview());
        Assert.Equal(model.RenderPreview(), ComposeCompiler.Compile(draft.Snapshot).RenderPreview());
    }
}
