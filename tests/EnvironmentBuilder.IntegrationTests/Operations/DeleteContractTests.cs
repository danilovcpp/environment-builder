using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Operations;

public sealed class DeleteContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_verified_absence_closes_description_and_releases_references(bool absent)
    {
        await using var fixture = await EditFixture.CreateAsync(configure: WithPack);
        if (absent) { fixture.Absent.UnionWith(fixture.Resources.Select(r => r.ResourceId)); }
        fixture.BeforeRemoval = async () =>
        {
            var item = Assert.Single(await fixture.Store.Environments.ListAsync(TestContext.Current.CancellationToken));
            Assert.NotNull(item.Deletion); Assert.NotNull(item.Document);
            Assert.Equal(fixture.Environment.Id, item.Deletion.EnvironmentId);
        };
        var result = await fixture.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.True(result.Deleted); Assert.Empty(await fixture.Store.Environments.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(absent ? 0 : 3, fixture.Mutations.Count);
        Assert.Empty(await fixture.Ledger.ReadAsync(fixture.Environment.Id, TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Store.Operations.ReadUnresolvedAsync(TestContext.Current.CancellationToken));
        await fixture.Store.Worker.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT (SELECT count(*) FROM PackageReferences)+(SELECT count(*) FROM DataGenerations)+(SELECT count(*) FROM DataApplicationRecords)+(SELECT count(*) FROM OperationSteps)";
            Assert.Equal(0L, command.ExecuteScalar());
            command.CommandText = "SELECT json FROM Operations WHERE kind='delete'";
            string summary = (string)command.ExecuteScalar()!;
            Assert.DoesNotContain("selected-data", summary); Assert.DoesNotContain("seedPack", summary); return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("stale-token")]
    [InlineData("engine-drift")]
    public async Task Declined_or_stale_plan_has_zero_mutations_and_no_tombstone(string fault)
    {
        await using var fixture = await EditFixture.CreateAsync(); fixture.Fault = fault;
        var result = await fixture.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.False(result.Deleted); Assert.Empty(fixture.Mutations);
        Assert.Null(Assert.Single(await fixture.Store.Environments.ListAsync(TestContext.Current.CancellationToken)).Deletion);
    }

    [Theory]
    [InlineData("result:beforeCommit")]
    [InlineData("delete-finalization:beforeCommit")]
    [InlineData("volume-in-use")]
    public async Task Interrupted_delete_retains_snapshot_ledger_and_retries_absent_resources_with_a_new_plan(string fault)
    {
        await using var fixture = await EditFixture.CreateAsync(configure: WithPack);
        if (fault.Contains(':', StringComparison.Ordinal)) { fixture.FaultBoundary = fault; } else { fixture.Fault = fault; }
        var first = await fixture.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.False(first.Deleted);
        var tombstone = Assert.Single(await fixture.Store.Environments.ListAsync(TestContext.Current.CancellationToken)).Deletion!;
        Assert.Equal(CanonicalJson.Hash(fixture.Environment), CanonicalJson.Hash(tombstone.Snapshot));
        Assert.Equal(3, tombstone.Plan.Resources.Length); Assert.NotEmpty(tombstone.RetryReason);
        Assert.Equal(3, (await fixture.Ledger.ReadAsync(fixture.Environment.Id, TestContext.Current.CancellationToken)).Count);
        Assert.True(await fixture.Store.CountAsync("PackageReferences") > 0);
        fixture.FaultBoundary = null; fixture.Fault = null;
        var retry = await fixture.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.True(retry.Deleted); Assert.NotEqual(first.Operation!.Plan.Hash, retry.Operation!.Plan.Hash);
        Assert.Equal(2, fixture.Confirmations);
        Assert.Equal(3, fixture.Absent.Count);
        Assert.Equal(fault == "volume-in-use" ? 4 : 3, fixture.Mutations.Count);
    }

    [Fact]
    public async Task Lost_final_commit_acknowledgement_is_resolved_without_replaying_cleanup()
    {
        await using var fixture = await EditFixture.CreateAsync(); fixture.FaultBoundary = "delete-finalization:afterCommit";
        var result = await fixture.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.True(result.Deleted); Assert.Equal(3, fixture.Mutations.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Cancellation_after_each_effect_keeps_visible_remnants_and_safe_retry(int effect)
    {
        await using var fixture = await EditFixture.CreateAsync(); using var cancellation = new CancellationTokenSource();
        fixture.AfterRemoval = () => { if (fixture.Mutations.Count == effect) { cancellation.Cancel(); } };
        var result = await fixture.DeleteAsync(cancellation.Token);
        Assert.False(result.Deleted);
        var item = Assert.Single(await fixture.Store.Environments.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(effect, item.Deletion!.DeletedResources.Length); Assert.Equal(3 - effect, item.Deletion.Remnants.Length);
        fixture.AfterRemoval = null;
        Assert.True((await fixture.DeleteAsync(TestContext.Current.CancellationToken)).Deleted);
        Assert.Equal(3, fixture.Mutations.Count);
    }

    [Fact]
    public async Task Retry_cannot_switch_engine_or_expand_the_confirmed_scope()
    {
        await using var fixture = await EditFixture.CreateAsync(); fixture.Fault = "volume-in-use";
        Assert.False((await fixture.DeleteAsync(TestContext.Current.CancellationToken)).Deleted);
        int mutations = fixture.Mutations.Count; int confirmations = fixture.Confirmations;
        fixture.Fault = null; fixture.ScopeChange = plan => plan with { EngineId = "another-engine" };
        var result = await fixture.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.False(result.Deleted); Assert.Equal("delete-scope-changed", result.Code);
        Assert.Equal(mutations, fixture.Mutations.Count); Assert.Equal(confirmations, fixture.Confirmations);
        Assert.NotNull(Assert.Single(await fixture.Store.Environments.ListAsync(TestContext.Current.CancellationToken)).Deletion);
    }

    [Fact]
    public async Task Restart_keeps_confirmed_tombstone_as_an_admission_blocker_even_without_a_destructive_effect()
    {
        await using var fixture = await EditFixture.CreateAsync(); fixture.FaultBoundary = "delete-begin:afterCommit";
        Assert.False((await fixture.DeleteAsync(TestContext.Current.CancellationToken)).Deleted);
        Assert.Empty(fixture.Mutations);
        fixture.FaultBoundary = null;
        await using var restarted = new OperationCoordinator(fixture.Store.Operations, fixture.Store.Clock, () => true);
        await restarted.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(restarted.AdmissionBlockReason(fixture.Environment.Id));
        Assert.NotNull(Assert.Single(await fixture.Store.Environments.ListAsync(TestContext.Current.CancellationToken)).Deletion);
        Assert.True((await fixture.DeleteAsync(TestContext.Current.CancellationToken)).Deleted);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Crash_between_each_effect_and_result_commit_is_reconciled_without_repeating_removal(int effect)
    {
        await using var fixture = await EditFixture.CreateAsync();
        fixture.AfterRemoval = () => { if (fixture.Mutations.Count == effect) { fixture.FaultBoundary = "result:beforeCommit"; } };
        Assert.False((await fixture.DeleteAsync(TestContext.Current.CancellationToken)).Deleted);
        Assert.Equal(effect, fixture.Mutations.Count);
        Assert.NotNull(Assert.Single(await fixture.Store.Environments.ListAsync(TestContext.Current.CancellationToken)).Deletion);
        fixture.AfterRemoval = null; fixture.FaultBoundary = null;
        Assert.True((await fixture.DeleteAsync(TestContext.Current.CancellationToken)).Deleted);
        Assert.Equal(3, fixture.Mutations.Count);
    }

    [Fact]
    public async Task Crash_immediately_after_durable_confirmation_still_exposes_the_full_tombstone()
    {
        await using var fixture = await EditFixture.CreateAsync(); int writes = 0;
        fixture.BoundaryAction = point =>
        {
            if (point == "operation:beforeCommit" && ++writes == 5) { throw new IOException("crash after confirmation"); }
        };
        Assert.False((await fixture.DeleteAsync(TestContext.Current.CancellationToken)).Deleted);
        var tombstone = Assert.Single(await fixture.Store.Environments.ListAsync(TestContext.Current.CancellationToken)).Deletion;
        Assert.NotNull(tombstone); Assert.Equal(3, tombstone.Remnants.Length); Assert.Empty(fixture.Mutations);
        fixture.BoundaryAction = null;
        Assert.True((await fixture.DeleteAsync(TestContext.Current.CancellationToken)).Deleted);
    }

    [Fact]
    public async Task Cancellation_arriving_after_final_commit_never_reopens_history_or_resource_references()
    {
        await using var fixture = await EditFixture.CreateAsync(); using var cancellation = new CancellationTokenSource();
        fixture.BoundaryAction = point => { if (point == "delete-finalization:afterCommit") { cancellation.Cancel(); } };
        Assert.True((await fixture.DeleteAsync(cancellation.Token)).Deleted);
        Assert.Empty(await fixture.Store.Environments.ListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Store.Operations.ReadUnresolvedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await fixture.Store.CountAsync("PackageReferences"));
        Assert.Equal(0, await fixture.Store.CountAsync("OperationSteps"));
    }

    [Fact]
    public async Task Finalization_never_cascades_unresolved_history_or_releases_its_references()
    {
        await using var fixture = await EditFixture.CreateAsync(configure: WithPack);
        // Inject durable history unknown to the in-memory coordinator to exercise the persistence boundary itself.
        Operation unresolved = await fixture.Store.CreateOperationAsync(fixture.Environment);
        await fixture.Store.Operations.WriteIntentAsync(new(unresolved.Id, "unknown-step", "create", "uncertain-resource"), TestContext.Current.CancellationToken);
        var result = await fixture.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.False(result.Deleted); Assert.Equal("unresolved-history-outside-delete-scope", result.Code);
        Assert.NotNull(Assert.Single(await fixture.Store.Environments.ListAsync(TestContext.Current.CancellationToken)).Deletion);
        Assert.Contains(await fixture.Store.Operations.ReadUnresolvedAsync(TestContext.Current.CancellationToken), o => o.Id == unresolved.Id);
        Assert.True(await fixture.Store.CountAsync("PackageReferences") > 0);
        Assert.Equal(3, (await fixture.Ledger.ReadAsync(fixture.Environment.Id, TestContext.Current.CancellationToken)).Count);
        await fixture.Store.Worker.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT state FROM OperationSteps WHERE operationId=$id AND stepId='unknown-step'";
            command.Parameters.AddWithValue("$id", unresolved.Id.ToString());
            Assert.Equal("Started", command.ExecuteScalar()); return true;
        }, TestContext.Current.CancellationToken);
    }

    private static IsolatedEnvironment WithPack(IsolatedEnvironment environment) => environment with
    {
        Baseline = new(environment.Baseline.Applications.Add(environment.Baseline.Applications[0] with
        { SeedPack = new(Guid.NewGuid(), "1.0.0", new string('a', 64)) }))
    };
}
