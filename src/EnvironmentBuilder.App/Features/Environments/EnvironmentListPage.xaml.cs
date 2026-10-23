using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EnvironmentBuilder.App.Features.Environments;

public partial class EnvironmentListPage : UserControl
{
    private bool restoring;
    public EnvironmentListPage() { InitializeComponent(); }
    private void RowsScrollChanged(object sender, ScrollChangedEventArgs e)
    { if (!restoring && DataContext is EnvironmentListViewModel model && e.VerticalChange != 0) { model.ScrollOffset = e.VerticalOffset; } }
    private void RowsLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not EnvironmentListViewModel model) { return; }
        restoring = true;
        Rows.UpdateLayout();
        FindScroll(Rows)?.ScrollToVerticalOffset(model.ScrollOffset);
        restoring = false;
    }
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) { return scroll; }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { ScrollViewer? found = FindScroll(VisualTreeHelper.GetChild(parent, i)); if (found is not null) { return found; } }
        return null;
    }
}
