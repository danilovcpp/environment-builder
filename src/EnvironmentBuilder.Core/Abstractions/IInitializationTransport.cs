using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.Core.Abstractions;

// Operation-owned transport; caller owns payload stream, response exposes sanitized outcomes only.
public interface IInitializationTransport
{
    Task<InitializationResponse> SendAsync(Guid componentId, InitializationStep step, string idempotencyKey, Stream? body, CancellationToken cancellationToken);
}
public sealed record InitializationResponse(int? Status, bool AssertionsPassed, bool OutcomeKnown, string? ErrorCode)
{
    public TimeSpan? RetryAfter { get; init; }
}
