using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.App.Features.Settings;
using EnvironmentBuilder.App.Shell;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.UiTests;

internal static class ApplicationSettingsTests
{
    public static void Verify()
    {
        string directory = Path.Combine(Path.GetTempPath(), "EnvironmentBuilder-settings-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var themes = new ThemeService();
        var shell = new ShellViewModel(new NavigationService(), themes) { IsLoading = false };
        shell.ApplicationSettings.Initialize(directory);
        var window = new MainWindow(shell) { Width = 960, Height = 600, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        try
        {
            Assert.NotNull(window.Icon);
            Assert.True(shell.NavigateCommand.ExecuteAsync("settings").IsCompletedSuccessfully);
            UiReferenceTests.Layout(window);
            Assert.True(shell.ShowSettings); Assert.False(shell.ShowServices);
            Assert.Single(UiReferenceTests.Descendants(window).OfType<ApplicationSettingsPage>(), page => page.IsVisible);
            var theme = UiReferenceTests.Descendants(window).OfType<ComboBox>().Single(combo => AutomationProperties.GetAutomationId(combo) == "ApplicationTheme");
            theme.SelectedValue = "dark";
            shell.ApplicationSettings.StartPage = "services";
            shell.ApplicationSettings.ShowDescriptions = false;
            UiReferenceTests.Layout(window);
            Assert.True(themes.IsDark);
            Assert.False((bool)Application.Current.FindResource("Preference.ShowDescriptions"));
            var reopened = new ApplicationSettingsViewModel(themes);
            reopened.Initialize(directory);
            Assert.Equal("dark", reopened.Theme); Assert.Equal("services", reopened.StartPage); Assert.False(reopened.ShowDescriptions);
            Assert.Empty(reopened.Error);
            UiReferenceTests.Capture(window, "application-settings-dark-960");
            theme.SelectedValue = "light"; UiReferenceTests.Layout(window);
            Assert.False(themes.IsDark);
            UiReferenceTests.Capture(window, "application-settings-light-960");
            // Empty descriptions must not reserve a second line or shift the name upwards.
            var row = new EnvironmentRow { DataContext = new EnvironmentRowViewModel(new EnvironmentListItem(Guid.NewGuid(), "One line", 1, null, null, false)) };
            window.Content = new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; UiReferenceTests.Layout(window);
            var name = UiReferenceTests.Descendants(row).OfType<TextBlock>().Single(text => text.Text == "One line");
            Rect bounds = name.TransformToAncestor(row).TransformBounds(new Rect(name.RenderSize));
            Assert.InRange(bounds.Top + bounds.Height / 2, 27, 29);
            var document = EnvironmentDraft.Create().Snapshot with { Name = "With description", Description = "Visible description" };
            row.DataContext = new EnvironmentRowViewModel(new EnvironmentListItem(document.Id, document.Name, 1, document, null, false));
            UiReferenceTests.Layout(window);
            var description = UiReferenceTests.Descendants(row).OfType<TextBlock>().Single(text => text.Text == document.Description);
            Assert.Equal(Visibility.Collapsed, description.Visibility);
            shell.ApplicationSettings.ShowDescriptions = true;
            UiReferenceTests.Layout(window);
            Assert.Equal(Visibility.Visible, description.Visibility);
            Assert.True(shell.NavigateCommand.ExecuteAsync("services").IsCompletedSuccessfully);
            Assert.True(shell.ShowServices); Assert.False(shell.ShowSettings);
        }
        finally { window.Close(); File.Delete(Path.Combine(directory, "preferences.json")); Directory.Delete(directory); }
    }
}
