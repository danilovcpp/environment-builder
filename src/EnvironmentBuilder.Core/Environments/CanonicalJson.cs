using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnvironmentBuilder.Core.Environments;

public static class CanonicalJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowOutOfOrderMetadataProperties = true,
        MaxDepth = 32,
        Converters = { new ContractInt32Converter(), new ContractInt64Converter() }
    };

    public static byte[] Encode(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        return Encode(document.RootElement);
    }

    public static byte[] Encode(JsonElement element, string? excludeRootProperty = null)
    {
        var text = new StringBuilder();
        Write(element, text, excludeRootProperty);
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    public static string Hash(string json) => Convert.ToHexStringLower(SHA256.HashData(Encode(json)));
    public static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(Encode(JsonSerializer.SerializeToElement(value, Options))));
    public static string ManifestHash(JsonElement manifest) => Convert.ToHexStringLower(SHA256.HashData(Encode(manifest, "contentHash")));

    private static void Write(JsonElement value, StringBuilder output, string? exclude = null)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                bool first = true;
                foreach (JsonProperty property in value.EnumerateObject().Where(p => p.Name != exclude).OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) { output.Append(','); }
                    first = false;
                    String(property.Name, output);
                    output.Append(':');
                    Write(property.Value, output);
                }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                bool firstItem = true;
                foreach (JsonElement item in value.EnumerateArray())
                {
                    if (!firstItem) { output.Append(','); }
                    firstItem = false;
                    Write(item, output);
                }
                output.Append(']');
                break;
            case JsonValueKind.String: String(value.GetString()!, output); break;
            case JsonValueKind.Number: output.Append(value.GetRawText()); break;
            case JsonValueKind.True: output.Append("true"); break;
            case JsonValueKind.False: output.Append("false"); break;
            case JsonValueKind.Null: output.Append("null"); break;
            default: throw new JsonException("Undefined JSON value.");
        }
    }

    private static void String(string value, StringBuilder output)
    {
        output.Append('"');
        foreach (char c in value)
        {
            output.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                _ => c.ToString()
            });
        }
        output.Append('"');
    }
}
