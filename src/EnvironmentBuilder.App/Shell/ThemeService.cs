using System.Windows;
using Wpf.Ui.Appearance;
using Microsoft.Win32;

namespace EnvironmentBuilder.App.Shell;

public sealed class ThemeService : IDisposable
{
    private string mode = "light";
    private bool watching;
    public bool IsDark { get; private set; }
    public void SetMode(string value)
    {
        mode = value;
        Apply(value == "dark" || value == "system" && SystemIsDark());
    }
    public void WatchSystemTheme()
    {
        if (watching) { return; }
        SystemEvents.UserPreferenceChanged += SystemThemeChanged;
        watching = true;
    }
    private void SystemThemeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (mode == "system") { Application.Current.Dispatcher.InvokeAsync(() => Apply(SystemIsDark())); }
    }
    private static bool SystemIsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }
    public void Dispose()
    {
        if (watching) { SystemEvents.UserPreferenceChanged -= SystemThemeChanged; watching = false; }
    }
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
