using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.App.Features.Settings;
using EnvironmentBuilder.App.Shell;
using EnvironmentBuilder.Core.Abstractions;

namespace EnvironmentBuilder.UiTests;

internal static class SettingsSmokeTests
{
    public static void Verify()
    {
        var fixture = new AuthoringUiFixture();
        var themes = new ThemeService(); var navigation = new NavigationService();
        using var settings = new SettingsViewModel(fixture, fixture, fixture, new Catalog());
        Assert.True(settings.LoadAsync(CancellationToken.None).IsCompletedSuccessfully);
        var shell = new ShellViewModel(navigation, themes) { IsLoading = false, Settings = settings, Authoring = fixture.Session(navigation) };
        var window = new MainWindow(shell) { Width = 1280, Height = 720, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        var page = Descendants(window).OfType<SettingsPage>().Single();
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        try
        {
            Assert.True(shell.NavigateCommand.ExecuteAsync("services").IsCompletedSuccessfully);
            settings.AddTargetCommand.Execute(null);
            UiReferenceTests.Layout(window);
            var name = Descendants(window).OfType<TextBox>().Single(text => AutomationProperties.GetAutomationId(text) == "ServiceDefinitionName" && text.IsVisible);
            name.Text = "My configured API";
            var image = Descendants(window).OfType<TextBox>().Single(text => AutomationProperties.GetName(text) == "Образ компонента" && text.IsVisible);
            image.Text = "nginx:stable-alpine";
            var row = settings.SelectedDefinition!;
            Assert.Equal("My configured API", row.Name);
            Assert.Equal("nginx:stable-alpine", row.Editor.ImageReference);
            settings.AddInfrastructureCommand.Execute(null);
            var infrastructure = settings.SelectedDefinition!;
            infrastructure.Name = "Redis"; infrastructure.Editor.ImageReference = "redis";
            var timeout = Stopwatch.StartNew();
            while (!infrastructure.Editor.SearchTask.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(5))
            { UiReferenceTests.Layout(window); Thread.Sleep(10); }
            Assert.True(infrastructure.Editor.SearchTask.IsCompletedSuccessfully);
            UiReferenceTests.Layout(window);
            Assert.True(infrastructure.Editor.ShowRepositories, $"Image: {infrastructure.Editor.ImageReference}; selected: {infrastructure.Editor.SelectedRepository?.Name}; results: {infrastructure.Editor.Repositories.Count}; status: {infrastructure.Editor.HubStatus}");
            var results = Descendants(page).OfType<ListBox>().Single(list => AutomationProperties.GetAutomationId(list) == "DockerHubResults");
            results.BringIntoView(); UiReferenceTests.Layout(window); UiReferenceTests.Capture(window, "dockerhub-search");
            Assert.True(results.IsVisible); results.SelectedIndex = 0;
            UiReferenceTests.Layout(window);
            Assert.True(infrastructure.Editor.SelectionTask.IsCompletedSuccessfully);
            var tags = Descendants(page).OfType<ComboBox>().Single(combo => AutomationProperties.GetAutomationId(combo) == "DockerHubTags");
            tags.BringIntoView(); UiReferenceTests.Layout(window);
            tags.SelectedItem = "7.2"; UiReferenceTests.Layout(window);
            Assert.Equal("redis:7.2", infrastructure.Editor.ImageReference);
            infrastructure.Editor.RefreshVersionsCommand.Execute(null); UiReferenceTests.Layout(window);
            Assert.Equal("redis:7.2", infrastructure.Editor.ImageReference);
            infrastructure.Editor.ImageReference = "redis:custom"; UiReferenceTests.Layout(window);
            Assert.Equal("redis:custom", infrastructure.Editor.ImageReference);
            infrastructure.Editor.SelectedTag = "7.2"; UiReferenceTests.Layout(window);
            Assert.Equal("redis:7.2", infrastructure.Editor.ImageReference);
            timeout.Restart();
            while (!infrastructure.Editor.SearchTask.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(5))
            { UiReferenceTests.Layout(window); Thread.Sleep(10); }
            infrastructure.Editor.RefreshVersionsCommand.Execute(null); UiReferenceTests.Layout(window);
            foreach (bool dark in new[] { false, true })
            {
                themes.Apply(dark); UiReferenceTests.Layout(window);
                UiReferenceTests.Capture(window, dark ? "dockerhub-versions-dark" : "dockerhub-versions-light");
            }
            settings.SelectedDefinition = row;
            row.AddDependencyCommand.Execute(null); UiReferenceTests.Layout(window);
            var dependency = Descendants(page).OfType<ComboBox>().Single(combo => AutomationProperties.GetName(combo) == "Компонент зависимости");
            dependency.BringIntoView(); UiReferenceTests.Layout(window);
            dependency.SelectedItem = infrastructure; UiReferenceTests.Layout(window);
            Assert.Same(infrastructure, row.Dependencies[0].SelectedService);
            var required = Descendants(page).OfType<CheckBox>().Single(check => check.Content as string == "Обязательная");
            required.IsChecked = false; UiReferenceTests.Layout(window);
            Assert.False(row.Dependencies[0].IsRequired);
            dependency.BringIntoView(); UiReferenceTests.Layout(window);
            foreach (bool dark in new[] { false, true })
            {
                themes.Apply(dark); UiReferenceTests.Layout(window);
                UiReferenceTests.Capture(window, dark ? "settings-workflow-dark" : "settings-workflow-light");
            }
            Assert.True(settings.SaveCommand.ExecuteAsync(null).IsCompletedSuccessfully);
            Assert.Empty(settings.Error);
            Assert.True(Assert.Single(row.Definition.DependencyRequirements).Optional);
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
        finally { window.Close(); SynchronizationContext.SetSynchronizationContext(previousContext); }
    }
    private sealed class Catalog : IDockerHubCatalog
    {
        public Task<IReadOnlyList<DockerHubRepository>> SearchAsync(string query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DockerHubRepository>>([new("redis", "Redis cache", true)]);
        public Task<DockerHubRepository> GetRepositoryAsync(string name, CancellationToken cancellationToken) => Task.FromResult(new DockerHubRepository(name, "Redis cache", true));
        public Task<DockerHubTagPage> GetTagsAsync(string name, int page, CancellationToken cancellationToken) => Task.FromResult(new DockerHubTagPage(["latest", "7.2"], false));
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
