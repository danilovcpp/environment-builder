using EnvironmentBuilder.App.Composition;
using EnvironmentBuilder.App.Shell;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Infrastructure.Imports;
using EnvironmentBuilder.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace EnvironmentBuilder.IntegrationTests.Support;

public sealed class CompositionTests
{
    [Fact]
    public async Task Production_composition_opens_offline_store_and_releases_the_writer_on_shutdown()
    {
        using var isolated = IsolatedTestStore.Create();
        var navigation = new NavigationService();
        var themes = new ThemeService();
        var shell = new ShellViewModel(navigation, themes);
        await using (AppServices app = await AppServices.CreateAsync(shell, navigation, themes, isolated.DataDirectory))
        {
            Assert.Empty(await app.Provider.GetRequiredService<IEnvironmentStore>().ListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await app.Provider.GetRequiredService<IOperationStore>().ReadUnresolvedAsync(TestContext.Current.CancellationToken));
            Assert.NotNull(app.Provider.GetRequiredService<ISecretVault>());
            Assert.NotNull(app.Provider.GetRequiredService<IInputResolver>());
            Assert.NotNull(app.Provider.GetRequiredService<SeedPackImporter>());
            Assert.NotNull(app.Provider.GetRequiredService<OperationCoordinator>());
            Assert.True(app.Lease.CanWrite);
        }
        await using var writer = await SingleWriterLease.AcquireAsync(new AppDataDirectory(isolated.DataDirectory));
        Assert.True(writer.CanWrite);
    }
}
