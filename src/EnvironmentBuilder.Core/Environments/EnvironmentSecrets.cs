using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.Core.Environments;

public static class EnvironmentSecrets
{
    // Only effective values are resolved. Unused defaults and literal strings that resemble references are data.
    public static IReadOnlyList<string> References(IsolatedEnvironment environment)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (EnvironmentComponent component in environment.Components)
        {
            foreach (Value value in component.Parameters.Values.Concat(component.Environment.Values))
            { if (value.SecretRef is not null) { result.Add(value.SecretRef); } }
            if (component.Source is BuildSource build) { result.UnionWith(build.BuildSecrets.Values); }
        }
        foreach (BaselineApplication application in environment.Baseline.Applications)
        {
            foreach (Value value in application.Initializer.Steps.SelectMany(s => s.Headers.Values))
            { if (value.SecretRef is not null) { result.Add(value.SecretRef); } }
        }
        return result.ToArray();
    }
}
