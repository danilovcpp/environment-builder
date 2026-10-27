using System.Collections.Immutable;
using System.Security.Cryptography;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.Infrastructure.Security;

namespace EnvironmentBuilder.Infrastructure.Docker;

public sealed class RuntimeInputs(string root, string privateRoot, ImmutableDictionary<string, string> paths, PreviewSnapshot snapshot) : IAsyncDisposable
{
    private bool retained;
    public string Root { get; } = root;
    public ImmutableDictionary<string, string> Paths { get; } = paths;
    public PreviewSnapshot Snapshot { get; } = snapshot;
    // Unknown acceptance also retains mounts: cleanup must wait for exact container absence.
    public void RetainForContainers() => retained = true;
    public ValueTask DisposeAsync()
    {
        if (!retained && Directory.Exists(Root))
        { LocalPathResolver.RequireWithin(Root, privateRoot); LocalPathResolver.RejectReparsePoints(Root); Directory.Delete(Root, true); }
        return ValueTask.CompletedTask;
    }
}

public sealed class RuntimeInputCapture(AppDataDirectory directory, ILocalPathMappings mappings, LocalPathResolver resolver, PreviewInputCapture preview)
{
    public async Task<RuntimeInputs> CaptureAsync(IsolatedEnvironment environment, IReadOnlyDictionary<string, string> images, CancellationToken token)
    {
        string root = directory.Child("staging", "runtime-" + Guid.NewGuid().ToString("N"));
        var paths = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var resolved = (await mappings.ReadAsync(token).ConfigureAwait(false)).ToList();
        AppDataDirectory.CreatePrivate(root);
        try
        {
            PreviewSnapshot before = await preview.CaptureAsync(environment, images, token).ConfigureAwait(false);
            foreach (EnvironmentComponent component in environment.Components)
            {
                if (component.Source is BuildSource build)
                {
                    string source = resolver.Resolve(new(build.PathRef), resolved, build.ContextSubdirectory, true);
                    string destination = Path.Combine(root, component.Id.ToString("N"), "context");
                    await CopyAsync(source, destination, token).ConfigureAwait(false);
                    paths.Add(component.Id + ":context:" + build.PathRef + "/" + build.ContextSubdirectory, destination);
                    string hash = await PreviewInputCapture.HashTreeAsync(destination, token).ConfigureAwait(false);
                    if (before.Inputs.Single(i => i.Reference == component.Id + ":build:" + build.PathRef).ContentHash != hash) { throw new IOException("Source changed while being captured."); }
                }
                foreach (ReadOnlyInputStorage input in component.Storage.OfType<ReadOnlyInputStorage>())
                {
                    string source = resolver.Resolve(new(input.PathRef), resolved, ".", false);
                    string key = component.Id + ":input:" + input.PathRef;
                    if (paths.ContainsKey(key)) { continue; }
                    string destination = Path.Combine(root, component.Id.ToString("N"), "inputs", input.Id);
                    await CopyAsync(source, destination, token).ConfigureAwait(false);
                    paths.Add(key, destination);
                    string hash = await PreviewInputCapture.HashTreeAsync(destination, token).ConfigureAwait(false);
                    if (before.Inputs.Single(i => i.Reference == component.Id + ":input:" + input.Id + ":" + input.TargetPath).ContentHash != hash) { throw new IOException("Read-only input changed during capture."); }
                }
            }
            PreviewSnapshot after = await preview.CaptureAsync(environment, images, token).ConfigureAwait(false);
            if (before.Token != after.Token || after.Inputs.Any(i => i.State.StartsWith("unknown:", StringComparison.Ordinal))) { throw new IOException("Inputs unavailable or changed during capture."); }
            return new(root, directory.Root, paths.ToImmutable(), after);
        }
        catch { LocalPathResolver.RequireWithin(root, directory.Root); LocalPathResolver.RejectReparsePoints(root); Directory.Delete(root, true); throw; }
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken token)
    {
        LocalPathResolver.RejectReparsePoints(source);
        if (Directory.Exists(source))
        {
            AppDataDirectory.CreatePrivate(destination);
            string[] before = Directory.GetFileSystemEntries(source).Order(StringComparer.Ordinal).ToArray();
            foreach (string entry in before) { await CopyAsync(entry, Path.Combine(destination, Path.GetFileName(entry)), token).ConfigureAwait(false); }
            if (!before.SequenceEqual(Directory.GetFileSystemEntries(source).Order(StringComparer.Ordinal), StringComparer.Ordinal)) { throw new IOException("Input tree changed during copy."); }
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            await input.CopyToAsync(output, token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false);
        }
        LocalPathResolver.RejectReparsePoints(source);
    }
}
