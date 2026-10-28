using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.App.Features.Environments;

public sealed record InspectComponentRow(ComponentObservation Actual, string Name, string DeclaredReadiness)
{
    public Guid ComponentId => Actual.ComponentId;
    public string Status => Actual.Status;
    public string Health => Actual.Health == "notConfigured" ? "Не настроен" : Actual.Health;
    public string RuntimeReady => DeclaredReadiness == "none" ? "Проверка не задана" : Actual.RuntimeReady ? "Подтверждена" : "Не подтверждена";
    public string ApplicationReady => DeclaredReadiness == "none" ? "Проверка не задана" : Actual.ApplicationReady ? "Готов" : "Не подтверждена";
    public string Drift => Actual.Drift == "expected" ? "" : Actual.Drift;
    public string DisplayStatus => Status == "running" ? "Запущен" : Status == "exited" ? "Остановлен" : Status;
    public string Category => Name.Contains("postgres", StringComparison.OrdinalIgnoreCase) || Name.Contains("rabbit", StringComparison.OrdinalIgnoreCase) ? "Infrastructure" : "Backend";
    public string Icon => Category == "Infrastructure" ? "Database" : "Box";
    public string Container => Actual.ContainerId is { Length: > 12 } id ? id[..12] : Actual.ContainerId ?? "—";
    public string Ports => Actual.Ports.Length == 0 ? "—" : string.Join(", ", Actual.Ports.Select(p => p.HostPort));
    public string Details => ComponentId + " · " + Actual.ContainerId + " · " + Actual.Reason + (Actual.ExitCode is null ? "" : " · exit " + Actual.ExitCode + " · OOM " + Actual.OomKilled);
}

