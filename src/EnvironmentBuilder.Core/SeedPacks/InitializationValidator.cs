using System.Text.RegularExpressions;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.SeedPacks;

public static class InitializationValidator
{
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Host", "Content-Length", "Transfer-Encoding", "Connection", "Content-Type", "Proxy-Authorization", "Proxy-Connection", "Upgrade", "Trailer", "TE", "Expect" };

    public static void Validate(InitializationDefinition initializer, ComponentDefinition? target = null, SeedPack? pack = null)
    {
        DocumentShape.Validate(System.Text.Json.JsonSerializer.SerializeToElement(initializer, CanonicalJson.Options), "initialization");
        EnvironmentValidator.Unique(initializer.Steps.Select(s => s.Id), "/steps");
        if (target is not null && (initializer.TargetContract.ContractId != target.Contract.Id || !initializer.TargetContract.Versions.Contains(target.Contract.Version)))
        { throw new DocumentException("/targetContract", "contract-mismatch"); }
        foreach (InitializationStep step in initializer.Steps)
        {
            string path = "/steps/" + step.Id;
            RequireRequestPath(step.Path, path + "/path");
            if (target is not null && !target.Endpoints.Any(e => e.Id == step.EndpointId))
            { throw new DocumentException(path + "/endpointId", "missing-endpoint"); }
            if (step.Method is "GET" or "HEAD" && step.Body.Kind != "none")
            { throw new DocumentException(path + "/body", "body-forbidden"); }
            EnvironmentValidator.Unique(step.Headers.Keys, path + "/headers", StringComparer.OrdinalIgnoreCase);
            foreach ((string header, Value value) in step.Headers)
            {
                ValidateHeader(header, path);
                if (value.Literal?.Any(char.IsControl) == true)
                { throw new DocumentException(path + "/headers", "unsafe-header-value"); }
            }
            if (step.Retry.Mode == "key")
            {
                ValidateHeader(step.Retry.IdempotencyHeader!, path);
                if (step.Headers.Keys.Contains(step.Retry.IdempotencyHeader!, StringComparer.OrdinalIgnoreCase))
                { throw new DocumentException(path + "/retry", "idempotency-header-collision"); }
            }
            if (step.Retry.Mode != "none" && string.IsNullOrWhiteSpace(step.Retry.SafetyEvidence))
            { throw new DocumentException(path + "/retry", "missing-safety-evidence"); }
            foreach (JsonAssertion assertion in step.Expect.Assertions)
            {
                if (Regex.IsMatch(assertion.Pointer, "~(?![01])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                { throw new DocumentException(path + "/expect", "invalid-json-pointer"); }
            }
            if (step.Body.Kind == "seedSection" && pack is not null && !pack.Sections.Any(s => s.Id == step.Body.SectionId))
            { throw new DocumentException(path + "/body", "unknown-section"); }
        }
    }

    public static void RequireRequestPath(string value, string fieldPath, bool basePath = false)
    {
        string decoded = value;
        for (int i = 0; i < 8; i++)
        {
            if (!decoded.StartsWith('/') || decoded.StartsWith("//", StringComparison.Ordinal) ||
                decoded.Contains('\\') || decoded.Contains('#') || decoded.Any(char.IsControl) ||
                decoded.Split('?', 2)[0].Split('/').Any(s => s is "." or "..") ||
                (basePath && (decoded.Contains('?') || (decoded.Length > 1 && decoded.EndsWith('/')))))
            { throw new DocumentException(fieldPath, "unsafe-request-path"); }
            string next = Uri.UnescapeDataString(decoded);
            if (next == decoded) { return; }
            decoded = next;
        }
        throw new DocumentException(fieldPath, "excessive-path-encoding");
    }

    private static void ValidateHeader(string header, string path)
    {
        if (ForbiddenHeaders.Contains(header) || header.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) ||
            header.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) || header.Equals("Forwarded", StringComparison.OrdinalIgnoreCase))
        { throw new DocumentException(path + "/headers", "routing-header-forbidden"); }
    }
}
