using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.App.Shell;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Docker;
using EnvironmentBuilder.Infrastructure.Imports;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace EnvironmentBuilder.App.Composition;

public sealed class AppServices : IAsyncDisposable
{
    private AppServices(ServiceProvider provider, SqliteWorker worker, SingleWriterLease lease)
    { Provider = provider; Worker = worker; Lease = lease; }
    public ServiceProvider Provider { get; }
    public SqliteWorker Worker { get; }
    public SingleWriterLease Lease { get; }
    public IReadOnlyList<string> RuntimeFileWarnings { get; private set; } = [];

    public static async Task<AppServices> CreateAsync(ShellViewModel shell, NavigationService navigation, ThemeService themes, string? dataDirectory = null)
    {
        // ACL and directory APIs are synchronous OS work, deliberately kept off the Dispatcher.
        AppDataDirectory directory = await Task.Run(() => new AppDataDirectory(dataDirectory)).ConfigureAwait(false);
        SingleWriterLease lease = await SingleWriterLease.AcquireAsync(directory).ConfigureAwait(false);
        var worker = new SqliteWorker(directory, lease);
        try
        {
            await worker.Ready.ConfigureAwait(false);
            var services = new ServiceCollection();
            services.AddSingleton(directory).AddSingleton(lease).AddSingleton(worker);
            services.AddSingleton<IClock, SystemClock>();
            services.AddSingleton<StrictDocumentReader>();
            services.AddSingleton<IEnvironmentStore, EnvironmentStore>();
            services.AddSingleton<ComponentLibraryStore>();
            services.AddSingleton<IComponentLibrary, AuthoringLibrary>();
            services.AddSingleton<ILocalPathMappings, LocalPathMappings>();
            services.AddSingleton<IInitializationDocumentReader>(p => p.GetRequiredService<StrictDocumentReader>());
            services.AddSingleton<PreviewInputCapture>();
            services.AddSingleton<IPreviewInputCapture>(p => p.GetRequiredService<PreviewInputCapture>());
            services.AddSingleton<CreateEnvironmentService>();
            services.AddSingleton<PreviewReviewStore>();
            services.AddSingleton<IAuthoringInteractions, AuthoringInteractions>();
            services.AddSingleton<EnvironmentListViewModel>();
            services.AddSingleton<AuthoringSession>();
            services.AddSingleton<IOperationStore, OperationStore>();
            services.AddSingleton<ResourceLedgerStore>();
            services.AddSingleton<DataApplicationStore>();
            services.AddSingleton<IDataApplicationJournal>(p => p.GetRequiredService<DataApplicationStore>());
            services.AddSingleton<BaselineExecutor>();
            services.AddSingleton<DockerProcessFactory>();
            services.AddSingleton<RuntimeInputCapture>();
            services.AddSingleton<DockerRuntimeAdapter>();
            services.AddSingleton<IStartRuntime>(p => p.GetRequiredService<DockerRuntimeAdapter>());
            services.AddSingleton<IRefreshRuntime>(p => p.GetRequiredService<DockerRuntimeAdapter>());
            services.AddSingleton<StartEnvironmentService>();
            services.AddSingleton<IStopRuntime, DockerStopAdapter>();
            services.AddSingleton<StopEnvironmentService>();
            services.AddSingleton<ICleanupRuntime, DockerRemovalAdapter>();
            services.AddSingleton<IEditPersistence, EditFinalization>();
            services.AddSingleton<VerifiedCleanupExecutor>();
            services.AddSingleton<EditEnvironmentService>();
            services.AddSingleton<IDeletePersistence, DeleteFinalization>();
            services.AddSingleton<DeleteEnvironmentService>();
            services.AddSingleton<IDataResetPersistence, DataResetFinalization>();
            services.AddSingleton<ClearEnvironmentDataService>();
            services.AddSingleton<BaselinePreflight>();
            services.AddSingleton<RefreshEnvironmentDataService>();
            services.AddSingleton<IDestructiveInteractions, DestructiveInteractions>();
            services.AddSingleton<IEnvironmentObservationSource, DockerObservationReader>();
            services.AddSingleton<LogMemoryBudget>();
            services.AddSingleton<IInspectionStreamsFactory, DockerInspectionStreams>();
            services.AddSingleton<IRuntimeInteractions, RuntimeInteractions>();
            services.AddSingleton<EnvironmentCommands>();
            services.AddSingleton(p => new InspectViewModel(p.GetRequiredService<IEnvironmentObservationSource>(), p.GetRequiredService<IInspectionStreamsFactory>(),
                p.GetRequiredService<EnvironmentCommands>(), p.GetRequiredService<UiDispatcher>().ApplyAsync));
            services.AddSingleton<ShutdownCoordinator>();
            services.AddSingleton<ContentBlobStore>();
            services.AddSingleton<SeedPackStore>();
            services.AddSingleton<ISeedPackStore>(p => p.GetRequiredService<SeedPackStore>());
            services.AddSingleton<SeedPackImporter>();
            services.AddSingleton(p => new Features.SeedPacks.SeedPackLibraryViewModel(p.GetRequiredService<SeedPackStore>().ListMetadataAsync));
            services.AddSingleton(p => new Features.Settings.SettingsViewModel(themes, p.GetRequiredService<IComponentLibrary>(), p.GetRequiredService<ILocalPathMappings>()));
            services.AddSingleton<ISecretVault, DpapiSecretVault>();
            services.AddSingleton<LocalPathResolver>();
            services.AddSingleton<IInputResolver, InputResolver>();
            services.AddSingleton<SanitizedDiagnostics>();
            services.AddSingleton(p => new OperationCoordinator(p.GetRequiredService<IOperationStore>(), p.GetRequiredService<IClock>(), () => lease.CanWrite));
            services.AddSingleton(shell).AddSingleton(navigation).AddSingleton(themes);
            services.AddSingleton<UiDispatcher>().AddSingleton<DialogService>();
            ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            await provider.GetRequiredService<OperationCoordinator>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            return new AppServices(provider, worker, lease)
            { RuntimeFileWarnings = await PrivateRuntimeFiles.CleanOrphansAsync(directory, provider.GetRequiredService<IOperationStore>(), CancellationToken.None).ConfigureAwait(false) };
        }
        catch { await worker.DisposeAsync().ConfigureAwait(false); await lease.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync().ConfigureAwait(false);
        await Worker.DisposeAsync().ConfigureAwait(false);
        await Lease.DisposeAsync().ConfigureAwait(false);
    }
}
