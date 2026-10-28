using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.SeedPacks;

public sealed record PackApplicationChoice(SeedPackReference Pack, string Title, string InitializerPath)
{
    public override string ToString() => $"{Title} · {Pack.Version} · {InitializerPath}";
}

public sealed class BaselineSelectionService(ISeedPackStore packs)
{
    public async Task<IsolatedEnvironment> SelectAsync(IsolatedEnvironment draft, Guid instance, SeedPackReference reference, string initializerPath, CancellationToken token)
    {
        EnvironmentComponent component = draft.Components.Single(c => c.Id == instance);
        VerifiedSeedPack verified = await packs.FindAsync(reference, token).ConfigureAwait(false) ?? throw new DocumentException("/baseline", "missing-pack");
        if (component.DefinitionSnapshot.Readiness.Kind == "none" || !verified.Manifest.Compatibility.Any(c =>
            c.ContractId == component.DefinitionSnapshot.Contract.Id && c.Versions.Contains(component.DefinitionSnapshot.Contract.Version)))
        { throw new DocumentException("/baseline", "pack-contract-mismatch"); }
        if (!verified.Initializers.TryGetValue(initializerPath, out InitializationDefinition? initializer))
        { throw new DocumentException("/baseline", "pack-apply-contract-unavailable"); }
        InitializationValidator.Validate(initializer, component.DefinitionSnapshot, verified.Manifest);
        var application = new BaselineApplication(instance, initializer, reference);
        var updated = draft with { Baseline = new([.. draft.Baseline.Applications, application]) };
        EnvironmentValidator.Validate(updated);
        // This is a draft transformation. Persisted environments still go through the destructive edit workflow.
        return updated;
    }
}
