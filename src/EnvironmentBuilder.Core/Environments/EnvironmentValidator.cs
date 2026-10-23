using System.Globalization;
using System.Text.Json;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.Core.Environments;

public static class EnvironmentValidator
{
    public static void Validate(IsolatedEnvironment environment)
    {
        DocumentShape.Validate(JsonSerializer.SerializeToElement(environment, CanonicalJson.Options), "environment");
        Name(environment.Name, "/name");
        Unique(environment.Components.Select(c => c.Id.ToString()), "/components");
        var bindings = new HashSet<(string Protocol, int Port)>();
        foreach (EnvironmentComponent component in environment.Components)
        {
            ValidateDefinition(component.DefinitionSnapshot);
            ValidateValues(component.DefinitionSnapshot, component.Source, component.Parameters, component.Environment, component.Ports, component.Storage);
            foreach (Port port in component.Ports.Where(p => p.HostPort.HasValue))
            {
                if (!bindings.Add((port.Protocol, port.HostPort!.Value)))
                { throw new DocumentException("/components/" + component.Id + "/ports", "host-port-overlap"); }
            }
        }
        DependencyValidator.Validate(environment);
        Unique(environment.Baseline.Applications.Select(CanonicalJson.Hash), "/baseline/applications");
        foreach (BaselineApplication application in environment.Baseline.Applications)
        {
            EnvironmentComponent? component = environment.Components.FirstOrDefault(c => c.Id == application.ComponentId);
            if (component is null) { throw new DocumentException("/baseline", "missing-instance"); }
            if (component.DefinitionSnapshot.Readiness.Kind == "none") { throw new DocumentException("/baseline", "readiness-none"); }
            InitializationValidator.Validate(application.Initializer, component.DefinitionSnapshot);
            if (application.SeedPack is null && application.Initializer.Steps.Any(s => s.Body.Kind == "seedSection"))
            { throw new DocumentException("/baseline", "section-without-pack"); }
        }
        foreach (EnvironmentComponent component in environment.Components)
        {
            InitializationDefinition? mandatory = component.DefinitionSnapshot.Initialization;
            if (mandatory is null) { continue; }
            BaselineApplication[] applications = environment.Baseline.Applications.Where(a => a.ComponentId == component.Id).ToArray();
            if (applications.Length == 0 || applications[0].SeedPack is not null || CanonicalJson.Hash(applications[0].Initializer) != CanonicalJson.Hash(mandatory) ||
                applications.Count(a => a.SeedPack is null && CanonicalJson.Hash(a.Initializer) == CanonicalJson.Hash(mandatory)) != 1)
            { throw new DocumentException("/baseline", "mandatory-snapshot-missing-or-out-of-order"); }
        }
    }

    public static void ValidateDefinition(ComponentDefinition definition)
    {
        DocumentShape.Validate(JsonSerializer.SerializeToElement(definition, CanonicalJson.Options), "component");
        Name(definition.Name, "/name");
        Unique(definition.ParameterDefinitions.Select(p => p.Name), "/parameterDefinitions");
        Unique(definition.ParameterDefinitions.Select(p => p.EnvironmentVariable), "/parameterDefinitions/environmentVariable");
        Unique(definition.DependencyRequirements.Select(p => p.Id), "/dependencyRequirements");
        Unique(definition.Endpoints.Select(p => p.Id), "/endpoints");
        foreach (Endpoint endpoint in definition.Endpoints) { InitializationValidator.RequireRequestPath(endpoint.BasePath, "/endpoints/basePath", true); }
        var defaults = definition.ParameterDefinitions.Where(p => p.DefaultValue is not null).ToDictionary(p => p.Name, p => p.DefaultValue!);
        ValidateValues(definition, definition.DefaultSource, defaults, definition.DefaultEnvironment, definition.DefaultPorts, definition.DefaultStorage, false);
        if (definition.Readiness.Kind == "http")
        {
            if (!definition.Endpoints.Any(e => e.Id == definition.Readiness.EndpointId)) { throw new DocumentException("/readiness", "missing-endpoint"); }
            InitializationValidator.RequireRequestPath(definition.Readiness.Path!, "/readiness/path");
        }
        if (definition.Initialization is not null)
        {
            if (definition.Readiness.Kind == "none") { throw new DocumentException("/initialization", "readiness-none"); }
            InitializationValidator.Validate(definition.Initialization, definition);
            if (definition.Initialization.Steps.Any(s => s.Body.Kind == "seedSection")) { throw new DocumentException("/initialization", "mandatory-requires-pack"); }
        }
    }

    public static void ValidatePack(SeedPack pack)
    {
        JsonElement json = JsonSerializer.SerializeToElement(pack, CanonicalJson.Options);
        DocumentShape.Validate(json, "seedpack");
        Unique(pack.Sections.Select(s => s.Id), "/sections");
        Unique(pack.Sections.Select(s => s.Path).Concat(pack.Initializers.Select(i => i.Path)), "/entries", StringComparer.OrdinalIgnoreCase);
        Unique(pack.Compatibility.Select(c => c.ContractId), "/compatibility");
        if (pack.ContentHash != CanonicalJson.ManifestHash(json)) { throw new DocumentException("/contentHash", "manifest-hash-mismatch"); }
    }

