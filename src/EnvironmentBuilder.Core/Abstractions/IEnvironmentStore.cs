using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.Abstractions;

// Application-owned, serial persistence worker owns the connection.
public interface IEnvironmentStore
{
    Task SaveNewAsync(IsolatedEnvironment environment, CancellationToken cancellationToken);
    Task<IReadOnlyList<EnvironmentListItem>> ListAsync(CancellationToken cancellationToken);
}
public sealed record EnvironmentListItem(Guid Id, string Name, long Revision, IsolatedEnvironment? Document, string? ErrorCode, bool HasUnresolvedOperation)
{
    public DateTimeOffset? SavedAt { get; init; }
}
