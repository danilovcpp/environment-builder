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
    public string Details => ComponentId + " · " + Actual.ContainerId + " · " + Actual.Reason;
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
    [ObservableProperty] private string endpoints = "Адреса не проверены";
    [ObservableProperty] private string error = "";
    [ObservableProperty] private bool logsPaused;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand))]
    private bool isOperationActive;
    public string StartDisabledReason => environment is null ? "Окружение не выбрано." : commands.DisabledReason(environment.Id) ?? "";
    private bool CanStart() => environment is not null && commands.DisabledReason(environment.Id) is null;
    private bool CanCancel() => IsOperationActive;
    public Func<Task>? Back { get; set; }
    public Func<IsolatedEnvironment, Task>? Preview { get; set; }

    public async Task OpenAsync(IsolatedEnvironment document, CancellationToken token)
    {
        await CloseSessionAsync();
        environment = document; observation = null; sessionId = Guid.NewGuid(); Name = document.Name;
        SelectedComponent = null; Components.Clear(); Logs = ""; Endpoints = "Адреса не проверены"; Error = "";
        StartCommand.NotifyCanExecuteChanged();
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
            Guid? selected = SelectedComponent?.ComponentId ?? next.Components.FirstOrDefault()?.ComponentId;
            InspectionStreamsSnapshot live = await session!.ReadAsync(next, selected, LogsPaused, token).ConfigureAwait(false);
            await apply(() =>
            {
                if (id != sessionId || token.IsCancellationRequested || next.EnvironmentId != document.Id || next.SessionId != id) { return; }
                observation = next;
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
                var handle = commands.Get(document.Id);
                OperationId = handle?.Current.Id.ToString() ?? "";
                IsOperationActive = handle is { Completion.IsCompleted: false };
                StartCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(StartDisabledReason));
                Progress = handle is null ? "Нет активной операции" : StateLabel(handle.Current.State) + " · " + string.Join("; ", handle.Current.Progress.Select(p => p.ComponentId + " " + p.Stage)) + ResultLabel(handle.Current.ResultCode);
                Error = next.Reason ?? "";
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
        OperationState.NeedsAttention => "Требуется сверка результата",
        OperationState.Failed => "Запуск заблокирован или не удался",
        _ => state.ToString()
    };
    private static string ResultLabel(string? code) => code switch
    {
        null => "",
        "windows-24h2-x64-required" => " · Требуется Windows 11 x64 24H2 или новее (build 26100+).",
        "runtime-capability-unsupported" => " · Конфигурация Docker не поддерживается; проверьте требования.",
        "required-input-or-runtime-unavailable" => " · Проверьте локальные пути, секреты, пакеты, порты и доступность Docker.",
        "private-runtime-files-cleanup-failed" => " · Не удалось удалить приватные временные файлы. Проверьте доступ к каталогу данных.",
        _ => " · " + code
    };
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (environment is null) { return; }
        try { var handle = await commands.StartAsync(environment, CancellationToken.None); OperationId = handle.Current.Id.ToString(); IsOperationActive = true; Progress = "Запуск принят · этапы без известного процента"; }
        catch (Exception error) when (error is not OutOfMemoryException) { Error = "Запуск заблокирован. Проверьте inputs, хранилище и незавершённые операции."; }
    }
    [RelayCommand(CanExecute = nameof(CanCancel))] private Task CancelAsync() => environment is null ? Task.CompletedTask : commands.CancelAsync(environment.Id, CancellationToken.None);
    [RelayCommand] private Task RefreshAsync() => environment is null || cancellation is null ? Task.CompletedTask : RefreshCoreAsync(environment, sessionId, cancellation.Token);
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
