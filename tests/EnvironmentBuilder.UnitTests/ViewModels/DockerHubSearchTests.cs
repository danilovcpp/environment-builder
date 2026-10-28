using EnvironmentBuilder.App.Features.Settings;
using EnvironmentBuilder.Core.Abstractions;

namespace EnvironmentBuilder.UnitTests.ViewModels;

public sealed class DockerHubSearchTests
{
    [Fact]
    public async Task Selection_loads_tags_and_logo_and_manual_tags_and_pagination_update_the_image()
    {
        var catalog = new Catalog();
        var settings = new SettingsViewModel(catalog: catalog);
        settings.AddInfrastructureCommand.Execute(null);
        using var source = settings.SelectedDefinition!.Editor;
        source.ImageReference = "r"; source.ImageReference = "re"; source.ImageReference = "redis";
        await source.SearchTask;
        Assert.Equal(new[] { "redis" }, catalog.Queries);
        Assert.True(source.ShowRepositories);
        source.SelectedRepository = Assert.Single(source.Repositories);
        await source.SelectionTask;
        Assert.False(source.ShowRepositories);
        Assert.Equal("https://example.com/redis.png", source.LogoUrl);
        Assert.Equal(new[] { "latest", "7.2" }, source.Tags);
        source.SelectedTag = "7.2";
        Assert.Equal("redis:7.2", source.ImageReference);
        await source.LoadMoreTagsCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "latest", "7.2", "7.0" }, source.Tags);
        Assert.False(source.HasMoreTags);
        source.SelectedTag = "custom-version";
        Assert.Equal("redis:custom-version", source.ImageReference);
        Assert.Equal("https://example.com/redis.png", settings.SelectedDefinition.Build().LogoUrl);
        source.ImageReference = "redis:7.0"; await source.SearchTask;
        Assert.Equal("7.0", source.SelectedTag);
        source.RefreshVersionsCommand.Execute(null); await source.SelectionTask;
        Assert.Equal("redis:7.0", source.ImageReference);
        source.SelectedTag = "7.2"; Assert.Equal("redis:7.2", source.ImageReference);
    }

    [Fact]
    public async Task Stale_search_and_selection_responses_do_not_overwrite_a_new_image()
    {
        var pendingSearch = new TaskCompletionSource<IReadOnlyList<DockerHubRepository>>();
        var searchStarted = new TaskCompletionSource();
        var pendingTags = new TaskCompletionSource<DockerHubTagPage>();
        var pendingLogo = new TaskCompletionSource<DockerHubRepository>();
        var catalog = new Catalog
        {
            Search = query => { if (query == "old") { searchStarted.SetResult(); return pendingSearch.Task; } return Task.FromResult<IReadOnlyList<DockerHubRepository>>([new(query, "", false)]); },
            Tags = (_, _) => pendingTags.Task,
            Logo = _ => pendingLogo.Task
        };
        using var settings = new SettingsViewModel(catalog: catalog);
        settings.AddInfrastructureCommand.Execute(null);
        var source = settings.SelectedDefinition!.Editor;
        source.ImageReference = "old";
        Task oldSearch = source.SearchTask;
        await searchStarted.Task;
        source.ImageReference = "redis"; await source.SearchTask;
        pendingSearch.SetResult([new("old", "", false)]); await oldSearch;
        Assert.Equal("redis", Assert.Single(source.Repositories).Name);
        source.SelectedRepository = source.Repositories[0];
        Task oldSelection = source.SelectionTask;
        source.ImageReference = "postgres:17";
        pendingTags.SetResult(new(["stale-tag"], false));
        pendingLogo.SetResult(new("redis", "", true, "https://example.com/stale.png"));
        await oldSelection;
        Assert.Equal("postgres:17", source.ImageReference);
        Assert.Empty(source.Tags); Assert.Null(source.LogoUrl);
        source.ImageReference = ""; await source.SearchTask;
        source.ImageReference = "p"; await source.SearchTask;
        Assert.False(source.IsSearching);
    }

    [Fact]
    public async Task Network_failures_allow_manual_images_and_missing_logos_do_not_hide_versions()
    {
        using var settings = new SettingsViewModel(catalog: new Catalog { Search = _ => throw new System.Net.Http.HttpRequestException("unavailable") });
        settings.AddInfrastructureCommand.Execute(null);
        var source = settings.SelectedDefinition!.Editor;
        source.ImageReference = "private.registry/redis:custom"; await source.SearchTask;
        Assert.Contains("вручную", source.HubStatus);
        Assert.Equal("private.registry/redis:custom", Assert.IsType<EnvironmentBuilder.Core.Environments.ImageSource>(source.Build()).Reference);
        using var other = new SettingsViewModel(catalog: new Catalog { Logo = _ => throw new System.Net.Http.HttpRequestException("no logo") });
        other.AddInfrastructureCommand.Execute(null);
        var withTags = other.SelectedDefinition!.Editor;
        withTags.ImageReference = "redis"; await withTags.SearchTask;
        withTags.SelectedRepository = withTags.Repositories[0]; await withTags.SelectionTask;
        Assert.NotEmpty(withTags.Tags); Assert.Null(withTags.LogoUrl);
    }

    private sealed class Catalog : IDockerHubCatalog
    {
        public List<string> Queries { get; } = [];
        public Func<string, Task<IReadOnlyList<DockerHubRepository>>>? Search { get; init; }
        public Func<string, Task<DockerHubRepository>>? Logo { get; init; }
        public Func<string, int, Task<DockerHubTagPage>>? Tags { get; init; }
        public Task<IReadOnlyList<DockerHubRepository>> SearchAsync(string query, CancellationToken cancellationToken)
        { Queries.Add(query); return Search?.Invoke(query) ?? Task.FromResult<IReadOnlyList<DockerHubRepository>>([new(query, "Redis", true)]); }
        public Task<DockerHubRepository> GetRepositoryAsync(string name, CancellationToken cancellationToken) => Logo?.Invoke(name) ?? Task.FromResult(new DockerHubRepository(name, "", true, "https://example.com/redis.png"));
        public Task<DockerHubTagPage> GetTagsAsync(string name, int page, CancellationToken cancellationToken) => Tags?.Invoke(name, page) ?? Task.FromResult(page == 1 ? new DockerHubTagPage(["latest", "7.2"], true) : new DockerHubTagPage(["7.2", "7.0"], false));
    }
}
