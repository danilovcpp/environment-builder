using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.Core.Abstractions;

// One operation owns its immutable inputs, Docker authority, secret context and HTTP transport.
public interface IStartRuntime
{
    Task<IStartSession> PrepareAsync(IsolatedEnvironment environment, Guid operationId, CancellationToken token);
}
public interface IStartSession : IAsyncDisposable
{
    PreviewSnapshot Snapshot { get; }
    OperationPlan Plan { get; }
    IInitializationTransport Transport { get; }
    Task PrepareImagesAsync(OperationExecution execution, CancellationToken token);
    Task<ComponentObservation> StartComponentAsync(EnvironmentComponent component, OperationExecution execution, CancellationToken token);
    Task<ComponentObservation> WaitAsync(EnvironmentComponent component, string condition, CancellationToken token);
    Task<Stream?> OpenBodyAsync(BaselineApplication application, InitializationStep step, CancellationToken token);
}
public interface IDataApplicationJournal
{
    Task<DataApplicationRecord?> ReadAsync(DataApplicationKey key, CancellationToken token);
    Task SaveAsync(DataApplicationRecord record, CancellationToken cancellationToken);
}
