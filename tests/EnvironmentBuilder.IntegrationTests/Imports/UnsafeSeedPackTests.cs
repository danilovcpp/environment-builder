using System.IO.Compression;
using System.Text;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Imports;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Imports;

public sealed class UnsafeSeedPackTests
{
    [Fact]
    public async Task Import_verifies_payloads_is_idempotent_and_references_block_deletion()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        using var blobs = new ContentBlobStore(fixture.Directory, fixture.Worker);
        var store = new SeedPackStore(fixture.Worker, blobs, fixture.Reader, fixture.Clock);
        var importer = new SeedPackImporter(fixture.Reader, blobs, store);
        string zip = CreateZip(fixture);
        SeedPackReference first = await importer.ImportAsync(zip, TestContext.Current.CancellationToken);
        Assert.Equal(first, await importer.ImportAsync(zip, TestContext.Current.CancellationToken));
        Assert.Equal(1, await fixture.CountAsync("SeedPacks"));
        Assert.Equal(2, await fixture.CountAsync("PackEntries"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RemoveAsync(first with { ContentHash = new string('0', 64) }, TestContext.Current.CancellationToken));
        Assert.Equal(2, await fixture.CountAsync("PackEntries"));
        Assert.NotNull(await store.FindAsync(first, TestContext.Current.CancellationToken));
        Assert.Equal(0, await blobs.CleanupOrphansAsync(TestContext.Current.CancellationToken));
        var environment = fixture.Example();
        var verified = await store.FindAsync(first, TestContext.Current.CancellationToken);
        environment = environment with
        {
            Baseline = new DataBaseline([.. environment.Baseline.Applications,
            new BaselineApplication(environment.Components[0].Id, verified!.Initializers.Values.Single(), first)])
        };
        await fixture.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RemoveAsync(first, TestContext.Current.CancellationToken));
        var inputs = new Infrastructure.Security.InputResolver(fixture.Worker, new Infrastructure.Security.LocalPathResolver(),
            new Infrastructure.Security.DpapiSecretVault(fixture.Worker, fixture.Clock), fixture.Clock);
        Assert.DoesNotContain(await Core.Environments.EnvironmentReadiness.CheckAsync(environment, inputs, store, TestContext.Current.CancellationToken), issue => issue.Code == "missing-pack");
        var missing = environment with { Baseline = new DataBaseline([.. environment.Baseline.Applications[..^1], environment.Baseline.Applications[^1] with { SeedPack = first with { Id = Guid.NewGuid() } }]) };
        Assert.Contains(await Core.Environments.EnvironmentReadiness.CheckAsync(missing, inputs, store, TestContext.Current.CancellationToken), issue => issue.Code == "missing-pack");
        BaselineApplication selected = environment.Baseline.Applications[^1];
        selected = selected with { Initializer = selected.Initializer with { Steps = [selected.Initializer.Steps[0] with { Path = "/different" }] } };
        var conflicting = environment with { Baseline = new DataBaseline([.. environment.Baseline.Applications[..^1], selected]) };
        await Assert.ThrowsAsync<Core.Environments.DocumentException>(() => Core.Environments.EnvironmentReadiness.CheckAsync(conflicting, inputs, store, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("sections/extra.exe")]
    [InlineData("MANIFEST.JSON")]
    [InlineData("sections/nested.zip")]
    [InlineData("sections/data:stream.ndjson")]
    public async Task Unsafe_and_undeclared_entries_leave_the_library_empty(string extra)
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        using var blobs = new ContentBlobStore(fixture.Directory, fixture.Worker);
        var store = new SeedPackStore(fixture.Worker, blobs, fixture.Reader, fixture.Clock);
        var importer = new SeedPackImporter(fixture.Reader, blobs, store);
        string zip = CreateZip(fixture, extra);
        await Assert.ThrowsAnyAsync<Exception>(() => importer.ImportAsync(zip, TestContext.Current.CancellationToken));
        Assert.Equal(0, await fixture.CountAsync("SeedPacks"));
        Assert.Equal(0, await fixture.CountAsync("PackEntries"));
    }

