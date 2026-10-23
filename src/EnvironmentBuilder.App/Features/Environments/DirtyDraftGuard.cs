namespace EnvironmentBuilder.App.Features.Environments;

public enum DirtyDraftDecision { Stay, Discard, Save }
public sealed class DirtyDraftGuard(Func<CancellationToken, Task<DirtyDraftDecision>> decide)
{
    public async Task<bool> CanLeaveAsync(bool dirty, Func<CancellationToken, Task<bool>> save, CancellationToken cancellationToken)
    {
        if (!dirty) { return true; }
        return await decide(cancellationToken) switch
        {
            DirtyDraftDecision.Discard => true,
            DirtyDraftDecision.Save => await save(cancellationToken),
            _ => false
        };
    }
}
