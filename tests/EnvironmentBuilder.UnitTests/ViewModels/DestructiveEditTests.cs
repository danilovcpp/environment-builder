using EnvironmentBuilder.App.Components;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.UnitTests.Environments;

namespace EnvironmentBuilder.UnitTests.ViewModels;

public sealed class DestructiveEditTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Editing_requires_confirmed_quiescence_and_preserves_instance_ids(bool allowed)
    {
        var fake = new EditFake { Allowed = allowed }; var editor = Editor(fake);
        Assert.True(editor.IsReadOnly); await editor.BeginEditCommand.ExecuteAsync(null);
        Assert.Equal(!allowed, editor.IsReadOnly); Assert.Equal(allowed, editor.CanEdit);
        Assert.Equal(fake.Original.Components[0].Id, editor.Components[0].Id);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("stale-plan")]
    [InlineData("partial-cleanup")]
    public async Task Failed_save_preserves_dirty_draft_and_blocks_dirty_navigation(string code)
    {
        var fake = new EditFake { Failure = code }; var editor = Editor(fake);
        await editor.BeginEditCommand.ExecuteAsync(null); editor.Name = "Candidate";
        var guard = new DirtyDraftGuard(_ => Task.FromResult(DirtyDraftDecision.Save));
        Assert.False(await guard.CanLeaveAsync(editor.IsDirty, editor.TrySaveAsync, TestContext.Current.CancellationToken));
        Assert.Equal("Candidate", editor.Name); Assert.True(editor.IsDirty); Assert.Equal(1, editor.Revision); Assert.NotEmpty(editor.ActionError);
    }

    [Fact]
    public async Task Successful_dirty_save_adopts_next_revision_and_same_identity()
    {
        var fake = new EditFake(); var editor = Editor(fake); await editor.BeginEditCommand.ExecuteAsync(null); editor.Name = "Candidate";
        Assert.True(await editor.TrySaveAsync(TestContext.Current.CancellationToken));
        Assert.Equal(fake.Original.Id, editor.Id); Assert.Equal(2, editor.Revision); Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task Edits_made_while_commit_is_pending_remain_dirty_after_publication()
    {
        var fake = new EditFake { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var editor = Editor(fake); await editor.BeginEditCommand.ExecuteAsync(null); editor.Name = "Confirmed candidate";
        Task<bool> saving = editor.TrySaveAsync(TestContext.Current.CancellationToken);
        editor.Name = "New draft changes";
        fake.Pending.SetResult(new(true, fake.Original with { Name = "Confirmed candidate", Revision = 2 }, null, null));
        Assert.False(await saving); Assert.True(editor.IsDirty); Assert.Equal("New draft changes", editor.Name); Assert.Equal(2, editor.Revision);
    }

    [Fact]
    public void Warning_contains_exact_scope_identity_revision_and_data_loss()
    {
        var fake = new EditFake(); var plan = new OperationPlan(Guid.NewGuid(), fake.Original.Id, 1, CanonicalJson.Hash(fake.Original), Guid.NewGuid(), CanonicalJson.Hash(fake.Original.Baseline), [], [], ["verify-quiescence", "remove-containers", "remove-volumes", "remove-network", "publish-revision"], "Данные будут удалены без восстановления.", fake.Original with { Revision = 2 }) { EnvironmentName = fake.Original.Name };
        var model = new DestructivePlanViewModel(plan);
        Assert.Contains(fake.Original.Id.ToString(), model.Details); Assert.Contains(fake.Original.Name, model.Details);
        Assert.Contains("1 → 2", model.Details); Assert.Contains("Данные", model.Warning); Assert.Contains("remove-volumes", model.Details);
    }

    [Fact]
    public void Plans_created_before_edit_fields_keep_their_confirmation_hash()
    {
        var fake = new EditFake();
        var plan = new OperationPlan(Guid.NewGuid(), fake.Original.Id, 1, CanonicalJson.Hash(fake.Original), Guid.NewGuid(), CanonicalJson.Hash(fake.Original.Baseline), [], [], ["stop"], "Preserve data");
        string json = System.Text.Json.JsonSerializer.Serialize(plan, CanonicalJson.Options);
        Assert.DoesNotContain("expectedResources", json); Assert.DoesNotContain("nextDataGenerationId", json);
        Assert.Equal(CanonicalJson.Hash(json), System.Text.Json.JsonSerializer.Deserialize<OperationPlan>(json, CanonicalJson.Options)!.Hash);
    }

    private static EnvironmentEditorViewModel Editor(EditFake fake)
    {
        var authoring = new AuthoringFakes();
        return new(EnvironmentDraft.Open(fake.Original), new CreateEnvironmentService(authoring, authoring, authoring), authoring, authoring, authoring, authoring, authoring, authoring, fake, fake);
    }

    private sealed class EditFake : IEnvironmentEditing, IDestructiveInteractions
    {
        public IsolatedEnvironment Original { get; } = CreateOriginal();
        private static IsolatedEnvironment CreateOriginal()
        {
            var draft = EnvironmentDraft.Create(); var definition = AuthoringContractTests.Definition(); draft.AddComponent(definition, [definition]);
            return draft.Snapshot with { Name = "Original" };
        }
        public bool Allowed { get; init; } = true;
        public string? Failure { get; init; }
        public TaskCompletionSource<EditEnvironmentResult>? Pending { get; init; }
        public Task<QuiescenceResult> CheckAsync(IsolatedEnvironment environment, CancellationToken token) => Task.FromResult(new QuiescenceResult(Allowed, Allowed ? null : "Обновите состояние Docker."));
        public Task<EditEnvironmentResult> SaveAsync(IsolatedEnvironment original, IsolatedEnvironment candidate, Func<OperationPlan, CancellationToken, Task<ConfirmationRecord?>> confirm, CancellationToken token) => Pending?.Task ?? Task.FromResult(new EditEnvironmentResult(Failure is null, Failure is null ? candidate with { Revision = original.Revision + 1 } : null, null, Failure));
        public Task<ConfirmationRecord?> ConfirmAsync(OperationPlan plan, CancellationToken token) => Task.FromResult<ConfirmationRecord?>(new(plan.Hash, DateTimeOffset.UtcNow));
    }
}
