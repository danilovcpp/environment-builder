using System.Windows;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.App.Features.SeedPacks;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.SeedPacks;
using Microsoft.Win32;

namespace EnvironmentBuilder.App.Shell;

public sealed class SeedPackInteractions(DialogService dialogs, IEnvironmentStore environments, AuthoringSession authoring, ShellViewModel shell) : ISeedPackInteractions
{
    public async Task<string?> SelectArchiveAsync(CancellationToken token) => await Application.Current.Dispatcher.InvokeAsync(() =>
    {
        token.ThrowIfCancellationRequested();
        var picker = new OpenFileDialog { Filter = "SeedPack ZIP (*.zip)|*.zip", CheckFileExists = true, Multiselect = false, Title = "Импорт внешнего SeedPack" };
        return picker.ShowDialog(Application.Current.MainWindow) == true ? picker.FileName : null;
    });
    public async Task<bool> ConfirmRemovalAsync(SeedPackRow pack, CancellationToken token) =>
        await dialogs.ShowAsync(new("Удалить версию SeedPack", $"{pack.Title}\n{pack.Reference.Id} · {pack.Version}\nSHA-256: {pack.Reference.ContentHash}\n\nНеиспользуемая версия будет удалена из библиотеки. Ссылки и активные чтения повторно проверяются перед удалением.",
            [new("cancel", "Отмена"), new("remove", "Удалить версию")], "cancel"), token) == "remove";
    public async Task OpenOwnerAsync(SeedPackOwner owner, CancellationToken token)
    {
        var items = await environments.ListAsync(token);
        var item = items.FirstOrDefault(e => e.Document?.Id == owner.Id);
        if (item?.Document is { } document) { await shell.NavigateCommand.ExecuteAsync("environments"); await authoring.OpenAsync(document); }
        else { await dialogs.ShowAsync(new("Владелец пакета", $"{owner.Name}\n{owner.Kind} · {owner.Id}\nНезавершённая операция удерживает пакет до разрешения результата.", [new("close", "Закрыть")], "close"), token); }
    }
    public async Task OpenAssignmentAsync(SeedPackRow pack, CancellationToken token)
    {
        await shell.NavigateCommand.ExecuteAsync("environments");
        if (!shell.ShowEnvironments) { return; }
        authoring.PreferredSeedPack = pack.Reference;
        shell.Status = "Выберите или создайте окружение; пакет «" + pack.Title + "» будет выбран в редакторе baseline.";
    }
    public async Task CopyIdentityAsync(SeedPackRow pack, CancellationToken token) => await Application.Current.Dispatcher.InvokeAsync(() =>
    { token.ThrowIfCancellationRequested(); Clipboard.SetText($"{pack.Reference.Id} · {pack.Version} · {pack.Reference.ContentHash}"); });
}
