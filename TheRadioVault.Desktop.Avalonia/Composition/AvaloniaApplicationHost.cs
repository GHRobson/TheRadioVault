using AvaloniaDesktopLifetime = Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
using RadioVaultApplicationLifetime = TheRadioVault.Application.Abstractions.IApplicationLifetime;
using TheRadioVault.Application.Abstractions;
using TheRadioVault.Application.Composition;
using TheRadioVault.Application.Models;
using TheRadioVault.Application.Services;
using TheRadioVault.Core.Playback;
using TheRadioVault.Desktop.Avalonia.Anywhere;
using TheRadioVault.Desktop.Avalonia.Diarization;
using TheRadioVault.Desktop.Avalonia.Local;
using TheRadioVault.Desktop.Avalonia.Platform;
using TheRadioVault.Desktop.Avalonia.Playback;
using TheRadioVault.Desktop.Avalonia.Research;
using TheRadioVault.Desktop.Avalonia.Transcription;
using TheRadioVault.Presentation.Navigation;
using TheRadioVault.Presentation.ViewModels;
using TheRadioVault.Services;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Jobs;
using TheRadioVault.Services.Services;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Services;

namespace TheRadioVault.Desktop.Avalonia.Composition;

public sealed class AvaloniaApplicationHost : IDisposable
{
    private static string BuildVersion => AppVersionService.DisplayVersion;
    private readonly NativeServerConnectionPreferences _nativeServerPreferences;
    private readonly bool _isRemoteSession;

    private AvaloniaApplicationHost(
        ApplicationServiceRegistry services,
        MainWindowViewModel mainWindow,
        NativeServerConnectionPreferences nativeServerPreferences,
        bool isRemoteSession)
    {
        Services = services;
        MainWindow = mainWindow;
        _nativeServerPreferences = nativeServerPreferences;
        _isRemoteSession = isRemoteSession;
    }

    public ApplicationServiceRegistry Services { get; }
    public MainWindowViewModel MainWindow { get; }
    public bool IsRemoteSession => _isRemoteSession;
    public LoopbackServerClient ServerConnection => Services.GetRequiredService<LoopbackServerClient>();
    public string ServerDisplayName => ServerConnection.ServerDisplayName;
    public long StartupCacheSizeBytes => ServerConnection.CacheSizeBytes;
    public DateTimeOffset? LastCacheSyncAt => _nativeServerPreferences.LibraryCacheSynchronizedAt;

