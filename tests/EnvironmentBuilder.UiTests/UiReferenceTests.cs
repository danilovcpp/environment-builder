using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EnvironmentBuilder.App.Components;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.App.Features.SeedPacks;
using EnvironmentBuilder.App.Shell;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.UiTests;

// Runs on the shared resource test's STA: WPF permits one Application per process.
internal static class UiReferenceTests
{
    public static void Verify()
    {
        var fixture = new UiReferenceFixture();
        Assert.True(fixture.Shell.Authoring!.List.LoadAsync(CancellationToken.None).IsCompletedSuccessfully);
        var window = new MainWindow(fixture.Shell) { Width = 1280, Height = 720, Left = 40, Top = 40, ShowInTaskbar = false };
        window.Show();
        try
        {
            foreach (var size in new[] { new Size(1280, 720), new Size(1672, 941) })
            {
                window.Width = size.Width; window.Height = size.Height;
                foreach (bool dark in new[] { false, true })
                {
                    string suffix = $"-{size.Width}-" + (dark ? "dark" : "light");
                    fixture.Themes.Apply(dark);
                    VerifyContrast();
                    Assert.True(fixture.Shell.NavigateCommand.ExecuteAsync("environments").IsCompletedSuccessfully);
                    fixture.Shell.Authoring.List.SelectSaved(fixture.Store.Items[0].Id);
                    Layout(window);
                    Capture(window, "environments" + suffix);
                    var row = Descendants(window).OfType<EnvironmentRow>().First();
                    Assert.InRange(row.ActualHeight, 38, 56);
                    var selected = fixture.Shell.Authoring.List.SelectedItem!;
                    selected.IsExpanded = true; Layout(window); Capture(window, "environments-expanded" + suffix); selected.IsExpanded = false;
                    var rows = Descendants(window).OfType<ListBox>().Single(list => list.Name == "Rows");
                    var scroll = Descendants(rows).OfType<ScrollViewer>().First();
                    scroll.ScrollToVerticalOffset(76); Layout(window);
                    double offset = fixture.Shell.Authoring.List.ScrollOffset;
                    fixture.Themes.Apply(!dark); Layout(window);
                    Assert.Same(selected, fixture.Shell.Authoring.List.SelectedItem);
                    Assert.Equal(offset, fixture.Shell.Authoring.List.ScrollOffset);
                    fixture.Themes.Apply(dark);
                    Assert.Equal(12, fixture.Shell.Authoring.List.VisibleItems.Count);
                    fixture.Shell.Authoring.List.Search = "not-found"; Layout(window); Capture(window, "environments-filter-empty" + suffix); fixture.Shell.Authoring.List.Search = "";
                    foreach (string route in new[] { "seedpacks", "settings", "diagnostics" })
                    {
                        Assert.True(fixture.Shell.NavigateCommand.ExecuteAsync(route).IsCompletedSuccessfully);
                        Layout(window); Capture(window, route + suffix);
                    }
                    Assert.True(fixture.Shell.NavigateCommand.ExecuteAsync("environments").IsCompletedSuccessfully);
                    Assert.True(fixture.Shell.Authoring.List.CreateNewCommand.ExecuteAsync(null).IsCompletedSuccessfully);
                    var editor = Assert.IsType<EnvironmentEditorViewModel>(fixture.Shell.Authoring.Current);
                    editor.Name = fixture.Store.Items[0].Name;
                    editor.SelectedDefinition = editor.Library.First(d => d.Definition.ComponentType == "target");
                    editor.AddComponentCommand.Execute(null);
                    Layout(window); Capture(window, "editor" + suffix);
                    var context = Descendants(window).OfType<ContextPanel>().Single();
                    var input = Descendants(window).OfType<TextBox>().Single(text => text.Name == "NameInput");
                    Assert.True(input.TransformToAncestor(window).Transform(new Point(input.ActualWidth, 0)).X <= window.ActualWidth - context.ActualWidth,
                        "Editor fields must fit before the context panel; long names scroll inside the input.");
                    context.IsExpanded = false; Layout(window); Assert.Equal(48, context.ActualWidth);
                    Capture(window, "editor-context-collapsed" + suffix);
                    context.IsExpanded = true;
                    Assert.True(editor.ShowPreviewCommand.ExecuteAsync(null).IsCompletedSuccessfully);
                    Layout(window); Capture(window, "compose" + suffix);
                    var preview = Assert.IsType<ComposePreviewViewModel>(fixture.Shell.Authoring.Current);
                    Assert.True(preview.GoBackCommand.ExecuteAsync(null).IsCompletedSuccessfully);
                    Assert.Same(editor, fixture.Shell.Authoring.Current);
                    fixture.Themes.Apply(!dark); Layout(window); Assert.Same(editor, fixture.Shell.Authoring.Current); Assert.Equal(fixture.Store.Items[0].Name, editor.Name);
                    fixture.Themes.Apply(dark);
                    fixture.Shell.Authoring.DiscardScreen();
                    VerifyDialogs(window, suffix);
                }
            }
            VerifyStates(fixture, window);
        }
        finally { window.Close(); }
    }

