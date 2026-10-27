using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.Infrastructure.Docker;

public sealed class DockerInspectionStreams(DockerProcessFactory processes, LogMemoryBudget budget) : IInspectionStreamsFactory
{
    public IInspectionStreams Create(IsolatedEnvironment environment) => new Streams(processes.Create(environment), budget);
    private sealed class Streams(IDockerProcess docker, LogMemoryBudget budget) : IInspectionStreams
    {
        private readonly MetricSampler metrics = new(docker);
        private LogObservationSession? logs;
        private string? selected;
        public async Task<InspectionStreamsSnapshot> ReadAsync(EnvironmentObservation observation, Guid? selectedComponent, bool paused, CancellationToken token)
        {
            ResourceObservation? resource = observation.Resources.FirstOrDefault(r => r.Ledger.ComponentId == selectedComponent && r.Ledger.Kind == ResourceKind.Container && r.Ownership.Verified);
            string? id = observation.Freshness == "current" && !paused ? resource?.Actual?.Identity : null;
            if (selected != id)
            {
                if (logs is not null) { await logs.DisposeAsync().ConfigureAwait(false); logs = null; }
                selected = id;
                if (id is not null)
                {
                    logs = new LogObservationSession(docker, id, budget);
                    logs.Start(resource!.Ledger, resource.Actual!);
                }
            }
            var samples = new Dictionary<Guid, IReadOnlyDictionary<string, Measurement>>();
            if (observation.Freshness == "current")
            {
                ComponentObservation[] running = observation.Components.Where(c => c.ContainerId is not null && c.Status == "running" && c.Drift == "expected").ToArray();
                var batch = await metrics.SampleManyAsync(running.Select(c => c.ContainerId!).ToArray(), DateTimeOffset.UtcNow, token).ConfigureAwait(false);
                foreach (ComponentObservation component in running)
                {
                    samples[component.ComponentId] = batch[component.ContainerId!].Measurements;
                }
            }
            LogSnapshot snapshot = logs?.Snapshot() ?? new([], 0, 0, paused ? "paused" : "unavailable");
            return new(snapshot.Lines, snapshot.Dropped, snapshot.Truncated, snapshot.State, samples);
        }
        public async ValueTask DisposeAsync() { if (logs is not null) { await logs.DisposeAsync().ConfigureAwait(false); } metrics.Dispose(); }
    }
}
