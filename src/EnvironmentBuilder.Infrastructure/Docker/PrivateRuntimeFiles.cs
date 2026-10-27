using System.Diagnostics;
using System.Text;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.Infrastructure.Security;

namespace EnvironmentBuilder.Infrastructure.Docker;

public sealed class PrivateRuntimeFiles : IAsyncDisposable
{
    private readonly string privateRoot;
    public PrivateRuntimeFiles(AppDataDirectory directory, Guid operation)
    {
        privateRoot = directory.Root;
        Root = directory.Child("staging", "private-" + operation.ToString("N"));
        AppDataDirectory.CreatePrivate(Root);
    }
    public string Root { get; }
    public string ComposeFile => Path.Combine(Root, "compose.json");
    public string EnvironmentFile => Path.Combine(Root, "empty.env");
    public async Task<string> WriteAsync(string name, string value, CancellationToken token)
    {
        string path = Path.GetFullPath(Path.Combine(Root, name));
        LocalPathResolver.RequireWithin(path, Root);
        LocalPathResolver.RejectReparsePoints(path);
        await File.WriteAllTextAsync(path, value, new UTF8Encoding(false), token).ConfigureAwait(false);
        return path;
    }
    public ValueTask DisposeAsync()
    {
        try
        {
            LocalPathResolver.RequireWithin(Root, privateRoot); LocalPathResolver.RejectReparsePoints(Root);
            if (Directory.Exists(Root)) { Directory.Delete(Root, true); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new UnresolvedEffectException("private-runtime-files-cleanup-failed"); }
        return ValueTask.CompletedTask;
    }
    public static async Task<IReadOnlyList<string>> CleanOrphansAsync(AppDataDirectory directory, IOperationStore journal, CancellationToken token)
    {
        var unresolved = (await journal.ReadUnresolvedAsync(token).ConfigureAwait(false)).Select(o => o.Id).ToHashSet();
        return await Task.Run<IReadOnlyList<string>>(() =>
        {
            var warnings = new List<string>();
            Process[] clients = Process.GetProcessesByName("docker");
            bool clientActive = clients.Length > 0;
            foreach (Process client in clients) { client.Dispose(); }
            foreach (string root in Directory.EnumerateDirectories(directory.Child("staging"), "private-*"))
            {
                token.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(Path.GetFileName(root)["private-".Length..], "N", out Guid operation)) { warnings.Add("private-files-unknown-owner"); continue; }
                if (clientActive || unresolved.Contains(operation)) { warnings.Add("private-files-await-reconciliation"); continue; }
                try { LocalPathResolver.RequireWithin(root, directory.Root); Check(root); Directory.Delete(root, true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { warnings.Add("private-files-cleanup-failed"); }
            }
            return warnings;
            static void Check(string path)
            {
                LocalPathResolver.RejectReparsePoints(path);
                foreach (string child in Directory.EnumerateFileSystemEntries(path))
                { LocalPathResolver.RejectReparsePoints(child); if (Directory.Exists(child)) { Check(child); } }
            }
        }, token).ConfigureAwait(false);
    }
}
