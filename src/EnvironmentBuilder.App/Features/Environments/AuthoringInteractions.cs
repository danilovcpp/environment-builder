using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.App.Features.Environments;

// WPF/file-dialog/clipboard boundaries; headless ViewModels use deterministic substitutes.
public interface IAuthoringInteractions
{
    Task<byte[]?> SelectComponentDocumentAsync(CancellationToken cancellationToken);
    Task<string?> SelectFolderAsync(CancellationToken cancellationToken);
    Task<string?> SelectInitializerDocumentAsync(CancellationToken cancellationToken);
    Task<DirtyDraftDecision> DecideDirtyDraftAsync(CancellationToken cancellationToken);
    Task CopyPreviewAsync(string sanitizedPreview, CancellationToken cancellationToken);
}
