using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace EnvironmentBuilder.Infrastructure.Security;

public sealed record SanitizedText
{
    internal SanitizedText(string value) { Value = value; }
    public string Value { get; }
}

// Owns the complete context for the lifetime of a single output stream.
public sealed class SecretRedactor : IDisposable
{
    private readonly SecretContext context;
    private readonly string[] variants;
    private readonly int tailLength;
    private string pending = "";
    private bool disposed;

    public SecretRedactor(SecretContext context)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        variants = context.Values.SelectMany(Forms).Where(s => s.Length != 0).Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToArray();
        tailLength = Math.Max(0, variants.Select(s => s.Length).DefaultIfEmpty(1).Max() - 1);
    }

    public SanitizedText Redact(string value, string? fieldName = null)
    {
        RequireContext();
        if (fieldName is not null && IsSensitive(fieldName)) { return new SanitizedText("[REDACTED]"); }
        return new SanitizedText(Scan(value, value.Length, out _));
    }

    public SanitizedText Append(string chunk, bool final = false)
    {
        RequireContext();
        string combined = pending + chunk;
        int limit = final ? combined.Length : Math.Max(0, combined.Length - tailLength);
        string result = Scan(combined, limit, out int consumed);
        pending = combined[consumed..];
        return new SanitizedText(result);
    }

    private string Scan(string value, int limit, out int consumed)
    {
        var output = new StringBuilder();
        int i = 0;
        while (i < limit)
        {
            string? match = variants.FirstOrDefault(secret => value.AsSpan(i).StartsWith(secret, StringComparison.Ordinal));
            if (match is null) { output.Append(value[i++]); }
            else { output.Append("[REDACTED]"); i += match.Length; }
        }
        consumed = i;
        return output.ToString();
    }

    private static IEnumerable<string> Forms(ReadOnlyMemory<char> memory)
    {
        string value = new(memory.Span);
        yield return value;
        yield return Uri.EscapeDataString(value);
        yield return System.Text.RegularExpressions.Regex.Replace(Uri.EscapeDataString(value), "%[0-9A-F]{2}", m => m.Value.ToLowerInvariant());
        string json = JsonSerializer.Serialize(value)[1..^1];
        yield return json;
        yield return System.Text.RegularExpressions.Regex.Replace(json, "\\\\u[0-9A-Fa-f]{4}", m => m.Value.ToLowerInvariant());
        yield return JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })[1..^1];
        yield return string.Concat(value.Select(c => "\\u" + ((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static bool IsSensitive(string name) => new[] { "password", "secret", "token", "authorization", "cookie", "body", "compose", "privateEnvironment" }.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
    private void RequireContext() { ObjectDisposedException.ThrowIf(disposed, this); _ = context.Values; }
    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        pending = "";
        Array.Clear(variants);
        context.Dispose();
    }
}
