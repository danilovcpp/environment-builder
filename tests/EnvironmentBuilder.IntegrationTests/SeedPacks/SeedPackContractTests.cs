using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Imports;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.SeedPacks;

public sealed class SeedPackContractTests
{
    [Theory]
    [InlineData("count")]
    [InlineData("missing-section")]
    [InlineData("initializer-compatibility")]
    [InlineData("schema-version")]
    public async Task Metadata_conflicts_and_missing_entries_never_publish_a_partial_package(string fault)
    {
        await using var fixture = await FoundationStore.CreateAsync();
        using var blobs = new ContentBlobStore(fixture.Directory, fixture.Worker);
        var store = new SeedPackStore(fixture.Worker, blobs, fixture.Reader, fixture.Clock);
        var pack = PackFixture.Create(fixture);
        using (var archive = ZipFile.Open(pack.Path, ZipArchiveMode.Update))
        {
            if (fault == "missing-section") { archive.GetEntry(pack.Manifest.Sections[0].Path)!.Delete(); }
            else
            {
                var node = JsonSerializer.SerializeToNode(pack.Manifest, CanonicalJson.Options)!;
                if (fault == "count") { node["sections"]![0]!["recordCount"] = 3; }
                if (fault == "schema-version") { node["schemaVersion"] = 2; }
                if (fault == "initializer-compatibility") { node["compatibility"]![0]!["contractId"] = "unrelated.service"; }
                node["contentHash"] = CanonicalJson.ManifestHash(JsonSerializer.SerializeToElement(node));
                archive.GetEntry("manifest.json")!.Delete(); using var stream = archive.CreateEntry("manifest.json").Open();
                await JsonSerializer.SerializeAsync(stream, node, cancellationToken: TestContext.Current.CancellationToken);
            }
        }
        await Assert.ThrowsAnyAsync<Exception>(() => new SeedPackImporter(fixture.Reader, blobs, store).ImportAsync(pack.Path, TestContext.Current.CancellationToken));
        Assert.Empty(await store.ListMetadataAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await fixture.CountAsync("PackEntries")); Assert.Equal(0, await fixture.CountAsync("Operations"));
    }

