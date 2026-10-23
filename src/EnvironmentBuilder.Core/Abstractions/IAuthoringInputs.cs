using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.Core.Abstractions;

public interface IPreviewInputCapture
{
    Task<PreviewSnapshot> CaptureAsync(IsolatedEnvironment environment, CancellationToken cancellationToken);
}
public interface IComponentLibrary
{
    Task<IReadOnlyList<ComponentDefinition>> ReadAsync(CancellationToken cancellationToken);
    Task<ComponentDefinition> ImportAsync(ReadOnlyMemory<byte> document, CancellationToken cancellationToken);
}
public interface ILocalPathMappings
{
    Task<IReadOnlyList<LocalPathMapping>> ReadAsync(CancellationToken cancellationToken);
    Task MapAsync(string reference, string absolutePath, CancellationToken cancellationToken);
}
public interface IInitializationDocumentReader
{
    EnvironmentBuilder.Core.SeedPacks.InitializationDefinition ReadInitialization(string json);
}
