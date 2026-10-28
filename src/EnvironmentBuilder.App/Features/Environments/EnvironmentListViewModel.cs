using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.App.Features.Environments;

public sealed record FilterOption(string Key, string Label);
public sealed record ComponentListRow(string Name, string Type, string Source, string Ports, string Status);

public sealed partial class EnvironmentRowViewModel : ObservableObject
{
    public EnvironmentRowViewModel(EnvironmentListItem item) { Item = item; }
    public EnvironmentListItem Item { get; }
    public Guid Id => Item.Id;
    public string Name => Item.Name;
    public string Description => Item.Document?.Description ?? "";
    public long Revision => Item.Revision;
    public string StateKey => Item.ErrorCode is not null ? "unreadable" : Item.HasUnresolvedOperation ? "needsAttention" : "unknown";
    public string State => Item.ErrorCode is not null ? "Ошибка документа: " + Item.ErrorCode : Item.Deletion is not null ? "Удаление не завершено — остались ресурсы или требуется финализация" : Item.HasUnresolvedOperation ? "Требуется сверка операции" : "Docker не проверен";
    public string SavedAt => Item.SavedAt?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "Время сохранения неизвестно";
    public string Components => Item.Document is null ? "Состав недоступен" : string.Join(" · ", Item.Document.Components.Select(c => c.DefinitionSnapshot.Name));
    public string Measurement => "Метрики не получены";
    public string ComponentCount => Item.Document is null ? "—" : $"{Item.Document.Components.Length} комп.";
    public IEnumerable<Components.ServicePresentation> TargetIcons => Icons("target");
    public IEnumerable<Components.ServicePresentation> InfrastructureIcons => Icons("infrastructure");
    private IEnumerable<Components.ServicePresentation> Icons(string type) => Item.Document?.Components.Where(c => c.DefinitionSnapshot.ComponentType == type).Take(4).Select(c => new Components.ServicePresentation(c.DefinitionSnapshot.Name, type, "", type == "infrastructure" ? "Database" : "Box", type == "infrastructure" ? "#1B659A" : "#0072FF")) ?? [];
    public IReadOnlyList<ComponentListRow> ComponentRows => Item.Document?.Components.Select(c => new ComponentListRow(c.DefinitionSnapshot.Name,
        c.DefinitionSnapshot.ComponentType == "target" ? "Приложение" : "Инфраструктура",
        c.Source is ImageSource image ? image.Reference : "Локальная сборка",
        string.Join(", ", c.Ports.Select(p => $"{p.HostPort?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "auto"} → {p.ContainerPort}/{p.Protocol}")), "Не проверен")).ToArray() ?? [];
    public bool CanOpen => Item.Document is not null && Item.Deletion is null;
    public bool CanDelete => Item.Document is not null && (Item.Deletion is not null || !Item.HasUnresolvedOperation);
    public string DisabledReason => Item.Deletion is not null ? Item.Deletion.RetryReason : CanOpen ? "" : "Документ повреждён или не поддерживается. Откройте диагностику хранилища.";
    public string DeletionDetails => Item.Deletion is not { } deletion ? "" :
        $"Удаление окружения {Name}\nID: {Id}\nУдалены: {string.Join(", ", deletion.DeletedResources)}\nОстатки / требуют сверки: {string.Join(", ", deletion.Remnants)}\n" +
        deletion.RetryReason + "\nПовтор заново проверит только подтверждённый scope и запросит подтверждение нового плана.";
    [ObservableProperty] private bool isExpanded;
}

