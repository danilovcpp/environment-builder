using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.App.Shell;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.App.Features.Settings;

public sealed partial class DefinitionRow : ObservableObject
{
    public DefinitionRow(ComponentDefinition definition, bool enabled = true, ILocalPathMappings? mappings = null, IAuthoringInteractions? interactions = null)
    {
        Definition = definition; name = definition.Name; this.enabled = enabled;
        Editor = new ComponentEditorViewModel(new EnvironmentComponent(Guid.NewGuid(), definition, definition.DefaultSource,
            definition.ParameterDefinitions.Where(p => p.DefaultValue is not null).ToImmutableDictionary(p => p.Name, p => p.DefaultValue!),
            definition.DefaultEnvironment, definition.DefaultPorts, definition.DefaultStorage), mappings: mappings, interactions: interactions)
        { IsLibraryDefault = true, IsExpanded = true };
    }
    public ComponentDefinition Definition { get; private set; }
    public ComponentEditorViewModel Editor { get; }
    [ObservableProperty] private string name;
    partial void OnNameChanged(string value) => Editor.DisplayName = value;
    [ObservableProperty] private bool enabled;
    public string Type => Definition.ComponentType == "target" ? "Целевой сервис" : "Инфраструктура";
    public string Version => Definition.Version;
    public string Source => Editor.IsBuild ? Editor.LocalFolder : Editor.ImageReference;
    public string Details => $"{Name}\n{Type}\n\n{Source}\n\nID: {Definition.Id}\nВерсия: {Version}";
    public bool IsNew { get; set; }
    public ComponentDefinition Build()
    {
        EnvironmentComponent values = Editor.Build();
        return Definition with
        {
            Name = Name.Trim(),
            DefaultSource = values.Source,
            DefaultEnvironment = values.Environment,
            DefaultPorts = values.Ports,
            DefaultStorage = values.Storage,
            ParameterDefinitions = Definition.ParameterDefinitions.Select(p => p with { DefaultValue = values.Parameters.GetValueOrDefault(p.Name) }).ToImmutableArray()
        };
    }
    public void Accept(ComponentDefinition saved) { Definition = saved; IsNew = false; OnPropertyChanged(nameof(Version)); OnPropertyChanged(nameof(Source)); }
}

