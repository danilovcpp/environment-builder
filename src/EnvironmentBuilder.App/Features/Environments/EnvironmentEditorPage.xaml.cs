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
    private int focusRequest;
    public EnvironmentEditorPage() { InitializeComponent(); }
    private void PageLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not EnvironmentEditorViewModel editor) { return; }
        model = editor; model.PropertyChanged += ModelChanged;
        EditorScroll.ScrollToVerticalOffset(model.ScrollOffset);
        int request = ++focusRequest;
        Dispatcher.InvokeAsync(() => { if (model is not null && request == focusRequest) { FocusField(model.LastFocusedField); } }, DispatcherPriority.Input);
    }
    private void PageUnloaded(object sender, RoutedEventArgs e) { focusRequest++; if (model is not null) { model.PropertyChanged -= ModelChanged; model = null; } }
    private void RememberScroll(object sender, ScrollChangedEventArgs e) { if (model is not null && e.VerticalChange != 0) { model.ScrollOffset = e.VerticalOffset; } }
    private void RememberFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (model is not null && e.NewFocus is FrameworkElement element && element.Tag is string field) { model.LastFocusedField = field; }
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EnvironmentEditorViewModel.FocusField) && model?.FocusField.Length > 0)
        {
            focusRequest++;
            string field = model.FocusField;
            Dispatcher.InvokeAsync(() => { if (model?.FocusField == field) { FocusField(field); } }, DispatcherPriority.Input);
        }
    }
    private void FocusField(string field)
    {
        if (model is null) { return; }
        int request = focusRequest;
        string? tabHeader = field.Contains("parameters", StringComparison.Ordinal) ? "Параметры" : field.Contains("ports", StringComparison.Ordinal) ? "Порты" :
            field.Contains("storage", StringComparison.Ordinal) ? "Хранилища" : field.Contains("environment", StringComparison.Ordinal) ? "Переменные" : field.Contains("source", StringComparison.Ordinal) ? "Источник" : null;
        if (tabHeader is not null)
        {
            foreach (ComponentEditor component in Descendants(this).OfType<ComponentEditor>().ToArray())
            {
                if (component.DataContext is ComponentEditorViewModel row && field.Contains("/components/", StringComparison.Ordinal) &&
                    Guid.TryParse(field.Split('/').ElementAtOrDefault(2), out Guid targetId) && row.Id != targetId) { continue; }
                component.RevealTab(tabHeader);
            }
            UpdateLayout();
        }
        // Selecting a previously collapsed tab materializes its content on the next Dispatcher pass.
        Dispatcher.InvokeAsync(() => { if (model is not null && request == focusRequest) { FocusTarget(field); } }, DispatcherPriority.Loaded);
    }
    private void FocusTarget(string field)
    {
        string key = field.Trim('/').Split('/').FirstOrDefault() ?? "name";
        if (key == "components")
        {
            key = field.Contains("ports", StringComparison.Ordinal) ? "ports" : field.Contains("parameters", StringComparison.Ordinal) ? "parameters" :
            field.Contains("storage", StringComparison.Ordinal) ? "storage" : field.Contains("source", StringComparison.Ordinal) ? "source" : field.Contains("environment", StringComparison.Ordinal) ? "environment" : "components";
        }
        FrameworkElement? target = key is "parameters" or "ports" or "storage" or "environment" or "source"
            ? Descendants(this).OfType<ComponentEditor>().Where(component => component.DataContext is ComponentEditorViewModel row &&
                (!Guid.TryParse(field.Split('/').ElementAtOrDefault(2), out Guid targetId) || row.Id == targetId)).Select(component => component.FindField(key)).FirstOrDefault(c => c is not null)
            : Descendants(this).OfType<FrameworkElement>().FirstOrDefault(c => c.Tag as string == key);
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
            table.ScrollIntoView(table.SelectedItem);
            // Newly revealed tab content receives Loaded/focus restoration before the requested table focus.
            int request = focusRequest;
            Dispatcher.InvokeAsync(() => { if (model is not null && request == focusRequest && table.IsVisible && table.IsEnabled) { Keyboard.Focus(table); } }, DispatcherPriority.Input);
        }
        else if (!target.Focus()) { target.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var descendant in Descendants(child)) { yield return descendant; } }
    }
}
