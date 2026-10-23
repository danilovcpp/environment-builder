using System.Windows;
using System.Windows.Controls;

namespace EnvironmentBuilder.App.Components;

public partial class ContextPanel : UserControl
{
    public ContextPanel() { InitializeComponent(); }
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(ContextPanel));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(ContextPanel), new PropertyMetadata(true));
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
}
