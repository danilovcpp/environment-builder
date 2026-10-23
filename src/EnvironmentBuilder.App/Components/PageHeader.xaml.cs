using System.Windows;
using System.Windows.Controls;

namespace EnvironmentBuilder.App.Components;

public partial class PageHeader : UserControl
{
    public PageHeader() { InitializeComponent(); }
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(PageHeader));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
}
