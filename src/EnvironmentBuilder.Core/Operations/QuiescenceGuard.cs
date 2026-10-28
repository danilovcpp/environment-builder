using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.Operations;

public sealed record QuiescenceResult(bool ConfirmedQuiescent, string? Reason);

public static class QuiescenceGuard
{
    // A display status or a cached success never grants permission for destructive work.
    // Callers must obtain a fresh observation again inside the coordinator's environment lock.
    public static QuiescenceResult Check(EnvironmentObservation? observation, string? conflictReason = null)
    {
        if (conflictReason is not null) { return Block(conflictReason); }
        if (observation is null || observation.Freshness != "current") { return Block("Состояние Docker не подтверждено. Обновите состояние."); }
        if (observation.Reason is not null) { return Block("Проверка ресурсов не завершена: " + observation.Reason); }
        foreach (ResourceObservation resource in observation.Resources)
        {
            if (resource.Ledger.Unresolved || !resource.Ownership.Verified) { return Block("Принадлежность ресурса не подтверждена. Требуется сверка."); }
            if (resource.Actual is { Kind: ResourceKind.Container } actual && !IsStopped(actual.State))
            { return Block("Контейнер не подтверждён как остановленный: " + actual.State); }
        }
        foreach (ComponentObservation component in observation.Components)
        {
            if (component.Drift is not ("expected" or "missing")) { return Block("Обнаружен конфликт состава ресурсов. Требуется сверка."); }
            if (component.Status == "missing" && component.ContainerId is null) { continue; }
            if (!IsStopped(component.Status)) { return Block("Компонент не подтверждён как остановленный: " + component.Status); }
        }
        return new(true, null);
        static QuiescenceResult Block(string reason) => new(false, reason);
    }

    public static bool IsStopped(string state) => state is "created" or "exited";
}
