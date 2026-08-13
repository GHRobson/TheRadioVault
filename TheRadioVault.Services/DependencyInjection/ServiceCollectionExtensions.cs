using Microsoft.Extensions.DependencyInjection;
using TheRadioVault.Core.Events;
using TheRadioVault.Core.Playback;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Services;
using TheRadioVault.Services.Jobs;
using TheRadioVault.Media.Contracts;
using TheRadioVault.Media.Services;

namespace TheRadioVault.Services.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRadioVaultServices(this IServiceCollection services, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("A database path is required.", nameof(databasePath));

        services.AddSingleton(new SqliteDatabase(databasePath));
        services.AddSingleton<IApplicationEventBus, ApplicationEventBus>();
        services.AddSingleton<ILivePlaybackStateStore, LivePlaybackStateStore>();
        services.AddSingleton<IBackgroundJobQueue>(provider => new BackgroundJobQueue(2, provider.GetRequiredService<IApplicationEventBus>()));
        services.AddSingleton<IArchiveService, ArchiveService>();
        services.AddSingleton<ILibraryActionService, LibraryActionService>();
        services.AddSingleton<IMomentsService, MomentsService>();
        services.AddSingleton<IQueueService, QueueService>();
        services.AddSingleton<ISavedCollectionService, SavedCollectionService>();
        services.AddSingleton<ILibraryFolderService, LibraryFolderService>();
        services.AddSingleton<IArchiveHealthService, ArchiveHealthService>();
        services.AddSingleton<ILibraryBrowseService, LibraryBrowseService>();
        services.AddSingleton<IResearchWorkspaceService, ResearchWorkspaceService>();
        services.AddSingleton<IWikiService, WikiService>();
        services.AddSingleton<IBroadcastDetailsService, BroadcastDetailsService>();
        return services;
    }

    public static IServiceCollection AddRadioVaultMedia(this IServiceCollection services, string artworkCacheDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(artworkCacheDirectory))
            throw new ArgumentException("An artwork cache directory is required.", nameof(artworkCacheDirectory));

        services.AddSingleton<IAudioMetadataReader, TagLibAudioMetadataService>();
        services.AddSingleton<IAudioMetadataWriter, TagLibAudioMetadataService>();
        services.AddSingleton<IArtworkCache>(_ => new FileArtworkCache(artworkCacheDirectory));
        services.AddSingleton<IMediaFingerprintService, MediaFingerprintService>();
        services.AddSingleton<IMediaRenamePlanner, MediaRenamePlanner>();
        services.AddSingleton<IMediaInspectionService, MediaInspectionService>();
        services.AddSingleton<IFileSynchronizationService, FileSynchronizationService>();
        return services;
    }

    /// <summary>
    /// Registers the playback engine supplied by the host platform and the
    /// shared coordinator that sits above it.
    /// </summary>
    public static IServiceCollection AddRadioVaultPlayback<TEngine>(this IServiceCollection services)
        where TEngine : class, IPlaybackEngine
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IPlaybackEngine, TEngine>();
        services.AddSingleton<IPlaybackCoordinator, PlaybackCoordinator>();
        return services;
    }
}
