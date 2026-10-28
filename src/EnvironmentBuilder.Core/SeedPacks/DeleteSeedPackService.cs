namespace EnvironmentBuilder.Core.SeedPacks;

public sealed class DeleteSeedPackService(ISeedPackCatalog store)
{
    // The store checks owners again inside the same transaction that releases entry references.
    public Task DeleteAsync(SeedPackReference reference, CancellationToken token) => store.RemoveAsync(reference, token);
}
