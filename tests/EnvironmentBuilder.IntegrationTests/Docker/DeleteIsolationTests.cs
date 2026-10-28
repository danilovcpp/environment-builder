using System.Text.Json;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Docker;

public sealed class DeleteIsolationTests
{
    [Fact]
    public async Task Independent_partial_delete_and_retry_record_exact_scope_and_isolation_evidence()
    {
        await using var fixture = await EditFixture.CreateAsync(); fixture.Running = true; fixture.Fault = "volume-in-use";
        var second = fixture.Environment with { Id = fixture.SecondEnvironmentId, Name = "Independent second copy" };
        await fixture.Store.Environments.SaveNewAsync(second, TestContext.Current.CancellationToken);
        string source = Path.Combine(fixture.Store.Directory.Root, "source.txt");
        await File.WriteAllTextAsync(source, "source unchanged", TestContext.Current.CancellationToken);
        var partial = await fixture.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.False(partial.Deleted);
        var tombstone = (await fixture.Store.Environments.ListAsync(TestContext.Current.CancellationToken)).Single(i => i.Id == fixture.Environment.Id).Deletion!;
        Assert.NotNull(tombstone); Assert.Equal(2, tombstone.Remnants.Length); Assert.Single(tombstone.DeletedResources);
        fixture.Fault = null;
        var completed = await fixture.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.True(completed.Deleted); Assert.True(fixture.ForeignIntact);
        Assert.Equal(5, fixture.Mutations.Count); Assert.Equal("stop", fixture.Mutations[0][1]);
        var remaining = await fixture.Store.Environments.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CanonicalJson.Hash(second), CanonicalJson.Hash(Assert.Single(remaining).Document));
        Assert.Equal("source unchanged", await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Ledger.ReadAsync(fixture.Environment.Id, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fixture.Calls, c => c.Contains("prune") || c.Contains("down") || c.Contains("--volumes") || c.Contains("--remove-orphans") || c[0] is "image" or "build" or "rmi");
        string? artifacts = Environment.GetEnvironmentVariable("ENVIRONMENTBUILDER_ACCEPTANCE_ARTIFACTS");
        if (artifacts is not null)
        {
            Directory.CreateDirectory(artifacts);
            await File.WriteAllTextAsync(Path.Combine(artifacts, "delete.json"), JsonSerializer.Serialize(new
            {
                scenario = "US6 / SC-007",
                status = "Passed",
                mode = "isolated SQLite and production Docker sessions with stateful CLI double",
                checkedAt = DateTimeOffset.UtcNow,
                environmentId = fixture.Environment.Id,
                secondEnvironmentId = second.Id,
                partialOperationId = partial.Operation!.Id,
                retryOperationId = completed.Operation!.Id,
                confirmedResources = partial.Operation.Plan.Resources.Select(r => new { r.ResourceId, kind = r.Kind.ToString(), r.ActualDockerId }),
                partialDeleted = tombstone.DeletedResources,
                partialRemnants = tombstone.Remnants,
                retainedSnapshotHash = CanonicalJson.Hash(tombstone.Snapshot),
                newConfirmation = partial.Operation.Plan.Hash != completed.Operation.Plan.Hash,
                actualMutationCommands = fixture.Mutations,
                finalRemnants = completed.Operation.Remnants,
                finalLedgerCount = 0,
                finalVisibleEnvironmentIds = remaining.Select(e => e.Id),
                foreignLookalikesIntact = fixture.ForeignIntact,
                secondCopyHash = CanonicalJson.Hash(second),
                sourceContents = "source unchanged",
                globalCleanupCalls = 0,
                imageMutationCalls = 0,
                sourceMutationCalls = 0,
                stoppedBeforeRemoval = fixture.Mutations[0][1] == "stop"
            }, new JsonSerializerOptions { WriteIndented = true }), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Running_copy_is_stopped_before_removal_and_second_copy_sources_and_lookalikes_survive()
    {
        await using var fixture = await EditFixture.CreateAsync(); fixture.Fault = "running";
        var second = fixture.Environment with { Id = fixture.SecondEnvironmentId, Name = "Second running copy" };
        await fixture.Store.Environments.SaveNewAsync(second, TestContext.Current.CancellationToken);
        string source = Path.Combine(fixture.Store.Directory.Root, "source.txt");
        await File.WriteAllTextAsync(source, "immutable source", TestContext.Current.CancellationToken);
        var result = await fixture.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.True(result.Deleted); Assert.Equal("stop", fixture.Mutations[0][1]);
        Assert.Equal(4, fixture.Mutations.Count); Assert.True(fixture.ForeignIntact);
        Assert.All(fixture.Mutations.Skip(1), args => Assert.Equal("rm", args[1]));
        Assert.Equal(CanonicalJson.Hash(second), CanonicalJson.Hash(Assert.Single(await fixture.Store.Environments.ListAsync(TestContext.Current.CancellationToken)).Document));
        Assert.Equal("immutable source", await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("foreign-stopped-consumer")]
    [InlineData("copied-container-labels")]
    [InlineData("copied-volume-labels")]
    [InlineData("unexpected-owned")]
    [InlineData("foreign-network-attachment")]
    public async Task Unsafe_authority_blocks_before_any_stop_or_removal(string fault)
    {
        await using var fixture = await EditFixture.CreateAsync(); fixture.Fault = fault;
        var result = await fixture.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.False(result.Deleted); Assert.Empty(fixture.Mutations); Assert.True(fixture.ForeignIntact);
    }

    [Fact]
    public async Task Unverified_stop_prevents_every_removal_and_keeps_tombstone()
    {
        await using var fixture = await EditFixture.CreateAsync(); fixture.Fault = "stop-failed";
        Assert.False((await fixture.DeleteAsync(TestContext.Current.CancellationToken)).Deleted);
        Assert.Single(fixture.Mutations); Assert.Equal("stop", fixture.Mutations[0][1]);
        Assert.NotNull(Assert.Single(await fixture.Store.Environments.ListAsync(TestContext.Current.CancellationToken)).Deletion);
    }
}
