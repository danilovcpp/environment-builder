using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.Abstractions;

// Per preflight/readiness request; does not execute inputs.
public interface IInputResolver
{
    Task<IReadOnlyList<ReadinessIssue>> CheckAsync(IReadOnlyList<InputReference> references, CancellationToken cancellationToken);
}
