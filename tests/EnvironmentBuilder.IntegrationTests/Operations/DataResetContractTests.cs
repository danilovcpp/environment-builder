using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Operations;

public sealed class DataResetContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clear_preserves_configuration_retires_generation_and_removes_all_owned_stores(bool absent)
    {
        await using var fixture = await DataResetFixture.CreateAsync();
        Guid before = await fixture.Edit.GenerationAsync();
        if (absent) { fixture.Edit.Absent.UnionWith(fixture.Edit.Resources.Select(r => r.ResourceId)); }
        var operation = await fixture.RunAsync(refresh: false);
        Assert.Equal(OperationState.Succeeded, operation.State);
        Assert.Null(operation.Plan.Candidate);
        Assert.Equal(fixture.Edit.Resources.Length, operation.Plan.Resources.Length);
        Assert.Equal("retired", await fixture.Edit.ValidityAsync(before));
        Assert.NotEqual(before, await fixture.Edit.GenerationAsync());
        Assert.Empty(await fixture.Edit.Ledger.ReadAsync(fixture.Environment.Id, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Events);
        var journal = new EnvironmentBuilder.Infrastructure.Persistence.DataApplicationStore(fixture.Edit.Store.Worker);
        foreach (var key in fixture.OldApplications) { Assert.False(await journal.HasSuccessAsync(key, TestContext.Current.CancellationToken)); }
        Assert.Equal(absent ? 0 : 3, fixture.Edit.Mutations.Count);
        await fixture.AssertConfigurationAsync();
    }

    [Theory]
    [InlineData("mandatory")]
    [InlineData("empty")]
    [InlineData("pack")]
    public async Task Refresh_preflights_before_cleanup_applies_fresh_baseline_and_verifies_stop(string baseline)
    {
        await using var fixture = await DataResetFixture.CreateAsync(baseline);
        fixture.Edit.BeforeRemoval = () => { Assert.Contains("preflight", fixture.Events); Assert.DoesNotContain("start", fixture.Events); return Task.CompletedTask; };
        var operation = await fixture.RunAsync(refresh: true);
        Assert.Equal(OperationState.Succeeded, operation.State);
        Assert.True(operation.DataResetCompleted);
        Assert.False(fixture.Running);
        Assert.Equal(fixture.ExpectedCount, fixture.BusinessCount);
        Assert.Contains("verify-temporary-stop", operation.Plan.Stages);
        Assert.NotEmpty(operation.Plan.TemporaryResources);
        Assert.NotEmpty(operation.Plan.TemporaryPorts);
        Assert.Equal(operation.Plan.NextDataGenerationId, await fixture.Edit.GenerationAsync());
        Assert.Contains("verified-stop", fixture.Events);
        await fixture.AssertConfigurationAsync();
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("secret")]
    [InlineData("missing-pack")]
    [InlineData("apply-contract")]
    [InlineData("corrupt-pack")]
    public async Task Unavailable_baseline_blocks_before_confirmation_and_deletion(string fault)
    {
        await using var fixture = await DataResetFixture.CreateAsync("pack"); fixture.Fault = fault;
        Guid generation = await fixture.Edit.GenerationAsync();
        var operation = await fixture.RunAsync(refresh: true);
        Assert.Equal(OperationState.Failed, operation.State);
        Assert.Empty(fixture.Edit.Mutations); Assert.Equal(0, fixture.Confirmations);
        Assert.Equal(generation, await fixture.Edit.GenerationAsync());
        Assert.Equal(11, fixture.BusinessCount);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("readiness")]
    [InlineData("stop")]
    [InlineData("unknown-stop")]
    [InlineData("lost-volume")]
    public async Task Failed_restoration_is_partial_and_never_claims_stopped_success(string fault)
    {
        await using var fixture = await DataResetFixture.CreateAsync(); fixture.Fault = fault;
        var operation = await fixture.RunAsync(refresh: true);
        Assert.NotEqual(OperationState.Succeeded, operation.State);
        Assert.True(operation.DataResetCompleted); Assert.Equal(fault != "lost-volume", fixture.Running);
        if (fault is "http" or "readiness") { Assert.DoesNotContain("stop", fixture.Events); }
        Assert.NotNull(fixture.Edit.Coordinator.AdmissionBlockReason(fixture.Environment.Id));
        // An explicit Stop remains admissible after partial initialization.
        fixture.Fault = null;
        var stop = fixture.Stop.Stop(fixture.Environment); await stop.Completion;
        Assert.Equal(OperationState.Succeeded, stop.Current.State); Assert.False(fixture.Running);
        await fixture.AssertConfigurationAsync();
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("stale-token")]
    [InlineData("foreign-stopped-consumer")]
    public async Task Declined_stale_or_unsafe_scope_has_zero_effects(string fault)
    {
        await using var fixture = await DataResetFixture.CreateAsync();
        fixture.Edit.Fault = fault;
        var operation = await fixture.RunAsync(refresh: false);
        Assert.NotEqual(OperationState.Succeeded, operation.State); Assert.Empty(fixture.Edit.Mutations);
        Assert.Equal("active", await fixture.Edit.ValidityAsync(await fixture.Edit.GenerationAsync()));
    }

    [Theory]
    [InlineData("data-reset-finalization:beforeCommit", false)]
    [InlineData("data-reset-finalization:afterCommit", true)]
    public async Task Generation_finalization_is_atomic_and_resolves_lost_commit_acknowledgement(string fault, bool success)
    {
        await using var fixture = await DataResetFixture.CreateAsync(); fixture.Edit.FaultBoundary = fault;
        Guid previous = await fixture.Edit.GenerationAsync();
        var operation = await fixture.RunAsync(refresh: false);
        Assert.Equal(success, operation.State == OperationState.Succeeded);
        Assert.Equal(success ? "retired" : "invalidated", await fixture.Edit.ValidityAsync(previous));
        Assert.Equal(success, previous != await fixture.Edit.GenerationAsync());
        Assert.Equal(3, fixture.Edit.Mutations.Count);
        await fixture.AssertConfigurationAsync();
    }

    [Theory]
    [InlineData("generation-invalidation:beforeCommit", 0)]
    [InlineData("intent:beforeCommit", 0)]
    [InlineData("result:beforeCommit", 1)]
    public async Task Cleanup_fault_keeps_old_generation_and_precise_remnants(string fault, int expectedEffects)
    {
        await using var fixture = await DataResetFixture.CreateAsync(); fixture.Edit.FaultBoundary = fault;
        Guid generation = await fixture.Edit.GenerationAsync();
        var operation = await fixture.RunAsync(refresh: true);
        Assert.NotEqual(OperationState.Succeeded, operation.State); Assert.False(operation.DataResetCompleted);
        Assert.Equal(expectedEffects, fixture.Edit.Mutations.Count); Assert.NotEmpty(operation.Remnants);
        Assert.Equal(generation, await fixture.Edit.GenerationAsync()); Assert.DoesNotContain("start", fixture.Events);
        await fixture.AssertConfigurationAsync();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Cancellation_after_each_cleanup_effect_retains_invalidated_generation_and_actual_progress(int count)
    {
        await using var fixture = await DataResetFixture.CreateAsync();
        fixture.Edit.AfterRemoval = () => { if (fixture.Edit.Mutations.Count == count) { fixture.Cancel(); } };
        var operation = await fixture.RunAsync(refresh: true);
        Assert.Equal(OperationState.PartiallyCompleted, operation.State);
        Assert.Equal(count, fixture.Edit.Mutations.Count); Assert.Equal(count, operation.DeletedResources.Length);
        Assert.Equal(3 - count, operation.Remnants.Length); Assert.False(operation.DataResetCompleted);
        Assert.Equal("invalidated", await fixture.Edit.ValidityAsync(await fixture.Edit.GenerationAsync()));
    }
}
