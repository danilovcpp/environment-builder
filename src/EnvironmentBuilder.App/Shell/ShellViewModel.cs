using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvironmentBuilder.App.Features.Diagnostics;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.App.Features.SeedPacks;
using EnvironmentBuilder.App.Features.Settings;
using EnvironmentBuilder.Core.Abstractions;

namespace EnvironmentBuilder.App.Shell;

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly NavigationService navigation;
    private readonly ThemeService themes;
    private INotifyPropertyChanged? contextModel;
    public ShellViewModel(NavigationService navigation, ThemeService themes)
    {
        this.navigation = navigation;
        this.themes = themes;
        settings = new SettingsViewModel(themes);
        SeedPacks.PropertyChanged += ContextChanged;
        Settings.PropertyChanged += ContextChanged;
        navigation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(NavigationService.Current)) { return; }
            if (navigation.Current.Route == "editor") { Title = "Создание среды"; Description = "Настройте состав сервисов, инфраструктуры и параметры окружения."; }
            else if (navigation.Current.Route == "inspect") { Title = "Просмотр среды"; Description = "Состояние запущенных сервисов, журналы и диагностика окружения."; }
            else if (navigation.Current.Route == "preview") { Title = "Compose preview"; Description = "Будущая конфигурация с замаскированными секретами"; }
            else if (navigation.Current.Route == "environments") { Title = "Окружения"; Description = "Сохранённые описания локальных окружений"; }
            NotifyContext();
        };
    }
    [ObservableProperty] private AuthoringSession? authoring;
    [ObservableProperty] private SeedPackLibraryViewModel seedPacks = new();
    [ObservableProperty] private SettingsViewModel settings;
    public DiagnosticsViewModel Diagnostics { get; } = new();
    [ObservableProperty] private string title = "Окружения";
    [ObservableProperty] private string description = "Все созданные окружения. Запуск, остановка и управление изолированными средами на основе Docker.";
    [ObservableProperty] private string status = "Открытие хранилища…";
    [ObservableProperty] private string dataDirectory = "";
    [ObservableProperty] private string storageError = "";
    [ObservableProperty] private bool isLoading = true;
    [ObservableProperty] private bool showEnvironments = true;
    [ObservableProperty] private bool showSettings;
    [ObservableProperty] private bool showDiagnostics;
    [ObservableProperty] private bool showSeedPacks;
    [ObservableProperty] private string recoveryStatus = "Состояние runtime не проверено";

    public string PageIcon => navigation.Current.Route switch { "seedpacks" => "Database", "settings" => "Settings", "diagnostics" => "Activity", "preview" => "Code", "inspect" => "Activity", _ => "Box" };
    public string ContextTitle => navigation.Current.Route switch { "seedpacks" => "Сводка выбранного пака", "settings" => "Параметры компонента", "diagnostics" => "Состояние приложения", _ => "Сводка окружения" };
    public object? ContextDetail => navigation.Current.Route switch { "seedpacks" => SeedPacks, "settings" => Settings, "editor" => Authoring?.Current, _ => null };
    public bool HasContext => ShowSeedPacks || ShowDiagnostics || ShowEnvironments && Authoring?.Current is EnvironmentEditorViewModel or ComposePreviewViewModel;
    public string ContextSupportingText => navigation.Current.Route switch
    {
        "seedpacks" => "Текстовое preview\n\n" + SeedPacks.WorkflowReason,
        "settings" => "Изменение настроек\n\n" + Settings.WorkflowReason,
        "diagnostics" => Diagnostics.WorkflowReason,
        _ => Authoring?.Current is EnvironmentEditorViewModel editor ? $"Проверка конфигурации\n\nОшибок структуры: {editor.Errors.Count}\nЗамечаний доступности: {editor.ReadinessIssues.Count}\n\n{editor.SaveDisabledReason}"
            : "Наблюдение перечитывает состояние. Очистка удаляет данные. Восстановление baseline включает временный запуск и проверенную остановку; требуется подтверждение точного плана."
    };
    public string ContextSummary => navigation.Current.Route switch
    {
        "seedpacks" => SeedPacks.Summary,
        "settings" => Settings.Summary,
        "diagnostics" => Status + "\n\n" + RecoveryStatus,
        _ => Authoring?.Current switch
        {
            EnvironmentEditorViewModel editor => $"{editor.Name}\n{editor.Title}\n\n{editor.ConfigurationSummary}\n\nID: {editor.Id}\nРевизия: {editor.Revision}\n\n{editor.SaveDisabledReason}\n\nСохранение описания не запускает окружение.",
            InspectViewModel inspect => $"{inspect.Name}\n\n{inspect.Status}\n\n{inspect.Aggregate}\n\n{inspect.QuiescenceStatus}\n\n{inspect.Progress}",
            ComposePreviewViewModel preview => preview.Details,
            _ => Authoring?.List.SelectedItem is { } row ? $"{row.Name}\n\n{row.Description}\n\n{row.Components}\n\n{row.State}\n{row.Measurement}\n\nID: {row.Id}\nРевизия: {row.Revision}\n{row.SavedAt}" : "Выберите окружение для просмотра состава и состояния.\n\nОписания и Compose preview доступны без Docker.\n\n" + RecoveryStatus
        }
    };
    partial void OnAuthoringChanged(AuthoringSession? oldValue, AuthoringSession? newValue)
    {
        if (oldValue is not null) { oldValue.PropertyChanged -= AuthoringChanged; oldValue.List.PropertyChanged -= ContextChanged; }
        if (newValue is not null) { newValue.PropertyChanged += AuthoringChanged; newValue.List.PropertyChanged += ContextChanged; }
        ObserveContext();
    }
    private void AuthoringChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(AuthoringSession.Current)) { ObserveContext(); } }
    private void ObserveContext()
    {
        if (contextModel is not null) { contextModel.PropertyChanged -= ContextChanged; }
        contextModel = Authoring?.Current as INotifyPropertyChanged;
        if (contextModel == Authoring?.List) { contextModel = null; }
        if (contextModel is not null) { contextModel.PropertyChanged += ContextChanged; }
        NotifyContext();
    }
    private void ContextChanged(object? sender, PropertyChangedEventArgs e) => NotifyContext();
    private void NotifyContext() { OnPropertyChanged(nameof(ContextSummary)); OnPropertyChanged(nameof(ContextSupportingText)); OnPropertyChanged(nameof(ContextTitle)); OnPropertyChanged(nameof(ContextDetail)); OnPropertyChanged(nameof(PageIcon)); OnPropertyChanged(nameof(HasContext)); }
    partial void OnSeedPacksChanged(SeedPackLibraryViewModel? oldValue, SeedPackLibraryViewModel newValue) { if (oldValue is not null) { oldValue.PropertyChanged -= ContextChanged; } newValue.PropertyChanged += ContextChanged; NotifyContext(); }
    partial void OnSettingsChanged(SettingsViewModel? oldValue, SettingsViewModel newValue) { if (oldValue is not null) { oldValue.PropertyChanged -= ContextChanged; } newValue.PropertyChanged += ContextChanged; NotifyContext(); }
    partial void OnRecoveryStatusChanged(string value) { Diagnostics.RecoveryStatus = value; NotifyContext(); }

    public async Task LoadAsync(UiDispatcher dispatcher, string directory, int unresolved, CancellationToken cancellationToken)
    {
        if (Authoring is not null) { await Authoring.List.LoadAsync(cancellationToken); }
        await SeedPacks.LoadAsync(cancellationToken);
        await Settings.LoadAsync(cancellationToken);
        await dispatcher.ApplyAsync(() =>
        {
            DataDirectory = directory;
            Settings.DataDirectory = directory;
            RecoveryStatus = unresolved == 0 ? "Состояние Docker не проверено" : $"Незавершённых операций: {unresolved}. Требуется сверка.";
            IsLoading = false;
            Status = "Хранилище доступно · Docker не проверен";
            Diagnostics.StorageStatus = "Хранилище доступно. Одновременная запись из другого экземпляра приложения запрещена.";
            NotifyContext();
        }, cancellationToken).ConfigureAwait(false);
    }

    public void ReportStorageError(string code)
    {
        IsLoading = false;
        StorageError = code;
        Status = "Хранилище недоступно";
        Diagnostics.StorageStatus = Status; Diagnostics.Error = code; NotifyContext();
    }

    [RelayCommand]
    private async Task NavigateAsync(string route)
    {
        if (Authoring is not null && !await Authoring.LeaveAsync(CancellationToken.None)) { return; }
        navigation.Navigate(new NavigationAddress(route));
        ShowEnvironments = route == "environments";
        ShowSettings = route == "settings";
        ShowDiagnostics = route == "diagnostics";
        ShowSeedPacks = route == "seedpacks";
        (Title, Description) = route switch
        {
            "seedpacks" => ("Тестовые данные", "Управление themed data-seed паками для демонстрационных и тестовых окружений."),
            "settings" => ("Настройки", "Источники образов, build-настройки и параметры поддерживаемых сервисов."),
            "diagnostics" => ("Диагностика", "Состояние хранилища и незавершённые операции"),
            _ => ("Окружения", "Сохранённые описания локальных окружений")
        };
        NotifyContext();
    }
    [RelayCommand] private void ToggleTheme() => themes.Apply(!themes.IsDark);
}
