using EnvironmentBuilder.Core.Abstractions;

namespace EnvironmentBuilder.Core.Operations;

public sealed class OperationHandle
{
    internal OperationHandle(Operation operation) { Current = operation; }
    internal CancellationTokenSource Cancellation { get; } = new();
    internal Task LastWrite { get; set; } = Task.CompletedTask;
    public Operation Current { get; internal set; }
    public Task Completion { get; internal set; } = Task.CompletedTask;
}

public sealed class OperationExecution(DurableEffectRunner effects, Func<bool> canWrite, CancellationToken cancellationToken)
{
    private bool blocked;
    internal bool RequiresReconciliation => blocked;
    public async Task RunStepAsync(OperationStep step, Func<CancellationToken, Task> effect, Func<CancellationToken, Task<EffectObservation>> observe)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (blocked) { throw new UnresolvedEffectException(); }
        if (!canWrite()) { throw new IOException("Storage writer is unavailable."); }
        StepState result;
        try
        {
            result = await effects.RunAsync(step, token =>
            {
                if (!canWrite()) { throw new IOException("Storage writer is unavailable."); }
                return effect(token);
            }, observe, cancellationToken).ConfigureAwait(false);
        }
        catch { blocked = true; throw; }
        if (result != StepState.Succeeded) { blocked = true; throw new UnresolvedEffectException(); }
    }
}

public sealed class UnresolvedEffectException() : Exception("An effect requires reconciliation; scheduling has stopped.");

public sealed class OperationCoordinator(IOperationStore store, IClock clock, Func<bool> canWrite) : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, OperationHandle> active = [];
    private readonly HashSet<Guid> unresolved = [];
    private bool pendingClose;
    private bool disposed;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Operation> recovery = await store.ReadUnresolvedAsync(cancellationToken).ConfigureAwait(false);
        lock (sync) { foreach (Operation operation in recovery) { unresolved.Add(operation.EnvironmentId); } }
    }

    public OperationHandle Submit(Operation operation, Func<OperationExecution, Task> workflow)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (active.TryGetValue(operation.EnvironmentId, out OperationHandle? existing)) { return existing; }
            if (pendingClose || !canWrite() || unresolved.Contains(operation.EnvironmentId)) { throw new InvalidOperationException("New effects are blocked by storage, close, or unresolved recovery."); }
            var handle = new OperationHandle(operation);
            active.Add(operation.EnvironmentId, handle);
            handle.Completion = RunAsync(handle, workflow);
            return handle;
        }
    }

    private async Task RunAsync(OperationHandle handle, Func<OperationExecution, Task> workflow)
    {
        // Yield prevents an entirely synchronous workflow from completing inside admission's lock.
        await Task.Yield();
        try
        {
            await PersistAsync(handle, operation => operation, CancellationToken.None).ConfigureAwait(false);
            await PersistAsync(handle, operation => operation with { State = operation.CancellationRequested ? OperationState.CancelRequested : OperationState.Running }, CancellationToken.None).ConfigureAwait(false);
            var execution = new OperationExecution(new DurableEffectRunner(store), canWrite, handle.Cancellation.Token);
            await workflow(execution).ConfigureAwait(false);
            if (execution.RequiresReconciliation) { throw new UnresolvedEffectException(); }
            handle.Current = handle.Current with { State = handle.Cancellation.IsCancellationRequested ? OperationState.Cancelled : OperationState.Succeeded, CompletedAt = clock.UtcNow };
        }
        catch (UnresolvedEffectException)
        { handle.Current = handle.Current with { State = OperationState.NeedsAttention, ResultCode = "effect-outcome-unknown" }; }
        catch (OperationCanceledException)
        { handle.Current = handle.Current with { State = OperationState.Cancelled, CompletedAt = clock.UtcNow }; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { handle.Current = handle.Current with { State = OperationState.NeedsAttention, ResultCode = "operation-interrupted" }; }
        try { await PersistAsync(handle, operation => operation, CancellationToken.None).ConfigureAwait(false); }
        catch
        {
            handle.Current = handle.Current with { State = OperationState.NeedsAttention, ResultCode = "operation-result-not-durable" };
            throw;
        }
        finally
        {
            Task remainingWrites;
            lock (sync)
            {
                active.Remove(handle.Current.EnvironmentId);
                if (handle.Current.State == OperationState.NeedsAttention) { unresolved.Add(handle.Current.EnvironmentId); }
                remainingWrites = handle.LastWrite;
            }
            try { await remainingWrites.ConfigureAwait(false); }
            finally { handle.Cancellation.Dispose(); }
        }
    }

    private Task PersistAsync(OperationHandle handle, Func<Operation, Operation> transition, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            Task previous = handle.LastWrite;
            handle.LastWrite = WriteAfterAsync(previous, handle, transition, cancellationToken);
            return handle.LastWrite;
        }
    }

    private async Task WriteAfterAsync(Task previous, OperationHandle handle, Func<Operation, Operation> transition, CancellationToken cancellationToken)
    {
        await Task.Yield();
        // Its owning caller observes the previous failure. A later write must still be able to record NeedsAttention.
        try { await previous.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        handle.Current = transition(handle.Current);
        await store.SaveAsync(handle.Current, cancellationToken).ConfigureAwait(false);
    }

    public Task RequestCancelAsync(Guid environmentId, CancellationToken cancellationToken)
    {
        OperationHandle? handle;
        lock (sync)
        {
            active.TryGetValue(environmentId, out handle);
            if (handle is not null)
            {
                handle.Cancellation.Cancel();
                return PersistAsync(handle, operation => operation.State is OperationState.Succeeded or OperationState.Failed or OperationState.Cancelled or OperationState.NeedsAttention
                    ? operation : operation with { State = OperationState.CancelRequested, CancellationRequested = true }, cancellationToken);
            }
        }
        return Task.CompletedTask;
    }

    public async Task PrepareCloseAsync(bool cancel, CancellationToken cancellationToken)
    {
        OperationHandle[] handles;
        lock (sync) { pendingClose = true; handles = active.Values.ToArray(); }
        if (cancel) { foreach (OperationHandle handle in handles) { await RequestCancelAsync(handle.Current.EnvironmentId, cancellationToken).ConfigureAwait(false); } }
        await Task.WhenAll(handles.Select(h => h.Completion)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    public void ReturnFromClose() { lock (sync) { pendingClose = false; } }
    public async ValueTask DisposeAsync()
    {
        lock (sync) { disposed = true; }
        await PrepareCloseAsync(cancel: true, CancellationToken.None).ConfigureAwait(false);
    }
}
