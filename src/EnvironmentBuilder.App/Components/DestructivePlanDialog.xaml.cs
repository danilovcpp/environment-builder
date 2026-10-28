using System.Windows;

namespace EnvironmentBuilder.App.Components;

public partial class DestructivePlanDialog : Window
{
    public DestructivePlanDialog() { InitializeComponent(); Loaded += (_, _) => CancelButton.Focus(); }
    private void Accept(object sender, RoutedEventArgs e) => DialogResult = true;
}
