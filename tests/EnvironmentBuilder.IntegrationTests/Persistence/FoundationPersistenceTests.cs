using System.Text.Json;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.IntegrationTests.Support;
using Microsoft.Data.Sqlite;

namespace EnvironmentBuilder.IntegrationTests.Persistence;

public sealed class FoundationPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsupported_or_corrupt_database_is_reported_without_replacement(bool unsupported)
    {
        using var isolated = IsolatedTestStore.Create();
        var directory = new AppDataDirectory(isolated.DataDirectory);
        if (unsupported)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = directory.DatabasePath, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE KeepEvidence(value TEXT); INSERT INTO KeepEvidence VALUES('preserve'); PRAGMA user_version=99";
            command.ExecuteNonQuery();
        }
        else { await File.WriteAllTextAsync(directory.DatabasePath, "corrupt database evidence", TestContext.Current.CancellationToken); }
        byte[] original = await File.ReadAllBytesAsync(directory.DatabasePath, TestContext.Current.CancellationToken);
        await using SingleWriterLease lease = await SingleWriterLease.AcquireAsync(directory);
        await using (var worker = new SqliteWorker(directory, lease))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => worker.Ready);
            Assert.False(lease.CanWrite);
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(directory.DatabasePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Component_versions_are_immutable_and_failed_changes_preserve_the_previous_snapshot()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        var library = new ComponentLibraryStore(fixture.Worker, fixture.Reader);
        var definition = fixture.Example().Components[0].DefinitionSnapshot;
        await library.SaveAsync(definition, TestContext.Current.CancellationToken);
        await library.SaveAsync(definition, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DocumentException>(() => library.SaveAsync(definition with { Name = "different content" }, TestContext.Current.CancellationToken));
        var item = Assert.Single(await library.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(CanonicalJson.Hash(definition), CanonicalJson.Hash(item.Document));
    }

    [Fact]
    public async Task Generation_invalidation_is_durable_and_success_keys_do_not_cross_instances_or_applications()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        var environment = fixture.Example();
        await fixture.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken);
        (Guid generation, string baseline) = await fixture.Worker.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,baselineHash FROM DataGenerations";
            using var row = command.ExecuteReader();
            Assert.True(row.Read());
            return (Guid.Parse(row.GetString(0)), row.GetString(1));
        }, TestContext.Current.CancellationToken);
        var key = new DataApplicationKey(environment.Id, environment.Components[0].Id, generation, baseline, CanonicalJson.Hash("1"), CanonicalJson.Hash("2"), "bootstrap");
        var applications = new DataApplicationStore(fixture.Worker);
        var record = new DataApplicationRecord(key, StepState.Pending, 0, null, null, null, null, null);
        await applications.SaveAsync(record, TestContext.Current.CancellationToken);
        record = record with { State = StepState.Started, Attempt = 1, StartedAt = fixture.Clock.UtcNow };
        await applications.SaveAsync(record, TestContext.Current.CancellationToken);
        record = record with { State = StepState.Succeeded, CompletedAt = fixture.Clock.UtcNow, AcceptedStatus = 204, AssertionsPassed = true };
        await applications.SaveAsync(record, TestContext.Current.CancellationToken);
        Assert.True(await applications.HasSuccessAsync(key, TestContext.Current.CancellationToken));
        Assert.False(await applications.HasSuccessAsync(key with { ComponentId = Guid.NewGuid() }, TestContext.Current.CancellationToken));
        Assert.False(await applications.HasSuccessAsync(key with { ApplicationHash = CanonicalJson.Hash("3") }, TestContext.Current.CancellationToken));
        Assert.NotEqual(key.StableIdempotencyKey, (key with { ApplicationHash = CanonicalJson.Hash("3") }).StableIdempotencyKey);
        Operation operation = await fixture.CreateOperationAsync(environment);
        await applications.InvalidateAsync(generation, operation.Id, "confirmed-reset", fixture.Clock.UtcNow, TestContext.Current.CancellationToken);
        Assert.False(await applications.HasSuccessAsync(key, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => applications.SaveAsync(record, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Confirmed_plans_are_immutable_and_candidate_does_not_replace_the_environment()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        var environment = fixture.Example();
        await fixture.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken);
        Operation operation = await fixture.CreateOperationAsync(environment);
        await fixture.Operations.ConfirmAsync(operation, new ConfirmationRecord(operation.Plan.Hash, fixture.Clock.UtcNow), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Operations.ConfirmAsync(operation, new ConfirmationRecord("stale", fixture.Clock.UtcNow), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Operations.SaveAsync(operation with { Plan = operation.Plan with { Consequences = "changed" } }, TestContext.Current.CancellationToken));
        Assert.Equal(environment.Name, Assert.Single(await fixture.Environments.ListAsync(TestContext.Current.CancellationToken)).Name);
        Assert.True(Assert.Single(await fixture.Environments.ListAsync(TestContext.Current.CancellationToken)).HasUnresolvedOperation);
        Assert.Single(await fixture.Operations.ReadUnresolvedAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resource_lifetimes_cannot_be_rebound_to_recreated_runtime_ids()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        var environment = fixture.Example();
        await fixture.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken);
        Guid generation = await fixture.Worker.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM DataGenerations";
            return Guid.Parse((string)command.ExecuteScalar()!);
        }, TestContext.Current.CancellationToken);
        Operation operation = await fixture.CreateOperationAsync(environment);
        Guid installation = await fixture.Worker.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM AppMetadata WHERE key='installationId'";
            return Guid.Parse((string)command.ExecuteScalar()!);
        }, TestContext.Current.CancellationToken);
        var resource = new OwnedResource(Guid.NewGuid(), installation, environment.Id, environment.Components[0].Id,
            ResourceKind.Container, 1, generation, "c-instance", System.Collections.Immutable.ImmutableDictionary<string, string>.Empty,
            "actual-id", null, [], true, operation.Id, true);
        var ledger = new ResourceLedgerStore(fixture.Worker);
        await ledger.SaveAsync(resource, "create", null, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.SaveAsync(resource with { ActualDockerId = "replacement-id" }, null, null, TestContext.Current.CancellationToken));
        Assert.Equal("actual-id", Assert.Single(await ledger.ReadAsync(environment.Id, TestContext.Current.CancellationToken)).ActualDockerId);
    }
}
