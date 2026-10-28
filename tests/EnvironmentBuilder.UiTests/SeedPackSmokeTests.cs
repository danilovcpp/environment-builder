using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using EnvironmentBuilder.App.Components;
using EnvironmentBuilder.App.Composition;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.App.Features.SeedPacks;
using EnvironmentBuilder.App.Shell;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Imports;
using EnvironmentBuilder.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace EnvironmentBuilder.UiTests;

// Shared STA host, real production importer/store/index/preview and compiled WPF views.
internal static class SeedPackSmokeTests
{
    public static void Verify()
    {
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EnvironmentBuilder.Tests"));
        string root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        var navigation = new NavigationService(); var themes = new ThemeService(); var shell = new ShellViewModel(navigation, themes);
        AppServices services = Result(AppServices.CreateAsync(shell, navigation, themes, root));
        var provider = services.Provider;
        var store = provider.GetRequiredService<SeedPackStore>(); var importer = provider.GetRequiredService<SeedPackImporter>();
        var preview = provider.GetRequiredService<ISeedPreviewReader>();
        var io = new Interactions { Path = CreateArchive(root) };
        var model = new SeedPackLibraryViewModel(store.ListMetadataAsync, importer, store, preview, io);
        shell.Authoring = provider.GetRequiredService<AuthoringSession>(); shell.SeedPacks = model; shell.IsLoading = false;
        Run(model.LoadAsync(CancellationToken.None)); Assert.True(model.IsEmpty);
        Run(shell.NavigateCommand.ExecuteAsync("seedpacks"));
        var window = new MainWindow(shell) { Width = 1280, Height = 720, Left = 40, Top = 40, ShowInTaskbar = false };
        window.Show();
        try
        {
            UiReferenceTests.Layout(window); UiReferenceTests.Capture(window, "seedpacks-production-empty-light");
            Run(model.ImportCommand.ExecuteAsync(null)); Assert.Single(model.Items); Assert.NotNull(model.SelectedPack);
            Run(model.Preview.FirstCommand.ExecuteAsync(null)); Assert.Contains("\"row\":0", model.Preview.Text);
            foreach (var size in new[] { new Size(1280, 720), new Size(1672, 941) })
            {
                window.Width = size.Width; window.Height = size.Height;
                foreach (bool dark in new[] { false, true })
                {
                    themes.Apply(dark); UiReferenceTests.Layout(window);
                    var next = UiReferenceTests.Descendants(window).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "SeedPreviewNext");
                    Assert.True(next.IsEnabled); window.Activate(); next.Focus(); Assert.True(next.IsKeyboardFocused);
                    FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.SPACE);
                    UiReferenceTests.Layout(window); Run(model.Preview.NextCommand.ExecutionTask ?? Task.CompletedTask); UiReferenceTests.Layout(window);
                    Assert.Contains("\"row\":100", model.Preview.Text);
                    Assert.Contains("ограниченная", model.Preview.Status);
                    UiReferenceTests.Capture(window, $"seedpacks-production-{size.Width}-" + (dark ? "dark" : "light"));
                    Run(model.Preview.FirstCommand.ExecuteAsync(null));
                }
            }
            var reader = provider.GetRequiredService<StrictDocumentReader>();
            var environment = reader.Read<IsolatedEnvironment>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Contracts/examples/environment.json")));
            var draft = EnvironmentDraft.Create(); draft.AddComponent(environment.Components[0].DefinitionSnapshot, [environment.Components[0].DefinitionSnapshot]);
            var authoringIo = new AuthoringUiFixture();
            var editor = new EnvironmentEditorViewModel(draft, new CreateEnvironmentService(provider.GetRequiredService<IEnvironmentStore>(), authoringIo, store), authoringIo, authoringIo, reader, authoringIo, authoringIo, store);
            Run(editor.LoadLibraryAsync(CancellationToken.None)); editor.Name = "SeedPack offline UI owner";
            Run(editor.SelectSeedPackCommand.ExecuteAsync(null)); Assert.Equal(model.SelectedPack!.Reference, editor.BuildSnapshot().Baseline.Applications[^1].SeedPack);
            Assert.True(Result(editor.TrySaveAsync(CancellationToken.None)));
            Run(model.RemoveCommand.ExecuteAsync(null)); Assert.Single(model.Owners); Assert.Equal(0, io.Confirmations);
            UiReferenceTests.Layout(window); UiReferenceTests.Capture(window, "seedpacks-production-referenced-light");
            var application = draft.Snapshot.Baseline.Applications[^1];
            var key = new DataApplicationKey(draft.Snapshot.Id, application.ComponentId, Guid.NewGuid(), CanonicalJson.Hash(draft.Snapshot.Baseline), CanonicalJson.Hash(application), CanonicalJson.Hash(application.Initializer), application.Initializer.Steps[0].Id);
            foreach (bool dark in new[] { false, true })
            {
                themes.Apply(dark);
                var decision = new UnknownInitializationViewModel(draft.Snapshot, new(key, StepState.Unknown, 1, DateTimeOffset.UtcNow, null, null, null, "http-transport-unavailable"));
                var dialog = new UnknownInitializationDialog { Owner = window, DataContext = decision, ShowInTaskbar = false };
                dialog.Show(); UiReferenceTests.Layout(dialog);
                Assert.True(((Button)dialog.FindName("CancelButton")).IsKeyboardFocused);
                Assert.False(decision.IndependentlyVerified);
                UiReferenceTests.Capture(dialog, "seedpacks-unknown-" + (dark ? "dark" : "light")); dialog.Close();
            }
        }
        finally
        {
            window.Close(); Run(services.DisposeAsync().AsTask());
            Assert.StartsWith(parent + Path.DirectorySeparatorChar, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(root, recursive: true);
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }
    private static string CreateArchive(string root)
    {
        var reader = new StrictDocumentReader();
        var manifest = reader.Read<SeedPack>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Contracts/examples/seedpack/manifest.json")));
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 230).Select(i => $"{{\"row\":{i},\"text\":\"External text <Button /> is never executed\"}}\n")));
        manifest = manifest with { Title = "External Operations Pack", Sections = [manifest.Sections[0] with { RecordCount = 230, Bytes = data.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(data)) }] };
        manifest = manifest with { ContentHash = CanonicalJson.ManifestHash(JsonSerializer.SerializeToElement(manifest, CanonicalJson.Options)) };
        string path = Path.Combine(root, "external.zip"); using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (Stream output = zip.CreateEntry("manifest.json").Open()) { output.Write(JsonSerializer.SerializeToUtf8Bytes(manifest, CanonicalJson.Options)); }
        using (Stream output = zip.CreateEntry(manifest.Sections[0].Path).Open()) { output.Write(data); }
        foreach (var entry in manifest.Initializers) { zip.CreateEntryFromFile(Path.Combine(AppContext.BaseDirectory, "Contracts/examples/seedpack", entry.Path), entry.Path); }
        return path;
    }
    private static T Result<T>(Task<T> task) { Run(task); return task.GetAwaiter().GetResult(); }
    private static void Run(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            timeout.Tick += (_, _) => frame.Continue = false; timeout.Start();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            try { Dispatcher.PushFrame(frame); Assert.True(task.IsCompleted, "UI operation exceeded 30 seconds."); }
            finally { timeout.Stop(); }
        }
        task.GetAwaiter().GetResult();
    }
    private sealed class Interactions : ISeedPackInteractions
    {
        public string Path { get; init; } = "";
        public int Confirmations { get; private set; }
        public Task<string?> SelectArchiveAsync(CancellationToken token) => Task.FromResult<string?>(Path);
        public Task<bool> ConfirmRemovalAsync(SeedPackRow pack, CancellationToken token) { Confirmations++; return Task.FromResult(true); }
        public Task OpenOwnerAsync(SeedPackOwner owner, CancellationToken token) => Task.CompletedTask;
    }
}
