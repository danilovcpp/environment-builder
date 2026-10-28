using System.Windows;
using EnvironmentBuilder.App.Components;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.App.Shell;

public sealed class DestructiveInteractions : IDestructiveInteractions
{
    public async Task<ConfirmationRecord?> ConfirmAsync(OperationPlan plan, CancellationToken token) => await Application.Current.Dispatcher.InvokeAsync(() =>
    {
        token.ThrowIfCancellationRequested();
        var dialog = new DestructivePlanDialog { Owner = Application.Current.MainWindow, DataContext = new DestructivePlanViewModel(plan) };
        using var registration = token.Register(() => dialog.Dispatcher.BeginInvoke(new Action(dialog.Close)));
        return dialog.ShowDialog() == true && !token.IsCancellationRequested ? new ConfirmationRecord(plan.Hash, DateTimeOffset.UtcNow) : null;
    });
}
