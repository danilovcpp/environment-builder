using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.App.Features.SeedPacks;

public sealed record SeedPackRow(SeedPack Manifest)
{
    public SeedPackReference Reference => new(Manifest.Id, Manifest.Version, Manifest.ContentHash);
    public string Title => Manifest.Title;
    public string Theme => Manifest.Theme;
    public string Version => Manifest.Version;
    public long Bytes => Manifest.Sections.Sum(s => s.Bytes) + Manifest.Initializers.Sum(s => s.Bytes);
    public long Records => Manifest.Sections.Sum(s => s.RecordCount);
    public int SectionCount => Manifest.Sections.Length;
    public string Size => Bytes < 1048576 ? $"{Bytes / 1024d:N0} KB" : $"{Bytes / 1048576d:N1} MB";
    public string Entities => $"{SectionCount} секц. / {Records:N0} записей";
    public string Status => "Проверен";
    public string Icon => "Database";
    public bool Installed => false;
    public string DiskSize => Size;
    public string ValidationTitle => "Пакет проверен при импорте";
    public string ValidationDetail => "Состав и hashes проверены. Совместимость проверяется при назначении.";
    public string Compatibility => string.Join(" · ", Manifest.Compatibility.Select(c => c.ContractId + " " + string.Join(", ", c.Versions)));
    public string InitializerCount => Manifest.Initializers.Length.ToString(System.Globalization.CultureInfo.CurrentCulture);
    public string Summary => $"{Title}\n{Theme} · v{Version}\n\n{Manifest.Purpose}\n\nСекций: {SectionCount} · записей: {Records:N0}\nПроверенный размер: {Bytes:N0} bytes\n\nID: {Manifest.Id}\nSHA-256: {Manifest.ContentHash}\n\nСовместимость:\n" + string.Join("\n", Manifest.Compatibility.Select(c => c.ContractId + " · " + string.Join(", ", c.Versions)));
}
public interface ISeedPackInteractions
{
    Task<string?> SelectArchiveAsync(CancellationToken token);
    Task<bool> ConfirmRemovalAsync(SeedPackRow pack, CancellationToken token);
    Task OpenOwnerAsync(SeedPackOwner owner, CancellationToken token);
    Task OpenAssignmentAsync(SeedPackRow pack, CancellationToken token) => Task.CompletedTask;
    Task CopyIdentityAsync(SeedPackRow pack, CancellationToken token) => Task.CompletedTask;
}

