using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.App.Features.SeedPacks;

public sealed partial class SeedPackPreviewViewModel(ISeedPreviewReader? reader = null) : ObservableObject
{
    private SeedPackReference? pack;
    private long selection;
    private SeedPreviewCursor? next;
    private CancellationTokenSource? pending;
    public ObservableCollection<SeedSection> Sections { get; } = [];
    [ObservableProperty] private SeedSection? selectedSection;
    [ObservableProperty] private string text = "";
    [ObservableProperty] private string status = "Выберите пакет и секцию. Preview ограничен 100 записями / 256 КиБ на страницу.";
    [ObservableProperty] private bool isLoading;
    private bool CanRead() => !IsLoading && pack is not null && SelectedSection is not null && reader is not null;
    private bool CanNext() => CanRead() && next is not null;
    public void Select(SeedPack? manifest)
    {
        Cancel(); selection++; pack = manifest is null ? null : new(manifest.Id, manifest.Version, manifest.ContentHash);
        Text = ""; next = null; Sections.Clear();
        if (manifest is not null) { foreach (var section in manifest.Sections) { Sections.Add(section); } }
        SelectedSection = Sections.FirstOrDefault();
        Status = manifest is null ? "Выберите пакет." : Sections.Count == 0 ? "В пакете нет секций для текстового просмотра." : "Выберите «Первая страница». Не более 100 записей / 256 КиБ на страницу.";
        NotifyCommands();
    }
    partial void OnSelectedSectionChanged(SeedSection? value)
    { Cancel(); selection++; next = null; Text = ""; Status = value is null ? "Секция не выбрана." : $"{value.Title} · {value.RecordCount:N0} записей · {value.Bytes:N0} bytes. Откройте первую страницу."; NotifyCommands(); }
    partial void OnIsLoadingChanged(bool value) => NotifyCommands();
    private void NotifyCommands() { FirstCommand.NotifyCanExecuteChanged(); NextCommand.NotifyCanExecuteChanged(); }
    [RelayCommand(CanExecute = nameof(CanRead))] private Task FirstAsync(CancellationToken token) => ReadAsync(null, token);
    [RelayCommand(CanExecute = nameof(CanNext))] private Task NextAsync(CancellationToken token) => ReadAsync(next, token);
    [RelayCommand] private void Cancel() => pending?.Cancel();
    private async Task ReadAsync(SeedPreviewCursor? cursor, CancellationToken token)
    {
        if (!CanRead()) { return; }
        long version = selection;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        pending = cancellation; IsLoading = true;
        try
        {
            SeedPreviewPage page = await reader!.ReadAsync(pack!, SelectedSection!.Id, cursor, cancellation.Token);
            if (version != selection || cancellation.IsCancellationRequested) { return; }
            Text = page.Text; next = page.Next;
            Status = $"Записи {page.StartRecord + (page.Records.Length == 0 ? 0 : 1):N0}–{page.StartRecord + page.Records.Length:N0} из {page.TotalRecords:N0}. " +
                (page.IsLimited ? "Показана ограниченная порция; доступно продолжение →" : "Конец секции.");
        }
        catch (OperationCanceledException) { if (version == selection) { Status = "Просмотр отменён."; } }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (version == selection) { Text = ""; next = null; Status = "Не удалось прочитать проверенное содержимое. Повторите импорт пакета."; } }
        finally { if (ReferenceEquals(pending, cancellation)) { pending = null; } IsLoading = false; }
    }
}
