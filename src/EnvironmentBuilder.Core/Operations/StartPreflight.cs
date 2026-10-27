using System.Collections.Immutable;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.Operations;

public sealed record InputDelta(string Reference, string Before, string After);
public sealed class PreflightBlockedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
public static class StartPreflight
{
    public static ImmutableArray<InputDelta> Delta(PreviewSnapshot? reviewed, PreviewSnapshot current)
    {
        if (reviewed is null) { return [new("full-review", "not-reviewed", current.Token)]; }
        var changes = ImmutableArray.CreateBuilder<InputDelta>();
        if (reviewed.EnvironmentId != current.EnvironmentId || reviewed.Revision != current.Revision || reviewed.ConfigurationHash != current.ConfigurationHash)
        { changes.Add(new("configuration", reviewed.ConfigurationHash, current.ConfigurationHash)); }
        foreach (string key in reviewed.Inputs.Select(i => i.Reference).Union(current.Inputs.Select(i => i.Reference), StringComparer.Ordinal))
        {
            PreviewInput? before = reviewed.Inputs.FirstOrDefault(i => i.Reference == key);
            PreviewInput? after = current.Inputs.FirstOrDefault(i => i.Reference == key);
            if (before != after) { changes.Add(new(key, Describe(before), Describe(after))); }
        }
        if (reviewed.Token != current.Token && changes.Count == 0) { changes.Add(new("effective-inputs", reviewed.Token, current.Token)); }
        return changes.ToImmutable();
    }
    private static string Describe(PreviewInput? input) => input is null ? "absent" : input.State + ":" + (input.ContentHash ?? input.ImageIdentity ?? "unknown");
}
