using System.Windows;

namespace EnvironmentBuilder.App.Shell;

public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel viewModel) { InitializeComponent(); DataContext = viewModel; }
}
