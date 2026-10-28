using System.Collections.Immutable;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.Operations;

public static class DestructivePlanBuilder
{
    public static OperationPlan Build(IsolatedEnvironment original, IsolatedEnvironment candidate, OperationPlan scope, EnvironmentObservation actual)
    {
        RequireQuiescent(actual);
        if (scope.EnvironmentId != original.Id || scope.Revision != original.Revision || scope.ConfigurationHash != CanonicalJson.Hash(original) ||
            scope.DataGenerationId == Guid.Empty || string.IsNullOrWhiteSpace(scope.EngineId) || string.IsNullOrWhiteSpace(scope.Endpoint) ||
            !scope.Resources.Select(r => r.ResourceId).ToHashSet().SetEquals(actual.Resources.Select(r => r.Ledger.ResourceId)) ||
            scope.Resources.Any(r => r.EnvironmentId != original.Id || r.Unresolved || r.ActualDockerId is null))
        { throw new PreflightBlockedException("cleanup-scope-unverified"); }
        return scope with
        {
            EnvironmentName = original.Name,
            Candidate = candidate,
            NextDataGenerationId = Guid.NewGuid(),
            ObservedAt = actual.CheckedAt,
            Resources = scope.Resources.OrderBy(r => r.Kind).ThenBy(r => r.ResourceId).ToImmutableArray(),
            ExpectedResources = actual.Resources.Where(r => r.Actual is not null).Select(r => r.Actual! with { Consumers = r.Actual!.Consumers.Order(StringComparer.Ordinal).ToImmutableArray() }).OrderBy(r => r.Kind).ThenBy(r => r.Identity, StringComparer.Ordinal).ToImmutableArray(),
            Inputs = candidate.Components.Select(c => new InputFingerprint("component://" + c.Id, CanonicalJson.Hash(c), null)).ToImmutableArray(),
            Stages = ["verify-quiescence", "invalidate-generation", "remove-containers", "remove-volumes", "remove-network", "verify-absence", "publish-revision"],
            Consequences = "Данные прежнего окружения и результаты инициализации будут удалены. Восстановление удалённого не предусмотрено. Новая ревизия публикуется только после полной проверенной очистки. Окружение останется остановленным и неинициализированным. Временные запуски не требуются."
        };
    }

    public static void RequireQuiescent(EnvironmentObservation actual)
    {
        QuiescenceResult guard = QuiescenceGuard.Check(actual);
        if (!guard.ConfirmedQuiescent) { throw new PreflightBlockedException("cleanup-not-quiescent"); }
    }

    public static void RequireCurrent(OperationPlan plan, EnvironmentObservation actual, IEnumerable<string>? removed = null)
    {
        RequireQuiescent(actual);
        if (actual.EnvironmentId != plan.EnvironmentId || actual.Resources.Length != plan.Resources.Length) { throw new PreflightBlockedException("stale-plan"); }
        var absent = actual.Resources.Where(r => r.Actual is null).Select(r => r.Ledger.ActualDockerId!).ToHashSet(StringComparer.Ordinal);
        var completed = (removed ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (ResourceObservation resource in actual.Resources)
        {
            OwnedResource? expected = plan.Resources.SingleOrDefault(r => r.ResourceId == resource.Ledger.ResourceId);
            if (expected is null || CanonicalJson.Hash(expected) != CanonicalJson.Hash(resource.Ledger)) { throw new PreflightBlockedException("stale-plan"); }
            if (resource.Actual is null) { continue; }
            if (completed.Contains(resource.Actual.Identity)) { throw new PreflightBlockedException("removed-resource-reappeared"); }
            ResourceEvidence? prior = plan.ExpectedResources?.SingleOrDefault(r => r.Kind == resource.Actual.Kind && r.Identity == resource.Actual.Identity);
            if (prior is null) { throw new PreflightBlockedException("stale-plan"); }
            var current = resource.Actual with { Consumers = resource.Actual.Consumers.Order(StringComparer.Ordinal).ToImmutableArray() };
            var allowed = prior with { Consumers = prior.Consumers.Where(c => !absent.Contains(c)).Order(StringComparer.Ordinal).ToImmutableArray() };
            if (CanonicalJson.Hash(current) != CanonicalJson.Hash(allowed)) { throw new PreflightBlockedException("stale-plan"); }
        }
    }
}
