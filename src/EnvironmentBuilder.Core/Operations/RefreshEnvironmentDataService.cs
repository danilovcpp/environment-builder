using System.Collections.Immutable;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.Core.Operations;

public sealed class RefreshEnvironmentDataService(ClearEnvironmentDataService clear, BaselinePreflight preflight, BaselineExecutor baseline,
    IStopRuntime stop, OperationCoordinator coordinator, IClock clock)
{
    public OperationHandle Refresh(IsolatedEnvironment environment, Func<OperationPlan, CancellationToken, Task<ConfirmationRecord?>> confirm)
    {
        Operation operation = ClearEnvironmentDataService.CreateOperation(environment, "refresh", clock);
        return coordinator.Submit(operation, async execution =>
        {
            ICleanupSession cleanup = await clear.PrepareAsync(environment, operation.Id, execution.CancellationToken).ConfigureAwait(false);
            OperationPlan plan = await ClearEnvironmentDataService.PlanAsync(environment, cleanup, execution.CancellationToken).ConfigureAwait(false);
            await using IStartSession temporary = await preflight.PrepareAsync(environment, operation.Id, plan.NextDataGenerationId!.Value, execution.CancellationToken).ConfigureAwait(false);
            if (temporary.Plan.EngineId != plan.EngineId || temporary.Plan.Endpoint != plan.Endpoint) { throw new PreflightBlockedException("refresh-authority-changed"); }
            plan = plan with
            {
                Inputs = temporary.Plan.Inputs,
                TemporaryResources = temporary.Plan.Resources,
                TemporaryPorts = ComposeCompiler.Compile(environment).Services.SelectMany(s => s.Ports.Select(p => new TemporaryPort(s.ComponentId, p))).ToImmutableArray(),
                Stages = [.. plan.Stages, "temporary-images", "temporary-start", "readiness", "baseline", "verify-baseline", "temporary-stop", "verify-temporary-stop"],
                Consequences = "Все перечисленные данные будут удалены; спецификация, ревизия и baseline сохраняются. Компоненты и порты временно запускаются для наполнения. Успех требует проверенного baseline и фактической остановки. Ошибка или отмена оставляет частичное восстановление и работающие компоненты для Inspect и явной команды «Остановить». Автоматического отката или остановки при ошибке наполнения нет."
            };
            await clear.ExecuteAsync(plan, operation.Id, cleanup, execution, confirm).ConfigureAwait(false);
            await clear.ResetAsync(execution).ConfigureAwait(false);
            await StartWorkflow.ExecuteAsync(environment, temporary, baseline, execution).ConfigureAwait(false);
            await execution.ReportAsync(null, "verify-baseline").ConfigureAwait(false);
            await baseline.VerifyAsync(environment, temporary, execution.CancellationToken).ConfigureAwait(false);
            IStopSession stopped = await stop.PrepareAsync(environment, operation.Id, 30, execution.CancellationToken).ConfigureAwait(false);
            if (stopped.Plan.EngineId != plan.EngineId || stopped.Plan.Endpoint != plan.Endpoint || stopped.Plan.DataGenerationId != plan.NextDataGenerationId ||
                stopped.Plan.ConfigurationHash != plan.ConfigurationHash ||
                stopped.Plan.Resources.Any(r => !plan.TemporaryResources.Any(t => t.ResourceId == r.ResourceId)) ||
                !stopped.Plan.Resources.Select(r => r.ResourceId).ToHashSet().SetEquals(plan.TemporaryResources.Select(r => r.ResourceId)))
            { throw new UnresolvedEffectException("temporary-scope-changed"); }
            await StopEnvironmentService.ExecuteAsync(environment, stopped, execution).ConfigureAwait(false);
            // Stopped containers alone do not prove the restored volumes survived the final stage.
            await baseline.VerifyAsync(environment, temporary, execution.CancellationToken).ConfigureAwait(false);
        });
    }
}
