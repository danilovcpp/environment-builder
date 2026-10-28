using System.Collections.Immutable;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.Operations;

public sealed class ClearEnvironmentDataService(ICleanupRuntime runtime, VerifiedCleanupExecutor cleanup,
    IDataResetPersistence persistence, OperationCoordinator coordinator, IClock clock)
{
    public OperationHandle Clear(IsolatedEnvironment environment, Func<OperationPlan, CancellationToken, Task<ConfirmationRecord?>> confirm)
    {
        Operation operation = CreateOperation(environment, "clear", clock);
        return coordinator.Submit(operation, async execution =>
        {
            ICleanupSession session = await PrepareAsync(environment, operation.Id, execution.CancellationToken).ConfigureAwait(false);
            OperationPlan plan = await PlanAsync(environment, session, execution.CancellationToken).ConfigureAwait(false);
            await ExecuteAsync(plan, operation.Id, session, execution, confirm).ConfigureAwait(false);
            await execution.CompleteAtomicallyAsync(persistence.FinalizeAsync, clock.UtcNow).ConfigureAwait(false);
        });
    }

    internal static Operation CreateOperation(IsolatedEnvironment environment, string kind, IClock clock)
    {
        EnvironmentValidator.Validate(environment);
        Guid operation = Guid.NewGuid();
        var plan = new OperationPlan(operation, environment.Id, environment.Revision, CanonicalJson.Hash(environment), Guid.Empty,
            CanonicalJson.Hash(environment.Baseline), [], [], ["preflight"], "Подготовка плана удаления данных.");
        return new(operation, environment.Id, kind, OperationState.Planned, clock.UtcNow, environment.Revision, plan.ConfigurationHash, plan);
    }

    internal async Task<ICleanupSession> PrepareAsync(IsolatedEnvironment environment, Guid operation, CancellationToken token)
    {
        try { return await runtime.PrepareAsync(environment, operation, token).ConfigureAwait(false); }
        catch (PreflightBlockedException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException) { throw new PreflightBlockedException("cleanup-preflight-unavailable"); }
    }

    internal static async Task<OperationPlan> PlanAsync(IsolatedEnvironment environment, ICleanupSession session, CancellationToken token)
    {
        OperationPlan edit = DestructivePlanBuilder.Build(environment, environment, session.Scope, await session.ReadAsync(token).ConfigureAwait(false));
        return edit with
        {
            Candidate = null,
            Inputs = [],
            Packs = environment.Baseline.Applications.Where(a => a.SeedPack is not null).Select(a => a.SeedPack!).Distinct().ToImmutableArray(),
            Stages = ["verify-quiescence", "invalidate-generation", "remove-containers", "remove-volumes", "remove-network", "verify-absence", "reset-generation"],
            Consequences = "Все перечисленные хранилища и отметки наполнения будут удалены. Спецификация, ревизия и baseline сохраняются. Окружение останется остановленным и неинициализированным. Следующий Start создаст и наполнит данные заново. Удалённые данные не восстанавливаются."
        };
    }

    internal async Task ExecuteAsync(OperationPlan plan, Guid operation, ICleanupSession session, OperationExecution execution,
        Func<OperationPlan, CancellationToken, Task<ConfirmationRecord?>> confirm)
    {
        await execution.SetPlanAsync(plan).ConfigureAwait(false);
        ConfirmationRecord? decision = await confirm(plan, execution.CancellationToken).ConfigureAwait(false);
        if (decision is null) { throw new OperationCanceledException("Confirmation declined."); }
        if (decision.PlanHash != plan.Hash) { throw new PreflightBlockedException("stale-plan"); }
        execution.CancellationToken.ThrowIfCancellationRequested();
        await session.RevalidateAsync(execution.CancellationToken).ConfigureAwait(false);
        DestructivePlanBuilder.RequireCurrent(plan, await session.ReadAsync(execution.CancellationToken).ConfigureAwait(false));
        await execution.ConfirmPlanAsync(decision).ConfigureAwait(false);
        await cleanup.ExecuteAsync(plan, operation, session, execution).ConfigureAwait(false);
    }

    internal Task ResetAsync(OperationExecution execution) => execution.CommitDataResetAsync(persistence.FinalizeAsync);
}
