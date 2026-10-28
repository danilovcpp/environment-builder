using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.App.Features.Environments;

public sealed partial class EnvironmentEditorViewModel : ObservableObject
{
    private readonly CreateEnvironmentService service;
    private readonly IComponentLibrary library;
    private readonly IAuthoringInteractions interactions;
    private readonly IInitializationDocumentReader documents;
    private readonly ILocalPathMappings mappings;
    private readonly IInputResolver inputs;
    private readonly ISeedPackStore packs;
    private readonly IEnvironmentEditing? editing;
    private readonly IDestructiveInteractions? destructive;
    private IsolatedEnvironment? original;
    private bool editEnabled;
    private bool synchronizing;
    private bool unparsedEdits;
    private long editVersion;

    public EnvironmentEditorViewModel(EnvironmentDraft draft, CreateEnvironmentService service, IComponentLibrary library, IAuthoringInteractions interactions,
        IInitializationDocumentReader documents, ILocalPathMappings mappings, IInputResolver inputs, ISeedPackStore packs,
        IEnvironmentEditing? editing = null, IDestructiveInteractions? destructive = null)
    {
        Draft = draft; this.service = service; this.library = library; this.interactions = interactions; this.documents = documents; this.mappings = mappings; this.inputs = inputs; this.packs = packs;
        this.editing = editing; this.destructive = destructive; original = draft.IsPersisted ? draft.Snapshot : null;
        RenderDraft();
        Components.Changed += Edited; Dependencies.Changed += Edited; Baseline.Changed += Edited;
        Validate();
    }
    public EnvironmentDraft Draft { get; }
    public Guid Id => Draft.Snapshot.Id;
    public long Revision => Draft.Snapshot.Revision;
    public bool IsReadOnly => Draft.IsPersisted && !editEnabled;
    public bool CanEdit => !IsReadOnly && !IsBusy;
    public bool CanInteract => !IsBusy;
    public bool IsDirty => unparsedEdits || Draft.IsDirty;
    public bool CanSave => CanEdit && Errors.Count == 0;
    public bool CanPreview => !IsBusy && Errors.Count == 0;
    public bool CanInspect => Draft.IsPersisted && !IsBusy && !IsDirty;
    public bool CanBeginEdit => IsReadOnly && !IsBusy && editing is not null;
    public string SaveDisabledReason => IsReadOnly ? "Сохранённое описание открыто для просмотра. Изменение требует подтверждённой остановки и очистки." : IsBusy ? "Дождитесь завершения текущего действия." : Errors.Count != 0 ? "Исправьте ошибки структуры перед сохранением." : "";
    public string Title => IsReadOnly ? "Описание окружения" : Draft.IsPersisted ? "Изменение окружения" : "Новое окружение";
    public string SavedDocumentJson => System.Text.Json.JsonSerializer.Serialize(Draft.Snapshot, new System.Text.Json.JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = true });
    public EditorRows<ComponentEditorViewModel> Components { get; } = [];
    public EditorRows<DependencyEditor> Dependencies { get; } = [];
    public EditorRows<BaselineEditor> Baseline { get; } = [];
    public ObservableCollection<DefinitionChoice> Library { get; } = [];
    public IEnumerable<DefinitionChoice> TargetDefinitions => Library.Where(d => d.Definition.ComponentType == "target");
    public IEnumerable<DefinitionChoice> InfrastructureDefinitions => Library.Where(d => d.Definition.ComponentType == "infrastructure");
    public string ConfigurationSummary => $"{Components.Count(c => c.Definition.ComponentType == "target")} приложений · {Components.Count(c => c.Definition.ComponentType == "infrastructure")} инфраструктурных · {Dependencies.Count} связей · {Baseline.Count} baseline";
    public ObservableCollection<ValidationMessage> Errors { get; } = [];
    public ObservableCollection<ReadinessIssue> ReadinessIssues { get; } = [];
    public ObservableCollection<ComponentChoice> ComponentChoices { get; } = [];
    public ObservableCollection<DependencyChoice> UnresolvedDependencies { get; } = [];
    [ObservableProperty] private DefinitionChoice? selectedDefinition;
    [ObservableProperty] private DependencyChoice? selectedRequirement;
    [ObservableProperty] private ComponentChoice? selectedRequiredInstance;
    [ObservableProperty] private ComponentChoice? baselineTarget;
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string description = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(CanSave), nameof(CanPreview), nameof(SaveDisabledReason))]
    private bool isBusy;
    [ObservableProperty] private string actionError = "";
    [ObservableProperty] private string libraryStatus = "Загрузка библиотеки…";
    [ObservableProperty] private string mappingReference = "local-path://source";
    [ObservableProperty] private string focusField = "";
    public string LastFocusedField { get; set; } = "name";
    public double ScrollOffset { get; set; }
    public Func<IsolatedEnvironment, Task>? Saved { get; set; }
    public Func<IsolatedEnvironment, Task>? Preview { get; set; }
    public Func<IsolatedEnvironment, Task>? Inspect { get; set; }
    public Func<Task>? Back { get; set; }

    public async Task LoadLibraryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var definitions = await library.ReadAsync(cancellationToken);
            Library.Clear(); foreach (var definition in definitions) { Library.Add(new DefinitionChoice(definition)); }
            SelectedDefinition ??= Library.FirstOrDefault();
            LibraryStatus = Library.Count == 0 ? "Библиотека пуста. Импортируйте JSON-описание компонента." : $"Описаний в библиотеке: {Library.Count}";
            OnPropertyChanged(nameof(TargetDefinitions)); OnPropertyChanged(nameof(InfrastructureDefinitions));
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException) { LibraryStatus = "Не удалось прочитать библиотеку. Проверьте документы в хранилище."; }
    }
    public IsolatedEnvironment BuildSnapshot() => Draft.Snapshot with
    {
        Name = Name,
        Description = Description.Length == 0 ? null : Description,
        Components = Components.Select(c => c.Build()).ToImmutableArray(),
        Dependencies = Dependencies.Select(d => d.Build()).ToImmutableArray(),
        Baseline = new DataBaseline(Baseline.Select(b => b.Build()).ToImmutableArray())
    };

    private void Validate()
    {
        Errors.Clear();
        try
        {
            Draft.Update(BuildSnapshot());
            unparsedEdits = false;
            EnvironmentValidator.Validate(Draft.Snapshot);
        }
        catch (DocumentException error) { Errors.Add(new ValidationMessage(error.FieldPath, Explain(error.Code))); }
        catch (Exception error) when (error is System.Text.Json.JsonException or ArgumentException) { Errors.Add(new ValidationMessage("/components", "Проверьте типы и обязательные поля документа.")); }
        UnresolvedDependencies.Clear(); foreach (DependencyChoice choice in Draft.UnresolvedDependencies) { UnresolvedDependencies.Add(choice); }
        NotifyState();
    }
    private static string Explain(string code) => code switch
    {
        "blank-name" => "Введите имя окружения.",
        "required-parameter" => "Заполните обязательный параметр.",
        "host-port-overlap" => "Публикуемый порт уже используется другим экземпляром.",
        "plaintext-secret-forbidden" => "Для секретного параметра укажите secret:// ссылку.",
        _ => "Проверьте поле: " + code
    };
    private void Edited(object? sender, EventArgs e)
    {
        if (synchronizing) { return; }
        editVersion++; unparsedEdits = true; ReadinessIssues.Clear(); Validate();
    }
    partial void OnNameChanged(string value) => Edited(this, EventArgs.Empty);
    partial void OnDescriptionChanged(string value) => Edited(this, EventArgs.Empty);
    private void RenderDraft()
    {
        synchronizing = true;
        Name = Draft.Snapshot.Name; Description = Draft.Snapshot.Description ?? "";
        Components.Clear(); ComponentChoices.Clear(); Dependencies.Clear(); Baseline.Clear();
        foreach (EnvironmentComponent component in Draft.Snapshot.Components)
        {
            var model = new ComponentEditorViewModel(component, IsReadOnly); Components.Add(model); ComponentChoices.Add(new ComponentChoice(component.Id, model.Label));
        }
        foreach (ComponentDependency dependency in Draft.Snapshot.Dependencies) { Dependencies.Add(DependencyEditor.From(dependency)); }
        foreach (BaselineApplication application in Draft.Snapshot.Baseline.Applications)
        {
            ComponentDefinition? definition = Draft.Snapshot.Components.FirstOrDefault(c => c.Id == application.ComponentId)?.DefinitionSnapshot;
            bool mandatory = application.SeedPack is null && definition?.Initialization is not null && CanonicalJson.Hash(application.Initializer) == CanonicalJson.Hash(definition.Initialization);
            Baseline.Add(new BaselineEditor(application, mandatory, documents.ReadInitialization));
        }
        BaselineTarget = ComponentChoices.FirstOrDefault(); synchronizing = false;
    }
    private void NotifyState()
    {
        OnPropertyChanged(nameof(ConfigurationSummary));
        OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(CanSave)); OnPropertyChanged(nameof(CanPreview)); OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanInteract)); OnPropertyChanged(nameof(SaveDisabledReason));
        SaveCommand.NotifyCanExecuteChanged(); ShowPreviewCommand.NotifyCanExecuteChanged(); InspectSavedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanBeginEdit)); OnPropertyChanged(nameof(CanInspect)); BeginEditCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsBusyChanged(bool value) => NotifyState();

    [RelayCommand]
    private void AddDefinition(DefinitionChoice definition)
    {
        SelectedDefinition = definition;
        AddComponentCommand.Execute(null);
    }

    [RelayCommand(CanExecute = nameof(CanBeginEdit))]
    private async Task BeginEditAsync(CancellationToken token)
    {
        if (editing is null || original is null) { return; }
        await WithActionAsync(async () =>
        {
            var guard = await editing.CheckAsync(original, token);
            if (!guard.ConfirmedQuiescent) { ActionError = guard.Reason ?? "Остановка не подтверждена."; return; }
            editEnabled = true; RenderDraft();
            OnPropertyChanged(nameof(IsReadOnly)); OnPropertyChanged(nameof(Title)); NotifyState();
        });
    }

    [RelayCommand]
    private void AddComponent()
    {
        if (!CanEdit || SelectedDefinition is null) { return; }
        try { Draft.Update(BuildSnapshot()); Draft.AddComponent(SelectedDefinition.Definition, Library.Select(i => i.Definition).ToArray()); RenderDraft(); Edited(this, EventArgs.Empty); }
        catch (DocumentException error) { ActionError = error.Message; }
    }
    [RelayCommand]
    private void RemoveComponent(ComponentEditorViewModel component)
    {
        if (!CanEdit) { return; }
        synchronizing = true;
        Components.Remove(component);
        foreach (var edge in Dependencies.Where(e => e.DependentId == component.Id || e.RequiredId == component.Id).ToArray()) { Dependencies.Remove(edge); }
        foreach (var application in Baseline.Where(a => a.ComponentId == component.Id).ToArray()) { Baseline.Remove(application); }
        ComponentChoices.Remove(ComponentChoices.Single(c => c.Id == component.Id));
        synchronizing = false; Edited(this, EventArgs.Empty);
    }
    [RelayCommand]
    private void BindRequirement()
    {
        if (!CanEdit || SelectedRequirement is null || SelectedRequiredInstance is null) { return; }
        try { Draft.Update(BuildSnapshot()); Draft.Bind(SelectedRequirement, SelectedRequiredInstance.Id); RenderDraft(); Edited(this, EventArgs.Empty); }
        catch (DocumentException error) { ActionError = error.Message; }
    }
    [RelayCommand] private void AddDependency() { if (CanEdit) { Dependencies.Add(new DependencyEditor()); } }
    [RelayCommand] private void RemoveDependency(DependencyEditor row) { if (CanEdit) { Dependencies.Remove(row); } }
    [RelayCommand] private void RemoveBaseline(BaselineEditor row) { if (CanEdit && !row.IsMandatory) { Baseline.Remove(row); } }
    [RelayCommand]
    private void MoveBaselineUp(BaselineEditor row)
    {
        if (CanEdit && !row.IsMandatory && Baseline.IndexOf(row) > 0) { Baseline.Move(Baseline.IndexOf(row), Baseline.IndexOf(row) - 1); Edited(this, EventArgs.Empty); }
    }
    [RelayCommand]
    private async Task ImportComponentAsync(CancellationToken cancellationToken)
    {
        if (!CanEdit) { return; }
        await WithActionAsync(async () =>
        {
            byte[]? bytes = await interactions.SelectComponentDocumentAsync(cancellationToken);
            if (bytes is null) { return; }
            ComponentDefinition imported = await library.ImportAsync(bytes, cancellationToken);
            await LoadLibraryAsync(cancellationToken);
            SelectedDefinition = Library.First(i => i.Definition.Id == imported.Id && i.Definition.Version == imported.Version);
        });
    }
    [RelayCommand]
    private async Task AddBaselineAsync(CancellationToken cancellationToken)
    {
        if (!CanEdit || BaselineTarget is null) { return; }
        await WithActionAsync(async () =>
        {
            string? json = await interactions.SelectInitializerDocumentAsync(cancellationToken);
            if (json is null) { return; }
            Baseline.Add(new BaselineEditor(new BaselineApplication(BaselineTarget.Id, documents.ReadInitialization(json), null), false, documents.ReadInitialization));
        });
    }
    [RelayCommand]
    private async Task ChooseFolderAsync(CancellationToken cancellationToken)
    {
        if (!CanEdit) { return; }
        await WithActionAsync(async () =>
        {
            string? path = await interactions.SelectFolderAsync(cancellationToken);
            if (path is not null) { await mappings.MapAsync(MappingReference, path, cancellationToken); }
        });
        await CheckReadinessAsync(cancellationToken);
    }
    [RelayCommand]
    private async Task CheckReadinessAsync(CancellationToken cancellationToken)
    {
        Validate(); if (Errors.Count != 0) { return; }
        long version = editVersion;
        await WithActionAsync(async () =>
        {
            var issues = await EnvironmentReadiness.CheckAsync(BuildSnapshot(), inputs, packs, cancellationToken);
            if (version != editVersion) { return; }
            ReadinessIssues.Clear(); foreach (var issue in issues) { ReadinessIssues.Add(issue); }
        });
    }
    public async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        Validate(); if (!CanSave) { return false; }
        bool saved = false;
        await WithActionAsync(async () =>
        {
            if (original is not null && editing is not null && destructive is not null)
            {
                IsolatedEnvironment requested = BuildSnapshot();
                var edited = await editing.SaveAsync(original, requested, destructive.ConfirmAsync, cancellationToken);
                if (edited.Saved && edited.Environment is not null)
                {
                    IsolatedEnvironment visible = BuildSnapshot();
                    saved = CanonicalJson.Hash(visible) == CanonicalJson.Hash(requested);
                    Draft.AcceptSaved(edited.Environment); original = edited.Environment; unparsedEdits = !saved; editEnabled = !saved;
                    if (saved) { RenderDraft(); }
                    else { Draft.Update(visible with { Revision = edited.Environment.Revision }); ActionError = "Сохранён подтверждённый snapshot. Новые изменения остаются в черновике."; }
                    OnPropertyChanged(nameof(IsReadOnly)); OnPropertyChanged(nameof(Revision)); OnPropertyChanged(nameof(Title));
                }
                else
                {
                    ActionError = edited.Code switch
                    {
                        "cancelled" => "Изменение отменено. Черновик и прежнее описание сохранены.",
                        "stale-plan" or "saved-configuration-changed" or "resource-authority-changed" => "План устарел. Проверьте состояние и сохраните снова для нового подтверждения.",
                        _ => "Новая ревизия не сохранена. Прежнее описание остаётся действующим. Уже удалённые данные не восстанавливаются."
                    };
                    if (edited.Operation is { } operation) { ActionError += "\nПроверенно удалены: " + string.Join(", ", operation.DeletedResources) + "\nОстатки / требуют сверки: " + string.Join(", ", operation.Remnants); }
                }
                return;
            }
            CreateEnvironmentResult result = await service.CreateAsync(Draft, cancellationToken);
            ReadinessIssues.Clear(); foreach (var issue in result.ReadinessIssues) { ReadinessIssues.Add(issue); }
            // Navigation is only safe if the committed snapshot still matches the visible draft.
            saved = CanonicalJson.Hash(result.Environment) == CanonicalJson.Hash(BuildSnapshot());
            if (saved) { unparsedEdits = false; }
            else { ActionError = "Сохранён прежний snapshot. Новые изменения остаются в черновике."; }
            OnPropertyChanged(nameof(IsReadOnly));
        });
        NotifyState(); return saved;
    }
    [RelayCommand(CanExecute = nameof(CanSave), IncludeCancelCommand = true)]
    private async Task SaveAsync(CancellationToken cancellationToken)
    { if (await TrySaveAsync(cancellationToken) && Saved is not null) { await Saved(Draft.Snapshot); } }
    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task ShowPreviewAsync()
    {
        Validate(); if (Errors.Count == 0 && Preview is not null) { await Preview(BuildSnapshot()); }
    }
    [RelayCommand] private Task GoBackAsync() => Back?.Invoke() ?? Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanInspect))] private Task InspectSavedAsync() => Inspect?.Invoke(Draft.Snapshot) ?? Task.CompletedTask;
    [RelayCommand] private void FocusError(ValidationMessage issue) { FocusField = ""; FocusField = issue.FieldPath; }
    private async Task WithActionAsync(Func<Task> action)
    {
        IsBusy = true; ActionError = "";
        try { await action(); }
        catch (OperationCanceledException) { ActionError = "Действие отменено. Введённые данные сохранены в черновике."; }
        catch (DocumentException error) { ActionError = Explain(error.Code) + " · " + error.FieldPath; }
        catch (Exception error) when (error is not OutOfMemoryException) { ActionError = "Не удалось выполнить действие. Проверьте входные данные и доступ к хранилищу."; }
        finally { IsBusy = false; }
    }
}