    private static void ValidateValues(ComponentDefinition definition, ComponentSource source, IReadOnlyDictionary<string, Value> parameters,
        IReadOnlyDictionary<string, Value> environment, IEnumerable<Port> ports, IEnumerable<Storage> storage, bool requireValues = true)
    {
        if (definition.ComponentType == "infrastructure" && source is not ImageSource) { throw new DocumentException("/source", "infrastructure-image-only"); }
        if (source is ImageSource image && (image.Reference.Any(char.IsWhiteSpace) || image.Reference.Contains("://", StringComparison.Ordinal)))
        { throw new DocumentException("/source/reference", "invalid-image-reference"); }
        if (source is BuildSource build)
        {
            SafeRelativePath(build.ContextSubdirectory, "/source/contextSubdirectory", true);
            SafeRelativePath(build.Dockerfile, "/source/dockerfile");
        }
        foreach (string key in parameters.Keys)
        {
            if (!definition.ParameterDefinitions.Any(p => p.Name == key)) { throw new DocumentException("/parameters/" + key, "undeclared-parameter"); }
        }
        foreach (ParameterDefinition parameter in definition.ParameterDefinitions)
        {
            if (environment.ContainsKey(parameter.EnvironmentVariable)) { throw new DocumentException("/environment/" + parameter.EnvironmentVariable, "binding-collision"); }
            if (!parameters.TryGetValue(parameter.Name, out Value? value))
            {
                if (requireValues && parameter.Required) { throw new DocumentException("/parameters/" + parameter.Name, "required-parameter"); }
                continue;
            }
            if (parameter.Secret && value.SecretRef is null) { throw new DocumentException("/parameters/" + parameter.Name, "plaintext-secret-forbidden"); }
            if (value.Literal is not null) { ValidateLiteral(parameter, value.Literal); }
        }
        Unique(ports.Select(p => p.Id), "/ports");
        Unique(ports.Where(p => p.HostPort.HasValue).Select(p => p.Protocol + ":" + p.HostPort), "/ports/hostPort");
        foreach (Port port in ports)
        {
            if (port.Purpose == "control" && (port.Access != "loopback" || port.Protocol != "tcp")) { throw new DocumentException("/ports", "unsafe-control-port"); }
        }
        Storage[] mounts = storage.ToArray();
        Unique(mounts.Select(s => s.Id), "/storage");
        for (int i = 0; i < mounts.Length; i++)
        {
            string target = mounts[i].TargetPath;
            if (!target.StartsWith('/') || target == "/" || target.Contains("//", StringComparison.Ordinal) || target.EndsWith('/') || target.Contains('\\') || target.Contains(':') || target.Any(char.IsControl) ||
                target.Split('/').Any(s => s is "." or "..") || target.StartsWith("/proc", StringComparison.Ordinal) || target.StartsWith("/sys", StringComparison.Ordinal) ||
                target.StartsWith("/dev", StringComparison.Ordinal) || target.Contains("docker.sock", StringComparison.OrdinalIgnoreCase) || target.Contains("containerd.sock", StringComparison.OrdinalIgnoreCase))
            { throw new DocumentException("/storage", "unsafe-mount"); }
            for (int j = 0; j < i; j++)
            {
                string previous = mounts[j].TargetPath.TrimEnd('/');
                if (target.TrimEnd('/') == previous || target.StartsWith(previous + "/", StringComparison.Ordinal) || previous.StartsWith(target.TrimEnd('/') + "/", StringComparison.Ordinal))
                { throw new DocumentException("/storage", "overlapping-mounts"); }
            }
        }
    }

    public static void ValidateLiteral(ParameterDefinition parameter, string value)
    {
        bool valid = parameter.Type switch
        {
            "integer" => System.Text.RegularExpressions.Regex.IsMatch(value, "^-?(0|[1-9][0-9]*)\\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
            "boolean" => value is "true" or "false",
            "string" => true,
            _ => false
        };
        if (!valid) { throw new DocumentException("/parameters/" + parameter.Name, "invalid-parameter-type"); }
    }

    public static void SafeRelativePath(string path, string fieldPath, bool allowRoot = false)
    {
        if (allowRoot && path == ".") { return; }
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl) ||
            path.Split('/').Any(s => s is "" or "." or ".." || s.EndsWith('.') || s.EndsWith(' ')))
        { throw new DocumentException(fieldPath, "unsafe-relative-path"); }
    }

    public static void Unique(IEnumerable<string> values, string path, StringComparer? comparer = null)
    {
        var seen = new HashSet<string>(comparer ?? StringComparer.Ordinal);
        foreach (string value in values) { if (!seen.Add(value)) { throw new DocumentException(path, "duplicate-identity"); } }
    }
    private static void Name(string value, string path) { if (string.IsNullOrWhiteSpace(value)) { throw new DocumentException(path, "blank-name"); } }
}
