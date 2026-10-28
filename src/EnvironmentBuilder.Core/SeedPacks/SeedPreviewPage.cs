using System.Collections.Immutable;

namespace EnvironmentBuilder.Core.SeedPacks;

public sealed record SeedPreviewCursor(SeedPackReference Pack, string SectionId, long Record);
public sealed record SeedPreviewPage(string SectionId, long StartRecord, long TotalRecords, ImmutableArray<string> Records,
    SeedPreviewCursor? Next)
{
    public string Text => string.Join('\n', Records);
    public bool IsLimited => Next is not null;
}

// These boundaries perform only file/storage I/O; importing and previewing have no runtime collaborator.
public interface ISeedPreviewReader
{
    Task<SeedPreviewPage> ReadAsync(SeedPackReference pack, string section, SeedPreviewCursor? cursor, CancellationToken token);
}
public interface ISeedPackImporter
{
    Task<SeedPackReference> ImportAsync(string archivePath, CancellationToken cancellationToken);
}
public interface ISeedPackCatalog
{
    Task<IReadOnlyList<SeedPack>> ListMetadataAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SeedPackOwner>> ReadOwnersAsync(SeedPackReference reference, CancellationToken token);
    Task RemoveAsync(SeedPackReference reference, CancellationToken cancellationToken);
}
public sealed record SeedPackOwner(string Kind, Guid Id, string Name);
public sealed class SeedPackInUseException(IReadOnlyList<SeedPackOwner> owners) : InvalidOperationException("This exact package version is retained by its owners.")
{
    public IReadOnlyList<SeedPackOwner> Owners { get; } = owners;
}
