using System.Collections.Immutable;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.Operations;

public sealed class StopEnvironmentService(OperationCoordinator coordinator, IStopRuntime runtime, IClock clock)
{
    public OperationHandle Stop(IsolatedEnvironment environment, int timeoutSeconds = 30)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeoutSeconds, 600);
        var initial = new OperationPlan(Guid.NewGuid(), environment.Id, environment.Revision, CanonicalJson.Hash(environment), Guid.Empty,
            CanonicalJson.Hash(environment.Baseline), [], [], ["stop", "verify-quiescence"],
            $"Graceful stop timeout: {timeoutSeconds}s; Docker may terminate the container after this timeout. Data and initialization markers are preserved.");
        var operation = new Operation(Guid.NewGuid(), environment.Id, "stop", OperationState.Planned, clock.UtcNow, environment.Revision, initial.ConfigurationHash, initial);
        return coordinator.Submit(operation, async execution =>
        {
            IStopSession session;
            try { session = await runtime.PrepareAsync(environment, operation.Id, timeoutSeconds, execution.CancellationToken).ConfigureAwait(false); }
            catch (PreflightBlockedException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException) { throw new PreflightBlockedException("stop-preflight-unavailable"); }
            await execution.SetPlanAsync(session.Plan).ConfigureAwait(false);
            await execution.ConfirmAsync(session.Plan.Hash).ConfigureAwait(false);
            await ExecuteAsync(environment, session, execution).ConfigureAwait(false);
        });
    }

    internal static async Task ExecuteAsync(IsolatedEnvironment environment, IStopSession session, OperationExecution execution)
    {
        foreach (OwnedResource container in Order(environment, session.Plan.Resources))
        {
            await execution.ReportAsync(container.ComponentId, "stop").ConfigureAwait(false);
            await session.StopAsync(container, execution, execution.CancellationToken).ConfigureAwait(false);
        }
        await execution.ReportAsync(null, "verify-quiescence").ConfigureAwait(false);
        EnvironmentObservation actual = await session.ReadAsync(execution.CancellationToken).ConfigureAwait(false);
        if (!QuiescenceGuard.Check(actual).ConfirmedQuiescent) { throw new UnresolvedEffectException("stop-not-confirmed"); }
        await execution.ReportAsync(null, "confirmed-quiescent").ConfigureAwait(false);
    }

    public static ImmutableArray<OwnedResource> Order(IsolatedEnvironment environment, IEnumerable<OwnedResource> resources)
    {
        var remaining = environment.Components.Select(c => c.Id).ToList();
        var startOrder = new List<Guid>();
        while (remaining.Count > 0)
        {
            Guid next = remaining.First(id => environment.Dependencies.Where(d => d.DependentId == id).All(d => startOrder.Contains(d.RequiredId)));
            startOrder.Add(next); remaining.Remove(next);
        }
        startOrder.Reverse();
        return resources.Where(r => r.Kind == ResourceKind.Container)
            .OrderBy(r => r.ComponentId.HasValue && startOrder.Contains(r.ComponentId.Value) ? startOrder.IndexOf(r.ComponentId.Value) : int.MaxValue)
            .ThenBy(r => r.ResourceId).ToImmutableArray();
    }
}
