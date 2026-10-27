using System.Collections.Immutable;
using System.Text.Json;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Infrastructure.Docker;

namespace EnvironmentBuilder.IntegrationTests.Docker;

public sealed class ObservationContractTests
{
    [Fact]
    public void Actual_stopped_container_mounts_and_network_attachments_are_included_as_consumers()
    {
        OwnedResource volume = DockerAdapterContractTests.Ledger();
        JsonElement actual = JsonSerializer.SerializeToElement(new { Name = volume.Name, Labels = ResourceOwnershipVerifier.Labels(volume), CreatedAt = volume.Volume!.CreatedAt, Driver = "local", Scope = "local", Options = new Dictionary<string, string>() });
        JsonElement stopped = JsonSerializer.SerializeToElement(new { Id = "stopped-foreign", State = new { Status = "exited" }, Mounts = new[] { new { Type = "volume", Name = volume.Name } } });
        DockerResourceRead read = ResourceConsumerReader.Parse(volume, actual, [stopped]);
        Assert.Contains("stopped-foreign", read.Evidence.Consumers);
        Assert.False(ResourceOwnershipVerifier.Verify(volume, read.Evidence).Verified);
        OwnedResource network = volume with { Kind = ResourceKind.Network, ActualDockerId = "network-id", Volume = null };
        JsonElement attached = JsonSerializer.SerializeToElement(new { Name = network.Name, Id = "network-id", Labels = ResourceOwnershipVerifier.Labels(network), Containers = new Dictionary<string, object> { ["foreign-attachment"] = new { } } });
        Assert.False(ResourceOwnershipVerifier.Verify(network, ResourceConsumerReader.Parse(network, attached, []).Evidence).Verified);
    }

    [Fact]
    public void Unknown_capability_output_is_blocked_instead_of_guessing_defaults()
    {
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        Assert.Throws<NotSupportedException>(() => DockerCapabilityProbe.ParseCapabilities(DockerEndpoint.Default, empty, empty, empty));
    }
    [Theory]
    [InlineData("1.5MB", "bytes", 1500000)]
    [InlineData("1.5MiB", "bytes", 1572864)]
    [InlineData("350.5%", "%", 350.5)]
    [InlineData("0B", "bytes", 0)]
    public void Stats_units_are_invariant_and_cpu_is_not_clamped(string text, string unit, double expected) => Assert.Equal(expected, MetricSampler.Parse(text, unit, DateTimeOffset.UtcNow).Value);

    [Theory]
    [InlineData("N/A")]
    [InlineData("1,5MB")]
    [InlineData("1unknown")]
    public void Invalid_stats_are_missing_instead_of_zero(string text) => Assert.Null(MetricSampler.Parse(text, "bytes", DateTimeOffset.UtcNow).Value);

    [Fact]
    public async Task Logs_enforce_line_ring_and_application_budgets_and_release_on_disposal()
    {
        var budget = new LogMemoryBudget();
        var docker = new FakeDocker(_ => throw new InvalidOperationException("No process expected."));
        var session = new LogObservationSession(docker, new string('a', 64), budget);
        session.Append(new string('x', 30000) + "\n");
        for (int i = 0; i < 12000; i++) { session.Append(new string('z', 1024) + "\n"); }
        var snapshot = session.Snapshot();
        Assert.True(snapshot.Dropped > 0); Assert.Equal(1, snapshot.Truncated);
        Assert.True(snapshot.Lines.Count <= 10000); Assert.True(budget.Bytes <= 8 * 1024 * 1024);
        Assert.All(snapshot.Lines, line => Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 16384));
        await session.DisposeAsync(); Assert.Equal(0, budget.Bytes);
    }

    [Fact]
    public async Task One_stats_probe_samples_all_components_and_retains_at_most_300_per_identity()
    {
        string id = new('a', 64);
        var docker = new FakeDocker(args =>
        {
            Assert.Equal(new[] { "stats", "--no-stream", "--no-trunc", "--format", "{{json .}}", id }, args);
            return Task.FromResult(new DockerResult(0, JsonSerializer.Serialize(new { ID = id, CPUPerc = "120.5%", PIDs = "0", MemUsage = "0B / 1GiB", NetIO = "1kB / 2KiB", BlockIO = "0B / 0B" }), ""));
        });
        using var sampler = new MetricSampler(docker);
        for (int i = 0; i < 305; i++)
        {
            var batch = await sampler.SampleManyAsync([id], DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
            Assert.Equal(120.5, batch[id].Measurements["cpu"].Value);
        }
        Assert.Equal(300, sampler.History(id).Count);
    }

    [Theory]
    [InlineData("28.0.0", "1.48", "2.24.6", "linux", true)]
    [InlineData("29.7.2", "1.55", "5.4.0", "linux", true)]
    [InlineData("29.9.1", "1.55", "5.6.2", "linux", true)]
    [InlineData("27.9.0", "1.48", "2.24.6", "linux", false)]
    [InlineData("28.0.0", "1.47", "2.24.6", "linux", false)]
    [InlineData("28.0.0", "1.48", "2.24.6", "windows", false)]
    public void Capability_output_fixtures_cover_minimum_current_and_unsupported_modes(string version, string api, string compose, string os, bool supported)
    {
        JsonElement server = JsonSerializer.SerializeToElement(new { Version = version, ApiVersion = api });
        JsonElement info = JsonSerializer.SerializeToElement(new { ID = "engine", OSType = os, Architecture = "x86_64", OperatingSystem = "Docker Desktop", KernelVersion = "microsoft-standard-WSL2" });
        JsonElement plugin = JsonSerializer.SerializeToElement(new { version = compose });
        if (supported) { Assert.Equal("engine", DockerCapabilityProbe.ParseCapabilities(DockerEndpoint.Default, server, info, plugin).EngineId); }
        else { Assert.Throws<NotSupportedException>(() => DockerCapabilityProbe.ParseCapabilities(DockerEndpoint.Default, server, info, plugin)); }
    }
    internal sealed class FakeDocker(Func<IReadOnlyList<string>, Task<DockerResult>> run) : IDockerProcess
    {
        public string Endpoint => DockerEndpoint.Default;
        public Task<DockerResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken token, Func<string, bool, Task>? output = null) => run(arguments);
    }
}
