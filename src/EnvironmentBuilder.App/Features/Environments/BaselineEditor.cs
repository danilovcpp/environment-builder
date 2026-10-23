using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.App.Features.Environments;

public sealed partial class BaselineEditor : EditorRow
{
    public BaselineEditor(BaselineApplication application, bool mandatory, Func<string, InitializationDefinition> read)
    {
        this.read = read; ComponentId = application.ComponentId; IsMandatory = mandatory;
        InitializerJson = JsonSerializer.Serialize(application.Initializer, new JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = true });
        PackId = application.SeedPack?.Id.ToString() ?? ""; PackVersion = application.SeedPack?.Version ?? "1.0.0"; PackHash = application.SeedPack?.ContentHash ?? "";
        UsePack = application.SeedPack is not null;
    }
    private readonly Func<string, InitializationDefinition> read;
    public bool IsMandatory { get; }
    public bool CanEdit => !IsMandatory;
    public string KindLabel => IsMandatory ? "Обязательная инициализация" : "Назначение baseline";
    [ObservableProperty] private Guid componentId;
    [ObservableProperty] private string initializerJson = "";
    [ObservableProperty] private bool usePack;
    [ObservableProperty] private string packId = "";
    [ObservableProperty] private string packVersion = "1.0.0";
    [ObservableProperty] private string packHash = "";
    public BaselineApplication Build()
    {
        if (UsePack && !Guid.TryParseExact(PackId, "D", out _)) { throw new DocumentException("/baseline/seedPack/id", "invalid-uuid"); }
        return new BaselineApplication(ComponentId, read(InitializerJson), UsePack ? new SeedPackReference(Guid.Parse(PackId), PackVersion, PackHash) : null);
    }
}
