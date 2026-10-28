using System.IO;
using System.Text;
using System.Text.Json;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.App.Shell;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Imports;

namespace EnvironmentBuilder.UiTests;

internal sealed class AuthoringUiFixture : IEnvironmentStore, IComponentLibrary, IComponentLibrarySettings, IAuthoringInteractions, IInitializationDocumentReader, ILocalPathMappings, IInputResolver, ISeedPackStore, IPreviewInputCapture
{
    private readonly Dictionary<Guid, (string Version, bool Enabled)> selections = [];
    public List<EnvironmentListItem> Items { get; } = [];
    public ComponentDefinition[] Definitions { get; private set; }
    public AuthoringUiFixture()
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "AuthoringFixtures/components.json")));
        Definitions = json.RootElement.EnumerateArray().Select(c => new StrictDocumentReader().Read<ComponentDefinition>(Encoding.UTF8.GetBytes(c.GetRawText()))).ToArray();
    }
    public Task SaveNewAsync(IsolatedEnvironment environment, CancellationToken cancellationToken)
    { Items.Add(new EnvironmentListItem(environment.Id, environment.Name, 1, environment, null, false)); return Task.CompletedTask; }
    public Task<IReadOnlyList<EnvironmentListItem>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EnvironmentListItem>>(Items.ToArray());
    public Task<IReadOnlyList<ComponentDefinition>> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ComponentDefinition>>(Definitions);
    public Task<IReadOnlyList<ComponentLibraryEntry>> ReadEntriesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ComponentLibraryEntry>>(
        Definitions.GroupBy(d => d.Id).Select(group => selections.TryGetValue(group.Key, out var choice)
            ? new ComponentLibraryEntry(group.Single(d => d.Version == choice.Version), choice.Enabled)
            : new ComponentLibraryEntry(group.First(), true)).ToArray());
    public Task SaveAsync(ComponentDefinition definition, bool enabled, CancellationToken cancellationToken)
    {
        EnvironmentValidator.ValidateDefinition(definition);
        Definitions = [.. Definitions.Where(d => d.Id != definition.Id || d.Version != definition.Version), definition];
        selections[definition.Id] = (definition.Version, enabled);
        return Task.CompletedTask;
    }
    public Task<ComponentDefinition> ImportAsync(ReadOnlyMemory<byte> document, CancellationToken cancellationToken) => Task.FromResult(new StrictDocumentReader().Read<ComponentDefinition>(document));
    public Task<byte[]?> SelectComponentDocumentAsync(CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
    public Task<string?> SelectFolderAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task<string?> SelectInitializerDocumentAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task<DirtyDraftDecision> DecideDirtyDraftAsync(CancellationToken cancellationToken) => Task.FromResult(DirtyDraftDecision.Stay);
    public Task CopyPreviewAsync(string sanitizedPreview, CancellationToken cancellationToken) => Task.CompletedTask;
    public InitializationDefinition ReadInitialization(string json) => new StrictDocumentReader().ReadInitialization(json);
    Task<IReadOnlyList<LocalPathMapping>> ILocalPathMappings.ReadAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LocalPathMapping>>([]);
    public Task MapAsync(string reference, string absolutePath, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<IReadOnlyList<ReadinessIssue>> CheckAsync(IReadOnlyList<InputReference> references, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ReadinessIssue>>([new("missing-path", "/source", "Offline")]);
    public Task<VerifiedSeedPack?> FindAsync(SeedPackReference reference, CancellationToken cancellationToken) => Task.FromResult<VerifiedSeedPack?>(null);
    public Task<PreviewSnapshot> CaptureAsync(IsolatedEnvironment environment, CancellationToken cancellationToken) => Task.FromResult(new PreviewSnapshot(environment.Id, 1, CanonicalJson.Hash(environment), "", "v1", CanonicalJson.Hash(environment.Baseline), [], [], [], [new("fixture-image", "unknown", null, null)]));
    public AuthoringSession Session(NavigationService navigation) => new(new EnvironmentListViewModel(this), new CreateEnvironmentService(this, this, this), this, this, this, this, this, this, this, navigation);
}
