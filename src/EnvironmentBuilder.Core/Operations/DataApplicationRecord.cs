using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.Operations;

public sealed record DataApplicationKey(Guid EnvironmentId, Guid ComponentId, Guid DataGenerationId, string BaselineHash,
    string ApplicationHash, string InitializerHash, string StepId)
{
    public string StableIdempotencyKey => CanonicalJson.Hash(new { EnvironmentId, ComponentId, DataGenerationId, BaselineHash, ApplicationHash, InitializerHash, StepId });
}
public sealed record DataApplicationRecord(DataApplicationKey Key, StepState State, int Attempt, DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt, int? AcceptedStatus, bool? AssertionsPassed, string? ErrorCode);
