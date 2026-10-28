using System.Collections.Immutable;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.App.Features.Environments;

public sealed partial class ComponentEditorViewModel : EditorRow
{
    private readonly ILocalPathMappings? mappings;
    private readonly IAuthoringInteractions? interactions;
    private string mappedFolder = "";
    public ComponentEditorViewModel(EnvironmentComponent component, bool isReadOnly = false, ILocalPathMappings? mappings = null, IAuthoringInteractions? interactions = null)
    {
        this.mappings = mappings; this.interactions = interactions;
        IsReadOnly = isReadOnly;
        Id = component.Id;
        Definition = component.DefinitionSnapshot;
        SourceKind = component.Source is BuildSource ? "build" : "image";
        ImageReference = component.Source is ImageSource image ? image.Reference : "";
        if (component.Source is BuildSource build)
        { PathRef = build.PathRef; ContextSubdirectory = build.ContextSubdirectory; Dockerfile = build.Dockerfile; BuildArguments = JsonSerializer.Serialize(build.BuildArguments); BuildSecrets = JsonSerializer.Serialize(build.BuildSecrets); }
        foreach (ParameterDefinition parameter in Definition.ParameterDefinitions)
        {
            component.Parameters.TryGetValue(parameter.Name, out Value? value);
            Parameters.Add(new ValueEditor
            {
                Key = parameter.Name,
                Text = value?.SecretRef ?? value?.Literal ?? "",
                UseSecret = value?.SecretRef is not null || parameter.Secret,
                TypeHint = parameter.Type,
                Required = parameter.Required,
                DeclaredSecret = parameter.Secret,
                IsDeclared = true,
                Included = value is not null
            });
        }
        foreach ((string key, Value value) in component.Parameters.Where(p => !Definition.ParameterDefinitions.Any(d => d.Name == p.Key))) { Parameters.Add(ValueEditor.From(key, value)); }
        foreach ((string key, Value value) in component.Environment.OrderBy(p => p.Key, StringComparer.Ordinal)) { Variables.Add(ValueEditor.From(key, value)); }
        foreach (Port port in component.Ports) { Ports.Add(PortEditor.From(port)); }
        foreach (Storage storage in component.Storage) { Storage.Add(StorageEditor.From(storage)); }
        Parameters.Changed += ChildChanged; Variables.Changed += ChildChanged; Ports.Changed += ChildChanged; Storage.Changed += ChildChanged;
    }
    public Guid Id { get; }
    public bool IsReadOnly { get; }
    public bool CanEdit => !IsReadOnly;
    public bool IsLibraryDefault { get; init; }
    public bool CanRemove => !IsLibraryDefault;
    public System.Windows.Input.ICommand? RemoveCommand { get; init; }
    public bool HasPendingFolder => IsBuild && LocalFolder != mappedFolder;
    public bool CanChooseFolder => CanEdit && mappings is not null && interactions is not null;
    public bool IsImage => !IsBuild;
    [ObservableProperty] private bool isExpanded;
    [ObservableProperty] private string localFolder = "";
    [ObservableProperty] private string folderError = "";
    public ComponentDefinition Definition { get; }
    public string Label => IsLibraryDefault ? DisplayName ?? Definition.Name : Definition.Name + " · " + Id.ToString("N")[..8];
    public string NetworkHost => "c-" + Id.ToString("N");
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Label))] private string? displayName;
    public string DefinitionDetails => (Definition.ComponentType == "target" ? "Целевой сервис" : "Инфраструктура") + " · " + (IsBuild ? "Сборка из папки" : "Готовый образ");
    public bool HasParameters => Parameters.Count != 0;
    public string[] SourceKinds => Definition.ComponentType == "infrastructure" ? ["image"] : ["image", "build"];
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsBuild), nameof(IsImage), nameof(DefinitionDetails))] private string sourceKind = "image";
    public bool IsBuild => SourceKind == "build";
    [ObservableProperty] private string imageReference = "";
    [ObservableProperty] private string pathRef = "local-path://source";
    [ObservableProperty] private string contextSubdirectory = ".";
    [ObservableProperty] private string dockerfile = "Dockerfile";
    [ObservableProperty] private string buildArguments = "{}";
    [ObservableProperty] private string buildSecrets = "{}";
    public EditorRows<ValueEditor> Parameters { get; } = [];
    public EditorRows<ValueEditor> Variables { get; } = [];
    public EditorRows<PortEditor> Ports { get; } = [];
    public EditorRows<StorageEditor> Storage { get; } = [];
    public void LoadLocalFolder(IReadOnlyList<LocalPathMapping> paths)
    {
        mappedFolder = paths.FirstOrDefault(p => "local-path://" + p.Alias == PathRef)?.AbsolutePath ?? "";
        LocalFolder = mappedFolder;
    }
    public async Task SaveLocalFolderAsync(CancellationToken token)
    {
        if (!IsBuild || mappings is null || LocalFolder == mappedFolder) { return; }
        string reference = "local-path://source-" + Guid.NewGuid().ToString("N");
        await mappings.MapAsync(reference, LocalFolder, token);
        PathRef = reference; mappedFolder = LocalFolder;
    }
    [RelayCommand]
    private async Task ChooseSourceFolderAsync(CancellationToken token)
    {
        if (!CanChooseFolder) { return; }
        FolderError = "";
        try
        {
            string? folder = await interactions!.SelectFolderAsync(token);
            if (folder is not null) { LocalFolder = folder; }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException) { FolderError = "Не удалось выбрать папку исходников."; }
    }
    public EnvironmentComponent Build()
    {
        ComponentSource source = SourceKind == "image" ? new ImageSource(ImageReference) : new BuildSource(PathRef, Dockerfile, ContextSubdirectory,
            EditorJson.Strings(BuildArguments, "/source/buildArguments").ToImmutableDictionary(), EditorJson.Strings(BuildSecrets, "/source/buildSecrets").ToImmutableDictionary());
        return new EnvironmentComponent(Id, Definition, source, Map(Parameters, "/parameters"), Map(Variables, "/environment"), Ports.Select(p => p.Build()).ToImmutableArray(), Storage.Select(s => s.Build()).ToImmutableArray());
    }
    private static ImmutableDictionary<string, Value> Map(IEnumerable<ValueEditor> rows, string path)
    {
        var result = ImmutableDictionary.CreateBuilder<string, Value>(StringComparer.Ordinal);
        foreach (ValueEditor row in rows.Where(r => r.Included))
        { if (!result.TryAdd(row.Key, row.Build())) { throw new DocumentException(path, "duplicate-key"); } }
        return result.ToImmutable();
    }
    [RelayCommand] private void AddVariable() => Variables.Add(new ValueEditor { Key = "VARIABLE" + (Variables.Count + 1) });
    [RelayCommand] private void RemoveVariable(ValueEditor row) => Variables.Remove(row);
    [RelayCommand] private void AddPort() => Ports.Add(new PortEditor { Id = "port" + (Ports.Count + 1) });
    [RelayCommand] private void RemovePort(PortEditor row) => Ports.Remove(row);
    [RelayCommand] private void AddStorage() => Storage.Add(new StorageEditor { Id = "data" + (Storage.Count + 1), TargetPath = "/data" + (Storage.Count + 1) });
    [RelayCommand] private void RemoveStorage(StorageEditor row) => Storage.Remove(row);
}
