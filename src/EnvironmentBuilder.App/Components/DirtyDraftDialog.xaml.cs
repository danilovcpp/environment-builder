using System.Windows;
using EnvironmentBuilder.App.Features.Environments;

namespace EnvironmentBuilder.App.Components;

public partial class DirtyDraftDialog : Window
{
    public DirtyDraftDialog() { InitializeComponent(); }
    public DirtyDraftDecision Decision { get; private set; } = DirtyDraftDecision.Stay;
    private void SaveClick(object sender, RoutedEventArgs e) { Decision = DirtyDraftDecision.Save; Close(); }
    private void DiscardClick(object sender, RoutedEventArgs e) { Decision = DirtyDraftDecision.Discard; Close(); }
    private void StayClick(object sender, RoutedEventArgs e) { Close(); }
}