    [Fact]
    public async Task Reusing_a_package_version_with_another_manifest_hash_is_a_conflict()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        using var blobs = new ContentBlobStore(fixture.Directory, fixture.Worker);
        var store = new SeedPackStore(fixture.Worker, blobs, fixture.Reader, fixture.Clock);
        var importer = new SeedPackImporter(fixture.Reader, blobs, store);
        string zip = CreateZip(fixture);
        SeedPackReference first = await importer.ImportAsync(zip, TestContext.Current.CancellationToken);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            ZipArchiveEntry entry = archive.GetEntry("manifest.json")!;
            System.Text.Json.Nodes.JsonNode manifest;
            using (var input = entry.Open()) { manifest = System.Text.Json.Nodes.JsonNode.Parse(input)!; }
            manifest["title"] = "Changed immutable content";
            manifest["contentHash"] = Core.Environments.CanonicalJson.ManifestHash(System.Text.Json.JsonSerializer.SerializeToElement(manifest));
            entry.Delete();
            using var output = new StreamWriter(archive.CreateEntry("manifest.json").Open(), new UTF8Encoding(false));
            output.Write(manifest.ToJsonString());
        }
        await Assert.ThrowsAsync<Core.Environments.DocumentException>(() => importer.ImportAsync(zip, TestContext.Current.CancellationToken));
        Assert.Equal(1, await fixture.CountAsync("SeedPacks"));
        Assert.NotNull(await store.FindAsync(first, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("link")]
    [InlineData("executable")]
    [InlineData("hash")]
    public async Task Declared_entries_still_require_safe_metadata_and_actual_content_verification(string kind)
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        using var blobs = new ContentBlobStore(fixture.Directory, fixture.Worker);
        var store = new SeedPackStore(fixture.Worker, blobs, fixture.Reader, fixture.Clock);
        var importer = new SeedPackImporter(fixture.Reader, blobs, store);
        string zip = CreateZip(fixture);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            ZipArchiveEntry entry = archive.GetEntry("sections/requests.ndjson")!;
            if (kind == "link") { entry.ExternalAttributes = unchecked((int)0xa1ff0000); }
            else if (kind == "executable") { entry.ExternalAttributes = unchecked((int)0x81ed0000); }
            else
            {
                entry.Delete();
                using Stream output = archive.CreateEntry("sections/requests.ndjson").Open();
                output.Write(Encoding.UTF8.GetBytes("{\"corrupt\":true}\n"));
            }
        }
        await Assert.ThrowsAsync<IOException>(() => importer.ImportAsync(zip, TestContext.Current.CancellationToken));
        Assert.Equal(0, await fixture.CountAsync("SeedPacks"));
    }

    [Fact]
    public async Task Failed_pack_commit_leaves_only_collectable_promoted_blobs()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync(boundary =>
        { if (boundary == "pack:beforeCommit") { throw new IOException("disk full"); } });
        using var blobs = new ContentBlobStore(fixture.Directory, fixture.Worker);
        var store = new SeedPackStore(fixture.Worker, blobs, fixture.Reader, fixture.Clock);
        var importer = new SeedPackImporter(fixture.Reader, blobs, store);
        await Assert.ThrowsAsync<IOException>(() => importer.ImportAsync(CreateZip(fixture), TestContext.Current.CancellationToken));
        Assert.Equal(0, await fixture.CountAsync("SeedPacks"));
        Assert.Equal(2, await blobs.CleanupOrphansAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_active_payload_lease_blocks_orphan_collection()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        using var blobs = new ContentBlobStore(fixture.Directory, fixture.Worker);
        byte[] bytes = Encoding.UTF8.GetBytes("fixture");
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        using var input = new MemoryStream(bytes);
        using StagedBlob staged = await blobs.StageAsync(input, hash, bytes.Length, 1024, TestContext.Current.CancellationToken);
        using (BlobLease lease = await blobs.PromoteAsync(staged, TestContext.Current.CancellationToken))
        { Assert.Equal(0, await blobs.CleanupOrphansAsync(TestContext.Current.CancellationToken)); }
        Assert.Equal(1, await blobs.CleanupOrphansAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{}\n{}\n", 2)]
    [InlineData("{}\r\n{}", 2)]
    [InlineData("", 0)]
    public async Task Ndjson_index_records_exact_byte_offsets(string data, long count)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(data));
        using var index = new MemoryStream();
        Assert.Equal(count, await new NdjsonReader().ValidateAndIndexAsync(input, index, TestContext.Current.CancellationToken));
        Assert.Equal(count * 8, index.Length);
        if (count == 2) { Assert.Equal(data.Contains('\r') ? 4 : 3, BitConverter.ToInt64(index.ToArray(), 8)); }
    }

    [Theory]
    [InlineData("{}\n\n")]
    [InlineData("[]\n")]
    [InlineData("{invalid}\n")]
    public async Task Ndjson_rejects_blank_nonobject_and_invalid_records(string data)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(data));
        using var index = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(() => new NdjsonReader().ValidateAndIndexAsync(input, index, TestContext.Current.CancellationToken));
    }

    private static string CreateZip(FoundationStore fixture, string? extra = null)
    {
        string path = fixture.Directory.Child(Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        string root = Path.Combine(AppContext.BaseDirectory, "Contracts/examples/seedpack");
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        { zip.CreateEntryFromFile(file, Path.GetRelativePath(root, file).Replace('\\', '/')); }
        if (extra is not null)
        {
            using Stream output = zip.CreateEntry(extra).Open();
            output.Write(Encoding.UTF8.GetBytes("fixture"));
        }
        return path;
    }
}
