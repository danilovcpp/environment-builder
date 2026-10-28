using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.Core.Abstractions;

// A pinned Docker authority and exact resource scope, owned by one coordinated operation.
public interface IStopRuntime
{
    Task<IStopSession> PrepareAsync(IsolatedEnvironment environment, Guid operationId, int timeoutSeconds, CancellationToken token);
}

public interface IStopSession
{
    OperationPlan Plan { get; }
    Task<EnvironmentObservation> ReadAsync(CancellationToken token);
    Task StopAsync(OwnedResource container, OperationExecution execution, CancellationToken token);
}
