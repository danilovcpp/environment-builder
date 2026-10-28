using EnvironmentBuilder.App.Features.Settings;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Authoring;

public sealed class LibraryDependencyTests
{
    [Fact]
    public async Task Target_dependencies_select_existing_components_preserve_optionality_and_follow_saved_versions()
    {
        await using var fixture = await FoundationStore.CreateAsync();
        var library = new AuthoringLibrary(new ComponentLibraryStore(fixture.Worker, fixture.Reader), fixture.Reader);
        using var settings = new SettingsViewModel(library);
        settings.AddInfrastructureCommand.Execute(null);
        var redis = settings.SelectedDefinition!; redis.Name = "Redis"; redis.Editor.ImageReference = "redis:7";
        settings.AddInfrastructureCommand.Execute(null);
        var metrics = settings.SelectedDefinition!; metrics.Name = "Metrics"; metrics.Editor.ImageReference = "prom/prometheus:latest";
        Assert.False(redis.CanHaveDependencies); redis.AddDependencyCommand.Execute(null); Assert.Empty(redis.Dependencies);
        settings.AddTargetCommand.Execute(null);
        var api = settings.SelectedDefinition!; api.Name = "API"; api.Editor.ImageReference = "nginx:stable";
        Assert.Equal(2, api.DependencyOptions.Count); Assert.DoesNotContain(api, api.DependencyOptions);
        api.AddDependencyCommand.Execute(null); api.Dependencies[0].SelectedService = redis;
        api.AddDependencyCommand.Execute(null); api.Dependencies[1].SelectedService = metrics; api.Dependencies[1].IsRequired = false;
        await settings.SaveCommand.ExecuteAsync(null); Assert.Empty(settings.Error);
        Assert.False(api.Definition.DependencyRequirements[0].Optional); Assert.True(api.Definition.DependencyRequirements[1].Optional);
        var draft = EnvironmentDraft.Create();
        var entries = await library.ReadEntriesAsync(TestContext.Current.CancellationToken);
        draft.AddComponent(api.Definition, entries.Select(entry => entry.Definition).ToArray());
        draft.Update(draft.Snapshot with { Name = "Required only" });
        Assert.Equal(2, draft.Snapshot.Components.Length); Assert.Single(draft.Snapshot.Dependencies);
        EnvironmentValidator.Validate(draft.Snapshot);
        draft.AddComponent(metrics.Definition, entries.Select(entry => entry.Definition).ToArray());
        Assert.Equal(2, draft.Snapshot.Dependencies.Length); EnvironmentValidator.Validate(draft.Snapshot);
        var oldSnapshot = draft.Snapshot;

        redis.Editor.ImageReference = "redis:8";
        await settings.SaveCommand.ExecuteAsync(null); Assert.Empty(settings.Error);
        Assert.Equal(redis.Definition.Version, api.Definition.DependencyRequirements[0].DefinitionVersion);
        Assert.NotEqual(oldSnapshot.Components.Single(component => component.DefinitionSnapshot.Id == api.Definition.Id).DefinitionSnapshot.Version, api.Definition.Version);
        EnvironmentValidator.Validate(oldSnapshot);
        using var reopened = new SettingsViewModel(library);
        await reopened.LoadAsync(TestContext.Current.CancellationToken);
        var loaded = reopened.Definitions.Single(row => row.Name == "API");
        Assert.Equal("Redis", loaded.Dependencies[0].SelectedService!.Name);
        Assert.True(loaded.Dependencies[0].IsRequired); Assert.False(loaded.Dependencies[1].IsRequired);
    }

    [Fact]
    public async Task Dependency_cycles_are_rejected_before_publishing_the_library()
    {
        await using var fixture = await FoundationStore.CreateAsync();
        var library = new AuthoringLibrary(new ComponentLibraryStore(fixture.Worker, fixture.Reader), fixture.Reader);
        using var settings = new SettingsViewModel(library);
        settings.AddTargetCommand.Execute(null); var first = settings.SelectedDefinition!; first.Editor.ImageReference = "first:latest";
        settings.AddTargetCommand.Execute(null); var second = settings.SelectedDefinition!; second.Editor.ImageReference = "second:latest";
        first.AddDependencyCommand.Execute(null); first.Dependencies[0].SelectedService = second;
        second.AddDependencyCommand.Execute(null); second.Dependencies[0].SelectedService = first;
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Contains("dependency-cycle", settings.Error);
        Assert.Empty(await library.ReadAsync(TestContext.Current.CancellationToken));
    }
}
