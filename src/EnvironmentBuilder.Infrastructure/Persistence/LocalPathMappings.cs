using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Infrastructure.Security;

namespace EnvironmentBuilder.Infrastructure.Persistence;

public sealed class LocalPathMappings(SqliteWorker worker, IClock clock) : ILocalPathMappings
{
    public Task<IReadOnlyList<LocalPathMapping>> ReadAsync(CancellationToken cancellationToken) => worker.ReadAsync<IReadOnlyList<LocalPathMapping>>(connection =>
    {
        using var command = Sql.Command(connection, null, "SELECT alias,absolutePath,verifiedAt FROM LocalPathMappings ORDER BY alias");
        using var rows = command.ExecuteReader();
        var result = new List<LocalPathMapping>();
        while (rows.Read()) { result.Add(new LocalPathMapping(rows.GetString(0), rows.GetString(1), rows.IsDBNull(2) ? null : DateTimeOffset.Parse(rows.GetString(2), System.Globalization.CultureInfo.InvariantCulture))); }
        return result;
    }, cancellationToken);

    public Task MapAsync(string reference, string absolutePath, CancellationToken cancellationToken)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(reference, "^local-path://[a-zA-Z0-9._-]{1,128}\\z")) { throw new DocumentException("/pathRef", "invalid-local-path-reference"); }
        string root = LocalPathResolver.ValidateAbsoluteRoot(absolutePath);
        if (!Directory.Exists(root) && !File.Exists(root)) { throw new FileNotFoundException("Selected input is unavailable."); }
        return worker.CommitAsync("path-mapping", (connection, transaction) => Sql.Execute(connection, transaction,
            "INSERT INTO LocalPathMappings VALUES($alias,$path,$at) ON CONFLICT(alias) DO UPDATE SET absolutePath=$path,verifiedAt=$at",
            ("$alias", reference[13..]), ("$path", root), ("$at", Sql.Instant(clock.UtcNow))), cancellationToken);
    }
}
