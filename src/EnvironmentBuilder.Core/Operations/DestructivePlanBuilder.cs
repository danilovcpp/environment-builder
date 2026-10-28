using System.Collections.Immutable;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.Operations;

public static class DestructivePlanBuilder
{
    public static OperationPlan BuildDeletion(IsolatedEnvironment environment, OperationPlan scope, EnvironmentObservation actual)
    {
        RequireDeletionSafe(actual);
        OperationPlan plan = Build(environment, environment, scope, AsStopped(actual));
        return plan with
        {
            Candidate = null,
            NextDataGenerationId = null,
            Inputs = [],
            ExpectedResources = actual.Resources.Where(r => r.Actual is not null).Select(r => r.Actual! with
            { Consumers = r.Actual!.Consumers.Order(StringComparer.Ordinal).ToImmutableArray() }).OrderBy(r => r.Kind).ThenBy(r => r.Identity, StringComparer.Ordinal).ToImmutableArray(),
            Stages = ["stop-before-removal", "verify-quiescence", "invalidate-generation", "remove-containers", "remove-volumes", "remove-network", "verify-absence", "close-description"],
            Consequences = "Описание окружения, все перечисленные ресурсы, данные и отметки наполнения будут удалены без возможности восстановления. Работающие контейнеры сначала будут остановлены с timeout 30 секунд; после timeout Docker может принудительно завершить их. При сбое описание и остатки останутся видимыми для нового подтверждённого плана удаления."
        };
    }

    public static void RequireDeletionSafe(EnvironmentObservation actual)
    {
        RequireQuiescent(AsStopped(actual));
    }

    public static void RequireDeletionCurrent(OperationPlan plan, EnvironmentObservation actual)
    {
        RequireDeletionSafe(actual);
        RequireCurrent(plan, AsStopped(actual));
        var current = actual.Resources.Where(r => r.Actual is not null).Select(r => r.Actual! with
        { Consumers = r.Actual!.Consumers.Order(StringComparer.Ordinal).ToImmutableArray() }).OrderBy(r => r.Kind).ThenBy(r => r.Identity, StringComparer.Ordinal);
        if (CanonicalJson.Hash(current) != CanonicalJson.Hash(plan.ExpectedResources)) { throw new PreflightBlockedException("stale-plan"); }
    }

    // Deletion alone permits known running states during planning; the real observation remains authoritative.
    // Cleanup still requires verified Stop and compares all other ownership and consumer evidence unchanged.
    private static EnvironmentObservation AsStopped(EnvironmentObservation actual) => actual with
    {
        Resources = actual.Resources.Select(r => r.Actual is { Kind: ResourceKind.Container, State: "running" or "restarting" or "paused" } container
            ? r with { Actual = container with { State = "exited" } } : r).ToImmutableArray(),
        Components = actual.Components.Select(c => c.Status is "running" or "restarting" or "paused" ? c with { Status = "exited" } : c).ToImmutableArray()
    };
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
            if (plan.Stages.Contains("stop-before-removal") && allowed.Kind == ResourceKind.Container &&
                allowed.State is "running" or "restarting" or "paused" && QuiescenceGuard.IsStopped(current.State))
            { allowed = allowed with { State = current.State }; }
            if (CanonicalJson.Hash(current) != CanonicalJson.Hash(allowed)) { throw new PreflightBlockedException("stale-plan"); }
        }
    }
}
