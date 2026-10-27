using System.Collections.Immutable;
using System.Globalization;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.Operations;

public sealed record ResourceEvidence(ResourceKind Kind, string Name, string Identity, ImmutableDictionary<string, string> Labels,
    VolumeIncarnation? Volume, ImmutableArray<string> Consumers, bool ConsumersKnown, string State);

public static class ResourceOwnershipVerifier
{
    public static ImmutableDictionary<string, string> Labels(OwnedResource resource)
    {
        var labels = new Dictionary<string, string>
        {
            ["io.environmentbuilder.installation"] = resource.InstallationId.ToString(),
            ["io.environmentbuilder.environment"] = resource.EnvironmentId.ToString(),
            ["io.environmentbuilder.resource"] = resource.ResourceId.ToString(),
            ["io.environmentbuilder.revision"] = resource.CreationRevision.ToString(CultureInfo.InvariantCulture),
            ["io.environmentbuilder.generation"] = resource.DataGenerationId.ToString()
        };
        if (resource.ComponentId.HasValue) { labels["io.environmentbuilder.component"] = resource.ComponentId.Value.ToString(); }
        return labels.ToImmutableDictionary(StringComparer.Ordinal);
    }

    public static EffectObservation Verify(OwnedResource? ledger, ResourceEvidence actual, IEnumerable<string>? allowedConsumers = null)
    {
        if (ledger is null || ledger.Unresolved || ledger.ActualDockerId is null) { return Block("missing-ledger-evidence"); }
        if (ledger.Kind != actual.Kind || ledger.Name != actual.Name || ledger.ActualDockerId != actual.Identity) { return Block("resource-identity-mismatch"); }
        if (Labels(ledger).Any(pair => !actual.Labels.TryGetValue(pair.Key, out string? value) || value != pair.Value)) { return Block("ownership-label-mismatch"); }
        if (!actual.ConsumersKnown) { return Block("consumers-unknown"); }
        var allowed = (allowedConsumers ?? []).ToHashSet(StringComparer.Ordinal);
        if (actual.Consumers.Any(consumer => !allowed.Contains(consumer))) { return Block("foreign-consumer"); }
        if (ledger.Kind == ResourceKind.Volume && (ledger.Volume is null || actual.Volume is null ||
            !DateTimeOffset.TryParse(actual.Volume.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _) ||
            actual.Volume.Driver != "local" || actual.Volume.Scope != "local" || actual.Volume.Options.Count != 0 ||
            CanonicalJson.Hash(ledger.Volume) != CanonicalJson.Hash(actual.Volume))) { return Block("volume-incarnation-mismatch"); }
        return new(actual.Identity, true, null);
        static EffectObservation Block(string code) => new(null, false, code);
    }
}
