using System.Net;

namespace EnvironmentBuilder.IntegrationTests.Support;

public sealed class TestHarnessTests
{
    [Fact]
    public void Stores_have_independent_directories_and_ids()
    {
        using var first = IsolatedTestStore.Create();
        using var second = IsolatedTestStore.Create();
        Guid environmentId = first.CreateEnvironmentId();

        Assert.NotEqual(first.DataDirectory, second.DataDirectory);
        Assert.NotEqual(first.InstallationId, second.InstallationId);
        Assert.True(first.OwnsEnvironment(environmentId));
        Assert.False(second.OwnsEnvironment(environmentId));
        Assert.Throws<ArgumentException>(() => first.GetPath("..", "outside.json"));
        Assert.Throws<ArgumentException>(() => first.GetPath(Path.GetTempPath()));
    }

    [Fact]
    public void Disposing_a_store_preserves_its_siblings()
    {
        using var sibling = IsolatedTestStore.Create();
        var store = IsolatedTestStore.Create();
        string child = store.GetPath("state.db");
        File.WriteAllText(child, "isolated fixture");

        store.Dispose();
        store.Dispose();

        Assert.False(Directory.Exists(store.DataDirectory));
        Assert.True(Directory.Exists(sibling.DataDirectory));
        Assert.Throws<ObjectDisposedException>(() => store.CreateEnvironmentId());
    }

    [Theory]
    [InlineData(MutationBoundary.BeforeEffect, 0)]
    [InlineData(MutationBoundary.AfterEffect, 1)]
    public async Task Faults_distinguish_an_unattempted_effect_from_a_lost_result(
        MutationBoundary boundary, int expectedEffects)
    {
        var faults = new FaultController();
        faults.FailNext("create", boundary);
        int effects = 0;

        await Assert.ThrowsAsync<InjectedFaultException>(() => faults.RunMutationAsync(
            "create", _ => { effects++; return Task.CompletedTask; }, TestContext.Current.CancellationToken));

        Assert.Equal(expectedEffects, effects);
        await faults.RunMutationAsync(
            "create", _ => { effects++; return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        Assert.Equal(expectedEffects + 1, effects);
    }

    [Fact]
    public async Task Cancellation_before_a_mutation_prevents_the_effect()
    {
        var faults = new FaultController();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool effectRan = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => faults.RunMutationAsync(
            "create", _ => { effectRan = true; return Task.CompletedTask; }, cancellation.Token));

        Assert.False(effectRan);
    }

    [Fact]
    public async Task Clock_process_and_http_are_controlled_without_live_dependencies()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(1), clock.GetUtcNow());
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromSeconds(-1)));

        var process = new FakeProcessRunner();
        process.Enqueue(new TestProcessResult(7, "partial", "failure"));
        TestProcessResult result = await process.RunAsync(
            new TestProcessRequest("docker", ["inspect", "test-id"]), TestContext.Current.CancellationToken);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("test-id", Assert.Single(process.Requests).Arguments[1]);

        using var handler = new FakeHttpMessageHandler();
        handler.Enqueue((request, _) => Task.FromResult(new HttpResponseMessage(
            request.Method == HttpMethod.Get ? HttpStatusCode.OK : HttpStatusCode.BadRequest)));
        using var client = new HttpClient(handler);
        using HttpResponseMessage response = await client.GetAsync(
            "http://fixture.invalid/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync(
            "http://fixture.invalid/unplanned", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Http_double_can_reproduce_a_lost_response_after_an_effect()
    {
        int effects = 0;
        using var handler = new FakeHttpMessageHandler();
        handler.Enqueue((_, _) =>
        {
            effects++;
            throw new HttpRequestException("Response lost after mutation.");
        });
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync(
            "http://fixture.invalid/init", null, TestContext.Current.CancellationToken));

        Assert.Equal(1, effects);
    }
}
