using System.Security.Cryptography;
using System.Text;

namespace EnvironmentBuilder.Infrastructure.Persistence;

public sealed class SingleWriterLease : IAsyncDisposable
{
    private readonly ManualResetEvent release = new(false);
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int writable;
    private int disposed;
    private SingleWriterLease() { }
    public bool CanWrite => Volatile.Read(ref writable) == 1;
    public void Revoke() => Interlocked.Exchange(ref writable, 0);
    public void RequireWrite()
    {
        if (!CanWrite) { throw new IOException("Storage writer is unavailable; new effects are blocked."); }
    }

    public static async Task<SingleWriterLease> AcquireAsync(AppDataDirectory directory)
    {
        var lease = new SingleWriterLease();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(directory.Root.ToUpperInvariant())));
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, "Local\\EnvironmentBuilder-" + key);
            bool acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) { throw new IOException("Это хранилище уже открыто другим экземпляром Environment Builder."); }
                Volatile.Write(ref lease.writable, 1);
                ready.SetResult();
                lease.release.WaitOne();
            }
            catch (Exception error) { ready.TrySetException(error); }
            finally
            {
                lease.Revoke();
                if (acquired) { mutex.ReleaseMutex(); }
                lease.finished.SetResult();
            }
        })
        { IsBackground = true, Name = "EnvironmentBuilder writer lease" };
        thread.Start();
        try { await ready.Task.ConfigureAwait(false); return lease; }
        catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
        Revoke();
        release.Set();
        await finished.Task.ConfigureAwait(false);
        release.Dispose();
    }
}
