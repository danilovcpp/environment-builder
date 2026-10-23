using System.Collections.Immutable;
using System.Text.Json;

namespace EnvironmentBuilder.Core.Environments;

public sealed record ComposePort(string Id, int Target, string Protocol, int? Published, string HostIp, bool AutomaticControl);
public sealed record ComposeService(Guid ComponentId, string Key, ComponentSource Source, ImmutableDictionary<string, Value> Environment,
    ImmutableArray<ComposePort> Ports, ImmutableArray<Storage> Storage, ImmutableArray<ComponentDependency> Dependencies)
{
    public ImmutableDictionary<string, ParameterDefinition> ParameterBindings { get; init; } = ImmutableDictionary<string, ParameterDefinition>.Empty;
}

// JSON is a Compose-supported YAML subset and avoids a second parser/quoting implementation.
public sealed record ComposeModel(Guid EnvironmentId, string ProjectName, ImmutableArray<ComposeService> Services)
{
    public string RenderPreview() => RenderInternal(value => value.SecretRef is not null ? "<secret:" + value.SecretRef + ">" : value.Literal!,
        reference => "<staged:" + reference + ">", reference => "<build-secret-file:" + reference + ">", null, true);

    public string Render(Func<Value, string> resolveValue, Func<string, string> stagedPath, Func<string, string> buildSecretFile,
        IReadOnlyDictionary<Guid, string>? pinnedImages = null)
        => RenderInternal(resolveValue, stagedPath, buildSecretFile, pinnedImages, false);

    private string RenderInternal(Func<Value, string> resolveValue, Func<string, string> stagedPath, Func<string, string> buildSecretFile,
        IReadOnlyDictionary<Guid, string>? pinnedImages, bool preview)
    {
        var serviceMap = new SortedDictionary<string, object>(StringComparer.Ordinal);
        var volumes = new SortedDictionary<string, object>(StringComparer.Ordinal);
        var secrets = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (ComposeService service in Services)
        {
            var model = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["platform"] = "linux/amd64", ["networks"] = new[] { "private" } };
            if (pinnedImages?.TryGetValue(service.ComponentId, out string? pinned) == true) { model["image"] = Escape(pinned); }
            else if (service.Source is ImageSource image) { model["image"] = Escape(image.Reference); }
            else if (service.Source is BuildSource build)
            {
                var buildModel = new SortedDictionary<string, object>(StringComparer.Ordinal)
                {
                    ["context"] = Escape(stagedPath(service.ComponentId + ":context:" + build.PathRef + "/" + build.ContextSubdirectory)),
                    ["dockerfile"] = Escape(build.Dockerfile),
                    ["args"] = build.BuildArguments.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => Escape(p.Value)),
                    ["platforms"] = new[] { "linux/amd64" }
                };
                var buildSecrets = new List<object>();
                foreach ((string id, string reference) in build.BuildSecrets.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    string key = "s-" + service.ComponentId.ToString("N") + "-" + id;
                    secrets[key] = new { file = Escape(buildSecretFile(reference)) };
                    buildSecrets.Add(new { source = key, target = id });
                }
                buildModel["secrets"] = buildSecrets;
                model["build"] = buildModel;
            }
            model["environment"] = service.Environment.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p =>
            {
                string value = resolveValue(p.Value);
                if (service.ParameterBindings.TryGetValue(p.Key, out ParameterDefinition? binding) && !(preview && p.Value.SecretRef is not null))
                {
                    EnvironmentValidator.ValidateLiteral(binding, value);
                    if (binding.Type == "integer") { value = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture); }
                }
                return Escape(value);
            });
            model["ports"] = service.Ports.Select(p => new { target = p.Target, published = p.Published?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0", host_ip = p.HostIp, protocol = p.Protocol }).ToArray();
            model["volumes"] = service.Storage.Select<Storage, object>(storage =>
            {
                if (storage is ManagedStorage)
                {
                    string key = "v-" + service.ComponentId.ToString("N") + "-" + storage.Id;
                    volumes[key] = new { name = ProjectName + "-" + key, driver = "local" };
                    return new { type = "volume", source = key, target = Escape(storage.TargetPath) };
                }
                var input = (ReadOnlyInputStorage)storage;
                return new { type = "bind", source = Escape(stagedPath(service.ComponentId + ":input:" + input.PathRef)), target = Escape(input.TargetPath), read_only = true, bind = new { create_host_path = false } };
            }).ToArray();
            // Application 'ready' is enforced by the scheduler after initialization, never by Compose.
            model["depends_on"] = service.Dependencies.OrderBy(d => d.RequiredId).ToDictionary(d => "c-" + d.RequiredId.ToString("N"),
                d => new { condition = d.Condition == "healthy" ? "service_healthy" : "service_started" });
            serviceMap[service.Key] = model;
        }
        return JsonSerializer.Serialize(new { name = ProjectName, services = serviceMap, volumes, networks = new { @private = new { driver = "bridge", name = ProjectName + "-network" } }, secrets }, new JsonSerializerOptions { WriteIndented = true });
    }
    private static string Escape(string value) => value.Replace("$", "$$", StringComparison.Ordinal);
}
