using System.Windows;
using EnvironmentBuilder.App.Composition;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.App.Shell;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;

namespace EnvironmentBuilder.App;

public partial class App : Application
{
    private AppServices? services;
    private Task? startup;
    private ThemeService? themes;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Do not subscribe to SystemThemeWatcher: the repository owns its palette.
        ApplicationThemeManager.Apply(ApplicationTheme.Light, updateAccent: false);
        base.OnStartup(e);
        var navigation = new NavigationService();
        themes = new ThemeService();
        themes.WatchSystemTheme();
        var shell = new ShellViewModel(navigation, themes);
        var window = new MainWindow(shell);
        MainWindow = window;
        window.RequestClose = RequestShutdownAsync;
        window.Show();
        startup = InitializeAsync(shell, navigation, themes);
        await startup;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        themes?.Dispose();
        base.OnExit(e);
    }

    private async Task InitializeAsync(ShellViewModel shell, NavigationService navigation, ThemeService themes)
    {
        try
        {
            services = await AppServices.CreateAsync(shell, navigation, themes);
            shell.Authoring = services.Provider.GetRequiredService<AuthoringSession>();
            shell.SeedPacks = services.Provider.GetRequiredService<Features.SeedPacks.SeedPackLibraryViewModel>();
            shell.Settings = services.Provider.GetRequiredService<Features.Settings.SettingsViewModel>();
            IReadOnlyList<Operation> unresolved = await services.Provider.GetRequiredService<IOperationStore>().ReadUnresolvedAsync(CancellationToken.None);
            await shell.LoadAsync(services.Provider.GetRequiredService<UiDispatcher>(),
                services.Provider.GetRequiredService<AppDataDirectory>().Root, unresolved.Count, CancellationToken.None);
            if (services.RuntimeFileWarnings.Count > 0) { shell.RecoveryStatus += " · private runtime files: " + string.Join(", ", services.RuntimeFileWarnings.Distinct()); }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            shell.ReportStorageError(error switch
            {
                NotSupportedException => "Версия хранилища не поддерживается. Данные сохранены.",
                UnauthorizedAccessException => "Нет доступа к приватному хранилищу.",
                _ => "Хранилище занято, повреждено или недоступно. Проверьте каталог данных."
            });
        }
    }

    private async Task<bool> RequestShutdownAsync()
    {
        try
        {
            if (startup is not null) { await startup; }
            if (services is not null && MainWindow?.DataContext is ShellViewModel active && active.Authoring is not null &&
                !await services.Provider.GetRequiredService<ShutdownCoordinator>().RequestAsync(active.Authoring.CanLeaveAsync, CancellationToken.None))
            { return false; }
            if (services is not null) { await services.DisposeAsync(); }
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (MainWindow?.DataContext is ShellViewModel shell) { shell.ReportStorageError("Не удалось безопасно завершить запись. Окно остаётся открытым."); }
            return false;
        }
    }
}
