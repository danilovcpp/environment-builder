using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.App.Features.Environments;

public sealed partial class ComposePreviewViewModel(IPreviewInputCapture capture, IAuthoringInteractions interactions) : ObservableObject
{
    [ObservableProperty] private string content = "";
    [ObservableProperty] private string details = "";
    [ObservableProperty] private string error = "";
    [ObservableProperty] private bool isLoading;
    public PreviewSnapshot? Snapshot { get; private set; }
    public Func<Task>? Back { get; set; }
    public async Task LoadAsync(IsolatedEnvironment environment, CancellationToken cancellationToken)
    {
        IsLoading = true; Error = ""; Snapshot = null; Content = ""; Details = "";
        try
        {
            ComposeModel model = ComposeCompiler.Compile(environment);
            Content = model.RenderPreview();
            Snapshot = await capture.CaptureAsync(environment, cancellationToken);
            Details = $"Ревизия {environment.Revision} · {environment.Id}\nToken: {Snapshot.Token}\n" +
                string.Join('\n', Snapshot.Inputs.Select(i => i.Reference + " · " + i.State)) +
                "\nBuild и read-only mounts используют placeholders будущего приватного staging. Docker не вызывается.";
        }
        catch (OperationCanceledException) { Error = "Подготовка preview отменена."; }
        catch (Exception failure) when (failure is not OutOfMemoryException) { Error = "Не удалось подготовить preview. Проверьте структуру и доступ к входным файлам."; }
        finally { IsLoading = false; CopyCommand.NotifyCanExecuteChanged(); }
    }
    private bool CanCopy => !IsLoading && Error.Length == 0 && Content.Length != 0;
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task CopyAsync(CancellationToken cancellationToken)
    {
        try { await interactions.CopyPreviewAsync(Content, cancellationToken); }
        catch (Exception failure) when (failure is not OutOfMemoryException) { Error = "Не удалось скопировать preview в буфер обмена."; }
    }
    [RelayCommand] private Task GoBackAsync() => Back?.Invoke() ?? Task.CompletedTask;
}
