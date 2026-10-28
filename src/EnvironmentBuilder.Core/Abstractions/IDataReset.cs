using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.Core.Abstractions;

// Refresh must retain captured inputs across cleanup, while allocating resources in the new generation.
public interface IRefreshRuntime
{
    Task<IStartSession> PrepareAsync(IsolatedEnvironment environment, Guid operationId, Guid generation, CancellationToken token);
}

public interface IDataResetPersistence
{
    Task FinalizeAsync(Operation operation, CancellationToken token);
}
