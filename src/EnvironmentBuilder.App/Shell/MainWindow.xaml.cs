using System.ComponentModel;
using System.Windows;

namespace EnvironmentBuilder.App.Shell;

public partial class MainWindow : Window
{
    private bool closing;
    private bool approved;
    public MainWindow(ShellViewModel viewModel) { InitializeComponent(); DataContext = viewModel; Closing += OnClosing; }
    public Func<Task<bool>>? RequestClose { get; set; }
    private void WindowSizeChanged(object sender, SizeChangedEventArgs e) => Resources["Size.ContextPanel"] = ActualWidth >= 1500 ? 448d : 320d;
    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (approved || RequestClose is null) { return; }
        e.Cancel = true;
        if (closing) { return; }
        closing = true;
        try { if (await RequestClose()) { approved = true; Close(); } }
        finally { closing = false; }
    }
}
