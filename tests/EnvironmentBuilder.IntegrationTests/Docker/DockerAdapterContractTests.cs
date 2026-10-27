using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Infrastructure.Docker;
using EnvironmentBuilder.Infrastructure.Imports;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.Infrastructure.Security;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Docker;

public sealed class DockerAdapterContractTests
{
    [Fact]
    public async Task Image_launch_uses_the_captured_identity_and_does_not_follow_a_later_tag_change()
    {
        string original = "sha256:" + new string('a', 64); string changed = "sha256:" + new string('b', 64);
        int tagReads = 0;
        var docker = new ObservationContractTests.FakeDocker(args =>
        {
            string identity = args[^1] == "app:mutable" ? (++tagReads == 1 ? original : changed) : args[^1];
            return Task.FromResult(new DockerResult(0, JsonSerializer.Serialize(new { Id = identity, Os = "linux", Architecture = "amd64" }), ""));
        });
        var preparation = new ImagePreparation(docker);
        ResolvedImage captured = await preparation.ResolveAsync("app:mutable", TestContext.Current.CancellationToken);
        string pinned = await preparation.InspectPinnedAsync(captured.Identity, TestContext.Current.CancellationToken);
        var environment = new StrictDocumentReader().Read<IsolatedEnvironment>(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Contracts/examples/environment.json"), TestContext.Current.CancellationToken));
        string rendered = ComposeCompiler.Compile(environment).Render(v => v.Literal ?? "resolved", _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException(), new Dictionary<Guid, string> { [environment.Components[0].Id] = pinned });
        Assert.Contains(original, rendered); Assert.DoesNotContain(changed, rendered); Assert.Equal(1, tagReads);
    }

    [Fact]
    public async Task Changed_engine_identity_is_rejected_before_another_command()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        var docker = new ObservationContractTests.FakeDocker(args =>
        { Assert.Equal(new[] { "info", "--format", "{{json .}}" }, args); return Task.FromResult(new DockerResult(0, "{\"ID\":\"different-engine\"}", "")); });
        var probe = new DockerCapabilityProbe(docker, fixture.Directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.RequireEngineAsync(new(DockerEndpoint.Default, "original-engine", "28.0.0", "1.48", "2.24.6"), TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Real_process_drains_both_streams_concurrently_redacts_chunk_boundaries_and_bounds_retained_output()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        var vault = new DpapiSecretVault(fixture.Worker, fixture.Clock);
        string reference = await vault.SaveAsync("process canary", Support.Program.ProcessSentinel.AsMemory(), TestContext.Current.CancellationToken);
        // apphost resolves its original DLL name; copy only the executable beside the existing test payload.
        string target = Path.Combine(AppContext.BaseDirectory, "docker.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "EnvironmentBuilder.IntegrationTests.exe"), target, true);
        try
        {
            var runner = new DockerProcessRunner(target, DockerEndpoint.Default, () => SecretContext.ResolveAsync(vault, [reference], CancellationToken.None));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            DockerResult result = await runner.RunAsync(["process-contract-fixture"], deadline.Token);
            Assert.Equal(0, result.ExitCode); Assert.True(result.Truncated);
            Assert.StartsWith("stdout [REDACTED]", result.Output); Assert.StartsWith("stderr [REDACTED]", result.Error);
            Assert.DoesNotContain(Support.Program.ProcessSentinel, result.Output + result.Error);
            Assert.True(result.Output.Length <= 4 * 1024 * 1024); Assert.True(result.Error.Length <= 4 * 1024 * 1024);
        }
        finally { File.Delete(target); }
    }
    [Fact]
    public void Process_contract_uses_explicit_endpoint_arguments_and_an_environment_allowlist()
    {
        ProcessStartInfo info = DockerProcessRunner.CreateStartInfo(@"C:\Program Files\Docker\docker.exe",
            DockerEndpoint.Default, ["inspect", "a; echo unsafe"], new Dictionary<string, string>
            { ["PATH"] = "safe", ["DOCKER_HOST"] = "tcp://foreign:2375", ["COMPOSE_FILE"] = "foreign", ["SECRET"] = "plaintext" });
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardOutput && info.RedirectStandardError);
        Assert.Equal(new[] { "--host", DockerEndpoint.Default, "inspect", "a; echo unsafe" }, info.ArgumentList);
        Assert.False(info.Environment.ContainsKey("DOCKER_HOST"));
        Assert.False(info.Environment.ContainsKey("COMPOSE_FILE"));
        Assert.False(info.Environment.ContainsKey("SECRET"));
    }

    [Theory]
    [InlineData("2.24.6", true)]
    [InlineData("5.4.0", true)]
    [InlineData("5.6.2", true)]
    [InlineData("2.24.5", false)]
    [InlineData("3.0.0", false)]
    [InlineData("unexpected", false)]
    public void Compose_versions_are_explicitly_supported(string version, bool supported) =>
        Assert.Equal(supported, DockerCapabilityProbe.SupportsCompose(version));

    [Theory]
    [InlineData("tcp://localhost:2375")]
    [InlineData("ssh://localhost")]
    [InlineData("npipe:////remote/pipe/docker_engine")]
    public void Remote_endpoints_are_rejected(string endpoint) => Assert.Throws<ArgumentException>(() => DockerEndpoint.RequireLocal(endpoint));

    [Fact]
    public void Names_and_copied_labels_do_not_authorize_deletion_without_identity_and_consumers()
    {
        OwnedResource ledger = Ledger();
        ResourceEvidence evidence = Evidence(ledger);
        Assert.True(ResourceOwnershipVerifier.Verify(ledger, evidence).Verified);
        Assert.False(ResourceOwnershipVerifier.Verify(null, evidence).Verified);
        Assert.False(ResourceOwnershipVerifier.Verify(ledger, evidence with { Identity = "recreated" }).Verified);
        Assert.False(ResourceOwnershipVerifier.Verify(ledger, evidence with { ConsumersKnown = false }).Verified);
        Assert.False(ResourceOwnershipVerifier.Verify(ledger, evidence with { Consumers = ["foreign-stopped-container"] }).Verified);
        Assert.False(ResourceOwnershipVerifier.Verify(ledger, evidence with { Volume = evidence.Volume! with { CreatedAt = "later" } }).Verified);
        Assert.False(ResourceOwnershipVerifier.Verify(ledger, evidence with { Volume = null }).Verified);
    }

    internal static OwnedResource Ledger() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ResourceKind.Volume, 1,
        Guid.NewGuid(), "volume", ImmutableDictionary<string, string>.Empty, "volume",
        new VolumeIncarnation("2026-10-23T00:00:00Z", "local", "local", ImmutableDictionary<string, string>.Empty), [], true, Guid.NewGuid(), false);
    internal static ResourceEvidence Evidence(OwnedResource ledger) => new(ledger.Kind, ledger.Name, ledger.ActualDockerId!,
        ResourceOwnershipVerifier.Labels(ledger), ledger.Volume, [], true, "available");
}
