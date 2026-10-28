using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.Infrastructure.Docker;

public sealed record DockerResourceRead(ResourceEvidence Evidence, ComponentObservation? Component);
public sealed class ResourceConsumerReader(IDockerProcess docker)
{
    public async Task<ImmutableArray<JsonElement>> AllContainersAsync(CancellationToken token)
    {
        DockerResult list = await docker.RunAsync(["container", "ls", "--all", "--no-trunc", "--quiet"], token).ConfigureAwait(false);
        Require(list);
        string[] ids = list.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Any(id => id.Length != 64 || id.Any(c => !char.IsAsciiHexDigit(c)))) { throw new IOException("Unknown container identity output."); }
        if (ids.Length == 0) { return []; }
        var result = ImmutableArray.CreateBuilder<JsonElement>();
        foreach (string[] batch in ids.Chunk(100))
        {
            DockerResult inspection = await docker.RunAsync(["container", "inspect", .. batch], token).ConfigureAwait(false);
            Require(inspection);
            using JsonDocument json = JsonDocument.Parse(inspection.Output);
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() != batch.Length) { throw new IOException("Incomplete container inspection."); }
            string[] observedIds = json.RootElement.EnumerateArray().Select(c => c.GetProperty("Id").GetString() ?? throw new IOException("Missing container identity.")).ToArray();
            if (observedIds.Distinct(StringComparer.Ordinal).Count() != batch.Length || !batch.ToHashSet(StringComparer.Ordinal).SetEquals(observedIds))
            { throw new IOException("Container inspection identity set differs from the requested set."); }
            result.AddRange(json.RootElement.EnumerateArray().Select(e => e.Clone()));
        }
        return result.ToImmutable();
    }

    public async Task<DockerResourceRead?> ReadAsync(OwnedResource ledger, ImmutableArray<JsonElement> all, CancellationToken token)
    {
        JsonElement actual;
        if (ledger.Kind == ResourceKind.Container)
        {
            JsonElement? found = all.Where(c => c.GetProperty("Id").GetString() == ledger.ActualDockerId || c.GetProperty("Name").GetString()?.TrimStart('/') == ledger.Name).Select(c => (JsonElement?)c).FirstOrDefault();
            if (found is null) { return null; }
            actual = found.Value;
        }
        else
        {
            string kind = ledger.Kind.ToString().ToLowerInvariant();
            DockerResult list = await docker.RunAsync([kind, "ls", "--format", "{{json .Name}}"], token).ConfigureAwait(false);
            Require(list);
            string[] names = list.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<string>(line) is string name && !string.IsNullOrWhiteSpace(name) ? name : throw new IOException("Unknown resource list output.")).ToArray();
            bool exists = names.Contains(ledger.Name, StringComparer.Ordinal);
            if (!exists) { return null; }
            DockerResult inspect = await docker.RunAsync([kind, "inspect", ledger.Name], token).ConfigureAwait(false);
            Require(inspect);
            using JsonDocument json = JsonDocument.Parse(inspect.Output);
            actual = json.RootElement[0].Clone();
        }
        return Parse(ledger, actual, all);
    }

    public static DockerResourceRead Parse(OwnedResource ledger, JsonElement actual, ImmutableArray<JsonElement> all)
    {
        var consumers = ImmutableArray.CreateBuilder<string>();
        foreach (JsonElement container in all)
        {
            bool uses = ledger.Kind switch
            {
                ResourceKind.Volume => container.GetProperty("Mounts").EnumerateArray().Any(m => m.GetProperty("Type").GetString() == "volume" && m.GetProperty("Name").GetString() == ledger.Name),
                ResourceKind.Network => container.GetProperty("NetworkSettings").GetProperty("Networks").EnumerateObject().Any(n => n.Value.GetProperty("NetworkID").GetString() == actual.GetProperty("Id").GetString()),
                _ => false
            };
            if (uses) { consumers.Add(container.GetProperty("Id").GetString()!); }
        }
        if (ledger.Kind == ResourceKind.Network && actual.TryGetProperty("Containers", out JsonElement attachments) && attachments.ValueKind == JsonValueKind.Object)
        { consumers.AddRange(attachments.EnumerateObject().Select(p => p.Name)); }
        JsonElement labelElement = ledger.Kind == ResourceKind.Container ? actual.GetProperty("Config").GetProperty("Labels") : actual.GetProperty("Labels");
        ImmutableDictionary<string, string> labels = Map(labelElement);
        VolumeIncarnation? volume = ledger.Kind == ResourceKind.Volume ? new(actual.GetProperty("CreatedAt").GetString()!, actual.GetProperty("Driver").GetString()!, actual.GetProperty("Scope").GetString()!, Map(actual.GetProperty("Options"))) : null;
        string id = actual.GetProperty(ledger.Kind == ResourceKind.Volume ? "Name" : "Id").GetString()!;
        string name = actual.GetProperty("Name").GetString()!.TrimStart('/');
        ComponentObservation? component = null;
        if (ledger.Kind == ResourceKind.Container)
        {
            JsonElement state = actual.GetProperty("State");
            string health = state.TryGetProperty("Health", out JsonElement h) ? h.GetProperty("Status").GetString()! : "notConfigured";
            var ports = ImmutableArray.CreateBuilder<PublishedPort>();
            foreach (JsonProperty port in actual.GetProperty("NetworkSettings").GetProperty("Ports").EnumerateObject())
            {
                if (port.Value.ValueKind != JsonValueKind.Array) { continue; }
                string[] parts = port.Name.Split('/');
                foreach (JsonElement mapping in port.Value.EnumerateArray())
                { ports.Add(new(int.Parse(parts[0], CultureInfo.InvariantCulture), parts[1], mapping.GetProperty("HostIp").GetString()!, int.Parse(mapping.GetProperty("HostPort").GetString()!, CultureInfo.InvariantCulture))); }
            }
            component = new(ledger.ComponentId!.Value, id, state.GetProperty("Status").GetString()!, health, ports.ToImmutable(), ImmutableDictionary<string, Measurement>.Empty)
            {
                ExitCode = state.TryGetProperty("ExitCode", out var exitCode) ? exitCode.GetInt32() : null,
                OomKilled = state.TryGetProperty("OOMKilled", out var oomKilled) ? oomKilled.GetBoolean() : null
            };
        }
        return new(new(ledger.Kind, name, id, labels, volume, consumers.Distinct(StringComparer.Ordinal).ToImmutableArray(), true, component?.Status ?? "available")
        {
            NetworkDriver = ledger.Kind == ResourceKind.Network && actual.TryGetProperty("Driver", out var driver) ? driver.GetString() : null,
            NetworkScope = ledger.Kind == ResourceKind.Network && actual.TryGetProperty("Scope", out var networkScope) ? networkScope.GetString() : null
        }, component);
    }
    internal static void Require(DockerResult result)
    { if (result.ExitCode != 0 || result.Truncated) { throw new IOException("Docker query failed or exceeded its output limit."); } }
    private static ImmutableDictionary<string, string> Map(JsonElement json) => json.ValueKind == JsonValueKind.Null ? ImmutableDictionary<string, string>.Empty : json.EnumerateObject().ToImmutableDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
}
