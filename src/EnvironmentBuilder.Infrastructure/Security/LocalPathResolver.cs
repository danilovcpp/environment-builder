using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Infrastructure.Security;

public sealed class LocalPathResolver
{
    public string Resolve(LocalPathReference reference, IEnumerable<LocalPathMapping> mappings, string relative = ".", bool requireDirectory = false)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(reference.Reference, "^local-path://[a-zA-Z0-9._-]{1,128}\\z"))
        { throw new DocumentException("/pathRef", "invalid-local-path-reference"); }
        string alias = reference.Reference[13..];
        LocalPathMapping? mapping = mappings.SingleOrDefault(m => m.Alias == alias);
        if (mapping is null) { throw new FileNotFoundException("Local path mapping is unresolved."); }
        string root = ValidateAbsoluteRoot(mapping.AbsolutePath);
        EnvironmentValidator.SafeRelativePath(relative, "/path", true);
        string target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        RequireWithin(target, root);
        RejectReparsePoints(target);
        if (!File.Exists(target) && !Directory.Exists(target)) { throw new FileNotFoundException("Mapped input is unavailable."); }
        if (requireDirectory && !Directory.Exists(target)) { throw new IOException("A directory input is required."); }
        return target;
    }

    public static string ValidateAbsoluteRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            path[2..].Contains(':') || path.Any(char.IsControl))
        { throw new IOException("A local absolute path without alternate streams is required."); }
        string absolute = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        RejectReparsePoints(absolute);
        return absolute;
    }

    public static void RequireWithin(string target, string root)
    {
        if (!target.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !target.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        { throw new IOException("Path escapes its private root."); }
    }

    public static void RejectReparsePoints(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            { throw new IOException("Reparse points are not allowed in managed paths."); }
            current = Path.GetDirectoryName(current);
        }
    }
}
