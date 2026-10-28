using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.App.Features.Settings;

// The library describes where to obtain a service. Runtime values belong to an environment instance.
public sealed partial class ServiceSourceViewModel : ObservableObject, IDisposable
{
    private readonly ComponentDefinition definition;
    private readonly ILocalPathMappings? mappings;
    private readonly IAuthoringInteractions? interactions;
    private readonly IDockerHubCatalog? catalog;
    private string mappedFolder = "";
    private bool selecting;
    private bool disposed;
    private CancellationTokenSource? searchCancellation;
    private CancellationTokenSource? selectionCancellation;
    private CancellationTokenSource? tagCancellation;
    private int tagPage;

    public ServiceSourceViewModel(ComponentDefinition definition, ILocalPathMappings? mappings, IAuthoringInteractions? interactions, IDockerHubCatalog? catalog)
    {
        this.definition = definition; this.mappings = mappings; this.interactions = interactions; this.catalog = catalog;
        sourceKind = definition.DefaultSource is BuildSource ? "build" : "image";
        imageReference = definition.DefaultSource is ImageSource image ? image.Reference : "";
        selectedTag = SplitImage(imageReference).Tag;
        pathRef = definition.DefaultSource is BuildSource build ? build.PathRef : "local-path://source";
        logoUrl = definition.LogoUrl;
    }

