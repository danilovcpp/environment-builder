using System.Text.Json;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Infrastructure.Imports;
using EnvironmentBuilder.Infrastructure.Persistence;

namespace EnvironmentBuilder.IntegrationTests.Support;

public sealed class FoundationStore : IAsyncDisposable
{
    private readonly IsolatedTestStore isolated = IsolatedTestStore.Create();
    private FoundationStore() { Directory = new AppDataDirectory(isolated.DataDirectory); }
    public AppDataDirectory Directory { get; }
    public SingleWriterLease Lease { get; private set; } = null!;
    public SqliteWorker Worker { get; private set; } = null!;
    public StrictDocumentReader Reader { get; } = new();
    public IClock Clock { get; } = new SystemClock();
    public EnvironmentStore Environments => new(Worker, Reader, Clock);
    public OperationStore Operations => new(Worker, Clock);
    public static async Task<FoundationStore> CreateAsync(Action<string>? fault = null)
    {
        var store = new FoundationStore();
        try
        {
            store.Lease = await SingleWriterLease.AcquireAsync(store.Directory);
            store.Worker = new SqliteWorker(store.Directory, store.Lease, fault);
            await store.Worker.Ready;
            return store;
        }
        catch { await store.DisposeAsync(); throw; }
    }
    public IsolatedEnvironment Example() => Reader.Read<IsolatedEnvironment>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Contracts/examples/environment.json")));
    public async Task<Operation> CreateOperationAsync(IsolatedEnvironment environment)
    {
        var plan = new OperationPlan(Guid.NewGuid(), environment.Id, environment.Revision, CanonicalJson.Hash(environment), Guid.NewGuid(), CanonicalJson.Hash(environment.Baseline), [], [], [], "test");
        var operation = new Operation(Guid.NewGuid(), environment.Id, "start", OperationState.Planned, Clock.UtcNow, environment.Revision, plan.ConfigurationHash, plan);
        await Operations.SaveAsync(operation, TestContext.Current.CancellationToken);
        return operation;
    }
    public Task<long> CountAsync(string table) => Worker.ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM " + table;
        return (long)command.ExecuteScalar()!;
    }, TestContext.Current.CancellationToken);
    public async ValueTask DisposeAsync()
    {
        if (Worker is not null) { await Worker.DisposeAsync(); }
        if (Lease is not null) { await Lease.DisposeAsync(); }
        isolated.Dispose();
    }
}