    private static void VerifyStates(UiReferenceFixture fixture, Window window)
    {
        window.Width = 1280; window.Height = 720;
        Assert.True(fixture.Shell.NavigateCommand.ExecuteAsync("environments").IsCompletedSuccessfully);
        var list = fixture.Shell.Authoring!.List;
        foreach (bool dark in new[] { false, true })
        {
            fixture.Themes.Apply(dark); string suffix = dark ? "-dark" : "-light";
            list.IsLoading = true; Layout(window); Capture(window, "loading" + suffix); list.IsLoading = false;
            list.Error = "Не удалось прочитать хранилище. Повторите загрузку или откройте диагностику."; Layout(window); Capture(window, "error" + suffix); list.Error = "";
            list.StatusFilter = "needsAttention"; Layout(window); Capture(window, "partial-operation" + suffix);
            list.StatusFilter = "unreadable"; Layout(window); Capture(window, "unreadable-document" + suffix); list.StatusFilter = "all";
        }
        fixture.Store.Items.Clear(); Assert.True(list.LoadAsync(CancellationToken.None).IsCompletedSuccessfully);
        foreach (bool dark in new[] { false, true }) { fixture.Themes.Apply(dark); Layout(window); Capture(window, dark ? "empty-dark" : "empty-light"); }
        foreach (bool dark in new[] { false, true })
        {
            fixture.Themes.Apply(dark); string suffix = dark ? "-dark" : "-light";
            var pending = new TaskCompletionSource<IReadOnlyList<SeedPack>>();
            fixture.Shell.SeedPacks = new SeedPackLibraryViewModel(_ => pending.Task);
            Task loading = fixture.Shell.SeedPacks.LoadAsync(CancellationToken.None);
            Assert.True(fixture.Shell.NavigateCommand.ExecuteAsync("seedpacks").IsCompletedSuccessfully);
            Layout(window); Capture(window, "seedpacks-loading" + suffix);
            Assert.True(fixture.Shell.SeedPacks.IsLoading);
            pending.SetResult([]); Layout(window); Assert.True(loading.IsCompletedSuccessfully);
            Capture(window, "seedpacks-empty" + suffix);
            fixture.Shell.SeedPacks = new SeedPackLibraryViewModel(_ => throw new IOException("fixture failure"));
            Assert.True(fixture.Shell.SeedPacks.LoadAsync(CancellationToken.None).IsCompletedSuccessfully);
            Layout(window); Capture(window, "seedpacks-error" + suffix);
        }
    }
    internal static void VerifyInspect(object fixture)
    {
        var navigation = new NavigationService();
        var themes = new ThemeService();
        var shell = new ShellViewModel(navigation, themes) { IsLoading = false, Status = "Визуальный fixture · частичные измерения" };
        navigation.Navigate(new NavigationAddress("inspect"));
        var window = new MainWindow(shell) { Left = 40, Top = 40, ShowInTaskbar = false };
        var page = new InspectPage { DataContext = fixture };
        ((ContentControl)window.FindName("EnvironmentContent")).Content = page;
        window.Show(); Layout(window);
        var actions = Descendants(window).OfType<ContentControl>().Single(control => control.Tag as string == "HeaderActions");
        actions.ContentTemplate = (DataTemplate)actions.Resources[new DataTemplateKey(typeof(InspectViewModel))];
        actions.Content = fixture;
        try
        {
            var context = Descendants(window).OfType<ContextPanel>().Single();
            context.Text = "Prepared runtime · частичные измерения\n\nCPU ≈125.5% · покрытие 1/2\nПамять: нет данных · покрытие 0/2\n\nreadiness: выполняется, процент неизвестен";
            foreach (var size in new[] { new Size(1280, 720), new Size(1672, 941) })
            {
                window.Width = size.Width; window.Height = size.Height;
                foreach (bool dark in new[] { false, true })
                {
                    themes.Apply(dark); Layout(window);
                    string suffix = $"-{size.Width}-" + (dark ? "dark" : "light");
                    Capture(window, "inspect-shell" + suffix);
                    var tabs = Descendants(page).OfType<TabControl>().Single();
                    foreach (TabItem tab in tabs.Items)
                    {
                        tabs.SelectedItem = tab; Layout(window);
                        Capture(window, "inspect-" + tabs.SelectedIndex + suffix);
                    }
                    tabs.SelectedIndex = 0;
                    Assert.Same(fixture, page.DataContext);
                }
            }
        }
        finally { window.Close(); }
    }

