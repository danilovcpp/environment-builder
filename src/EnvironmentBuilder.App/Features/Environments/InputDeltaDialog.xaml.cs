using System.Windows;
namespace EnvironmentBuilder.App.Features.Environments;

public partial class InputDeltaDialog : Window
{
    public InputDeltaDialog() { InitializeComponent(); Loaded += (_, _) => CancelButton.Focus(); }
    private void Accept(object sender, RoutedEventArgs e) => DialogResult = true;
}
