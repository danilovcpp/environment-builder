using System.Security.AccessControl;
using System.Security.Principal;
using EnvironmentBuilder.Infrastructure.Security;

namespace EnvironmentBuilder.Infrastructure.Persistence;

public sealed class AppDataDirectory
{
    public AppDataDirectory(string? explicitPath = null)
    {
        Root = LocalPathResolver.ValidateAbsoluteRoot(explicitPath ?? Environment.GetEnvironmentVariable("ENVIRONMENTBUILDER_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EnvironmentBuilder"));
        if (Path.GetPathRoot(Root) == Root) { throw new IOException("The data directory cannot be a drive root."); }
        CreatePrivate(Root);
        CreatePrivate(Path.Combine(Root, "blobs"));
        CreatePrivate(Path.Combine(Root, "staging"));
        CreatePrivate(Path.Combine(Root, "diagnostics"));
    }

    public string Root { get; }
    public string DatabasePath => Path.Combine(Root, "state.db");
    public string Child(params string[] parts)
    {
        if (parts.Any(Path.IsPathRooted)) { throw new IOException("Expected a private relative path."); }
        string path = Path.GetFullPath(Path.Combine([Root, .. parts]));
        LocalPathResolver.RequireWithin(path, Root);
        LocalPathResolver.RejectReparsePoints(path);
        return path;
    }

    public static void CreatePrivate(string path)
    {
        LocalPathResolver.RejectReparsePoints(path);
        DirectoryInfo directory = Directory.CreateDirectory(path);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetOwner(identity.User!);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }
}
