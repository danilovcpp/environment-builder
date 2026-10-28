using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.App.Features.SeedPacks;

public sealed record SeedPackRow(SeedPack Manifest)
{
    public string Title => Manifest.Title;
    public string Theme => Manifest.Theme;
    public string Version => Manifest.Version;
    public long Bytes => Manifest.Sections.Sum(s => s.Bytes) + Manifest.Initializers.Sum(s => s.Bytes);
    public long Records => Manifest.Sections.Sum(s => s.RecordCount);
    public int SectionCount => Manifest.Sections.Length;
    public string Summary => $"{Title}\n{Theme} · v{Version}\n\n{Manifest.Purpose}\n\nСекций: {SectionCount} · записей: {Records}\nРазмер по manifest: {Bytes:N0} bytes\n\nID: {Manifest.Id}\nSHA-256: {Manifest.ContentHash}\n\nСовместимость:\n" + string.Join("\n", Manifest.Compatibility.Select(c => c.ContractId + " · " + string.Join(", ", c.Versions)));
}

public sealed partial class SeedPackLibraryViewModel(Func<CancellationToken, Task<IReadOnlyList<SeedPack>>>? read = null) : ObservableObject
{
    private IReadOnlyList<SeedPackRow> all = [];
    public ObservableCollection<SeedPackRow> Items { get; } = [];
    public ObservableCollection<SeedPackPresentation> DisplayItems { get; } = new(SeedPackPresentation.Examples);
    [ObservableProperty] private SeedPackPresentation? selectedDisplayPack = SeedPackPresentation.Examples[0];
    public bool ShowingExamples => all.Count == 0 && Error.Length == 0;
    public string DisplayCount => ShowingExamples ? "8" : Count;
    public string InstalledCount => ShowingExamples ? "5" : "—";
    public string DisplaySize => ShowingExamples ? "382 MB" : Size;
    public string EntityCount => ShowingExamples ? "12" : all.SelectMany(p => p.Manifest.Sections).Select(s => s.Id).Distinct().Count().ToString(System.Globalization.CultureInfo.CurrentCulture);
    public string ExampleNotice => ShowingExamples ? "Демонстрационные данные · библиотека пока пуста" : "";
    [ObservableProperty] private string displayFilter = "Все";
    partial void OnDisplayFilterChanged(string value) => Filter();
    [CommunityToolkit.Mvvm.Input.RelayCommand] private void FilterTheme(string value) => DisplayFilter = value;
    public ObservableCollection<string> Themes { get; } = ["Все темы"];
    [ObservableProperty] private SeedPackRow? selectedPack;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string theme = "Все темы";
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isLoaded;
    [ObservableProperty] private string error = "";
    public string Count => IsLoaded ? all.Count.ToString(System.Globalization.CultureInfo.CurrentCulture) : "—";
    public string Size => IsLoaded ? $"{all.Sum(p => p.Bytes):N0} bytes" : "—";
    public bool IsEmpty => IsLoaded && all.Count == 0;
    public bool NoResults => IsLoaded && all.Count > 0 && Items.Count == 0;
    public string Summary => SelectedPack?.Summary ?? "Выберите пакет в библиотеке.\n\nЗдесь отображаются сохранённые метаданные. Проверка содержимого, постраничный просмотр и применение пока недоступны в этой версии.";
    public string WorkflowReason => "Импорт, удаление, применение и постраничный просмотр пока недоступны в этой версии. Сохранённые метаданные доступны только для чтения.";

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Error = "Чтение библиотеки отменено."; DisplayItems.Clear(); SelectedDisplayPack = null; }
        catch (Exception error) when (error is not OutOfMemoryException) { Error = "Не удалось прочитать метаданные пакетов. Проверьте хранилище в диагностике."; DisplayItems.Clear(); SelectedDisplayPack = null; }
        finally { IsLoading = false; NotifyState(); }
    }
    partial void OnSearchChanged(string value) => Filter();
    partial void OnThemeChanged(string value) => Filter();
    partial void OnSelectedPackChanged(SeedPackRow? value)
    {
        OnPropertyChanged(nameof(Summary));
        if (value is not null) { SelectedDisplayPack = DisplayItems.FirstOrDefault(p => p.PackId == value.Manifest.Id && p.RawVersion == value.Version); }
    }
    partial void OnSelectedDisplayPackChanged(SeedPackPresentation? value)
    {
        if (value is not null && !value.IsExample) { SelectedPack = Items.FirstOrDefault(p => p.Manifest.Id == value.PackId && p.Version == value.RawVersion); }
    }
    private void Filter()
    {
        SeedPackRow? selected = SelectedPack;
        Items.Clear();
        foreach (var item in all.Where(p => (Theme == "Все темы" || p.Theme == Theme) && (Search.Length == 0 || p.Title.Contains(Search, StringComparison.OrdinalIgnoreCase) || p.Manifest.Purpose.Contains(Search, StringComparison.OrdinalIgnoreCase)))) { Items.Add(item); }
        SelectedPack = Items.FirstOrDefault(p => p.Manifest.Id == selected?.Manifest.Id && p.Version == selected.Version);
        var previous = SelectedDisplayPack;
        DisplayItems.Clear();
        IEnumerable<SeedPackPresentation> display = ShowingExamples ? SeedPackPresentation.Examples : Items.Select(p => new SeedPackPresentation(p.Title, p.Theme, "—", p.Records.ToString(System.Globalization.CultureInfo.CurrentCulture), "v" + p.Version, "Загружен", "Database", false, $"{p.Bytes / 1048576d:N2} MB", null, null, null, null, null) { PackId = p.Manifest.Id, RawVersion = p.Version });
        foreach (var item in display.Where(p => (DisplayFilter == "Все" || p.Theme == DisplayFilter || DisplayFilter == "Большие" && p.Size is "Large" or "XL" || DisplayFilter == "Избранные" && p == SeedPackPresentation.Examples[0]) && (!ShowingExamples || p.Title.Contains(Search, StringComparison.OrdinalIgnoreCase)))) { DisplayItems.Add(item); }
        SelectedDisplayPack = DisplayItems.FirstOrDefault(p => previous is not null && (p.IsExample ? p.Title == previous.Title : p.PackId == previous.PackId && p.RawVersion == previous.RawVersion)) ?? DisplayItems.FirstOrDefault();
        NotifyState();
    }
    private void NotifyState() { OnPropertyChanged(nameof(Count)); OnPropertyChanged(nameof(Size)); OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(NoResults)); OnPropertyChanged(nameof(ShowingExamples)); OnPropertyChanged(nameof(DisplayCount)); OnPropertyChanged(nameof(InstalledCount)); OnPropertyChanged(nameof(DisplaySize)); OnPropertyChanged(nameof(EntityCount)); OnPropertyChanged(nameof(ExampleNotice)); }
}
