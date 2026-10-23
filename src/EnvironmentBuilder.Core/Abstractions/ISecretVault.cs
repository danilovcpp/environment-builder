namespace EnvironmentBuilder.Core.Abstractions;

// Application-owned vault; caller must dispose every returned plaintext value.
public interface ISecretVault
{
    Task<string> SaveAsync(string label, ReadOnlyMemory<char> value, CancellationToken cancellationToken);
    Task<SecretValue?> ResolveAsync(string reference, CancellationToken cancellationToken);
}
public sealed class SecretValue(char[] value) : IDisposable
{
    private char[]? buffer = value;
    public ReadOnlyMemory<char> Value => buffer ?? throw new ObjectDisposedException(nameof(SecretValue));
    public void Dispose()
    {
        char[]? owned = Interlocked.Exchange(ref buffer, null);
        if (owned is not null) { Array.Clear(owned); }
    }
}
