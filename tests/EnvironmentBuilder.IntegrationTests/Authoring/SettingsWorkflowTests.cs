using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.App.Features.Settings;
using EnvironmentBuilder.App.Shell;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Authoring;

public sealed class SettingsWorkflowTests
{
    [Fact]
    public async Task Settings_feed_the_creation_catalog_and_environment_values_are_saved_as_independent_snapshots()
    {
        await using var fixture = await FoundationStore.CreateAsync();
        var library = new AuthoringLibrary(new ComponentLibraryStore(fixture.Worker, fixture.Reader), fixture.Reader);
        var mappings = new LocalPathMappings(fixture.Worker, fixture.Clock);
        var settings = new SettingsViewModel(library, mappings);
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        settings.AddTargetCommand.Execute(null);
        var target = settings.SelectedDefinition!;
        target.Name = "My API"; target.Editor.ImageReference = "nginx:stable-alpine";
        target.Icon = "Globe"; target.Color = "#123ABC";
        settings.AddInfrastructureCommand.Execute(null);
        var infrastructure = settings.SelectedDefinition!;
        infrastructure.Name = "Redis"; infrastructure.Editor.ImageReference = "redis:7.2-alpine";
        settings.AddInfrastructureCommand.Execute(null);
        var hidden = settings.SelectedDefinition!;
        hidden.Name = "Disabled"; hidden.Editor.ImageReference = "postgres:17-alpine"; hidden.Enabled = false;
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Empty(settings.Error);

        var reopenedLibrary = new AuthoringLibrary(new ComponentLibraryStore(fixture.Worker, fixture.Reader), fixture.Reader);
        var reopened = new SettingsViewModel(reopenedLibrary, mappings);
        await reopened.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, reopened.Definitions.Count);
        Assert.False(reopened.Definitions.Single(d => d.Name == "Disabled").Enabled);
        var editor = Editor(fixture, reopenedLibrary, mappings);
        await editor.LoadLibraryAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, editor.Library.Count);
        Assert.DoesNotContain(editor.Library, d => d.Definition.Id == hidden.Definition.Id);
        editor.Name = "My environment";
        editor.AddDefinitionCommand.Execute(editor.Library.Single(d => d.Definition.Id == target.Definition.Id));
        editor.AddDefinitionCommand.Execute(editor.Library.Single(d => d.Definition.Id == infrastructure.Definition.Id));
        var targetInstance = editor.Components.Single(c => c.Definition.Id == target.Definition.Id);
        targetInstance.Variables.Add(new ValueEditor { Key = "MODE", Text = "environment-only" });
        targetInstance.Ports.Add(new PortEditor { Id = "http", ContainerPort = "80", HostPort = "8081" });
        Assert.True(await editor.TrySaveAsync(TestContext.Current.CancellationToken));
        var saved = Assert.Single(await fixture.Environments.ListAsync(TestContext.Current.CancellationToken)).Document!;
        Assert.Equal("environment-only", saved.Components.Single(c => c.DefinitionSnapshot.Id == target.Definition.Id).Environment["MODE"].Literal);
        var libraryDefinition = (await reopenedLibrary.ReadEntriesAsync(TestContext.Current.CancellationToken)).Single(d => d.Definition.Id == target.Definition.Id).Definition;
        Assert.Empty(libraryDefinition.DefaultEnvironment); Assert.Empty(libraryDefinition.DefaultPorts);
        Assert.Equal("Globe", libraryDefinition.Icon); Assert.Equal("#123ABC", libraryDefinition.Color);
        Assert.Equal(8081, saved.Components.Single(c => c.DefinitionSnapshot.Id == target.Definition.Id).Ports.Single().HostPort);
        var independent = Editor(fixture, reopenedLibrary, mappings);
        await independent.LoadLibraryAsync(TestContext.Current.CancellationToken);
        independent.AddDefinitionCommand.Execute(independent.Library.Single(d => d.Definition.Id == target.Definition.Id));
        var independentInstance = Assert.Single(independent.Components);
        Assert.Empty(independentInstance.Variables); Assert.Empty(independentInstance.Ports);
        independent.Name = "Another environment";
        independentInstance.Variables.Add(new ValueEditor { Key = "MODE", Text = "second-environment" });
        independentInstance.Ports.Add(new PortEditor { Id = "http", ContainerPort = "80", HostPort = "8082" });
        Assert.True(await independent.TrySaveAsync(TestContext.Current.CancellationToken));
        Assert.Equal("environment-only", saved.Components.Single(c => c.DefinitionSnapshot.Id == target.Definition.Id).Environment["MODE"].Literal);

        reopened.Definitions.Single(d => d.Definition.Id == target.Definition.Id).Editor.ImageReference = "nginx:other-tag";
        await reopened.SaveCommand.ExecuteAsync(null);
        Assert.Empty(reopened.Error);
        var another = Editor(fixture, reopenedLibrary, mappings);
        await another.LoadLibraryAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, another.Library.Count);
        Assert.Equal("nginx:other-tag", Assert.IsType<ImageSource>(another.Library.Single(d => d.Definition.Id == target.Definition.Id).Definition.DefaultSource).Reference);
        Assert.Equal("nginx:stable-alpine", Assert.IsType<ImageSource>(saved.Components.Single(c => c.DefinitionSnapshot.Id == target.Definition.Id).Source).Reference);
    }

    [Fact]
    public async Task Changing_the_default_build_folder_keeps_existing_environment_paths_unchanged()
    {
        await using var fixture = await FoundationStore.CreateAsync();
        var library = new AuthoringLibrary(new ComponentLibraryStore(fixture.Worker, fixture.Reader), fixture.Reader);
        var mappings = new LocalPathMappings(fixture.Worker, fixture.Clock);
        string first = fixture.Directory.Child("first-source"), second = fixture.Directory.Child("second-source");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        var settings = new SettingsViewModel(library, mappings);
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        settings.AddTargetCommand.Execute(null);
        var service = settings.SelectedDefinition!;
        service.Name = "Local API"; service.Editor.SourceKind = "build"; service.Editor.LocalFolder = first;
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Empty(settings.Error);
        string firstReference = Assert.IsType<BuildSource>(service.Definition.DefaultSource).PathRef;
        var editor = Editor(fixture, library, mappings);
        await editor.LoadLibraryAsync(TestContext.Current.CancellationToken);
        editor.AddDefinitionCommand.Execute(Assert.Single(editor.Library));
        Assert.Equal(first, Assert.Single(editor.Components).LocalFolder);
        service.Editor.LocalFolder = second;
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Empty(settings.Error);
        var paths = await mappings.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(first, paths.Single(p => "local-path://" + p.Alias == firstReference).AbsolutePath);
        Assert.NotEqual(firstReference, Assert.IsType<BuildSource>(service.Definition.DefaultSource).PathRef);
        Assert.Equal(firstReference, Assert.IsType<BuildSource>(editor.BuildSnapshot().Components[0].Source).PathRef);
    }

    [Fact]
    public async Task An_invalid_service_preserves_edits_and_does_not_publish_other_unsaved_services()
    {
        await using var fixture = await FoundationStore.CreateAsync();
        var library = new AuthoringLibrary(new ComponentLibraryStore(fixture.Worker, fixture.Reader), fixture.Reader);
        var settings = new SettingsViewModel(library);
        settings.AddTargetCommand.Execute(null);
        settings.SelectedDefinition!.Editor.ImageReference = "nginx:stable-alpine";
        settings.AddInfrastructureCommand.Execute(null);
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.NotEmpty(settings.Error);
        Assert.Equal(2, settings.Definitions.Count);
        Assert.Empty(await library.ReadAsync(TestContext.Current.CancellationToken));
    }

    private static EnvironmentEditorViewModel Editor(FoundationStore fixture, IComponentLibrary library, ILocalPathMappings mappings)
    {
        var inputs = new AvailableInputs(); var packs = new NoPacks();
        return new EnvironmentEditorViewModel(EnvironmentDraft.Create(), new CreateEnvironmentService(fixture.Environments, inputs, packs),
            library, new AuthoringInteractions(), fixture.Reader, mappings, inputs, packs);
    }
    private sealed class AvailableInputs : IInputResolver
    {
        public Task<IReadOnlyList<ReadinessIssue>> CheckAsync(IReadOnlyList<InputReference> references, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ReadinessIssue>>([]);
    }
    private sealed class NoPacks : ISeedPackStore
    {
        public Task<VerifiedSeedPack?> FindAsync(SeedPackReference reference, CancellationToken cancellationToken) => Task.FromResult<VerifiedSeedPack?>(null);
    }
}
