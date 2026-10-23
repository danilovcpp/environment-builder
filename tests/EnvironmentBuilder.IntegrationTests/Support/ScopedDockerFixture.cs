using System.Collections.Immutable;

namespace EnvironmentBuilder.IntegrationTests.Support;

public sealed record DockerTestManifest
{
    private DockerTestManifest(Guid installationId, ImmutableArray<Guid> environmentIds)
    {
        InstallationId = installationId;
        EnvironmentIds = environmentIds;
    }

    public Guid InstallationId { get; }
    public ImmutableArray<Guid> EnvironmentIds { get; }

    public static DockerTestManifest ForStore(IsolatedTestStore store, IEnumerable<Guid> environmentIds)
    {
        ImmutableArray<Guid> ids = [.. environmentIds];
        if (ids.IsEmpty || ids.Distinct().Count() != ids.Length || ids.Any(id => !store.OwnsEnvironment(id)))
        {
            throw new ArgumentException("The manifest must contain unique IDs allocated by this test store.", nameof(environmentIds));
        }

        return new DockerTestManifest(store.InstallationId, ids);
    }
}

// A live implementation must use production capability probes and plan/ownership cleanup.
// The harness deliberately has no raw Docker deletion or best-effort cleanup fallback.
public interface IScopedDockerBackend
{
    Task<string?> CheckPrerequisitesAsync(CancellationToken cancellationToken);
    Task CleanupOwnedResourcesAsync(DockerTestManifest manifest, CancellationToken cancellationToken);
}

public sealed class DockerTestBlockedException(string message) : InvalidOperationException(message);

public sealed class ScopedDockerFixture : IAsyncDisposable
{
    private readonly IScopedDockerBackend backend;
    private readonly IsolatedTestStore store;
    private bool disposed;

    private ScopedDockerFixture(IsolatedTestStore store, DockerTestManifest manifest, IScopedDockerBackend backend)
    {
        this.store = store;
        Manifest = manifest;
        this.backend = backend;
    }

    public DockerTestManifest Manifest { get; }

    public static Task<ScopedDockerFixture> CreateAsync(
        IsolatedTestStore store, DockerTestManifest manifest, IScopedDockerBackend backend, CancellationToken cancellationToken) =>
        CreateAsync(store, manifest, backend, Environment.GetEnvironmentVariable("ENVIRONMENTBUILDER_DOCKER_TESTS"), cancellationToken);

    // Explicit input also lets offline safety tests exercise the opt-in gate without changing process-wide environment.
    internal static async Task<ScopedDockerFixture> CreateAsync(
        IsolatedTestStore store, DockerTestManifest manifest, IScopedDockerBackend backend,
        string? optIn, CancellationToken cancellationToken)
    {
        if (optIn != "1")
        {
            throw new DockerTestBlockedException("Set ENVIRONMENTBUILDER_DOCKER_TESTS=1 to enable live Docker tests.");
        }

        if (manifest.InstallationId != store.InstallationId || manifest.EnvironmentIds.Any(id => !store.OwnsEnvironment(id)))
        {
            throw new ArgumentException("The Docker manifest belongs to another test store.", nameof(manifest));
        }

        string? failure = await backend.CheckPrerequisitesAsync(cancellationToken);
        if (failure is not null)
        {
            throw new DockerTestBlockedException($"Required Docker prerequisite failed: {failure}");
        }

        return new ScopedDockerFixture(store, manifest, backend);
    }

    public void RequireAllowedEnvironment(Guid environmentId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!Manifest.EnvironmentIds.Contains(environmentId) || !store.OwnsEnvironment(environmentId))
        {
            throw new InvalidOperationException("The environment is outside the exact Docker test manifest.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        foreach (Guid environmentId in Manifest.EnvironmentIds)
        {
            RequireAllowedEnvironment(environmentId);
        }

        // Cleanup must finish or report its failure even when the test's token was cancelled.
        disposed = true;
        await backend.CleanupOwnedResourcesAsync(Manifest, CancellationToken.None);
    }
}
