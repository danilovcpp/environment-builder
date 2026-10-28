using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.App.Features.Environments;

public interface IDestructiveInteractions
{
    Task<ConfirmationRecord?> ConfirmAsync(OperationPlan plan, CancellationToken token);
}
