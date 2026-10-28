using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.Core.SeedPacks;

public enum InitializationDecision { AcceptVerified, Repeat, Refresh }
public interface IInitializationResolutionStore
{
    Task<IReadOnlyList<DataApplicationRecord>> ListUnresolvedAsync(Guid environment, CancellationToken token);
    Task<IReadOnlyList<DataApplicationRecord>> ReadRecordsAsync(Guid environment, CancellationToken token);
    Task<bool> IsReplayAuthorizedAsync(DataApplicationKey key, int attempt, CancellationToken token);
    Task ResolveAsync(IsolatedEnvironment environment, DataApplicationRecord expected, InitializationDecision decision, Operation resolution, CancellationToken token);
    Task ValidateRefreshAsync(IsolatedEnvironment environment, Guid operation, CancellationToken token);
    Task FinishRefreshAsync(Operation operation, CancellationToken token);
}

public sealed class ResolveUnknownInitializationService(IInitializationResolutionStore store, IEnvironmentObservationSource observations,
    OperationCoordinator coordinator, IClock clock)
{
    public Task<IReadOnlyList<DataApplicationRecord>> ListAsync(Guid environment, CancellationToken token) => store.ListUnresolvedAsync(environment, token);
    public Task<IReadOnlyList<DataApplicationRecord>> ReadRecordsAsync(Guid environment, CancellationToken token) => store.ReadRecordsAsync(environment, token);

    public OperationHandle Resolve(IsolatedEnvironment environment, DataApplicationRecord expected, InitializationDecision decision, bool independentlyVerified)
    {
        if (decision == InitializationDecision.Refresh) { throw new InvalidOperationException("Stop, then request a new confirmed baseline refresh."); }
        if (decision == InitializationDecision.AcceptVerified && !independentlyVerified) { throw new InvalidOperationException("Independent verification of the service result must be explicitly acknowledged."); }
        if (expected.Key.EnvironmentId != environment.Id) { throw new InvalidOperationException("Initialization belongs to another environment."); }
        var plan = new OperationPlan(Guid.NewGuid(), environment.Id, environment.Revision, CanonicalJson.Hash(environment), expected.Key.DataGenerationId,
            CanonicalJson.Hash(environment.Baseline), [], [],
            ["read-only-verification", "record-explicit-initialization-decision"],
            decision == InitializationDecision.Repeat ? "Повтор может создать дубликаты. Прежний idempotency key сохранится. Отправка произойдёт только при отдельном Start." : "Пользователь независимо проверил результат сервиса. Приложение проверяет актуальность ресурсов и поколения, но не выводит бизнес-успех из состояния контейнера.")
        { Packs = [.. environment.Baseline.Applications.Where(a => a.SeedPack is not null).Select(a => a.SeedPack!)] };
        var operation = new Operation(Guid.NewGuid(), environment.Id, "resolve-initialization", OperationState.Planned, clock.UtcNow, environment.Revision, plan.ConfigurationHash, plan);
        return coordinator.SubmitReconciliation(operation, async execution =>
        {
            EnvironmentObservation actual = await observations.ReadAsync(environment, Guid.NewGuid(), null, execution.CancellationToken).ConfigureAwait(false);
            if (actual.EnvironmentId != environment.Id || actual.Freshness != "current" || actual.Resources.Any(r => !r.Ownership.Verified || r.Actual is null) ||
                environment.Components.Any(c => !actual.Components.Any(a => a.ComponentId == c.Id && a.Status != "unknown")) ||
                actual.Components.Any(c => c.Drift != "expected" || c.Reason == "data-generation-invalid"))
            { throw new PreflightBlockedException("initialization-verification-unavailable"); }
            await execution.CompleteAtomicallyAsync((resolved, token) => store.ResolveAsync(environment, expected, decision, resolved, token), clock.UtcNow).ConfigureAwait(false);
        });
    }
}
