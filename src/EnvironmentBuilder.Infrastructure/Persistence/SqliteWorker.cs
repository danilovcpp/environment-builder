using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace EnvironmentBuilder.Infrastructure.Persistence;

public sealed class SqliteWorker : IAsyncDisposable
{
    private readonly BlockingCollection<Action<SqliteConnection>> work = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SingleWriterLease lease;
    private readonly Action<string>? fault;
    private int disposed;

    public SqliteWorker(AppDataDirectory directory, SingleWriterLease lease, Action<string>? fault = null)
    {
        this.lease = lease;
        this.fault = fault;
        var thread = new Thread(() => Run(directory)) { IsBackground = true, Name = "EnvironmentBuilder SQLite" };
        thread.Start();
    }

    public string NativeVersion { get; private set; } = "unknown";
    public Task Ready => ready.Task;
    public Task<T> ReadAsync<T>(Func<SqliteConnection, T> read, CancellationToken cancellationToken = default) => Enqueue(read, cancellationToken);

    public Task<T> CommitAsync<T>(string boundary, Func<SqliteConnection, SqliteTransaction, T> mutate, CancellationToken cancellationToken = default) =>
        Enqueue(connection =>
        {
            lease.RequireWrite();
            using SqliteTransaction transaction = connection.BeginTransaction();
            T result = mutate(connection, transaction);
            fault?.Invoke(boundary + ":beforeCommit");
            lease.RequireWrite();
            transaction.Commit();
            fault?.Invoke(boundary + ":afterCommit");
            return result;
        }, cancellationToken);

    private async Task<T> Enqueue<T>(Func<SqliteConnection, T> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Add(connection =>
        {
            try { cancellationToken.ThrowIfCancellationRequested(); result.SetResult(action(connection)); }
            catch (OperationCanceledException) { result.SetCanceled(cancellationToken); }
            catch (Exception error)
            {
                if (error is SqliteException sqlite && sqlite.SqliteErrorCode is 8 or 10 or 11 or 13 or 26) { lease.Revoke(); }
                result.SetException(error);
            }
        }, cancellationToken);
        return await result.Task.ConfigureAwait(false);
    }

    private void Run(AppDataDirectory directory)
    {
        try
        {
            lease.RequireWrite();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = directory.DatabasePath, Pooling = false }.ToString());
            connection.Open();
            NativeVersion = DatabaseMigrator.Initialize(connection);
            ready.SetResult();
            foreach (Action<SqliteConnection> action in work.GetConsumingEnumerable()) { action(connection); }
        }
        catch (Exception error) { lease.Revoke(); ready.TrySetException(error); }
        finally { completed.SetResult(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
        work.CompleteAdding();
        await completed.Task.ConfigureAwait(false);
        work.Dispose();
    }
}
