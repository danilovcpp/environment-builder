using System.Collections.Immutable;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Infrastructure.Docker;
using EnvironmentBuilder.Infrastructure.Security;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Docker;

public sealed class RuntimeCaptureTests
{
    [Fact]
    public async Task Captured_build_mount_bytes_are_immutable_and_lease_is_retained_for_accepted_containers()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        string source = Path.Combine(fixture.Directory.Root, "source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "Dockerfile"), "FROM scratch", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(source, ".dockerignore"), "ignored", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(source, "content.txt"), "original", TestContext.Current.CancellationToken);
        var mappings = new Mapping(source);
        var preview = new PreviewInputCapture(mappings, new LocalPathResolver());
        var capture = new RuntimeInputCapture(fixture.Directory, mappings, new LocalPathResolver(), preview);
        var environment = BuildEnvironment(fixture.Example());
        RuntimeInputs captured = await capture.CaptureAsync(environment, ImmutableDictionary<string, string>.Empty, TestContext.Current.CancellationToken);
        string staged = captured.Paths.Values.Single();
        Assert.NotEqual(source, staged);
        await File.WriteAllTextAsync(Path.Combine(source, "content.txt"), "changed", TestContext.Current.CancellationToken);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(staged, "content.txt"), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(staged, ".dockerignore")));
        captured.RetainForContainers(); await captured.DisposeAsync(); Assert.True(Directory.Exists(staged));
        await using RuntimeInputs cancelled = await capture.CaptureAsync(environment, ImmutableDictionary<string, string>.Empty, TestContext.Current.CancellationToken);
        string temporary = cancelled.Root; await cancelled.DisposeAsync(); Assert.False(Directory.Exists(temporary));
    }

    [Fact]
    public async Task Source_change_between_capture_and_final_hash_rejects_and_removes_staging()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        string source = Path.Combine(fixture.Directory.Root, "source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "Dockerfile"), "FROM scratch", TestContext.Current.CancellationToken);
        var mappings = new Mapping(source) { OnRead = count => { if (count == 3) { File.WriteAllText(Path.Combine(source, "Dockerfile"), "FROM changed"); } } };
        var capture = new RuntimeInputCapture(fixture.Directory, mappings, new LocalPathResolver(), new(mappings, new LocalPathResolver()));
        await Assert.ThrowsAsync<IOException>(() => capture.CaptureAsync(BuildEnvironment(fixture.Example()), ImmutableDictionary<string, string>.Empty, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Directory.Child("staging"), "runtime-*"));
    }
    private static IsolatedEnvironment BuildEnvironment(IsolatedEnvironment original)
    {
        EnvironmentComponent first = original.Components[0] with { Source = new BuildSource("local-path://source", "Dockerfile", ".", ImmutableDictionary<string, string>.Empty, ImmutableDictionary<string, string>.Empty) };
        return original with { Components = original.Components.SetItem(0, first) };
    }
    private sealed class Mapping(string root) : ILocalPathMappings
    {
        private int reads;
        public Action<int>? OnRead { get; init; }
        public Task<IReadOnlyList<LocalPathMapping>> ReadAsync(CancellationToken token)
        { OnRead?.Invoke(Interlocked.Increment(ref reads)); return Task.FromResult<IReadOnlyList<LocalPathMapping>>([new("source", root, null)]); }
        public Task MapAsync(string reference, string absolutePath, CancellationToken token) => throw new NotSupportedException();
    }
}
