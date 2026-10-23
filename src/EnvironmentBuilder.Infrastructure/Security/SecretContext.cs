using EnvironmentBuilder.Core.Abstractions;

namespace EnvironmentBuilder.Infrastructure.Security;

public sealed class SecretContext : IDisposable
{
    private readonly List<SecretValue> values;
    private bool disposed;
    private SecretContext(List<SecretValue> values) { this.values = values; }
    public IReadOnlyList<ReadOnlyMemory<char>> Values
    {
        get { ObjectDisposedException.ThrowIf(disposed, this); return values.Select(v => v.Value).ToArray(); }
    }

    public static async Task<SecretContext> ResolveAsync(ISecretVault vault, IEnumerable<string> references, CancellationToken cancellationToken)
    {
        var values = new List<SecretValue>();
        try
        {
            foreach (string reference in references.Distinct(StringComparer.Ordinal))
            { values.Add(await vault.ResolveAsync(reference, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Secret context is unresolved; output remains closed.")); }
            return new SecretContext(values);
        }
        catch { foreach (SecretValue value in values) { value.Dispose(); } throw; }
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        foreach (SecretValue value in values) { value.Dispose(); }
        values.Clear();
    }
}
