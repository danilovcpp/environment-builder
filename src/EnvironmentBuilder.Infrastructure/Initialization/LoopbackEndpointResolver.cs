using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.Infrastructure.Initialization;

public static class LoopbackEndpointResolver
{
    public static Uri Resolve(Endpoint endpoint, ComponentObservation observed)
    {
        if (observed.ContainerId is null || observed.Status != "running" || observed.Drift != "expected") { throw new InvalidOperationException("A verified running container is required."); }
        PublishedPort mapping = observed.Ports.SingleOrDefault(p => p.ContainerPort == endpoint.ContainerPort && p.Protocol == "tcp" && p.HostIp == "127.0.0.1")
            ?? throw new InvalidOperationException("A verified loopback control mapping is required.");
        if (mapping.HostPort is < 1 or > 65535 || endpoint.Scheme is not ("http" or "https")) { throw new InvalidOperationException("Invalid HTTP mapping."); }
        InitializationValidator.RequireRequestPath(endpoint.BasePath, "/endpoint/basePath", basePath: true);
        return new Uri(endpoint.Scheme + "://127.0.0.1:" + mapping.HostPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + endpoint.BasePath);
    }
}
