using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.Core.Environments;

public static class EnvironmentReadiness
{
    public static async Task<IReadOnlyList<ReadinessIssue>> CheckAsync(IsolatedEnvironment environment, IInputResolver inputs, ISeedPackStore packs, CancellationToken cancellationToken)
    {
        EnvironmentValidator.Validate(environment);
        var references = new List<InputReference>();
        foreach (EnvironmentComponent component in environment.Components)
        {
            string path = "/components/" + component.Id;
            if (component.Source is ImageSource image) { references.Add(new InputReference(InputKind.Image, image.Reference, component.Id, path + "/source")); }
            if (component.Source is BuildSource build)
            {
                references.Add(new InputReference(InputKind.SourcePath, build.PathRef, component.Id, path + "/source/pathRef"));
                references.Add(new InputReference(InputKind.Dockerfile, build.PathRef + "/" + build.ContextSubdirectory + "/" + build.Dockerfile, component.Id, path + "/source/dockerfile"));
                references.AddRange(build.BuildSecrets.Values.Select(secret => new InputReference(InputKind.Secret, secret, component.Id, path + "/source/buildSecrets")));
            }
            references.AddRange(component.Storage.OfType<ReadOnlyInputStorage>().Select(s => new InputReference(InputKind.ReadOnlyInput, s.PathRef, component.Id, path + "/storage/" + s.Id)));
            references.AddRange(component.Parameters.Values.Concat(component.Environment.Values).Where(v => v.SecretRef is not null).Select(v => new InputReference(InputKind.Secret, v.SecretRef!, component.Id, path)));
        }
        foreach (BaselineApplication application in environment.Baseline.Applications)
        {
            foreach (Value value in application.Initializer.Steps.SelectMany(s => s.Headers.Values).Where(v => v.SecretRef is not null))
            { references.Add(new InputReference(InputKind.Secret, value.SecretRef!, application.ComponentId, "/baseline/headers")); }
        }
        var issues = new List<ReadinessIssue>(await inputs.CheckAsync(references, cancellationToken).ConfigureAwait(false));
        foreach (BaselineApplication application in environment.Baseline.Applications.Where(a => a.SeedPack is not null))
        {
            VerifiedSeedPack? pack;
            try { pack = await packs.FindAsync(application.SeedPack!, cancellationToken).ConfigureAwait(false); }
            catch (IOException) { issues.Add(new ReadinessIssue("pack-payload-unavailable", "/baseline", "Verified package payload is unavailable.")); continue; }
            if (pack is null) { issues.Add(new ReadinessIssue("missing-pack", "/baseline", "Exact package version is unavailable.")); continue; }
            EnvironmentComponent target = environment.Components.Single(c => c.Id == application.ComponentId);
            if (!pack.Manifest.Compatibility.Any(c => c.ContractId == target.DefinitionSnapshot.Contract.Id && c.Versions.Contains(target.DefinitionSnapshot.Contract.Version)))
            { throw new DocumentException("/baseline", "pack-contract-mismatch"); }
            InitializationValidator.Validate(application.Initializer, target.DefinitionSnapshot, pack.Manifest);
            if (!pack.Initializers.Values.Any(i => CanonicalJson.Hash(i) == CanonicalJson.Hash(application.Initializer)))
            { throw new DocumentException("/baseline", "initializer-not-declared-in-pack"); }
        }
        return issues;
    }
}
