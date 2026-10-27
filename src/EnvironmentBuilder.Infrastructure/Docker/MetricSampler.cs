using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Infrastructure.Docker;

public sealed record MetricSample(string ContainerId, DateTimeOffset At, ImmutableDictionary<string, Measurement> Measurements);
public sealed class MetricSampler(IDockerProcess docker) : IDisposable
{
    private readonly SemaphoreSlim probe = new(1, 1);
    private readonly Dictionary<string, Queue<MetricSample>> history = new(StringComparer.Ordinal);
    public async Task<MetricSample> SampleAsync(string verifiedContainerId, DateTimeOffset at, CancellationToken token)
        => (await SampleManyAsync([verifiedContainerId], at, token).ConfigureAwait(false))[verifiedContainerId];

    public async Task<IReadOnlyDictionary<string, MetricSample>> SampleManyAsync(IReadOnlyList<string> verifiedContainerIds, DateTimeOffset at, CancellationToken token)
    {
        if (verifiedContainerIds.Count == 0) { return new Dictionary<string, MetricSample>(); }
        if (verifiedContainerIds.Any(id => id.Length != 64 || id.Any(c => !char.IsAsciiHexDigit(c)))) { throw new ArgumentException("Exact verified container IDs required.", nameof(verifiedContainerIds)); }
        if (!await probe.WaitAsync(0, token).ConfigureAwait(false)) { return verifiedContainerIds.ToDictionary(id => id, id => Missing(id, at, "probe-overlap")); }
        try
        {
            DockerResult result = await docker.RunAsync(["stats", "--no-stream", "--no-trunc", "--format", "{{json .}}", .. verifiedContainerIds], token).ConfigureAwait(false);
            var resultMap = verifiedContainerIds.ToDictionary(id => id, id => Missing(id, at, "stats-unavailable"), StringComparer.Ordinal);
            if (result.ExitCode == 0 && !result.Truncated)
            {
                foreach (string line in result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        using JsonDocument json = JsonDocument.Parse(line);
                        string id = json.RootElement.GetProperty("ID").GetString()!;
                        if (!resultMap.ContainsKey(id)) { throw new JsonException("Stats identity mismatch."); }
                        var values = ImmutableDictionary.CreateBuilder<string, Measurement>(StringComparer.Ordinal);
                        values["cpu"] = Parse(json.RootElement.GetProperty("CPUPerc").GetString()!, "%", at);
                        values["pids"] = Parse(json.RootElement.GetProperty("PIDs").GetString()!, "count", at);
                        Pair("MemUsage", "memoryUsed", "memoryLimit"); Pair("NetIO", "networkReceived", "networkSent"); Pair("BlockIO", "blockRead", "blockWritten");
                        resultMap[id] = new(id, at, values.ToImmutable());
                        void Pair(string field, string first, string second)
                        {
                            string[] parts = json.RootElement.GetProperty(field).GetString()!.Split('/');
                            if (parts.Length != 2) { throw new JsonException("Stats pair expected."); }
                            values[first] = Parse(parts[0].Trim(), "bytes", at); values[second] = Parse(parts[1].Trim(), "bytes", at);
                        }
                    }
                    catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
                    { return verifiedContainerIds.ToDictionary(id => id, id => Missing(id, at, "unknown-stats-format")); }
                }
            }
            lock (history)
            {
                foreach ((string id, MetricSample sample) in resultMap)
                {
                    if (!history.TryGetValue(id, out Queue<MetricSample>? samples)) { samples = new(); history.Add(id, samples); }
                    samples.Enqueue(sample); while (samples.Count > 300) { samples.Dequeue(); }
                }
            }
            return resultMap;
        }
        finally { probe.Release(); }
    }
    public IReadOnlyList<MetricSample> History(string id) { lock (history) { return history.TryGetValue(id, out var samples) ? samples.ToArray() : []; } }
    public static Measurement Parse(string text, string unit, DateTimeOffset at)
    {
        Match match = Regex.Match(text, "^([0-9]+(?:\\.[0-9]+)?)\\s*([a-zA-Z%]*)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value)) { return Measurement.Missing(unit, at, "invalid-measurement"); }
        string suffix = match.Groups[2].Value;
        double? multiplier = unit switch
        {
            "%" => suffix == "%" ? 1 : null,
            "count" => suffix == "" && value == Math.Truncate(value) ? 1 : null,
            "bytes" => suffix switch { "B" => 1, "kB" => 1000, "MB" => 1000000, "GB" => 1000000000, "TB" => 1e12, "KiB" => 1024, "MiB" => 1048576, "GiB" => 1073741824, "TiB" => 1099511627776, _ => null },
            _ => null
        };
        return multiplier.HasValue && double.IsFinite(value * multiplier.Value) ? new(value * multiplier.Value, unit, at) { Original = text } : Measurement.Missing(unit, at, "unknown-measurement-unit");
    }
    private static MetricSample Missing(string id, DateTimeOffset at, string reason) => new(id, at,
        new[] { "cpu", "pids", "memoryUsed", "memoryLimit", "networkReceived", "networkSent", "blockRead", "blockWritten" }.ToImmutableDictionary(k => k, k => Measurement.Missing(k == "cpu" ? "%" : k == "pids" ? "count" : "bytes", at, reason), StringComparer.Ordinal));
    public void Dispose() => probe.Dispose();
}
