using System.Collections.Immutable;

namespace EnvironmentBuilder.Core.Environments;

public sealed record PreviewInput(string Reference, string State, string? ContentHash, string? ImageIdentity);
public sealed record PreviewSnapshot(Guid EnvironmentId, long Revision, string ConfigurationHash, string CanonicalEffectiveSpec,
    string CompilerVersion, string BaselineHash, ImmutableArray<string> InitializerHashes, ImmutableArray<string> PackHashes,
    ImmutableArray<string> SecretReferences, ImmutableArray<PreviewInput> Inputs)
{
    [System.Text.Json.Serialization.JsonIgnore] public string Token => CanonicalJson.Hash(this);
}