public sealed partial class SeedPackLibraryViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<SeedPack>>>? read;
    private readonly ISeedPackImporter? importer;
    private readonly ISeedPackCatalog? catalog;
    private readonly ISeedPackInteractions? interactions;
    private IReadOnlyList<SeedPackRow> all = [];
    private long ownerSelection;
    public SeedPackLibraryViewModel(Func<CancellationToken, Task<IReadOnlyList<SeedPack>>>? read = null, ISeedPackImporter? importer = null,
        ISeedPackCatalog? catalog = null, ISeedPreviewReader? preview = null, ISeedPackInteractions? interactions = null)
    { this.read = read; this.importer = importer; this.catalog = catalog; this.interactions = interactions; Preview = new(preview); }
    public SeedPackPreviewViewModel Preview { get; }
    public ObservableCollection<SeedPackRow> Items { get; } = [];
    public ObservableCollection<SeedPackOwner> Owners { get; } = [];
    public ObservableCollection<SeedPackRow> DisplayItems => Items;
    public SeedPackRow? SelectedDisplayPack { get => SelectedPack; set => SelectedPack = value; }
    public string DisplayCount => Count;
    public string InstalledCount => RecordCount;
    public string DisplaySize => Size;
    public string ExampleNotice => "";
    [ObservableProperty] private string displayFilter = "Все";
    partial void OnDisplayFilterChanged(string value) => Filter();
    [RelayCommand] private void FilterTheme(string value) => DisplayFilter = value;
    public string EntityCount => IsLoaded ? all.SelectMany(p => p.Manifest.Sections).Select(s => s.Id).Distinct().Count().ToString(System.Globalization.CultureInfo.CurrentCulture) : "—";
    public ObservableCollection<string> Themes { get; } = ["Все темы"];
    [ObservableProperty] private SeedPackRow? selectedPack;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string theme = "Все темы";
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isLoaded;
    [ObservableProperty] private string error = "";
    [ObservableProperty] private string progress = "";
    [ObservableProperty] private string ownerStatus = "";
    [ObservableProperty] private int selectedTab;
    public string Count => IsLoaded ? all.Count.ToString(System.Globalization.CultureInfo.CurrentCulture) : "—";
    public string Size => !IsLoaded ? "—" : all.Sum(p => p.Bytes) < 1048576 ? $"{all.Sum(p => p.Bytes) / 1024d:N0} KB" : $"{all.Sum(p => p.Bytes) / 1048576d:N1} MB";
    public string RecordCount => IsLoaded ? all.Sum(p => p.Records).ToString("N0", System.Globalization.CultureInfo.CurrentCulture) : "—";
    public bool IsEmpty => IsLoaded && all.Count == 0;
    public bool NoResults => IsLoaded && all.Count > 0 && Items.Count == 0;
    public string Summary => SelectedPack?.Summary ?? "Выберите пакет в библиотеке. Импортируйте внешний ZIP для начала работы.";
    public string WorkflowReason => importer is null ? "Импорт и удаление недоступны: хранилище не подключено." : "Импорт и preview работают без Docker. Назначьте точную версию компоненту в редакторе; наполнение выполняется при явном запуске или восстановлении baseline.";
    public bool CanRemove => !IsBusy && SelectedPack is not null && catalog is not null && interactions is not null;
    private bool CanImport() => !IsBusy && importer is not null && interactions is not null;
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanRemove)); ImportCommand.NotifyCanExecuteChanged(); RemoveCommand.NotifyCanExecuteChanged(); ApplyToEnvironmentCommand.NotifyCanExecuteChanged(); }
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (read is null) { return; }
        IsLoading = true; Error = "";
        try
        {
            var next = await read(cancellationToken);
            all = next.Select(p => new SeedPackRow(p)).ToArray();
            Themes.Clear(); Themes.Add("Все темы");
            foreach (string value in all.Select(p => p.Theme).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) { Themes.Add(value); }
            if (!Themes.Contains(Theme)) { Theme = "Все темы"; }
            IsLoaded = true; Filter();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Error = "Чтение библиотеки отменено."; }
        catch (Exception error) when (error is not OutOfMemoryException) { Error = "Не удалось прочитать метаданные пакетов. Проверьте хранилище в диагностике."; }
        finally { IsLoading = false; NotifyState(); }
    }
    partial void OnSearchChanged(string value) => Filter();
    partial void OnThemeChanged(string value) => Filter();
    partial void OnSelectedPackChanged(SeedPackRow? value)
    {
        OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(CanRemove)); RemoveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedDisplayPack)); ApplyToEnvironmentCommand.NotifyCanExecuteChanged();
        Preview.Select(value?.Manifest); Owners.Clear(); OwnerStatus = ""; ownerSelection++;
    }
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task OwnersAsync(CancellationToken token)
    {
        var selected = SelectedPack; long version = ownerSelection;
        if (selected is null || catalog is null) { return; }
        try
        {
            var owners = await catalog.ReadOwnersAsync(selected.Reference, token);
            if (version != ownerSelection) { return; }
            Owners.Clear(); foreach (var owner in owners) { Owners.Add(owner); }
            OwnerStatus = Owners.Count == 0 ? "Сохранённые ссылки отсутствуют; активные чтения повторно проверяются при удалении." : "Удаление удерживают:";
            SelectedTab = 3;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException) { if (version == ownerSelection) { OwnerStatus = "Не удалось проверить владельцев."; } }
    }
    [RelayCommand] private Task OpenOwnerAsync(SeedPackOwner owner, CancellationToken token) => interactions?.OpenOwnerAsync(owner, token) ?? Task.CompletedTask;
    private bool CanAssign() => !IsBusy && SelectedPack is not null && interactions is not null;
    [RelayCommand(CanExecute = nameof(CanAssign))] private Task ApplyToEnvironmentAsync(CancellationToken token) => interactions!.OpenAssignmentAsync(SelectedPack!, token);
    [RelayCommand]
    private async Task CopyIdentityAsync(SeedPackRow? row, CancellationToken token)
    {
        if ((row ?? SelectedPack) is not { } selected || interactions is null) { return; }
        try { await interactions.CopyIdentityAsync(selected, token); }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException) { Error = "Буфер обмена недоступен. Повторите копирование."; }
    }
    [RelayCommand] private async Task ViewPackAsync(SeedPackRow row) { SelectedPack = row; await Preview.FirstCommand.ExecuteAsync(null); }
    [RelayCommand] private async Task PackOwnersAsync(SeedPackRow row) { SelectedPack = row; await OwnersCommand.ExecuteAsync(null); }
    [RelayCommand(CanExecute = nameof(CanImport), IncludeCancelCommand = true)]
    private async Task ImportAsync(CancellationToken token)
    {
        IsBusy = true; Error = ""; Progress = "Выбор ZIP…";
        try
        {
            string? path = await interactions!.SelectArchiveAsync(token);
            if (path is null) { return; }
            Progress = "Проверка архива, секций и построение индекса…";
            SeedPackReference imported = await importer!.ImportAsync(path, token);
            await LoadAsync(token);
            SelectedPack = Items.FirstOrDefault(p => p.Reference == imported);
            Progress = "Пакет проверен и сохранён.";
        }
        catch (OperationCanceledException) { Progress = "Импорт отменён. Частичный пакет не добавлен."; }
        catch (Exception error) when (error is not OutOfMemoryException) { Error = "Импорт отклонён. Проверьте формат ZIP, состав, размеры, hashes и неизменность версии."; Progress = ""; }
        finally { IsBusy = false; if (Progress == "Выбор ZIP…") { Progress = ""; } }
    }
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync(CancellationToken token)
    {
        SeedPackRow selected = SelectedPack!;
        IsBusy = true; Error = "";
        try
        {
            await OwnersAsync(token);
            if (Owners.Count != 0) { return; }
            if (!await interactions!.ConfirmRemovalAsync(selected, token)) { return; }
            await new DeleteSeedPackService(catalog!).DeleteAsync(selected.Reference, token);
            await LoadAsync(token); Progress = "Неиспользуемая версия удалена.";
        }
        catch (SeedPackInUseException held)
        { Owners.Clear(); foreach (var owner in held.Owners) { Owners.Add(owner); } OwnerStatus = "Удаление удерживают:"; SelectedTab = 3; }
        catch (OperationCanceledException) { Progress = "Удаление отменено."; }
        catch (Exception error) when (error is not OutOfMemoryException) { Error = "Не удалось удалить версию. Проверьте владельцев и хранилище."; }
        finally { IsBusy = false; }
    }
    private void Filter()
    {
        SeedPackReference? previous = SelectedPack?.Reference;
        Items.Clear();
        foreach (var item in all.Where(p => (Theme == "Все темы" || p.Theme == Theme) && (DisplayFilter == "Все" || p.Theme == DisplayFilter || DisplayFilter == "Большие" && p.Bytes >= 32 * 1024 * 1024) &&
            (Search.Length == 0 || p.Title.Contains(Search, StringComparison.OrdinalIgnoreCase) || p.Manifest.Purpose.Contains(Search, StringComparison.OrdinalIgnoreCase)))) { Items.Add(item); }
        SelectedPack = Items.FirstOrDefault(p => p.Reference == previous);
        NotifyState();
    }
    private void NotifyState()
    { foreach (string property in new[] { nameof(Count), nameof(Size), nameof(IsEmpty), nameof(NoResults), nameof(RecordCount), nameof(EntityCount), nameof(DisplayCount), nameof(InstalledCount), nameof(DisplaySize) }) { OnPropertyChanged(property); } }
}
