using System.Net.Http.Json;
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
using EnvironmentBuilder.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace EnvironmentBuilder.IntegrationTests.Docker;

[Trait("Category", "Docker")]
public sealed class StopStartPersistenceTests
{
    [Fact]
    public async Task Twenty_cycles_preserve_business_data_generation_attempt_history_and_neighbour_with_interruption_evidence()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("ENVIRONMENTBUILDER_DOCKER_TESTS"));
        var evidence = new List<object>();
        string status = "Blocked";
        int completedCycles = 0;
        string? blocker = null;
        try
        {
            using IsolatedTestStore isolated = IsolatedTestStore.Create();
            Guid[] ids = [isolated.CreateEnvironmentId(), isolated.CreateEnvironmentId()];
            var navigation = new NavigationService(); var themes = new ThemeService();
            await using AppServices services = await AppServices.CreateAsync(new ShellViewModel(navigation, themes), navigation, themes, isolated.DataDirectory);
            var provider = services.Provider;
            var reader = provider.GetRequiredService<StrictDocumentReader>();
            var template = reader.Read<IsolatedEnvironment>(await File.ReadAllBytesAsync(Path.Combine(DemoContractTests.DemoRoot, "descriptors/environment.json"), TestContext.Current.CancellationToken));
            string secret = await provider.GetRequiredService<ISecretVault>().SaveAsync("US3 test broker", "us3-test-password".AsMemory(), TestContext.Current.CancellationToken);
            template = reader.Read<IsolatedEnvironment>(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(template, CanonicalJson.Options).Replace(DockerProcessFactory.References(template).Single(), secret, StringComparison.Ordinal)));
            string sourceRoot = Path.Combine(isolated.DataDirectory, "demo-source");
            StartInspectDockerTests.CopyDemoSource(sourceRoot);
            await provider.GetRequiredService<ILocalPathMappings>().MapAsync("local-path://demo", sourceRoot, TestContext.Current.CancellationToken);
            string zip = Path.Combine(isolated.DataDirectory, "small.zip");
            DemoContractTests.CreateArchive(Path.Combine(DemoContractTests.DemoRoot, "seedpacks/small"), zip);
            await provider.GetRequiredService<SeedPackImporter>().ImportAsync(zip, TestContext.Current.CancellationToken);
            IsolatedEnvironment[] environments = ids.Select(id => template with { Id = id, Name = "us3-test-" + id.ToString("N") }).ToArray();
            foreach (var environment in environments) { await provider.GetRequiredService<IEnvironmentStore>().SaveNewAsync(environment, TestContext.Current.CancellationToken); }
            var backend = new StartInspectDockerTests.CleanupBackend(provider, environments, isolated);
            await using var fixture = await ScopedDockerFixture.CreateAsync(isolated, DockerTestManifest.ForStore(isolated, ids), backend, TestContext.Current.CancellationToken);
            status = "Failed";
            var start = provider.GetRequiredService<StartEnvironmentService>();
            var stop = provider.GetRequiredService<StopEnvironmentService>();
            var observations = provider.GetRequiredService<IEnvironmentObservationSource>();
            var journal = provider.GetRequiredService<SqliteWorker>();
            foreach (var environment in environments)
            {
                fixture.RequireAllowedEnvironment(environment.Id);
                var handle = start.Start(environment, null, (_, _, _) => Task.FromResult(true)); await handle.Completion;
                Assert.Equal(OperationState.Succeeded, handle.Current.State);
            }
            IsolatedEnvironment selected = environments[0]; IsolatedEnvironment neighbour = environments[1];
            var initial = await observations.ReadAsync(selected, Guid.NewGuid(), null, TestContext.Current.CancellationToken);
            Guid requestId = selected.Components.Single(c => c.DefinitionSnapshot.Contract.Id == "demo.requests").Id;
            var endpoint = selected.Components.Single(c => c.Id == requestId).DefinitionSnapshot.Endpoints.First();
            Uri address = LoopbackEndpointResolver.Resolve(endpoint, initial.Components.Single(c => c.ComponentId == requestId));
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false });
            using var business = new HttpRequestMessage(HttpMethod.Post, new Uri(address, "/business")) { Content = JsonContent.Create(new { id = "us3-sentinel", value = "preserve through Stop/Start" }) };
            business.Headers.Add("Idempotency-Key", "us3-sentinel-" + selected.Id);
            using var response = await http.SendAsync(business, TestContext.Current.CancellationToken); response.EnsureSuccessStatusCode();
            await WaitForCountsAsync(provider, selected, 11);
            var dataBefore = await BusinessAsync(provider, selected); var neighbourBefore = await BusinessAsync(provider, neighbour);
            string attempts = await AttemptHistoryAsync(journal);
            var ledger = provider.GetRequiredService<ResourceLedgerStore>();
            string resources = CanonicalJson.Hash(await ledger.ReadAsync(selected.Id, TestContext.Current.CancellationToken));
            for (int cycle = 1; cycle <= 20; cycle++)
            {
                var stopped = stop.Stop(selected); await stopped.Completion;
                Assert.Equal(OperationState.Succeeded, stopped.Current.State);
                var actual = await observations.ReadAsync(selected, Guid.NewGuid(), null, TestContext.Current.CancellationToken);
                Assert.True(QuiescenceGuard.Check(actual).ConfirmedQuiescent);
                if (cycle == 1)
                {
                    // Cancellation before admission's first effect leaves stopped data and durable successes intact.
                    var cancelled = start.Start(selected, null, (_, _, _) => Task.FromResult(false)); await cancelled.Completion;
                    Assert.Equal(OperationState.Cancelled, cancelled.Current.State);
                    Assert.True(QuiescenceGuard.Check(await observations.ReadAsync(selected, Guid.NewGuid(), null, TestContext.Current.CancellationToken)).ConfirmedQuiescent);
                }
                var resumed = start.Start(selected, null, (_, _, _) => Task.FromResult(true)); await resumed.Completion;
                Assert.Equal(OperationState.Succeeded, resumed.Current.State);
                Assert.Equal(dataBefore, await BusinessAsync(provider, selected));
                Assert.Equal(neighbourBefore, await BusinessAsync(provider, neighbour));
                Assert.Equal(attempts, await AttemptHistoryAsync(journal));
                Assert.Equal(resources, CanonicalJson.Hash(await ledger.ReadAsync(selected.Id, TestContext.Current.CancellationToken)));
                Assert.Equal(CanonicalJson.Hash(selected), CanonicalJson.Hash((await provider.GetRequiredService<IEnvironmentStore>().ListAsync(TestContext.Current.CancellationToken)).Single(e => e.Id == selected.Id).Document!));
                evidence.Add(new { Cycle = cycle, Stop = stopped.Current.Id, Start = resumed.Current.Id, Generation = resumed.Current.Plan.DataGenerationId, Quiescence = true, Data = dataBefore, AttemptsHash = attempts });
                completedCycles++;
            }
            var interrupted = new StopEnvironmentService(provider.GetRequiredService<OperationCoordinator>(), new InterruptedStop(provider.GetRequiredService<IStopRuntime>()), provider.GetRequiredService<IClock>()).Stop(selected);
            await interrupted.Completion;
            Assert.Equal(OperationState.NeedsAttention, interrupted.Current.State);
            Assert.NotNull(provider.GetRequiredService<OperationCoordinator>().AdmissionBlockReason(selected.Id));
            // Explicit Stop remains available after an interrupted operation; it never resolves the old outcome implicitly.
            var finish = stop.Stop(selected); await finish.Completion;
            Assert.Equal(OperationState.Succeeded, finish.Current.State);
            Assert.Equal(neighbourBefore, await BusinessAsync(provider, neighbour));
            Assert.Equal(attempts, await AttemptHistoryAsync(journal));
            evidence.Add(new { InterruptedStop = interrupted.Current.State, ExplicitStop = finish.Current.State, NeighbourUnchanged = true });
            status = "Passed";
        }
        catch (DockerTestBlockedException error) { blocker = error.Message; throw; }
        finally
        {
            string? output = Environment.GetEnvironmentVariable("ENVIRONMENTBUILDER_RUNTIME_ARTIFACTS");
            if (output is not null)
            {
                Directory.CreateDirectory(output);
                await File.WriteAllTextAsync(Path.Combine(output, "stop-start-persistence.json"), JsonSerializer.Serialize(new { Status = status, Blocker = blocker, CompletedCycles = completedCycles, Evidence = evidence }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
            }
        }
    }

    private static Task<string> AttemptHistoryAsync(SqliteWorker worker) => worker.ReadAsync(connection =>
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT json FROM InitializationAttempts ORDER BY idempotencyKey,attempt,state";
        using var rows = command.ExecuteReader(); var history = new List<string>();
        while (rows.Read()) { history.Add(rows.GetString(0)); }
        return CanonicalJson.Hash(history);
    }, TestContext.Current.CancellationToken);

    private static async Task<string> BusinessAsync(ServiceProvider provider, IsolatedEnvironment environment)
    {
        var observed = await provider.GetRequiredService<IEnvironmentObservationSource>().ReadAsync(environment, Guid.NewGuid(), null, TestContext.Current.CancellationToken);
        Assert.Equal("current", observed.Freshness);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false });
        var data = new List<string>();
        foreach (var component in environment.Components.Where(c => c.DefinitionSnapshot.Contract.Id != "demo.broker"))
        {
            if (component.DefinitionSnapshot.Endpoints.IsEmpty) { continue; }
            Uri address = LoopbackEndpointResolver.Resolve(component.DefinitionSnapshot.Endpoints.First(), observed.Components.Single(c => c.ComponentId == component.Id));
            data.Add(await http.GetStringAsync(new Uri(address, "/probe"), TestContext.Current.CancellationToken));
        }
        return CanonicalJson.Hash(data);
    }

    private static async Task WaitForCountsAsync(ServiceProvider provider, IsolatedEnvironment environment, int expected)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false });
        var observation = await provider.GetRequiredService<IEnvironmentObservationSource>().ReadAsync(environment, Guid.NewGuid(), null, timeout.Token);
        foreach (var component in environment.Components.Where(c => !c.DefinitionSnapshot.Endpoints.IsEmpty))
        {
            Uri address = LoopbackEndpointResolver.Resolve(component.DefinitionSnapshot.Endpoints.First(), observation.Components.Single(c => c.ComponentId == component.Id));
            while (true)
            {
                using var counts = JsonDocument.Parse(await http.GetStringAsync(new Uri(address, "/counts"), timeout.Token));
                if (counts.RootElement.GetProperty("records").GetInt32() == expected && counts.RootElement.GetProperty("pendingEvents").GetInt32() == 0) { break; }
                await Task.Delay(200, timeout.Token);
            }
        }
    }

    private sealed class InterruptedStop(IStopRuntime runtime) : IStopRuntime
    {
        public async Task<IStopSession> PrepareAsync(IsolatedEnvironment environment, Guid operationId, int timeoutSeconds, CancellationToken token) =>
            new Session(await runtime.PrepareAsync(environment, operationId, timeoutSeconds, token));
        private sealed class Session(IStopSession inner) : IStopSession
        {
            public OperationPlan Plan => inner.Plan;
            public Task<EnvironmentObservation> ReadAsync(CancellationToken token) => inner.ReadAsync(token);
            public async Task StopAsync(OwnedResource container, OperationExecution execution, CancellationToken token)
            { await inner.StopAsync(container, execution, token); throw new IOException("Injected stop interruption after accepted effect."); }
        }
    }
}
