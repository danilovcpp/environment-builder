using System.Windows;
using Wpf.Ui.Appearance;

namespace EnvironmentBuilder.App.Shell;

public sealed class ThemeService
{
    public bool IsDark { get; private set; }
    public void Apply(bool dark)
    {
        Application application = Application.Current;
        application.Dispatcher.VerifyAccess();
        ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light, updateAccent: false);
        var dictionaries = application.Resources.MergedDictionaries;
        ResourceDictionary? existing = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.EndsWith("Themes/Light.xaml", StringComparison.Ordinal) == true || d.Source?.OriginalString.EndsWith("Themes/Dark.xaml", StringComparison.Ordinal) == true);
        var replacement = new ResourceDictionary { Source = new Uri($"pack://application:,,,/EnvironmentBuilder.App;component/Themes/{(dark ? "Dark" : "Light")}.xaml") };
        if (existing is not null) { dictionaries[dictionaries.IndexOf(existing)] = replacement; }
        else { dictionaries.Add(replacement); }
        IsDark = dark;
    }
}
