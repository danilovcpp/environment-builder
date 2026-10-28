using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EnvironmentBuilder.Infrastructure.Security;

namespace EnvironmentBuilder.Infrastructure.Persistence;

public sealed class StagedBlob : IDisposable
{
    internal StagedBlob(string path, string hash, long bytes) { Path = path; Hash = hash; Bytes = bytes; }
    internal string Path { get; }
    public string Hash { get; }
    public long Bytes { get; }
    public void Dispose()
    {
        LocalPathResolver.RejectReparsePoints(Path);
        if (File.Exists(Path)) { File.Delete(Path); }
    }
}

public sealed class BlobLease : IDisposable
{
    private readonly Action release;
    private bool disposed;
    internal BlobLease(string hash, long bytes, FileStream stream, Action release) { Hash = hash; Bytes = bytes; Stream = stream; this.release = release; }
    public string Hash { get; }
    public long Bytes { get; }
    public FileStream Stream { get; }
    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        Stream.Dispose();
        release();
    }
}

public sealed class ContentBlobStore(AppDataDirectory directory, SqliteWorker worker) : IDisposable
{
    private readonly SemaphoreSlim files = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> leases = new(StringComparer.Ordinal);

    public async Task<StagedBlob> StageAsync(Stream input, string expectedHash, long expectedBytes, long limit, CancellationToken cancellationToken)
    {
        ValidateHash(expectedHash);
        if (expectedBytes < 0 || expectedBytes > limit) { throw new IOException("Payload exceeds its declared limit."); }
        string path = directory.Child("staging", Guid.NewGuid().ToString("N") + ".payload");
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[65536];
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    total += count;
                    if (total > limit || total > expectedBytes) { throw new IOException("Expanded payload exceeds its limit."); }
                    digest.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            if (total != expectedBytes || Convert.ToHexStringLower(digest.GetHashAndReset()) != expectedHash) { throw new IOException("Payload size/hash mismatch."); }
            return new StagedBlob(path, expectedHash, total);
        }
        catch { if (File.Exists(path)) { File.Delete(path); } throw; }
    }

    public async Task<BlobLease> PromoteAsync(StagedBlob staged, CancellationToken cancellationToken)
    {
        await files.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = directory.Child("blobs", staged.Hash);
            if (!File.Exists(path)) { File.Move(staged.Path, path); }
            else { staged.Dispose(); }
            return await OpenVerifiedAsync(staged.Hash, staged.Bytes, cancellationToken).ConfigureAwait(false);
        }
        finally { files.Release(); }
    }

    public async Task<BlobLease> AcquireAsync(string hash, CancellationToken cancellationToken)
    {
        ValidateHash(hash);
        long? size = await worker.ReadAsync(connection =>
        {
            using var command = Sql.Command(connection, null, "SELECT bytes FROM ContentBlobs WHERE hash=$hash", ("$hash", hash));
            return command.ExecuteScalar() is long bytes ? (long?)bytes : null;
        }, cancellationToken).ConfigureAwait(false);
        if (size is null) { throw new FileNotFoundException("Verified blob metadata is missing."); }
        await files.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await OpenVerifiedAsync(hash, size.Value, cancellationToken).ConfigureAwait(false); }
        finally { files.Release(); }
    }

    private async Task<BlobLease> OpenVerifiedAsync(string hash, long size, CancellationToken cancellationToken)
    {
        string path = directory.Child("blobs", hash);
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        try
        {
            if (input.Length != size || Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false)) != hash)
            { throw new IOException("Immutable blob is corrupt."); }
            input.Position = 0;
            leases.AddOrUpdate(hash, 1, (_, count) => count + 1);
            return new BlobLease(hash, size, input, () => leases.AddOrUpdate(hash, 0, (_, count) => count - 1));
        }
        catch { input.Dispose(); throw; }
    }

    public async Task<int> CleanupOrphansAsync(CancellationToken cancellationToken)
    {
        await files.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int removed = 0;
            foreach (string path in Directory.EnumerateFiles(directory.Child("blobs")))
            {
                string hash = Path.GetFileName(path);
                if (!Regex.IsMatch(hash, "^[0-9a-f]{64}\\z") || leases.GetValueOrDefault(hash) > 0) { continue; }
                LocalPathResolver.RejectReparsePoints(path);
                bool unused = await worker.CommitAsync("blob-cleanup", (connection, transaction) =>
                {
                    using var references = Sql.Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM PackEntries WHERE blobHash=$hash)", ("$hash", hash));
                    if ((long)references.ExecuteScalar()! != 0) { return false; }
                    Sql.Execute(connection, transaction, "DELETE FROM ContentBlobs WHERE hash=$hash", ("$hash", hash));
                    return true;
                }, cancellationToken).ConfigureAwait(false);
                if (unused)
                {
                    File.Delete(path);
                    string index = directory.Child("blobs", hash + ".index");
                    if (File.Exists(index)) { File.Delete(index); }
                    removed++;
                }
            }
            return removed;
        }
        finally { files.Release(); }
    }

    internal string NewIndexPath() => directory.Child("staging", Guid.NewGuid().ToString("N") + ".index");
    internal bool IsLeased(string hash) => leases.GetValueOrDefault(hash) > 0;
    internal async Task<T> WithFileLockAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        await files.WaitAsync(token).ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); }
        finally { files.Release(); }
    }
    internal string PromoteIndex(string staging, string hash)
    {
        string target = directory.Child("blobs", hash + ".index");
        File.Move(staging, target, overwrite: true);
        return "blobs/" + hash + ".index";
    }
    internal static void ValidateHash(string hash)
    { if (!Regex.IsMatch(hash, "^[0-9a-f]{64}\\z")) { throw new IOException("Invalid blob identity."); } }
    public void Dispose() => files.Dispose();
}
