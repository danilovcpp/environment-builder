using EnvironmentBuilder.Core.Abstractions;

namespace EnvironmentBuilder.Core.Environments;

public sealed record CreateEnvironmentResult(IsolatedEnvironment Environment, IReadOnlyList<ReadinessIssue> ReadinessIssues);
public sealed class CreateEnvironmentService(IEnvironmentStore store, IInputResolver inputs, ISeedPackStore packs)
{
    public async Task<CreateEnvironmentResult> CreateAsync(EnvironmentDraft draft, CancellationToken cancellationToken)
    {
        if (draft.IsPersisted && draft.IsDirty) { throw new InvalidOperationException("Existing configuration changes require confirmed cleanup."); }
        IsolatedEnvironment snapshot = draft.Snapshot;
        EnvironmentValidator.Validate(snapshot);
        IReadOnlyList<ReadinessIssue> readiness = await EnvironmentReadiness.CheckAsync(snapshot, inputs, packs, cancellationToken).ConfigureAwait(false);
        await store.SaveNewAsync(snapshot, cancellationToken).ConfigureAwait(false);
        // A snapshot changed while awaiting I/O must remain dirty.
        if (CanonicalJson.Hash(snapshot) == CanonicalJson.Hash(draft.Snapshot)) { draft.MarkSaved(); }
        return new CreateEnvironmentResult(snapshot, readiness);
    }
}
