using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.Core.Abstractions;

// Persistence and Docker authority are substituted independently in workflow tests.
public interface IEnvironmentEditing
{
    Task<QuiescenceResult> CheckAsync(IsolatedEnvironment environment, CancellationToken token);
    Task<EditEnvironmentResult> SaveAsync(IsolatedEnvironment original, IsolatedEnvironment candidate,
        Func<OperationPlan, CancellationToken, Task<ConfirmationRecord?>> confirm, CancellationToken token);
}

public interface ICleanupRuntime
{
    Task<ICleanupSession> PrepareAsync(IsolatedEnvironment environment, Guid operationId, CancellationToken token);
}

public interface ICleanupSession
{
    OperationPlan Scope { get; }
    Task<EnvironmentObservation> ReadAsync(CancellationToken token);
    Task RevalidateAsync(CancellationToken token);
    Task RemoveAsync(OperationPlan plan, System.Collections.Immutable.ImmutableArray<string> removed, OwnedResource resource, OperationExecution execution, CancellationToken token);
}

public interface IEditPersistence
{
    Task InvalidateAsync(OperationPlan plan, Guid operationId, CancellationToken token);
    Task FinalizeAsync(Operation operation, CancellationToken token);
}
