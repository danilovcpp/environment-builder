using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.Infrastructure.Security;

namespace EnvironmentBuilder.Infrastructure.Imports;

public sealed class SeedPreviewReader(SeedPackStore store, ContentBlobStore blobs, AppDataDirectory directory) : ISeedPreviewReader, IDisposable
{
    private const int PageBytes = 256 * 1024;
    private readonly SemaphoreSlim reads = new(1, 1);
    private readonly Dictionary<SeedPreviewCursor, (SeedPreviewPage Page, long Bytes)> cache = [];
    private readonly Queue<SeedPreviewCursor> order = [];
    public long CacheBytes { get; private set; }
    public long EvictedPages { get; private set; }

    public async Task<SeedPreviewPage> ReadAsync(SeedPackReference pack, string section, SeedPreviewCursor? cursor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = cursor ?? new SeedPreviewCursor(pack, section, 0);
        if (key.Pack != pack || key.SectionId != section || key.Record < 0) { throw new IOException("Preview cursor belongs to another selection."); }
        await reads.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var manifest = (await store.ListMetadataAsync(token).ConfigureAwait(false)).SingleOrDefault(p => p.Id == pack.Id && p.Version == pack.Version && p.ContentHash == pack.ContentHash)
                ?? throw new IOException("Exact package version is unavailable.");
            var selected = manifest.Sections.SingleOrDefault(s => s.Id == section) ?? throw new IOException("Section is not declared.");
            if (key.Record > selected.RecordCount) { throw new IOException("Preview cursor is out of range."); }
            // The verified lease prevents deletion and modification during index seek and bounded reads.
            using BlobLease lease = await blobs.AcquireAsync(selected.Sha256, token).ConfigureAwait(false);
            if (cache.TryGetValue(key, out var cached)) { return cached.Page; }
            string indexPath = directory.Child("blobs", selected.Sha256 + ".index");
            LocalPathResolver.RejectReparsePoints(indexPath);
            if (!File.Exists(indexPath) || new FileInfo(indexPath).Length != selected.RecordCount * 8)
            { await RebuildIndexAsync(lease, indexPath, token).ConfigureAwait(false); }
            long offset = lease.Bytes;
            if (key.Record < selected.RecordCount)
            {
                await using var index = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                index.Position = key.Record * 8;
                byte[] bytes = new byte[8]; await index.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                offset = BinaryPrimitives.ReadInt64LittleEndian(bytes);
                if (offset < 0 || offset >= lease.Bytes) { throw new IOException("Derived index is invalid; reimport the package."); }
                if (offset > 0) { lease.Stream.Position = offset - 1; if (lease.Stream.ReadByte() != '\n') { throw new IOException("Derived index is not on a record boundary."); } }
            }
            lease.Stream.Position = offset;
            var records = ImmutableArray.CreateBuilder<string>();
            byte[] buffer = new byte[65536];
            int retained = 0, length = 0;
            bool finished = false;
            while (!finished && records.Count < 100 && key.Record + records.Count < selected.RecordCount)
            {
                int count = await lease.Stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), token).ConfigureAwait(false);
                length += count;
                int newline = Array.IndexOf(buffer, (byte)'\n', 0, length);
                if (newline < 0 && count != 0)
                { if (length == buffer.Length) { throw new IOException("Preview record exceeds 64 KiB."); } continue; }
                int recordBytes = newline < 0 ? length : newline + 1;
                if (recordBytes == 0) { throw new IOException("Section record count differs from verified metadata."); }
                if (retained + recordBytes > PageBytes) { break; }
                int textBytes = newline < 0 ? length : newline;
                if (textBytes > 0 && buffer[textBytes - 1] == '\r') { textBytes--; }
                records.Add(new UTF8Encoding(false, true).GetString(buffer, 0, textBytes));
                retained += recordBytes;
                // Read-ahead is local to one page. Seek back rather than retaining an unbounded stream buffer.
                lease.Stream.Position -= length - recordBytes;
                length = 0;
                finished = count == 0;
            }
            long nextRecord = key.Record + records.Count;
            var page = new SeedPreviewPage(section, key.Record, selected.RecordCount, records.ToImmutable(),
                nextRecord < selected.RecordCount ? new(pack, section, nextRecord) : null);
            long size = records.Sum(s => (long)s.Length * sizeof(char)) + records.Count * 64L;
            while (CacheBytes + size > 8 * 1024 * 1024 && order.TryDequeue(out var oldest))
            { CacheBytes -= cache[oldest].Bytes; cache.Remove(oldest); EvictedPages++; }
            cache.Add(key, (page, size)); order.Enqueue(key); CacheBytes += size;
            return page;
        }
        finally { reads.Release(); }
    }

    private static async Task RebuildIndexAsync(BlobLease lease, string path, CancellationToken token)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            lease.Stream.Position = 0;
            await using (var index = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            { await new NdjsonReader().ValidateAndIndexAsync(lease.Stream, index, token).ConfigureAwait(false); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
    public void Dispose() => reads.Dispose();
}
