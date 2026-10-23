using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.UnitTests.ViewModels;

public sealed class EnvironmentEditorTests
{
    [Fact]
    public async Task Validation_blocks_structural_errors_but_readiness_does_not_prevent_save()
    {
        var io = new AuthoringFakes();
        var editor = io.Editor();
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.NotEmpty(editor.SaveDisabledReason);
        await editor.LoadLibraryAsync(TestContext.Current.CancellationToken);
        editor.Name = "Local draft";
        editor.AddComponentCommand.Execute(null);
        Assert.True(editor.CanSave);
        await editor.CheckReadinessCommand.ExecuteAsync(null);
        Assert.Contains(editor.ReadinessIssues, issue => issue.Code == "image-identity-unknown");
        Assert.True(editor.CanSave);
        editor.Components[0].Parameters[0].Text = "9223372036854775808";
        Assert.False(editor.CanSave);
        editor.Components[0].Parameters[0].Text = "1000";
        Assert.True(editor.CanSave);
        io.FailSave = true;
        Assert.False(await editor.TrySaveAsync(TestContext.Current.CancellationToken));
        Assert.True(editor.IsDirty);
        Assert.NotEmpty(editor.ActionError);
        io.FailSave = false;
        Assert.True(await editor.TrySaveAsync(TestContext.Current.CancellationToken));
        Assert.False(editor.IsDirty);
        Assert.True(editor.IsReadOnly);
        Assert.False(editor.CanSave);
    }

    [Fact]
    public async Task Preview_and_dirty_navigation_keep_the_same_editor_and_list_context()
    {
        var io = new AuthoringFakes();
        var navigation = new App.Shell.NavigationService();
        var list = new EnvironmentListViewModel(io) { Search = "kept filter", ScrollOffset = 83 };
        var session = new AuthoringSession(list, new CreateEnvironmentService(io, io, io), io, io, io, io, io, io, io, navigation);
        await list.CreateNewCommand.ExecuteAsync(null);
        var editor = Assert.IsType<EnvironmentEditorViewModel>(session.Current);
        editor.Name = "Draft remains";
        editor.AddComponentCommand.Execute(null);
        editor.ScrollOffset = 179;
        editor.LastFocusedField = "description";
        await editor.ShowPreviewCommand.ExecuteAsync(null);
        var preview = Assert.IsType<ComposePreviewViewModel>(session.Current);
        Assert.NotEmpty(preview.Content);
        Assert.Equal(1, io.Captures);
        await preview.CopyCommand.ExecuteAsync(null);
        Assert.Equal(preview.Content, io.Clipboard);
        await preview.GoBackCommand.ExecuteAsync(null);
        Assert.Same(editor, session.Current);
        Assert.Equal(179, editor.ScrollOffset);
        Assert.Equal("description", editor.LastFocusedField);
        io.Decisions.Enqueue(DirtyDraftDecision.Stay);
        Assert.False(await session.LeaveAsync(TestContext.Current.CancellationToken));
        Assert.Same(editor, session.Current);
        io.Decisions.Enqueue(DirtyDraftDecision.Save);
        io.FailSave = true;
        Assert.False(await session.LeaveAsync(TestContext.Current.CancellationToken));
        Assert.Same(editor, session.Current);
        io.FailSave = false;
        io.Decisions.Enqueue(DirtyDraftDecision.Save);
        Assert.True(await session.LeaveAsync(TestContext.Current.CancellationToken));
        Assert.Same(list, session.Current);
        Assert.Equal("kept filter", list.Search);
        Assert.Equal(83, list.ScrollOffset);
        Assert.NotNull(list.SelectedId);
        Assert.NotEmpty(list.SelectionNotice);
    }
    [Theory]
    [InlineData(DirtyDraftDecision.Stay, false)]
    [InlineData(DirtyDraftDecision.Discard, true)]
    [InlineData(DirtyDraftDecision.Save, true)]
    public async Task Dirty_guard_obeys_the_decision_and_never_navigates_after_failed_save(DirtyDraftDecision decision, bool leaves)
    {
        var guard = new DirtyDraftGuard(_ => Task.FromResult(decision));
        Assert.Equal(leaves, await guard.CanLeaveAsync(true, _ => Task.FromResult(true), TestContext.Current.CancellationToken));
        if (decision == DirtyDraftDecision.Save)
        { Assert.False(await guard.CanLeaveAsync(true, _ => Task.FromResult(false), TestContext.Current.CancellationToken)); }
    }
}
