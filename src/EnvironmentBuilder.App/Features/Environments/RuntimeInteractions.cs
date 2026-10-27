using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.App.Features.Environments;

public enum CloseOperationsDecision { Wait, Cancel, Return }
public interface IRuntimeInteractions
{
    Task<bool> ConfirmInputsAsync(IReadOnlyList<InputDelta> delta, OperationPlan plan, CancellationToken token);
    Task<CloseOperationsDecision> DecideCloseAsync(int activeOperations, CancellationToken token);
}
