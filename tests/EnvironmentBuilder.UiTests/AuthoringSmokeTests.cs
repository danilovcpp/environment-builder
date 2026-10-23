using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Infrastructure.Imports;
using EnvironmentBuilder.Infrastructure.Persistence;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace EnvironmentBuilder.UiTests;

[Trait("Category", "UI")]
public sealed class AuthoringSmokeTests
{
    private readonly List<Process> launched = [];
    [Fact]
    public async Task Keyboard_create_preview_save_reopen_and_dirty_decisions_work_in_a_real_window()
    {
        Assert.True(Environment.UserInteractive, "An interactive Windows desktop is required; this gate cannot be skipped.");
        string parent = Path.Combine(Path.GetTempPath(), "EnvironmentBuilder.AuthoringUiTests");
        string root = Path.Combine(parent, "test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await SeedLibraryAsync(root);
            string exe = Path.Combine(AppContext.BaseDirectory, "EnvironmentBuilder.App.exe");
            Assert.True(File.Exists(exe));
            using var automation = new UIA3Automation();
            using (var app = Launch(exe, root))
            {
                Window window = app.GetMainWindow(automation, TimeSpan.FromSeconds(15)) ?? throw new InvalidOperationException("Interactive main window unavailable.");
                window.Focus();
                Press(Find(window, "CreateEnvironment"));
                TextBox name = Find(window, "EnvironmentName").AsTextBox();
                name.Focus(); Keyboard.Type("Keyboard environment");
                var library = Find(window, "ComponentLibrary").AsComboBox();
                library.Select(0);
                Press(Find(window, "AddComponent"));
                Wait(() => Find(window, "SaveEnvironment").IsEnabled, "save enabled after adding a valid component");
                AutomationElement theme = window.FindFirstDescendant(cf => cf.ByName("Переключить светлую и тёмную тему")) ?? throw new InvalidOperationException("Theme command missing.");
                Press(theme);
                Assert.Equal("Keyboard environment", Find(window, "EnvironmentName").AsTextBox().Text);
                Press(theme);
                Press(Find(window, "ShowCompose"));
                Wait(() => Find(window, "ComposeContent").AsTextBox().Text.Contains("127.0.0.1", StringComparison.Ordinal), "Compose content");
                Press(Find(window, "BackFromPreview"));
                Assert.Equal("Keyboard environment", Find(window, "EnvironmentName").AsTextBox().Text);
                Press(Find(window, "BackToList"));
                Wait(() => window.ModalWindows.Length == 1, "dirty dialog");
                Window dirty = window.ModalWindows.Single();
                dirty.Focus(); Keyboard.Type(VirtualKeyShort.ESCAPE);
                Wait(() => window.ModalWindows.Length == 0, "Escape stays in editor");
                Assert.Equal("Keyboard environment", Find(window, "EnvironmentName").AsTextBox().Text);
                Press(Find(window, "BackToList"));
                Wait(() => window.ModalWindows.Length == 1, "dirty Save dialog");
                Press(Find(window.ModalWindows.Single(), "DirtySave"));
                Find(window, "EnvironmentRows");
                Press(Find(window, "OpenEnvironment"));
                Assert.Equal("Keyboard environment", Find(window, "EnvironmentName").AsTextBox().Text);
                Assert.False(Find(window, "SaveEnvironment").IsEnabled);
                Press(Find(window, "BackToList"));
                Press(Find(window, "CreateEnvironment"));
                Find(window, "EnvironmentName").Focus(); Keyboard.Type("Discard me");
                Press(Find(window, "BackToList"));
                Wait(() => window.ModalWindows.Length == 1, "dirty Discard dialog");
                Press(Find(window.ModalWindows.Single(), "DirtyDiscard"));
                Find(window, "EnvironmentRows");
                window.Close();
                Wait(() => app.HasExited, "Application must close after durable storage shutdown.");
            }
            using (var app = Launch(exe, root))
            {
                Window window = app.GetMainWindow(automation, TimeSpan.FromSeconds(15)) ?? throw new InvalidOperationException("Interactive main window unavailable.");
                Press(Find(window, "OpenEnvironment"));
                Assert.Equal("Keyboard environment", Find(window, "EnvironmentName").AsTextBox().Text);
                Press(Find(window, "ShowCompose"));
                Wait(() => Find(window, "ComposeContent").AsTextBox().Text.Contains("services", StringComparison.Ordinal), "preview after process restart");
                window.Close();
                Wait(() => app.HasExited, "Application shutdown");
            }
            await VerifySavedAsync(root);
        }
        finally
        {
            foreach (Process process in launched)
            {
                if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
                process.Dispose();
            }
            string resolved = Path.GetFullPath(root);
            Assert.StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, resolved, StringComparison.OrdinalIgnoreCase);
            Infrastructure.Security.LocalPathResolver.RejectReparsePoints(resolved);
            if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
        }
    }
    private FlaUI.Core.Application Launch(string exe, string root)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        info.Environment["ENVIRONMENTBUILDER_DATA_DIR"] = root;
        info.Environment["ENVIRONMENTBUILDER_DOCKER_TESTS"] = "0";
        var application = FlaUI.Core.Application.Launch(info);
        launched.Add(Process.GetProcessById(application.ProcessId));
        return application;
    }
    private static AutomationElement Find(AutomationElement parent, string id) => Retry.WhileNull(() => parent.FindFirstDescendant(cf => cf.ByAutomationId(id)),
        timeout: TimeSpan.FromSeconds(10), interval: TimeSpan.FromMilliseconds(100), throwOnTimeout: true).Result!;
    private static void Wait(Func<bool> condition, string purpose) => Assert.True(Retry.WhileFalse(condition, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100)).Success, purpose);
    private static void Press(AutomationElement element) { element.Focus(); Keyboard.Type(VirtualKeyShort.SPACE); }
    private static async Task SeedLibraryAsync(string root)
    {
        var directory = new AppDataDirectory(root);
        await using var lease = await SingleWriterLease.AcquireAsync(directory);
        await using var worker = new SqliteWorker(directory, lease);
        await worker.Ready;
        var reader = new StrictDocumentReader();
        var library = new ComponentLibraryStore(worker, reader);
        using JsonDocument components = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "AuthoringFixtures/components.json"), TestContext.Current.CancellationToken));
        foreach (JsonElement component in components.RootElement.EnumerateArray())
        { await library.SaveAsync(reader.Read<ComponentDefinition>(Encoding.UTF8.GetBytes(component.GetRawText())), TestContext.Current.CancellationToken); }
    }
    private static async Task VerifySavedAsync(string root)
    {
        var directory = new AppDataDirectory(root);
        await using var lease = await SingleWriterLease.AcquireAsync(directory);
        await using var worker = new SqliteWorker(directory, lease);
        await worker.Ready;
        var store = new EnvironmentStore(worker, new StrictDocumentReader(), new SystemClock());
        var row = Assert.Single(await store.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Keyboard environment", row.Name);
        Assert.Equal(1, row.Revision);
        Assert.Equal(2, row.Document!.Components.Length);
        long operations = await worker.ReadAsync(connection =>
        { using var command = connection.CreateCommand(); command.CommandText = "SELECT count(*) FROM Operations"; return (long)command.ExecuteScalar()!; }, TestContext.Current.CancellationToken);
        Assert.Equal(0, operations);
    }
}
