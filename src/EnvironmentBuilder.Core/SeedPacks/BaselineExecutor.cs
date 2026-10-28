using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.Core.SeedPacks;

public sealed class BaselineExecutor(IDataApplicationJournal journal, IClock clock)
{
    public async Task VerifyAsync(IsolatedEnvironment environment, IStartSession session, CancellationToken token)
    {
        await session.ValidateDataGenerationAsync(token).ConfigureAwait(false);
        foreach (BaselineApplication application in environment.Baseline.Applications)
        {
            foreach (InitializationStep step in application.Initializer.Steps)
            {
                var key = new DataApplicationKey(environment.Id, application.ComponentId, session.Plan.DataGenerationId, session.Plan.BaselineHash,
                    CanonicalJson.Hash(application), CanonicalJson.Hash(application.Initializer), step.Id);
                if ((await journal.ReadAsync(key, token).ConfigureAwait(false))?.State != StepState.Succeeded)
                { throw new UnresolvedEffectException("baseline-not-verified"); }
            }
        }
    }

    public async Task ExecuteAsync(IsolatedEnvironment environment, EnvironmentComponent component, IStartSession session, CancellationToken token)
    {
        foreach (BaselineApplication application in environment.Baseline.Applications.Where(a => a.ComponentId == component.Id))
        {
            foreach (InitializationStep step in application.Initializer.Steps)
            {
                token.ThrowIfCancellationRequested();
                await session.ValidateDataGenerationAsync(token).ConfigureAwait(false);
                var key = new DataApplicationKey(environment.Id, component.Id, session.Plan.DataGenerationId, session.Plan.BaselineHash,
                    CanonicalJson.Hash(application), CanonicalJson.Hash(application.Initializer), step.Id);
                DataApplicationRecord? previous = await journal.ReadAsync(key, token).ConfigureAwait(false);
                if (previous?.State == StepState.Succeeded) { continue; }
                if (previous is not null && (previous.State != StepState.Pending || previous.Attempt != 0)) { throw new UnresolvedEffectException(); }
                if (previous is null) { await journal.SaveAsync(new(key, StepState.Pending, 0, null, null, null, null, null), token).ConfigureAwait(false); }
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                budget.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));
                int attempt = 0;
                while (true)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    attempt++;
                    var started = new DataApplicationRecord(key, StepState.Started, attempt, clock.UtcNow, null, null, null, null);
                    await journal.SaveAsync(started, token).ConfigureAwait(false);
                    InitializationResponse response;
                    try
                    {
                        await using Stream? body = await session.OpenBodyAsync(application, step, budget.Token).ConfigureAwait(false);
                        response = await session.Transport.SendAsync(component.Id, step, key.StableIdempotencyKey, body, budget.Token).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    { response = new(null, false, false, "http-transport-unavailable"); }
                    StepState state = response.OutcomeKnown && response.AssertionsPassed ? StepState.Succeeded : response.OutcomeKnown ? StepState.Failed : StepState.Unknown;
                    await session.ValidateDataGenerationAsync(CancellationToken.None).ConfigureAwait(false);
                    await journal.SaveAsync(started with
                    {
                        State = state,
                        CompletedAt = clock.UtcNow,
                        AcceptedStatus = response.Status,
                        AssertionsPassed = response.AssertionsPassed,
                        ErrorCode = response.ErrorCode
                    }, CancellationToken.None).ConfigureAwait(false);
                    if (state == StepState.Succeeded) { break; }
                    if (!InitializationRetryPolicy.CanRetry(step.Retry, attempt, response)) { throw new UnresolvedEffectException(); }
                    // A durable result precedes every safe retry. The store validates the supplied policy too.
                    if (journal is not ISafeInitializationRetry retry) { throw new UnresolvedEffectException(); }
                    try { await Task.Delay(InitializationRetryPolicy.Delay(attempt, response.RetryAfter), budget.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new UnresolvedEffectException(); }
                    await retry.AuthorizeAsync(key, step.Retry, attempt + 1, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }
}
public interface ISafeInitializationRetry
{
    Task AuthorizeAsync(DataApplicationKey key, RetryPolicy policy, int nextAttempt, CancellationToken token);
}
