using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Persistence;

public sealed class DurableBoundaryTests
{
    [Theory]
    [InlineData(EffectBoundary.BeforeIntent, 0)]
    [InlineData(EffectBoundary.AfterIntent, 0)]
    [InlineData(EffectBoundary.BeforeEffect, 0)]
    [InlineData(EffectBoundary.AfterEffect, 1)]
    [InlineData(EffectBoundary.BeforeObservation, 1)]
    [InlineData(EffectBoundary.AfterObservation, 1)]
    [InlineData(EffectBoundary.BeforeResult, 1)]
    [InlineData(EffectBoundary.AfterResult, 1)]
    public async Task Faults_at_each_durable_boundary_stop_scheduling_without_replaying_the_effect(EffectBoundary boundary, int expectedEffects)
    {
        var journal = new FaultJournal();
        var runner = new DurableEffectRunner(journal, point => { if (point == boundary) { throw new IOException("fault"); } });
        int effects = 0;
        var step = new OperationStep(Guid.NewGuid(), "create", "create", "target");
        Task<StepState> Run() => runner.RunAsync(step, _ => { effects++; return Task.CompletedTask; },
            _ => Task.FromResult(new EffectObservation("actual", true)), TestContext.Current.CancellationToken);
        if (boundary == EffectBoundary.BeforeIntent) { await Assert.ThrowsAsync<IOException>(Run); }
        else { Assert.Equal(StepState.Unknown, await Run()); }
        Assert.Equal(expectedEffects, effects);
    }
    [Fact]
    public async Task Environment_json_summary_generation_and_references_commit_atomically()
    {
        bool fail = true;
        await using FoundationStore store = await FoundationStore.CreateAsync(boundary =>
        { if (fail && boundary == "environment:beforeCommit") { throw new IOException("disk full"); } });
        IsolatedEnvironment environment = store.Example();
        await Assert.ThrowsAsync<IOException>(() => store.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken));
        Assert.Equal(0, await store.CountAsync("Environments"));
        Assert.Equal(0, await store.CountAsync("DataGenerations"));
        Assert.Equal(0, await store.CountAsync("PackageReferences"));
        fail = false;
        await store.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken);
        await store.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken);
        Assert.Equal(1, await store.CountAsync("Environments"));
        Assert.Equal(1, await store.CountAsync("DataGenerations"));
        var item = Assert.Single(await store.Environments.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(CanonicalJson.Hash(environment), CanonicalJson.Hash(item.Document));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Environments.SaveNewAsync(environment with { Name = "changed" }, TestContext.Current.CancellationToken));
        Assert.Equal(environment.Name, Assert.Single(await store.Environments.ListAsync(TestContext.Current.CancellationToken)).Name);
    }

    [Fact]
    public async Task One_corrupt_or_unsupported_row_does_not_hide_valid_rows()
    {
        await using FoundationStore store = await FoundationStore.CreateAsync();
        IsolatedEnvironment good = store.Example();
        IsolatedEnvironment corrupt = good with { Id = Guid.NewGuid() };
        IsolatedEnvironment future = good with { Id = Guid.NewGuid() };
        foreach (var environment in new[] { good, corrupt, future }) { await store.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken); }
        await store.Worker.CommitAsync("fixture", (connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE Environments SET json=$json WHERE id=$id";
            command.Parameters.AddWithValue("$json", "{");
            command.Parameters.AddWithValue("$id", corrupt.Id.ToString());
            command.ExecuteNonQuery();
            command.Parameters["$json"].Value = System.Text.Json.JsonSerializer.Serialize(future, CanonicalJson.Options).Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal);
            command.Parameters["$id"].Value = future.Id.ToString();
            return command.ExecuteNonQuery();
        }, TestContext.Current.CancellationToken);
        var items = await store.Environments.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, items.Count);
        Assert.Single(items, i => i.Document is not null);
        Assert.Contains(items, i => i.Id == corrupt.Id && i.ErrorCode == "invalid-json");
        Assert.Contains(items, i => i.Id == future.Id && i.ErrorCode == "unsupported-version");
    }

    [Fact]
    public async Task Durable_started_survives_a_failed_result_and_writer_loss_prevents_another_effect()
    {
        bool failResult = false;
        await using FoundationStore store = await FoundationStore.CreateAsync(boundary =>
        { if (failResult && boundary == "result:beforeCommit") { throw new IOException("disk full"); } });
        var environment = store.Example();
        await store.Environments.SaveNewAsync(environment, TestContext.Current.CancellationToken);
        Operation operation = await store.CreateOperationAsync(environment);
        var step = new OperationStep(operation.Id, "create", "create", "resource");
        failResult = true;
        int effects = 0;
        var runner = new DurableEffectRunner(store.Operations);
        Assert.Equal(StepState.Unknown, await runner.RunAsync(step, _ => { effects++; return Task.CompletedTask; },
            _ => Task.FromResult(new EffectObservation("actual-id", true)), TestContext.Current.CancellationToken));
        Assert.Equal(1, effects);
        string state = await store.Worker.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT state FROM OperationSteps";
            return (string)command.ExecuteScalar()!;
        }, TestContext.Current.CancellationToken);
        Assert.Equal("Started", state);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(step, _ => { effects++; return Task.CompletedTask; },
            _ => Task.FromResult(new EffectObservation("actual-id", true)), TestContext.Current.CancellationToken));
        store.Lease.Revoke();
        await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(step with { StepId = "other" }, _ => { effects++; return Task.CompletedTask; },
            _ => Task.FromResult(new EffectObservation("actual-id", true)), TestContext.Current.CancellationToken));
        Assert.Equal(1, effects);
    }

    [Fact]
    public async Task Store_uses_a_supported_native_engine_and_excludes_a_second_writer()
    {
        using var store = IsolatedTestStore.Create();
        var directory = new AppDataDirectory(store.DataDirectory);
        await using SingleWriterLease lease = await SingleWriterLease.AcquireAsync(directory);
        await Assert.ThrowsAsync<IOException>(() => SingleWriterLease.AcquireAsync(directory));
        await using var worker = new SqliteWorker(directory, lease);
        await worker.Ready;
        Assert.True(Version.Parse(worker.NativeVersion) >= new Version(3, 51, 3));
        Assert.Equal(1L, await worker.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys";
            return (long)command.ExecuteScalar()!;
        }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_intent_prevents_effect_and_failed_result_remains_unknown()
    {
        var journal = new FaultJournal { FailIntent = true };
        var runner = new DurableEffectRunner(journal);
        int effects = 0;
        var step = new OperationStep(Guid.NewGuid(), "create", "create", "resource");
        await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(step,
            _ => { effects++; return Task.CompletedTask; }, _ => Task.FromResult(new EffectObservation("resource-id", true)),
            TestContext.Current.CancellationToken));
        Assert.Equal(0, effects);
        journal.FailIntent = false;
        journal.FailResult = true;
        Assert.Equal(StepState.Unknown, await runner.RunAsync(step,
            _ => { effects++; return Task.CompletedTask; }, _ => Task.FromResult(new EffectObservation("resource-id", true)),
            TestContext.Current.CancellationToken));
        Assert.Equal(1, effects);
        Assert.Equal(StepState.Started, journal.Persisted);
    }

    private sealed class FaultJournal : IOperationStore
    {
        public bool FailIntent { get; set; }
        public bool FailResult { get; set; }
        public StepState Persisted { get; private set; }
        public Task SaveAsync(Operation operation, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<Operation>> ReadUnresolvedAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Operation>>([]);
        public Task WriteIntentAsync(OperationStep step, CancellationToken cancellationToken)
        {
            if (FailIntent) { throw new IOException("intent fault"); }
            Persisted = StepState.Started;
            return Task.CompletedTask;
        }
        public Task WriteResultAsync(OperationStep step, EffectObservation result, CancellationToken cancellationToken)
        {
            if (FailResult) { throw new IOException("result fault"); }
            Persisted = result.Verified ? StepState.Succeeded : StepState.Unknown;
            return Task.CompletedTask;
        }
    }
}
