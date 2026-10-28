using System.Text;
using System.Text.Json;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Imports;
using Microsoft.Data.Sqlite;

namespace EnvironmentBuilder.Infrastructure.Persistence;

public sealed class EnvironmentStore(SqliteWorker worker, StrictDocumentReader reader, IClock clock) : IEnvironmentStore
{
    public Task SaveNewAsync(IsolatedEnvironment environment, CancellationToken cancellationToken)
    {
        EnvironmentValidator.Validate(environment);
        if (environment.Revision != 1) { throw new DocumentException("/revision", "new-environment-must-start-at-one"); }
        string json = JsonSerializer.Serialize(environment, CanonicalJson.Options);
        string hash = CanonicalJson.Hash(environment);
        return worker.CommitAsync("environment", (connection, transaction) =>
        {
            using SqliteCommand existing = Sql.Command(connection, transaction, "SELECT hash FROM Environments WHERE id=$id", ("$id", environment.Id.ToString()));
            object? savedHash = existing.ExecuteScalar();
            if (savedHash is not null)
            {
                if (Equals(savedHash, hash)) { return false; }
                throw new InvalidOperationException("Existing environments require the confirmed edit workflow.");
            }
            Guid generation = Guid.NewGuid();
            Sql.Execute(connection, transaction,
                "INSERT INTO Environments(id,revision,json,hash,name,description,componentTypes,generationId,savedAt) VALUES($id,1,$json,$hash,$name,$description,$types,$generation,$at)",
                ("$id", environment.Id.ToString()), ("$json", json), ("$hash", hash), ("$name", environment.Name), ("$description", environment.Description),
                ("$types", string.Join(',', environment.Components.Select(c => c.DefinitionSnapshot.ComponentType).Distinct())), ("$generation", generation.ToString()), ("$at", Sql.Instant(clock.UtcNow)));
            Sql.Execute(connection, transaction,
                "INSERT INTO DataGenerations(id,environmentId,baselineHash,validity,allocatedAt) VALUES($id,$environment,$hash,'active',$at)",
                ("$id", generation.ToString()), ("$environment", environment.Id.ToString()), ("$hash", CanonicalJson.Hash(environment.Baseline)), ("$at", Sql.Instant(clock.UtcNow)));
            SaveReferences(connection, transaction, "environment", environment.Id, environment.Baseline.Applications);
            return true;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<EnvironmentListItem>> ListAsync(CancellationToken cancellationToken) => worker.ReadAsync<IReadOnlyList<EnvironmentListItem>>(connection =>
    {
        using SqliteCommand command = Sql.Command(connection, null,
            "SELECT id,name,revision,json,EXISTS(SELECT 1 FROM Operations o WHERE o.environmentId=e.id AND (o.state NOT IN ('Succeeded','Failed','Cancelled') OR EXISTS(SELECT 1 FROM OperationSteps s WHERE s.operationId=o.id AND s.state IN ('Started','Unknown')) OR EXISTS(SELECT 1 FROM ResourceLedger r WHERE r.operationId=o.id AND r.unresolved=1))),hash,savedAt,deletionState,(SELECT json FROM Operations o WHERE o.environmentId=e.id AND o.kind='delete' AND o.confirmationHash IS NOT NULL ORDER BY createdAt DESC,rowid DESC LIMIT 1) FROM Environments e WHERE deletionState<>'closed' ORDER BY savedAt DESC,id");
        using SqliteDataReader rows = command.ExecuteReader();
        var items = new List<EnvironmentListItem>();
        while (rows.Read())
        {
            IsolatedEnvironment? document = null;
            string? error = null;
            try
            {
                document = reader.Read<IsolatedEnvironment>(Encoding.UTF8.GetBytes(rows.GetString(3)), "environment");
                if (document.Id.ToString() != rows.GetString(0) || document.Revision != rows.GetInt64(2))
                { document = null; error = "row-identity-mismatch"; }
                else if (CanonicalJson.Hash(document) != rows.GetString(5) || document.Name != rows.GetString(1))
                { document = null; error = "row-hash-or-summary-mismatch"; }
            }
            catch (DocumentException failure) { error = failure.Code; }
            items.Add(new EnvironmentListItem(Guid.Parse(rows.GetString(0)), rows.GetString(1), rows.GetInt64(2), document, error, rows.GetBoolean(4) || rows.GetString(7) == "deleting")
            {
                SavedAt = DateTimeOffset.Parse(rows.GetString(6), System.Globalization.CultureInfo.InvariantCulture),
                Deletion = rows.GetString(7) == "deleting" && document is not null && !rows.IsDBNull(8)
                    ? new DeletionTombstone(document, JsonSerializer.Deserialize<Operation>(rows.GetString(8), CanonicalJson.Options)!) : null
            });
        }
        return items;
    }, cancellationToken);

    internal static void SaveReferences(SqliteConnection connection, SqliteTransaction transaction, string ownerKind, Guid owner, IEnumerable<BaselineApplication> applications)
    {
        foreach (BaselineApplication application in applications.Where(a => a.SeedPack is not null))
        { AddReference(connection, transaction, ownerKind, owner, CanonicalJson.Hash(application), application.SeedPack!); }
    }

    internal static void AddReference(SqliteConnection connection, SqliteTransaction transaction, string ownerKind, Guid owner, string applicationId, SeedPackReference reference) =>
        Sql.Execute(connection, transaction, "INSERT OR IGNORE INTO PackageReferences VALUES($kind,$owner,$application,$pack,$version,$hash)",
            ("$kind", ownerKind), ("$owner", owner.ToString()), ("$application", applicationId), ("$pack", reference.Id.ToString()), ("$version", reference.Version), ("$hash", reference.ContentHash));
}
