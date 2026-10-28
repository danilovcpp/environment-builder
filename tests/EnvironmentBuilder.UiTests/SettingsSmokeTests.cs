using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.App.Features.Settings;
using EnvironmentBuilder.App.Shell;

namespace EnvironmentBuilder.UiTests;

internal static class SettingsSmokeTests
{
    public static void Verify()
    {
        var fixture = new AuthoringUiFixture();
        var themes = new ThemeService(); var navigation = new NavigationService();
        var settings = new SettingsViewModel(themes, fixture, fixture, fixture);
        Assert.True(settings.LoadAsync(CancellationToken.None).IsCompletedSuccessfully);
        var shell = new ShellViewModel(navigation, themes) { IsLoading = false, Settings = settings, Authoring = fixture.Session(navigation) };
        var window = new MainWindow(shell) { Width = 1280, Height = 720, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        try
        {
            Assert.True(shell.NavigateCommand.ExecuteAsync("settings").IsCompletedSuccessfully);
            settings.AddTargetCommand.Execute(null);
            UiReferenceTests.Layout(window);
            var name = Descendants(window).OfType<TextBox>().Single(text => AutomationProperties.GetAutomationId(text) == "ServiceDefinitionName" && text.IsVisible);
            name.Text = "My configured API";
            var image = Descendants(window).OfType<TextBox>().Single(text => AutomationProperties.GetName(text) == "Образ компонента" && text.IsVisible);
            image.Text = "nginx:stable-alpine";
            var row = settings.SelectedDefinition!;
            Assert.Equal("My configured API", row.Name);
            Assert.Equal("nginx:stable-alpine", row.Editor.ImageReference);
            foreach (bool dark in new[] { false, true })
            {
                themes.Apply(dark); UiReferenceTests.Layout(window);
                UiReferenceTests.Capture(window, dark ? "settings-workflow-dark" : "settings-workflow-light");
            }
            Assert.True(settings.SaveCommand.ExecuteAsync(null).IsCompletedSuccessfully);
            Assert.Empty(settings.Error);
            Assert.Contains(fixture.Definitions, definition => definition.Name == "My configured API");
            Assert.True(shell.NavigateCommand.ExecuteAsync("environments").IsCompletedSuccessfully);
            Assert.True(shell.Authoring!.List.CreateNewCommand.ExecuteAsync(null).IsCompletedSuccessfully);
            var editor = Assert.IsType<EnvironmentEditorViewModel>(shell.Authoring.Current);
            editor.Name = "My configured environment";
            editor.AddDefinitionCommand.Execute(editor.Library.Single(d => d.Definition.Id == row.Definition.Id));
            Assert.Single(editor.Components).Variables.Add(new ValueEditor { Key = "MODE", Text = "test" });
            UiReferenceTests.Layout(window);
            UiReferenceTests.Capture(window, "settings-created-environment");
            bool started = false;
            editor.StartSaved = saved =>
            {
                Assert.Contains(fixture.Items, item => item.Id == saved.Id);
                Assert.Equal("test", Assert.Single(saved.Components).Environment["MODE"].Literal);
                started = true; return Task.CompletedTask;
            };
            Assert.True(editor.SaveAndStartCommand.ExecuteAsync(null).IsCompletedSuccessfully);
            Assert.True(started);
        }
        finally { window.Close(); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) { yield return descendant; }
        }
    }
}
