using EnvironmentBuilder.App.Composition;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.App.Features.Settings;
using EnvironmentBuilder.App.Shell;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Infrastructure.Docker;
using EnvironmentBuilder.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace EnvironmentBuilder.IntegrationTests.Docker;

// Explicit opt-in: runs the production adapters against local Docker in a disposable store.
public sealed class SettingsDockerWorkflowTests(ITestOutputHelper output)
{
    public static bool Enabled => Environment.GetEnvironmentVariable("ENVIRONMENTBUILDER_LIVE_DOCKER") == "1";

    [Fact(Skip = "Set ENVIRONMENTBUILDER_LIVE_DOCKER=1 to run against local Docker.", SkipUnless = nameof(Enabled), Timeout = 300000)]
    [Trait("Category", "DockerLive")]
    public async Task Settings_to_saved_environment_to_running_image_and_build_services()
    {
        using var isolated = IsolatedTestStore.Create();
        var navigation = new NavigationService(); var themes = new ThemeService();
        var shell = new ShellViewModel(navigation, themes);
        await using var services = await AppServices.CreateAsync(shell, navigation, themes, isolated.DataDirectory);
        var provider = services.Provider;
        var settings = provider.GetRequiredService<SettingsViewModel>();
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        settings.AddInfrastructureCommand.Execute(null);
        settings.SelectedDefinition!.Name = "Redis";
        settings.SelectedDefinition.Editor.ImageReference = "redis:7.2-alpine";
        settings.AddTargetCommand.Execute(null);
        var image = settings.SelectedDefinition!;
        image.Name = "Image service"; image.Editor.ImageReference = "nginx:stable-alpine";
        settings.AddTargetCommand.Execute(null);
        var build = settings.SelectedDefinition!;
        build.Name = "Built service"; build.Editor.SourceKind = "build";
        string source = isolated.GetPath("source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "Dockerfile"), "FROM nginx:stable-alpine\nCOPY index.html /usr/share/nginx/html/index.html\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(source, "index.html"), "configured from settings", TestContext.Current.CancellationToken);
        build.Editor.LocalFolder = source;
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Empty(settings.Error);

        var library = provider.GetRequiredService<IComponentLibrary>();
        var editor = new EnvironmentEditorViewModel(EnvironmentDraft.Create(), provider.GetRequiredService<CreateEnvironmentService>(), library,
            provider.GetRequiredService<IAuthoringInteractions>(), provider.GetRequiredService<IInitializationDocumentReader>(),
            provider.GetRequiredService<ILocalPathMappings>(), provider.GetRequiredService<IInputResolver>(), provider.GetRequiredService<ISeedPackStore>());
        await editor.LoadLibraryAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, editor.Library.Count);
        editor.Name = "Settings workflow acceptance";
        foreach (var definition in editor.Library) { editor.AddDefinitionCommand.Execute(definition); }
        var imageInstance = editor.Components.Single(c => c.Definition.Id == image.Definition.Id);
        imageInstance.Variables.Add(new ValueEditor { Key = "CHECK_VALUE", Text = "environment-only" });
        imageInstance.Ports.Add(new PortEditor { Id = "http", ContainerPort = "80" });
        editor.Components.Single(c => c.Definition.Id == build.Definition.Id).Ports.Add(new PortEditor { Id = "http", ContainerPort = "80" });
        IsolatedEnvironment? saved = null;
        OperationHandle? handle = null;
        editor.StartSaved = async environment =>
        {
            saved = environment;
            handle = provider.GetRequiredService<StartEnvironmentService>().Start(environment, null, (_, _, _) => Task.FromResult(true));
            await handle.Completion;
        };
        try
        {
            await editor.SaveAndStartCommand.ExecuteAsync(null);
            Assert.Empty(editor.ActionError);
            Assert.NotNull(saved); Assert.NotNull(handle);
            output.WriteLine($"Start: {handle.Current.State} · {handle.Current.ResultCode}");
            Assert.True(handle.Current.State == OperationState.Succeeded, $"{handle.Current.State}: {handle.Current.ResultCode}");
            var actual = await provider.GetRequiredService<IEnvironmentObservationSource>().ReadAsync(saved, Guid.NewGuid(), null, TestContext.Current.CancellationToken);
            Assert.Equal("current", actual.Freshness);
            Assert.Equal(3, actual.Components.Length);
            Assert.All(actual.Components, component => Assert.Equal("running", component.Status));
            Assert.All(actual.Components, component => Assert.Equal("expected", component.Drift));
            foreach (var component in actual.Components)
            { output.WriteLine($"{saved.Components.Single(c => c.Id == component.ComponentId).DefinitionSnapshot.Name}: {component.Status} · {component.ContainerId}"); }

            var built = actual.Components.Single(c => c.ComponentId == saved.Components.Single(s => s.DefinitionSnapshot.Id == build.Definition.Id).Id);
            int port = Assert.Single(built.Ports).HostPort;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            string? body = null;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try { body = await http.GetStringAsync($"http://127.0.0.1:{port}/", TestContext.Current.CancellationToken); break; }
                catch (HttpRequestException) when (attempt < 19) { await Task.Delay(250, TestContext.Current.CancellationToken); }
            }
            Assert.Equal("configured from settings", body);
            var imageActual = actual.Components.Single(c => c.ComponentId == saved.Components.Single(s => s.DefinitionSnapshot.Id == image.Definition.Id).Id);
            var docker = provider.GetRequiredService<DockerProcessFactory>().Create(saved);
            var inspected = await docker.RunAsync(["inspect", "--format", "{{json .Config.Env}}", imageActual.ContainerId!], TestContext.Current.CancellationToken);
            Assert.Equal(0, inspected.ExitCode);
            Assert.Contains("CHECK_VALUE=environment-only", inspected.Output);
            Assert.DoesNotContain("CHECK_VALUE=library-default", inspected.Output);
            output.WriteLine("HTTP and environment override verified.");
        }
        finally
        {
            if (saved is not null)
            {
                var deleted = await provider.GetRequiredService<DeleteEnvironmentService>().DeleteAsync(saved,
                    (plan, _) => Task.FromResult<ConfirmationRecord?>(new(plan.Hash, DateTimeOffset.UtcNow)), CancellationToken.None);
                output.WriteLine($"Cleanup: {deleted.Deleted} · {deleted.Code}");
                if (!deleted.Deleted) { isolated.RetainForReconciliation(); }
                Assert.True(deleted.Deleted, $"Cleanup requires reconciliation: {deleted.Code}; store: {isolated.DataDirectory}");
            }
        }
    }
}
