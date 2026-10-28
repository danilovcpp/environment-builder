using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Docker;

public sealed class OwnershipDeletionTests
{
    [Theory]
    [InlineData("copied-container-labels")]
    [InlineData("copied-volume-labels")]
    [InlineData("volume-created")]
    [InlineData("volume-driver")]
    [InlineData("volume-scope")]
    [InlineData("volume-options")]
    [InlineData("volume-evidence-missing")]
    [InlineData("foreign-stopped-consumer")]
    [InlineData("foreign-network-attachment")]
    [InlineData("unexpected-owned")]
    [InlineData("unexpected-volume")]
    [InlineData("unexpected-network")]
    [InlineData("network-driver")]
    [InlineData("network-scope")]
    public async Task Conflicting_identity_incarnation_or_consumers_blocks_deletion(string fault)
    {
        await using var fixture = await EditFixture.CreateAsync(); fixture.Fault = fault;
        var result = await fixture.SaveAsync(fixture.Environment with { Name = "Candidate" }, TestContext.Current.CancellationToken);
        Assert.False(result.Saved); Assert.Empty(fixture.Mutations); Assert.Equal(1, (await fixture.SavedAsync()).Revision);
    }

    [Fact]
    public async Task Exact_removal_preserves_second_copy_and_foreign_lookalike()
    {
        await using var fixture = await EditFixture.CreateAsync();
        var result = await fixture.SaveAsync(fixture.Environment with { Name = "Selected copy" }, TestContext.Current.CancellationToken);
        Assert.True(result.Saved);
        Assert.Equal(new[] { "container", "volume", "network" }, fixture.Mutations.Select(m => m[0]));
        Assert.All(fixture.Mutations, m => { Assert.Equal(3, m.Count); Assert.Equal("rm", m[1]); Assert.Contains(m[2], fixture.Resources.Select(r => r.ActualDockerId)); });
        Assert.DoesNotContain(fixture.Mutations.SelectMany(m => m), argument => argument is "--force" or "-f" or "prune" or "down" or "--remove-orphans" or "--rmi");
        Assert.True(fixture.ForeignIntact);
    }

    [Theory]
    [InlineData("engine-drift")]
    [InlineData("consumer-drift")]
    [InlineData("ledger-drift")]
    [InlineData("revision-drift")]
    [InlineData("state-drift")]
    public async Task Material_change_after_confirmation_requires_a_new_review(string fault)
    {
        await using var fixture = await EditFixture.CreateAsync(); fixture.AfterConfirmationFault = fault;
        var result = await fixture.SaveAsync(fixture.Environment with { Name = "Candidate" }, TestContext.Current.CancellationToken);
        Assert.False(result.Saved); Assert.Equal(1, fixture.Confirmations); Assert.Empty(fixture.Mutations);
    }

    [Fact]
    public async Task Partial_cleanup_retains_authoritative_snapshot_and_exact_remnants()
    {
        await using var fixture = await EditFixture.CreateAsync(); fixture.Fault = "volume-in-use";
        var result = await fixture.SaveAsync(fixture.Environment with { Name = "Candidate" }, TestContext.Current.CancellationToken);
        Assert.False(result.Saved); Assert.Equal(1, (await fixture.SavedAsync()).Revision);
        Assert.Single(result.Operation!.DeletedResources); Assert.Equal(2, result.Operation.Remnants.Length);
        Assert.Equal("invalidated", await fixture.ValidityAsync(await fixture.GenerationAsync())); Assert.True(fixture.ForeignIntact);
    }
}
