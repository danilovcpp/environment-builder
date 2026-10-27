using System.Windows;
using EnvironmentBuilder.App.Components;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.App.Shell;

public sealed class RuntimeInteractions : IRuntimeInteractions
{
    public async Task<bool> ConfirmInputsAsync(IReadOnlyList<InputDelta> delta, OperationPlan plan, CancellationToken token) => await Application.Current.Dispatcher.InvokeAsync(() =>
    {
        token.ThrowIfCancellationRequested();
        var dialog = new InputDeltaDialog
        {
            Owner = Application.Current.MainWindow,
            DataContext = $"Окружение {plan.EnvironmentId} · ревизия {plan.Revision}\nEngine {plan.EngineId}\nEndpoint {plan.Endpoint}\n\n" +
                string.Join("\n\n", delta.Select(d => d.Reference + "\nБыло: " + d.Before + "\nТеперь: " + d.After)) + "\n\n" + plan.Consequences +
                "\n\n" + string.Join("\n", plan.Inputs.Select(i => i.Reference + ": " + (i.Hash ?? i.RuntimeIdentity ?? "unknown"))) +
                (plan.Candidate is null ? "" : "\n\nCompose:\n" + ComposeCompiler.Compile(plan.Candidate).RenderPreview())
        };
        using var registration = token.Register(() => dialog.Dispatcher.BeginInvoke(new Action(dialog.Close)));
        return dialog.ShowDialog() == true && !token.IsCancellationRequested;
    });
    public async Task<CloseOperationsDecision> DecideCloseAsync(int activeOperations, CancellationToken token) => await Application.Current.Dispatcher.InvokeAsync(() =>
    {
        token.ThrowIfCancellationRequested();
        var dialog = new CloseOperationsDialog { Owner = Application.Current.MainWindow, DataContext = $"Активных операций: {activeOperations}. Выберите способ завершения." };
        dialog.ShowDialog(); return dialog.Decision;
    });
}
