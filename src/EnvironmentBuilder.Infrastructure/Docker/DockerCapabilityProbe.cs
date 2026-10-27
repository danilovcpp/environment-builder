using System.Text.Json;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Infrastructure.Persistence;

namespace EnvironmentBuilder.Infrastructure.Docker;

public sealed record DockerCapabilities(string Endpoint, string EngineId, string EngineVersion, string ApiVersion, string ComposeVersion);
public sealed class DockerCapabilityProbe(IDockerProcess docker, AppDataDirectory directory)
{
    public static bool SupportsCompose(string text) => Version.TryParse(text.TrimStart('v').Split('-')[0], out Version? version) &&
        version.Major is 2 or 5 && version >= new Version(2, 24, 6);

    public async Task<DockerCapabilities> ProbeAsync(CancellationToken token)
    {
        DockerEndpoint.RequireLocal(docker.Endpoint);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
        { throw new PreflightBlockedException("windows-24h2-x64-required"); }
        JsonElement server = await JsonAsync(["version", "--format", "{{json .Server}}"], token).ConfigureAwait(false);
        JsonElement info = await JsonAsync(["info", "--format", "{{json .}}"], token).ConfigureAwait(false);
        JsonElement compose = await JsonAsync(["compose", "version", "--format", "json"], token).ConfigureAwait(false);
        DockerCapabilities capabilities = ParseCapabilities(docker.Endpoint, server, info, compose);
        DockerResult help = await docker.RunAsync(["compose", "up", "--help"], token).ConfigureAwait(false);
        if (help.ExitCode != 0 || help.Truncated || new[] { "--no-deps", "--no-build", "--pull", "--detach" }.Any(flag => !help.Output.Contains(flag, StringComparison.Ordinal)))
        { throw new NotSupportedException("Required Compose launch capabilities are unavailable."); }
        string root = directory.Child("staging", "probe-" + Guid.NewGuid().ToString("N"));
        AppDataDirectory.CreatePrivate(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "compose.json"), "{\"services\":{\"sentinel\":{\"image\":\"scratch\",\"platform\":\"linux/amd64\",\"ports\":[{\"target\":8080,\"published\":\"0\",\"host_ip\":\"127.0.0.1\"}]}}}", token).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(root, "empty.env"), "", token).ConfigureAwait(false);
            DockerResult sentinel = await docker.RunAsync(["compose", "--ansi", "never", "--progress", "plain", "--project-directory", root,
                "--env-file", Path.Combine(root, "empty.env"), "-f", Path.Combine(root, "compose.json"), "-p", "eb-capability", "config", "--format", "json"], token).ConfigureAwait(false);
            if (sentinel.ExitCode != 0 || sentinel.Truncated) { throw new NotSupportedException("Compose sentinel validation failed."); }
            using JsonDocument parsed = JsonDocument.Parse(sentinel.Output);
            if (!parsed.RootElement.GetProperty("services").TryGetProperty("sentinel", out _)) { throw new NotSupportedException("Unknown Compose output format."); }
        }
        finally { Security.LocalPathResolver.RequireWithin(root, directory.Root); Security.LocalPathResolver.RejectReparsePoints(root); Directory.Delete(root, recursive: true); }
        return capabilities;
    }

    public static DockerCapabilities ParseCapabilities(string endpoint, JsonElement server, JsonElement info, JsonElement compose)
    {
        DockerEndpoint.RequireLocal(endpoint);
        string engine = Required(info, "ID");
        string version = Required(server, "Version");
        string api = Required(server, "ApiVersion");
        string composeVersion = Required(compose, "version");
        if (!Version.TryParse(version.Split('-')[0], out Version? engineVersion) || engineVersion < new Version(28, 0, 0) ||
            !Version.TryParse(api, out Version? apiVersion) || apiVersion < new Version(1, 48) || !SupportsCompose(composeVersion) ||
            Required(info, "OSType") != "linux" || Required(info, "Architecture") is not ("x86_64" or "amd64") ||
            !Required(info, "OperatingSystem").Contains("Docker Desktop", StringComparison.OrdinalIgnoreCase) ||
            !Required(info, "KernelVersion").Contains("WSL2", StringComparison.OrdinalIgnoreCase))
        { throw new NotSupportedException("Docker Desktop WSL2/Linux capabilities are unsupported or unverified."); }
        return new(endpoint, engine, version, api, composeVersion);
    }

    public async Task RequireEngineAsync(DockerCapabilities pinned, CancellationToken token)
    {
        JsonElement info = await JsonAsync(["info", "--format", "{{json .}}"], token).ConfigureAwait(false);
        if (docker.Endpoint != pinned.Endpoint || Required(info, "ID") != pinned.EngineId) { throw new InvalidOperationException("Docker authority changed; review is required."); }
    }
    private async Task<JsonElement> JsonAsync(string[] args, CancellationToken token)
    {
        DockerResult result = await docker.RunAsync(args, token).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.Truncated) { throw new IOException("Docker capability query failed."); }
        try { using JsonDocument document = JsonDocument.Parse(result.Output); return document.RootElement.Clone(); }
        catch (JsonException) { throw new NotSupportedException("Unknown Docker capability output."); }
    }
    private static string Required(JsonElement json, string name) => json.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
        ? value.GetString()! : throw new NotSupportedException("Missing Docker capability field: " + name);
}
