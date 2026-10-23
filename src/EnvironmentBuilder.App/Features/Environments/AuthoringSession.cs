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
    private object current;

    public AuthoringSession(EnvironmentListViewModel list, CreateEnvironmentService create, IComponentLibrary library, IAuthoringInteractions interactions,
        IInitializationDocumentReader documents, ILocalPathMappings mappings, IInputResolver inputs, ISeedPackStore packs, IPreviewInputCapture capture, NavigationService navigation)
    {
        List = list; this.create = create; this.library = library; this.interactions = interactions; this.documents = documents; this.mappings = mappings; this.inputs = inputs; this.packs = packs; this.capture = capture; this.navigation = navigation;
        guard = new DirtyDraftGuard(interactions.DecideDirtyDraftAsync); current = list;
        List.Create = CreateAsync;
        List.Open = row => OpenAsync(row.Item.Document!);
    }
    public EnvironmentListViewModel List { get; }
    public EnvironmentEditorViewModel? Editor { get; private set; }
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
        Guid? savedId = Editor?.Draft.IsPersisted == true ? Editor.Id : null;
        if (savedId.HasValue) { await List.LoadAsync(cancellationToken); List.SelectSaved(savedId.Value); }
        DiscardScreen();
        return true;
    }
    private async Task CreateAsync()
    {
        if (!await CanLeaveAsync(CancellationToken.None)) { return; }
        Editor = null;
        await OpenEditorAsync(EnvironmentDraft.Create());
    }
    public async Task OpenAsync(IsolatedEnvironment environment)
    {
        if (!await CanLeaveAsync(CancellationToken.None)) { return; }
        await OpenEditorAsync(EnvironmentDraft.Open(environment));
    }
    private async Task OpenEditorAsync(EnvironmentDraft draft)
    {
        Editor = new EnvironmentEditorViewModel(draft, create, library, interactions, documents, mappings, inputs, packs);
        Editor.Saved = async saved => { await List.LoadAsync(CancellationToken.None); List.SelectSaved(saved.Id); Editor = null; Current = List; navigation.Navigate(new NavigationAddress("environments")); };
        Editor.Back = ReturnToListAsync;
        Editor.Preview = ShowPreviewAsync;
        Current = Editor;
        navigation.Navigate(new NavigationAddress("editor", draft.IsPersisted ? draft.Snapshot.Id : null, draft.DraftId));
        await Editor.LoadLibraryAsync(CancellationToken.None);
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
        var preview = new ComposePreviewViewModel(capture, interactions)
        {
            Back = () => { Current = editor; navigation.Navigate(new NavigationAddress("editor", editor.IsReadOnly ? editor.Id : null, editor.Draft.DraftId)); return Task.CompletedTask; }
        };
        Current = preview;
        navigation.Navigate(new NavigationAddress("preview", environment.Id, editor.Draft.DraftId));
        await preview.LoadAsync(environment, CancellationToken.None);
    }
}
