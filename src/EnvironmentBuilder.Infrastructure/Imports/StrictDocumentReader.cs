using System.Globalization;
using System.Text;
using System.Text.Json;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.Infrastructure.Imports;

public sealed class StrictDocumentReader
{
    public object Read(ReadOnlyMemory<byte> utf8, string? expectedKind = null)
    {
        if (utf8.Length > 4 * 1024 * 1024) { throw new DocumentException("", "document-too-large"); }
        try
        {
            _ = new UTF8Encoding(false, true).GetString(utf8.Span);
            CheckTokens(utf8.Span);
            using JsonDocument document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out JsonElement kindElement) || kindElement.ValueKind != JsonValueKind.String)
            { throw new DocumentException("/kind", "missing-kind"); }
            string kind = kindElement.GetString()!;
            if (expectedKind is not null && kind != expectedKind) { throw new DocumentException("/kind", "unexpected-kind"); }
            if (kind != "environment" && utf8.Length > 1024 * 1024) { throw new DocumentException("", "document-too-large"); }
            if (!root.TryGetProperty("schemaVersion", out JsonElement version) || version.ValueKind != JsonValueKind.Number || !ContractInteger.TryRead(version.GetRawText(), out long v) || v != 1)
            { throw new DocumentException("/schemaVersion", "unsupported-version"); }
            DocumentShape.Validate(root, kind);
            switch (kind)
            {
                case "environment":
                    var environment = root.Deserialize<IsolatedEnvironment>(CanonicalJson.Options)!;
                    EnvironmentValidator.Validate(environment);
                    return environment;
                case "component":
                    var component = root.Deserialize<ComponentDefinition>(CanonicalJson.Options)!;
                    EnvironmentValidator.ValidateDefinition(component);
                    return component;
                case "initialization":
                    var initializer = root.Deserialize<InitializationDefinition>(CanonicalJson.Options)!;
                    InitializationValidator.Validate(initializer);
                    return initializer;
                case "seedpack":
                    var pack = root.Deserialize<SeedPack>(CanonicalJson.Options)!;
                    EnvironmentValidator.ValidatePack(pack);
                    return pack;
                default: throw new DocumentException("/kind", "unsupported-kind");
            }
        }
        catch (JsonException) { throw new DocumentException("", "invalid-json"); }
        catch (DecoderFallbackException) { throw new DocumentException("", "invalid-utf8"); }
    }

    public T Read<T>(ReadOnlyMemory<byte> utf8, string? expectedKind = null) where T : class =>
        Read(utf8, expectedKind) as T ?? throw new DocumentException("/kind", "unexpected-kind");

    private static void CheckTokens(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = 32 });
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) { objects.Push(new HashSet<string>(StringComparer.Ordinal)); }
            if (reader.TokenType == JsonTokenType.EndObject) { objects.Pop(); }
            if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
            {
                CheckUnicodeEscapes(reader.ValueSpan);
                if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))
                { throw new DocumentException("", "duplicate-property"); }
            }
        }
    }

    private static void CheckUnicodeEscapes(ReadOnlySpan<byte> token)
    {
        for (int i = 0; i < token.Length; i++)
        {
            if (token[i] != '\\') { continue; }
            i++;
            if (token[i] != 'u') { continue; }
            int code = int.Parse(Encoding.ASCII.GetString(token.Slice(i + 1, 4)), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            i += 4;
            if (code is >= 0xdc00 and <= 0xdfff) { throw new DocumentException("", "invalid-unicode"); }
            if (code is < 0xd800 or > 0xdbff) { continue; }
            if (i + 6 >= token.Length || token[i + 1] != '\\' || token[i + 2] != 'u') { throw new DocumentException("", "invalid-unicode"); }
            int low = int.Parse(Encoding.ASCII.GetString(token.Slice(i + 3, 4)), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (low is < 0xdc00 or > 0xdfff) { throw new DocumentException("", "invalid-unicode"); }
            i += 6;
        }
    }
}
