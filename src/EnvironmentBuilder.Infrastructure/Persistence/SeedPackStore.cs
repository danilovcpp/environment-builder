using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Imports;
using Microsoft.Data.Sqlite;

namespace EnvironmentBuilder.Infrastructure.Persistence;

public sealed class SeedPackStore(SqliteWorker worker, ContentBlobStore blobs, StrictDocumentReader reader, IClock clock) : ISeedPackStore, ISeedPackCatalog
{
    // Metadata-only presentation read; opening the library never acquires payloads or runs an initializer.
    public async Task<IReadOnlyList<SeedPack>> ListMetadataAsync(CancellationToken cancellationToken)
    {
        string[] documents = await worker.ReadAsync(connection =>
        {
            using SqliteCommand command = Sql.Command(connection, null, "SELECT json FROM SeedPacks ORDER BY packId,version");
            using SqliteDataReader rows = command.ExecuteReader();
            var result = new List<string>();
            while (rows.Read()) { result.Add(rows.GetString(0)); }
            return result.ToArray();
        }, cancellationToken).ConfigureAwait(false);
        return documents.Select(json => reader.Read<SeedPack>(Encoding.UTF8.GetBytes(json), "seedpack")).ToArray();
    }
    internal Task CommitAsync(SeedPack manifest, IReadOnlyList<ImportedPackEntry> entries, CancellationToken cancellationToken) => worker.CommitAsync("pack", (connection, transaction) =>
    {
        EnvironmentValidator.ValidatePack(manifest);
        using SqliteCommand previous = Sql.Command(connection, transaction, "SELECT contentHash FROM SeedPacks WHERE packId=$id AND version=$version", ("$id", manifest.Id.ToString()), ("$version", manifest.Version));
        object? existing = previous.ExecuteScalar();
        if (existing is not null)
        {
            if (!Equals(existing, manifest.ContentHash)) { throw new DocumentException("/version", "immutable-version-conflict"); }
            return false;
        }
        Sql.Execute(connection, transaction, "INSERT INTO SeedPacks VALUES($id,$version,$hash,$json,$at)", ("$id", manifest.Id.ToString()), ("$version", manifest.Version),
            ("$hash", manifest.ContentHash), ("$json", JsonSerializer.Serialize(manifest, CanonicalJson.Options)), ("$at", Sql.Instant(clock.UtcNow)));
        foreach (ImportedPackEntry entry in entries)
        {
            Sql.Execute(connection, transaction, "INSERT OR IGNORE INTO ContentBlobs VALUES($hash,$path,$bytes)", ("$hash", entry.Blob.Hash), ("$path", "blobs/" + entry.Blob.Hash), ("$bytes", entry.Blob.Bytes));
            Sql.Execute(connection, transaction, "INSERT INTO PackEntries VALUES($id,$version,$path,$section,$hash,$bytes,$records,$index)",
                ("$id", manifest.Id.ToString()), ("$version", manifest.Version), ("$path", entry.Path), ("$section", entry.SectionId), ("$hash", entry.Blob.Hash), ("$bytes", entry.Blob.Bytes), ("$records", entry.RecordCount), ("$index", entry.IndexPath));
        }
        return true;
    }, cancellationToken);

