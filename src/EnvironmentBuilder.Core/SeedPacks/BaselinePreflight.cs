using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.Core.SeedPacks;

public sealed class BaselinePreflight(IInputResolver inputs, ISeedPackStore packs, IRefreshRuntime runtime)
{
    public async Task<IStartSession> PrepareAsync(IsolatedEnvironment environment, Guid operation, Guid generation, CancellationToken token)
    {
        try
        {
            IReadOnlyList<ReadinessIssue> issues = await EnvironmentReadiness.CheckAsync(environment, inputs, packs, token).ConfigureAwait(false);
            ReadinessIssue? blocker = issues.FirstOrDefault(i => i.Code != "image-identity-unknown");
            if (blocker is not null) { throw new PreflightBlockedException(blocker.Code); }
            IStartSession session = await runtime.PrepareAsync(environment, operation, generation, token).ConfigureAwait(false);
            if (session.Plan.EnvironmentId != environment.Id || session.Plan.Revision != environment.Revision ||
                session.Plan.ConfigurationHash != CanonicalJson.Hash(environment) || session.Plan.DataGenerationId != generation || session.Plan.BaselineHash != CanonicalJson.Hash(environment.Baseline))
            { await session.DisposeAsync().ConfigureAwait(false); throw new PreflightBlockedException("refresh-input-plan-mismatch"); }
            return session;
        }
        catch (PreflightBlockedException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (DocumentException error) { throw new PreflightBlockedException(error.Code); }
        catch (Exception error) when (error is not OutOfMemoryException) { throw new PreflightBlockedException("baseline-preflight-unavailable"); }
    }
}
