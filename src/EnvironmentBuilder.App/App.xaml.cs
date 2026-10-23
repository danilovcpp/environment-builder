using System.Windows;
using Wpf.Ui.Appearance;

namespace EnvironmentBuilder.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Do not subscribe to SystemThemeWatcher: the repository owns its palette.
        ApplicationThemeManager.Apply(ApplicationTheme.Light, updateAccent: false);
        base.OnStartup(e);
    }
}
