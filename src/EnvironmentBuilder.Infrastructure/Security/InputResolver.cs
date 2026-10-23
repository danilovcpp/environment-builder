using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Infrastructure.Persistence;

namespace EnvironmentBuilder.Infrastructure.Security;

public sealed class InputResolver(SqliteWorker worker, LocalPathResolver paths, ISecretVault vault, IClock clock) : IInputResolver
{
    public async Task<IReadOnlyList<ReadinessIssue>> CheckAsync(IReadOnlyList<InputReference> references, CancellationToken cancellationToken)
    {
        List<LocalPathMapping> mappings = await worker.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT alias,absolutePath FROM LocalPathMappings";
            using var rows = command.ExecuteReader();
            var result = new List<LocalPathMapping>();
            while (rows.Read()) { result.Add(new LocalPathMapping(rows.GetString(0), rows.GetString(1), null)); }
            return result;
        }, cancellationToken).ConfigureAwait(false);
        var issues = new List<ReadinessIssue>();
        foreach (InputReference reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? code = null;
            switch (reference.Kind)
            {
                case InputKind.Image: code = "image-identity-unknown"; break;
                case InputKind.Secret:
                    using (SecretValue? value = await vault.ResolveAsync(reference.Reference, cancellationToken).ConfigureAwait(false))
                    { if (value is null) { code = "missing-secret"; } }
                    break;
                case InputKind.SourcePath:
                case InputKind.ReadOnlyInput:
                case InputKind.Dockerfile:
                    try
                    {
                        int slash = reference.Reference.IndexOf('/', "local-path://".Length);
                        string pathRef = slash < 0 ? reference.Reference : reference.Reference[..slash];
                        string relative = slash < 0 ? "." : reference.Reference[(slash + 1)..];
                        if (relative.StartsWith("./", StringComparison.Ordinal)) { relative = relative[2..]; }
                        paths.Resolve(new LocalPathReference(pathRef), mappings, relative, reference.Kind == InputKind.SourcePath);
                    }
                    catch (IOException) { code = "missing-or-unsafe-path"; }
                    catch (UnauthorizedAccessException) { code = "path-access-denied"; }
                    break;
            }
            if (code is not null) { issues.Add(new ReadinessIssue(code, reference.FieldPath, "Input requires explicit resolution before runtime.", clock.UtcNow)); }
        }
        return issues;
    }
}
