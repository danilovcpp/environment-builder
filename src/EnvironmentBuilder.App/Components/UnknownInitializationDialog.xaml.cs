using System.Windows;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.App.Components;

public partial class UnknownInitializationDialog : Window
{
    public UnknownInitializationDialog() { InitializeComponent(); Loaded += (_, _) => CancelButton.Focus(); }
    private void Choose(InitializationDecision decision) { ((UnknownInitializationViewModel)DataContext).Decision = decision; DialogResult = true; }
    private void AcceptVerified(object sender, RoutedEventArgs e) => Choose(InitializationDecision.AcceptVerified);
    private void Repeat(object sender, RoutedEventArgs e) => Choose(InitializationDecision.Repeat);
    private void Refresh(object sender, RoutedEventArgs e) => Choose(InitializationDecision.Refresh);
}
