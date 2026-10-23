using System.Collections.Immutable;
using System.Text.Json;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.UnitTests.Environments;

namespace EnvironmentBuilder.UnitTests.ViewModels;

internal sealed class AuthoringFakes : IEnvironmentStore, IComponentLibrary, IAuthoringInteractions, IInitializationDocumentReader, ILocalPathMappings, IInputResolver, ISeedPackStore, IPreviewInputCapture
{
    internal List<EnvironmentListItem> Items { get; } = [];
    internal Queue<DirtyDraftDecision> Decisions { get; } = new();
    internal bool FailSave { get; set; }
    internal TaskCompletionSource<IReadOnlyList<EnvironmentListItem>>? PendingLoad { get; set; }
    internal string? Clipboard { get; private set; }
    internal int Captures { get; private set; }
    public Task SaveNewAsync(IsolatedEnvironment environment, CancellationToken cancellationToken)
    {
        if (FailSave) { throw new System.IO.IOException("disk fault"); }
        if (Items.All(i => i.Id != environment.Id)) { Items.Add(new EnvironmentListItem(environment.Id, environment.Name, 1, environment, null, false)); }
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<EnvironmentListItem>> ListAsync(CancellationToken cancellationToken) => PendingLoad?.Task ?? Task.FromResult<IReadOnlyList<EnvironmentListItem>>(Items.ToArray());
    public Task<IReadOnlyList<ComponentDefinition>> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ComponentDefinition>>([AuthoringContractTests.Definition()]);
    public Task<ComponentDefinition> ImportAsync(ReadOnlyMemory<byte> document, CancellationToken cancellationToken) => Task.FromResult(JsonSerializer.Deserialize<ComponentDefinition>(document.Span, CanonicalJson.Options)!);
    public Task<byte[]?> SelectComponentDocumentAsync(CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
    public Task<string?> SelectFolderAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task<string?> SelectInitializerDocumentAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task<DirtyDraftDecision> DecideDirtyDraftAsync(CancellationToken cancellationToken) => Task.FromResult(Decisions.Dequeue());
    public Task CopyPreviewAsync(string sanitizedPreview, CancellationToken cancellationToken) { Clipboard = sanitizedPreview; return Task.CompletedTask; }
    public InitializationDefinition ReadInitialization(string json) => JsonSerializer.Deserialize<InitializationDefinition>(json, CanonicalJson.Options)!;
    Task<IReadOnlyList<LocalPathMapping>> ILocalPathMappings.ReadAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LocalPathMapping>>([]);
    public Task MapAsync(string reference, string absolutePath, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<IReadOnlyList<ReadinessIssue>> CheckAsync(IReadOnlyList<InputReference> references, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ReadinessIssue>>([new("image-identity-unknown", "/source", "Offline")]);
    public Task<VerifiedSeedPack?> FindAsync(SeedPackReference reference, CancellationToken cancellationToken) => Task.FromResult<VerifiedSeedPack?>(null);
    public Task<PreviewSnapshot> CaptureAsync(IsolatedEnvironment environment, CancellationToken cancellationToken)
    {
        Captures++;
        return Task.FromResult(new PreviewSnapshot(environment.Id, environment.Revision, CanonicalJson.Hash(environment), JsonSerializer.Serialize(environment, CanonicalJson.Options), "v1", CanonicalJson.Hash(environment.Baseline), [], [], [], []));
    }
    internal EnvironmentEditorViewModel Editor(EnvironmentDraft? draft = null) => new(draft ?? EnvironmentDraft.Create(), new CreateEnvironmentService(this, this, this), this, this, this, this, this, this);
}
