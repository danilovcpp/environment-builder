using System.Text.Json;
using System.Text.RegularExpressions;

namespace EnvironmentBuilder.Infrastructure.Docker;

public sealed record ResolvedImage(string Reference, string Identity, bool Local);
public sealed class ImagePreparation(IDockerProcess docker)
{
    public async Task<ResolvedImage> ResolveAsync(string reference, CancellationToken token)
    {
        DockerResult local = await docker.RunAsync(["image", "inspect", "--format", "{{json .}}", reference], token).ConfigureAwait(false);
        if (local.ExitCode == 0 && !local.Truncated)
        {
            using JsonDocument json = JsonDocument.Parse(local.Output);
            RequirePlatform(json.RootElement, "Os", "Architecture");
            return new(reference, RequireIdentity(json.RootElement.GetProperty("Id").GetString()!), true);
        }
        DockerResult remote = await docker.RunAsync(["manifest", "inspect", "--verbose", reference], token).ConfigureAwait(false);
        ResourceConsumerReader.Require(remote);
        using JsonDocument manifest = JsonDocument.Parse(remote.Output);
        JsonElement[] descriptors = manifest.RootElement.ValueKind == JsonValueKind.Array ? manifest.RootElement.EnumerateArray().ToArray() : [manifest.RootElement];
        JsonElement chosen = descriptors.Single(d =>
        {
            JsonElement platform = d.TryGetProperty("Descriptor", out JsonElement descriptor) ? descriptor.GetProperty("platform") : d.GetProperty("Platform");
            return platform.GetProperty("os").GetString() == "linux" && platform.GetProperty("architecture").GetString() == "amd64";
        });
        string digest = RequireIdentity(chosen.TryGetProperty("Descriptor", out JsonElement desc) ? desc.GetProperty("digest").GetString()! : chosen.GetProperty("Digest").GetString()!);
        string repository = reference.Split('@')[0];
        int slash = repository.LastIndexOf('/'); int colon = repository.LastIndexOf(':');
        if (colon > slash) { repository = repository[..colon]; }
        return new(reference, repository + "@" + digest, false);
    }
    public async Task<string> InspectPinnedAsync(string identity, CancellationToken token)
    {
        DockerResult result = await docker.RunAsync(["image", "inspect", "--format", "{{json .}}", identity], token).ConfigureAwait(false);
        ResourceConsumerReader.Require(result);
        using JsonDocument json = JsonDocument.Parse(result.Output);
        RequirePlatform(json.RootElement, "Os", "Architecture");
        return RequireIdentity(json.RootElement.GetProperty("Id").GetString()!);
    }
    public static string RequireIdentity(string identity) => Regex.IsMatch(identity, "^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ? identity : throw new IOException("Unknown image identity.");
    private static void RequirePlatform(JsonElement json, string os, string architecture)
    { if (json.GetProperty(os).GetString() != "linux" || json.GetProperty(architecture).GetString() != "amd64") { throw new NotSupportedException("Image platform must be linux/amd64."); } }
}
