using System.Collections.Immutable;
using System.IO;
using EnvironmentBuilder.App.Features.SeedPacks;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Imports;

namespace EnvironmentBuilder.UnitTests.ViewModels;

public sealed class SeedPackLibraryTests
{
    [Fact]
    public async Task Empty_library_has_no_fictional_items_and_sections_come_from_selected_manifest()
    {
        var empty = new SeedPackLibraryViewModel(_ => Task.FromResult<IReadOnlyList<SeedPack>>([]));
        await empty.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Empty(empty.DisplayItems); Assert.True(empty.IsEmpty);
        var pack = new StrictDocumentReader().Read<SeedPack>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Contracts/examples/seedpack/manifest.json")));
        var model = new SeedPackLibraryViewModel(_ => Task.FromResult<IReadOnlyList<SeedPack>>([pack]));
        await model.LoadAsync(TestContext.Current.CancellationToken);
        model.SelectedPack = model.Items[0];
        Assert.Equal("requests", Assert.Single(model.Preview.Sections).Id);
        Assert.False(model.CanRemove); Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task Import_progress_failure_cancel_and_owner_navigation_keep_committed_items_only()
    {
        var io = new LibraryIo();
        var model = new SeedPackLibraryViewModel(io.ListMetadataAsync, io, io, io, io);
        var import = model.ImportCommand.ExecuteAsync(null);
        Assert.True(model.IsBusy); Assert.Contains("Проверка", model.Progress); Assert.Empty(model.Items);
        io.Import.SetException(new IOException("raw sentinel-secret payload")); await import;
        Assert.False(model.IsBusy); Assert.Empty(model.Items); Assert.DoesNotContain("sentinel", model.Error);
        io.Import = new();
        import = model.ImportCommand.ExecuteAsync(null);
        io.Import.SetCanceled(TestContext.Current.CancellationToken); await import; Assert.Contains("отменён", model.Progress); Assert.Empty(model.Items);
        io.Import = new();
        import = model.ImportCommand.ExecuteAsync(null);
        io.Packs.Add(io.Pack); io.Import.SetResult(new(io.Pack.Id, io.Pack.Version, io.Pack.ContentHash)); await import;
        Assert.Single(model.Items); Assert.Equal(io.Pack.Id, model.SelectedPack!.Reference.Id);
        io.Owners.Add(new("environment", Guid.NewGuid(), "Owner"));
        await model.RemoveCommand.ExecuteAsync(null);
        Assert.Single(model.Owners); Assert.Equal(0, io.Removals);
        await model.OpenOwnerCommand.ExecuteAsync(model.Owners[0]); Assert.Equal(model.Owners[0], io.Opened);
        io.Owners.Clear(); io.Confirm = false; await model.RemoveCommand.ExecuteAsync(null); Assert.Equal(0, io.Removals);
        io.Confirm = true; await model.RemoveCommand.ExecuteAsync(null); Assert.Equal(1, io.Removals); Assert.Empty(model.Items);
    }

    [Fact]
    public async Task Preview_ignores_late_page_after_selection_changes_and_cancel_is_observable()
    {
        var io = new LibraryIo(); var model = new SeedPackPreviewViewModel(io);
        model.Select(io.Pack); var read = model.FirstCommand.ExecuteAsync(null); Assert.True(model.IsLoading);
        model.Select(io.Pack with { Version = "2.0.0", Sections = [] });
        io.Page.SetResult(new("requests", 0, 1000, ["{\"old\":true}"], new(new(io.Pack.Id, io.Pack.Version, io.Pack.ContentHash), "requests", 1)));
        await read; Assert.Empty(model.Text); Assert.Empty(model.Sections); Assert.Contains("нет секций", model.Status);
        io.Page = new(); model.Select(io.Pack); read = model.FirstCommand.ExecuteAsync(null);
        model.CancelCommand.Execute(null); io.Page.SetCanceled(TestContext.Current.CancellationToken); await read;
        Assert.Contains("отменён", model.Status); Assert.False(model.IsLoading);
    }

    private sealed class LibraryIo : ISeedPackImporter, ISeedPackCatalog, ISeedPreviewReader, ISeedPackInteractions
    {
        public SeedPack Pack { get; } = new StrictDocumentReader().Read<SeedPack>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Contracts/examples/seedpack/manifest.json")));
        public List<SeedPack> Packs { get; } = [];
        public List<SeedPackOwner> Owners { get; } = [];
        public TaskCompletionSource<SeedPackReference> Import { get; set; } = new();
        public TaskCompletionSource<SeedPreviewPage> Page { get; set; } = new();
        public SeedPackOwner? Opened { get; private set; }
        public bool Confirm { get; set; } = true;
        public int Removals { get; private set; }
        public Task<SeedPackReference> ImportAsync(string path, CancellationToken token) => Import.Task;
        public Task<IReadOnlyList<SeedPack>> ListMetadataAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<SeedPack>>(Packs.ToArray());
        public Task<IReadOnlyList<SeedPackOwner>> ReadOwnersAsync(SeedPackReference reference, CancellationToken token) => Task.FromResult<IReadOnlyList<SeedPackOwner>>(Owners.ToArray());
        public Task RemoveAsync(SeedPackReference reference, CancellationToken token) { Removals++; Packs.RemoveAll(p => p.Id == reference.Id && p.Version == reference.Version); return Task.CompletedTask; }
        public Task<SeedPreviewPage> ReadAsync(SeedPackReference pack, string section, SeedPreviewCursor? cursor, CancellationToken token) => Page.Task;
        public Task<string?> SelectArchiveAsync(CancellationToken token) => Task.FromResult<string?>("external.zip");
        public Task<bool> ConfirmRemovalAsync(SeedPackRow pack, CancellationToken token) => Task.FromResult(Confirm);
        public Task OpenOwnerAsync(SeedPackOwner owner, CancellationToken token) { Opened = owner; return Task.CompletedTask; }
    }
}
