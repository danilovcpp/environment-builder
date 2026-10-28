using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.Core.Operations;

internal static class StartWorkflow
{
    internal static async Task ExecuteAsync(IsolatedEnvironment environment, IStartSession session, BaselineExecutor baseline, OperationExecution execution)
    {
        await execution.ReportAsync(null, "images").ConfigureAwait(false);
        await session.PrepareImagesAsync(execution, execution.CancellationToken).ConfigureAwait(false);
        var completed = new Dictionary<Guid, ComponentObservation>();
        var remaining = environment.Components.ToList();
        // Serial execution is bounded to one accepted mutation and stops issuing stages at the first failure.
        while (remaining.Count > 0)
        {
            execution.CancellationToken.ThrowIfCancellationRequested();
            EnvironmentComponent component = remaining.FirstOrDefault(c => environment.Dependencies.Where(d => d.DependentId == c.Id).All(d => completed.ContainsKey(d.RequiredId)))
                ?? throw new InvalidOperationException("No schedulable DAG vertex.");
            foreach (ComponentDependency edge in environment.Dependencies.Where(d => d.DependentId == component.Id))
            {
                ComponentObservation required = completed[edge.RequiredId];
                if (!ReadinessEvaluator.DependencySatisfied(edge.Condition, required.Status, required.Health, required.ApplicationReady))
                {
                    if (edge.Condition == "ready") { throw new InvalidOperationException("Dependency is not application-ready."); }
                    required = await session.WaitAsync(environment.Components.Single(c => c.Id == edge.RequiredId), edge.Condition, execution.CancellationToken).ConfigureAwait(false);
                    if (!ReadinessEvaluator.DependencySatisfied(edge.Condition, required.Status, required.Health, required.ApplicationReady)) { throw new InvalidOperationException("Dependency condition failed."); }
                }
            }
            await execution.ReportAsync(component.Id, "runtime").ConfigureAwait(false);
            await session.ValidateDataGenerationAsync(execution.CancellationToken).ConfigureAwait(false);
            ComponentObservation actual = await session.StartComponentAsync(component, execution, execution.CancellationToken).ConfigureAwait(false);
            string kind = component.DefinitionSnapshot.Readiness.Kind;
            if (kind != "none")
            {
                await execution.ReportAsync(component.Id, "readiness").ConfigureAwait(false);
                actual = await session.WaitAsync(component, kind, execution.CancellationToken).ConfigureAwait(false);
                await execution.ReportAsync(component.Id, "baseline").ConfigureAwait(false);
                await session.ValidateDataGenerationAsync(execution.CancellationToken).ConfigureAwait(false);
                await baseline.ExecuteAsync(environment, component, session, execution.CancellationToken).ConfigureAwait(false);
            }
            completed.Add(component.Id, actual with { RuntimeReady = kind != "none", ApplicationReady = ReadinessEvaluator.ApplicationReady(kind, true, true) });
            remaining.Remove(component);
            await execution.ReportAsync(component.Id, kind == "none" ? "running-readiness-not-configured" : "applicationReady").ConfigureAwait(false);
        }
    }
}
