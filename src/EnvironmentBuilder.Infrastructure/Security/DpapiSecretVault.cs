using System.Security.Cryptography;
using System.Text;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Infrastructure.Persistence;

namespace EnvironmentBuilder.Infrastructure.Security;

public sealed class DpapiSecretVault(SqliteWorker worker, IClock clock) : ISecretVault
{
    public async Task<string> SaveAsync(string label, ReadOnlyMemory<char> value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 256 || value.Length is 0 or > 65536) { throw new ArgumentException("A bounded label and nonempty secret are required."); }
        string reference = "secret://" + Guid.NewGuid();
        byte[] plaintext = new byte[Encoding.UTF8.GetByteCount(value.Span)];
        Encoding.UTF8.GetBytes(value.Span, plaintext);
        byte[] ciphertext;
        try { ciphertext = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        await worker.CommitAsync("secret", (connection, transaction) => Sql.Execute(connection, transaction,
            "INSERT INTO Secrets VALUES($reference,$label,$ciphertext,$at)", ("$reference", reference), ("$label", label), ("$ciphertext", ciphertext), ("$at", Sql.Instant(clock.UtcNow))), cancellationToken).ConfigureAwait(false);
        return reference;
    }

    public async Task<SecretValue?> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(reference, "^secret://[0-9a-f-]{36}\\z")) { return null; }
        byte[]? encrypted = await worker.ReadAsync(connection =>
        {
            using var command = Sql.Command(connection, null, "SELECT ciphertext FROM Secrets WHERE reference=$reference", ("$reference", reference));
            return command.ExecuteScalar() as byte[];
        }, cancellationToken).ConfigureAwait(false);
        if (encrypted is null) { return null; }
        byte[] bytes;
        try { bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { return null; }
        try { return new SecretValue(new UTF8Encoding(false, true).GetChars(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
