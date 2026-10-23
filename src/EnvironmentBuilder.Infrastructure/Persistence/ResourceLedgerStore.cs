using System.Text.Json;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using Microsoft.Data.Sqlite;

namespace EnvironmentBuilder.Infrastructure.Persistence;

public sealed class ResourceLedgerStore(SqliteWorker worker)
{
    public Task SaveAsync(OwnedResource resource, string? createIntent, string? deleteIntent, CancellationToken cancellationToken) =>
        worker.CommitAsync("ledger", (connection, transaction) =>
        {
            using SqliteCommand installation = Sql.Command(connection, transaction, "SELECT value FROM AppMetadata WHERE key='installationId'");
            if (!Equals(installation.ExecuteScalar(), resource.InstallationId.ToString())) { throw new InvalidOperationException("Resource installation identity mismatch."); }
            using SqliteCommand command = Sql.Command(connection, transaction, "SELECT json FROM ResourceLedger WHERE resourceId=$id", ("$id", resource.ResourceId.ToString()));
            if (command.ExecuteScalar() is string previous)
            {
                OwnedResource old = JsonSerializer.Deserialize<OwnedResource>(previous, CanonicalJson.Options)!;
                if (old.InstallationId != resource.InstallationId || old.EnvironmentId != resource.EnvironmentId || old.ComponentId != resource.ComponentId ||
                    old.DataGenerationId != resource.DataGenerationId || old.Kind != resource.Kind || old.Name != resource.Name || old.CreationRevision != resource.CreationRevision ||
                    old.OperationId != resource.OperationId || CanonicalJson.Hash(old.DesiredAttributes) != CanonicalJson.Hash(resource.DesiredAttributes) ||
                    (old.ActualDockerId is not null && old.ActualDockerId != resource.ActualDockerId) || (old.Volume is not null && CanonicalJson.Hash(old.Volume) != CanonicalJson.Hash(resource.Volume)))
                { throw new InvalidOperationException("A resource lifetime cannot be rebound to another incarnation."); }
            }
            Sql.Execute(connection, transaction,
                "INSERT INTO ResourceLedger VALUES($id,$installation,$environment,$component,$kind,$revision,$generation,$operation,$json,$unresolved,$create,$delete) ON CONFLICT(resourceId) DO UPDATE SET json=$json,unresolved=$unresolved,deleteIntent=COALESCE($delete,deleteIntent)",
                ("$id", resource.ResourceId.ToString()), ("$installation", resource.InstallationId.ToString()), ("$environment", resource.EnvironmentId.ToString()),
                ("$component", resource.ComponentId?.ToString()), ("$kind", resource.Kind.ToString().ToLowerInvariant()), ("$revision", resource.CreationRevision),
                ("$generation", resource.DataGenerationId.ToString()), ("$operation", resource.OperationId.ToString()), ("$json", JsonSerializer.Serialize(resource, CanonicalJson.Options)),
                ("$unresolved", resource.Unresolved), ("$create", createIntent), ("$delete", deleteIntent));
            return true;
        }, cancellationToken);

    public Task<IReadOnlyList<OwnedResource>> ReadAsync(Guid environmentId, CancellationToken cancellationToken) => worker.ReadAsync<IReadOnlyList<OwnedResource>>(connection =>
    {
        using SqliteCommand command = Sql.Command(connection, null, "SELECT json FROM ResourceLedger WHERE environmentId=$id", ("$id", environmentId.ToString()));
        using SqliteDataReader rows = command.ExecuteReader();
        var resources = new List<OwnedResource>();
        while (rows.Read()) { resources.Add(JsonSerializer.Deserialize<OwnedResource>(rows.GetString(0), CanonicalJson.Options)!); }
        return resources;
    }, cancellationToken);
}
