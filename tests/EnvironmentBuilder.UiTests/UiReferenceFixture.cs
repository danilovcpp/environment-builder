using System.IO;
using System.Text.Json;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.App.Features.SeedPacks;
using EnvironmentBuilder.App.Features.Settings;
using EnvironmentBuilder.App.Shell;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Imports;

namespace EnvironmentBuilder.UiTests;

internal sealed class UiReferenceFixture
{
    public AuthoringUiFixture Store { get; } = new();
    public NavigationService Navigation { get; } = new();
    public ThemeService Themes { get; } = new();
    public ShellViewModel Shell { get; }
    public UiReferenceFixture()
    {
        var draft = EnvironmentDraft.Create();
        draft.AddComponent(Store.Definitions.First(d => d.ComponentType == "target"), Store.Definitions);
        using JsonDocument states = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "UiReferenceFixtures/states.json")));
        for (int i = 0; i < 12; i++)
        {
            var document = draft.Snapshot with
            {
                Id = Guid.NewGuid(),
                Name = i == 0 ? states.RootElement.GetProperty("longName").GetString()! : $"Рабочая среда {i:00}",
                Description = "Изолированный визуальный fixture · Docker не проверен"
            };
            Store.Items.Add(new EnvironmentListItem(document.Id, document.Name, 1, document, null, i == 1));
        }
        Store.Items[2] = Store.Items[2] with { Document = null, ErrorCode = "invalid-json" };
        var seed = new StrictDocumentReader().Read<SeedPack>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Contracts/examples/seedpack/manifest.json")));
        SeedPack[] packs = Enumerable.Range(0, 8).Select(i =>
        {
            var pack = seed with { Id = Guid.NewGuid(), Title = i == 0 ? "Generic Office Starter · визуальный fixture" : $"SeedPack fixture {i:00}", Theme = i % 2 == 0 ? "Office" : "Generic" };
            return pack with { ContentHash = CanonicalJson.ManifestHash(JsonSerializer.SerializeToElement(pack, CanonicalJson.Options)) };
        }).ToArray();
        Shell = new ShellViewModel(Navigation, Themes)
        {
            Authoring = Store.Session(Navigation),
            IsLoading = false,
            SeedPacks = new SeedPackLibraryViewModel(_ => Task.FromResult<IReadOnlyList<SeedPack>>(packs)),
            Settings = new SettingsViewModel(Store, Store),
            Status = "Хранилище доступно · Docker не проверен",
            DataDirectory = "Изолированное тестовое хранилище"
        };
        Assert.True(Shell.SeedPacks.LoadAsync(CancellationToken.None).IsCompletedSuccessfully);
        Assert.True(Shell.Settings.LoadAsync(CancellationToken.None).IsCompletedSuccessfully);
        Shell.Settings.DataDirectory = Shell.DataDirectory;
        Shell.SeedPacks.SelectedPack = Shell.SeedPacks.Items[0];
        Shell.Settings.SelectedDefinition = Shell.Settings.Definitions[0];
        Shell.Diagnostics.StorageStatus = Shell.Status;
        Shell.Diagnostics.RecoveryStatus = "Незавершённых операций: 1. Требуется явная сверка; автоматический повтор выключен.";
    }
}
