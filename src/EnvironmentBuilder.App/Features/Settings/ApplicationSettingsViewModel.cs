using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.App.Shell;

namespace EnvironmentBuilder.App.Features.Settings;

public sealed partial class ApplicationSettingsViewModel(ThemeService themes) : ObservableObject
{
    private PreferencesStore? store;
    private bool loading;
    [ObservableProperty] private string theme = "system";
    [ObservableProperty] private string startPage = "environments";
    [ObservableProperty] private bool showDescriptions = true;
    [ObservableProperty] private string dataDirectory = "Хранилище ещё не открыто";
    [ObservableProperty] private string status = "Изменения применяются и сохраняются автоматически.";
    [ObservableProperty] private string error = "";
    public static FilterOption[] Themes { get; } = [new("system", "Как в Windows"), new("light", "Светлая"), new("dark", "Тёмная")];
    public static FilterOption[] StartPages { get; } = [new("environments", "Окружения"), new("seedpacks", "Тестовые данные"), new("services", "Сервисы"), new("diagnostics", "Диагностика")];
    public string Version => typeof(ApplicationSettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public void Initialize(string directory)
    {
        DataDirectory = directory;
        store = new PreferencesStore(Path.Combine(directory, "preferences.json"));
        AppPreferences preferences = new();
        try { preferences = store.Read(); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException)
        { Error = "Не удалось прочитать настройки. Используются значения по умолчанию; их можно сохранить повторно."; }
        loading = true;
        try { Theme = preferences.Theme; StartPage = preferences.StartPage; ShowDescriptions = preferences.ShowDescriptions; }
        finally { loading = false; }
        Apply();
    }

    partial void OnThemeChanged(string value) => Save();
    partial void OnStartPageChanged(string value) => Save();
    partial void OnShowDescriptionsChanged(bool value) => Save();

    private void Apply()
    {
        themes.SetMode(Theme);
        Application.Current.Resources["Preference.ShowDescriptions"] = ShowDescriptions;
    }

    [RelayCommand]
    private void Save()
    {
        if (loading) { return; }
        Apply();
        if (store is null) { return; }
        try
        {
            store.Save(new AppPreferences(Theme, StartPage, ShowDescriptions));
            Error = ""; Status = "Настройки сохранены. Изменение начальной страницы действует при следующем запуске.";
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        { Error = "Изменения применены, но не сохранены. Проверьте доступ к хранилищу и повторите сохранение."; }
    }
}
