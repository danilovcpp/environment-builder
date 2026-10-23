using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.Core.Environments;

public enum InputKind { SourcePath, Dockerfile, ReadOnlyInput, Image, Secret, SeedPack }
public sealed record InputReference(InputKind Kind, string Reference, Guid ComponentId, string FieldPath, SeedPackReference? Pack = null);
public sealed record LocalPathReference(string Reference);
public sealed record LocalPathMapping(string Alias, string AbsolutePath, DateTimeOffset? VerifiedAt);
public sealed record SecretReference(string Reference);
public sealed record ReadinessIssue(string Code, string FieldPath, string Explanation, DateTimeOffset? CheckedAt = null);
