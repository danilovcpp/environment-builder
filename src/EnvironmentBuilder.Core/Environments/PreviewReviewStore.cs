namespace EnvironmentBuilder.Core.Environments;

// App-owned snapshots of previews actually presented to the user; Start must not silently replace one.
public sealed class PreviewReviewStore
{
    private PreviewSnapshot? current;
    public void Remember(PreviewSnapshot snapshot) => current = snapshot;
    public PreviewSnapshot? Find(Guid environmentId) => current?.EnvironmentId == environmentId ? current : null;
}
