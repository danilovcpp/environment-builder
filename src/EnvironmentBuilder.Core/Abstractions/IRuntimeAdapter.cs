using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.Core.Abstractions;

// Operation-scoped engine/endpoint identity; observation never grants mutation permission.
public interface IRuntimeAdapter
{
    Task<EffectObservation> ObserveAsync(OwnedResource resource, CancellationToken cancellationToken);
}
