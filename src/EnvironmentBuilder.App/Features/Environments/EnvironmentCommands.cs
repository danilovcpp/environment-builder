using CommunityToolkit.Mvvm.ComponentModel;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.App.Features.Environments;

// Application-owned handles survive route changes. Observation and cancellation remain independent.
public sealed class EnvironmentCommands(StartEnvironmentService start, OperationCoordinator coordinator, PreviewReviewStore reviews, IRuntimeInteractions interactions, StopEnvironmentService stop, EditEnvironmentService? edit = null,
    ClearEnvironmentDataService? clear = null, RefreshEnvironmentDataService? restore = null, IDestructiveInteractions? destructive = null,
    IInputResolver? inputs = null, ISeedPackStore? packs = null, DeleteEnvironmentService? delete = null) : ObservableObject, IEnvironmentEditing
{
    public const string ObservationLabel = "Обновить состояние";
    public const string ClearLabel = "Очистить данные";
    public const string RestoreLabel = "Восстановить baseline (refresh данных)";
    public const string DeleteLabel = "Удалить окружение";
    public const string DeleteExplanation = "Показывает точный план удаления описания и данных. Работающее окружение сначала будет остановлено. Частичное удаление остаётся видимым для безопасного повтора.";
    public const string ObservationExplanation = "Только перечитывает состояние Docker. Данные, baseline и ревизия сохраняются.";
    public const string ClearExplanation = "После подтверждения удаляет все данные и отметки наполнения. Окружение остаётся остановленным; baseline и ревизия сохраняются.";
    public const string RestoreExplanation = "Проверяет baseline до удаления, восстанавливает данные с временным запуском компонентов и подтверждает их остановку. Ошибка оставляет частичное восстановление для Inspect и явного Stop.";
    private readonly Dictionary<Guid, OperationHandle> handles = [];
    public OperationHandle? Get(Guid environment) => coordinator.Active.FirstOrDefault(h => h.Current.EnvironmentId == environment) ?? handles.GetValueOrDefault(environment);
    public Task<QuiescenceResult> CheckAsync(IsolatedEnvironment environment, CancellationToken token) => edit?.CheckAsync(environment, token) ?? Task.FromResult(new QuiescenceResult(false, "Изменение недоступно."));
    public Task<EditEnvironmentResult> SaveAsync(IsolatedEnvironment original, IsolatedEnvironment candidate, Func<OperationPlan, CancellationToken, Task<ConfirmationRecord?>> confirm, CancellationToken token) =>
        edit?.SaveAsync(original, candidate, confirm, token, handle => handles[original.Id] = handle) ?? Task.FromResult(new EditEnvironmentResult(false, null, null, "edit-unavailable"));
    public string? DisabledReason(Guid environment) => coordinator.AdmissionBlockReason(environment);
    public QuiescenceResult Quiescence(Guid environment, EnvironmentObservation? observation) => QuiescenceGuard.Check(observation, coordinator.AdmissionBlockReason(environment));
    public string? DataResetDisabledReason(Guid environment, EnvironmentObservation? observation) => Quiescence(environment, observation) is { ConfirmedQuiescent: true } ? null : Quiescence(environment, observation).Reason;
    public async Task<string?> CheckRestoreInputsAsync(IsolatedEnvironment environment, CancellationToken token)
    {
        if (inputs is null || packs is null) { return "Проверка входов baseline недоступна."; }
        try
        {
            IReadOnlyList<ReadinessIssue> issues = await EnvironmentReadiness.CheckAsync(environment, inputs, packs, token).ConfigureAwait(false);
            return issues.FirstOrDefault(i => i.Code != "image-identity-unknown") is { } issue ? "Baseline недоступен: " + issue.Code + ". Обновите состояние после исправления входов." : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException) { return "Пакеты или способ применения baseline не подтверждены. Проверьте выбранные версии и входы, затем обновите состояние."; }
    }
    public Task<OperationHandle> ClearAsync(IsolatedEnvironment environment, CancellationToken token) => SubmitResetAsync(environment, false, token);
    public Task<DeleteEnvironmentResult> DeleteAsync(IsolatedEnvironment environment, CancellationToken token)
    {
        if (delete is null || destructive is null) { throw new InvalidOperationException("Удаление окружения недоступно."); }
        return delete.DeleteAsync(environment, destructive.ConfirmAsync, token, handle => handles[environment.Id] = handle);
    }
    public Task<OperationHandle> RestoreAsync(IsolatedEnvironment environment, CancellationToken token) => SubmitResetAsync(environment, true, token);
    private Task<OperationHandle> SubmitResetAsync(IsolatedEnvironment environment, bool refresh, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (destructive is null || (refresh ? restore is null : clear is null)) { throw new InvalidOperationException("Операция с данными недоступна."); }
        if (coordinator.AdmissionBlockReason(environment.Id) is { } blocked) { throw new InvalidOperationException(blocked); }
        OperationHandle handle = refresh ? restore!.Refresh(environment, destructive.ConfirmAsync) : clear!.Clear(environment, destructive.ConfirmAsync);
        handles[environment.Id] = handle;
        return Task.FromResult(handle);
    }
    public string? StopDisabledReason(Guid environment, EnvironmentObservation? observation)
    {
        string? blocked = coordinator.AdmissionBlockReason(environment, stopping: true);
        if (blocked is not null) { return blocked; }
        if (observation is null || observation.Freshness != "current") { return "Обновите состояние Docker перед остановкой."; }
        if (observation.Components.Any(c => c.Drift is "unexpected-owned" or "foreign-conflict")) { return "Принадлежность контейнеров не подтверждена. Требуется сверка."; }
        if (observation.Resources.Any(r => r.Ledger.Kind == ResourceKind.Container && !r.Ownership.Verified)) { return "Принадлежность контейнеров не подтверждена. Требуется сверка."; }
        if (observation.Resources.Any(r => r.Actual is { Kind: ResourceKind.Container, State: "unknown" })) { return "Состояние контейнеров неизвестно. Обновите состояние Docker."; }
        return observation.Resources.Any(r => r.Ledger.Kind == ResourceKind.Container && r.Actual is not null && !QuiescenceGuard.IsStopped(r.Actual.State))
            ? null : "Нет работающих контейнеров для остановки.";
    }
    public Task<OperationHandle> StartAsync(IsolatedEnvironment environment, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Get(environment.Id) is { Completion.IsCompleted: false } active) { return Task.FromResult(active); }
        OperationHandle handle = start.Start(environment, reviews.Find(environment.Id), interactions.ConfirmInputsAsync);
        handles[environment.Id] = handle;
        return Task.FromResult(handle);
    }
    public Task CancelAsync(Guid environment, CancellationToken token) => coordinator.RequestCancelAsync(environment, token);
    public Task<OperationHandle> StopAsync(IsolatedEnvironment environment, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Get(environment.Id) is { Completion.IsCompleted: false } active) { return Task.FromResult(active); }
        OperationHandle handle = stop.Stop(environment);
        handles[environment.Id] = handle;
        return Task.FromResult(handle);
    }
}
