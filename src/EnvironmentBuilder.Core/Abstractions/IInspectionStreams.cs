using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.Abstractions;

public sealed record InspectionStreamsSnapshot(IReadOnlyList<string> Lines, long Dropped, long Truncated, string LogState,
    IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, Measurement>> Measurements);
public interface IInspectionStreamsFactory
{
    IInspectionStreams Create(IsolatedEnvironment environment);
}
public interface IInspectionStreams : IAsyncDisposable
{
    Task<InspectionStreamsSnapshot> ReadAsync(EnvironmentObservation observation, Guid? selectedComponent, bool paused, CancellationToken token);
}
