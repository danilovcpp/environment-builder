using System.Text;
using System.Text.Json;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Infrastructure.Imports;
using Microsoft.Data.Sqlite;

namespace EnvironmentBuilder.Infrastructure.Persistence;

public sealed record ComponentLibraryItem(Guid Id, string Version, ComponentDefinition? Document, string? ErrorCode);
public sealed class ComponentLibraryStore(SqliteWorker worker, StrictDocumentReader reader)
{
    public Task SaveAsync(ComponentDefinition definition, CancellationToken cancellationToken) => SaveAsync(definition, null, cancellationToken);
    public Task SaveAsync(ComponentDefinition definition, bool? enabled, CancellationToken cancellationToken)
    {
        EnvironmentValidator.ValidateDefinition(definition);
        string json = JsonSerializer.Serialize(definition, CanonicalJson.Options);
        string hash = CanonicalJson.Hash(definition);
        return worker.CommitAsync("component", (connection, transaction) =>
        {
            using SqliteCommand command = Sql.Command(connection, transaction, "SELECT hash FROM ComponentDefinitions WHERE definitionId=$id AND version=$version", ("$id", definition.Id.ToString()), ("$version", definition.Version));
            object? existing = command.ExecuteScalar();
            if (existing is not null && !Equals(existing, hash)) { throw new DocumentException("/version", "immutable-version-conflict"); }
            Sql.Execute(connection, transaction, "INSERT OR IGNORE INTO ComponentDefinitions VALUES($id,$version,$hash,$json)",
                ("$id", definition.Id.ToString()), ("$version", definition.Version), ("$hash", hash), ("$json", json));
            if (enabled is not null)
            {
                Sql.Execute(connection, transaction, "INSERT INTO ComponentLibrarySelections VALUES($id,$version,$enabled) ON CONFLICT(definitionId) DO UPDATE SET version=$version,enabled=$enabled",
                    ("$id", definition.Id.ToString()), ("$version", definition.Version), ("$enabled", enabled.Value ? 1 : 0));
            }
            return true;
        }, cancellationToken);
    }

    public Task<IReadOnlyDictionary<Guid, (string Version, bool Enabled)>> ReadSelectionsAsync(CancellationToken cancellationToken) => worker.ReadAsync<IReadOnlyDictionary<Guid, (string, bool)>>(connection =>
    {
        using var command = Sql.Command(connection, null, "SELECT definitionId,version,enabled FROM ComponentLibrarySelections");
        using var rows = command.ExecuteReader();
        var result = new Dictionary<Guid, (string, bool)>();
        while (rows.Read()) { result.Add(Guid.Parse(rows.GetString(0)), (rows.GetString(1), rows.GetInt32(2) != 0)); }
        return result;
    }, cancellationToken);

    public Task<IReadOnlyList<ComponentLibraryItem>> ListAsync(CancellationToken cancellationToken) => worker.ReadAsync<IReadOnlyList<ComponentLibraryItem>>(connection =>
    {
        using SqliteCommand command = Sql.Command(connection, null, "SELECT definitionId,version,json,hash FROM ComponentDefinitions ORDER BY definitionId,version");
        using SqliteDataReader rows = command.ExecuteReader();
        var result = new List<ComponentLibraryItem>();
        while (rows.Read())
        {
            ComponentDefinition? document = null;
            string? error = null;
            try
            {
                document = reader.Read<ComponentDefinition>(Encoding.UTF8.GetBytes(rows.GetString(2)), "component");
                if (document.Id.ToString() != rows.GetString(0) || document.Version != rows.GetString(1)) { document = null; error = "row-identity-mismatch"; }
                else if (CanonicalJson.Hash(document) != rows.GetString(3)) { document = null; error = "row-hash-mismatch"; }
            }
            catch (DocumentException failure) { error = failure.Code; }
            result.Add(new ComponentLibraryItem(Guid.Parse(rows.GetString(0)), rows.GetString(1), document, error));
        }
        return result;
    }, cancellationToken);
}
