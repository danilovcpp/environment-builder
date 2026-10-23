using System.Collections.Immutable;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.Core.Abstractions;

// Application-owned metadata; payload leases are owned by the caller's observation/operation.
public interface ISeedPackStore
{
    Task<VerifiedSeedPack?> FindAsync(SeedPackReference reference, CancellationToken cancellationToken);
}
public sealed record VerifiedSeedPack(SeedPack Manifest, ImmutableDictionary<string, InitializationDefinition> Initializers);
