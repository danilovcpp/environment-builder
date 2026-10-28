using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using EnvironmentBuilder.App.Components;
using EnvironmentBuilder.App.Shell;
using FlaUI.Core.Input;

namespace EnvironmentBuilder.UiTests;

internal static class ActiveControlStatesTests
{
    internal static void Verify()
    {
        var buttons = new SpacedWrapPanel();
        foreach (string kind in new[] { "Primary", "Secondary", "Ghost", "Destructive" })
        {
            buttons.Children.Add(new Button { Content = kind, Style = Resource<Style>("Button." + kind) });
        }
        var choices = new SpacedWrapPanel();
        choices.Children.Add(new CheckBox { Content = "Checkbox", IsChecked = true });
        choices.Children.Add(new RadioButton { Content = "Radio", IsChecked = true });
        choices.Children.Add(new CheckBox { Content = "Switch", IsChecked = true, Style = Resource<Style>("Toggle.Switch") });
        choices.Children.Add(new RadioButton { Content = "Chip", IsChecked = true, Style = Resource<Style>("Toggle.Chip") });
        var input = new TextBox { Text = "Жж ддру — active text", Margin = new Thickness(0, 12, 0, 0) };
        var tabs = new TabControl { Margin = new Thickness(0, 12, 0, 0) };
        tabs.Items.Add(new TabItem { Header = "Selected", Content = "Tab content" });
        tabs.Items.Add(new TabItem { Header = "Other", Content = "Other content" });
        tabs.Items.Add(new TabItem { Header = "Disabled", IsEnabled = false });
        var expander = new Expander { Header = "Expandable section", Content = new TextBlock { Text = "Expanded content", Margin = new Thickness(0, 8, 0, 0) } };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Active states", Style = Resource<Style>("Text.SectionTitle"), Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(buttons);
        choices.Margin = new Thickness(0, 12, 0, 0); panel.Children.Add(choices);
        panel.Children.Add(input); panel.Children.Add(tabs); panel.Children.Add(expander);
        var window = new Window { Content = panel, Width = 900, Height = 460, Left = 40, Top = 40, Topmost = true, ShowInTaskbar = false };
        window.SetResourceReference(Control.BackgroundProperty, "Brush.Canvas");
        window.Show(); window.Activate();
        try
        {
            var theme = new ThemeService();
            foreach (bool dark in new[] { false, true })
            {
                theme.Apply(dark); UiReferenceTests.Layout(window);
                foreach (Button button in buttons.Children)
                {
                    var caption = UiReferenceTests.Descendants(button).OfType<ContentPresenter>().First();
                    Rect before = Bounds(caption, window);
                    Size size = button.RenderSize;
                    MoveTo(button, window);
                    Assert.True(button.IsMouseOver, "Real pointer must reach the button.");
                    string kind = (string)button.Content;
                    Assert.Same(Resource<Brush>(kind switch { "Primary" => "Brush.AccentHover", "Destructive" => "Brush.ErrorBackground", _ => "Brush.Hover" }), button.Background);
                    Mouse.Down(MouseButton.Left);
                    try
                    {
                        UiReferenceTests.Layout(window);
                        Assert.True(button.IsPressed);
                        Assert.Same(Resource<Brush>(kind switch { "Primary" => "Brush.AccentPressed", "Destructive" => "Brush.ErrorPressed", _ => "Brush.Pressed" }), button.Background);
                        Assert.Equal(before, Bounds(caption, window));
                        Assert.Equal(size, button.RenderSize);
                        UiReferenceTests.Capture(window, $"pressed-{kind.ToLowerInvariant()}-" + (dark ? "dark" : "light"));
                    }
                    finally { Mouse.Up(MouseButton.Left); UiReferenceTests.Layout(window); }
                    button.IsEnabled = false; UiReferenceTests.Layout(window);
                    Assert.Same(Resource<Brush>("Brush.TextDisabled"), button.Foreground);
                    Assert.Equal(size, button.RenderSize);
                    button.IsEnabled = true;
                }

                foreach (ToggleButton choice in choices.Children)
                {
                    Rect before = Bounds(choice, window);
                    MoveTo(choice, window);
                    Mouse.Down(MouseButton.Left);
                    try
                    {
                        UiReferenceTests.Layout(window);
                        Assert.True(choice.IsPressed);
                        Assert.Equal(before, Bounds(choice, window));
                        UiReferenceTests.Capture(window, "choice-" + choice.Content + "-" + (dark ? "dark" : "light"));
                    }
                    finally { Mouse.Up(MouseButton.Left); UiReferenceTests.Layout(window); }
                    choice.IsChecked = true;
                    choice.IsEnabled = false;
                    UiReferenceTests.Layout(window);
                    Assert.Same(Resource<Brush>("Brush.TextDisabled"), choice.Foreground);
                    Assert.Equal(before, Bounds(choice, window));
                    choice.IsEnabled = true;
                }

                var binding = input.SetBinding(TextBox.TextProperty, new Binding(nameof(Value.Text)) { Source = new Value() });
                input.Focus(); input.SelectAll(); UiReferenceTests.Layout(window);
                Rect textBefore = input.GetRectFromCharacterIndex(0);
                Validation.MarkInvalid(binding, new ValidationError(new ExceptionValidationRule(), binding, "Required", null));
                UiReferenceTests.Layout(window);
                Assert.Equal(textBefore, input.GetRectFromCharacterIndex(0));
                Assert.Same(Resource<Brush>("Brush.ErrorForeground"), input.BorderBrush);
                UiReferenceTests.Capture(window, "validation-selected-" + (dark ? "dark" : "light"));
                Validation.ClearInvalid(binding); input.IsEnabled = false; UiReferenceTests.Layout(window);
                Assert.Same(Resource<Brush>("Brush.TextDisabled"), input.Foreground);
                input.IsEnabled = true;
                ((TabItem)tabs.Items[1]).IsSelected = true;
                expander.IsExpanded = true;
                UiReferenceTests.Layout(window);
                Assert.Equal("Other content", tabs.SelectedContent);
                Assert.Equal(Visibility.Visible, ((UIElement)expander.Template.FindName("ExpandedBody", expander)).Visibility);
                UiReferenceTests.Capture(window, "active-components-" + (dark ? "dark" : "light"));
            }
        }
        finally { Mouse.Up(MouseButton.Left); window.Close(); }
    }

    private static void MoveTo(FrameworkElement element, Window window)
    {
        Point point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        Mouse.MoveTo((int)Math.Round(point.X), (int)Math.Round(point.Y));
        UiReferenceTests.Layout(window);
    }

    private static T Resource<T>(string key) => (T)Application.Current.FindResource(key);
    private static Rect Bounds(FrameworkElement element, Visual parent) => element.TransformToAncestor(parent).TransformBounds(new Rect(element.RenderSize));
    private sealed class Value { public string Text { get; set; } = "Жж ддру — active text"; }
}
