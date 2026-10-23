namespace EnvironmentBuilder.IntegrationTests.Support;

public sealed class IsolatedTestStore : IDisposable
{
    private readonly HashSet<Guid> environmentIds = [];
    private readonly string parentDirectory;
    private bool disposed;

    private IsolatedTestStore(string parentDirectory)
    {
        this.parentDirectory = Path.GetFullPath(parentDirectory);
        string productionDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EnvironmentBuilder");
        if (IsWithin(this.parentDirectory, productionDirectory))
        {
            throw new ArgumentException("Test stores cannot be created in the application data directory.", nameof(parentDirectory));
        }

        InstallationId = Guid.NewGuid();
        DataDirectory = Path.Combine(this.parentDirectory, $"test-{InstallationId:N}");
        Directory.CreateDirectory(DataDirectory);
    }

    public Guid InstallationId { get; }
    public string DataDirectory { get; }

    public static IsolatedTestStore Create(string? parentDirectory = null) =>
        new(parentDirectory ?? Path.Combine(Path.GetTempPath(), "EnvironmentBuilder.Tests"));

    public Guid CreateEnvironmentId()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Guid id = Guid.NewGuid();
        environmentIds.Add(id);
        return id;
    }

    public bool OwnsEnvironment(Guid id)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return environmentIds.Contains(id);
    }

    public string GetPath(params string[] parts)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (parts.Length == 0 || parts.Any(part => string.IsNullOrWhiteSpace(part) || Path.IsPathRooted(part)))
        {
            throw new ArgumentException("A test path must be relative to its store.", nameof(parts));
        }

        string path = Path.GetFullPath(Path.Combine([DataDirectory, .. parts]));
        if (!IsWithin(path, DataDirectory) || path.Equals(DataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A test path cannot escape its store.", nameof(parts));
        }

        return path;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        string resolved = Path.GetFullPath(DataDirectory);
        if (!IsWithin(resolved, parentDirectory) ||
            !Path.GetFileName(resolved).Equals($"test-{InstallationId:N}", StringComparison.Ordinal))
        {
            throw new IOException("Refusing cleanup outside the exact isolated test store.");
        }

        if (Directory.Exists(resolved))
        {
            EnsureNoReparsePoints(new DirectoryInfo(resolved));
            Directory.Delete(resolved, recursive: true);
        }

        disposed = true;
    }

    private static bool IsWithin(string path, string root) =>
        path.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static void EnsureNoReparsePoints(DirectoryInfo directory)
    {
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Refusing recursive cleanup through a reparse point.");
        }

        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Refusing recursive cleanup of a store containing reparse points.");
            }

            if (entry is DirectoryInfo child)
            {
                EnsureNoReparsePoints(child);
            }
        }
    }
}
