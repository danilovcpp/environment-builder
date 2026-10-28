using System.ComponentModel;
using System.Windows;

namespace EnvironmentBuilder.App.Shell;

public partial class MainWindow : Window
{
    private bool closing;
    private bool approved;
    private bool compactLayout;
    private bool contextCollapsedForLayout;
    public MainWindow(ShellViewModel viewModel) { InitializeComponent(); DataContext = viewModel; Closing += OnClosing; }
    public Func<Task<bool>>? RequestClose { get; set; }
    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Resources["Size.ContextPanel"] = FindResource(ActualWidth >= (double)FindResource("Breakpoint.Wide")
            ? "Size.ContextPanelWide" : "Size.ContextPanelRegular");
        bool compact = ActualWidth < (double)FindResource("Breakpoint.Compact");
        Resources["Layout.Compact"] = compact;
        Resources["Layout.RuntimeMetricColumns"] = compact ? 3 : 6;
        Resources["Grid.EnvironmentMetrics"] = compact ? new GridLength(0) : new GridLength(1.6, GridUnitType.Star);
        if (PageContext is null || compact == compactLayout) { return; }
        if (compact)
        {
            contextCollapsedForLayout = PageContext.IsExpanded;
            PageContext.IsExpanded = false;
        }
        else if (contextCollapsedForLayout)
        {
            PageContext.IsExpanded = true;
            contextCollapsedForLayout = false;
        }
        compactLayout = compact;
    }
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
