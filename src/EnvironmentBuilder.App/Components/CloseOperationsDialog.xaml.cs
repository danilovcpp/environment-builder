using System.Windows;
using EnvironmentBuilder.App.Features.Environments;
namespace EnvironmentBuilder.App.Components;

public partial class CloseOperationsDialog : Window
{
    public CloseOperationsDialog() { InitializeComponent(); Loaded += (_, _) => ReturnButton.Focus(); }
    public CloseOperationsDecision Decision { get; private set; } = CloseOperationsDecision.Return;
    private void Wait(object sender, RoutedEventArgs e) { Decision = CloseOperationsDecision.Wait; DialogResult = true; }
    private void CancelOperations(object sender, RoutedEventArgs e) { Decision = CloseOperationsDecision.Cancel; DialogResult = true; }
}
