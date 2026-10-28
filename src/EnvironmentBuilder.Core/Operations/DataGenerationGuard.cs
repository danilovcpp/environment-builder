using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.Operations;

public static class DataGenerationGuard
{
    public static string? BlockReason(IsolatedEnvironment environment, DataGeneration generation, IEnumerable<ResourceObservation> resources)
    {
        if (generation.Validity != GenerationValidity.Active || generation.EnvironmentId != environment.Id || generation.BaselineHash != CanonicalJson.Hash(environment.Baseline))
        { return "data-generation-invalid"; }
        foreach (ResourceObservation resource in resources)
        {
            if (resource.Ledger.DataGenerationId != generation.Id) { return "data-generation-mismatch"; }
            if (resource.Ledger.Kind != ResourceKind.Volume) { continue; }
            if (resource.Actual is null) { return "persisted-volume-missing"; }
            if (!resource.Ownership.Verified) { return resource.Ownership.ErrorCode ?? "persisted-volume-unverified"; }
        }
        return null;
    }
}
