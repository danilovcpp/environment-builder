using System.Collections.Immutable;

namespace EnvironmentBuilder.Core.Environments;

public static class ComposeCompiler
{
    public static ComposeModel Compile(IsolatedEnvironment environment)
    {
        EnvironmentValidator.Validate(environment);
        var services = ImmutableArray.CreateBuilder<ComposeService>();
        foreach (EnvironmentComponent component in environment.Components.OrderBy(c => c.Id))
        {
            var values = component.Environment.ToBuilder();
            foreach (ParameterDefinition parameter in component.DefinitionSnapshot.ParameterDefinitions)
            {
                if (component.Parameters.TryGetValue(parameter.Name, out Value? value)) { values.Add(parameter.EnvironmentVariable, value); }
            }
            var ports = component.Ports.Select(p => new ComposePort(p.Id, p.ContainerPort, p.Protocol, p.HostPort,
                p.Access == "network" ? "0.0.0.0" : "127.0.0.1", false)).ToList();
            var endpointIds = environment.Baseline.Applications.Where(a => a.ComponentId == component.Id).SelectMany(a => a.Initializer.Steps.Select(s => s.EndpointId)).ToHashSet(StringComparer.Ordinal);
            if (component.DefinitionSnapshot.Readiness.Kind == "http") { endpointIds.Add(component.DefinitionSnapshot.Readiness.EndpointId!); }
            foreach (Endpoint endpoint in component.DefinitionSnapshot.Endpoints.Where(e => endpointIds.Contains(e.Id)))
            {
                if (!ports.Any(p => p.Target == endpoint.ContainerPort && p.Protocol == "tcp" && p.HostIp == "127.0.0.1"))
                { ports.Add(new ComposePort("control-" + endpoint.Id, endpoint.ContainerPort, "tcp", null, "127.0.0.1", true)); }
            }
            services.Add(new ComposeService(component.Id, "c-" + component.Id.ToString("N"), component.Source, values.ToImmutable(), ports.ToImmutableArray(), component.Storage,
                environment.Dependencies.Where(e => e.DependentId == component.Id).ToImmutableArray())
            { ParameterBindings = component.DefinitionSnapshot.ParameterDefinitions.ToImmutableDictionary(p => p.EnvironmentVariable, StringComparer.Ordinal) });
        }
        return new ComposeModel(environment.Id, "eb-" + environment.Id.ToString("N"), services.ToImmutable());
    }
}
