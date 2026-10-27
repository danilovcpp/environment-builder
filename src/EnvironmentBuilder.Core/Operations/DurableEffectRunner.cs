using EnvironmentBuilder.Core.Abstractions;

namespace EnvironmentBuilder.Core.Operations;

public enum EffectBoundary { BeforeIntent, AfterIntent, BeforeEffect, AfterEffect, BeforeObservation, AfterObservation, BeforeResult, AfterResult }

public sealed class DurableEffectRunner(IOperationStore journal, Action<EffectBoundary>? fault = null)
{
    public async Task<StepState> RunAsync(OperationStep step, Func<CancellationToken, Task> effect,
        Func<CancellationToken, Task<EffectObservation>> observe, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        fault?.Invoke(EffectBoundary.BeforeIntent);
        await journal.WriteIntentAsync(step, cancellationToken).ConfigureAwait(false);
        // A cancelled client cannot undo a server mutation. Finish observation and persistence at a safe point.
        EffectObservation observation;
        bool dispatched = false;
        try
        {
            fault?.Invoke(EffectBoundary.AfterIntent);
            fault?.Invoke(EffectBoundary.BeforeEffect);
            dispatched = true;
            await effect(CancellationToken.None).ConfigureAwait(false);
            fault?.Invoke(EffectBoundary.AfterEffect);
            fault?.Invoke(EffectBoundary.BeforeObservation);
            observation = await observe(CancellationToken.None).ConfigureAwait(false);
            fault?.Invoke(EffectBoundary.AfterObservation);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            observation = new EffectObservation(null, false, "effect-outcome-unknown");
            if (dispatched)
            {
                // A failed/timed-out client can still have created a resource. Capture its identity/ledger,
                // but stop scheduling because the stage itself did not complete normally.
                try
                {
                    EffectObservation actual = await observe(CancellationToken.None).ConfigureAwait(false);
                    observation = actual with { Verified = false, ErrorCode = "effect-interrupted-observation-recorded" };
                }
                catch (Exception observationError) when (observationError is not OutOfMemoryException) { }
            }
        }
        try
        {
            fault?.Invoke(EffectBoundary.BeforeResult);
            await journal.WriteResultAsync(step, observation, CancellationToken.None).ConfigureAwait(false);
            fault?.Invoke(EffectBoundary.AfterResult);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return StepState.Unknown; }
        return observation.Verified ? StepState.Succeeded : StepState.Unknown;
    }
}
