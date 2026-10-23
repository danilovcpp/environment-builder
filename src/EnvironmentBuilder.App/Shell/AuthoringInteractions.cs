using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using EnvironmentBuilder.App.Components;
using EnvironmentBuilder.App.Features.Environments;
using Microsoft.Win32;

namespace EnvironmentBuilder.App.Shell;

public sealed class AuthoringInteractions : IAuthoringInteractions
{
    public async Task<byte[]?> SelectComponentDocumentAsync(CancellationToken cancellationToken)
    {
        string? path = await SelectFileAsync("Описание компонента", cancellationToken);
        return path is null ? null : await ReadBoundedAsync(path, cancellationToken);
    }
    public async Task<string?> SelectInitializerDocumentAsync(CancellationToken cancellationToken)
    {
        string? path = await SelectFileAsync("Описание инициализации", cancellationToken);
        return path is null ? null : new UTF8Encoding(false, true).GetString(await ReadBoundedAsync(path, cancellationToken));
    }
    private static async Task<string?> SelectFileAsync(string title, CancellationToken cancellationToken) => await Application.Current.Dispatcher.InvokeAsync(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new OpenFileDialog { Title = title, Filter = "JSON documents|*.json", CheckFileExists = true };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    });
    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (input.Length > 1024 * 1024) { throw new IOException("Document exceeds 1 MiB."); }
        using var output = new MemoryStream();
        byte[] buffer = new byte[65536]; int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        { if (output.Length + count > 1024 * 1024) { throw new IOException("Document exceeds 1 MiB."); } output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    public async Task<string?> SelectFolderAsync(CancellationToken cancellationToken) => await Application.Current.Dispatcher.InvokeAsync(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new OpenFolderDialog { Title = "Папка исходников или входных файлов" };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
    });
    public async Task<DirtyDraftDecision> DecideDirtyDraftAsync(CancellationToken cancellationToken) => await Application.Current.Dispatcher.InvokeAsync(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        IInputElement? focus = Keyboard.FocusedElement;
        var dialog = new DirtyDraftDialog { Owner = Application.Current.MainWindow };
        try { dialog.ShowDialog(); return dialog.Decision; }
        finally { if (focus is not null) { Keyboard.Focus(focus); } }
    });
    public async Task CopyPreviewAsync(string sanitizedPreview, CancellationToken cancellationToken)
    { await Application.Current.Dispatcher.InvokeAsync(() => { cancellationToken.ThrowIfCancellationRequested(); Clipboard.SetText(sanitizedPreview); }); }
}
