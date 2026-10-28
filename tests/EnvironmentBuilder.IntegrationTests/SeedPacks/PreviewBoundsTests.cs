using System.Diagnostics;
using System.Text;
using EnvironmentBuilder.Infrastructure.Imports;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.SeedPacks;

public sealed class PreviewBoundsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Records_at_64KiB_are_accepted_and_larger_records_are_rejected(int extra)
    {
        string prefix = "{\"text\":\"", suffix = "\"}\n";
        byte[] bytes = Encoding.UTF8.GetBytes(prefix + new string('a', 65536 - prefix.Length - suffix.Length + extra) + suffix);
        using var source = new MemoryStream(bytes); using var index = new MemoryStream();
        if (extra == 0) { Assert.Equal(1, await new NdjsonReader().ValidateAndIndexAsync(source, index, TestContext.Current.CancellationToken)); }
        else { await Assert.ThrowsAsync<IOException>(() => new NdjsonReader().ValidateAndIndexAsync(source, index, TestContext.Current.CancellationToken)); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(220)]
    public async Task Missing_derived_index_is_rebuilt_and_empty_sections_have_an_explicit_end(int count)
    {
        await using var fixture = await FoundationStore.CreateAsync();
        using var blobs = new ContentBlobStore(fixture.Directory, fixture.Worker);
        var store = new SeedPackStore(fixture.Worker, blobs, fixture.Reader, fixture.Clock);
        var pack = PackFixture.Create(fixture, count);
        var reference = await new SeedPackImporter(fixture.Reader, blobs, store).ImportAsync(pack.Path, TestContext.Current.CancellationToken);
        File.Delete(fixture.Directory.Child("blobs", pack.Manifest.Sections[0].Sha256 + ".index"));
        using var preview = new SeedPreviewReader(store, blobs, fixture.Directory);
        var page = await preview.ReadAsync(reference, "requests", null, TestContext.Current.CancellationToken);
        Assert.Equal(Math.Min(count, 100), page.Records.Length); Assert.Equal(count > 100, page.IsLimited);
        Assert.Equal(count * 8, new FileInfo(fixture.Directory.Child("blobs", pack.Manifest.Sections[0].Sha256 + ".index")).Length);
    }
    [Fact]
    public async Task Hundred_thousand_records_seek_continue_and_show_bounds_without_runtime()
    {
        await using var fixture = await FoundationStore.CreateAsync();
        using var blobs = new ContentBlobStore(fixture.Directory, fixture.Worker);
        var store = new SeedPackStore(fixture.Worker, blobs, fixture.Reader, fixture.Clock);
        var pack = PackFixture.Create(fixture, 100000);
        var reference = await new SeedPackImporter(fixture.Reader, blobs, store).ImportAsync(pack.Path, TestContext.Current.CancellationToken);
        using var preview = new SeedPreviewReader(store, blobs, fixture.Directory);
        var time = Stopwatch.StartNew();
        var page = await preview.ReadAsync(reference, "requests", null, TestContext.Current.CancellationToken);
        time.Stop();
        Assert.True(time.Elapsed < TimeSpan.FromSeconds(2), time.Elapsed.ToString());
        Assert.Equal(100, page.Records.Length); Assert.True(page.IsLimited); Assert.NotNull(page.Next);
        Assert.True(Encoding.UTF8.GetByteCount(page.Text) <= 256 * 1024);
        var next = await preview.ReadAsync(reference, "requests", page.Next, TestContext.Current.CancellationToken);
        Assert.Contains("\"row\":100,", next.Text); Assert.Equal(100, next.StartRecord);
        Assert.Equal(0, await fixture.CountAsync("Operations"));
        Assert.Equal(0, await fixture.CountAsync("DataApplicationRecords"));
        TestContext.Current.TestOutputHelper!.WriteLine($"SC-009 first page: {time.Elapsed.TotalMilliseconds:F2} ms; records=100000; runtime effects=0");
    }

    [Fact]
    public async Task Byte_limit_cursor_identity_cancellation_and_cache_eviction_are_enforced()
    {
        await using var fixture = await FoundationStore.CreateAsync();
        using var blobs = new ContentBlobStore(fixture.Directory, fixture.Worker);
        var store = new SeedPackStore(fixture.Worker, blobs, fixture.Reader, fixture.Clock);
        var pack = PackFixture.Create(fixture, 300, 60000);
        var reference = await new SeedPackImporter(fixture.Reader, blobs, store).ImportAsync(pack.Path, TestContext.Current.CancellationToken);
        using var preview = new SeedPreviewReader(store, blobs, fixture.Directory);
        var page = await preview.ReadAsync(reference, "requests", null, TestContext.Current.CancellationToken);
        Assert.Equal(4, page.Records.Length);
        var cursor = page.Next!;
        await Assert.ThrowsAnyAsync<Exception>(() => preview.ReadAsync(reference with { Version = "9.0.0" }, "requests", cursor, TestContext.Current.CancellationToken));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preview.ReadAsync(reference, "requests", cursor, canceled.Token));
        while (page.Next is not null) { page = await preview.ReadAsync(reference, "requests", page.Next, TestContext.Current.CancellationToken); }
        Assert.True(preview.CacheBytes <= 8 * 1024 * 1024); Assert.True(preview.EvictedPages > 0);
        Assert.False(page.IsLimited);
        Assert.Equal(0, (await preview.ReadAsync(reference, "requests", null, TestContext.Current.CancellationToken)).StartRecord);
    }
}
