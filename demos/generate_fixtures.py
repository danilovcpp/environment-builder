"""Regenerate declarative demo inputs without Docker, HTTP, or application assemblies."""
import copy
import hashlib
import json
from pathlib import Path
import uuid
import zipfile

ROOT = Path(__file__).resolve().parent


def identity(name):
    return str(uuid.uuid5(uuid.NAMESPACE_URL, "environmentbuilder.demo/" + name))


def encode(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True).encode()


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(json.dumps(value, ensure_ascii=False, indent=2).encode() + b"\n")


def initializer(service, seed=False):
    return {
        "kind": "initialization", "schemaVersion": 1,
        "id": identity(service + ("/seed" if seed else "/bootstrap")), "version": "1.0.0",
        "targetContract": {"contractId": "demo." + service, "versions": ["1.0.0"]},
        "steps": [{"id": "seed" if seed else "bootstrap", "title": "Seed records" if seed else "Bootstrap",
                   "endpointId": "api", "method": "POST", "path": "/seed" if seed else "/bootstrap",
                   "headers": {}, "body": {"kind": "seedSection", "sectionId": "records"} if seed else {"kind": "none"},
                   "expect": {"statuses": [204], "assertions": []}, "timeoutSeconds": 120,
                   "retry": {"mode": "key", "maxAttempts": 3, "idempotencyHeader": "Idempotency-Key",
                             "safetyEvidence": "DemoStore commits Applied(key,action) in the same SQLite FULL transaction as the mutation; replay of the same key/action returns 204."}}]
    }


def generate():
    services = ["requests", "scheduler", "reporting"]
    secret = "secret://" + identity("broker-password")
    descriptors = {}
    for service in ["rabbitmq", *services]:
        rabbit = service == "rabbitmq"
        source = {"kind": "image", "reference": "rabbitmq:4-management"} if rabbit else {
            "kind": "build", "pathRef": "local-path://demo", "dockerfile": service.title() + ".Api/Dockerfile",
            "contextSubdirectory": ".", "buildArguments": {}, "buildSecrets": {}}
        values = {"RABBITMQ_DEFAULT_USER": {"literal": "demo"}, "RABBITMQ_DEFAULT_PASS": {"secretRef": secret}} if rabbit else {
            "BROKER_HOST": {"literal": "c-" + identity("rabbitmq/instance").replace("-", "")},
            "BROKER_USER": {"literal": "demo"}, "BROKER_PASSWORD": {"secretRef": secret}}
        requirements = []
        if not rabbit:
            for required in (["rabbitmq", "requests"] if service == "scheduler" else ["rabbitmq", "scheduler"] if service == "reporting" else ["rabbitmq"]):
                requirements.append({"id": required, "definitionId": identity(required + "/definition"), "definitionVersion": "1.0.0",
                                     "condition": "running" if required == "rabbitmq" else "ready", "reason": "Demo message pipeline prerequisite"})
        descriptor = {
            "kind": "component", "schemaVersion": 1, "id": identity(service + "/definition"), "version": "1.0.0", "name": service.title(),
            "componentType": "infrastructure" if rabbit else "target", "contract": {"id": "demo." + service, "version": "1.0.0"},
            "defaultSource": source, "parameterDefinitions": [], "defaultEnvironment": values, "defaultPorts": [],
            "defaultStorage": [{"kind": "managed", "id": "data", "targetPath": "/var/lib/rabbitmq" if rabbit else "/data"}],
            "endpoints": [] if rabbit else [{"id": "api", "containerPort": 8080, "scheme": "http", "basePath": "/"}],
            "readiness": {"kind": "running", "timeoutSeconds": 120} if rabbit else {"kind": "http", "endpointId": "api", "path": "/health", "timeoutSeconds": 120},
            "dependencyRequirements": requirements, "initialization": None if rabbit else initializer(service)
        }
        descriptors[service] = descriptor
        write(ROOT / "descriptors" / (service + ".component.json"), descriptor)
    manifests = {}
    for size, count in [("small", 10), ("medium", 1000), ("large", 100000)]:
        folder = ROOT / "seedpacks" / size
        section = folder / "sections" / "records.ndjson"
        section.parent.mkdir(parents=True, exist_ok=True)
        with section.open("wb") as stream:
            for number in range(count):
                stream.write(encode({"id": f"seed-{number:06}", "value": number}) + b"\n")
        entries = []
        for service in services:
            path = "initializers/" + service + ".json"
            write(folder / path, initializer(service, True))
            content = (folder / path).read_bytes()
            entries.append({"path": path, "bytes": len(content), "sha256": hashlib.sha256(content).hexdigest()})
        content = section.read_bytes()
        manifest = {"kind": "seedpack", "schemaVersion": 1, "id": identity("pack/" + size), "version": "1.0.0",
                    "title": size.title() + " demo records", "theme": "Demo", "purpose": "Deterministic independent service seed records",
                    "compatibility": [{"contractId": "demo." + s, "versions": ["1.0.0"]} for s in services],
                    "sections": [{"id": "records", "title": "Records", "path": "sections/records.ndjson", "bytes": len(content), "recordCount": count, "sha256": hashlib.sha256(content).hexdigest()}],
                    "initializers": entries}
        manifest["contentHash"] = hashlib.sha256(encode(manifest)).hexdigest()
        write(folder / "manifest.json", manifest)
        manifests[size] = manifest
    environment = {"kind": "environment", "schemaVersion": 1, "id": identity("environment"), "revision": 1, "name": "Demo pipeline", "components": [], "dependencies": [], "baseline": {"applications": []}}
    for service, definition in descriptors.items():
        component = {"id": identity(service + "/instance"), "definitionSnapshot": definition, "source": copy.deepcopy(definition["defaultSource"]),
                     "parameters": {}, "environment": copy.deepcopy(definition["defaultEnvironment"]), "ports": [], "storage": copy.deepcopy(definition["defaultStorage"])}
        environment["components"].append(component)
        for requirement in definition["dependencyRequirements"]:
            environment["dependencies"].append({"dependentId": component["id"], "requiredId": identity(requirement["id"] + "/instance"), "requirementId": requirement["id"], "condition": requirement["condition"], "reason": requirement["reason"]})
        if service != "rabbitmq":
            environment["baseline"]["applications"].extend([
                {"componentId": component["id"], "initializer": initializer(service), "seedPack": None},
                {"componentId": component["id"], "initializer": initializer(service, True), "seedPack": {k: manifests["small"][k] for k in ["id", "version", "contentHash"]}}])
    write(ROOT / "descriptors" / "environment.json", environment)


if __name__ == "__main__":
    generate()
