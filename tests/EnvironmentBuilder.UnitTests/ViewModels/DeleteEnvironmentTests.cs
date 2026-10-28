using EnvironmentBuilder.App.Components;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.UnitTests.ViewModels;

public sealed class DeleteEnvironmentTests
{
    [Fact]
    public void Confirmation_names_the_environment_and_identity_and_explains_stop_and_data_loss()
    {
        var environment = Document();
        var plan = Plan(environment);
        var model = new DestructivePlanViewModel(plan);
        Assert.Equal("Удалить окружение", model.Title); Assert.Contains(environment.Name, model.Details);
        Assert.Contains(environment.Id.ToString(), model.Details); Assert.Contains("без возможности восстановления", model.Warning);
        Assert.Contains("stop-before-removal", model.Details);
    }

    [Fact]
    public async Task Tombstone_remains_visible_with_remnants_and_retry_and_blocks_start_and_edit()
    {
        var source = new AuthoringFakes(); var environment = Document(); var plan = Plan(environment);
        var operation = new Operation(Guid.NewGuid(), environment.Id, "delete", OperationState.NeedsAttention, DateTimeOffset.UtcNow, 1, plan.ConfigurationHash, plan)
        { Remnants = ["data-volume"], DeletedResources = ["container-id"], ResultCode = "operation-interrupted" };
        source.Items.Add(new(environment.Id, environment.Name, 1, environment, null, true) { Deletion = new(environment, operation) });
        var model = new EnvironmentListViewModel(source); await model.LoadAsync(TestContext.Current.CancellationToken);
        var row = Assert.Single(model.VisibleItems); model.SelectedItem = row;
        Assert.Equal("needsAttention", row.StateKey); Assert.Contains("Удаление", row.State);
        Assert.Contains("data-volume", row.DeletionDetails); Assert.Contains("operation-interrupted", row.DeletionDetails);
        Assert.Contains("container-id", row.DeletionDetails); Assert.False(row.CanOpen); Assert.True(row.CanDelete);
        Assert.True(model.DeleteSelectedCommand.CanExecute(null)); Assert.False(model.ClearSelectedDataCommand.CanExecute(null));
        Assert.Contains("Повторить", model.DeleteLabel);
    }

    [Fact]
    public async Task Successful_delete_clears_selection_and_declined_delete_keeps_it()
    {
        var source = new AuthoringFakes(); var environment = Document();
        source.Items.Add(new(environment.Id, environment.Name, 1, environment, null, false));
        var model = new EnvironmentListViewModel(source); await model.LoadAsync(TestContext.Current.CancellationToken); model.SelectSaved(environment.Id);
        model.Delete = (_, _) => Task.FromResult(new DeleteEnvironmentResult(false, null, "cancelled"));
        await model.DeleteSelectedCommand.ExecuteAsync(null); Assert.Equal(environment.Id, model.SelectedId);
        model.Delete = (_, _) => { source.Items.Clear(); return Task.FromResult(new DeleteEnvironmentResult(true, null, null)); };
        await model.DeleteSelectedCommand.ExecuteAsync(null); Assert.Null(model.SelectedId); Assert.Null(model.SelectedItem); Assert.True(model.IsEmpty);
    }

    [Theory]
    [InlineData("stale-plan")]
    [InlineData("foreign-consumer")]
    public async Task Blocked_delete_preserves_the_selected_row_and_explains_the_reason(string code)
    {
        var source = new AuthoringFakes(); var environment = Document();
        source.Items.Add(new(environment.Id, environment.Name, 1, environment, null, false));
        var model = new EnvironmentListViewModel(source) { Delete = (_, _) => Task.FromResult(new DeleteEnvironmentResult(false, null, code)) };
        await model.LoadAsync(TestContext.Current.CancellationToken); model.SelectSaved(environment.Id);
        await model.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.Equal(environment.Id, model.SelectedId); Assert.Single(model.VisibleItems); Assert.Contains(code, model.DeletionDetails);
    }

    [Fact]
    public async Task Cancellation_targets_the_running_delete_even_when_selection_changes()
    {
        var source = new AuthoringFakes(); var environment = Document();
        source.Items.Add(new(environment.Id, environment.Name, 1, environment, null, false));
        var second = environment with { Id = Guid.NewGuid() };
        source.Items.Add(new(second.Id, second.Name, 1, second, null, false));
        var pending = new TaskCompletionSource<DeleteEnvironmentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid? cancelled = null;
        var model = new EnvironmentListViewModel(source)
        { Delete = (_, _) => pending.Task, CancelDelete = id => { cancelled = id; return Task.CompletedTask; } };
        await model.LoadAsync(TestContext.Current.CancellationToken); model.SelectSaved(environment.Id);
        Task deleting = model.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.True(model.IsDeleting); Assert.False(model.DeleteSelectedCommand.CanExecute(null));
        model.SelectSaved(second.Id); await model.CancelDeleteCommand.ExecuteAsync(null);
        Assert.Equal(environment.Id, cancelled);
        pending.SetResult(new(false, null, "cancelled")); await deleting; Assert.False(model.IsDeleting);
    }

    private static IsolatedEnvironment Document()
    {
        var draft = EnvironmentDraft.Create(); var definition = Environments.AuthoringContractTests.Definition();
        draft.AddComponent(definition, [definition]); return draft.Snapshot with { Name = "Selected copy" };
    }
    private static OperationPlan Plan(IsolatedEnvironment environment) => new(Guid.NewGuid(), environment.Id, 1, CanonicalJson.Hash(environment), Guid.NewGuid(),
        CanonicalJson.Hash(environment.Baseline), [], [], ["stop-before-removal", "close-description"], "Описание и данные будут удалены без возможности восстановления.")
    { EnvironmentName = environment.Name };
}
