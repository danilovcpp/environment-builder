using System.Windows;
using System.Windows.Input;
using EnvironmentBuilder.App.Components;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.App.Shell;

public sealed class UnknownInitializationInteractions : IUnknownInitializationInteractions
{
    public async Task<UnknownInitializationViewModel?> DecideAsync(IsolatedEnvironment environment, DataApplicationRecord record, CancellationToken token) =>
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            IInputElement? focus = Keyboard.FocusedElement;
            var model = new UnknownInitializationViewModel(environment, record);
            var dialog = new UnknownInitializationDialog { Owner = Application.Current.MainWindow, DataContext = model };
            using var registration = token.Register(() => dialog.Dispatcher.BeginInvoke(new Action(dialog.Close)));
            try { return dialog.ShowDialog() == true && !token.IsCancellationRequested ? model : null; }
            finally { if (focus is not null) { Keyboard.Focus(focus); } }
        });
}
