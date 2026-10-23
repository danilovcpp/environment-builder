using System.ComponentModel;
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
    private bool closing;
    private Task? startup;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Do not subscribe to SystemThemeWatcher: the repository owns its palette.
        ApplicationThemeManager.Apply(ApplicationTheme.Light, updateAccent: false);
        base.OnStartup(e);
        var navigation = new NavigationService();
        var themes = new ThemeService();
        var shell = new ShellViewModel(navigation, themes);
        var window = new MainWindow(shell);
        MainWindow = window;
        window.Closing += OnWindowClosing;
        window.Show();
        startup = InitializeAsync(shell, navigation, themes);
        await startup;
    }

    private async Task InitializeAsync(ShellViewModel shell, NavigationService navigation, ThemeService themes)
    {
        try
        {
            services = await AppServices.CreateAsync(shell, navigation, themes);
            shell.Authoring = services.Provider.GetRequiredService<AuthoringSession>();
            IReadOnlyList<Operation> unresolved = await services.Provider.GetRequiredService<IOperationStore>().ReadUnresolvedAsync(CancellationToken.None);
            await shell.LoadAsync(services.Provider.GetRequiredService<UiDispatcher>(),
                services.Provider.GetRequiredService<AppDataDirectory>().Root, unresolved.Count, CancellationToken.None);
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

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        if (closing) { return; }
        closing = true;
        try
        {
            if (startup is not null) { await startup; }
            if (MainWindow?.DataContext is ShellViewModel active && active.Authoring is not null && !await active.Authoring.CanLeaveAsync(CancellationToken.None))
            { closing = false; return; }
            if (services is not null) { await services.DisposeAsync(); }
            if (sender is Window window) { window.Closing -= OnWindowClosing; window.Close(); }
            Shutdown();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            closing = false;
            if (MainWindow?.DataContext is ShellViewModel shell) { shell.ReportStorageError("Не удалось безопасно завершить запись. Окно остаётся открытым."); }
        }
    }
}
