using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace EnvironmentBuilder.Core.Environments;

// Secret-параметр содержит ссылку, значение в JSON запрещено.
public sealed record Value
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Literal { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SecretRef { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ImageSource), "image")]
[JsonDerivedType(typeof(BuildSource), "build")]
public abstract record ComponentSource;
public sealed record ImageSource(string Reference) : ComponentSource;
public sealed record BuildSource(string PathRef, string Dockerfile, string ContextSubdirectory,
    ImmutableDictionary<string, string> BuildArguments, ImmutableDictionary<string, string> BuildSecrets) : ComponentSource;

// default — loopback, LAN открывается явно для каждого порта.
public sealed record Port(string Id, int ContainerPort, string Protocol, int? HostPort, string Access, string Purpose);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ManagedStorage), "managed")]
[JsonDerivedType(typeof(ReadOnlyInputStorage), "readOnlyInput")]
public abstract record Storage(string Id, string TargetPath);
public sealed record ManagedStorage(string Id, string TargetPath) : Storage(Id, TargetPath);
// Внешние source/input mounts только read-only.
public sealed record ReadOnlyInputStorage(string Id, string TargetPath, string PathRef) : Storage(Id, TargetPath);
public sealed record Endpoint(string Id, int ContainerPort, string Scheme, string BasePath);
public sealed record Readiness(string Kind, int TimeoutSeconds)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? EndpointId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Path { get; init; }
}
public sealed record ParameterDefinition(string Name, string Type, bool Required, bool Secret, Value? DefaultValue, string EnvironmentVariable);
public sealed record ComponentContract(string Id, string Version);
public sealed record ContractCompatibility(string ContractId, ImmutableArray<string> Versions);
