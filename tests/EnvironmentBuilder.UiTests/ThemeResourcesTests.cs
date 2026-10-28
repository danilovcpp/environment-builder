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
        var app = new ResourceApplication();
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
            UiReferenceTests.Verify();
            VerifyShell(diagnostics);
            VerifyAuthoring(diagnostics);
            VerifyInspect(diagnostics);
            VerifyDestructiveEdit(diagnostics);
            VerifyDataReset(diagnostics);
        }
        finally
        {
            app.Shutdown();
        }
    }

    private static void VerifyDestructiveEdit(BindingDiagnostics diagnostics)
    {
        var fixture = new AuthoringUiFixture();
        var draft = Core.Environments.EnvironmentDraft.Create();
        draft.AddComponent(fixture.Definitions.First(d => d.ComponentType == "target"), fixture.Definitions);
        var original = draft.Snapshot with { Name = "Окружение с важными данными · точный план изменения" };
        var volume = new Core.Operations.OwnedResource(Guid.NewGuid(), Guid.NewGuid(), original.Id, original.Components[0].Id, Core.Operations.ResourceKind.Volume, 1,
            Guid.NewGuid(), "eb-selected-persisted-data", System.Collections.Immutable.ImmutableDictionary<string, string>.Empty, "eb-selected-persisted-data",
            new("2026-10-28T00:00:00Z", "local", "local", System.Collections.Immutable.ImmutableDictionary<string, string>.Empty), [], true, Guid.NewGuid(), false);
        var plan = new Core.Operations.OperationPlan(Guid.NewGuid(), original.Id, 1, Core.Environments.CanonicalJson.Hash(original), volume.DataGenerationId,
            Core.Environments.CanonicalJson.Hash(original.Baseline), [volume], [], ["verify-quiescence", "remove-containers", "remove-volumes", "remove-network", "publish-revision"],
            "Удаление данных необратимо. Окружение останется остановленным и неинициализированным. Временные запуски не требуются.", original with { Name = "Новое имя", Revision = 2 })
        { EnvironmentName = original.Name, EngineId = "engine-pinned", Endpoint = "npipe://local-engine", ObservedAt = DateTimeOffset.UtcNow, NextDataGenerationId = Guid.NewGuid() };
        var model = new App.Components.DestructivePlanViewModel(plan);
        var dialog = new App.Components.DestructivePlanDialog { DataContext = model, Left = -10000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        dialog.Show();
        var themes = new App.Shell.ThemeService();
        foreach (bool dark in new[] { false, true })
        {
            themes.Apply(dark); dialog.UpdateLayout(); dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Assert.Same(model, dialog.DataContext);
            Assert.Equal("CancelButton", Assert.IsType<Button>(System.Windows.Input.FocusManager.GetFocusedElement(dialog)).Name);
            var details = Descendants(dialog).OfType<TextBox>().Single();
            Assert.Contains(original.Id.ToString(), details.Text); Assert.Contains(volume.ActualDockerId!, details.Text);
            Assert.True(details.ActualHeight > 200);
            SavePreview((FrameworkElement)dialog.Content, dark ? "destructive-edit-dark" : "destructive-edit-light");
        }
        dialog.Hide();
        dialog.Left = 80; dialog.Top = 80; dialog.ShowActivated = true;
        bool escaped = false;
        dialog.PreviewKeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Escape) { escaped = true; } };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timeout.Tick += (_, _) => dialog.Close();
        dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            dialog.Activate();
            Assert.True(((Button)dialog.FindName("CancelButton")).Focus());
            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        }), DispatcherPriority.ApplicationIdle);
        timeout.Start();
        try { Assert.False(dialog.ShowDialog() == true); Assert.True(escaped, "Escape must cancel the destructive dialog through keyboard input."); }
        finally { timeout.Stop(); }
        Assert.Empty(diagnostics.Errors);
    }

    private static void VerifyDataReset(BindingDiagnostics diagnostics)
    {
        var fixture = new AuthoringUiFixture();
        var draft = Core.Environments.EnvironmentDraft.Create();
        draft.AddComponent(fixture.Definitions.First(d => d.ComponentType == "target"), fixture.Definitions);
        var environment = draft.Snapshot with { Name = "Baseline · восстановление данных и временный запуск" };
        var container = new Core.Operations.OwnedResource(Guid.NewGuid(), Guid.NewGuid(), environment.Id, environment.Components[0].Id, Core.Operations.ResourceKind.Container, 1,
            Guid.NewGuid(), "eb-temporary-component", System.Collections.Immutable.ImmutableDictionary<string, string>.Empty, null, null, [], true, Guid.NewGuid(), true);
        foreach (bool restore in new[] { false, true })
        {
            var plan = new Core.Operations.OperationPlan(Guid.NewGuid(), environment.Id, 1, Core.Environments.CanonicalJson.Hash(environment), Guid.NewGuid(),
                Core.Environments.CanonicalJson.Hash(environment.Baseline), [], [], restore ? ["cleanup", "temporary-start", "baseline", "temporary-stop", "verify-stop"] : ["cleanup", "reset-generation"],
                restore ? App.Features.Environments.EnvironmentCommands.RestoreExplanation : App.Features.Environments.EnvironmentCommands.ClearExplanation)
            {
                EnvironmentName = environment.Name,
                EngineId = "engine-pinned",
                Endpoint = "npipe://local-engine",
                NextDataGenerationId = Guid.NewGuid(),
                TemporaryResources = restore ? [container] : [],
                TemporaryPorts = restore ? [new(environment.Components[0].Id, new("control", 8080, "tcp", null, "127.0.0.1", true))] : []
            };
            var model = new App.Components.DestructivePlanViewModel(plan);
            Assert.DoesNotContain("сохранить новую ревизию", model.Title);
            Assert.Contains("(сохраняется)", model.Details);
            if (restore) { Assert.Contains("Временные порты", model.Details); Assert.Contains("eb-temporary-component", model.Details); }
            var dialog = new App.Components.DestructivePlanDialog { DataContext = model, Left = -10000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
            dialog.Show();
            foreach (bool dark in new[] { false, true })
            {
                new App.Shell.ThemeService().Apply(dark); dialog.UpdateLayout(); dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                SavePreview((FrameworkElement)dialog.Content, (restore ? "restore" : "clear") + (dark ? "-dark" : "-light"));
            }
            dialog.Close();
        }
        Assert.Empty(diagnostics.Errors);
    }

    private static void VerifyInspect(BindingDiagnostics diagnostics)
    {
        var fixture = new InspectVisualFixture();
        var page = new App.Features.Environments.InspectPage { DataContext = fixture };
        var window = new Window { Content = page, Width = 1280, Height = 720, Left = -10000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        var themes = new App.Shell.ThemeService();
        foreach (bool dark in new[] { false, true })
        {
            themes.Apply(dark); window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Assert.Same(fixture, page.DataContext);
            SavePreview(page, dark ? "inspect-dark" : "inspect-light");
        }
        var delta = new App.Features.Environments.InputDeltaDialog { Owner = window, DataContext = "image: unknown → sha256:pinned", ShowActivated = false };
        delta.Show(); delta.UpdateLayout(); delta.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); delta.Close();
        var close = new App.Components.CloseOperationsDialog { Owner = window, DataContext = "Активных операций: 1", ShowActivated = false };
        close.Show(); close.UpdateLayout(); close.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); close.Close();
        Assert.Empty(diagnostics.Errors); window.Close();
        UiReferenceTests.VerifyInspect(fixture);
    }

    private sealed class InspectVisualFixture
    {
        public string Name => "Prepared runtime · partial measurements";
        public string Status => "current · проверено 2026-10-23 · health отдельно от running";
        public string OperationId => "28ca94f2-ae91-488c-9a68-b4187dd1e1fc";
        public string Progress => "Running · readiness · процент неизвестен";
        public bool IsOperationActive => true;
        public string StartDisabledReason => "Операция уже выполняется";
        public string StopDisabledReason => "Операция уже выполняется";
        public string DataResetDisabledReason => "Подтвердите остановку и принадлежность ресурсов перед удалением данных.";
        public string RestoreDisabledReason => "Baseline проверяется до удаления данных; ошибка оставляет работающие компоненты для явного Stop.";
        public string QuiescenceStatus => "Остановка не подтверждена: running";
        public string Error => "";
        public string Measurements => "CPU 125.5% ≈ · memory unavailable · покрытие 1/2";
        public string Aggregate => "CPU ≈125.5% · покрытие 1/2; память — нет данных · покрытие 0/2";
        public string Endpoints => "127.0.0.1:49152 → 8080/tcp";
        public bool LogsPaused { get; set; }
        public string LogStatus => "following · удалено строк 24 · усечено 1";
        public string Logs => "2026-10-23T15:00:00Z event published\n2026-10-23T15:00:01Z [REDACTED]";
        public App.Features.Environments.InspectComponentRow[] Components { get; } = [new(new(Guid.NewGuid(), new string('a', 64), "running", "notConfigured", [], System.Collections.Immutable.ImmutableDictionary<string, Core.Environments.Measurement>.Empty), "Prepared component", "none")];
        public App.Features.Environments.InspectComponentRow? SelectedComponent { get; set; }
        public System.Windows.Input.ICommand StartCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }, () => false);
        public System.Windows.Input.ICommand StopCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }, () => false);
        public System.Windows.Input.ICommand ClearDataCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }, () => false);
        public System.Windows.Input.ICommand RestoreBaselineCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }, () => false);
        public System.Windows.Input.ICommand CancelCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
        public System.Windows.Input.ICommand RefreshCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
        public System.Windows.Input.ICommand ReturnCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
        public System.Windows.Input.ICommand ShowComposeCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
    }

    private static void VerifyAuthoring(BindingDiagnostics diagnostics)
    {
        var fixture = new AuthoringUiFixture();
        var navigation = new App.Shell.NavigationService();
        var themes = new App.Shell.ThemeService();
        var session = fixture.Session(navigation);
        var shell = new App.Shell.ShellViewModel(navigation, themes) { Authoring = session, IsLoading = false };
        shell.Status = "Хранилище доступно · Docker не проверен";
        var window = new App.Shell.MainWindow(shell) { Width = 1280, Height = 720, Left = 40, Top = 40, ShowActivated = true, ShowInTaskbar = false };
        window.Show();
        window.Activate();
        Assert.True(session.List.CreateNewCommand.ExecuteAsync(null).IsCompletedSuccessfully);
        var editor = Assert.IsType<App.Features.Environments.EnvironmentEditorViewModel>(session.Current);
        editor.Name = "Offline authoring · сохранение без Docker";
        editor.SelectedDefinition = editor.Library.First(i => i.Definition.ComponentType == "target");
        editor.AddComponentCommand.Execute(null);
        Assert.Equal(2, editor.Components.Count);
        // Error summary must activate the parameter tab and focus its grid, even while another tab is selected.
        editor.Components[0].Parameters[0].Text = "invalid-integer";
        window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        editor.FocusErrorCommand.Execute(Assert.Single(editor.Errors));
        window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Assert.Contains(Descendants(window).OfType<TabItem>(), item => item.Header as string == "Параметры" && item.IsSelected);
        Assert.True(Descendants(window).OfType<DataGrid>().Any(grid => grid.Tag as string == "parameters" && grid.IsKeyboardFocusWithin),
            "Focused: " + System.Windows.Input.Keyboard.FocusedElement + "; " + string.Join(" | ", Descendants(window).OfType<DataGrid>().Select(grid => $"{grid.Tag}: visible={grid.IsVisible}, enabled={grid.IsEnabled}, focusable={grid.Focusable}")));
        editor.Components[0].Parameters[0].Text = "1000";
        foreach (bool dark in new[] { false, true })
        {
            themes.Apply(dark);
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Assert.Same(editor, session.Current);
            var page = Descendants(window).OfType<App.Features.Environments.EnvironmentEditorPage>().Single();
            foreach (var tabs in Descendants(page).OfType<TabControl>().ToArray())
            {
                for (int i = 0; i < tabs.Items.Count; i++)
                {
                    tabs.SelectedIndex = i; window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                }
                tabs.SelectedIndex = 0;
            }
            window.UpdateLayout();
            SavePreview((FrameworkElement)window.Content, dark ? "authoring-dark" : "authoring-light");
        }
        Assert.True(editor.ShowPreviewCommand.ExecuteAsync(null).IsCompletedSuccessfully);
        window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        var preview = Assert.IsType<App.Features.Environments.ComposePreviewViewModel>(session.Current);
        Assert.Contains("127.0.0.1", preview.Content);
        SavePreview((FrameworkElement)window.Content, "authoring-preview");
        Assert.True(preview.GoBackCommand.ExecuteAsync(null).IsCompletedSuccessfully);
        window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Assert.Same(editor, session.Current);
        Assert.Empty(diagnostics.Errors);
        window.Close();
    }

    private static void VerifyShell(BindingDiagnostics diagnostics)
    {
        var navigation = new App.Shell.NavigationService();
        var themes = new App.Shell.ThemeService();
        var model = new App.Shell.ShellViewModel(navigation, themes);
        model.IsLoading = false;
        model.Status = "Хранилище доступно · Docker не проверен";
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
            var context = Descendants(window).OfType<App.Components.ContextPanel>().Single();
            context.IsExpanded = false;
            window.UpdateLayout();
            Assert.Equal(48, context.ActualWidth);
            context.IsExpanded = true;
            window.UpdateLayout();
            Assert.Equal(320, context.ActualWidth);
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
        DpiScale dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(outputDirectory, $"resources-{theme.ToLowerInvariant()}.png"));
        encoder.Save(stream);
        File.AppendAllText(Path.Combine(outputDirectory, "resource-dpi.txt"), $"{theme}: {root.ActualWidth}x{root.ActualHeight} DIP; {dpi.PixelsPerInchX}x{dpi.PixelsPerInchY} DPI{Environment.NewLine}");
    }

    private sealed record FixtureRow(string Name, string Status);
    private sealed class ResourceApplication : Application
    {
        public void InitializeComponent()
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Load the production dictionaries without executing App.OnStartup against a user's data.
            Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = ApplicationTheme.Light });
            Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            foreach (string name in new[] { "Light", "Typography", "Components" })
            { Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/EnvironmentBuilder.App;component/Themes/{name}.xaml") }); }
        }
    }

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
