"""Offline schema oracle for runtime parity fixtures. All $ref resolve in the local registry."""
import copy
import json
from pathlib import Path
from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource

ROOT = Path(__file__).resolve().parents[2]
CONTRACTS = ROOT / "specs/001-environment-builder/contracts"
schemas = [json.loads(p.read_text(encoding="utf-8")) for p in CONTRACTS.glob("*.schema.json")]
registry = Registry().with_resources((s["$id"], Resource.from_contents(s)) for s in schemas)
validators = {s["properties"]["kind"]["const"]: Draft202012Validator(s, registry=registry, format_checker=FormatChecker()) for s in schemas if "properties" in s}
cases = []


def walk(node, path):
    yield path, node
    if isinstance(node, dict):
        for name, value in node.items():
            yield from walk(value, path + [name])
    elif isinstance(node, list):
        for index, value in enumerate(node):
            yield from walk(value, path + [index])


for file in sorted((CONTRACTS / "examples").rglob("*.json")):
    original = json.loads(file.read_text(encoding="utf-8"))
    validator = validators[original["kind"]]
    assert validator.is_valid(original), file
    for path, value in walk(original, []):
        if not path:
            continue
        replacements = [None, {}, [], True, 0, "", "unexpected\n", 1.5, 1e-100]
        if isinstance(value, int) and not isinstance(value, bool):
            replacements += [float(value), -1, 65536, 9007199254740992]
        if isinstance(value, dict):
            replacements += [dict(value, unexpected=True)]
        if isinstance(value, list):
            replacements += [value + value] if value else []
            if value and all(isinstance(item, int) for item in value):
                replacements += [value + [float(value[0])]]
        for replacement in replacements:
            candidate = copy.deepcopy(original)
            parent = candidate
            for segment in path[:-1]:
                parent = parent[segment]
            parent[path[-1]] = replacement
            cases.append(dict(file=file.relative_to(CONTRACTS / "examples").as_posix(), path=path, value=replacement, valid=validator.is_valid(candidate)))
output = ROOT / "tests/EnvironmentBuilder.IntegrationTests/Fixtures/document-shape-corpus.json"
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(cases, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
print(f"Generated {len(cases)} locally validated shape cases.")
