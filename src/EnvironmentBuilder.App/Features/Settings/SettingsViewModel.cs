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
    public DefinitionRow(ComponentDefinition definition, bool enabled = true, ILocalPathMappings? mappings = null, IAuthoringInteractions? interactions = null, IDockerHubCatalog? catalog = null)
    {
        Definition = definition; name = definition.Name; this.enabled = enabled;
        icon = definition.Icon ?? (definition.ComponentType == "infrastructure" ? "Database" : "Box");
        color = definition.Color ?? "#0072FF";
        Editor = new ServiceSourceViewModel(definition, mappings, interactions, catalog);
        foreach (var requirement in definition.DependencyRequirements) { Dependencies.Add(new LibraryDependencyEditor(requirement)); }
    }
    public ComponentDefinition Definition { get; private set; }
    public ServiceSourceViewModel Editor { get; }
    [ObservableProperty] private string name;
    [ObservableProperty] private string icon;
    [ObservableProperty] private string color;
    public bool CanHaveDependencies => Definition.ComponentType == "target";
    public ObservableCollection<DefinitionRow> DependencyOptions { get; } = [];
    public EditorRows<LibraryDependencyEditor> Dependencies { get; } = [];
    [RelayCommand] private void AddDependency() { if (CanHaveDependencies) { Dependencies.Add(new LibraryDependencyEditor()); } }
    [RelayCommand] private void RemoveDependency(LibraryDependencyEditor dependency) => Dependencies.Remove(dependency);
    public string[] Icons { get; } = ["Box", "Database", "Server", "Globe", "Code", "HardDrive", "Network", "Layers", "Settings", "FileText", "MessageCircle", "Bell", "Play", "Wifi"];
    [ObservableProperty] private bool enabled;
    public string Type => Definition.ComponentType == "target" ? "Целевой сервис" : "Инфраструктура";
    public string Version => Definition.Version;
    public string Source => Editor.IsBuild ? Editor.LocalFolder : Editor.ImageReference;
    public string Details => $"{Name}\n{Type}\n\n{Source}\n\nID: {Definition.Id}\nВерсия: {Version}";
    public bool IsNew { get; set; }
    public ComponentDefinition Build(Func<DefinitionRow, ComponentDefinition>? resolve = null)
    {
        return Definition with
        {
            Name = Name.Trim(),
            DefaultSource = Editor.Build(),
            Icon = Icon,
            Color = Color.Trim(),
            LogoUrl = Editor.LogoUrl,
            DependencyRequirements = CanHaveDependencies ? Dependencies.Select(dependency => dependency.Build(resolve)).ToImmutableArray() : Definition.DependencyRequirements
        };
    }
    public void Accept(ComponentDefinition saved) { Definition = saved; IsNew = false; OnPropertyChanged(nameof(Version)); OnPropertyChanged(nameof(Source)); }
}

