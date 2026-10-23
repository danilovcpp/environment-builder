using System.Windows;
using System.Windows.Controls;

namespace EnvironmentBuilder.App.Components;

public partial class StatusBar : UserControl
{
    public StatusBar() { InitializeComponent(); }
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusBar));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
}
