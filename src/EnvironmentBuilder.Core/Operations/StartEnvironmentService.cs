using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.Core.Operations;

public sealed class StartEnvironmentService(OperationCoordinator coordinator, IStartRuntime runtime, BaselineExecutor baseline, IClock clock)
{
    public OperationHandle Start(IsolatedEnvironment environment, PreviewSnapshot? reviewed, Func<IReadOnlyList<InputDelta>, OperationPlan, CancellationToken, Task<bool>> confirm)
    {
        EnvironmentValidator.Validate(environment);
        var initial = new OperationPlan(Guid.NewGuid(), environment.Id, environment.Revision, CanonicalJson.Hash(environment), Guid.Empty,
            CanonicalJson.Hash(environment.Baseline), [], [], ["preflight", "images", "runtime", "readiness", "baseline"], "Start may leave running components after failure or cancellation.");
        var operation = new Operation(Guid.NewGuid(), environment.Id, "start", OperationState.Planned, clock.UtcNow, environment.Revision, initial.ConfigurationHash, initial);
        return coordinator.Submit(operation, async execution =>
        {
            IStartSession prepared;
            try { prepared = await runtime.PrepareAsync(environment, operation.Id, execution.CancellationToken).ConfigureAwait(false); }
            catch (NotSupportedException) { throw new PreflightBlockedException("runtime-capability-unsupported"); }
            catch (PreflightBlockedException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { throw new PreflightBlockedException("required-input-or-runtime-unavailable"); }
            await using IStartSession session = prepared;
            await execution.SetPlanAsync(session.Plan).ConfigureAwait(false);
            var delta = StartPreflight.Delta(reviewed, session.Snapshot);
            if (delta.Length > 0 && !await confirm(delta, session.Plan, execution.CancellationToken).ConfigureAwait(false)) { throw new OperationCanceledException("Input review was declined."); }
            await execution.ConfirmAsync(session.Snapshot.Token).ConfigureAwait(false);
            await session.ValidateDataGenerationAsync(execution.CancellationToken).ConfigureAwait(false);
            await StartWorkflow.ExecuteAsync(environment, session, baseline, execution).ConfigureAwait(false);
        });
    }
}
