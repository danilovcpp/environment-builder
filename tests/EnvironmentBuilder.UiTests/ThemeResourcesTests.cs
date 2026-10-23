using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace EnvironmentBuilder.UiTests;

[Trait("Category", "UI")]
public sealed class ThemeResourcesTests
{
    [Fact]
    public async Task Both_themes_render_shared_controls_and_update_existing_content_without_binding_errors()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                VerifyResources();
                completion.SetResult();
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task;
    }

    private static void VerifyResources()
    {
        using var diagnostics = new BindingDiagnostics();
        var app = new App.App();
        app.InitializeComponent();
        try
        {
            var gallery = new StackPanel { Margin = new Thickness(24) };
            var root = new Border { Child = gallery, Width = 1024, Height = 900 };
            var window = new Window
            {
                Content = root,
                Width = root.Width,
                Height = root.Height,
                WindowStyle = WindowStyle.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = 0,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            root.SetResourceReference(Border.BackgroundProperty, "Brush.Canvas");
            gallery.Children.Add(new TextBlock { Text = "Environment Builder · Shared resources", Style = (Style)app.FindResource("Text.PageTitle") });
            var editor = new TextBox { Text = "Draft survives theme changes", Margin = new Thickness(0, 16, 0, 8) };
            gallery.Children.Add(editor);
            var combo = new ComboBox { ItemsSource = new[] { "Loopback", "Network (explicit)" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 8) };
            gallery.Children.Add(combo);
            var buttons = new WrapPanel();
            foreach (string variant in new[] { "Primary", "Secondary", "Ghost", "Destructive" })
            {
                buttons.Children.Add(new Button { Content = variant, Style = (Style)app.FindResource("Button." + variant), Margin = new Thickness(0, 0, 8, 8) });
            }

            buttons.Children.Add(new Button { Content = "Disabled", IsEnabled = false, Style = (Style)app.FindResource("Button.Primary") });
            gallery.Children.Add(buttons);
            gallery.Children.Add(new CheckBox { Content = "Checked", IsChecked = true, Margin = new Thickness(0, 8, 0, 8) });
            gallery.Children.Add(new CheckBox { Content = "Indeterminate", IsThreeState = true, IsChecked = null, Margin = new Thickness(0, 0, 0, 8) });
            var tabs = new TabControl { Margin = new Thickness(0, 8, 0, 8) };
            tabs.Items.Add(new TabItem { Header = "Configuration", Content = "Shared controls retain state." });
            tabs.Items.Add(new TabItem { Header = "Disabled", IsEnabled = false });
            gallery.Children.Add(tabs);
            var table = new DataGrid
            {
                ItemsSource = new[] { new FixtureRow("Draft", "Not started"), new FixtureRow("Example", "Needs attention") },
                AutoGenerateColumns = false,
                Height = 160,
                SelectedIndex = 0,
            };
            table.Columns.Add(new DataGridTextColumn { Header = "Environment", Binding = new Binding(nameof(FixtureRow.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            table.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding(nameof(FixtureRow.Status)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            gallery.Children.Add(table);
            gallery.Children.Add(new ScrollBar { Orientation = Orientation.Horizontal, Maximum = 100, Value = 30, ViewportSize = 20, Margin = new Thickness(0, 16, 0, 8) });
            var errorEditor = new TextBox { Margin = new Thickness(0, 8, 0, 8) };
            BindingExpressionBase errorBinding = errorEditor.SetBinding(TextBox.TextProperty, new Binding(nameof(FixtureRow.Name)) { Source = new FixtureRow("", "") });
            Validation.MarkInvalid(errorBinding, new ValidationError(new ExceptionValidationRule(), errorBinding, "Required field", null));
            gallery.Children.Add(errorEditor);

            object selectedItem = table.SelectedItem;
            window.Show();
            foreach (string theme in new[] { "Light", "Dark", "Light" })
            {
                ApplicationThemeManager.Apply(theme == "Light" ? ApplicationTheme.Light : ApplicationTheme.Dark, updateAccent: false);
                app.Resources.MergedDictionaries[2] = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/EnvironmentBuilder.App;component/Themes/{theme}.xaml"),
                };
                root.Measure(new Size(root.Width, root.Height));
                root.Arrange(new Rect(0, 0, root.Width, root.Height));
                root.UpdateLayout();
                root.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                // Closed popups are detached; create one as it would be created on opening.
                var tooltip = new ToolTip { Content = "Shared tooltip" };
                editor.ToolTip = tooltip;
                tooltip.ApplyTemplate();

                Assert.Equal("Draft survives theme changes", editor.Text);
                Assert.Equal(0, combo.SelectedIndex);
                Assert.Same(selectedItem, table.SelectedItem);
                Assert.Same(app.FindResource("Brush.Input"), editor.Background);
                Assert.Same(app.FindResource("Brush.ErrorForeground"), errorEditor.BorderBrush);
                Assert.Same(app.FindResource("Brush.PanelRaised"), tooltip.Background);
                var primaryButton = (Button)buttons.Children[0];
                DependencyObject caption = Descendants(primaryButton).First(child => child is AccessText or TextBlock);
                Assert.Same(app.FindResource("Brush.OnAccent"), TextElement.GetForeground(caption));
                var disabledButton = (Button)buttons.Children[4];
                Assert.Same(app.FindResource("Brush.AccentDisabled"), disabledButton.Background);
                Assert.Same(app.FindResource("Brush.TextDisabled"), disabledButton.Foreground);
                Assert.Same(app.FindResource("Brush.ErrorForeground"), ((Button)buttons.Children[3]).Foreground);
                Assert.True(table.Columns[0].ActualWidth > 100, "Star columns must allocate their available width.");
                Assert.Contains(Descendants(table).OfType<TextBlock>(), text => text.Text == "Draft");
                Assert.All(Descendants(root).OfType<Control>(), control => Assert.NotNull(control.Template));
                SavePreview(root, theme);
            }

            Assert.Empty(diagnostics.Errors);
            window.Close();
            VerifyShell(diagnostics);
        }
        finally
        {
            app.Shutdown();
        }
    }

    private static void VerifyShell(BindingDiagnostics diagnostics)
    {
        var navigation = new App.Shell.NavigationService();
        var themes = new App.Shell.ThemeService();
        var model = new App.Shell.ShellViewModel(navigation, themes);
        model.IsLoading = false;
        model.Status = "Хранилище доступно · Docker не проверен";
        model.Environments.Add(new App.Shell.ShellEnvironmentRow(Guid.NewGuid(), "Контрактный пример", 1, "Состояние runtime неизвестно"));
        var window = new App.Shell.MainWindow(model) { Left = -10000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        foreach (bool dark in new[] { false, true, false })
        {
            themes.Apply(dark);
            foreach (string route in new[] { "settings", "seedpacks", "diagnostics", "environments" })
            {
                model.NavigateCommand.Execute(route);
                Assert.Equal(route, navigation.Current.Route);
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            }
            Assert.Same(model, window.DataContext);
            Assert.Single(model.Environments);
            var context = Descendants(window).OfType<App.Components.ContextPanel>().Single();
            context.IsExpanded = false;
            window.UpdateLayout();
            Assert.Equal(48, context.ActualWidth);
            context.IsExpanded = true;
            window.UpdateLayout();
            Assert.Equal(240, context.ActualWidth);
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "Контрактный пример");
            SavePreview((FrameworkElement)window.Content, dark ? "shell-dark" : "shell-light");
        }
        Assert.Empty(diagnostics.Errors);
        window.Close();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static void SavePreview(FrameworkElement root, string theme)
    {
        string? outputDirectory = Environment.GetEnvironmentVariable("ENVIRONMENTBUILDER_UI_ARTIFACTS");
        if (string.IsNullOrEmpty(outputDirectory))
        {
            return;
        }

        Directory.CreateDirectory(outputDirectory);
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(outputDirectory, $"resources-{theme.ToLowerInvariant()}.png"));
        encoder.Save(stream);
    }

    private sealed record FixtureRow(string Name, string Status);

    private sealed class BindingDiagnostics : TraceListener
    {
        public BindingDiagnostics() => PresentationTraceSources.DataBindingSource.Listeners.Add(this);

        public List<string> Errors { get; } = [];

        public override void Write(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                Errors.Add(message);
            }
        }

        public override void WriteLine(string? message) => Write(message);

        protected override void Dispose(bool disposing)
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }
}
