using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EnvironmentBuilder.App.Features.Environments;

public partial class EnvironmentEditorPage : UserControl
{
    private EnvironmentEditorViewModel? model;
    public EnvironmentEditorPage() { InitializeComponent(); }
    private void PageLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not EnvironmentEditorViewModel editor) { return; }
        model = editor; model.PropertyChanged += ModelChanged;
        EditorScroll.ScrollToVerticalOffset(model.ScrollOffset);
        Dispatcher.InvokeAsync(() => FocusField(model.LastFocusedField), DispatcherPriority.Input);
    }
    private void PageUnloaded(object sender, RoutedEventArgs e) { if (model is not null) { model.PropertyChanged -= ModelChanged; model = null; } }
    private void RememberScroll(object sender, ScrollChangedEventArgs e) { if (model is not null && e.VerticalChange != 0) { model.ScrollOffset = e.VerticalOffset; } }
    private void RememberFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (model is not null && e.NewFocus is FrameworkElement element && element.Tag is string field) { model.LastFocusedField = field; }
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(EnvironmentEditorViewModel.FocusField) && model?.FocusField.Length > 0) { FocusField(model.FocusField); } }
    private void FocusField(string field)
    {
        string? tabHeader = field.Contains("parameters", StringComparison.Ordinal) ? "Параметры" : field.Contains("ports", StringComparison.Ordinal) ? "Порты" :
            field.Contains("storage", StringComparison.Ordinal) ? "Хранилища" : field.Contains("environment", StringComparison.Ordinal) ? "Переменные" : field.Contains("source", StringComparison.Ordinal) ? "Источник" : null;
        if (tabHeader is not null)
        {
            foreach (ComponentEditor component in Descendants(this).OfType<ComponentEditor>().ToArray())
            {
                if (component.DataContext is ComponentEditorViewModel row && field.Contains("/components/", StringComparison.Ordinal) &&
                    Guid.TryParse(field.Split('/').ElementAtOrDefault(2), out Guid targetId) && row.Id != targetId) { continue; }
                foreach (TabControl tabs in Descendants(component).OfType<TabControl>().ToArray())
                { tabs.SelectedItem = tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Header as string == tabHeader); }
            }
            UpdateLayout();
        }
        string key = field.Trim('/').Split('/').FirstOrDefault() ?? "name";
        if (key == "components")
        {
            key = field.Contains("ports", StringComparison.Ordinal) ? "ports" : field.Contains("parameters", StringComparison.Ordinal) ? "parameters" :
            field.Contains("storage", StringComparison.Ordinal) ? "storage" : field.Contains("source", StringComparison.Ordinal) ? "source" : field.Contains("environment", StringComparison.Ordinal) ? "environment" : "components";
        }
        FrameworkElement? target = Descendants(this).OfType<FrameworkElement>().FirstOrDefault(c => c.Tag as string == key);
        if (target is null) { target = NameInput; }
        DependencyObject? parent = target;
        while (parent is not null)
        {
            if (parent is TabItem tab) { tab.IsSelected = true; }
            if (parent is Expander expander) { expander.IsExpanded = true; }
            parent = VisualTreeHelper.GetParent(parent);
        }
        target.BringIntoView();
        if (target is DataGrid table && table.Items.Count > 0)
        {
            string? parameter = key == "parameters" ? field.Split('/').LastOrDefault() : null;
            table.SelectedItem = table.Items.OfType<ValueEditor>().FirstOrDefault(v => v.Key == parameter) ?? table.Items[0];
            table.ScrollIntoView(table.SelectedItem); table.Focus();
        }
        else if (!target.Focus()) { target.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var descendant in Descendants(child)) { yield return descendant; } }
    }
}
