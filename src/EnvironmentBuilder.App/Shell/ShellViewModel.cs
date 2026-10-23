using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.Core.Abstractions;

namespace EnvironmentBuilder.App.Shell;

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly NavigationService navigation;
    private readonly ThemeService themes;
    public ShellViewModel(NavigationService navigation, ThemeService themes)
    {
        this.navigation = navigation;
        this.themes = themes;
        navigation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(NavigationService.Current)) { return; }
            if (navigation.Current.Route == "editor") { Title = "Описание окружения"; Description = "Состав, настройки и начальные данные без запуска"; }
            else if (navigation.Current.Route == "preview") { Title = "Compose preview"; Description = "Будущая конфигурация с замаскированными секретами"; }
            else if (navigation.Current.Route == "environments") { Title = "Окружения"; Description = "Сохранённые описания локальных окружений"; }
        };
    }
    [ObservableProperty] private AuthoringSession? authoring;
    [ObservableProperty] private string title = "Окружения";
    [ObservableProperty] private string description = "Сохранённые описания локальных окружений";
    [ObservableProperty] private string status = "Открытие хранилища…";
    [ObservableProperty] private string dataDirectory = "";
    [ObservableProperty] private string storageError = "";
    [ObservableProperty] private bool isLoading = true;
    [ObservableProperty] private bool showEnvironments = true;
    [ObservableProperty] private bool showSettings;
    [ObservableProperty] private bool showDiagnostics;
    [ObservableProperty] private bool showSeedPacks;
    [ObservableProperty] private string recoveryStatus = "Состояние runtime не проверено";

    public async Task LoadAsync(UiDispatcher dispatcher, string directory, int unresolved, CancellationToken cancellationToken)
    {
        if (Authoring is not null) { await Authoring.List.LoadAsync(cancellationToken); }
        await dispatcher.ApplyAsync(() =>
        {
            DataDirectory = directory;
            RecoveryStatus = unresolved == 0 ? "Состояние Docker не проверено" : $"Незавершённых операций: {unresolved}. Требуется сверка.";
            IsLoading = false;
            Status = "Хранилище доступно · Docker не проверен";
        }, cancellationToken).ConfigureAwait(false);
    }

    public void ReportStorageError(string code)
    {
        IsLoading = false;
        StorageError = code;
        Status = "Хранилище недоступно";
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
            "seedpacks" => ("Тестовые данные", "Библиотека неизменяемых пакетов начальных данных"),
            "settings" => ("Настройки", "Локальное хранилище и оформление"),
            "diagnostics" => ("Диагностика", "Состояние хранилища и незавершённые операции"),
            _ => ("Окружения", "Сохранённые описания локальных окружений")
        };
    }
    [RelayCommand] private void ToggleTheme() => themes.Apply(!themes.IsDark);
}
