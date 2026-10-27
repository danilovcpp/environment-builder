using System.IO.Compression;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Imports;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Docker;

public sealed class DemoContractTests
{
    internal static string DemoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../demos"));
    [Fact]
    public async Task Demo_descriptors_and_all_seed_sizes_pass_production_readers_and_hash_verification()
    {
        await using FoundationStore store = await FoundationStore.CreateAsync();
        IsolatedEnvironment environment = store.Reader.Read<IsolatedEnvironment>(await File.ReadAllBytesAsync(Path.Combine(DemoRoot, "descriptors/environment.json"), TestContext.Current.CancellationToken));
        Assert.Equal(4, environment.Components.Length);
        using var blobs = new ContentBlobStore(store.Directory, store.Worker);
        var packs = new SeedPackStore(store.Worker, blobs, store.Reader, store.Clock);
        foreach ((string name, int records) in new[] { ("small", 10), ("medium", 1000), ("large", 100000) })
        {
            string folder = Path.Combine(DemoRoot, "seedpacks", name);
            string zip = Path.Combine(store.Directory.Root, name + ".zip");
            CreateArchive(folder, zip);
            SeedPackReference reference = await new SeedPackImporter(store.Reader, blobs, packs).ImportAsync(zip, TestContext.Current.CancellationToken);
            var verified = await packs.FindAsync(reference, TestContext.Current.CancellationToken);
            Assert.Equal(records, Assert.Single(verified!.Manifest.Sections).RecordCount);
            Assert.Equal(3, verified.Initializers.Count);
        }
    }
    internal static void CreateArchive(string folder, string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            ZipArchiveEntry entry = archive.CreateEntry(Path.GetRelativePath(folder, file).Replace('\\', '/'));
            entry.ExternalAttributes = 0;
            using Stream target = entry.Open(); using Stream input = File.OpenRead(file); input.CopyTo(target);
        }
    }
}