    public async Task<VerifiedSeedPack?> FindAsync(SeedPackReference reference, CancellationToken cancellationToken)
    {
        string? json = await worker.ReadAsync(connection =>
        {
            using SqliteCommand command = Sql.Command(connection, null, "SELECT json FROM SeedPacks WHERE packId=$id AND version=$version", ("$id", reference.Id.ToString()), ("$version", reference.Version));
            return command.ExecuteScalar() as string;
        }, cancellationToken).ConfigureAwait(false);
        if (json is null) { return null; }
        SeedPack manifest = reader.Read<SeedPack>(Encoding.UTF8.GetBytes(json), "seedpack");
        if (manifest.Id != reference.Id || manifest.Version != reference.Version || manifest.ContentHash != reference.ContentHash) { throw new DocumentException("/seedPack", "pack-version-content-conflict"); }
        var initializers = ImmutableDictionary.CreateBuilder<string, InitializationDefinition>(StringComparer.Ordinal);
        foreach (SeedSection section in manifest.Sections)
        { using BlobLease lease = await blobs.AcquireAsync(section.Sha256, cancellationToken).ConfigureAwait(false); }
        foreach (InitializerEntry entry in manifest.Initializers)
        {
            using BlobLease lease = await blobs.AcquireAsync(entry.Sha256, cancellationToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            await lease.Stream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            initializers.Add(entry.Path, reader.Read<InitializationDefinition>(output.ToArray(), "initialization"));
        }
        return new VerifiedSeedPack(manifest, initializers.ToImmutable());
    }

    public Task<IReadOnlyList<SeedPackOwner>> ReadOwnersAsync(SeedPackReference reference, CancellationToken token) =>
        worker.ReadAsync(connection => ReadOwners(connection, null, reference), token);

    private static IReadOnlyList<SeedPackOwner> ReadOwners(SqliteConnection connection, SqliteTransaction? transaction, SeedPackReference reference)
    {
        using var command = Sql.Command(connection, transaction,
            "SELECT DISTINCT r.ownerKind,r.ownerId,COALESCE(e.name,oe.name,r.ownerId) FROM PackageReferences r LEFT JOIN Environments e ON r.ownerKind='environment' AND e.id=r.ownerId LEFT JOIN Operations o ON r.ownerKind='operation' AND o.id=r.ownerId LEFT JOIN Environments oe ON oe.id=o.environmentId WHERE r.packId=$id AND r.version=$version ORDER BY r.ownerKind,r.ownerId",
            ("$id", reference.Id.ToString()), ("$version", reference.Version));
        using var rows = command.ExecuteReader();
        var owners = new List<SeedPackOwner>();
        while (rows.Read()) { owners.Add(new(rows.GetString(0), Guid.Parse(rows.GetString(1)), rows.GetString(2))); }
        return owners;
    }

    public async Task RemoveAsync(SeedPackReference reference, CancellationToken cancellationToken)
    {
        await blobs.WithFileLockAsync(() => worker.CommitAsync("pack-delete", (connection, transaction) =>
        {
            using SqliteCommand actual = Sql.Command(connection, transaction, "SELECT contentHash FROM SeedPacks WHERE packId=$id AND version=$version", ("$id", reference.Id.ToString()), ("$version", reference.Version));
            if (actual.ExecuteScalar() is not string hash || hash != reference.ContentHash) { throw new InvalidOperationException("Exact package identity is required for deletion."); }
            IReadOnlyList<SeedPackOwner> owners = ReadOwners(connection, transaction, reference);
            if (owners.Count != 0) { throw new SeedPackInUseException(owners); }
            using (var entries = Sql.Command(connection, transaction, "SELECT blobHash FROM PackEntries WHERE packId=$id AND version=$version", ("$id", reference.Id.ToString()), ("$version", reference.Version)))
            using (var rows = entries.ExecuteReader())
            {
                while (rows.Read()) { if (blobs.IsLeased(rows.GetString(0))) { throw new SeedPackInUseException([new("lease", reference.Id, "Активное чтение содержимого")]); } }
            }
            Sql.Execute(connection, transaction, "DELETE FROM PackEntries WHERE packId=$id AND version=$version", ("$id", reference.Id.ToString()), ("$version", reference.Version));
            Sql.Execute(connection, transaction, "DELETE FROM SeedPacks WHERE packId=$id AND version=$version AND contentHash=$hash", ("$id", reference.Id.ToString()), ("$version", reference.Version), ("$hash", reference.ContentHash));
            return true;
        }, cancellationToken), cancellationToken).ConfigureAwait(false);
        await blobs.CleanupOrphansAsync(cancellationToken).ConfigureAwait(false);
    }
}
