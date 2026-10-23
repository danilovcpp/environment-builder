using System.Collections.Immutable;

namespace EnvironmentBuilder.Core.SeedPacks;

public sealed record DataBaseline(ImmutableArray<BaselineApplication> Applications);
public sealed record BaselineApplication(Guid ComponentId, InitializationDefinition Initializer, SeedPackReference? SeedPack);
public sealed record SeedPackReference(Guid Id, string Version, string ContentHash);
