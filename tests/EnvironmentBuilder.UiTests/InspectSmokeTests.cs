using System.Diagnostics;
using System.IO;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Infrastructure.Imports;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.Infrastructure.Security;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace EnvironmentBuilder.UiTests;

[Trait("Category", "UI")]
public sealed class InspectSmokeTests
{
    [Fact]
    public async Task Keyboard_inspect_compose_return_theme_switch_and_shutdown_preserve_context()
    {
        Assert.True(Environment.UserInteractive, "Interactive desktop is required; this gate cannot be skipped.");
        string parent = Path.Combine(Path.GetTempPath(), "EnvironmentBuilder.InspectUiTests");
        string root = Path.Combine(parent, "test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Process? launched = null;
        try
        {
            var directory = new AppDataDirectory(root);
            await using (var lease = await SingleWriterLease.AcquireAsync(directory))
            await using (var worker = new SqliteWorker(directory, lease))
            {
                await worker.Ready;
                var reader = new StrictDocumentReader();
                var document = reader.Read<IsolatedEnvironment>(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "AuthoringFixtures/environment.json"), TestContext.Current.CancellationToken));
                await new EnvironmentStore(worker, reader, new SystemClock()).SaveNewAsync(document, TestContext.Current.CancellationToken);
            }
            var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "EnvironmentBuilder.App.exe")) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            info.Environment["ENVIRONMENTBUILDER_DATA_DIR"] = root;
            using var app = FlaUI.Core.Application.Launch(info); launched = Process.GetProcessById(app.ProcessId);
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(15)) ?? throw new InvalidOperationException("Window unavailable.");
            window.Focus();
            Press(Find(window, "InspectEnvironment")); Find(window, "InspectComponents");
            Assert.False(Find(window, "CancelOperation").IsEnabled);
            Assert.False(Find(window, "StopEnvironment").IsEnabled);
            AutomationElement theme = window.FindFirstDescendant(cf => cf.ByName("Переключить светлую и тёмную тему"))!;
            Press(theme); Find(window, "InspectLogs"); Press(theme);
            Press(Find(window, "InspectCompose"));
            Assert.Contains("services", Find(window, "ComposeContent").AsTextBox().Text);
            Press(Find(window, "BackFromPreview")); Find(window, "InspectComponents");
            Press(Find(window, "InspectBack")); Find(window, "EnvironmentRows");
            window.Close();
            Assert.True(Retry.WhileFalse(() => app.HasExited, TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(100)).Success, "Close waits for observation disposal and storage shutdown.");
        }
        finally
        {
            if (launched is not null) { if (!launched.HasExited) { launched.Kill(); launched.WaitForExit(); } launched.Dispose(); }
            string resolved = Path.GetFullPath(root);
            Assert.StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, resolved, StringComparison.OrdinalIgnoreCase);
            LocalPathResolver.RejectReparsePoints(resolved); if (Directory.Exists(resolved)) { Directory.Delete(resolved, true); }
        }
    }
    private static AutomationElement Find(AutomationElement parent, string id) => Retry.WhileNull(() => parent.FindFirstDescendant(cf => cf.ByAutomationId(id)), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100), throwOnTimeout: true).Result!;
    private static void Press(AutomationElement element) { element.Focus(); Keyboard.Type(VirtualKeyShort.SPACE); }
}
