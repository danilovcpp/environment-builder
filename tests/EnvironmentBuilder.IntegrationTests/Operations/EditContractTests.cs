using System.Collections.Immutable;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Operations;

public sealed class EditContractTests
{
    [Fact]
    public async Task Canonical_noop_does_not_plan_confirm_or_change_generation()
    {
        await using var fixture = await EditFixture.CreateAsync();
        var generation = await fixture.GenerationAsync();
        var result = await fixture.SaveAsync(fixture.Environment, TestContext.Current.CancellationToken);
        Assert.True(result.Saved); Assert.Null(result.Operation);
        Assert.Equal(0, fixture.Preparations); Assert.Equal(0, fixture.Confirmations);
        Assert.Equal(generation, await fixture.GenerationAsync()); Assert.Empty(fixture.Mutations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Name_only_edit_publishes_same_id_next_revision_and_fresh_generation_after_verified_cleanup(bool empty)
    {
        await using var fixture = await EditFixture.CreateAsync(empty: empty);
        var original = fixture.Environment; var generation = await fixture.GenerationAsync();
        fixture.BeforeRemoval = async () =>
        {
            Assert.Equal(CanonicalJson.Hash(original), CanonicalJson.Hash(await fixture.SavedAsync()));
            Assert.Equal("invalidated", await fixture.ValidityAsync(generation));
        };
        var result = await fixture.SaveAsync(original with { Name = "Renamed only" }, TestContext.Current.CancellationToken);
        Assert.True(result.Saved); Assert.Equal(1, fixture.Confirmations);
        Assert.Equal(original.Id, result.Environment!.Id); Assert.Equal(original.Revision + 1, result.Environment.Revision);
        Assert.Equal("Renamed only", (await fixture.SavedAsync()).Name);
        Assert.NotEqual(generation, await fixture.GenerationAsync()); Assert.Equal("retired", await fixture.ValidityAsync(generation));
        Assert.Equal(OperationState.Succeeded, result.Operation!.State);
        Assert.Empty(result.Operation.Remnants); Assert.Empty(await fixture.Ledger.ReadAsync(original.Id, TestContext.Current.CancellationToken));
        Assert.Equal(empty ? 0 : 3, fixture.Mutations.Count);
        Assert.Equal(result.Operation.Plan.Hash, result.Operation.Confirmation!.PlanHash);
        Assert.Equal(original.Id, result.Operation.Plan.Candidate!.Id);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("stale-token")]
    [InlineData("engine-drift")]
    [InlineData("running")]
    public async Task Cancel_or_stale_or_unconfirmed_state_preserves_specification_and_data(string fault)
    {
        await using var fixture = await EditFixture.CreateAsync();
        var generation = await fixture.GenerationAsync(); fixture.Fault = fault;
        var result = await fixture.SaveAsync(fixture.Environment with { Name = "Candidate" }, TestContext.Current.CancellationToken);
        Assert.False(result.Saved); Assert.Equal(CanonicalJson.Hash(fixture.Environment), CanonicalJson.Hash(await fixture.SavedAsync()));
        Assert.Equal(generation, await fixture.GenerationAsync()); Assert.Equal("active", await fixture.ValidityAsync(generation)); Assert.Empty(fixture.Mutations);
    }

    [Theory]
    [InlineData("intent:beforeCommit", 0)]
    [InlineData("generation-invalidation:beforeCommit", 0)]
    [InlineData("result:beforeCommit", 1)]
    [InlineData("edit-finalization:beforeCommit", 3)]
    public async Task Persistence_fault_never_publishes_candidate_and_retains_evidence(string boundary, int effects)
    {
        await using var fixture = await EditFixture.CreateAsync(); fixture.FaultBoundary = boundary;
        var result = await fixture.SaveAsync(fixture.Environment with { Description = "Changed settings" }, TestContext.Current.CancellationToken);
        Assert.False(result.Saved); Assert.Equal(1, (await fixture.SavedAsync()).Revision);
        Assert.Equal(effects, fixture.Mutations.Count); Assert.NotNull(result.Operation!.Plan.Candidate);
        Assert.Equal(boundary.StartsWith("generation-", StringComparison.Ordinal) ? "active" : "invalidated", await fixture.ValidityAsync(await fixture.GenerationAsync()));
        Assert.NotEmpty(await fixture.Store.Operations.ReadUnresolvedAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Committed_finalization_is_recognized_after_lost_acknowledgement()
    {
        await using var fixture = await EditFixture.CreateAsync(); fixture.FaultBoundary = "edit-finalization:afterCommit";
        var result = await fixture.SaveAsync(fixture.Environment with { Name = "Committed" }, TestContext.Current.CancellationToken);
        Assert.True(result.Saved); Assert.Equal(2, (await fixture.SavedAsync()).Revision); Assert.Equal(3, fixture.Mutations.Count);
    }

    [Fact]
    public async Task Already_absent_resources_are_journaled_without_repeating_removal()
    {
        await using var fixture = await EditFixture.CreateAsync(); fixture.Absent.UnionWith(fixture.Resources.Select(r => r.ResourceId));
        var result = await fixture.SaveAsync(fixture.Environment with { Name = "Absent" }, TestContext.Current.CancellationToken);
        Assert.True(result.Saved); Assert.Empty(fixture.Mutations);
    }

    [Fact]
    public async Task Failed_finalization_can_be_explicitly_completed_after_rechecking_absence_without_repeating_cleanup()
    {
        await using var fixture = await EditFixture.CreateAsync(); fixture.FaultBoundary = "edit-finalization:beforeCommit";
        var result = await fixture.SaveAsync(fixture.Environment with { Name = "Finalize later" }, TestContext.Current.CancellationToken);
        Assert.False(result.Saved); fixture.FaultBoundary = null;
        var completed = await fixture.Service.FinalizeAsync(result.Operation!, TestContext.Current.CancellationToken);
        Assert.True(completed.Saved); Assert.Equal(3, fixture.Mutations.Count); Assert.Equal(2, (await fixture.SavedAsync()).Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_at_confirmation_or_after_first_effect_preserves_old_revision(bool afterEffect)
    {
        await using var fixture = await EditFixture.CreateAsync(); using var cancellation = new CancellationTokenSource();
        if (afterEffect) { fixture.AfterRemoval = cancellation.Cancel; } else { fixture.DuringConfirmation = cancellation.Cancel; }
        var result = await fixture.SaveAsync(fixture.Environment with { Name = "Cancelled" }, cancellation.Token);
        Assert.False(result.Saved); Assert.Equal(1, (await fixture.SavedAsync()).Revision); Assert.Equal(afterEffect ? 1 : 0, fixture.Mutations.Count);
        Assert.Equal(afterEffect ? OperationState.PartiallyCompleted : OperationState.Cancelled, result.Operation!.State);
        Assert.True(result.Operation.CancellationRequested);
        Assert.Equal(afterEffect ? "invalidated" : "active", await fixture.ValidityAsync(await fixture.GenerationAsync()));
        if (afterEffect) { Assert.Single(result.Operation.DeletedResources); Assert.Equal(2, result.Operation.Remnants.Length); }
    }

    [Fact]
    public async Task Component_settings_edit_preserves_existing_instance_identity()
    {
        await using var fixture = await EditFixture.CreateAsync();
        var component = fixture.Environment.Components[0];
        var candidate = fixture.Environment with { Components = fixture.Environment.Components.SetItem(0, component with { Parameters = component.Parameters.SetItem("MaxItems", new() { Literal = "2000" }) }) };
        var result = await fixture.SaveAsync(candidate, TestContext.Current.CancellationToken);
        Assert.True(result.Saved); Assert.Equal(component.Id, result.Environment!.Components[0].Id); Assert.Equal("2000", result.Environment.Components[0].Parameters["MaxItems"].Literal);
    }

    [Fact]
    public async Task Unverified_absence_after_successful_cli_exit_blocks_publication()
    {
        await using var fixture = await EditFixture.CreateAsync(); fixture.Fault = "removal-still-present";
        var result = await fixture.SaveAsync(fixture.Environment with { Name = "Candidate" }, TestContext.Current.CancellationToken);
        Assert.False(result.Saved); Assert.Equal(1, (await fixture.SavedAsync()).Revision); Assert.Single(fixture.Mutations);
    }

    [Fact]
    public async Task Stale_review_can_only_continue_through_a_new_plan_and_confirmation()
    {
        await using var fixture = await EditFixture.CreateAsync(); fixture.Fault = "stale-token";
        var candidate = fixture.Environment with { Name = "Reviewed again" };
        var rejected = await fixture.SaveAsync(candidate, TestContext.Current.CancellationToken);
        Assert.False(rejected.Saved); fixture.Fault = null;
        var accepted = await fixture.SaveAsync(candidate, TestContext.Current.CancellationToken);
        Assert.True(accepted.Saved); Assert.Equal(2, fixture.Confirmations);
        Assert.NotEqual(rejected.Operation!.Plan.Hash, accepted.Operation!.Plan.Hash);
    }

    [Fact]
    public async Task Composition_edit_adds_a_new_instance_without_reassigning_the_existing_id()
    {
        await using var fixture = await EditFixture.CreateAsync();
        var old = fixture.Environment.Components[0];
        var added = old with { Id = Guid.NewGuid(), Ports = old.Ports.Select(p => p with { HostPort = null }).ToImmutableArray() };
        var candidate = fixture.Environment with
        {
            Components = fixture.Environment.Components.Add(added),
            Baseline = new(fixture.Environment.Baseline.Applications.Add(fixture.Environment.Baseline.Applications[0] with { ComponentId = added.Id }))
        };
        var result = await fixture.SaveAsync(candidate, TestContext.Current.CancellationToken);
        Assert.True(result.Saved); Assert.Equal(new[] { old.Id, added.Id }, result.Environment!.Components.Select(c => c.Id));
        Assert.Equal(3, fixture.Mutations.Count);
    }

    [Fact]
    public async Task Candidate_pack_references_and_summary_indexes_publish_in_the_final_transaction()
    {
        await using var fixture = await EditFixture.CreateAsync();
        var pack = new EnvironmentBuilder.Core.SeedPacks.SeedPackReference(Guid.NewGuid(), "1.0.0", new string('a', 64));
        var candidate = fixture.Environment with
        {
            Name = "Indexed candidate",
            Description = "New summary",
            Baseline = new(fixture.Environment.Baseline.Applications.Add(fixture.Environment.Baseline.Applications[0] with { SeedPack = pack }))
        };
        var result = await fixture.SaveAsync(candidate, TestContext.Current.CancellationToken);
        Assert.True(result.Saved);
        var row = Assert.Single(await fixture.Store.Environments.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(candidate.Name, row.Name); Assert.Equal(candidate.Description, row.Document!.Description);
        await fixture.Store.Worker.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ownerKind,ownerId,packId,contentHash FROM PackageReferences";
            using var rows = command.ExecuteReader(); Assert.True(rows.Read());
            Assert.Equal("environment", rows.GetString(0)); Assert.Equal(candidate.Id.ToString(), rows.GetString(1));
            Assert.Equal(pack.Id.ToString(), rows.GetString(2)); Assert.Equal(pack.ContentHash, rows.GetString(3)); Assert.False(rows.Read()); return true;
        }, TestContext.Current.CancellationToken);
    }
}
