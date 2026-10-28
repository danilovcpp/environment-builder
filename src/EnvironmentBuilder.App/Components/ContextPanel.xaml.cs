using System.Windows;
using System.Windows.Controls;

namespace EnvironmentBuilder.App.Components;

public partial class ContextPanel : UserControl
{
    public ContextPanel() { InitializeComponent(); }
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(ContextPanel));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public static readonly DependencyProperty HeadingProperty = DependencyProperty.Register(nameof(Heading), typeof(string), typeof(ContextPanel), new PropertyMetadata("Сводка"));
    public string Heading { get => (string)GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }
    public static readonly DependencyProperty SupportingTextProperty = DependencyProperty.Register(nameof(SupportingText), typeof(string), typeof(ContextPanel), new PropertyMetadata(""));
    public string SupportingText { get => (string)GetValue(SupportingTextProperty); set => SetValue(SupportingTextProperty, value); }
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(ContextPanel), new PropertyMetadata(true));
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
}
