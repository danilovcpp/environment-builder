using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.App.Shell;

public sealed class ShutdownCoordinator(OperationCoordinator operations, IRuntimeInteractions interactions)
{
    private bool closing;
    public async Task<bool> RequestAsync(Func<CancellationToken, Task<bool>> dirtyGuard, CancellationToken token)
    {
        if (closing) { return false; }
        closing = true;
        try
        {
            if (!await dirtyGuard(token)) { return false; }
            CloseOperationsDecision decision = operations.Active.Count == 0 ? CloseOperationsDecision.Wait : await interactions.DecideCloseAsync(operations.Active.Count, token);
            if (decision == CloseOperationsDecision.Return) { operations.ReturnFromClose(); return false; }
            await operations.PrepareCloseAsync(decision == CloseOperationsDecision.Cancel, token);
            return true;
        }
        finally { closing = false; }
    }
}
