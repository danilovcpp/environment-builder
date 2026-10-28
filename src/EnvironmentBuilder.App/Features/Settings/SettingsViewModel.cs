using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvironmentBuilder.App.Shell;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.App.Features.Settings;

public sealed record DefinitionRow(ComponentDefinition Definition)
{
    public string Name => Definition.Name;
    public string Type => Definition.ComponentType == "target" ? "Приложение" : "Инфраструктура";
    public string Version => Definition.Version;
    public string Source => Definition.DefaultSource is ImageSource image ? image.Reference : "Локальная сборка";
    public string Details => $"{Name}\n{Type} · v{Version}\n\nИсточник: {Source}\nContract: {Definition.Contract.Id} · {Definition.Contract.Version}\n\nПараметров: {Definition.ParameterDefinitions.Length}\nПортов: {Definition.DefaultPorts.Length}\nХранилищ: {Definition.DefaultStorage.Length}\n\nID: {Definition.Id}\n\nИзменения defaults не пересчитывают сохранённые окружения.";
}

public sealed partial class SettingsViewModel(ThemeService themes, IComponentLibrary? library = null, ILocalPathMappings? mappings = null) : ObservableObject
{
    private IReadOnlyList<DefinitionRow> all = [];
    public ObservableCollection<DefinitionRow> Definitions { get; } = [];
    public ObservableCollection<LocalPathMapping> Paths { get; } = [];
    [ObservableProperty] private DefinitionRow? selectedDefinition;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string dataDirectory = "Хранилище ещё не открыто";
    [ObservableProperty] private string status = "Библиотека ещё не прочитана";
    public string Summary => SelectedDefinition?.Details ?? "Выберите описание компонента.\n\nСохранённые окружения используют собственные snapshots. Обновление библиотеки не меняет их автоматически.";
    public string WorkflowReason => "Изменение значений по умолчанию и управление версиями библиотеки пока недоступны в этой версии. Доступны просмотр и смена темы.";
    [RelayCommand] private void ToggleTheme() => themes.Apply(!themes.IsDark);
    partial void OnSelectedDefinitionChanged(DefinitionRow? value) => OnPropertyChanged(nameof(Summary));
    partial void OnSearchChanged(string value) => Filter();
    private void Filter()
    {
        var selected = SelectedDefinition;
        Definitions.Clear(); foreach (var row in all.Where(d => d.Name.Contains(Search, StringComparison.OrdinalIgnoreCase))) { Definitions.Add(row); }
        SelectedDefinition = Definitions.FirstOrDefault(d => d.Definition.Id == selected?.Definition.Id && d.Version == selected.Version);
    }
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (library is null) { return; }
        Status = "Чтение библиотеки…";
        try
        {
            all = (await library.ReadAsync(cancellationToken)).Select(d => new DefinitionRow(d)).ToArray(); Filter();
            Paths.Clear(); if (mappings is not null) { foreach (var path in await mappings.ReadAsync(cancellationToken)) { Paths.Add(path); } }
            Status = all.Count == 0 ? "Библиотека пуста. Описание можно импортировать из редактора окружения." : $"Сохранённых описаний: {all.Count}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Status = "Чтение отменено."; }
        catch (Exception error) when (error is not OutOfMemoryException) { Status = "Не удалось прочитать библиотеку или локальные пути. Проверьте хранилище."; }
    }
}
