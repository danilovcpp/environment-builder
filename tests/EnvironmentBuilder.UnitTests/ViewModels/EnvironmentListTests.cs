using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.Core.Abstractions;

namespace EnvironmentBuilder.UnitTests.ViewModels;

public sealed class EnvironmentListTests
{
    [Fact]
    public async Task Search_filters_selection_and_expansion_are_independent_of_reload()
    {
        var io = new AuthoringFakes();
        var draft = Core.Environments.EnvironmentDraft.Create();
        draft.AddComponent(Environments.AuthoringContractTests.Definition(), [Environments.AuthoringContractTests.Definition()]);
        var document = draft.Snapshot with { Name = "Identical name", Description = "Find by description" };
        io.Items.Add(new EnvironmentListItem(document.Id, document.Name, 1, document, null, false));
        io.Items.Add(new EnvironmentListItem(Guid.NewGuid(), document.Name, 1, null, "invalid-json", false));
        var model = new EnvironmentListViewModel(io);
        await model.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, model.VisibleItems.Count);
        model.VisibleItems[0].IsExpanded = true;
        model.SelectedItem = model.VisibleItems[0];
        model.ScrollOffset = 141;
        model.Search = "description";
        Assert.Single(model.VisibleItems);
        Assert.Equal(document.Id, model.SelectedId);
        model.TypeFilter = "infrastructure";
        Assert.True(model.NoResults);
        Assert.Equal(document.Id, model.SelectedId);
        model.TypeFilter = "all";
        await model.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(model.VisibleItems).IsExpanded);
        Assert.Equal(141, model.ScrollOffset);
        model.Search = ""; model.StatusFilter = "unreadable";
        Assert.False(Assert.Single(model.VisibleItems).CanOpen);
        Assert.NotEmpty(model.VisibleItems[0].DisabledReason);
        Assert.Equal("Метрики не получены", model.VisibleItems[0].Measurement);
    }

    [Fact]
    public async Task Loading_is_observable_before_an_asynchronous_store_reply()
    {
        var io = new AuthoringFakes { PendingLoad = new TaskCompletionSource<IReadOnlyList<EnvironmentListItem>>(TaskCreationOptions.RunContinuationsAsynchronously) };
        var model = new EnvironmentListViewModel(io);
        Task loading = model.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(model.IsLoading);
        Assert.False(model.IsEmpty);
        io.PendingLoad.SetResult([]);
        await loading;
        Assert.True(model.IsEmpty);
    }
    [Fact]
    public async Task Empty_loading_and_store_error_are_distinct_without_invented_metrics()
    {
        var source = new Store();
        var model = new EnvironmentListViewModel(source);
        Assert.True(model.IsLoading);
        await model.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(model.IsEmpty);
        Assert.False(model.IsLoading);
        source.Fail = true;
        await model.LoadAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(model.Error);
        Assert.False(model.IsEmpty);
    }

    private sealed class Store : IEnvironmentStore
    {
        public bool Fail { get; set; }
        public Task SaveNewAsync(Core.Environments.IsolatedEnvironment environment, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<EnvironmentListItem>> ListAsync(CancellationToken cancellationToken) => Fail
            ? Task.FromException<IReadOnlyList<EnvironmentListItem>>(new System.IO.IOException("fixture")) : Task.FromResult<IReadOnlyList<EnvironmentListItem>>([]);
    }
}