public sealed partial class SettingsViewModel(ThemeService themes, IComponentLibrary? library = null, ILocalPathMappings? mappings = null, IAuthoringInteractions? interactions = null) : ObservableObject
{
    private readonly List<DefinitionRow> all = [];
    public ObservableCollection<DefinitionRow> Definitions { get; } = [];
    public ObservableCollection<LocalPathMapping> Paths { get; } = [];
    [ObservableProperty] private DefinitionRow? selectedDefinition;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string dataDirectory = "Хранилище ещё не открыто";
    [ObservableProperty] private string status = "Добавьте сервисы, которые нужны при создании окружения.";
    [ObservableProperty] private string error = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(ResetCommand), nameof(AddTargetCommand), nameof(AddInfrastructureCommand))] private bool isBusy;
    public bool CanConfigure => !IsBusy && library is IComponentLibrarySettings;
    public string ServiceCount => $"Сервисов: {all.Count} · доступны при создании: {all.Count(d => d.Enabled)}";
    public string Summary => SelectedDefinition?.Details ?? "Добавьте целевой сервис или инфраструктуру и сохраните настройки.";
    public string WorkflowReason => library is IComponentLibrarySettings
        ? "Сохранённые и включённые сервисы доступны при создании окружения. Параметры конкретной среды задаются в её редакторе."
        : "Изменения недоступны: библиотека открыта только для просмотра.";
    partial void OnSelectedDefinitionChanged(DefinitionRow? value) => OnPropertyChanged(nameof(Summary));
    partial void OnSearchChanged(string value) => Filter();
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanConfigure));
    [RelayCommand] private void ToggleTheme() => themes.Apply(!themes.IsDark);
    [RelayCommand(CanExecute = nameof(CanConfigure))] private void AddTarget() => Add("target");
    [RelayCommand(CanExecute = nameof(CanConfigure))] private void AddInfrastructure() => Add("infrastructure");
    private void Add(string type)
    {
        Guid id = Guid.NewGuid();
        var definition = new ComponentDefinition("component", 1, id, "1.0.0", type == "target" ? "Новый сервис" : "Новая инфраструктура",
            type, new ComponentContract("service-" + id.ToString("N"), "1.0.0"), new ImageSource(""), [], ImmutableDictionary<string, Value>.Empty,
            [], [], [], new Readiness("running", 60), [], null);
        var row = new DefinitionRow(definition, mappings: mappings, interactions: interactions) { IsNew = true };
        Watch(row); all.Add(row); Search = ""; Filter(); SelectedDefinition = row;
        Status = "Задайте имя, источник и параметры, затем сохраните настройки.";
    }
    private void Watch(DefinitionRow row)
    {
        row.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(ServiceCount)); OnPropertyChanged(nameof(Summary)); };
    }
    private void Filter()
    {
        var selected = SelectedDefinition;
        Definitions.Clear();
        foreach (var row in all.Where(d => d.Name.Contains(Search, StringComparison.OrdinalIgnoreCase))) { Definitions.Add(row); }
        SelectedDefinition = Definitions.FirstOrDefault(d => d == selected) ?? Definitions.FirstOrDefault();
        OnPropertyChanged(nameof(ServiceCount));
    }
    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private async Task SaveAsync(CancellationToken token)
    {
        if (library is not IComponentLibrarySettings writable) { return; }
        IsBusy = true; Error = "";
        DefinitionRow? saving = null;
        try
        {
            var candidates = new List<(DefinitionRow Row, ComponentDefinition Definition)>();
            foreach (var row in all)
            {
                saving = row;
                await row.Editor.SaveLocalFolderAsync(token);
                var definition = row.Build();
                if (definition.DefaultSource is BuildSource && string.IsNullOrWhiteSpace(row.Editor.LocalFolder) && (row.IsNew || row.Definition.DefaultSource is not BuildSource))
                { throw new DocumentException("/source/folder", "required-folder"); }
                if (!row.IsNew && CanonicalJson.Hash(definition) != CanonicalJson.Hash(row.Definition))
                { definition = definition with { Version = "1.0.0-local." + Guid.NewGuid().ToString("N") }; }
                EnvironmentValidator.ValidateDefinition(definition);
                candidates.Add((row, definition));
            }
            foreach (var candidate in candidates)
            {
                saving = candidate.Row;
                await writable.SaveAsync(candidate.Definition, candidate.Row.Enabled, token);
                candidate.Row.Accept(candidate.Definition);
            }
            Status = "Настройки сохранены. Сервисы доступны в новом окружении.";
            OnPropertyChanged(nameof(ServiceCount));
        }
        catch (OperationCanceledException) { Status = "Сохранение отменено. Изменения остаются в настройках."; }
        catch (DocumentException failure) { SelectedDefinition = saving; Error = $"{saving?.Name}: проверьте поле {failure.FieldPath} ({failure.Code})."; }
        catch (Exception failure) when (failure is not OutOfMemoryException) { SelectedDefinition = saving; Error = "Не удалось сохранить сервис. Проверьте папку исходников, настройки и доступ к хранилищу."; }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanConfigure))] private Task ResetAsync(CancellationToken token) => LoadAsync(token);
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (library is null) { return; }
        IsBusy = true; Error = "";
        try
        {
            IReadOnlyList<ComponentLibraryEntry> entries = library is IComponentLibrarySettings writable
                ? await writable.ReadEntriesAsync(cancellationToken)
                : (await library.ReadAsync(cancellationToken)).Select(d => new ComponentLibraryEntry(d, true)).ToArray();
            IReadOnlyList<LocalPathMapping> paths = mappings is null ? [] : await mappings.ReadAsync(cancellationToken);
            Paths.Clear(); foreach (var path in paths) { Paths.Add(path); }
            Guid? selected = SelectedDefinition?.Definition.Id;
            all.Clear(); SelectedDefinition = null;
            foreach (var entry in entries)
            {
                var row = new DefinitionRow(entry.Definition, entry.Enabled, mappings, interactions);
                row.Editor.LoadLocalFolder(paths); Watch(row); all.Add(row);
            }
            Filter(); SelectedDefinition = Definitions.FirstOrDefault(d => d.Definition.Id == selected) ?? Definitions.FirstOrDefault();
            Status = all.Count == 0 ? "Библиотека пуста. Добавьте целевые сервисы и инфраструктуру." : "Библиотека загружена. Изменения применяются после сохранения настроек.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Status = "Чтение отменено."; }
        catch (Exception failure) when (failure is not OutOfMemoryException) { Error = "Не удалось прочитать библиотеку или локальные пути. Проверьте хранилище."; }
        finally { IsBusy = false; }
    }
}
