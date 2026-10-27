using EnvironmentBuilder.Core.Abstractions;

namespace EnvironmentBuilder.Core.SeedPacks;

public static class InitializationRetryPolicy
{
    public static bool CanRetry(RetryPolicy policy, int attempt, InitializationResponse response) =>
        policy.Mode is "key" or "idempotent" && !string.IsNullOrWhiteSpace(policy.SafetyEvidence) && attempt < Math.Min(3, policy.MaxAttempts) &&
        (response.Status is 408 or 429 or 502 or 503 or 504 || (!response.OutcomeKnown && response.ErrorCode is "http-transport-unavailable" or "http-timeout-or-cancelled"));
    public static TimeSpan Delay(int attempt, TimeSpan? retryAfter) => TimeSpan.FromMilliseconds(Math.Clamp(retryAfter?.TotalMilliseconds ?? 200 * attempt, 0, 5000));
}
