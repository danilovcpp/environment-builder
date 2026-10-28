using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using EnvironmentBuilder.Core.Abstractions;

namespace EnvironmentBuilder.Infrastructure.Docker;

public sealed partial class DockerHubCatalog(HttpClient http) : IDockerHubCatalog, IDisposable
{
    private const string Hub = "https://hub.docker.com";

    public async Task<IReadOnlyList<DockerHubRepository>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        using var document = await ReadAsync($"/v2/search/repositories/?query={Uri.EscapeDataString(query)}&page_size=25", cancellationToken).ConfigureAwait(false);
        return document.RootElement.GetProperty("results").EnumerateArray()
            .Select(item => new DockerHubRepository(Text(item, "repo_name"), Text(item, "short_description"),
                item.TryGetProperty("is_official", out var official) && official.ValueKind == JsonValueKind.True, Logo(item)))
            .Where(item => item.Name.Length > 0).OrderByDescending(item => item.IsOfficial).ToArray();
    }

    public async Task<DockerHubRepository> GetRepositoryAsync(string name, CancellationToken cancellationToken)
    {
        using var document = await ReadAsync(RepositoryPath(name), cancellationToken).ConfigureAwait(false);
        var item = document.RootElement;
        string? logo = Logo(item);
        if (logo is null)
        {
            var match = MarkdownLogo().Match(Text(item, "full_description"));
            if (match.Success) { logo = RasterLogo(match.Groups[1].Value); }
        }
        return new DockerHubRepository(name, Text(item, "description"), Text(item, "namespace") == "library", logo);
    }

    public async Task<DockerHubTagPage> GetTagsAsync(string name, int page, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        using var document = await ReadAsync($"{RepositoryPath(name)}/tags?page_size=100&page={page}", cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        return new DockerHubTagPage(root.GetProperty("results").EnumerateArray().Select(item => Text(item, "name"))
            .Where(tag => tag.Length > 0).Distinct(StringComparer.Ordinal).ToArray(), !string.IsNullOrEmpty(Text(root, "next")));
    }

    private async Task<JsonDocument> ReadAsync(string path, CancellationToken token)
    {
        using var response = await http.GetAsync(Hub + path, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
    }

    private static string RepositoryPath(string name)
    {
        string[] parts = name.Split('/');
        if (parts.Length is < 1 or > 2 || parts.Any(part => !RepositoryPart().IsMatch(part)))
        { throw new ArgumentException("Invalid Docker Hub repository name.", nameof(name)); }
        return $"/v2/namespaces/{Uri.EscapeDataString(parts.Length == 1 ? "library" : parts[0])}/repositories/{Uri.EscapeDataString(parts[^1])}";
    }

    private static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static string? Logo(JsonElement item)
    {
        string? logo = RasterLogo(Text(item, "logo_url"));
        if (logo is null && item.TryGetProperty("logo", out var value) && value.ValueKind == JsonValueKind.Object)
        { logo = RasterLogo(Text(value, "small")) ?? RasterLogo(Text(value, "large")); }
        return logo;
    }
    private static string? RasterLogo(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        string.IsNullOrEmpty(uri.UserInfo) && new[] { ".png", ".jpg", ".jpeg", ".gif", ".ico", ".bmp" }.Any(extension => uri.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) ? value : null;

    [GeneratedRegex(@"^[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*$")] private static partial Regex RepositoryPart();
    [GeneratedRegex(@"!\[[^\]\r\n]*logo[^\]\r\n]*\]\((https://[^\s)]+)\)", RegexOptions.IgnoreCase)] private static partial Regex MarkdownLogo();
    public void Dispose() => http.Dispose();
}
