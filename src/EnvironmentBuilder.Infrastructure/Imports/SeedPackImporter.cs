using System.Collections.Immutable;
using System.IO.Compression;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Persistence;

namespace EnvironmentBuilder.Infrastructure.Imports;

public sealed record ImportedPackEntry(string Path, string? SectionId, BlobLease Blob, long? RecordCount, string? IndexPath);

public sealed class SeedPackImporter(StrictDocumentReader reader, ContentBlobStore blobs, SeedPackStore store) : ISeedPackImporter
{
    public async Task<SeedPackReference> ImportAsync(string archivePath, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (file.Length > 512L * 1024 * 1024) { throw new IOException("ZIP exceeds 512 MiB."); }
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count is 0 or > 256) { throw new IOException("ZIP entry count is invalid."); }
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long declaredTotal = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            EnvironmentValidator.SafeRelativePath(entry.FullName, "/zip");
            int unixKind = (entry.ExternalAttributes >> 16) & 0xf000;
            if (unixKind is not 0 and not 0x8000 || ((entry.ExternalAttributes >> 16) & 0x49) != 0 ||
                (entry.ExternalAttributes & (int)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || !entries.TryAdd(entry.FullName, entry))
            { throw new IOException("ZIP links, directories and case collisions are forbidden."); }
            declaredTotal = checked(declaredTotal + entry.Length);
            if (declaredTotal > 2L * 1024 * 1024 * 1024) { throw new IOException("Expanded ZIP exceeds 2 GiB."); }
        }
        if (!entries.TryGetValue("manifest.json", out ZipArchiveEntry? manifestEntry) || manifestEntry.FullName != "manifest.json")
        { throw new IOException("ZIP must contain manifest.json at its root."); }
        byte[] manifestBytes = await ReadBoundedAsync(manifestEntry, cancellationToken).ConfigureAwait(false);
        SeedPack manifest = reader.Read<SeedPack>(manifestBytes, "seedpack");
        var declared = manifest.Sections.Select(s => s.Path).Concat(manifest.Initializers.Select(i => i.Path)).Append("manifest.json").ToHashSet(StringComparer.Ordinal);
        if (declared.Count != entries.Count || archive.Entries.Any(e => !declared.Contains(e.FullName))) { throw new IOException("ZIP contains undeclared or missing entries."); }
        var verified = new List<ImportedPackEntry>();
        var initializers = ImmutableDictionary.CreateBuilder<string, InitializationDefinition>(StringComparer.Ordinal);
        long actualTotal = manifestBytes.Length;
        try
        {
            foreach (SeedSection section in manifest.Sections)
            {
                ZipArchiveEntry entry = entries[section.Path];
                await using Stream source = entry.Open();
                using StagedBlob staged = await blobs.StageAsync(source, section.Sha256, section.Bytes, 256L * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                actualTotal = checked(actualTotal + staged.Bytes);
                if (actualTotal > 2L * 1024 * 1024 * 1024) { throw new IOException("Expanded ZIP exceeds 2 GiB."); }
                string indexPath = blobs.NewIndexPath();
                try
                {
                    long records;
                    await using (var input = new FileStream(staged.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
                    await using (var index = new FileStream(indexPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                    { records = await new NdjsonReader().ValidateAndIndexAsync(input, index, cancellationToken).ConfigureAwait(false); }
                    if (records != section.RecordCount) { throw new IOException("Section record count mismatch."); }
                    BlobLease lease = await blobs.PromoteAsync(staged, cancellationToken).ConfigureAwait(false);
                    try { verified.Add(new ImportedPackEntry(section.Path, section.Id, lease, records, blobs.PromoteIndex(indexPath, staged.Hash))); }
                    catch { lease.Dispose(); throw; }
                }
                finally { if (File.Exists(indexPath)) { File.Delete(indexPath); } }
            }
            foreach (InitializerEntry initializer in manifest.Initializers)
            {
                await using Stream source = entries[initializer.Path].Open();
                using StagedBlob staged = await blobs.StageAsync(source, initializer.Sha256, initializer.Bytes, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                actualTotal = checked(actualTotal + staged.Bytes);
                if (actualTotal > 2L * 1024 * 1024 * 1024) { throw new IOException("Expanded ZIP exceeds 2 GiB."); }
                InitializationDefinition definition = reader.Read<InitializationDefinition>(await File.ReadAllBytesAsync(staged.Path, cancellationToken).ConfigureAwait(false), "initialization");
                InitializationValidator.Validate(definition, pack: manifest);
                if (!manifest.Compatibility.Any(c => c.ContractId == definition.TargetContract.ContractId && definition.TargetContract.Versions.All(c.Versions.Contains)))
                { throw new DocumentException("/initializers", "pack-contract-mismatch"); }
                initializers.Add(initializer.Path, definition);
                verified.Add(new ImportedPackEntry(initializer.Path, null, await blobs.PromoteAsync(staged, cancellationToken).ConfigureAwait(false), null, null));
            }
            await store.CommitAsync(manifest, verified, cancellationToken).ConfigureAwait(false);
            return new SeedPackReference(manifest.Id, manifest.Version, manifest.ContentHash);
        }
        finally { foreach (ImportedPackEntry entry in verified) { entry.Blob.Dispose(); } }
    }

    private static async Task<byte[]> ReadBoundedAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Length > 1024 * 1024) { throw new IOException("Manifest exceeds 1 MiB."); }
        await using Stream input = entry.Open();
        using var output = new MemoryStream();
        byte[] buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > 1024 * 1024) { throw new IOException("Manifest exceeds 1 MiB."); }
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
