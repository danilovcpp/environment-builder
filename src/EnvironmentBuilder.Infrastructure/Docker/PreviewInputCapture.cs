using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Infrastructure.Security;

namespace EnvironmentBuilder.Infrastructure.Docker;

public sealed class PreviewInputCapture(ILocalPathMappings mappings, LocalPathResolver paths) : IPreviewInputCapture
{
    // Only explicitly supplied observations may resolve images; this class has no process/network boundary.
    public Task<PreviewSnapshot> CaptureAsync(IsolatedEnvironment environment, CancellationToken cancellationToken) => CaptureAsync(environment, ImmutableDictionary<string, string>.Empty, cancellationToken);

    public async Task<PreviewSnapshot> CaptureAsync(IsolatedEnvironment environment, IReadOnlyDictionary<string, string> knownImages, CancellationToken cancellationToken)
    {
        EnvironmentValidator.Validate(environment);
        IReadOnlyList<LocalPathMapping> local = await mappings.ReadAsync(cancellationToken).ConfigureAwait(false);
        // Enumeration and hashing can be CPU-heavy. Keep them off the UI thread as well as using async reads.
        return await Task.Run(async () =>
        {
            var inputs = ImmutableArray.CreateBuilder<PreviewInput>();
            foreach (EnvironmentComponent component in environment.Components.OrderBy(c => c.Id))
            {
                if (component.Source is ImageSource image)
                {
                    bool known = knownImages.TryGetValue(image.Reference, out string? identity);
                    inputs.Add(new PreviewInput(component.Id + ":image:" + image.Reference, known ? "known" : "unknown", null, identity));
                }
                if (component.Source is BuildSource build)
                {
                    await Capture(component.Id + ":build:" + build.PathRef, build.PathRef, build.ContextSubdirectory, true).ConfigureAwait(false);
                    string relative = build.ContextSubdirectory == "." ? build.Dockerfile : build.ContextSubdirectory + "/" + build.Dockerfile;
                    await Capture(component.Id + ":dockerfile:" + build.Dockerfile, build.PathRef, relative, false).ConfigureAwait(false);
                }
                foreach (ReadOnlyInputStorage storage in component.Storage.OfType<ReadOnlyInputStorage>().OrderBy(s => s.Id, StringComparer.Ordinal))
                { await Capture(component.Id + ":input:" + storage.Id + ":" + storage.TargetPath, storage.PathRef, ".", false).ConfigureAwait(false); }
            }
            JsonElement json = JsonSerializer.SerializeToElement(environment, CanonicalJson.Options);
            var secrets = new SortedSet<string>(StringComparer.Ordinal);
            Collect(json);
            return new PreviewSnapshot(environment.Id, environment.Revision, CanonicalJson.Hash(environment), Encoding.UTF8.GetString(CanonicalJson.Encode(json)), "compose-v1",
                CanonicalJson.Hash(environment.Baseline), environment.Baseline.Applications.Select(a => CanonicalJson.Hash(a.Initializer)).ToImmutableArray(),
                environment.Baseline.Applications.Where(a => a.SeedPack is not null).Select(a => CanonicalJson.Hash(a.SeedPack)).ToImmutableArray(), secrets.ToImmutableArray(), inputs.ToImmutable());

            async Task Capture(string key, string reference, string relative, bool directory)
            {
                try
                {
                    string path = paths.Resolve(new LocalPathReference(reference), local, relative, directory);
                    inputs.Add(new PreviewInput(key, "known", await HashTreeAsync(path, cancellationToken).ConfigureAwait(false), null));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { inputs.Add(new PreviewInput(key, "unknown:" + (error is FileNotFoundException ? "missing" : "unavailable-or-changing"), null, null)); }
            }
            void Collect(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString()!.StartsWith("secret://", StringComparison.Ordinal)) { secrets.Add(property.Value.GetString()!); }
                        Collect(property.Value);
                    }
                }
                else if (element.ValueKind == JsonValueKind.Array) { foreach (JsonElement item in element.EnumerateArray()) { Collect(item); } }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> HashTreeAsync(string root, CancellationToken cancellationToken)
    {
        LocalPathResolver.RejectReparsePoints(root);
        if (File.Exists(root)) { return await HashFileAsync(root, cancellationToken).ConfigureAwait(false); }
        string[] before = Enumerate(root).ToArray();
        var metadata = before.ToDictionary(path => path, path => { var info = new FileInfo(path); return (info.Length, info.LastWriteTimeUtc); }, StringComparer.Ordinal);
        var entries = new List<object>();
        foreach (string path in before)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(new { path = Path.GetRelativePath(root, path).Replace('\\', '/'), hash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false) });
        }
        if (!before.SequenceEqual(Enumerate(root), StringComparer.Ordinal)) { throw new IOException("Input tree changed during capture."); }
        foreach (string path in before)
        {
            LocalPathResolver.RejectReparsePoints(path);
            var info = new FileInfo(path);
            if (metadata[path] != (info.Length, info.LastWriteTimeUtc)) { throw new IOException("Previously captured input changed during capture."); }
        }
        return CanonicalJson.Hash(entries);
    }
    private static IEnumerable<string> Enumerate(string root)
    {
        var files = new List<string>();
        Visit(root);
        return files.Order(StringComparer.Ordinal);
        void Visit(string directory)
        {
            LocalPathResolver.RejectReparsePoints(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                LocalPathResolver.RejectReparsePoints(path);
                if (Directory.Exists(path)) { Visit(path); }
                else { files.Add(path); }
            }
        }
    }
    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        LocalPathResolver.RejectReparsePoints(path);
        var before = new FileInfo(path);
        long length = before.Length;
        DateTime written = before.LastWriteTimeUtc;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
        var after = new FileInfo(path);
        if (after.Length != length || after.LastWriteTimeUtc != written) { throw new IOException("Input changed during capture."); }
        return hash;
    }
}
