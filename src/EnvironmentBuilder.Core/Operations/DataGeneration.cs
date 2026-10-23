namespace EnvironmentBuilder.Core.Operations;

public enum GenerationValidity { Active, Invalidated, Retired }
public sealed record DataGeneration(Guid Id, Guid EnvironmentId, string BaselineHash, DateTimeOffset AllocatedAt,
    GenerationValidity Validity, DateTimeOffset? InvalidatedAt = null, string? InvalidationReason = null, Guid? TransitionOperationId = null);
