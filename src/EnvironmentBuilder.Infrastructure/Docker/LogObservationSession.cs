using System.Text;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.Infrastructure.Docker;

public sealed class LogMemoryBudget
{
    private long bytes;
    public bool Reserve(int count)
    {
        long total = Interlocked.Add(ref bytes, count);
        if (total <= 32 * 1024 * 1024) { return true; }
        Interlocked.Add(ref bytes, -count); return false;
    }
    public void Release(int count) => Interlocked.Add(ref bytes, -count);
    public long Bytes => Interlocked.Read(ref bytes);
}
public sealed record LogSnapshot(IReadOnlyList<string> Lines, long Dropped, long Truncated, string State);
public sealed class LogObservationSession : IAsyncDisposable
{
    private readonly IDockerProcess docker;
    private readonly string containerId;
    private readonly LogMemoryBudget budget;
    private readonly CancellationTokenSource cancellation = new();
    private readonly object sync = new();
    private readonly Queue<(string Line, int Bytes)> lines = [];
    private readonly StringBuilder[] pending = [new(), new()];
    private readonly bool[] discarding = new bool[2];
    private Task completion = Task.CompletedTask;
    private int bytes;
    private long dropped;
    private long truncated;
    private string state = "not-started";
    private bool disposed;
    private const int StreamBuffers = 2 * 1024 * 1024;
    public LogObservationSession(IDockerProcess docker, string containerId, LogMemoryBudget budget)
    {
        this.docker = docker; this.containerId = containerId; this.budget = budget;
        if (!budget.Reserve(StreamBuffers)) { cancellation.Dispose(); throw new InvalidOperationException("Log ingestion budget is exhausted."); }
    }
    public void Start(OwnedResource ledger, ResourceEvidence evidence, DateTimeOffset? since = null)
    {
        if (!ResourceOwnershipVerifier.Verify(ledger, evidence).Verified || ledger.Kind != ResourceKind.Container || evidence.Identity != containerId)
        { throw new InvalidOperationException("Logs require verified exact owned identity."); }
        if (state != "not-started") { throw new InvalidOperationException("Log session already started."); }
        state = "following";
        completion = FollowAsync(since);
    }
    private async Task FollowAsync(DateTimeOffset? since)
    {
        var args = new List<string> { "logs", "--tail", "1000", "--timestamps", "--follow" };
        if (since.HasValue) { args.AddRange(["--since", since.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture)]); }
        args.Add(containerId);
        try
        {
            DockerResult result = await docker.RunAsync(args, cancellation.Token, (chunk, error) => { Append(chunk, error); return Task.CompletedTask; }).ConfigureAwait(false);
            lock (sync) { state = result.ExitCode == 0 ? "ended" : "unavailable"; }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { lock (sync) { state = "paused"; } }
        catch (Exception error) when (error is not OutOfMemoryException) { lock (sync) { state = "unavailable"; } }
    }
    public void Append(string sanitizedChunk, bool error = false)
    {
        lock (sync)
        {
            int index = error ? 1 : 0;
            foreach (char character in sanitizedChunk)
            {
                if (character == '\n')
                {
                    Add(pending[index].ToString().TrimEnd('\r') + (discarding[index] ? " [truncated]" : ""));
                    pending[index].Clear(); discarding[index] = false;
                }
                else if (!discarding[index])
                {
                    if (pending[index].Length >= 4096) { discarding[index] = true; truncated++; }
                    else { pending[index].Append(character); }
                }
            }
        }
    }
    private void Add(string line)
    {
        // Account for the display string as well as retained lines; reserve covers chunk/redaction tails.
        int size = 2 * Math.Max(Encoding.UTF8.GetByteCount(line), line.Length * 2) + 32;
        while (lines.Count > 0 && (lines.Count >= 10000 || bytes + size > 8 * 1024 * 1024 - StreamBuffers)) { Evict(); }
        if (!budget.Reserve(size)) { dropped++; return; }
        lines.Enqueue((line, size)); bytes += size;
    }
    private void Evict() { var old = lines.Dequeue(); bytes -= old.Bytes; budget.Release(old.Bytes); dropped++; }
    public LogSnapshot Snapshot() { lock (sync) { return new(lines.Select(l => l.Line).ToArray(), dropped, truncated, state); } }
    public async ValueTask DisposeAsync()
    {
        if (disposed) { return; }
        disposed = true;
        await cancellation.CancelAsync().ConfigureAwait(false); await completion.ConfigureAwait(false);
        lock (sync) { while (lines.Count > 0) { Evict(); } foreach (StringBuilder buffer in pending) { buffer.Clear(); } }
        cancellation.Dispose();
        budget.Release(StreamBuffers);
    }
}
