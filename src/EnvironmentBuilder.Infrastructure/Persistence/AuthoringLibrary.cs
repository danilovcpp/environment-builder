using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Infrastructure.Imports;

namespace EnvironmentBuilder.Infrastructure.Persistence;

public sealed class AuthoringLibrary(ComponentLibraryStore store, StrictDocumentReader reader) : IComponentLibrary
{
    public async Task<IReadOnlyList<ComponentDefinition>> ReadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ComponentLibraryItem> items = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        if (items.Any(i => i.ErrorCode is not null)) { throw new DocumentException("/library", "unreadable-library-entry"); }
        return items.Select(i => i.Document!).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id).ThenBy(i => i.Version, StringComparer.Ordinal).ToArray();
    }
    public async Task<ComponentDefinition> ImportAsync(ReadOnlyMemory<byte> document, CancellationToken cancellationToken)
    {
        ComponentDefinition definition = reader.Read<ComponentDefinition>(document, "component");
        await store.SaveAsync(definition, cancellationToken).ConfigureAwait(false);
        return definition;
    }
}
