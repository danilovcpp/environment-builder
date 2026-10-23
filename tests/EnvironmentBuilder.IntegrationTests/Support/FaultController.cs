namespace EnvironmentBuilder.IntegrationTests.Support;

public enum MutationBoundary
{
    BeforeEffect,
    AfterEffect,
}

public sealed class InjectedFaultException(string step, MutationBoundary boundary)
    : IOException($"Injected failure at {step}/{boundary}.");

public sealed class FaultController
{
    private readonly Dictionary<(string Step, MutationBoundary Boundary), int> remainingFaults = [];

    public void FailNext(string step, MutationBoundary boundary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        var point = (step, boundary);
        remainingFaults[point] = remainingFaults.GetValueOrDefault(point) + 1;
    }

    public void Reach(string step, MutationBoundary boundary, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var point = (step, boundary);
        if (remainingFaults.GetValueOrDefault(point) > 0)
        {
            remainingFaults[point]--;
            throw new InjectedFaultException(step, boundary);
        }
    }

    public async Task RunMutationAsync(string step, Func<CancellationToken, Task> effect, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effect);
        Reach(step, MutationBoundary.BeforeEffect, cancellationToken);
        await effect(cancellationToken);
        Reach(step, MutationBoundary.AfterEffect, cancellationToken);
    }
}
