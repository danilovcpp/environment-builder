namespace EnvironmentBuilder.IntegrationTests.Support;

public sealed class ScopedDockerFixtureTests
{
    [Fact]
    public async Task Missing_opt_in_blocks_before_probing_or_cleanup()
    {
        using var store = IsolatedTestStore.Create();
        DockerTestManifest manifest = DockerTestManifest.ForStore(store, [store.CreateEnvironmentId()]);
        var backend = new RecordingBackend();

        await Assert.ThrowsAsync<DockerTestBlockedException>(() => ScopedDockerFixture.CreateAsync(
            store, manifest, backend, null, TestContext.Current.CancellationToken));

        Assert.Equal(0, backend.Probes);
        Assert.Empty(backend.CleanedEnvironments);
    }

    [Fact]
    public async Task Unavailable_required_engine_is_a_failure_when_opted_in()
    {
        using var store = IsolatedTestStore.Create();
        DockerTestManifest manifest = DockerTestManifest.ForStore(store, [store.CreateEnvironmentId()]);
        var backend = new RecordingBackend { PrerequisiteFailure = "Engine unavailable" };

        DockerTestBlockedException error = await Assert.ThrowsAsync<DockerTestBlockedException>(
            () => ScopedDockerFixture.CreateAsync(store, manifest, backend, "1", TestContext.Current.CancellationToken));

        Assert.Contains("Engine unavailable", error.Message);
        Assert.Empty(backend.CleanedEnvironments);
    }

    [Fact]
    public async Task Manifest_rejects_empty_foreign_and_duplicate_environment_ids()
    {
        using var store = IsolatedTestStore.Create();
        using var foreign = IsolatedTestStore.Create();
        Guid allowed = store.CreateEnvironmentId();
        var backend = new RecordingBackend();

        Assert.Throws<ArgumentException>(() => DockerTestManifest.ForStore(store, []));
        Assert.Throws<ArgumentException>(() => DockerTestManifest.ForStore(store, [allowed, allowed]));
        DockerTestManifest manifest = DockerTestManifest.ForStore(foreign, [foreign.CreateEnvironmentId()]);
        await Assert.ThrowsAsync<ArgumentException>(() => ScopedDockerFixture.CreateAsync(
            store, manifest, backend, "1", TestContext.Current.CancellationToken));
        Assert.Equal(0, backend.Probes);
    }

    [Fact]
    public async Task Cleanup_is_delegated_with_exact_manifest_and_cannot_expand_scope()
    {
        using var store = IsolatedTestStore.Create();
        Guid allowed = store.CreateEnvironmentId();
        Guid unrelated = store.CreateEnvironmentId();
        DockerTestManifest manifest = DockerTestManifest.ForStore(store, [allowed]);
        var backend = new RecordingBackend();
        var fixture = await ScopedDockerFixture.CreateAsync(
            store, manifest, backend, "1", TestContext.Current.CancellationToken);

        fixture.RequireAllowedEnvironment(allowed);
        Assert.Throws<InvalidOperationException>(() => fixture.RequireAllowedEnvironment(unrelated));
        await fixture.DisposeAsync();
        await fixture.DisposeAsync();

        Assert.Equal([allowed], backend.CleanedEnvironments);
        Assert.Throws<ObjectDisposedException>(() => fixture.RequireAllowedEnvironment(allowed));
    }

    [Fact]
    public async Task Ownership_cleanup_failure_is_reported_and_does_not_fall_back_to_global_cleanup()
    {
        using var store = IsolatedTestStore.Create();
        DockerTestManifest manifest = DockerTestManifest.ForStore(store, [store.CreateEnvironmentId()]);
        var backend = new RecordingBackend { CleanupFailure = new IOException("Ownership could not be verified") };
        var fixture = await ScopedDockerFixture.CreateAsync(
            store, manifest, backend, "1", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(() => fixture.DisposeAsync().AsTask());

        Assert.Empty(backend.CleanedEnvironments);
    }

    private sealed class RecordingBackend : IScopedDockerBackend
    {
        public int Probes { get; private set; }
        public string? PrerequisiteFailure { get; init; }
        public Exception? CleanupFailure { get; init; }
        public List<Guid> CleanedEnvironments { get; } = [];

        public Task<string?> CheckPrerequisitesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Probes++;
            return Task.FromResult(PrerequisiteFailure);
        }

        public Task CleanupOwnedResourcesAsync(DockerTestManifest manifest, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CleanupFailure is not null)
            {
                throw CleanupFailure;
            }

            CleanedEnvironments.AddRange(manifest.EnvironmentIds);
            return Task.CompletedTask;
        }
    }
}
