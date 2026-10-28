using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Infrastructure.Imports;

namespace EnvironmentBuilder.Infrastructure.Persistence;

public sealed class AuthoringLibrary(ComponentLibraryStore store, StrictDocumentReader reader) : IComponentLibrary, IComponentLibrarySettings
{
    public async Task<IReadOnlyList<ComponentDefinition>> ReadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ComponentLibraryItem> items = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        if (items.Any(i => i.ErrorCode is not null)) { throw new DocumentException("/library", "unreadable-library-entry"); }
        return items.Select(i => i.Document!).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Version, StringComparer.Ordinal).ToArray();
    }
    public async Task<IReadOnlyList<ComponentLibraryEntry>> ReadEntriesAsync(CancellationToken cancellationToken)
    {
        var definitions = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var selections = await store.ReadSelectionsAsync(cancellationToken).ConfigureAwait(false);
        return definitions.GroupBy(d => d.Id).Select(group =>
        {
            if (selections.TryGetValue(group.Key, out var selection))
            { return new ComponentLibraryEntry(group.Single(d => d.Version == selection.Version), selection.Enabled); }
            var latest = group.OrderByDescending(d => Version.TryParse(d.Version.Split('-')[0], out var version) ? version : new Version(0, 0)).ThenByDescending(d => d.Version, StringComparer.Ordinal).First();
            return new ComponentLibraryEntry(latest, true);
        }).OrderBy(e => e.Definition.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public Task SaveAsync(ComponentDefinition definition, bool enabled, CancellationToken cancellationToken) => store.SaveAsync(definition, enabled, cancellationToken);
    public async Task<ComponentDefinition> ImportAsync(ReadOnlyMemory<byte> document, CancellationToken cancellationToken)
    {
        ComponentDefinition definition = reader.Read<ComponentDefinition>(document, "component");
        await store.SaveAsync(definition, true, cancellationToken).ConfigureAwait(false);
        return definition;
    }
}
