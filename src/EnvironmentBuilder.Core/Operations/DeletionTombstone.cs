using System.Collections.Immutable;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.Operations;

public sealed record DeletionTombstone(IsolatedEnvironment Snapshot, Operation Operation)
{
    public Guid EnvironmentId => Snapshot.Id;
    public OperationPlan Plan => Operation.Plan;
    public ImmutableArray<string> DeletedResources => Operation.DeletedResources;
    public ImmutableArray<string> Remnants => Operation.Remnants.Union(
        Plan.Resources.Select(r => r.ActualDockerId!).Except(DeletedResources, StringComparer.Ordinal), StringComparer.Ordinal).ToImmutableArray();
    public string RetryReason => Operation.ResultCode ?? "Удаление не завершено. Нужны свежая сверка и подтверждение нового плана.";
}

public sealed record DeleteEnvironmentResult(bool Deleted, Operation? Operation, string? Code);

// The store owns the atomic transition from a live snapshot to a closed, sanitized history.
public interface IDeletePersistence
{
    Task BeginAsync(OperationPlan plan, Guid operationId, CancellationToken token);
    Task FinalizeAsync(Operation operation, CancellationToken token);
}
