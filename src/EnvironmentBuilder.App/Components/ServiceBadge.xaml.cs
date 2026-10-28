using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MahApps.Metro.IconPacks;

namespace EnvironmentBuilder.App.Components;

public partial class ServiceBadge : UserControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(ServiceBadge), new PropertyMetadata("Box", Changed));
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(nameof(Color), typeof(string), typeof(ServiceBadge), new PropertyMetadata("#0072FF", Changed));
    public static readonly DependencyProperty LogoUrlProperty = DependencyProperty.Register(nameof(LogoUrl), typeof(string), typeof(ServiceBadge), new PropertyMetadata(null, Changed));
    public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Color { get => (string?)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public string? LogoUrl { get => (string?)GetValue(LogoUrlProperty); set => SetValue(LogoUrlProperty, value); }
    private string? loadedLogo;
    public ServiceBadge() { InitializeComponent(); Update(); }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((ServiceBadge)sender).Update();
    private void Update()
    {
        if (Fallback is null) { return; }
        Fallback.Kind = Enum.TryParse(Icon, out PackIconLucideKind kind) ? kind : PackIconLucideKind.Box;
        try { Fallback.Foreground = (Brush)new BrushConverter().ConvertFromString(Color ?? "#0072FF")!; }
        catch (Exception error) when (error is FormatException or NotSupportedException) { Fallback.Foreground = Brushes.DodgerBlue; }
        if (loadedLogo == LogoUrl) { return; }
        loadedLogo = LogoUrl; Logo.Source = null; Fallback.Visibility = Visibility.Visible;
        if (!Uri.TryCreate(LogoUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https") { return; }
        try
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.UriSource = uri; bitmap.DecodePixelWidth = 96; bitmap.EndInit();
            bitmap.DownloadCompleted += (_, _) => { if (ReferenceEquals(Logo.Source, bitmap)) { Fallback.Visibility = Visibility.Collapsed; } };
            Logo.Source = bitmap;
            if (!bitmap.IsDownloading) { Fallback.Visibility = Visibility.Collapsed; }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Logo.Source = null; }
    }
    private void LogoFailed(object sender, ExceptionRoutedEventArgs args) { Logo.Source = null; Fallback.Visibility = Visibility.Visible; }
}
