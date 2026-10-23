using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Imports;

namespace EnvironmentBuilder.IntegrationTests.Imports;

public sealed class StrictDocumentTests
{
    [Fact]
    public void Reader_enforces_size_depth_utf8_and_normalizes_only_typed_integer_fields()
    {
        var reader = new StrictDocumentReader();
        Assert.Throws<DocumentException>(() => reader.Read(new byte[4 * 1024 * 1024 + 1]));
        Assert.Throws<DocumentException>(() => reader.Read(new byte[] { 0xff }));
        string deep = "{\"kind\":\"component\",\"nested\":" + new string('[', 33) + "0" + new string(']', 33) + "}";
        Assert.Throws<DocumentException>(() => reader.Read(Encoding.UTF8.GetBytes(deep)));
        byte[] source = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Contracts/examples/environment.json"));
        string json = Encoding.UTF8.GetString(source);
        string equivalent = System.Text.RegularExpressions.Regex.Replace(json, "(\"(?:schemaVersion|revision)\"\\s*:\\s*)1(?=[,}\\s])", "${1}1e0");
        Assert.Equal(CanonicalJson.Hash(reader.Read(source)), CanonicalJson.Hash(reader.Read(Encoding.UTF8.GetBytes(equivalent))));
        byte[] component = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Contracts/examples/component.json"));
        Assert.Throws<DocumentException>(() => reader.Read(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(component) + new string(' ', 1024 * 1024))));
    }
    [Fact]
    public void Runtime_shape_rules_match_the_locally_resolved_schema_corpus()
    {
        using JsonDocument corpus = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/document-shape-corpus.json")));
        foreach (JsonElement test in corpus.RootElement.EnumerateArray())
        {
            JsonNode original = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Contracts/examples", test.GetProperty("file").GetString()!)))!;
            string kind = original["kind"]!.GetValue<string>();
            JsonNode parent = original;
            JsonElement[] path = test.GetProperty("path").EnumerateArray().ToArray();
            foreach (JsonElement segment in path[..^1]) { parent = segment.ValueKind == JsonValueKind.Number ? parent[segment.GetInt32()]! : parent[segment.GetString()!]!; }
            JsonNode? replacement = JsonNode.Parse(test.GetProperty("value").GetRawText());
            if (path[^1].ValueKind == JsonValueKind.Number) { parent[path[^1].GetInt32()] = replacement; }
            else { parent[path[^1].GetString()!] = replacement; }
            Exception? error = Record.Exception(() => DocumentShape.Validate(JsonSerializer.SerializeToElement(original), kind));
            Assert.True(test.GetProperty("valid").GetBoolean() == (error is null), test.GetRawText() + " => " + error?.Message);
        }
    }
    [Theory]
    [InlineData("component.json")]
    [InlineData("environment.json")]
    [InlineData("environment-two-instances.json")]
    [InlineData("initialization.json")]
    [InlineData("seedpack/manifest.json")]
    [InlineData("seedpack/initializers/requests.json")]
    public void Published_documents_roundtrip_without_reapplying_defaults(string file)
    {
        var reader = new StrictDocumentReader();
        object model = reader.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Contracts/examples", file)));
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(model, CanonicalJson.Options);
        object again = reader.Read(serialized);
        Assert.Equal(CanonicalJson.Hash(model), CanonicalJson.Hash(again));
    }

    [Fact]
    public void Missing_pack_is_deferred_but_invalid_graph_and_baseline_are_rejected()
    {
        var reader = new StrictDocumentReader();
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Contracts/examples/environment.json"));
        var model = reader.Read<IsolatedEnvironment>(bytes);
        Assert.NotEmpty(model.Components);
        Assert.Throws<DocumentException>(() => EnvironmentValidator.Validate(model with { Name = " " }));
        Assert.Throws<DocumentException>(() => EnvironmentValidator.Validate(model with { Components = [model.Components[0], model.Components[0]] }));
        Assert.Throws<DocumentException>(() => EnvironmentValidator.Validate(model with { Baseline = new DataBaseline([]) }));
        Assert.Throws<DocumentException>(() => EnvironmentValidator.Validate(model with
        { Dependencies = [new ComponentDependency(model.Components[0].Id, model.Components[0].Id, null, "running", "self")] }));
    }

    [Theory]
    [InlineData("{\"kind\":\"component\",\"kind\":\"component\"}")]
    [InlineData("{\"kind\":\"component\",\"schemaVersion\":2}")]
    [InlineData("{\"kind\":\"component\",\"schemaVersion\":1,\"name\":\"\\ud800\"}")]
    [InlineData("{\"kind\":\"component\",\"schemaVersion\":1,}")]
    [InlineData("{/*comment*/\"kind\":\"component\"}")]
    public void Strict_parser_rejects_ambiguous_or_unsupported_json(string json) =>
        Assert.Throws<DocumentException>(() => new StrictDocumentReader().Read(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Shape_rules_reject_every_missing_required_and_unknown_property_in_examples()
    {
        foreach (string file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Contracts/examples"), "*.json", SearchOption.AllDirectories))
        {
            JsonNode node = JsonNode.Parse(File.ReadAllBytes(file))!;
            string kind = node["kind"]!.GetValue<string>();
            JsonObject root = node.AsObject();
            root["unknown"] = true;
            Assert.Throws<DocumentException>(() => DocumentShape.Validate(JsonSerializer.SerializeToElement(root), kind));
            root.Remove("unknown");
            foreach (string property in root.Select(p => p.Key).Where(k => k != "description").ToArray())
            {
                JsonNode? value = root[property];
                root.Remove(property);
                Assert.Throws<DocumentException>(() => DocumentShape.Validate(JsonSerializer.SerializeToElement(root), kind));
                root[property] = value;
            }
        }
    }

    [Theory]
    [InlineData("/safe/../escape")]
    [InlineData("//foreign/path")]
    [InlineData("/%252e%252e/path")]
    [InlineData("/safe%0d%0aHost:evil")]
    public void Decoded_request_traversal_is_rejected(string value) =>
        Assert.Throws<DocumentException>(() => InitializationValidator.RequireRequestPath(value, "/path"));
}
