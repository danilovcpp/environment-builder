using System.Collections.Immutable;

namespace EnvironmentBuilder.Core.Operations;

public enum OperationState { Planned, AwaitingConfirmation, Running, CancelRequested, Reconciling, NeedsAttention, Succeeded, Failed, PartiallyCompleted, Cancelled }
public enum StepState { Pending, Started, Succeeded, Failed, Unknown }
public sealed record Operation(Guid Id, Guid EnvironmentId, string Kind, OperationState State, DateTimeOffset CreatedAt,
    long OriginalRevision, string ConfigurationHash, OperationPlan Plan)
{
    public DateTimeOffset? CompletedAt { get; init; }
    public bool CancellationRequested { get; init; }
    public bool CloseRequested { get; init; }
    public ImmutableArray<string> Remnants { get; init; } = [];
    public ImmutableArray<StageProgress> Progress { get; init; } = [];
    public string? ResultCode { get; init; }
}
public sealed record StageProgress(Guid? ComponentId, string Stage, long Completed, long? Total);
public sealed record OperationStep(Guid OperationId, string StepId, string Action, string Target)
{
    public int Ordinal { get; init; }
    public StepState State { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? ActualIdentity { get; init; }
    public string? ErrorCode { get; init; }
}
// This finite result has no fields for bodies, raw output, or secret values.
public sealed record EffectObservation(string? ActualIdentity, bool Verified, string? ErrorCode = null);