    public static AvaloniaApplicationHost Create(
        AvaloniaStartupOptions options,
        AvaloniaWindowProvider windowProvider,
        AvaloniaDesktopLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(windowProvider);
        ArgumentNullException.ThrowIfNull(lifetime);

        StartupFailureReporter.Checkpoint("Selecting the Radio Vault Server application session.");
        var startupCoordinator = new ApplicationStartupCoordinator();
        var nativeServerPreferences = NativeServerConnectionPreferences.Load();
        var startupPlan = startupCoordinator.CreatePlan(new ApplicationStartupRequest(
            ForceLocalLibrary: options.ForceLocalLibrary,
            UseRemoteLibraryOnStartup: options.ForceRemoteLibrary || nativeServerPreferences.UseRemoteOnStartup,
            HasSavedServer: nativeServerPreferences.HasSavedServer));
        // The macOS product is client-only: it never owns or starts a local
        // Radio Vault Server. An unpaired first launch must therefore remain a
        // remote-client session so Settings can offer discovery and pairing.
        var isRemoteSession = startupPlan.IsRemoteClient || OperatingSystem.IsMacOS();
        var configuredDownloadScope = isRemoteSession
            ? nativeServerPreferences.ServerInstanceId
            : WebServerPreferences.Load().ServerInstanceId;
        var downloadScope = Guid.TryParse(configuredDownloadScope, out var parsedDownloadScope)
            ? parsedDownloadScope.ToString("N")
            : "local-server";

        var services = new ApplicationServiceRegistry();
        var navigation = new ShellNavigationService();
        var appLifetime = new AvaloniaApplicationLifetime(lifetime);
        var uiDispatcher = new AvaloniaUiDispatcher();
        services
            .RegisterSingleton<IUiDispatcher>(uiDispatcher)
            .RegisterSingleton<IFileSelectionService>(new AvaloniaFileSelectionService(windowProvider))
            .RegisterSingleton<ILibraryFolderShowSelectionService>(new AvaloniaLibraryFolderShowSelectionService(windowProvider))
            .RegisterSingleton<IClipboardService>(new AvaloniaClipboardService(windowProvider))
            .RegisterSingleton<IExternalLauncherService>(new AvaloniaExternalLauncherService())
            .RegisterSingleton<ISystemAppearanceService>(new AvaloniaSystemAppearanceService())
            .RegisterSingleton<IAppThemeService>(new AvaloniaThemeService(AvaloniaAppPaths.ThemePreferencePath))
            .RegisterSingleton<IScreenBoundsService>(new AvaloniaScreenBoundsService(windowProvider))
            .RegisterSingleton<RadioVaultApplicationLifetime>(appLifetime)
            .RegisterSingleton<IUserNotificationService>(new AvaloniaUserNotificationService(windowProvider))
            .RegisterSingleton<INavigationService>(navigation)
            .RegisterSingleton(startupCoordinator)
            .RegisterFactory(() => new ApplicationShutdownCoordinator())
            .RegisterFactory(() => new ApplicationWindowTransitionCoordinator());

        StartupFailureReporter.Checkpoint("Using the dedicated server as the only archive database owner.");
        var serverConnection = new LoopbackServerClient(
            remotePreferences: nativeServerPreferences,
            useRemoteServer: isRemoteSession);

        services
            .RegisterSingleton(serverConnection)
            .RegisterSingleton(registry => new ServerMediaProxy(registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<INativeDownloadService>(registry => new NativeDownloadService(
                registry.GetRequiredService<LoopbackServerClient>(),
                Path.Combine(AvaloniaAppPaths.DataDirectory, "Downloads", downloadScope)))
            .RegisterSingleton<INativeDownloadPreferencesStore>(new NativeDownloadPreferencesStore(
                Path.Combine(AvaloniaAppPaths.DataDirectory, "Downloads", downloadScope, "preferences.json")))
            .RegisterSingleton<ITranscriptRepository>(registry => new LoopbackTranscriptRepository(registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<ISpeakerIdentityRepository>(registry => new LoopbackSpeakerIdentityRepository(registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<IVoiceLearningCoordinator>(registry => new LoopbackVoiceLearningCoordinator(
                registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<IBackgroundJobQueue>(new BackgroundJobQueue(1))
            .RegisterSingleton<ITranscriptionCoordinator>(registry => new LoopbackTranscriptionCoordinator(
                registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<ITranscriptionBatchCoordinator>(registry => new LoopbackTranscriptionBatchCoordinator(
                registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<IServerTranscriptionAdministrationService>(registry => new LoopbackServerTranscriptionAdministrationService(
                registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<ILibraryBrowseService>(registry => new LoopbackLibraryBrowseService(registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<ILibraryActionService>(registry => new LoopbackLibraryActionService(registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<ILocalPlaybackLibraryService>(registry => new LoopbackPlaybackLibraryService(
                registry.GetRequiredService<LoopbackServerClient>(),
                registry.GetRequiredService<ServerMediaProxy>(),
                downloads: registry.GetRequiredService<INativeDownloadService>()))
            .RegisterSingleton<IQueueService>(registry => new LoopbackQueueService(registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<ISavedCollectionService>(registry => new LoopbackSavedCollectionService(registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<IMomentsService>(registry => new LoopbackMomentsService(registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<IResearchWorkspaceService>(registry => new LoopbackResearchWorkspaceService(registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<IWikiService>(registry => new LoopbackWikiService(registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<IBroadcastDetailsService>(registry => new LoopbackBroadcastDetailsService(registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<ILibraryFolderService>(registry => new LoopbackServerLibraryFolderService(
                registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<IArchiveHealthService>(registry => new LoopbackServerArchiveHealthService(
                registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<IPlaybackHandoffService>(registry => new LoopbackPlaybackHandoffService(registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<IResearchPackTransferService>(registry => new LoopbackResearchPackTransferService(registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<IWikiPackTransferService>(registry => new LoopbackWikiPackTransferService(registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<ILibraryMaintenanceService>(registry => new LoopbackServerLibraryMaintenanceService(
                registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<IRadioVaultAnywhereService>(registry => new DedicatedServerRadioVaultAnywhereService(
                registry.GetRequiredService<LoopbackServerClient>()))
            .RegisterSingleton<IConnectedAccessService>(new NativeConnectedAccessService(
                nativeServerPreferences, isRemoteSession, serverConnection))
            .RegisterSingleton<IConnectedPlaybackDiagnosticsService>(new LocalOnlyConnectedPlaybackDiagnosticsService());

        services
            .RegisterSingleton(registry => new PlaybackSessionCoordinator(CreatePlaybackEngine()))
            .RegisterSingleton(registry => new PlaybackViewModel(
                registry.GetRequiredService<PlaybackSessionCoordinator>(),
                registry.GetRequiredService<ILocalPlaybackLibraryService>(),
                registry.GetRequiredService<ILibraryActionService>(),
                registry.GetRequiredService<IQueueService>(),
                registry.GetRequiredService<IUiDispatcher>(),
                registry.GetRequiredService<IPlaybackHandoffService>(),
                isRemoteSession ? nativeServerPreferences.ServerDisplayName : "this computer's server"))
            .RegisterSingleton(registry => new DownloadsViewModel(
                registry.GetRequiredService<INativeDownloadService>(),
                registry.GetRequiredService<PlaybackViewModel>(),
                registry.GetRequiredService<IUiDispatcher>(),
                registry.GetRequiredService<IUserNotificationService>(),
                registry.GetRequiredService<ILibraryBrowseService>(),
                registry.GetRequiredService<INativeDownloadPreferencesStore>()))
            .RegisterSingleton(registry => new QueueViewModel(
                registry.GetRequiredService<IQueueService>(),
                registry.GetRequiredService<PlaybackViewModel>()))
            .RegisterSingleton(registry => new MomentsViewModel(
                registry.GetRequiredService<IMomentsService>(),
                registry.GetRequiredService<PlaybackViewModel>()))
            .RegisterSingleton(registry => new CollectionsViewModel(
                registry.GetRequiredService<ISavedCollectionService>(),
                registry.GetRequiredService<IQueueService>(),
                registry.GetRequiredService<PlaybackViewModel>()))
            .RegisterSingleton(registry => new TranscriptsViewModel(
                registry.GetRequiredService<ITranscriptRepository>(),
                registry.GetRequiredService<ISpeakerIdentityRepository>(),
                registry.GetRequiredService<IVoiceLearningCoordinator>(),
                registry.GetRequiredService<ITranscriptionCoordinator>(),
                registry.GetRequiredService<ITranscriptionBatchCoordinator>(),
                registry.GetRequiredService<IServerTranscriptionAdministrationService>(),
                registry.GetRequiredService<ILibraryBrowseService>(),
                registry.GetRequiredService<IBackgroundJobQueue>(),
                registry.GetRequiredService<IUiDispatcher>(),
                registry.GetRequiredService<IFileSelectionService>(),
                registry.GetRequiredService<PlaybackViewModel>()))
            .RegisterSingleton(registry => new ResearchWorkspaceViewModel(
                registry.GetRequiredService<IResearchWorkspaceService>(),
                registry.GetRequiredService<IResearchPackTransferService>(),
                registry.GetRequiredService<IFileSelectionService>(),
                registry.GetRequiredService<IExternalLauncherService>(),
                registry.GetRequiredService<PlaybackViewModel>(),
                registry.GetRequiredService<IWikiService>()))
            .RegisterSingleton(registry => new WikiViewModel(
                registry.GetRequiredService<IWikiService>(),
                registry.GetRequiredService<IWikiPackTransferService>(),
                registry.GetRequiredService<IFileSelectionService>(),
                registry.GetRequiredService<PlaybackViewModel>()))
            .RegisterSingleton(registry => new DashboardViewModel(
                registry.GetRequiredService<ILibraryBrowseService>(),
                registry.GetRequiredService<ILibraryActionService>(),
                registry.GetRequiredService<IBroadcastDetailsService>(),
                registry.GetRequiredService<PlaybackViewModel>(),
                registry.GetRequiredService<QueueViewModel>(),
                registry.GetRequiredService<IWikiService>()))
            .RegisterSingleton(registry => new LibraryViewModel(
                registry.GetRequiredService<ILibraryBrowseService>(),
                registry.GetRequiredService<ILibraryActionService>(),
                registry.GetRequiredService<IBroadcastDetailsService>(),
                registry.GetRequiredService<PlaybackViewModel>(),
                registry.GetRequiredService<QueueViewModel>(),
                registry.GetRequiredService<ITranscriptionCoordinator>(),
                registry.GetRequiredService<DownloadsViewModel>(),
                registry.GetRequiredService<IWikiService>()))
            .RegisterSingleton(registry => new SearchViewModel(
                registry.GetRequiredService<ILibraryBrowseService>(),
                registry.GetRequiredService<ILibraryActionService>(),
                registry.GetRequiredService<PlaybackViewModel>(),
                registry.GetRequiredService<QueueViewModel>(),
                registry.GetRequiredService<ITranscriptionCoordinator>(),
                registry.GetRequiredService<IWikiService>()))
            .RegisterSingleton(registry => new NowPlayingViewModel(
                registry.GetRequiredService<PlaybackViewModel>(),
                registry.GetRequiredService<MomentsViewModel>(),
                registry.GetRequiredService<QueueViewModel>(),
                registry.GetRequiredService<IBroadcastDetailsService>(),
                registry.GetRequiredService<ITranscriptRepository>(),
                registry.GetRequiredService<ITranscriptionCoordinator>(),
                registry.GetRequiredService<IWikiService>()))
            .RegisterSingleton(registry => new FullBroadcastInfoViewModel(
                registry.GetRequiredService<IBroadcastDetailsService>(),
                registry.GetRequiredService<PlaybackViewModel>(),
                registry.GetRequiredService<QueueViewModel>(),
                registry.GetRequiredService<ITranscriptRepository>(),
                registry.GetRequiredService<ITranscriptionCoordinator>(),
                registry.GetRequiredService<IWikiService>()))
            .RegisterSingleton(registry => new DesktopToolsViewModel(
                registry.GetRequiredService<ILibraryFolderService>(),
                registry.GetRequiredService<IArchiveHealthService>(),
                backup: null,
                registry.GetRequiredService<ILibraryMaintenanceService>(),
                registry.GetRequiredService<IRadioVaultAnywhereService>(),
                registry.GetRequiredService<IUiDispatcher>(),
                registry.GetRequiredService<IConnectedAccessService>(),
                registry.GetRequiredService<IConnectedPlaybackDiagnosticsService>(),
                registry.GetRequiredService<IFileSelectionService>(),
                registry.GetRequiredService<ILibraryFolderShowSelectionService>(),
                registry.GetRequiredService<IClipboardService>(),
                registry.GetRequiredService<IExternalLauncherService>(),
                registry.GetRequiredService<PlaybackViewModel>(),
                registry.GetRequiredService<IAppThemeService>(),
                registry.GetRequiredService<IServerTranscriptionAdministrationService>(),
                AvaloniaAppPaths.DataDirectory,
                AvaloniaAppPaths.DiagnosticLogPath,
                isRemoteSession,
                BuildVersion))
            .RegisterSingleton(registry => new MainWindowViewModel(
                version: BuildVersion,
                sessionDescription: isRemoteSession
                    ? $"Connected securely to {nativeServerPreferences.ServerDisplayName}."
                    : "Connected to the Radio Vault Server on this computer.",
                dashboard: registry.GetRequiredService<DashboardViewModel>(),
                library: registry.GetRequiredService<LibraryViewModel>(),
                search: registry.GetRequiredService<SearchViewModel>(),
                queue: registry.GetRequiredService<QueueViewModel>(),
                moments: registry.GetRequiredService<MomentsViewModel>(),
                collections: registry.GetRequiredService<CollectionsViewModel>(),
                transcripts: registry.GetRequiredService<TranscriptsViewModel>(),
                research: registry.GetRequiredService<ResearchWorkspaceViewModel>(),
                wiki: registry.GetRequiredService<WikiViewModel>(),
                downloads: registry.GetRequiredService<DownloadsViewModel>(),
                playback: registry.GetRequiredService<PlaybackViewModel>(),
                nowPlaying: registry.GetRequiredService<NowPlayingViewModel>(),
                broadcastInfo: registry.GetRequiredService<FullBroadcastInfoViewModel>(),
                tools: registry.GetRequiredService<DesktopToolsViewModel>(),
                navigation: registry.GetRequiredService<INavigationService>()));

        var required = new[]
        {
            typeof(IUiDispatcher), typeof(IFileSelectionService), typeof(ILibraryFolderShowSelectionService), typeof(IClipboardService),
            typeof(IExternalLauncherService), typeof(ISystemAppearanceService), typeof(IAppThemeService), typeof(IScreenBoundsService),
            typeof(RadioVaultApplicationLifetime), typeof(IUserNotificationService), typeof(INavigationService),
            typeof(ITranscriptRepository),
            typeof(IBackgroundJobQueue), typeof(ITranscriptionCoordinator), typeof(IServerTranscriptionAdministrationService),
            typeof(LoopbackServerClient), typeof(ServerMediaProxy), typeof(INativeDownloadService), typeof(ILibraryBrowseService), typeof(ILibraryActionService), typeof(ILocalPlaybackLibraryService),
            typeof(IQueueService), typeof(ISavedCollectionService), typeof(IMomentsService), typeof(IResearchWorkspaceService), typeof(IResearchPackTransferService), typeof(IWikiService), typeof(IWikiPackTransferService),
            typeof(IBroadcastDetailsService), typeof(ILibraryFolderService), typeof(IArchiveHealthService),
            typeof(ILibraryMaintenanceService), typeof(IPlaybackHandoffService),
            typeof(IRadioVaultAnywhereService), typeof(IConnectedAccessService), typeof(IConnectedPlaybackDiagnosticsService),
            typeof(PlaybackSessionCoordinator), typeof(PlaybackViewModel), typeof(DownloadsViewModel), typeof(QueueViewModel), typeof(MomentsViewModel), typeof(CollectionsViewModel), typeof(TranscriptsViewModel),
            typeof(SearchViewModel), typeof(ResearchWorkspaceViewModel), typeof(WikiViewModel),
            typeof(NowPlayingViewModel), typeof(FullBroadcastInfoViewModel), typeof(DesktopToolsViewModel),
            typeof(ApplicationStartupCoordinator), typeof(ApplicationShutdownCoordinator),
            typeof(ApplicationWindowTransitionCoordinator), typeof(MainWindowViewModel)
        };

        var report = services.CreateCompositionReport(required);
        if (!report.IsValid)
            throw new InvalidOperationException(
                "Avalonia server-client composition is incomplete: " + string.Join(", ", report.MissingRequiredServices));

        services.Freeze();
        StartupFailureReporter.Checkpoint("Native server-client and Radio Vault Web service graph frozen.");
        AvaloniaDiagnosticLog.Write(
            "Composition",
            "Avalonia server-client plus Anywhere service graph validated: " + services.CreateCompositionReport().ToDiagnosticText());
        return new AvaloniaApplicationHost(
            services,
            services.GetRequiredService<MainWindowViewModel>(),
            nativeServerPreferences,
            isRemoteSession);
    }

    private static IPlaybackEngine CreatePlaybackEngine()
        => OperatingSystem.IsMacOS()
            ? new MacAvFoundationPlaybackEngine()
            : OperatingSystem.IsLinux()
                ? new LinuxMpvPlaybackEngine()
                : new NAudioPlaybackEngine();

    public async Task InitializeStartupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isRemoteSession && ServerConnection.CacheSizeBytes > 0)
        {
            using (ServerConnection.UsePersistentCacheFirst())
                await MainWindow.InitializeAsync(warmCachedViews: true).ConfigureAwait(true);
            return;
        }

        await MainWindow.InitializeAsync(warmCachedViews: true).ConfigureAwait(true);
    }

    public async Task RefreshStartupCacheAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRemoteSession) return;
        var synchronizer = new NativeClientCacheSyncService(ServerConnection, _nativeServerPreferences);
        var plan = await synchronizer.CheckAsync(cancellationToken).ConfigureAwait(true);
        if (!plan.NoChanges)
        {
            ServerConnection.InvalidateMemoryCache();
            await MainWindow.RefreshAfterServerSyncAsync(plan.RequiresFullRefresh, plan.Kinds).ConfigureAwait(true);
        }
        synchronizer.Commit(plan);
    }

    public void Dispose()
    {
        if (Services.TryGetService<PlaybackViewModel>(out var playback) && playback is not null)
        {
            try { playback.FlushAsync().GetAwaiter().GetResult(); }
            catch { }
        }
        Services.Dispose();
    }
}
