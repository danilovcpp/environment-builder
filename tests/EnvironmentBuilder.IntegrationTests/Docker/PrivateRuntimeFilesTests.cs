using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Infrastructure.Docker;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Docker;

public sealed class PrivateRuntimeFilesTests
{
    [Fact]
    public async Task Locked_private_file_cleanup_is_visible_and_never_reported_as_success()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        var files = new PrivateRuntimeFiles(fixture.Directory, Guid.NewGuid());
        string path = await files.WriteAsync("secret", "private-canary", TestContext.Current.CancellationToken);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failure = await Assert.ThrowsAsync<UnresolvedEffectException>(() => files.DisposeAsync().AsTask());
            Assert.Equal("private-runtime-files-cleanup-failed", failure.Code);
        }
        await files.DisposeAsync(); Assert.False(Directory.Exists(files.Root));
    }
}
