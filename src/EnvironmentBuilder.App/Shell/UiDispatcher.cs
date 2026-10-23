using System.Windows;

namespace EnvironmentBuilder.App.Shell;

public sealed class UiDispatcher
{
    public async Task ApplyAsync(Action batch, CancellationToken cancellationToken)
    {
        await Application.Current.Dispatcher.InvokeAsync(batch, System.Windows.Threading.DispatcherPriority.DataBind, cancellationToken);
    }
}
