using EnvironmentBuilder.Core.Abstractions;

namespace EnvironmentBuilder.Core.Operations;

public sealed class OperationHandle
{
    private Operation current;
    internal OperationHandle(Operation operation) { current = operation; }
    internal CancellationTokenSource Cancellation { get; } = new();
    internal Task LastWrite { get; set; } = Task.CompletedTask;
    internal bool TerminalWriteFailed { get; set; }
    public Operation Current { get => Volatile.Read(ref current); internal set => Volatile.Write(ref current, value); }
    public Task Completion { get; internal set; } = Task.CompletedTask;
}

public sealed class OperationExecution(DurableEffectRunner effects, Func<bool> canWrite, CancellationToken cancellationToken,
    Func<Func<Operation, Operation>, Task>? update = null)
{
    private bool blocked;
    internal bool RequiresReconciliation => blocked;
    public CancellationToken CancellationToken => cancellationToken;
    public Task SetPlanAsync(OperationPlan plan) => update?.Invoke(o => o with { Plan = plan, State = OperationState.AwaitingConfirmation }) ?? Task.CompletedTask;
    public Task ConfirmAsync(string reviewToken) => update?.Invoke(o => o with { State = OperationState.Running, InputReviewToken = reviewToken, Confirmation = new(o.Plan.Hash, DateTimeOffset.UtcNow) }) ?? Task.CompletedTask;
    public Task ReportAsync(Guid? component, string stage) => update?.Invoke(o => o with { Progress = [new(component, stage, 0, null)] }) ?? Task.CompletedTask;
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

public sealed class UnresolvedEffectException(string code = "effect-outcome-unknown") : Exception("An operation requires reconciliation; scheduling has stopped.")
{
    public string Code { get; } = code;
}

public sealed class OperationCoordinator(IOperationStore store, IClock clock, Func<bool> canWrite) : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, OperationHandle> active = [];
    private readonly HashSet<Guid> unresolved = [];
    private bool pendingClose;
    private bool disposed;
    private bool resultWriteFailed;

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
        OperationState terminal;
        string? resultCode = null;
        DateTimeOffset? completedAt = null;
        try
        {
            await PersistAsync(handle, operation => operation, CancellationToken.None).ConfigureAwait(false);
            await PersistAsync(handle, operation => operation with { State = operation.CancellationRequested ? OperationState.CancelRequested : OperationState.Running }, CancellationToken.None).ConfigureAwait(false);
            var execution = new OperationExecution(new DurableEffectRunner(store), canWrite, handle.Cancellation.Token,
                transition => PersistAsync(handle, transition, CancellationToken.None));
            await workflow(execution).ConfigureAwait(false);
            if (execution.RequiresReconciliation) { throw new UnresolvedEffectException(); }
            terminal = handle.Cancellation.IsCancellationRequested ? OperationState.Cancelled : OperationState.Succeeded;
            completedAt = clock.UtcNow;
        }
        catch (UnresolvedEffectException unknown)
        { terminal = OperationState.NeedsAttention; resultCode = unknown.Code; }
        catch (PreflightBlockedException blocked)
        { terminal = OperationState.Failed; resultCode = blocked.Code; completedAt = clock.UtcNow; }
        catch (OperationCanceledException)
        { terminal = OperationState.Cancelled; completedAt = clock.UtcNow; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { terminal = OperationState.NeedsAttention; resultCode = "operation-interrupted"; }
        try
        {
            await PersistAsync(handle, operation => operation with { State = terminal, ResultCode = resultCode, CompletedAt = completedAt }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            lock (sync)
            {
                handle.TerminalWriteFailed = true;
                handle.Current = handle.Current with { State = OperationState.NeedsAttention, ResultCode = "operation-result-not-durable" };
                resultWriteFailed = true;
            }
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
        Operation next;
        lock (sync) { next = transition(handle.Current); }
        await store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        // Publishing after commit prevents a UI observer from seeing an undurable terminal success.
        lock (sync)
        {
            handle.Current = handle.TerminalWriteFailed ? next with { State = OperationState.NeedsAttention, ResultCode = "operation-result-not-durable" } : next;
        }
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
                return PersistAsync(handle, operation => operation with
                {
                    State = operation.State is OperationState.Succeeded or OperationState.Failed or OperationState.Cancelled or OperationState.NeedsAttention ? operation.State : OperationState.CancelRequested,
                    CancellationRequested = true
                }, cancellationToken);
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
        lock (sync) { if (resultWriteFailed) { throw new IOException("An operation result is not durable; safe shutdown is blocked."); } }
    }
    public void ReturnFromClose() { lock (sync) { pendingClose = false; } }
    public IReadOnlyList<OperationHandle> Active { get { lock (sync) { return active.Values.ToArray(); } } }
    public string? AdmissionBlockReason(Guid environment)
    {
        lock (sync)
        {
            if (pendingClose || disposed) { return "Приложение ожидает безопасного завершения."; }
            if (!canWrite()) { return "Запись в хранилище недоступна."; }
            if (active.ContainsKey(environment)) { return "Операция окружения уже выполняется."; }
            return unresolved.Contains(environment) ? "Незавершённый результат требует сверки." : null;
        }
    }
    public async ValueTask DisposeAsync()
    {
        lock (sync) { disposed = true; }
        await PrepareCloseAsync(cancel: true, CancellationToken.None).ConfigureAwait(false);
    }
}
