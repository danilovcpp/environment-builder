using System.IO;
using EnvironmentBuilder.App.Features.SeedPacks;
using EnvironmentBuilder.App.Features.Settings;
using EnvironmentBuilder.App.Shell;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Imports;

namespace EnvironmentBuilder.UnitTests.ViewModels;

public sealed class PresentationCatalogTests
{
    [Fact]
    public async Task Metadata_filters_keep_exact_versions_and_never_imply_payload_readiness()
    {
        var pack = new StrictDocumentReader().Read<SeedPack>(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Contracts/examples/seedpack/manifest.json"), TestContext.Current.CancellationToken));
        var model = new SeedPackLibraryViewModel(_ => Task.FromResult<IReadOnlyList<SeedPack>>([pack, pack with { Version = "2.0.0", Title = "Other package", Theme = "Other" }]));
        await model.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("2", model.Count);
        model.SelectedPack = model.Items[1];
        model.Search = "Other";
        Assert.Equal("2.0.0", Assert.Single(model.Items).Version);
        Assert.Equal("2.0.0", model.SelectedPack!.Version);
        Assert.Contains("SHA-256", model.Summary);
        model.Theme = pack.Theme;
        Assert.True(model.NoResults); Assert.Null(model.SelectedPack);
        model.Search = "";
        Assert.Equal(pack.Version, Assert.Single(model.Items).Version);
        Assert.Contains("недоступны", model.WorkflowReason);
    }

    [Fact]
    public async Task Unknown_loading_empty_failure_and_cancel_remain_distinct_and_safe()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<SeedPack>>();
        var model = new SeedPackLibraryViewModel(_ => pending.Task);
        Assert.Equal("—", model.Count); Assert.False(model.IsEmpty);
        Task loading = model.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(model.IsLoading); Assert.False(model.IsEmpty);
        pending.SetResult([]); await loading;
        Assert.True(model.IsEmpty); Assert.Equal("0", model.Count);
        var failing = new SeedPackLibraryViewModel(_ => throw new IOException("sensitive payload"));
        await failing.LoadAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(failing.Error); Assert.DoesNotContain("sensitive", failing.Error); Assert.False(failing.IsEmpty);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var canceled = new SeedPackLibraryViewModel(token => Task.FromCanceled<IReadOnlyList<SeedPack>>(token));
        await canceled.LoadAsync(cancellation.Token);
        Assert.Contains("отменено", canceled.Error); Assert.False(canceled.IsLoading);
    }

    [Fact]
    public async Task Settings_display_saved_definitions_without_changing_snapshots_or_claiming_future_workflows()
    {
        var io = new AuthoringFakes();
        var model = new SettingsViewModel(new ThemeService(), io, io);
        await model.LoadAsync(TestContext.Current.CancellationToken);
        model.SelectedDefinition = model.Definitions[0];
        Assert.Contains(model.SelectedDefinition.Definition.Id.ToString(), model.Summary);
        model.Search = "impossible name";
        Assert.Empty(model.Definitions); Assert.Null(model.SelectedDefinition);
        Assert.Contains("недоступны", model.WorkflowReason);
        Assert.Empty(io.Items);
    }
}
