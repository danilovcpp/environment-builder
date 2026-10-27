namespace EnvironmentBuilder.Core.Operations;

public static class ReadinessEvaluator
{
    public static bool DependencySatisfied(string condition, string status, string health, bool applicationReady) => status == "running" && condition switch
    { "running" => true, "healthy" => health == "healthy", "ready" => applicationReady, _ => false };
    public static bool ApplicationReady(string kind, bool runtimeReady, bool baselineSucceeded) => kind != "none" && runtimeReady && baselineSucceeded;
}
