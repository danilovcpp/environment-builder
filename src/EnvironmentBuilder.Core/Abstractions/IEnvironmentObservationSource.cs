using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.Abstractions;

public interface IEnvironmentObservationSource
{
    Task<EnvironmentObservation> ReadAsync(IsolatedEnvironment environment, Guid sessionId, EnvironmentObservation? previous, CancellationToken token);
}
