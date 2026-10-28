using CommunityToolkit.Mvvm.ComponentModel;
using EnvironmentBuilder.App.Shell;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.App.Features.Environments;

// App-owned authoring screens retain list/draft state across nested routes and theme changes.
public sealed class AuthoringSession : ObservableObject
{
    private readonly CreateEnvironmentService create;
    private readonly IComponentLibrary library;
    private readonly IAuthoringInteractions interactions;
    private readonly IInitializationDocumentReader documents;
    private readonly ILocalPathMappings mappings;
    private readonly IInputResolver inputs;
    private readonly ISeedPackStore packs;
    private readonly NavigationService navigation;
    private readonly IPreviewInputCapture capture;
    private readonly DirtyDraftGuard guard;
    private readonly InspectViewModel? inspect;
    private readonly PreviewReviewStore? reviews;
    private readonly EnvironmentCommands? commands;
    private readonly IDestructiveInteractions? destructive;
    private object current;

    public AuthoringSession(EnvironmentListViewModel list, CreateEnvironmentService create, IComponentLibrary library, IAuthoringInteractions interactions,
        IInitializationDocumentReader documents, ILocalPathMappings mappings, IInputResolver inputs, ISeedPackStore packs, IPreviewInputCapture capture, NavigationService navigation, InspectViewModel? inspect = null, PreviewReviewStore? reviews = null,
        EnvironmentCommands? commands = null, IDestructiveInteractions? destructive = null)
    {
        List = list; this.create = create; this.library = library; this.interactions = interactions; this.documents = documents; this.mappings = mappings; this.inputs = inputs; this.packs = packs; this.capture = capture; this.navigation = navigation;
        guard = new DirtyDraftGuard(interactions.DecideDirtyDraftAsync); current = list;
        this.inspect = inspect;
        this.reviews = reviews;
        this.commands = commands; this.destructive = destructive;
        List.Create = CreateAsync;
        List.Open = row => OpenAsync(row.Item.Document!);
        List.Inspect = row => OpenInspectAsync(row.Item.Document!);
        List.Start = async row => { await OpenInspectAsync(row.Item.Document!); if (Current == inspect && inspect?.StartCommand.CanExecute(null) == true) { await inspect.StartCommand.ExecuteAsync(null); } };
        List.Compose = row => ShowSavedPreviewAsync(row.Item.Document!);
        List.Observe = async row => { await OpenInspectAsync(row.Item.Document!); if (Current == inspect && inspect is not null) { await inspect.RefreshCommand.ExecuteAsync(null); } };
        List.ClearData = row => OpenResetAsync(row.Item.Document!, false);
        List.RestoreBaseline = row => OpenResetAsync(row.Item.Document!, true);
        List.Delete = (row, token) =>
        {
            if (commands is null) { throw new InvalidOperationException("Удаление недоступно."); }
            return commands.DeleteAsync(row.Item.Document!, token);
        };
        List.CancelDelete = id => commands?.CancelAsync(id, CancellationToken.None) ?? Task.CompletedTask;
    }
    public EnvironmentListViewModel List { get; }
    public EnvironmentEditorViewModel? Editor { get; private set; }
    public Core.SeedPacks.SeedPackReference? PreferredSeedPack { get; set; }
    public object Current { get => current; private set => SetProperty(ref current, value); }

