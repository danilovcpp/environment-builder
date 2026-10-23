using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvironmentBuilder.Core.Abstractions;

namespace EnvironmentBuilder.App.Shell;

public sealed record ShellEnvironmentRow(Guid Id, string Name, long Revision, string State);

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly NavigationService navigation;
    private readonly ThemeService themes;
    public ShellViewModel(NavigationService navigation, ThemeService themes)
    {
        this.navigation = navigation;
        this.themes = themes;
    }
    public ObservableCollection<ShellEnvironmentRow> Environments { get; } = [];
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
    [ObservableProperty] private string emptyMessage = "";

    public async Task LoadAsync(IEnvironmentStore store, UiDispatcher dispatcher, string directory, int unresolved, CancellationToken cancellationToken)
    {
        IReadOnlyList<EnvironmentListItem> items = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        await dispatcher.ApplyAsync(() =>
        {
            Environments.Clear();
            foreach (EnvironmentListItem item in items)
            { Environments.Add(new ShellEnvironmentRow(item.Id, item.Name, item.Revision, item.ErrorCode is not null ? "Ошибка документа: " + item.ErrorCode : item.HasUnresolvedOperation ? "Требует сверки операции" : "Состояние runtime неизвестно")); }
            DataDirectory = directory;
            RecoveryStatus = unresolved == 0 ? "Состояние Docker не проверено" : $"Незавершённых операций: {unresolved}. Требуется сверка.";
            IsLoading = false;
            EmptyMessage = items.Count == 0 ? "Окружения ещё не созданы" : "";
            Status = "Хранилище доступно · Docker не проверен";
        }, cancellationToken).ConfigureAwait(false);
    }

    public void ReportStorageError(string code)
    {
        IsLoading = false;
        StorageError = code;
        Status = "Хранилище недоступно";
        EmptyMessage = "";
    }

    [RelayCommand]
    private void Navigate(string route)
    {
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
