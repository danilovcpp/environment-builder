using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EnvironmentBuilder.App.Components;
using EnvironmentBuilder.App.Shell;

namespace EnvironmentBuilder.UiTests;

// Uses the shared resource test's STA and Application, just like the screen fixtures.
internal static class ControlVisualRegressionTests
{
    internal static void Verify()
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.SetResourceReference(Panel.BackgroundProperty, "Brush.Canvas");
        var input = new TextBox { Text = "Unchanged text position", Margin = new Thickness(0, 0, 0, 8) };
        var search = new TextBox { Text = "Search", Style = (Style)Application.Current.FindResource("Input.Search") };
        var combo = new ComboBox { ItemsSource = new[] { "First", "Second" }, SelectedIndex = 0 };
        var editableCombo = new ComboBox { ItemsSource = new[] { "First", "Second" }, IsEditable = true, Text = "First" };
        var check = new CheckBox { HorizontalAlignment = HorizontalAlignment.Left };
        var radio = new RadioButton { HorizontalAlignment = HorizontalAlignment.Left };
        var toggle = new CheckBox { Style = (Style)Application.Current.FindResource("Toggle.Switch"), HorizontalAlignment = HorizontalAlignment.Left };
        var table = new DataGrid { AutoGenerateColumns = false, Height = 120, ItemsSource = new[] { new Row("One", true), new Row("Two", false) } };
        table.Columns.Add(new DataGridTextColumn
        {
            Header = "Name", Binding = new Binding(nameof(Row.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            ElementStyle = (Style)Application.Current.FindResource("Text.Table"),
            EditingElementStyle = (Style)Application.Current.FindResource("Input.Table"),
        });
        table.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "Selected", Binding = new Binding(nameof(Row.Selected)),
            ElementStyle = (Style)Application.Current.FindResource("CheckBox.TableDisplay"),
            EditingElementStyle = (Style)Application.Current.FindResource("CheckBox.Table"),
        });
        var card = new Border { Width = 120, Height = 60, Padding = new Thickness(0), Style = (Style)Application.Current.FindResource("Panel.Card"), Child = new Border { Background = Brushes.Red } };
        var scroller = new ScrollViewer { Height = 60, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new Border { Height = 240 } };
        var other = new Button { Content = "Move focus" };
        var multiline = new TextBox { AcceptsReturn = true, Height = 80, Text = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"Line {i}: Жж ддру")), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        foreach (UIElement control in new UIElement[] { input, search, combo, editableCombo, multiline, check, radio, toggle, table, card, scroller, other }) { panel.Children.Add(control); }
        var window = new Window { Content = panel, Width = 640, Height = 760, ShowInTaskbar = false, Left = 40, Top = 40 };
        window.Show();
        try
        {
            var themes = new ThemeService();
            foreach (bool dark in new[] { false, true })
            {
                themes.Apply(dark);
                other.Focus();
                UiReferenceTests.Layout(window);
                Assert.Equal(32, input.ActualHeight);
                Assert.Equal(input.ActualHeight, search.ActualHeight);
                VerifyTextFits(input);
                VerifyTextFits(search);
                var comboEditor = (TextBox)editableCombo.Template.FindName("PART_EditableTextBox", editableCombo);
                VerifyTextFits(comboEditor);
                Assert.Same(editableCombo.Foreground, comboEditor.Foreground);
                Assert.Equal(editableCombo.FontSize, comboEditor.FontSize);
                Assert.Equal(editableCombo.FontFamily, comboEditor.FontFamily);
                multiline.ScrollToEnd();
                UiReferenceTests.Layout(window);
                Assert.True(multiline.VerticalOffset > 0);
                VerifyTextFits(multiline, multiline.Text.Length - 1);
                multiline.ScrollToHome();
                input.Text = new string('Ж', 160) + " ддру";
                input.CaretIndex = input.Text.Length;
                input.Focus();
                input.ScrollToHorizontalOffset(double.MaxValue);
                UiReferenceTests.Layout(window);
                Assert.True(input.HorizontalOffset > 0, "Long single-line input must scroll to the caret.");
                VerifyTextFits(input, input.Text.Length - 1);
                input.Text = "Unchanged text position";
                input.ScrollToHorizontalOffset(0);
                other.Focus();
                UiReferenceTests.Layout(window);
                foreach (Control control in new Control[] { input, search, combo, editableCombo })
                {
                    FrameworkElement content = control is TextBox text
                        ? (FrameworkElement)text.Template.FindName("PART_ContentHost", text)
                        : (FrameworkElement)control.Template.FindName(control == editableCombo ? "PART_EditableTextBox" : "Selection", control);
                    Rect before = Bounds(content, window);
                    Size size = control.RenderSize;
                    Thickness border = control.BorderThickness;
                    control.Focus(); // Editable ComboBox forwards focus to its text editor.
                    UiReferenceTests.Layout(window);
                    Assert.True(control.IsKeyboardFocusWithin, $"Expected focus inside {control.GetType().Name} (editable: {control == editableCombo}).");
                    Assert.Equal(before, Bounds(content, window));
                    Assert.Equal(size, control.RenderSize);
                    Assert.Equal(border, control.BorderThickness);
                    Assert.Contains(UiReferenceTests.Descendants(control).OfType<Border>(), ring =>
                        ring.Style == Application.Current.FindResource("Input.FocusRing") && ring.Visibility == Visibility.Visible && !ring.IsHitTestVisible);
                    other.Focus();
                }

                combo.IsDropDownOpen = true;
                UiReferenceTests.Layout(window);
                var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
                Assert.True(popup.IsOpen);
                Assert.True(((Border)popup.Child).Padding.Left > 0);
                combo.IsDropDownOpen = false;

                foreach (bool? state in new bool?[] { false, true, null })
                {
                    check.IsChecked = state;
                    UiReferenceTests.Layout(window);
                    var box = (Border)check.Template.FindName("Box", check);
                    Assert.Equal(16, check.ActualWidth);
                    Assert.Equal(new Size(16, 16), box.RenderSize);
                    Assert.Equal(Visibility.Collapsed, ((UIElement)check.Template.FindName("Label", check)).Visibility);
                    if (state is not false)
                    {
                        var glyph = (FrameworkElement)check.Template.FindName(state is true ? "Check" : "Indeterminate", check);
                        Rect bounds = Bounds(glyph, box);
                        Assert.InRange(Math.Abs(bounds.Left + bounds.Width / 2 - box.ActualWidth / 2), 0, 0.5);
                        Assert.InRange(Math.Abs(bounds.Top + bounds.Height / 2 - box.ActualHeight / 2), 0, 0.5);
                    }
                }

                Assert.Equal(16, radio.ActualWidth);
                Assert.Equal(30, toggle.ActualWidth);
                var row = (DataGridRow)table.ItemContainerGenerator.ContainerFromIndex(0);
                var cell = UiReferenceTests.Descendants(row).OfType<DataGridCell>().First();
                var caption = UiReferenceTests.Descendants(cell).OfType<TextBlock>().First();
                Rect captionBefore = Bounds(caption, table);
                Size rowBefore = row.RenderSize;
                row.IsSelected = true;
                Assert.True(cell.Focus());
                UiReferenceTests.Layout(window);
                Assert.Equal(captionBefore, Bounds(caption, table));
                Assert.Equal(rowBefore, row.RenderSize);
                Assert.Equal(new Thickness(0), row.BorderThickness);
                Assert.All(UiReferenceTests.Descendants(table).OfType<CheckBox>(), item => Assert.NotNull(item.Template.FindName("Box", item)));
                table.CurrentCell = new DataGridCellInfo(table.Items[0], table.Columns[0]);
                Assert.True(table.BeginEdit());
                UiReferenceTests.Layout(window);
                var cellEditor = UiReferenceTests.Descendants(cell).OfType<TextBox>().Single();
                Assert.Same(Application.Current.FindResource("Input.Table"), cellEditor.Style);
                VerifyTextFits(cellEditor);
                table.CancelEdit();

                Assert.False(card.Clip.FillContains(new Point(0.5, 0.5)));
                Assert.True(card.Clip.FillContains(new Point(60, 30)));
                var bitmap = new RenderTargetBitmap(120, 60, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(card);
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
                Assert.Equal(0, pixel[3]);
                card.Width = 80;
                card.CornerRadius = new CornerRadius(12, 0, 6, 0);
                UiReferenceTests.Layout(window);
                Assert.False(card.Clip.FillContains(new Point(0.5, 0.5)));
                Assert.True(card.Clip.FillContains(new Point(79.5, 0.5)));
                card.Width = 120;
                card.CornerRadius = new CornerRadius(8);

                var bar = UiReferenceTests.Descendants(scroller).OfType<ScrollBar>().Single(item => item.Orientation == Orientation.Vertical);
                Assert.Equal(8, bar.ActualWidth);
                scroller.ScrollToVerticalOffset(80);
                UiReferenceTests.Layout(window);
                Assert.Equal(80, scroller.VerticalOffset);
                UiReferenceTests.Capture(window, "control-states-" + (dark ? "dark" : "light"));
            }

            var converter = new RoundedClipConverter();
            var tiny = (Geometry)converter.Convert([8d, 6d, new CornerRadius(20)], typeof(Geometry), null!, CultureInfo.InvariantCulture);
            Assert.True(tiny.Bounds.Width <= 8 && tiny.Bounds.Height <= 6);
            VerifyWrapping();
        }
        finally { window.Close(); }
    }

    private static Rect Bounds(FrameworkElement child, Visual parent) => child.TransformToAncestor(parent).TransformBounds(new Rect(child.RenderSize));
    private static void VerifyTextFits(TextBox text, int index = 0)
    {
        Rect glyph = text.GetRectFromCharacterIndex(index);
        Assert.False(glyph.IsEmpty, "Input text must have a visible layout.");
        Assert.True(glyph.Height >= text.FontSize, "Glyphs must retain their full line height.");
        Assert.True(glyph.Top >= 0 && glyph.Bottom <= text.ActualHeight,
            $"Input clips text vertically: glyph {glyph}, control height {text.ActualHeight}.");
    }

    private static void VerifyWrapping()
    {
        var group = new SpacedWrapPanel { Spacing = 8 };
        var first = new Border { Width = 60, Height = 32 };
        var hidden = new Border { Width = 200, Height = 32, Visibility = Visibility.Collapsed };
        var second = new Border { Width = 60, Height = 32 };
        group.Children.Add(first); group.Children.Add(hidden); group.Children.Add(second);
        group.Measure(new Size(150, double.PositiveInfinity));
        group.Arrange(new Rect(0, 0, 150, group.DesiredSize.Height));
        Assert.Equal(8, Bounds(second, group).Left - Bounds(first, group).Right);
        group.Measure(new Size(100, double.PositiveInfinity));
        group.Arrange(new Rect(0, 0, 100, group.DesiredSize.Height));
        Assert.Equal(8, Bounds(second, group).Top - Bounds(first, group).Bottom);
    }

    private sealed class Row(string name, bool selected)
    {
        public string Name { get; set; } = name;
        public bool Selected { get; set; } = selected;
    }
}
