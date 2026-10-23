using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.UnitTests.Contracts;

public sealed class EnvironmentRuleTests
{
    private static IsolatedEnvironment Example() => JsonSerializer.Deserialize<IsolatedEnvironment>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Contracts/examples/environment.json")), CanonicalJson.Options)!;

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("1.0")]
    [InlineData("+1")]
    [InlineData("01")]
    public void Integer_parameters_are_canonical_Int64_strings(string value)
    {
        IsolatedEnvironment environment = Example();
        EnvironmentComponent component = environment.Components[0];
        component = component with { Parameters = component.Parameters.SetItem("MaxItems", new Value { Literal = value }) };
        Assert.Throws<DocumentException>(() => EnvironmentValidator.Validate(environment with { Components = [component] }));
    }

    [Fact]
    public void Parameters_do_not_accept_unknown_keys_plaintext_secrets_or_environment_collisions()
    {
        IsolatedEnvironment environment = Example();
        EnvironmentComponent component = environment.Components[0];
        Assert.Throws<DocumentException>(() => EnvironmentValidator.Validate(environment with { Components = [component with { Parameters = component.Parameters.Add("unknown", new Value { Literal = "value" }) }] }));
        Assert.Throws<DocumentException>(() => EnvironmentValidator.Validate(environment with { Components = [component with { Environment = component.Environment.Add("MAX_ITEMS", new Value { Literal = "value" }) }] }));
        ParameterDefinition parameter = component.DefinitionSnapshot.ParameterDefinitions[0] with { Secret = true, DefaultValue = null };
        component = component with { DefinitionSnapshot = component.DefinitionSnapshot with { ParameterDefinitions = [parameter] } };
        Assert.Throws<DocumentException>(() => EnvironmentValidator.Validate(environment with { Components = [component] }));
        component = component with { Parameters = component.Parameters.SetItem(parameter.Name, new Value { SecretRef = "secret://" + Guid.NewGuid() }) };
        EnvironmentValidator.Validate(environment with { Components = [component] });
    }

    [Fact]
    public void Build_and_readonly_inputs_are_descriptions_and_infrastructure_is_image_only()
    {
        IsolatedEnvironment environment = Example();
        var build = new BuildSource("local-path://source", "Dockerfile", ".", ImmutableDictionary<string, string>.Empty.Add("ARG", "literal $value"), ImmutableDictionary<string, string>.Empty.Add("credential", "secret://" + Guid.NewGuid()));
        EnvironmentComponent component = environment.Components[0] with { Source = build, Storage = [new ReadOnlyInputStorage("input", "/input", "local-path://input")] };
        EnvironmentValidator.Validate(environment with { Components = [component] });
        Assert.Throws<DocumentException>(() => EnvironmentValidator.Validate(environment with { Components = [component with { Source = build with { Dockerfile = "../escape" } }] }));
        Assert.Throws<DocumentException>(() => EnvironmentValidator.Validate(environment with { Components = [component with { DefinitionSnapshot = component.DefinitionSnapshot with { ComponentType = "infrastructure" } }] }));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/data/../outside")]
    [InlineData("/data//nested")]
    [InlineData("/var/run/docker.sock")]
    public void Unsafe_mounts_are_rejected(string target)
    {
        IsolatedEnvironment environment = Example();
        Assert.Throws<DocumentException>(() => EnvironmentValidator.Validate(environment with { Components = [environment.Components[0] with { Storage = [new ManagedStorage("data", target)] }] }));
    }

    [Fact]
    public void Port_scope_overlap_is_detected_across_instances()
    {
        IsolatedEnvironment environment = Example();
        EnvironmentComponent first = environment.Components[0] with { DefinitionSnapshot = environment.Components[0].DefinitionSnapshot with { Initialization = null } };
        EnvironmentComponent second = first with { Id = Guid.NewGuid(), Ports = [first.Ports[0] with { Access = "network" }] };
        Assert.Throws<DocumentException>(() => EnvironmentValidator.Validate(environment with { Components = [first, second], Baseline = new DataBaseline([]) }));
        EnvironmentValidator.Validate(environment with { Components = [first, second with { Ports = [second.Ports[0] with { HostPort = null }] }], Baseline = new DataBaseline([]) });
    }

    [Fact]
    public void Requirements_need_exact_explicit_instance_bindings_and_cycles_report_the_chain()
    {
        IsolatedEnvironment environment = Example();
        EnvironmentComponent first = environment.Components[0] with { DefinitionSnapshot = environment.Components[0].DefinitionSnapshot with { Initialization = null } };
        EnvironmentComponent required = first with { Id = Guid.NewGuid(), Ports = [], DefinitionSnapshot = first.DefinitionSnapshot with { Id = Guid.NewGuid() } };
        var requirement = new DependencyRequirement("required", required.DefinitionSnapshot.Id, required.DefinitionSnapshot.Version, "running", "Required component");
        first = first with { DefinitionSnapshot = first.DefinitionSnapshot with { DependencyRequirements = [requirement] } };
        environment = environment with { Components = [first, required, required with { Id = Guid.NewGuid() }], Baseline = new DataBaseline([]), Dependencies = [] };
        Assert.Throws<DocumentException>(() => EnvironmentValidator.Validate(environment));
        var edge = new ComponentDependency(first.Id, required.Id, requirement.Id, "running", requirement.Reason);
        EnvironmentValidator.Validate(environment with { Dependencies = [edge] });
        DocumentException cycle = Assert.Throws<DocumentException>(() => EnvironmentValidator.Validate(environment with { Dependencies = [edge, new ComponentDependency(required.Id, first.Id, null, "running", "cycle")] }));
        Assert.Contains(first.Id.ToString(), cycle.Code);
        Assert.Contains(required.Id.ToString(), cycle.Code);
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("Content-Length")]
    [InlineData("X-Forwarded-Host")]
    [InlineData("Proxy-Authorization")]
    public void Initializer_headers_cannot_override_routing_or_transport(string header)
    {
        InitializationDefinition initializer = Example().Components[0].DefinitionSnapshot.Initialization!;
        InitializationStep step = initializer.Steps[0] with { Headers = initializer.Steps[0].Headers.Add(header, new Value { Literal = "value" }) };
        Assert.Throws<DocumentException>(() => InitializationValidator.Validate(initializer with { Steps = [step] }));
    }

    [Fact]
    public void No_readiness_disallows_initialization_and_application_hashes_include_instance_and_exact_pack()
    {
        IsolatedEnvironment environment = Example();
        EnvironmentComponent component = environment.Components[0];
        Assert.Throws<DocumentException>(() => EnvironmentValidator.Validate(environment with { Components = [component with { DefinitionSnapshot = component.DefinitionSnapshot with { Readiness = new Readiness("none", 30) } }] }));
        BaselineApplication application = environment.Baseline.Applications[0];
        Assert.NotEqual(CanonicalJson.Hash(application), CanonicalJson.Hash(application with { ComponentId = Guid.NewGuid() }));
        Assert.NotEqual(CanonicalJson.Hash(application), CanonicalJson.Hash(application with { SeedPack = new SeedPackReference(Guid.NewGuid(), "1.0.0", new string('a', 64)) }));
    }
}