public sealed partial class InspectViewModel(IEnvironmentObservationSource source, IInspectionStreamsFactory streams, EnvironmentCommands commands,
    Func<Action, CancellationToken, Task> apply) : ObservableObject, IAsyncDisposable
{
    private CancellationTokenSource? cancellation;
    private Task polling = Task.CompletedTask;
    private IInspectionStreams? session;
    private readonly SemaphoreSlim refresh = new(1, 1);
    private IsolatedEnvironment? environment;
    private EnvironmentObservation? observation;
    private Guid sessionId;
    private string? baselineInputReason = "Входы baseline ещё не проверены.";
    private bool baselineChecked;
    public ObservableCollection<InspectComponentRow> Components { get; } = [];
    [ObservableProperty] private InspectComponentRow? selectedComponent;
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string status = "Runtime не проверен";
    [ObservableProperty] private string progress = "Нет активной операции";
    [ObservableProperty] private string operationId = "";
    [ObservableProperty] private string logs = "";
    [ObservableProperty] private string logStatus = "Логи не получены";
    [ObservableProperty] private string measurements = "Метрики не получены";
    [ObservableProperty] private string aggregate = "Общие метрики не получены";
    [ObservableProperty] private string cpuSummary = "—";
    [ObservableProperty] private string memorySummary = "—";
    public string RunningSummary => $"{Components.Count(c => c.Status == "running")} / {Components.Count}";
    public string HealthSummary => Components.Count == 0 ? "Не проверен" : Components.All(c => c.Actual.Health == "healthy") ? "Healthy" : "Не подтверждён";
    public string PortCount => Components.Sum(c => c.Actual.Ports.Length).ToString(System.Globalization.CultureInfo.CurrentCulture);
    [ObservableProperty] private string endpoints = "Адреса не проверены";
    [ObservableProperty] private string error = "";
    [ObservableProperty] private bool logsPaused;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(CancelCommand), nameof(ClearDataCommand), nameof(RestoreBaselineCommand))]
    private bool isOperationActive;
    private const string GenerationRecovery = "Сохранность поколения данных не подтверждена. Остановите окружение, проверьте ресурсы и рассмотрите восстановление baseline с подтверждением плана потери данных.";
    public string StartDisabledReason => environment is null ? "Окружение не выбрано." : commands.DisabledReason(environment.Id) ?? (observation?.Components.Any(c => c.Reason == "data-generation-invalid") == true ? GenerationRecovery : "");
    public string StopDisabledReason => environment is null ? "Окружение не выбрано." : commands.StopDisabledReason(environment.Id, observation) ?? "";
    public string DataResetDisabledReason => environment is null ? "Окружение не выбрано." : commands.DataResetDisabledReason(environment.Id, observation) ?? "";
    public string RestoreDisabledReason => DataResetDisabledReason.Length > 0 ? DataResetDisabledReason : baselineInputReason ?? "";
    public string QuiescenceStatus => environment is not null && commands.Quiescence(environment.Id, observation) is { ConfirmedQuiescent: true }
        ? "Остановка подтверждена проверкой ресурсов." : environment is null ? "Окружение не выбрано." : commands.Quiescence(environment.Id, observation).Reason!;
    private bool CanStart() => environment is not null && StartDisabledReason.Length == 0;
    private bool CanStop() => environment is not null && commands.StopDisabledReason(environment.Id, observation) is null;
    private bool CanCancel() => IsOperationActive;
    private bool CanReset() => !IsOperationActive && environment is not null && DataResetDisabledReason.Length == 0;
    private bool CanRestore() => CanReset() && RestoreDisabledReason.Length == 0;
    public Func<Task>? Back { get; set; }
    public Func<IsolatedEnvironment, Task>? Preview { get; set; }

    public async Task OpenAsync(IsolatedEnvironment document, CancellationToken token)
    {
        await CloseSessionAsync();
        environment = document; observation = null; sessionId = Guid.NewGuid(); Name = document.Name;
        baselineChecked = false; baselineInputReason = "Входы baseline ещё не проверены.";
        SelectedComponent = null; Components.Clear(); Logs = ""; Endpoints = "Адреса не проверены"; Error = "";
        StartCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged(); ClearDataCommand.NotifyCanExecuteChanged(); RestoreBaselineCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(DataResetDisabledReason)); OnPropertyChanged(nameof(StopDisabledReason)); OnPropertyChanged(nameof(QuiescenceStatus));
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        session = streams.Create(document);
        polling = PollAsync(document, sessionId, cancellation.Token);
    }
    private async Task PollAsync(IsolatedEnvironment document, Guid id, CancellationToken token)
    {
        try
        {
            await RefreshCoreAsync(document, id, token).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) { await RefreshCoreAsync(document, id, token).ConfigureAwait(false); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { await apply(() => { if (id == sessionId) { Error = "Наблюдение недоступно. Повторите проверку."; } }, CancellationToken.None).ConfigureAwait(false); }
    }
    private async Task RefreshCoreAsync(IsolatedEnvironment document, Guid id, CancellationToken token)
    {
        await refresh.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnvironmentObservation next = await source.ReadAsync(document, id, observation, token).ConfigureAwait(false);
            string? inputReason = baselineInputReason;
            if (!baselineChecked) { inputReason = await commands.CheckRestoreInputsAsync(document, token).ConfigureAwait(false); }
            Guid? selected = SelectedComponent?.ComponentId ?? next.Components.FirstOrDefault()?.ComponentId;
            InspectionStreamsSnapshot live = await session!.ReadAsync(next, selected, LogsPaused, token).ConfigureAwait(false);
            await apply(() =>
            {
                if (id != sessionId || token.IsCancellationRequested || next.EnvironmentId != document.Id || next.SessionId != id) { return; }
                observation = next;
                baselineInputReason = inputReason; baselineChecked = true; OnPropertyChanged(nameof(RestoreDisabledReason));
                string freshness = next.Freshness == "current" ? "Актуальное состояние" : next.Freshness == "stale" ? "Данные устарели" : "Состояние неизвестно";
                Status = $"{freshness} · проверено {next.CheckedAt.LocalDateTime:g} · последняя успешная проверка {next.LastSuccessfulAt?.LocalDateTime.ToString("g") ?? "неизвестна"}";
                Components.Clear();
                foreach (var component in next.Components)
                {
                    EnvironmentComponent? configured = document.Components.FirstOrDefault(c => c.Id == component.ComponentId);
                    Components.Add(new(component, configured?.DefinitionSnapshot.Name ?? "Неожиданный ресурс", configured?.DefinitionSnapshot.Readiness.Kind ?? "none"));
                }
                SelectedComponent = Components.FirstOrDefault(c => c.ComponentId == selected);
                Logs = string.Join('\n', live.Lines); LogStatus = $"{live.LogState} · удалено строк {live.Dropped} · усечено {live.Truncated}";
                ComponentObservation? current = SelectedComponent?.Actual;
                Endpoints = current is null || next.Freshness != "current" ? "Адреса не проверены" : string.Join('\n', current.Ports.Select(p =>
                    p.HostIp == "127.0.0.1" ? $"127.0.0.1:{p.HostPort} → {p.ContainerPort}/{p.Protocol}" : $"{p.HostIp}:{p.HostPort} → {p.ContainerPort}/{p.Protocol} (LAN; адрес хоста требуется явно)"));
                Measurements = "Метрики недоступны";
                if (current is not null && live.Measurements.TryGetValue(current.ComponentId, out var measured))
                { Measurements = string.Join(" · ", measured.Select(p => p.Key + ": " + (p.Value.Value?.ToString("G4", System.Globalization.CultureInfo.CurrentCulture) ?? p.Value.MissingReason) + " " + p.Value.Unit + " ≈")); }
                MeasurementAggregate cpu = Measurement.Sum(live.Measurements.Values.Where(m => m.ContainsKey("cpu")).Select(m => m["cpu"]), document.Components.Length);
                MeasurementAggregate memory = Measurement.Sum(live.Measurements.Values.Where(m => m.ContainsKey("memoryUsed")).Select(m => m["memoryUsed"]), document.Components.Length);
                Aggregate = $"CPU ≈ {cpu.Value?.ToString("G4", System.Globalization.CultureInfo.CurrentCulture) ?? "нет данных"}% · покрытие {cpu.Measured}/{cpu.Expected}; память ≈ {memory.Value?.ToString("G4", System.Globalization.CultureInfo.CurrentCulture) ?? "нет данных"} bytes · покрытие {memory.Measured}/{memory.Expected}";
                CpuSummary = cpu.Value is { } cpuValue ? $"≈ {cpuValue:G4}%" : "—";
                MemorySummary = memory.Value is { } memoryValue ? $"≈ {memoryValue / 1048576d:N1} MB" : "—";
                OnPropertyChanged(nameof(RunningSummary)); OnPropertyChanged(nameof(HealthSummary)); OnPropertyChanged(nameof(PortCount));
                var handle = commands.Get(document.Id);
                OperationId = handle?.Current.Id.ToString() ?? "";
                IsOperationActive = handle is { Completion.IsCompleted: false };
                StartCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); ClearDataCommand.NotifyCanExecuteChanged(); RestoreBaselineCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(DataResetDisabledReason)); OnPropertyChanged(nameof(StartDisabledReason)); OnPropertyChanged(nameof(StopDisabledReason)); OnPropertyChanged(nameof(QuiescenceStatus));
                Progress = handle is null ? "Нет активной операции" : StateLabel(handle.Current.State) + " · " + string.Join("; ", handle.Current.Progress.Select(p => p.ComponentId + " " + p.Stage)) + ResultLabel(handle.Current.ResultCode) +
                    (handle.Current.Kind is "clear" or "refresh" ? "\nУдалено: " + string.Join(", ", handle.Current.DeletedResources) + "\nОстатки очистки: " + string.Join(", ", handle.Current.Remnants) +
                    (handle.Current.State is OperationState.PartiallyCompleted or OperationState.NeedsAttention ? "\nЧастичное восстановление данных. Проверьте фактические компоненты. Для их остановки используйте отдельную команду «Остановить». Удалённые данные не восстанавливаются." : "") : "");
                Error = next.Reason ?? (next.Components.Any(c => c.Reason == "data-generation-invalid") ? GenerationRecovery : "");
            }, token).ConfigureAwait(false);
        }
        finally { refresh.Release(); }
    }
    private static string StateLabel(OperationState state) => state switch
    {
        OperationState.Planned => "Проверка перед запуском",
        OperationState.AwaitingConfirmation => "Ожидание подтверждения входов",
        OperationState.Running => "Выполняется",
        OperationState.CancelRequested => "Ожидание безопасной отмены",
        OperationState.Succeeded => "Все обязательные этапы завершены",
        OperationState.Cancelled => "Отменено",
        OperationState.PartiallyCompleted => "Выполнено частично",
        OperationState.NeedsAttention => "Требуется сверка результата",
        OperationState.Failed => "Операция заблокирована или не удалась",
        _ => state.ToString()
    };
    private static string ResultLabel(string? code) => code switch
    {
        null => "",
        "runtime-capability-unsupported" => " · Конфигурация Docker не поддерживается; проверьте требования.",
        "required-input-or-runtime-unavailable" => " · Проверьте локальные пути, секреты, пакеты, порты и доступность Docker.",
        "private-runtime-files-cleanup-failed" => " · Не удалось удалить приватные временные файлы. Проверьте доступ к каталогу данных.",
        "data-generation-invalid" or "data-generation-mismatch" or "persisted-volume-missing" or "volume-incarnation-mismatch" => " · " + GenerationRecovery,
        "stop-not-confirmed" or "stop-state-unknown" => " · Остановка не подтверждена. Обновите состояние Docker; изменение и очистка данных заблокированы.",
        "stop-preflight-unavailable" => " · Не удалось проверить Docker перед остановкой. Обновите состояние.",
        _ => " · " + code
    };
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (environment is null) { return; }
        try { var handle = await commands.StartAsync(environment, CancellationToken.None); OperationId = handle.Current.Id.ToString(); IsOperationActive = true; Progress = "Запуск принят · этапы без известного процента"; }
        catch (Exception error) when (error is not OutOfMemoryException) { Error = "Запуск заблокирован. Проверьте inputs, хранилище и незавершённые операции."; }
    }
    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (environment is null) { return; }
        try { var handle = await commands.StopAsync(environment, CancellationToken.None); OperationId = handle.Current.Id.ToString(); IsOperationActive = true; Progress = "Остановка принята · ожидание завершения контейнеров и проверки"; }
        catch (Exception error) when (error is not OutOfMemoryException) { Error = "Остановка заблокирована. Обновите состояние и проверьте принадлежность контейнеров."; }
    }
    [RelayCommand(CanExecute = nameof(CanCancel))] private Task CancelAsync() => environment is null ? Task.CompletedTask : commands.CancelAsync(environment.Id, CancellationToken.None);
    [RelayCommand(CanExecute = nameof(CanReset))] private Task ClearDataAsync() => ResetAsync(false);
    [RelayCommand(CanExecute = nameof(CanRestore))] private Task RestoreBaselineAsync() => ResetAsync(true);
    private async Task ResetAsync(bool restore)
    {
        if (environment is null) { return; }
        try
        {
            OperationHandle handle = restore ? await commands.RestoreAsync(environment, CancellationToken.None) : await commands.ClearAsync(environment, CancellationToken.None);
            OperationId = handle.Current.Id.ToString(); IsOperationActive = true;
            Progress = "Проверка остановки, принадлежности и входов; затем подтверждение плана потери данных.";
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Error = "Операция с данными заблокирована. Обновите состояние и проверьте входы и незавершённые операции."; }
    }
    [RelayCommand]
    private Task RefreshAsync()
    {
        baselineChecked = false;
        return environment is null || cancellation is null ? Task.CompletedTask : RefreshCoreAsync(environment, sessionId, cancellation.Token);
    }
    [RelayCommand] private Task ReturnAsync() => Back?.Invoke() ?? Task.CompletedTask;
    [RelayCommand] private Task ShowComposeAsync() => environment is null ? Task.CompletedTask : Preview?.Invoke(environment) ?? Task.CompletedTask;
    public async Task CloseSessionAsync()
    {
        if (cancellation is null) { return; }
        sessionId = Guid.NewGuid();
        await cancellation.CancelAsync(); await polling;
        if (session is not null) { await session.DisposeAsync(); session = null; }
        cancellation.Dispose(); cancellation = null;
    }
    public async ValueTask DisposeAsync() { await CloseSessionAsync(); refresh.Dispose(); }
}
