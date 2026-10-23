namespace EnvironmentBuilder.Core.SeedPacks;

public sealed record SeedSection(string Id, string Title, string Path, long RecordCount, long Bytes, string Sha256);
public sealed record InitializerEntry(string Path, long Bytes, string Sha256);
