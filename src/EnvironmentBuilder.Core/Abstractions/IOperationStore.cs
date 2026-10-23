using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.Core.Abstractions;

// Application-owned; each method returns only after its independent durable commit.
public interface IOperationStore
{
    Task WriteIntentAsync(OperationStep step, CancellationToken cancellationToken);
    Task WriteResultAsync(OperationStep step, EffectObservation result, CancellationToken cancellationToken);
    Task SaveAsync(Operation operation, CancellationToken cancellationToken);
    Task<IReadOnlyList<Operation>> ReadUnresolvedAsync(CancellationToken cancellationToken);
}
