"""Compile the fixed published v1 shapes into ordinary C# checks; no runtime schema engine."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTRACTS = ROOT / "specs/001-environment-builder/contracts"
schemas = {p.name: json.loads(p.read_text(encoding="utf-8")) for p in sorted(CONTRACTS.glob("*.schema.json"))}
methods = []
names = {}


def literal(value):
    return json.dumps(value, ensure_ascii=True)


def compile_node(file, pointer, node):
    key = (file, pointer)
    if key in names:
        return names[key]
    name = "Rule" + str(len(names))
    names[key] = name
    body = []
    def require(expr):
        body.append(f"        Require({expr}, path);")
    if "$ref" in node:
        target, _, fragment = node["$ref"].partition("#")
        target = target or file
        resolved = schemas[target]
        for segment in fragment.split("/")[1:]:
            resolved = resolved[segment]
        called = compile_node(target, fragment, resolved)
        body.append(f"        {called}(value, path);")
    else:
        kind = node.get("type")
        if kind:
            expr = {"object":"value.ValueKind == JsonValueKind.Object", "array":"value.ValueKind == JsonValueKind.Array",
                    "string":"value.ValueKind == JsonValueKind.String", "boolean":"value.ValueKind is JsonValueKind.True or JsonValueKind.False",
                    "integer":"TryInteger(value, out _)", "null":"value.ValueKind == JsonValueKind.Null"}[kind]
            require(expr)
        if "const" in node:
            require(f"TryInteger(value, out long constant) && constant == {node['const']}" if isinstance(node['const'], int) else f"value.ValueKind == JsonValueKind.String && value.GetString() == {literal(node['const'])}")
        if "enum" in node:
            require("value.ValueKind == JsonValueKind.String && value.GetString() is " + " or ".join(literal(v) for v in node["enum"]))
        for bound, op in [("minLength", ">="), ("maxLength", "<=" )]:
            if bound in node:
                require(f"value.GetString()!.EnumerateRunes().Count() {op} {node[bound]}")
        if "pattern" in node:
            require(f"Matches(value.GetString()!, {literal(node['pattern'])})")
        for bound, op in [("minimum", ">="), ("maximum", "<=")]:
            if bound in node:
                require(f"Integer(value) {op} {node[bound]}L")
        for union in ["oneOf", "anyOf"]:
            if union in node:
                calls = [compile_node(file, pointer + f"/{union}/{i}", child) for i, child in enumerate(node[union])]
                body.append(f"        Alternatives(value, path, {str(union == 'oneOf').lower()}, [{', '.join(calls)}]);")
        if kind == "object":
            properties = node.get("properties", {})
            for prop in node.get("required", []):
                require(f"value.TryGetProperty({literal(prop)}, out _)")
            body.append("        foreach (JsonProperty property in value.EnumerateObject())\n        {")
            if "propertyNames" in node:
                body.append(f"            Require(Matches(property.Name, {literal(node['propertyNames']['pattern'])}), path);")
            body.append("            switch (property.Name)\n            {")
            for prop, child in properties.items():
                called = compile_node(file, pointer + "/properties/" + prop, child)
                body.append(f"                case {literal(prop)}: {called}(property.Value, path + {literal('/' + prop)}); break;")
            extra = node.get("additionalProperties", True)
            if isinstance(extra, dict):
                called = compile_node(file, pointer + "/additionalProperties", extra)
                body.append(f"                default: {called}(property.Value, path + \"/\" + property.Name); break;")
            elif extra is False:
                body.append("                default: throw new DocumentException(path + \"/\" + property.Name, \"unknown-property\");")
            body.append("            }\n        }")
        if kind == "array":
            for bound, op in [("minItems", ">="), ("maxItems", "<=")]:
                if bound in node:
                    require(f"value.GetArrayLength() {op} {node[bound]}")
            if "items" in node:
                called = compile_node(file, pointer + "/items", node["items"])
                body.append(f"        for (int i = 0; i < value.GetArrayLength(); i++) {{ {called}(value[i], path + \"/\" + i); }}")
            if node.get("uniqueItems"):
                if node.get("items", {}).get("type") == "integer":
                    require("value.EnumerateArray().Select(Integer).Distinct().Count() == value.GetArrayLength()")
                else:
                    require("value.EnumerateArray().Select(item => Convert.ToBase64String(CanonicalJson.Encode(item))).Distinct(StringComparer.Ordinal).Count() == value.GetArrayLength()")
        for i, clause in enumerate(node.get("allOf", [])):
            # The only conditional in the fixed corpus: infrastructure has an image default.
            assert clause["if"]["properties"]["componentType"]["const"] == "infrastructure"
            body.append('        if (value.GetProperty("componentType").GetString() == "infrastructure")\n        {\n            Require(value.GetProperty("defaultSource").GetProperty("kind").GetString() == "image", path + "/defaultSource");\n        }')
    methods.append("    private static void " + name + "(JsonElement value, string path)\n    {\n" + "\n".join(body) + "\n    }")
    return name


roots = {file.removesuffix(".schema.json"): compile_node(file, "", schema) for file, schema in schemas.items() if file != "common.schema.json"}
header = '''// <auto-generated/> Generated by scripts/tooling/generate_contract_rules.py.
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EnvironmentBuilder.Core.Environments;

public static class DocumentShape
{
    public static void Validate(JsonElement value, string kind)
    {
        switch (kind)
        {
'''
footer = '''
    private static bool TryInteger(JsonElement value, out long number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && ContractInteger.TryRead(value.GetRawText(), out number);
    }
    private static long Integer(JsonElement value) => TryInteger(value, out long number) ? number : throw new DocumentException("", "invalid-integer");
    private static void Require(bool condition, string path)
    {
        if (!condition) { throw new DocumentException(path, "invalid-shape"); }
    }
    private static bool Matches(string value, string pattern) =>
        Regex.IsMatch(value, pattern + "\\\\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static void Alternatives(JsonElement value, string path, bool exact, Action<JsonElement, string>[] rules)
    {
        int matches = 0;
        foreach (Action<JsonElement, string> rule in rules)
        {
            try { rule(value, path); matches++; }
            catch (DocumentException) { }
        }
        Require(exact ? matches == 1 : matches > 0, path);
    }
}
'''
switch = "\n".join(f"            case {literal(kind)}: {rule}(value, \"\"); break;" for kind, rule in roots.items())
source = header + switch + '\n            default: throw new DocumentException("/kind", "unsupported-kind");\n        }\n    }\n\n' + "\n\n".join(methods) + footer
(ROOT / "src/EnvironmentBuilder.Core/Environments/DocumentShape.g.cs").write_text(source, encoding="utf-8")
