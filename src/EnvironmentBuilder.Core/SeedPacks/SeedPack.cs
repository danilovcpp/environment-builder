using System.Collections.Immutable;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.SeedPacks;

public sealed record SeedPack(string Kind, int SchemaVersion, Guid Id, string Version, string Title, string Theme,
    string Purpose, ImmutableArray<ContractCompatibility> Compatibility, ImmutableArray<SeedSection> Sections,
    ImmutableArray<InitializerEntry> Initializers, string ContentHash);