    [Fact]
    public async Task Selection_preserves_mandatory_snapshot_rejects_unavailable_contract_and_never_updates_saved_version()
    {
        await using var fixture = await FoundationStore.CreateAsync();
        using var blobs = new ContentBlobStore(fixture.Directory, fixture.Worker);
        var store = new SeedPackStore(fixture.Worker, blobs, fixture.Reader, fixture.Clock);
        var importer = new SeedPackImporter(fixture.Reader, blobs, store);
        var first = await importer.ImportAsync(PackFixture.Create(fixture).Path, TestContext.Current.CancellationToken);
        var original = fixture.Example(); var selector = new BaselineSelectionService(store);
        var selected = await selector.SelectAsync(original, original.Components[0].Id, first, "initializers/requests.json", TestContext.Current.CancellationToken);
        Assert.Equal(original.Baseline.Applications[0], selected.Baseline.Applications[0]); Assert.Null(selected.Baseline.Applications[0].SeedPack);
        await fixture.Environments.SaveNewAsync(selected, TestContext.Current.CancellationToken);
        await importer.ImportAsync(PackFixture.Create(fixture, version: "2.0.0").Path, TestContext.Current.CancellationToken);
        Assert.Equal(first, Assert.Single(await fixture.Environments.ListAsync(TestContext.Current.CancellationToken)).Document!.Baseline.Applications[^1].SeedPack);
        await Assert.ThrowsAsync<DocumentException>(() => selector.SelectAsync(original, original.Components[0].Id, first, "missing.json", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<DocumentException>(() => selector.SelectAsync(original, original.Components[0].Id, first with { ContentHash = new string('0', 64) }, "initializers/requests.json", TestContext.Current.CancellationToken));
        var incompatible = original with { Components = [original.Components[0] with { DefinitionSnapshot = original.Components[0].DefinitionSnapshot with { Contract = new("another.contract", "1.0.0") } }] };
        await Assert.ThrowsAsync<DocumentException>(() => selector.SelectAsync(incompatible, incompatible.Components[0].Id, first, "initializers/requests.json", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Candidate_operation_retains_package_until_terminal_resolution_and_shared_blobs_survive_deletion()
    {
        await using var fixture = await FoundationStore.CreateAsync();
        using var blobs = new ContentBlobStore(fixture.Directory, fixture.Worker);
        var store = new SeedPackStore(fixture.Worker, blobs, fixture.Reader, fixture.Clock);
        var importer = new SeedPackImporter(fixture.Reader, blobs, store);
        var first = await importer.ImportAsync(PackFixture.Create(fixture).Path, TestContext.Current.CancellationToken);
        var second = await importer.ImportAsync(PackFixture.Create(fixture, version: "2.0.0").Path, TestContext.Current.CancellationToken);
        var environment = fixture.Example(); await fixture.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken);
        var candidate = await new BaselineSelectionService(store).SelectAsync(environment, environment.Components[0].Id, first, "initializers/requests.json", TestContext.Current.CancellationToken);
        var operation = await fixture.CreateOperationAsync(environment); operation = operation with { Id = Guid.NewGuid(), Kind = "edit", Plan = operation.Plan with { Candidate = candidate } };
        await fixture.Operations.SaveAsync(operation, TestContext.Current.CancellationToken);
        Assert.Contains(await store.ReadOwnersAsync(first, TestContext.Current.CancellationToken), o => o.Kind == "operation" && o.Id == operation.Id);
        await Assert.ThrowsAsync<SeedPackInUseException>(() => store.RemoveAsync(first, TestContext.Current.CancellationToken));
        await fixture.Operations.SaveAsync(operation with { State = Core.Operations.OperationState.Cancelled }, TestContext.Current.CancellationToken);
        await store.RemoveAsync(first, TestContext.Current.CancellationToken);
        Assert.NotNull(await store.FindAsync(second, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Compression_and_timestamp_do_not_change_identity_and_delete_reports_exact_owners()
    {
        await using var fixture = await FoundationStore.CreateAsync();
        using var blobs = new ContentBlobStore(fixture.Directory, fixture.Worker);
        var store = new SeedPackStore(fixture.Worker, blobs, fixture.Reader, fixture.Clock);
        var importer = new SeedPackImporter(fixture.Reader, blobs, store);
        var first = await importer.ImportAsync(PackFixture.Create(fixture).Path, TestContext.Current.CancellationToken);
        Assert.Equal(first, await importer.ImportAsync(PackFixture.Create(fixture, compression: CompressionLevel.NoCompression).Path, TestContext.Current.CancellationToken));
        var verified = (await store.FindAsync(first, TestContext.Current.CancellationToken))!;
        var env = fixture.Example();
        env = env with { Baseline = new([.. env.Baseline.Applications, new(env.Components[0].Id, verified.Initializers.Values.Single(), first)]) };
        await fixture.Environments.SaveNewAsync(env, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<SeedPackInUseException>(() => new DeleteSeedPackService(store).DeleteAsync(first, TestContext.Current.CancellationToken));
        Assert.Contains(error.Owners, o => o.Id == env.Id && o.Name == env.Name);
        Assert.Single(await store.ListMetadataAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Active_payload_lease_blocks_delete_then_unreferenced_content_is_collected()
    {
        await using var fixture = await FoundationStore.CreateAsync();
        using var blobs = new ContentBlobStore(fixture.Directory, fixture.Worker);
        var store = new SeedPackStore(fixture.Worker, blobs, fixture.Reader, fixture.Clock);
        var imported = PackFixture.Create(fixture);
        var reference = await new SeedPackImporter(fixture.Reader, blobs, store).ImportAsync(imported.Path, TestContext.Current.CancellationToken);
        using (await blobs.AcquireAsync(imported.Manifest.Sections[0].Sha256, TestContext.Current.CancellationToken))
        { await Assert.ThrowsAsync<SeedPackInUseException>(() => store.RemoveAsync(reference, TestContext.Current.CancellationToken)); }
        await new DeleteSeedPackService(store).DeleteAsync(reference, TestContext.Current.CancellationToken);
        Assert.Empty(await store.ListMetadataAsync(TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(fixture.Directory.Child("blobs")));
    }
}
