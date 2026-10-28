using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using EnvironmentBuilder.App.Components;
using EnvironmentBuilder.App.Shell;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.UiTests;

internal static class DeleteSmokeTests
{
    public static void Verify()
    {
        var fixture = new UiReferenceFixture();
        var list = fixture.Shell.Authoring!.List;
        var item = fixture.Store.Items[0]; var environment = item.Document!;
        var plan = new OperationPlan(Guid.NewGuid(), environment.Id, environment.Revision, CanonicalJson.Hash(environment), Guid.NewGuid(),
            CanonicalJson.Hash(environment.Baseline), [], [], ["stop-before-removal", "verify-absence", "close-description"],
            App.Features.Environments.EnvironmentCommands.DeleteExplanation)
        { EnvironmentName = environment.Name, EngineId = "verified-engine", Endpoint = "npipe://local-engine", ObservedAt = DateTimeOffset.UtcNow };
        var operation = new Operation(Guid.NewGuid(), environment.Id, "delete", OperationState.NeedsAttention, DateTimeOffset.UtcNow, 1, plan.ConfigurationHash, plan)
        { DeletedResources = ["container-selected"], Remnants = ["volume-selected"], ResultCode = "volume-in-use" };
        fixture.Store.Items[0] = item with { HasUnresolvedOperation = true, Deletion = new(environment, operation) };
        Assert.True(list.LoadAsync(CancellationToken.None).IsCompletedSuccessfully); list.SelectSaved(environment.Id);
        var window = new MainWindow(fixture.Shell) { Width = 1280, Height = 720, Left = 40, Top = 40, ShowInTaskbar = false };
        window.Show();
        try
        {
            foreach (bool dark in new[] { false, true })
            {
                fixture.Themes.Apply(dark); UiReferenceTests.Layout(window);
                var retry = UiReferenceTests.Descendants(window).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "DeleteSelectedEnvironment");
                Assert.True(retry.IsEnabled); Assert.Equal("Повторить удаление", retry.Content);
                var details = UiReferenceTests.Descendants(window).OfType<TextBlock>().Single(t => AutomationProperties.GetAutomationId(t) == "DeletionRemnants");
                Assert.Contains("volume-selected", details.Text); Assert.Contains(environment.Id.ToString(), details.Text);
                retry.Focus(); Assert.True(retry.IsKeyboardFocused);
                UiReferenceTests.Capture(window, "delete-remnants-" + (dark ? "dark" : "light"));
                var dialog = new DestructivePlanDialog { Owner = window, DataContext = new DestructivePlanViewModel(plan), ShowInTaskbar = false };
                dialog.Show(); UiReferenceTests.Layout(dialog);
                Assert.True(((Button)dialog.FindName("CancelButton")).IsKeyboardFocused);
                UiReferenceTests.Capture(dialog, "delete-confirmation-" + (dark ? "dark" : "light"));
                dialog.Close();
            }
        }
        finally { window.Close(); }
    }
}
