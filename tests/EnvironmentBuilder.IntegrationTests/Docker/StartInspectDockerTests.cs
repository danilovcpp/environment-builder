using System.Collections.Immutable;
using System.Text.Json;
using EnvironmentBuilder.App.Composition;
using EnvironmentBuilder.App.Shell;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Infrastructure.Docker;
using EnvironmentBuilder.Infrastructure.Imports;
using EnvironmentBuilder.Infrastructure.Initialization;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.Infrastructure.Security;
using EnvironmentBuilder.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace EnvironmentBuilder.IntegrationTests.Docker;

[Trait("Category", "Docker")]
public sealed class StartInspectDockerTests
{
    [Fact]
    public async Task Two_prepared_demo_environments_start_initialize_exchange_and_expose_real_observations()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("ENVIRONMENTBUILDER_DOCKER_TESTS"));
        using IsolatedTestStore isolated = IsolatedTestStore.Create();
        Guid[] ids = [isolated.CreateEnvironmentId(), isolated.CreateEnvironmentId(), isolated.CreateEnvironmentId(), isolated.CreateEnvironmentId()];
        var navigation = new NavigationService(); var themes = new ThemeService();
        await using AppServices services = await AppServices.CreateAsync(new ShellViewModel(navigation, themes), navigation, themes, isolated.DataDirectory);
        ServiceProvider provider = services.Provider;
        var reader = provider.GetRequiredService<StrictDocumentReader>();
        var template = reader.Read<IsolatedEnvironment>(await File.ReadAllBytesAsync(Path.Combine(DemoContractTests.DemoRoot, "descriptors/environment.json"), TestContext.Current.CancellationToken));
        string secret = await provider.GetRequiredService<ISecretVault>().SaveAsync("test broker password", "demo-test-password".AsMemory(), TestContext.Current.CancellationToken);
        string templateJson = JsonSerializer.Serialize(template, CanonicalJson.Options).Replace(DockerProcessFactory.References(template).Single(), secret, StringComparison.Ordinal);
        template = reader.Read<IsolatedEnvironment>(System.Text.Encoding.UTF8.GetBytes(templateJson));
        string sourceRoot = Path.Combine(isolated.DataDirectory, "demo-source");
        CopyDemoSource(sourceRoot);
        await provider.GetRequiredService<ILocalPathMappings>().MapAsync("local-path://demo", sourceRoot, TestContext.Current.CancellationToken);
        string zip = Path.Combine(isolated.DataDirectory, "small.zip");
        DemoContractTests.CreateArchive(Path.Combine(DemoContractTests.DemoRoot, "seedpacks/small"), zip);
        await provider.GetRequiredService<SeedPackImporter>().ImportAsync(zip, TestContext.Current.CancellationToken);
        var environments = ids.Select(id => template with { Id = id, Name = "test-" + id.ToString("N") }).ToArray();
        environments[2] = WithReadinessFailure(environments[2]);
        environments[3] = WithInitializationFailure(environments[3]);
        foreach (var environment in environments) { await provider.GetRequiredService<IEnvironmentStore>().SaveNewAsync(environment, TestContext.Current.CancellationToken); }
        var backend = new CleanupBackend(provider, environments, isolated);
        await using ScopedDockerFixture fixture = await ScopedDockerFixture.CreateAsync(isolated, DockerTestManifest.ForStore(isolated, ids), backend, TestContext.Current.CancellationToken);
        var evidence = new List<object>();
        try
        {
            foreach (IsolatedEnvironment environment in environments.Take(2))
            {
                fixture.RequireAllowedEnvironment(environment.Id);
                // Declining unknown→resolved input review has no resource ledger or runtime mutations.
                OperationHandle declined = provider.GetRequiredService<StartEnvironmentService>().Start(environment, null, (_, _, _) => Task.FromResult(false));
                await declined.Completion.WaitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(OperationState.Cancelled, declined.Current.State);
                Assert.Empty(await provider.GetRequiredService<ResourceLedgerStore>().ReadAsync(environment.Id, TestContext.Current.CancellationToken));
                PreviewSnapshot shown = await provider.GetRequiredService<PreviewInputCapture>().CaptureAsync(environment, TestContext.Current.CancellationToken);
                await File.AppendAllTextAsync(Path.Combine(sourceRoot, "Shared/DemoHost.cs"), "\n// isolated source delta fixture\n", TestContext.Current.CancellationToken);
                bool sourceDelta = false;
                OperationHandle changedSource = provider.GetRequiredService<StartEnvironmentService>().Start(environment, shown, (delta, _, _) =>
                { sourceDelta = delta.Any(d => d.Reference.Contains(":build:", StringComparison.Ordinal)); return Task.FromResult(false); });
                await changedSource.Completion.WaitAsync(TestContext.Current.CancellationToken);
                Assert.True(sourceDelta); Assert.Equal(OperationState.Cancelled, changedSource.Current.State);
                string image = ((ImageSource)environment.Components.Single(c => c.Source is ImageSource).Source).Reference;
                PreviewSnapshot historicalImage = await provider.GetRequiredService<PreviewInputCapture>().CaptureAsync(environment,
                    new Dictionary<string, string> { [image] = "sha256:" + new string('0', 64) }, TestContext.Current.CancellationToken);
                bool imageDelta = false;
                OperationHandle changedImage = provider.GetRequiredService<StartEnvironmentService>().Start(environment, historicalImage, (delta, _, _) =>
                { imageDelta = delta.Any(d => d.Reference.Contains(":image:", StringComparison.Ordinal)); return Task.FromResult(false); });
                await changedImage.Completion.WaitAsync(TestContext.Current.CancellationToken);
                Assert.True(imageDelta); Assert.Equal(OperationState.Cancelled, changedImage.Current.State);
                Assert.Empty(await provider.GetRequiredService<ResourceLedgerStore>().ReadAsync(environment.Id, TestContext.Current.CancellationToken));
                OperationHandle handle = provider.GetRequiredService<StartEnvironmentService>().Start(environment, null, (_, _, _) => Task.FromResult(true));
                Assert.Same(handle, provider.GetRequiredService<StartEnvironmentService>().Start(environment, null, (_, _, _) => throw new InvalidOperationException("Duplicate Start must reuse the operation.")));
                Assert.Same(handle, provider.GetRequiredService<OperationCoordinator>().Active.Single(h => h.Current.EnvironmentId == environment.Id));
                await handle.Completion.WaitAsync(TestContext.Current.CancellationToken);
                Assert.True(handle.Current.State == OperationState.Succeeded, JsonSerializer.Serialize(handle.Current, CanonicalJson.Options));
                EnvironmentObservation observed = await provider.GetRequiredService<IEnvironmentObservationSource>().ReadAsync(environment, Guid.NewGuid(), null, TestContext.Current.CancellationToken);
                Assert.Equal("current", observed.Freshness); Assert.Equal(4, observed.Components.Length);
                Assert.All(observed.Components, component => Assert.Equal("running", component.Status));
                Assert.All(observed.Resources, resource => Assert.True(resource.Ownership.Verified, resource.Ownership.ErrorCode));
                var docker = provider.GetRequiredService<DockerProcessFactory>().Create(environment);
                using var sampler = new MetricSampler(docker);
                var sample = await sampler.SampleAsync(observed.Components[0].ContainerId!, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
                Assert.NotNull(sample.Measurements["cpu"].Value);
                await ProbeBusinessAsync(environment, observed);
                ResourceObservation resource = observed.Resources.First(r => r.Ledger.Kind == ResourceKind.Container && r.Actual is not null);
                await using (var logs = new LogObservationSession(docker, resource.Actual!.Identity, new LogMemoryBudget()))
                {
                    logs.Start(resource.Ledger, resource.Actual);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    while (logs.Snapshot().Lines.Count == 0) { await Task.Delay(100, deadline.Token); }
                    Assert.DoesNotContain("demo-test-password", string.Join('\n', logs.Snapshot().Lines));
                }
                evidence.Add(new { environment.Id, OperationId = handle.Current.Id, handle.Current.State, observed.Freshness, Components = observed.Components.Select(c => new { c.ComponentId, c.ContainerId, c.Status, c.Health, c.Ports }), Cpu = sample.Measurements["cpu"].Value });
            }
            foreach (IsolatedEnvironment environment in environments.Skip(2))
            {
                fixture.RequireAllowedEnvironment(environment.Id);
                OperationHandle failed = provider.GetRequiredService<StartEnvironmentService>().Start(environment, null, (_, _, _) => Task.FromResult(true));
                await failed.Completion.WaitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(OperationState.NeedsAttention, failed.Current.State);
                EnvironmentObservation observed = await provider.GetRequiredService<IEnvironmentObservationSource>().ReadAsync(environment, Guid.NewGuid(), null, TestContext.Current.CancellationToken);
                Assert.Equal("current", observed.Freshness);
                Assert.True(observed.Components.Count(c => c.Status == "running") >= 2, "Failed readiness/init must preserve accepted running containers.");
                Assert.Contains(observed.Components, c => c.Status == "running" && !c.ApplicationReady);
                evidence.Add(new { environment.Id, OperationId = failed.Current.Id, failed.Current.State, observed.Freshness, Running = observed.Components.Count(c => c.Status == "running") });
            }
        }
        finally
        {
            string? output = Environment.GetEnvironmentVariable("ENVIRONMENTBUILDER_RUNTIME_ARTIFACTS");
            if (output is not null) { Directory.CreateDirectory(output); await File.WriteAllTextAsync(Path.Combine(output, "live-evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None); }
        }
    }
    private static IsolatedEnvironment WithReadinessFailure(IsolatedEnvironment environment)
    {
        int index = Enumerable.Range(0, environment.Components.Length).Single(i => environment.Components[i].DefinitionSnapshot.Contract.Id == "demo.requests");
        EnvironmentComponent component = environment.Components[index];
        return environment with
        {
            Components = environment.Components.SetItem(index, component with
            { Environment = component.Environment.Add("ASPNETCORE_URLS", new() { Literal = "http://+:8081" }), DefinitionSnapshot = component.DefinitionSnapshot with { Readiness = component.DefinitionSnapshot.Readiness with { TimeoutSeconds = 3 } } })
        };
    }
    private static IsolatedEnvironment WithInitializationFailure(IsolatedEnvironment environment)
    {
        int index = Enumerable.Range(0, environment.Components.Length).Single(i => environment.Components[i].DefinitionSnapshot.Contract.Id == "demo.requests");
        EnvironmentComponent component = environment.Components[index];
        var initializer = component.DefinitionSnapshot.Initialization!;
        initializer = initializer with { Steps = initializer.Steps.SetItem(0, initializer.Steps[0] with { Expect = new([201], []) }) };
        return environment with
        {
            Components = environment.Components.SetItem(index, component with { DefinitionSnapshot = component.DefinitionSnapshot with { Initialization = initializer } }),
            Baseline = new(environment.Baseline.Applications.Select(a => a.ComponentId == component.Id && a.SeedPack is null ? a with { Initializer = initializer } : a).ToImmutableArray())
        };
    }
    internal static void CopyDemoSource(string target)
    {
        Directory.CreateDirectory(target);
        foreach (string name in new[] { "Directory.Build.props", "Directory.Packages.props", ".dockerignore" }) { File.Copy(Path.Combine(DemoContractTests.DemoRoot, name), Path.Combine(target, name)); }
        foreach (string folder in new[] { "Shared", "Requests.Api", "Scheduler.Api", "Reporting.Api" })
        {
            Directory.CreateDirectory(Path.Combine(target, folder));
            foreach (string file in Directory.EnumerateFiles(Path.Combine(DemoContractTests.DemoRoot, folder))) { File.Copy(file, Path.Combine(target, folder, Path.GetFileName(file))); }
        }
    }
    private static async Task ProbeBusinessAsync(IsolatedEnvironment environment, EnvironmentObservation observation)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        Uri Endpoint(string service) => LoopbackEndpointResolver.Resolve(environment.Components.Single(c => c.DefinitionSnapshot.Contract.Id == "demo." + service).DefinitionSnapshot.Endpoints.Single(),
            observation.Components.Single(c => c.ComponentId == environment.Components.Single(e => e.DefinitionSnapshot.Contract.Id == "demo." + service).Id));
        foreach (string service in new[] { "requests", "scheduler", "reporting" })
        {
            using var counts = JsonDocument.Parse(await client.GetStringAsync(new Uri(Endpoint(service), "/counts"), TestContext.Current.CancellationToken));
            Assert.Equal(10, counts.RootElement.GetProperty("records").GetInt32());
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint("requests"), "/business")) { Content = new StringContent("{\"id\":\"sentinel\",\"value\":42}", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", "business-sentinel");
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken); Assert.Equal(204, (int)response.StatusCode);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            using JsonDocument counts = JsonDocument.Parse(await client.GetStringAsync(new Uri(Endpoint("reporting"), "/counts"), deadline.Token));
            if (counts.RootElement.GetProperty("records").GetInt32() == 11) { break; }
            await Task.Delay(200, deadline.Token);
        }
    }

    internal sealed class CleanupBackend(ServiceProvider provider, IsolatedEnvironment[] environments, IsolatedTestStore isolated) : IScopedDockerBackend
    {
        public async Task<string?> CheckPrerequisitesAsync(CancellationToken token)
        {
            try
            {
                await new DockerCapabilityProbe(provider.GetRequiredService<DockerProcessFactory>().Create(environments[0]), provider.GetRequiredService<AppDataDirectory>()).ProbeAsync(token);
                return null;
            }
            catch (NotSupportedException error) { return error.Message; }
            catch (PreflightBlockedException error) { return error.Code; }
            catch (Exception error) when (error is not OutOfMemoryException) { return error.GetType().Name; }
        }
        public async Task CleanupOwnedResourcesAsync(DockerTestManifest manifest, CancellationToken token)
        {
            try { await CleanupAsync(manifest, token); }
            catch
            {
                isolated.RetainForReconciliation();
                string? output = Environment.GetEnvironmentVariable("ENVIRONMENTBUILDER_RUNTIME_ARTIFACTS");
                if (output is not null)
                {
                    Directory.CreateDirectory(output);
                    await File.WriteAllTextAsync(Path.Combine(output, "cleanup-blocked.json"), JsonSerializer.Serialize(new { Status = "Blocked", Store = isolated.DataDirectory, Environments = manifest.EnvironmentIds }), CancellationToken.None);
                }
                throw;
            }
        }
        private async Task CleanupAsync(DockerTestManifest manifest, CancellationToken token)
        {
            foreach (IsolatedEnvironment environment in environments)
            {
                if (!manifest.EnvironmentIds.Contains(environment.Id)) { throw new InvalidOperationException("Cleanup outside test allowlist."); }
                ResourceLedgerStore ledger = provider.GetRequiredService<ResourceLedgerStore>();
                IReadOnlyList<OwnedResource> resources = await ledger.ReadAsync(environment.Id, token);
                if (resources.Count == 0) { continue; }
                IDockerProcess docker = provider.GetRequiredService<DockerProcessFactory>().Create(environment);
                var reader = new ResourceConsumerReader(docker);
                var probe = new DockerCapabilityProbe(docker, provider.GetRequiredService<AppDataDirectory>());
                DockerCapabilities engine = await probe.ProbeAsync(token);
                var plan = new OperationPlan(Guid.NewGuid(), environment.Id, 1, CanonicalJson.Hash(environment), resources[0].DataGenerationId, CanonicalJson.Hash(environment.Baseline), resources.ToImmutableArray(), [], ["test-cleanup"], "Only exact test resources") { EngineId = engine.EngineId, Endpoint = engine.Endpoint };
                var operation = new Operation(Guid.NewGuid(), environment.Id, "test-cleanup", OperationState.Planned, DateTimeOffset.UtcNow, 1, plan.ConfigurationHash, plan);
                IOperationStore journal = provider.GetRequiredService<IOperationStore>(); await journal.SaveAsync(operation, token);
                var runner = new DurableEffectRunner(journal);
                foreach (OwnedResource resource in resources.OrderBy(r => r.Kind == ResourceKind.Container ? 0 : r.Kind == ResourceKind.Volume ? 1 : 2))
                {
                    await probe.RequireEngineAsync(engine, token);
                    var all = await reader.AllContainersAsync(token);
                    DockerResourceRead? actual = await reader.ReadAsync(resource, all, token);
                    if (actual is null) { continue; }
                    Assert.True(ResourceOwnershipVerifier.Verify(resource, actual.Evidence).Verified, "Cleanup blocked by ownership/consumer verification.");
                    if (resource.Kind == ResourceKind.Container && actual.Component?.Status == "running")
                    {
                        StepState stopped = await runner.RunAsync(new(operation.Id, "stop-" + resource.ResourceId, "test-stop", actual.Evidence.Identity), async safe =>
                        { Assert.Equal(0, (await docker.RunAsync(["container", "stop", "--time", "10", actual.Evidence.Identity], safe)).ExitCode); }, async safe =>
                        { var after = await reader.ReadAsync(resource, await reader.AllContainersAsync(safe), safe); return new(actual.Evidence.Identity, after?.Component?.Status is "exited" or "created", null); }, token);
                        Assert.Equal(StepState.Succeeded, stopped);
                    }
                    string kind = resource.Kind.ToString().ToLowerInvariant();
                    StepState removed = await runner.RunAsync(new(operation.Id, "remove-" + resource.ResourceId, "test-remove", actual.Evidence.Identity), async safe =>
                    { Assert.Equal(0, (await docker.RunAsync([kind, "rm", actual.Evidence.Identity], safe)).ExitCode); }, async safe =>
                    { return new(actual.Evidence.Identity, await reader.ReadAsync(resource, await reader.AllContainersAsync(safe), safe) is null, null); }, token);
                    Assert.Equal(StepState.Succeeded, removed);
                }
                await journal.SaveAsync(operation with { State = OperationState.Succeeded, CompletedAt = DateTimeOffset.UtcNow }, token);
            }
        }
    }
}