public sealed partial class EnvironmentListViewModel(IEnvironmentStore store) : ObservableObject
{
    private List<EnvironmentRowViewModel> all = [];
    private int loadVersion;
    private Guid? deletingId;
    public ObservableCollection<EnvironmentRowViewModel> VisibleItems { get; } = [];
    public IReadOnlyList<EnvironmentRowViewModel> AllItems => all;
    [ObservableProperty] private bool isLoading = true;
    [ObservableProperty] private string error = "";
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string statusFilter = "all";
    [ObservableProperty] private string typeFilter = "all";
    [ObservableProperty] private Guid? selectedId;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ObserveSelectedCommand), nameof(ClearSelectedDataCommand), nameof(RestoreSelectedBaselineCommand), nameof(DeleteSelectedCommand))]
    private EnvironmentRowViewModel? selectedItem;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand), nameof(CancelDeleteCommand))]
    private bool isDeleting;
    [ObservableProperty] private string deletionNotice = "";
    public double ScrollOffset { get; set; }
    public static FilterOption[] StatusOptions { get; } = [new("all", "Все состояния"), new("unknown", "Не проверено"), new("needsAttention", "Требует сверки"), new("unreadable", "Ошибка документа")];
    public static FilterOption[] TypeOptions { get; } = [new("all", "Все типы"), new("target", "Приложение"), new("infrastructure", "Инфраструктура")];
    public bool IsEmpty => !IsLoading && Error.Length == 0 && all.Count == 0;
    public bool NoResults => !IsLoading && Error.Length == 0 && all.Count > 0 && VisibleItems.Count == 0;
    public bool HasError => Error.Length > 0;
    public bool HasSelection => SelectedItem is not null;
    public bool AllStatus { get => StatusFilter == "all"; set { if (value) { StatusFilter = "all"; } } }
    public bool UnknownStatus { get => StatusFilter == "unknown"; set { if (value) { StatusFilter = "unknown"; } } }
    public bool AttentionStatus { get => StatusFilter == "needsAttention"; set { if (value) { StatusFilter = "needsAttention"; } } }
    public bool ErrorStatus { get => StatusFilter == "unreadable"; set { if (value) { StatusFilter = "unreadable"; } } }
    public string OfflineStatus => "Docker не проверен. Описания и preview доступны без запуска.";
    public string SelectionNotice => SelectedId is not null && VisibleItems.All(i => i.Id != SelectedId) ? "Сохранённое окружение выбрано, но скрыто текущими фильтрами." : "";
    public Func<EnvironmentRowViewModel, Task>? Open { get; set; }
    public Func<Task>? Create { get; set; }
    public Func<EnvironmentRowViewModel, Task>? Inspect { get; set; }
    public Func<EnvironmentRowViewModel, Task>? Start { get; set; }
    public Func<EnvironmentRowViewModel, Task>? Compose { get; set; }
    public Func<EnvironmentRowViewModel, Task>? Observe { get; set; }
    public Func<EnvironmentRowViewModel, Task>? ClearData { get; set; }
    public Func<EnvironmentRowViewModel, Task>? RestoreBaseline { get; set; }
    public Func<EnvironmentRowViewModel, CancellationToken, Task<DeleteEnvironmentResult>>? Delete { get; set; }
    public Func<Guid, Task>? CancelDelete { get; set; }
    public string DeleteLabel => SelectedItem?.Item.Deletion is not null ? "Повторить удаление" : EnvironmentCommands.DeleteLabel;
    public string DeletionDetails => IsDeleting ? "Удаление выполняется. Дождитесь проверенного результата или отмените дальнейшие этапы." :
        string.Join("\n", new[] { DeletionNotice, SelectedItem?.DeletionDetails ?? "" }.Where(s => s.Length > 0));

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        int version = ++loadVersion;
        IsLoading = true;
        Error = "";
        NotifyState();
        try
        {
            IReadOnlyList<EnvironmentListItem> items = await store.ListAsync(cancellationToken);
            if (version != loadVersion) { return; }
            var expanded = all.Where(i => i.IsExpanded).Select(i => i.Id).ToHashSet();
            all = items.Select(i => new EnvironmentRowViewModel(i) { IsExpanded = expanded.Contains(i.Id) }).ToList();
            if (!all.Any(i => i.Id == SelectedId)) { SelectedId = null; }
            Filter();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { if (version == loadVersion) { Error = "Загрузка отменена. Повторите загрузку."; } }
        catch (Exception failure) when (failure is not OutOfMemoryException) { if (version == loadVersion) { Error = "Не удалось прочитать хранилище. Повторите загрузку или откройте диагностику."; } }
        finally { if (version == loadVersion) { IsLoading = false; NotifyState(); } }
    }
    public void SelectSaved(Guid id) { SelectedId = id; SelectedItem = VisibleItems.FirstOrDefault(i => i.Id == id); OnPropertyChanged(nameof(SelectionNotice)); }
    partial void OnSearchChanged(string value) => Filter();
    partial void OnErrorChanged(string value) => NotifyState();
    partial void OnIsLoadingChanged(bool value) => NotifyState();
    partial void OnStatusFilterChanged(string value)
    {
        Filter();
        OnPropertyChanged(nameof(AllStatus)); OnPropertyChanged(nameof(UnknownStatus)); OnPropertyChanged(nameof(AttentionStatus)); OnPropertyChanged(nameof(ErrorStatus));
    }
    partial void OnTypeFilterChanged(string value) => Filter();
    partial void OnSelectedItemChanged(EnvironmentRowViewModel? value)
    {
        if (value is not null) { SelectedId = value.Id; }
        OnPropertyChanged(nameof(DeleteLabel)); OnPropertyChanged(nameof(DeletionDetails)); OnPropertyChanged(nameof(HasSelection));
    }
    partial void OnIsDeletingChanged(bool value) => OnPropertyChanged(nameof(DeletionDetails));
    partial void OnDeletionNoticeChanged(string value) => OnPropertyChanged(nameof(DeletionDetails));
    private void Filter()
    {
        VisibleItems.Clear();
        foreach (EnvironmentRowViewModel item in all.Where(i =>
            (Search.Length == 0 || i.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || i.Description.Contains(Search, StringComparison.OrdinalIgnoreCase)) &&
            (StatusFilter == "all" || i.StateKey == StatusFilter) && (TypeFilter == "all" || i.Item.Document?.Components.Any(c => c.DefinitionSnapshot.ComponentType == TypeFilter) == true)))
        { VisibleItems.Add(item); }
        SelectedItem = VisibleItems.FirstOrDefault(i => i.Id == SelectedId);
        NotifyState();
    }
    private void NotifyState() { OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(NoResults)); OnPropertyChanged(nameof(SelectionNotice)); OnPropertyChanged(nameof(HasError)); }
    [RelayCommand] private Task ReloadAsync(CancellationToken token) => LoadAsync(token);
    [RelayCommand] private Task CreateNewAsync() => Create?.Invoke() ?? Task.CompletedTask;
    [RelayCommand] private Task OpenItemAsync(EnvironmentRowViewModel row) => row.CanOpen ? Open?.Invoke(row) ?? Task.CompletedTask : Task.CompletedTask;
    [RelayCommand] private Task InspectItemAsync(EnvironmentRowViewModel row) => row.CanOpen ? Inspect?.Invoke(row) ?? Task.CompletedTask : Task.CompletedTask;
    [RelayCommand] private Task StartItemAsync(EnvironmentRowViewModel row) => row.CanOpen ? Start?.Invoke(row) ?? Task.CompletedTask : Task.CompletedTask;
    [RelayCommand] private Task ComposeItemAsync(EnvironmentRowViewModel row) => row.CanOpen ? Compose?.Invoke(row) ?? Task.CompletedTask : Task.CompletedTask;
    private bool CanUseSelected() => SelectedItem?.CanOpen == true;
    [RelayCommand(CanExecute = nameof(CanUseSelected))] private Task ObserveSelectedAsync() => SelectedItem?.CanOpen == true ? Observe?.Invoke(SelectedItem) ?? Task.CompletedTask : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanUseSelected))] private Task ClearSelectedDataAsync() => SelectedItem?.CanOpen == true ? ClearData?.Invoke(SelectedItem) ?? Task.CompletedTask : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanUseSelected))] private Task RestoreSelectedBaselineAsync() => SelectedItem?.CanOpen == true ? RestoreBaseline?.Invoke(SelectedItem) ?? Task.CompletedTask : Task.CompletedTask;
    private bool CanDeleteSelected() => !IsDeleting && SelectedItem?.CanDelete == true;
    private bool CanCancelDelete() => IsDeleting;
    [RelayCommand(CanExecute = nameof(CanCancelDelete))] private Task CancelDeleteAsync() => deletingId is { } id ? CancelDelete?.Invoke(id) ?? Task.CompletedTask : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelectedAsync(CancellationToken token)
    {
        if (SelectedItem is not { CanDelete: true } row) { return; }
        deletingId = row.Id; IsDeleting = true; Error = ""; DeletionNotice = "";
        try
        {
            if (Delete is null) { throw new InvalidOperationException("Удаление недоступно."); }
            DeleteEnvironmentResult result = await Delete(row, token);
            DeletionNotice = result.Deleted ? "" : result.Operation?.State == OperationState.Cancelled ? "Удаление отменено." : "Удаление не завершено: " + result.Code;
            await LoadAsync(CancellationToken.None);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            await LoadAsync(CancellationToken.None);
            Error = "Удаление не завершено. Проверьте остатки и повторите после сверки состояния.";
        }
        finally { IsDeleting = false; deletingId = null; }
    }
}