    public bool IsInfrastructure => definition.ComponentType == "infrastructure";
    public bool IsImage => !IsBuild;
    public bool IsBuild => SourceKind == "build";
    public bool CanChooseFolder => mappings is not null && interactions is not null;
    public string[] SourceKinds => IsInfrastructure ? ["image"] : ["image", "build"];
    public ObservableCollection<DockerHubRepository> Repositories { get; } = [];
    public ObservableCollection<string> Tags { get; } = [];
    public Task SearchTask { get; private set; } = Task.CompletedTask;
    public Task SelectionTask { get; private set; } = Task.CompletedTask;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsBuild), nameof(IsImage))] private string sourceKind;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanLoadVersions))] private string imageReference;
    [ObservableProperty] private string localFolder = "";
    [ObservableProperty] private string folderError = "";
    [ObservableProperty] private string pathRef;
    [ObservableProperty] private string? logoUrl;
    [ObservableProperty] private string hubStatus = "Начните вводить название образа для поиска в Docker Hub.";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanLoadVersions))] private bool isSearching;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanLoadVersions))] private bool isLoadingTags;
    [ObservableProperty] private bool hasMoreTags;
    [ObservableProperty] private DockerHubRepository? selectedRepository;
    [ObservableProperty] private string selectedTag = "latest";
    public bool ShowRepositories => SelectedRepository is null && Repositories.Count > 0;
    public bool CanLoadVersions => IsInfrastructure && !IsSearching && !IsLoadingTags && ImageReference.Trim().Length > 0 && !ImageReference.Contains('@');
    partial void OnSourceKindChanged(string value)
    {
        if (IsBuild) { searchCancellation?.Cancel(); selectionCancellation?.Cancel(); LogoUrl = null; }
    }

    partial void OnImageReferenceChanged(string value)
    {
        if (selecting) { return; }
        selectionCancellation?.Cancel();
        tagCancellation?.Cancel();
        selecting = true;
        SelectedRepository = null; Tags.Clear(); HasMoreTags = false; IsLoadingTags = false; LogoUrl = null;
        SelectedTag = SplitImage(value).Tag; selecting = false;
        SearchTask = SearchAsync(value);
    }
    private async Task SearchAsync(string value)
    {
        searchCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(); searchCancellation = cancellation;
        Repositories.Clear(); OnPropertyChanged(nameof(ShowRepositories)); IsSearching = false;
        string query = SplitImage(value).Name;
        if (disposed || !IsInfrastructure || catalog is null || query.Length < 2) { HubStatus = "Начните вводить название образа для поиска в Docker Hub."; searchCancellation = null; return; }
        try
        {
            IsSearching = true; HubStatus = "Поиск в Docker Hub…";
            await Task.Delay(350, cancellation.Token);
            var repositories = await catalog.SearchAsync(query, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            foreach (var repository in repositories) { Repositories.Add(repository); }
            OnPropertyChanged(nameof(ShowRepositories));
            HubStatus = repositories.Count == 0 ? "Ничего не найдено. Можно указать образ вручную." : "Выберите образ, чтобы загрузить его версии и логотип.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!cancellation.IsCancellationRequested) { HubStatus = "Docker Hub недоступен. Можно указать образ и тег вручную."; } }
        finally { if (ReferenceEquals(searchCancellation, cancellation)) { IsSearching = false; searchCancellation = null; } }
    }

    partial void OnSelectedRepositoryChanged(DockerHubRepository? value)
    {
        OnPropertyChanged(nameof(ShowRepositories));
        if (value is not null) { SelectionTask = SelectAsync(value); }
    }
    private async Task SelectAsync(DockerHubRepository repository)
    {
        searchCancellation?.Cancel(); selectionCancellation?.Cancel(); tagCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(); selectionCancellation = cancellation;
        var previous = SplitImage(ImageReference);
        selecting = true;
        Tags.Clear();
        SelectedTag = previous.Name == repository.Name ? previous.Tag : "latest";
        ImageReference = repository.Name + ":" + SelectedTag;
        LogoUrl = repository.LogoUrl ?? (previous.Name == repository.Name ? LogoUrl : null);
        selecting = false;
        HasMoreTags = false; tagPage = 0; HubStatus = "Загрузка версий и логотипа…";
        if (catalog is null) { selectionCancellation = null; return; }
        // These independent requests report their own failures: a missing logo must not hide tags.
        await Task.WhenAll(LoadTagsAsync(repository.Name, 1, cancellation.Token), LoadLogoAsync(repository, cancellation.Token));
        if (ReferenceEquals(selectionCancellation, cancellation)) { selectionCancellation = null; }
    }
    private async Task LoadLogoAsync(DockerHubRepository repository, CancellationToken token)
    {
        try
        {
            var details = await catalog!.GetRepositoryAsync(repository.Name, token);
            token.ThrowIfCancellationRequested(); LogoUrl = details.LogoUrl ?? repository.LogoUrl;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    private async Task LoadTagsAsync(string repository, int page, CancellationToken token)
    {
        try
        {
            IsLoadingTags = true;
            var result = await catalog!.GetTagsAsync(repository, page, token);
            token.ThrowIfCancellationRequested();
            foreach (string tag in result.Tags.Where(tag => !Tags.Contains(tag))) { Tags.Add(tag); }
            tagPage = page; HasMoreTags = result.HasMore;
            HubStatus = Tags.Count == 0 ? "Версии не найдены. Укажите тег вручную." : $"Загружено версий: {Tags.Count}. Можно выбрать или ввести тег.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!token.IsCancellationRequested) { HubStatus = "Не удалось загрузить версии. Укажите тег вручную или повторите загрузку."; HasMoreTags = true; } }
        finally { if (!token.IsCancellationRequested) { IsLoadingTags = false; } }
    }
    partial void OnSelectedTagChanged(string value)
    {
        if (selecting || string.IsNullOrWhiteSpace(ImageReference) || ImageReference.Contains('@')) { return; }
        selecting = true; ImageReference = (SelectedRepository?.Name ?? SplitImage(ImageReference).Name) + ":" + value; selecting = false;
    }
    [RelayCommand]
    private void RefreshVersions()
    {
        if (!CanLoadVersions) { return; }
        string name = SplitImage(ImageReference).Name;
        if (name.StartsWith("docker.io/", StringComparison.Ordinal)) { name = name[10..]; }
        if (SelectedRepository?.Name == name) { SelectionTask = SelectAsync(SelectedRepository); }
        else
        {
            var repository = Repositories.FirstOrDefault(repository => repository.Name == name) ?? new DockerHubRepository(name, "", !name.Contains('/'), LogoUrl);
            if (!Repositories.Contains(repository)) { Repositories.Add(repository); }
            SelectedRepository = repository;
        }
    }
    [RelayCommand]
    private async Task LoadMoreTagsAsync(CancellationToken token)
    {
        if (SelectedRepository is null || IsLoadingTags) { return; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        tagCancellation?.Cancel(); tagCancellation = cancellation;
        await LoadTagsAsync(SelectedRepository.Name, tagPage + 1, cancellation.Token);
        if (ReferenceEquals(tagCancellation, cancellation)) { tagCancellation = null; }
    }
    public void LoadLocalFolder(IReadOnlyList<LocalPathMapping> paths)
    { mappedFolder = paths.FirstOrDefault(path => "local-path://" + path.Alias == PathRef)?.AbsolutePath ?? ""; LocalFolder = mappedFolder; }
    public async Task SaveLocalFolderAsync(CancellationToken token)
    {
        if (!IsBuild || mappings is null || LocalFolder == mappedFolder) { return; }
        string reference = "local-path://source-" + Guid.NewGuid().ToString("N");
        await mappings.MapAsync(reference, LocalFolder, token); PathRef = reference; mappedFolder = LocalFolder;
    }
    [RelayCommand]
    private async Task ChooseSourceFolderAsync(CancellationToken token)
    {
        if (!CanChooseFolder) { return; }
        FolderError = "";
        try { string? folder = await interactions!.SelectFolderAsync(token); if (folder is not null) { LocalFolder = folder; } }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException) { FolderError = "Не удалось выбрать папку исходников."; }
    }
    public ComponentSource Build() => IsBuild
        ? (definition.DefaultSource as BuildSource ?? new BuildSource(PathRef, "Dockerfile", ".", ImmutableDictionary<string, string>.Empty, ImmutableDictionary<string, string>.Empty)) with { PathRef = PathRef }
        : new ImageSource(ImageReference.Trim());

    public void Dispose()
    { disposed = true; searchCancellation?.Cancel(); selectionCancellation?.Cancel(); tagCancellation?.Cancel(); }
    private static (string Name, string Tag) SplitImage(string reference)
    {
        string value = reference.Trim();
        int colon = value.LastIndexOf(':');
        return colon > value.LastIndexOf('/') && !value.Contains('@') ? (value[..colon], value[(colon + 1)..]) : (value, "latest");
    }
}