    public async Task<bool> CanLeaveAsync(CancellationToken cancellationToken)
    {
        if (Editor is null) { return true; }
        if (Editor.IsBusy) { return false; }
        return await guard.CanLeaveAsync(Editor.IsDirty, Editor.TrySaveAsync, cancellationToken);
    }
    public void DiscardScreen() { Editor = null; Current = List; }
    public async Task<bool> LeaveAsync(CancellationToken cancellationToken)
    {
        if (!await CanLeaveAsync(cancellationToken)) { return false; }
        if (inspect is not null) { await inspect.CloseSessionAsync(); }
        Guid? savedId = Editor?.Draft.IsPersisted == true ? Editor.Id : null;
        if (savedId.HasValue) { await List.LoadAsync(cancellationToken); List.SelectSaved(savedId.Value); }
        DiscardScreen();
        return true;
    }
    private async Task CreateAsync()
    {
        if (!await CanLeaveAsync(CancellationToken.None)) { return; }
        if (inspect is not null) { await inspect.CloseSessionAsync(); }
        Editor = null;
        await OpenEditorAsync(EnvironmentDraft.Create());
    }
    public async Task OpenAsync(IsolatedEnvironment environment)
    {
        if (!await CanLeaveAsync(CancellationToken.None)) { return; }
        if (inspect is not null) { await inspect.CloseSessionAsync(); }
        await OpenEditorAsync(EnvironmentDraft.Open(environment));
    }
    public async Task OpenInspectAsync(IsolatedEnvironment environment)
    {
        if (inspect is null || !await CanLeaveAsync(CancellationToken.None)) { return; }
        Editor = null;
        inspect.Back = async () => { await inspect.CloseSessionAsync(); Current = List; navigation.Navigate(new("environments")); };
        inspect.Preview = async document =>
        {
            await inspect.CloseSessionAsync();
            var preview = new ComposePreviewViewModel(capture, interactions, reviews) { Back = () => OpenInspectAsync(document) };
            Current = preview; navigation.Navigate(new("preview", document.Id));
            await preview.LoadAsync(document, CancellationToken.None);
        };
        Current = inspect; navigation.Navigate(new("inspect", environment.Id));
        await inspect.OpenAsync(environment, CancellationToken.None);
    }
    private async Task OpenResetAsync(IsolatedEnvironment environment, bool restore)
    {
        await OpenInspectAsync(environment);
        if (Current != inspect || inspect is null) { return; }
        await inspect.RefreshCommand.ExecuteAsync(null);
        var command = restore ? inspect.RestoreBaselineCommand : inspect.ClearDataCommand;
        if (command.CanExecute(null)) { await command.ExecuteAsync(null); }
    }
    private async Task ShowSavedPreviewAsync(IsolatedEnvironment environment)
    {
        if (!await CanLeaveAsync(CancellationToken.None)) { return; }
        if (inspect is not null) { await inspect.CloseSessionAsync(); }
        Editor = null;
        var preview = new ComposePreviewViewModel(capture, interactions, reviews)
        { Back = () => { Current = List; navigation.Navigate(new("environments")); return Task.CompletedTask; } };
        Current = preview; navigation.Navigate(new("preview", environment.Id));
        await preview.LoadAsync(environment, CancellationToken.None);
    }
    private async Task OpenEditorAsync(EnvironmentDraft draft)
    {
        Editor = new EnvironmentEditorViewModel(draft, create, library, interactions, documents, mappings, inputs, packs, commands, destructive);
        Editor.Saved = async saved => { await List.LoadAsync(CancellationToken.None); List.SelectSaved(saved.Id); Editor = null; Current = List; navigation.Navigate(new NavigationAddress("environments")); };
        Editor.Back = ReturnToListAsync;
        Editor.Preview = ShowPreviewAsync;
        Editor.Inspect = OpenInspectAsync;
        Current = Editor;
        navigation.Navigate(new NavigationAddress("editor", draft.IsPersisted ? draft.Snapshot.Id : null, draft.DraftId));
        await Editor.LoadLibraryAsync(CancellationToken.None);
        if (PreferredSeedPack is { } preferred) { Editor.SelectedPackChoice = Editor.PackChoices.FirstOrDefault(p => p.Pack == preferred); }
    }
    private async Task ReturnToListAsync()
    {
        if (!await CanLeaveAsync(CancellationToken.None)) { return; }
        Guid? savedId = Editor?.Draft.IsPersisted == true ? Editor.Id : null;
        Editor = null;
        await List.LoadAsync(CancellationToken.None);
        if (savedId.HasValue) { List.SelectSaved(savedId.Value); }
        Current = List; navigation.Navigate(new NavigationAddress("environments"));
    }
    private async Task ShowPreviewAsync(IsolatedEnvironment environment)
    {
        if (Editor is null) { return; }
        EnvironmentEditorViewModel editor = Editor;
        var preview = new ComposePreviewViewModel(capture, interactions, reviews)
        {
            Back = () => { Current = editor; navigation.Navigate(new NavigationAddress("editor", editor.IsReadOnly ? editor.Id : null, editor.Draft.DraftId)); return Task.CompletedTask; }
        };
        Current = preview;
        navigation.Navigate(new NavigationAddress("preview", environment.Id, editor.Draft.DraftId));
        await preview.LoadAsync(environment, CancellationToken.None);
    }
}
