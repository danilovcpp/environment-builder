using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvironmentBuilder.Core.Abstractions;

namespace EnvironmentBuilder.App.Features.Environments;

public sealed record FilterOption(string Key, string Label);

public sealed partial class EnvironmentRowViewModel : ObservableObject
{
    public EnvironmentRowViewModel(EnvironmentListItem item) { Item = item; }
    public EnvironmentListItem Item { get; }
    public Guid Id => Item.Id;
    public string Name => Item.Name;
    public string Description => Item.Document?.Description ?? "";
    public long Revision => Item.Revision;
    public string StateKey => Item.ErrorCode is not null ? "unreadable" : Item.HasUnresolvedOperation ? "needsAttention" : "unknown";
    public string State => Item.ErrorCode is not null ? "Ошибка документа: " + Item.ErrorCode : Item.HasUnresolvedOperation ? "Требуется сверка операции" : "Runtime не проверен";
    public string SavedAt => Item.SavedAt?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "Время сохранения неизвестно";
    public string Components => Item.Document is null ? "Состав недоступен" : string.Join(" · ", Item.Document.Components.Select(c => c.DefinitionSnapshot.Name));
    public string Measurement => "Метрики не получены";
    public bool CanOpen => Item.Document is not null;
    public string DisabledReason => CanOpen ? "" : "Документ повреждён или не поддерживается. Откройте диагностику хранилища.";
    [ObservableProperty] private bool isExpanded;
}

public sealed partial class EnvironmentListViewModel(IEnvironmentStore store) : ObservableObject
{
    private List<EnvironmentRowViewModel> all = [];
    private int loadVersion;
    public ObservableCollection<EnvironmentRowViewModel> VisibleItems { get; } = [];
    public IReadOnlyList<EnvironmentRowViewModel> AllItems => all;
    [ObservableProperty] private bool isLoading = true;
    [ObservableProperty] private string error = "";
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string statusFilter = "all";
    [ObservableProperty] private string typeFilter = "all";
    [ObservableProperty] private Guid? selectedId;
    [ObservableProperty] private EnvironmentRowViewModel? selectedItem;
    public double ScrollOffset { get; set; }
    public static FilterOption[] StatusOptions { get; } = [new("all", "Все состояния"), new("unknown", "Не проверено"), new("needsAttention", "Требует сверки"), new("unreadable", "Ошибка документа")];
    public static FilterOption[] TypeOptions { get; } = [new("all", "Все типы"), new("target", "Приложение"), new("infrastructure", "Инфраструктура")];
    public bool IsEmpty => !IsLoading && Error.Length == 0 && all.Count == 0;
    public bool NoResults => !IsLoading && Error.Length == 0 && all.Count > 0 && VisibleItems.Count == 0;
    public string OfflineStatus => "Docker не проверен. Описания и preview доступны без запуска.";
    public string SelectionNotice => SelectedId is not null && VisibleItems.All(i => i.Id != SelectedId) ? "Сохранённое окружение выбрано, но скрыто текущими фильтрами." : "";
    public Func<EnvironmentRowViewModel, Task>? Open { get; set; }
    public Func<Task>? Create { get; set; }
    public Func<EnvironmentRowViewModel, Task>? Inspect { get; set; }
    public Func<EnvironmentRowViewModel, Task>? Start { get; set; }
    public Func<EnvironmentRowViewModel, Task>? Compose { get; set; }

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
    partial void OnStatusFilterChanged(string value) => Filter();
    partial void OnTypeFilterChanged(string value) => Filter();
    partial void OnSelectedItemChanged(EnvironmentRowViewModel? value) { if (value is not null) { SelectedId = value.Id; } }
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
    private void NotifyState() { OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(NoResults)); OnPropertyChanged(nameof(SelectionNotice)); }
    [RelayCommand] private Task ReloadAsync(CancellationToken token) => LoadAsync(token);
    [RelayCommand] private Task CreateNewAsync() => Create?.Invoke() ?? Task.CompletedTask;
    [RelayCommand] private Task OpenItemAsync(EnvironmentRowViewModel row) => row.CanOpen ? Open?.Invoke(row) ?? Task.CompletedTask : Task.CompletedTask;
    [RelayCommand] private Task InspectItemAsync(EnvironmentRowViewModel row) => row.CanOpen ? Inspect?.Invoke(row) ?? Task.CompletedTask : Task.CompletedTask;
    [RelayCommand] private Task StartItemAsync(EnvironmentRowViewModel row) => row.CanOpen ? Start?.Invoke(row) ?? Task.CompletedTask : Task.CompletedTask;
    [RelayCommand] private Task ComposeItemAsync(EnvironmentRowViewModel row) => row.CanOpen ? Compose?.Invoke(row) ?? Task.CompletedTask : Task.CompletedTask;
}
