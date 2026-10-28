using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.SeedPacks;

internal static class PackFixture
{
    internal static (string Path, SeedPack Manifest) Create(FoundationStore fixture, int records = 2, int width = 16,
        string version = "1.0.0", CompressionLevel compression = CompressionLevel.Fastest)
    {
        var original = fixture.Reader.Read<SeedPack>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Contracts/examples/seedpack/manifest.json")));
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, records).Select(i => $"{{\"row\":{i},\"text\":\"{new string('a', width)}\"}}\n")));
        var section = original.Sections[0] with { Bytes = data.Length, RecordCount = records, Sha256 = Convert.ToHexStringLower(SHA256.HashData(data)) };
        SeedPack manifest = original with { Version = version, Sections = [section] };
        manifest = manifest with { ContentHash = CanonicalJson.ManifestHash(JsonSerializer.SerializeToElement(manifest, CanonicalJson.Options)) };
        string path = fixture.Directory.Child(Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(archive, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, CanonicalJson.Options), compression);
        Write(archive, section.Path, data, compression);
        foreach (var entry in manifest.Initializers)
        { Write(archive, entry.Path, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Contracts/examples/seedpack", entry.Path)), compression); }
        return (path, manifest);
    }
    private static void Write(ZipArchive archive, string name, byte[] bytes, CompressionLevel compression)
    { using Stream output = archive.CreateEntry(name, compression).Open(); output.Write(bytes); }
}