    private static void VerifyContrast()
    {
        foreach (string background in new[] { "Panel", "Canvas", "PanelMuted", "Selected", "Hover", "ContextPanel", "Sidebar", "Topbar" })
        {
            AssertContrast("TextPrimary", background, 4.5); AssertContrast("TextSecondary", background, 4.5);
        }
        foreach (string semantic in new[] { "Success", "Info", "Warning", "Error", "Neutral", "Purple" }) { AssertContrast(semantic + "Foreground", semantic + "Background", 4.5); }
        AssertContrast("OnAccent", "Accent", 4.5); AssertContrast("IconPrimary", "Panel", 3); AssertContrast("FocusRing", "Panel", 3);
    }
    private static void AssertContrast(string foreground, string background, double minimum)
    {
        double first = Luminance(((SolidColorBrush)Application.Current.FindResource("Brush." + foreground)).Color);
        double second = Luminance(((SolidColorBrush)Application.Current.FindResource("Brush." + background)).Color);
        double ratio = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
        Assert.True(ratio >= minimum, $"{foreground}/{background}: {ratio:N2}:1 < {minimum}:1");
    }
    private static double Luminance(Color color)
    {
        static double Linear(byte value) { double v = value / 255d; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }
    private static void VerifyDialogs(Window owner, string suffix)
    {
        Window[] dialogs = [new DirtyDraftDialog(), new CloseOperationsDialog { DataContext = "Активных операций: 1 · ожидается запись результата" }, new InputDeltaDialog { DataContext = "image: unknown → sha256:pinned\nТребуется подтверждение актуальных входов." }];
        foreach (Window dialog in dialogs)
        {
            dialog.Owner = owner; dialog.ShowInTaskbar = false; dialog.Show(); Layout(dialog); Capture(dialog, dialog.GetType().Name + suffix); dialog.Close();
        }
    }

    internal static void Layout(Window window)
    { window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); }
    internal static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) { yield return descendant; }
        }
    }
    internal static void Capture(Window window, string name)
    {
        string? output = Environment.GetEnvironmentVariable("ENVIRONMENTBUILDER_UI_ARTIFACTS");
        if (string.IsNullOrEmpty(output)) { return; }
        Directory.CreateDirectory(output);
        var root = (FrameworkElement)window.Content;
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
        File.AppendAllText(Path.Combine(output, "actual-dpi.txt"), $"{name}: {window.ActualWidth}x{window.ActualHeight} DIP; DPI {dpi.PixelsPerInchX}x{dpi.PixelsPerInchY}{Environment.NewLine}");
    }
}
