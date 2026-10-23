using System.Text;
using System.Text.Json;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Infrastructure.Persistence;

namespace EnvironmentBuilder.IntegrationTests.Support;

public static class FixtureStoreBuilder
{
    public static async Task SeedAsync(FoundationStore fixture, int count, CancellationToken cancellationToken)
    {
        using JsonDocument components = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "AuthoringFixtures/components.json"), cancellationToken));
        var library = new ComponentLibraryStore(fixture.Worker, fixture.Reader);
        foreach (JsonElement component in components.RootElement.EnumerateArray())
        { await library.SaveAsync(fixture.Reader.Read<ComponentDefinition>(Encoding.UTF8.GetBytes(component.GetRawText())), cancellationToken); }
        IsolatedEnvironment template = fixture.Reader.Read<IsolatedEnvironment>(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "AuthoringFixtures/environment.json"), cancellationToken));
        for (int i = 0; i < count; i++)
        {
            var draft = EnvironmentDraft.Create();
            draft.Update(draft.Snapshot with { Name = $"Fixture {i:D4}", Description = i % 2 == 0 ? "Поиск по описанию: чётное" : "Нечётное окружение", Components = template.Components, Dependencies = template.Dependencies, Baseline = template.Baseline });
            await fixture.Environments.SaveNewAsync(draft.Snapshot, cancellationToken);
        }
    }
}