public sealed partial class SettingsViewModel(IComponentLibrary? library = null, ILocalPathMappings? mappings = null, IAuthoringInteractions? interactions = null, IDockerHubCatalog? catalog = null) : ObservableObject, IDisposable
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
    public string Summary => SelectedDefinition?.Details ?? "Добавьте целевой сервис или инфраструктуру и сохраните библиотеку.";
    public string WorkflowReason => library is IComponentLibrarySettings
        ? "Сохранённые и включённые сервисы доступны при создании окружения. Параметры конкретной среды задаются в её редакторе."
        : "Изменения недоступны: библиотека открыта только для просмотра.";
    partial void OnSelectedDefinitionChanged(DefinitionRow? value) => OnPropertyChanged(nameof(Summary));
    partial void OnSearchChanged(string value) => Filter();
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanConfigure));
    [RelayCommand(CanExecute = nameof(CanConfigure))] private void AddTarget() => Add("target");
    [RelayCommand(CanExecute = nameof(CanConfigure))] private void AddInfrastructure() => Add("infrastructure");
    private void Add(string type)
    {
        Guid id = Guid.NewGuid();
        var definition = new ComponentDefinition("component", 1, id, "1.0.0", type == "target" ? "Новый сервис" : "Новая инфраструктура",
            type, new ComponentContract("service-" + id.ToString("N"), "1.0.0"), new ImageSource(""), [], ImmutableDictionary<string, Value>.Empty,
            [], [], [], new Readiness("running", 60), [], null);
        var row = new DefinitionRow(definition, mappings: mappings, interactions: interactions, catalog: catalog) { IsNew = true };
        Watch(row); all.Add(row); RefreshDependencyOptions(); Search = ""; Filter(); SelectedDefinition = row;
        Status = "Задайте имя, источник, иконку, цвет и зависимости целевого сервиса, затем сохраните библиотеку.";
    }
    private void Watch(DefinitionRow row)
    {
        row.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(ServiceCount)); OnPropertyChanged(nameof(Summary)); };
        row.Editor.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Summary));
    }
    private void RefreshDependencyOptions()
    {
        foreach (var row in all)
        {
            foreach (var option in all.Where(option => option != row && !row.DependencyOptions.Contains(option))) { row.DependencyOptions.Add(option); }
            foreach (var dependency in row.Dependencies.Where(dependency => dependency.SelectedService is null))
            { dependency.SelectedService = all.FirstOrDefault(option => option != row && option.Definition.Id == dependency.DefinitionId); }
        }
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
            foreach (var row in all)
            {
                saving = row;
                await row.Editor.SelectionTask.WaitAsync(token);
                await row.Editor.SaveLocalFolderAsync(token);
                if (row.Editor.IsBuild && string.IsNullOrWhiteSpace(row.Editor.LocalFolder) && (row.IsNew || row.Definition.DefaultSource is not BuildSource))
                { throw new DocumentException("/source/folder", "required-folder"); }
            }
            var candidates = new Dictionary<DefinitionRow, ComponentDefinition>();
            var visiting = new HashSet<DefinitionRow>();
            ComponentDefinition BuildCandidate(DefinitionRow row)
            {
                if (candidates.TryGetValue(row, out var built)) { return built; }
                if (!visiting.Add(row)) { throw new DocumentException("/dependencyRequirements", "dependency-cycle"); }
                saving = row;
                var definition = row.Build(BuildCandidate);
                saving = row;
                if (definition.DependencyRequirements.Select(requirement => requirement.DefinitionId).Distinct().Count() != definition.DependencyRequirements.Length)
                { throw new DocumentException("/dependencyRequirements", "duplicate-dependency"); }
                if (!row.IsNew && CanonicalJson.Hash(definition) != CanonicalJson.Hash(row.Definition))
                { definition = definition with { Version = "1.0.0-local." + Guid.NewGuid().ToString("N") }; }
                EnvironmentValidator.ValidateDefinition(definition);
                visiting.Remove(row); candidates.Add(row, definition); return definition;
            }
            foreach (var row in all) { BuildCandidate(row); }
            foreach (var (row, definition) in candidates)
            {
                saving = row;
                await writable.SaveAsync(definition, row.Enabled, token);
                row.Accept(definition);
            }
            Status = "Библиотека сохранена. Сервисы доступны в новом окружении.";
            OnPropertyChanged(nameof(ServiceCount));
        }
        catch (OperationCanceledException) { Status = "Сохранение отменено. Изменения остаются в редакторе библиотеки."; }
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
            foreach (var row in all) { row.Editor.Dispose(); }
            all.Clear(); SelectedDefinition = null;
            foreach (var entry in entries)
            {
                var row = new DefinitionRow(entry.Definition, entry.Enabled, mappings, interactions, catalog);
                row.Editor.LoadLocalFolder(paths); Watch(row); all.Add(row);
            }
            RefreshDependencyOptions(); Filter(); SelectedDefinition = Definitions.FirstOrDefault(d => d.Definition.Id == selected) ?? Definitions.FirstOrDefault();
            Status = all.Count == 0 ? "Библиотека пуста. Добавьте целевые сервисы и инфраструктуру." : "Библиотека загружена. Изменения применяются после сохранения библиотеки.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Status = "Чтение отменено."; }
        catch (Exception failure) when (failure is not OutOfMemoryException) { Error = "Не удалось прочитать библиотеку или локальные пути. Проверьте хранилище."; }
        finally { IsBusy = false; }
    }
    public void Dispose() { foreach (var row in all) { row.Editor.Dispose(); } }
}
