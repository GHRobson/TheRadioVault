using System.Collections.Concurrent;
using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Client.Mobile.Platform;
using TheRadioVault.Client.Mobile.Playback;
using TheRadioVault.Client.Mobile.Synchronization;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile;

public sealed class MobileClientSession : IDisposable
{
    private static readonly double[] PlaybackSpeeds = [0.75d, 1d, 1.25d, 1.5d, 1.75d, 2d];
    private static readonly TimeSpan DurableProgressInterval = TimeSpan.FromSeconds(5);
    // Matches the server commit tolerance. A running source continues to move
    // while this decoder is prepared, so a narrower local window can prevent a
    // valid transfer from ever reaching the commit and source-stop stages.
    private const long PlaybackTransferAlignmentToleranceMs = 3_000;
    private readonly MobileServerClient _server;
    private readonly MobileDownloadService _downloads;
    private readonly MobileOfflineMutationSynchronizationCoordinator _offlineMutationSynchronization;
    private readonly IMobilePlaybackEngine _playback;
    private readonly MobilePlaybackOwnershipCoordinator _playbackOwnership;
    private readonly MobilePlaybackSynchronizationCoordinator _playbackSynchronization;
    private readonly MobilePlaybackTimeline _playbackTimeline = new();
    private readonly IMobileNowPlayingService _nowPlaying;
    private readonly IMobileDownloadPolicy _downloadPolicy;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly SemaphoreSlim _exploreSyncGate = new(1, 1);
    private readonly Timer _syncTimer;
    private readonly Timer _metadataSyncTimer;
    private readonly MobileMetadataCache _metadataCache;
    private readonly MobileMetadataSynchronizationCoordinator _metadataSynchronization;
    private readonly ConcurrentDictionary<long, Task<byte[]?>> _artworkRequests = new();
    private readonly ConcurrentDictionary<long, WebClientBroadcastDetails> _broadcastDetails = new();
    private readonly ConcurrentDictionary<long, byte> _pendingPlayCountEpisodes = new();
    private MobileMediaProxy? _mediaProxy;
    private MobileDownloadRecord? _activeDownload;
    private CancellationTokenSource? _downloadCancellation;
    private MobileBroadcastItem? _pendingDownload;
    private byte[]? _nowPlayingArtwork;
    private long _nowPlayingArtworkEpisodeId;
    private IReadOnlyList<WebClientLibraryCollectionSummary> _incompleteLibraryCollections = [];
    private double _speed = 1d;
    private long _playbackGeneration;
    private DateTimeOffset _lastDurableSave = DateTimeOffset.MinValue;
    private DateTimeOffset _lastOfflineSave = DateTimeOffset.MinValue;
    private bool _explicitSeekPending;
    private bool _ownsPlayback;
    private bool _offlinePlayback;
    private bool _downloadPauseRequested;
    private bool _downloadCancelRequested;
    private MobileSyncDiagnostics _syncDiagnostics = new(0, null, null, string.Empty);
    private bool _disposed;

    public MobileClientSession(
        MobileServerClient server,
        IMobilePlaybackEngine playback,
        IMobileNowPlayingService nowPlaying,
        IMobileDownloadPolicy downloadPolicy)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _downloads = new MobileDownloadService(
            _server,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TheRadioVault",
                "Downloads"));
        var offlineMutationStore = new MobileOfflineMutationStore(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TheRadioVault",
                "PendingChanges"));
        _offlineMutationSynchronization = new MobileOfflineMutationSynchronizationCoordinator(
            offlineMutationStore,
            new MobileOfflineMutationTransport(_server),
            (summary, _) => ReconcileMutationBroadcastAsync(summary),
            summary => ApplyLocalBroadcastSummary(summary));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _playbackOwnership = new MobilePlaybackOwnershipCoordinator(() => _server.ClientId);
        _playbackSynchronization = new MobilePlaybackSynchronizationCoordinator(
            new MobilePlaybackSynchronizationTransport(_server),
            _playback,
            _playbackOwnership);
        _nowPlaying = nowPlaying ?? throw new ArgumentNullException(nameof(nowPlaying));
        _downloadPolicy = downloadPolicy ?? throw new ArgumentNullException(nameof(downloadPolicy));
        var metadataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TheRadioVault",
            "MetadataCache");
        _metadataCache = new MobileMetadataCache(metadataRoot, _server.Connection?.ServerInstanceId ?? string.Empty);
        _metadataSynchronization = new MobileMetadataSynchronizationCoordinator(
            new MobileMetadataSynchronizationTransport(_server),
            _metadataCache,
            Notify);
        _playback.StateChanged += PlaybackOnStateChanged;
        _playback.MediaEnded += PlaybackOnMediaEnded;
        _nowPlaying.CommandReceived += NowPlayingOnCommandReceived;
        _server.ConnectivityChanged += ServerOnConnectivityChanged;
        _syncTimer = new Timer(_ => _ = SynchronizePlaybackAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _metadataSyncTimer = new Timer(
            _ => _ = SynchronizeMetadataCacheAsync(),
            null,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(15));
        IsPaired = _server.IsPaired;
    }

    public event EventHandler? StateChanged;
    public event EventHandler? PlaybackStateChanged;
    public event Action<int>? TabRequested;

    public bool IsBusy { get; private set; }
    public bool IsPaired { get; private set; }
    public bool IsLiveConnected => _server.IsReachable;
    public bool IsMetadataSyncing => _metadataSynchronization.IsSynchronizing;
    public bool ShowsOfflineIndicator => IsPaired && !IsLiveConnected;
    public bool ShowsSyncIndicator => IsPaired && IsLiveConnected && IsMetadataSyncing;
    public int PendingSyncChanges => _syncDiagnostics.PendingChanges;
    public DateTimeOffset? LastSyncAttemptAt => _syncDiagnostics.LastAttemptAt;
    public DateTimeOffset? LastSuccessfulSyncAt =>
        _syncDiagnostics.LastSuccessfulSyncAt ?? _metadataSynchronization.LastSuccessfulSynchronizationAt;
    public string LastSyncError => _syncDiagnostics.LastError;
    public int CachedBroadcastCount => _metadataCache.Snapshot.Broadcasts.Count;
    public int CachedExplorePageCount => _metadataCache.Snapshot.ExplorePages.Count;
    public string StatusText { get; private set; } = "Pair this iPhone with your Radio Vault Server.";
    public string KnowledgeStatusText { get; private set; } = "Knowledge has not been loaded yet.";
    public string ServerName => _server.Connection?.ServerDisplayName ?? "No server paired";
    public string ServerAddress => _server.Connection is { } connection
        ? $"https://{connection.ServerAddress}:{connection.SecurePort}"
        : string.Empty;
    public int TotalBroadcasts { get; private set; }
    public int CompletedBroadcasts { get; private set; }
    public int InProgressBroadcasts { get; private set; }
    public int FavouriteBroadcasts { get; private set; }
    public IReadOnlyList<WebClientLibraryCollectionSummary> LibraryCollections { get; private set; } = [];
    public IReadOnlyList<MobileBroadcastItem> ContinueListening { get; private set; } = [];
    public IReadOnlyList<MobileBroadcastItem> RecentBroadcasts { get; private set; } = [];
    public IReadOnlyList<MobileBroadcastItem> OnThisDay { get; private set; } = [];
    public IReadOnlyList<MobileBroadcastItem> UnheardBroadcasts { get; private set; } = [];
    public IReadOnlyList<MobileBroadcastItem> LibraryBroadcasts { get; private set; } = [];
    public IReadOnlyList<MobileBroadcastItem> DownloadedBroadcasts { get; private set; } = [];
    public IReadOnlyList<WebMomentSummary> SavedMoments { get; private set; } = [];
    public MobileKnowledgeSnapshot? Knowledge { get; private set; }
    public IReadOnlyList<WebQueueItem> QueueItems { get; private set; } = [];
    public IReadOnlyList<DiscoveredRadioVaultServer> Servers { get; private set; } = [];
    public MobileBroadcastItem? SelectedBroadcast { get; private set; }
    public string NowPlayingTitle { get; private set; } = "Nothing playing";
    public string NowPlayingSubtitle { get; private set; } = "Choose a broadcast from Home or Library";
    public string PlaybackStatus { get; private set; } = "Ready";
    public bool IsPlaying => _playback.Current.IsPlaying;
    public bool CanControlPlayback => _playback.Current.IsOpen && _ownsPlayback;
    public long? LocalPlaybackEpisodeId => SelectedBroadcast?.EpisodeId;
    public long? PreparingPlaybackEpisodeId { get; private set; }
    public bool IsPreparingPlayback => PreparingPlaybackEpisodeId is > 0;
    public double PlaybackProgress { get; private set; }
    public string PlaybackTime { get; private set; } = "0:00 / 0:00";
    public string SpeedText => $"{_speed:0.##}×";
    public bool IsDownloading { get; private set; }
    public bool IsDownloadPaused { get; private set; }
    public long? ActiveDownloadEpisodeId { get; private set; }
    public string DownloadStatus { get; private set; } = "Downloads are stored on this iPhone.";
    public int DownloadProgressPercent { get; private set; }
    public long DownloadStorageBytes { get; private set; }
    public long PendingDownloadBytes { get; private set; }
    public string DownloadStorageText => $"{DownloadedBroadcasts.Count:N0} download{(DownloadedBroadcasts.Count == 1 ? string.Empty : "s")} · {FormatBytes(DownloadStorageBytes)} stored";
    public bool WifiOnlyDownloads
    {
        get => _downloadPolicy.WifiOnly;
        set
        {
            _downloadPolicy.WifiOnly = value;
            Notify();
        }
    }
    public bool AutoDownloadNewBroadcasts
    {
        get => _downloadPolicy.AutoDownloadNewBroadcasts;
        set
        {
            var wasEnabled = _downloadPolicy.AutoDownloadNewBroadcasts;
            _downloadPolicy.AutoDownloadNewBroadcasts = value;
            if (value && !wasEnabled) _downloadPolicy.AutoDownloadSince = DateTimeOffset.UtcNow;
            Notify();
        }
    }
    public bool DeleteCompletedDownloads
    {
        get => _downloadPolicy.DeleteCompletedDownloads;
        set
        {
            _downloadPolicy.DeleteCompletedDownloads = value;
            Notify();
            if (value) _ = CleanupCompletedDownloadsAsync();
        }
    }
    public long DownloadStorageLimitBytes
    {
        get => _downloadPolicy.StorageLimitBytes;
        set
        {
            _downloadPolicy.StorageLimitBytes = Math.Max(0, value);
            Notify();
            if (value > 0) _ = EnforceDownloadStorageLimitAsync();
        }
    }
    public string DownloadStorageLimitText => DownloadStorageLimitBytes <= 0
        ? "No storage limit"
        : $"Up to {FormatBytes(DownloadStorageLimitBytes)}";
    public bool HasMiniPlayer => SelectedBroadcast is not null || _playbackSynchronization.RemoteBroadcast is not null;
    public bool MiniPlayerShowsHandoff => _playbackSynchronization.RemoteBroadcast is not null && !_ownsPlayback;
    public string MiniPlayerTitle => _playbackSynchronization.RemoteBroadcast?.Title ?? NowPlayingTitle;
    public string MiniPlayerSubtitle => _playbackSynchronization.RemoteBroadcast is not null
        ? $"Playing on {_playbackSynchronization.RemoteOwner}"
        : NowPlayingSubtitle;
    public double MiniPlayerProgress => _playbackSynchronization.RemoteBroadcast is not null
        ? _playbackSynchronization.RemoteBroadcast.Progress / 100d
        : PlaybackProgress;
    public string MiniPlayerTime => _playbackSynchronization.RemoteBroadcast is { } remote
        ? $"{FormatTime(TimeSpan.FromMilliseconds(remote.Source.PositionMs))} / " +
          FormatTime(TimeSpan.FromMilliseconds(remote.Source.DurationMs))
        : PlaybackTime;
    public string MiniPlayerElapsedTime => FormatTime(TimeSpan.FromMilliseconds(MiniPlayerPositionMs));
    public string MiniPlayerRemainingTime => $"-{FormatTime(TimeSpan.FromMilliseconds(
        Math.Max(0, MiniPlayerDurationMs - MiniPlayerPositionMs)))}";
    public string MiniPlayerTotalTime => FormatTime(TimeSpan.FromMilliseconds(MiniPlayerDurationMs));
    public bool MiniPlayerCanAct => MiniPlayerShowsHandoff || CanControlPlayback;
    public MobileBroadcastItem? CurrentBroadcast => _playbackSynchronization.RemoteBroadcast ?? SelectedBroadcast;

    public bool CanToggleBroadcast(long episodeId)
        => CanControlPlayback && SelectedBroadcast?.EpisodeId == episodeId;

    public bool IsPlayingBroadcast(long episodeId)
        => CanToggleBroadcast(episodeId) && IsPlaying;

    public async Task<byte[]?> LoadArtworkAsync(MobileBroadcastItem broadcast)
    {
        ArgumentNullException.ThrowIfNull(broadcast);
        if (string.IsNullOrWhiteSpace(broadcast.Source.ArtworkPath)) return null;
        var task = _artworkRequests.GetOrAdd(
            broadcast.EpisodeId,
            episodeId => LoadArtworkCoreAsync(episodeId));
        var content = await task.ConfigureAwait(false);
        if (content is null) _artworkRequests.TryRemove(broadcast.EpisodeId, out _);
        return content;
    }

    public async Task<byte[]?> LoadArtworkAsync(long episodeId)
    {
        if (episodeId <= 0) return null;
        var task = _artworkRequests.GetOrAdd(episodeId, LoadArtworkCoreAsync);
        var content = await task.ConfigureAwait(false);
        if (content is null) _artworkRequests.TryRemove(episodeId, out _);
        return content;
    }

    public async Task InitializeAsync()
    {
        await RefreshSyncDiagnosticsAsync().ConfigureAwait(false);
        DownloadedBroadcasts = await _downloads.GetBroadcastsAsync().ConfigureAwait(false);
        await RefreshDownloadStorageAsync().ConfigureAwait(false);
        IsPaired = _server.IsPaired;
        if (IsPaired)
        {
            await _metadataSynchronization
                .LoadAsync(_server.Connection?.ServerInstanceId ?? string.Empty)
                .ConfigureAwait(false);
            await _downloads.ReconcileSummariesAsync(_metadataCache.Snapshot.Broadcasts).ConfigureAwait(false);
            DownloadedBroadcasts = await _downloads.GetBroadcastsAsync().ConfigureAwait(false);
            ApplyMetadataSnapshot();
        }
        Notify();
        if (!IsPaired)
        {
            TabRequested?.Invoke(0);
            return;
        }

        TabRequested?.Invoke(0);
        await RefreshAsync().ConfigureAwait(false);
    }

    public async Task<MobileDiagnosticSnapshot> GetDiagnosticSnapshotAsync()
    {
        var pending = await _offlineMutationSynchronization
            .GetPendingAsync(CurrentServerInstanceId())
            .ConfigureAwait(false);
        var storage = await _downloads.GetStorageAsync().ConfigureAwait(false);
        var cache = _metadataCache.Snapshot;
        var cachedImages = cache.ExploreDocuments
            .SelectMany(value => value.Images)
            .Select(value => value.ImageId)
            .Distinct()
            .Count(_metadataCache.HasImage);

        return new MobileDiagnosticSnapshot(
            DateTimeOffset.UtcNow,
            IsPaired,
            IsLiveConnected,
            IsMetadataSyncing,
            IsBusy,
            ServerName,
            ServerAddress,
            StatusText,
            pending.Count,
            pending.Count(value => value.Kind == MobileOfflineMutationKind.Favourite),
            pending.Count(value => value.Kind == MobileOfflineMutationKind.ListeningStatus),
            pending.Count(value => value.Kind == MobileOfflineMutationKind.Moment),
            LastSyncAttemptAt,
            LastSuccessfulSyncAt,
            LastSyncError,
            cache.UpdatedAt,
            cache.SyncSequence,
            cache.SyncRevision,
            cache.Broadcasts.Count,
            cache.Broadcasts.Select(value => value.CollectionId).Distinct().Count(),
            cache.ExplorePages.Count,
            cache.ExploreDocuments.Count,
            cachedImages,
            cache.Moments?.Count ?? 0,
            cache.Knowledge is not null,
            storage.DownloadCount,
            storage.CompletedBytes,
            storage.PendingBytes,
            IsDownloading,
            IsDownloadPaused,
            ActiveDownloadEpisodeId,
            DownloadStatus,
            HasMiniPlayer,
            CurrentBroadcast?.EpisodeId,
            IsPlaying,
            CanControlPlayback,
            MiniPlayerShowsHandoff,
            PlaybackStatus,
            MiniPlayerTime,
            WifiOnlyDownloads,
            AutoDownloadNewBroadcasts,
            DeleteCompletedDownloads,
            DownloadStorageLimitBytes);
    }

    public async Task RefreshAsync()
    {
        if (!IsPaired || IsBusy) return;
        SetBusy(true, $"Connecting to {ServerName}…");
        var metadataActivity = _metadataSynchronization.BeginActivity();
        try
        {
            var bootstrap = await _server.TestConnectionAsync().ConfigureAwait(false);
            var overview = await _server.GetOverviewAsync().ConfigureAwait(false);
            ApplyOnlineOverview(overview);
            await SynchronizeMetadataCacheAsync(forceExploreRefresh: true).ConfigureAwait(false);
            if (_metadataCache.Snapshot.Broadcasts.Count == 0)
            {
                await _metadataSynchronization
                    .BootstrapEmptyCacheAsync(bootstrap.Server.InstanceId, overview)
                    .ConfigureAwait(false);
                ApplyMetadataSnapshot();
            }
            if (_metadataCache.Snapshot.ExploreOverview is null)
                await RefreshExploreCacheAsync(warmEntireCache: false).ConfigureAwait(false);
            DownloadedBroadcasts = await _downloads.GetBroadcastsAsync().ConfigureAwait(false);
            await RefreshDownloadStorageAsync().ConfigureAwait(false);
            QueueItems = await _server.GetQueueAsync().ConfigureAwait(false);
            _metadataCache.SetQueue(QueueItems);
            await _metadataCache.SaveAsync().ConfigureAwait(false);
            await ObserveSharedPlaybackSafelyAsync(
                await _server.GetPlaybackSessionAsync().ConfigureAwait(false)).ConfigureAwait(false);
            StatusText = $"Connected to {bootstrap.Server.DisplayName} · {TotalBroadcasts:N0} broadcasts";
        }
        catch (Exception exception)
        {
            StatusText = _metadataCache.Snapshot.Broadcasts.Count > 0
                ? "Offline · showing the latest saved Radio Vault library"
                : "Could not reach the paired server: " + exception.Message;
            ApplyMetadataSnapshot();
        }
        finally
        {
            metadataActivity.Dispose();
            SetBusy(false);
        }
    }

    public async Task SearchAsync(string searchText, bool hideCompleted = false)
    {
        var search = searchText?.Trim() ?? string.Empty;
        if (_metadataCache.Snapshot.Broadcasts.Count > 0)
        {
            LibraryBroadcasts = QueryCachedBroadcasts(null, search, "All", null, null, hideCompleted);
            StatusText = $"{LibraryBroadcasts.Count:N0} broadcast{(LibraryBroadcasts.Count == 1 ? string.Empty : "s")} shown";
            Notify();
            return;
        }
        if (!IsPaired || IsBusy) return;
        SetBusy(true, search.Length == 0 ? "Loading the Library…" : $"Searching for “{search}”…");
        try
        {
            LibraryBroadcasts = Convert((await _server.BrowseAsync(
                search, hideCompleted: hideCompleted).ConfigureAwait(false)).Broadcasts);
            StatusText = $"{LibraryBroadcasts.Count:N0} broadcast{(LibraryBroadcasts.Count == 1 ? string.Empty : "s")} shown";
        }
        catch (Exception exception) { StatusText = "Search failed: " + exception.Message; }
        finally { SetBusy(false); }
    }

    public async Task<IReadOnlyList<MobileBroadcastItem>> BrowseCollectionAsync(
        int? collectionId,
        string? searchText = null,
        string filter = "All",
        int? year = null,
        int? month = null,
        bool hideCompleted = false,
        string? collectionName = null)
    {
        var search = searchText?.Trim() ?? string.Empty;
        if (_metadataCache.Snapshot.Broadcasts.Count > 0)
        {
            var cached = QueryCachedBroadcasts(collectionId, search, filter, year, month, hideCompleted, collectionName);
            StatusText = $"{cached.Count:N0} broadcast{(cached.Count == 1 ? string.Empty : "s")} shown";
            return cached;
        }
        if (!IsPaired || IsBusy) return [];
        SetBusy(true, search.Length == 0 ? "Loading broadcasts…" : $"Searching for “{search}”…");
        try
        {
            var collectionIds = ResolveCollectionIds(collectionId, collectionName);
            var results = await Task.WhenAll(collectionIds.Select(id => _server.BrowseAsync(
                search,
                collectionId: id,
                filter: filter,
                year: year,
                month: month,
                hideCompleted: hideCompleted))).ConfigureAwait(false);
            var broadcasts = Convert(results
                .SelectMany(value => value.Broadcasts)
                .GroupBy(value => value.RepresentativeEpisodeId)
                .Select(group => group.OrderByDescending(value => value.LastPlayedAt).First()));
            StatusText = $"{broadcasts.Count:N0} broadcast{(broadcasts.Count == 1 ? string.Empty : "s")} shown";
            return broadcasts;
        }
        catch (Exception exception)
        {
            StatusText = "Library failed: " + exception.Message;
            return [];
        }
        finally { SetBusy(false); }
    }

    public async Task<IReadOnlyList<WebClientLibraryArchivePeriodSummary>> LoadArchivePeriodsAsync(
        int? collectionId,
        int? year = null,
        bool hideCompleted = false,
        string? collectionName = null)
    {
        if (_metadataCache.Snapshot.Broadcasts.Count > 0)
        {
            var cached = BuildCachedArchivePeriods(collectionId, year, hideCompleted, collectionName);
            StatusText = $"{cached.Count:N0} archive period{(cached.Count == 1 ? string.Empty : "s")} shown";
            return cached;
        }
        if (!IsPaired || IsBusy) return [];
        SetBusy(true, year.HasValue ? "Loading months…" : "Loading years…");
        try
        {
            var results = await Task.WhenAll(ResolveCollectionIds(collectionId, collectionName)
                .Select(id => _server.GetArchivePeriodsAsync(id, year, hideCompleted))).ConfigureAwait(false);
            var periods = CombineArchivePeriods(results.SelectMany(value => value));
            StatusText = $"{periods.Count:N0} archive period{(periods.Count == 1 ? string.Empty : "s")} shown";
            return periods;
        }
        catch (Exception exception)
        {
            StatusText = "Archive periods failed: " + exception.Message;
            return [];
        }
        finally { SetBusy(false); }
    }

    public async Task<(
        WebClientLibrarySearchFacets? Facets,
        IReadOnlyList<WebClientLibrarySearchSuggestion> Suggestions,
        IReadOnlyList<MobileBroadcastItem> Results)> ExploreAsync(
        string? searchText,
        int? collectionId,
        string filter,
        int? year,
        string searchScope,
        bool hasTranscript)
    {
        if (!IsPaired || IsBusy) return (null, [], []);
        var search = searchText?.Trim() ?? string.Empty;
        var hasFilters = collectionId.HasValue || year.HasValue || hasTranscript ||
                         !filter.Equals("All", StringComparison.OrdinalIgnoreCase) ||
                         !searchScope.Equals("All", StringComparison.OrdinalIgnoreCase);
        SetBusy(true, search.Length == 0 ? "Exploring the archive…" : $"Searching for “{search}”…");
        try
        {
            var facets = await _server.GetSearchFacetsAsync().ConfigureAwait(false);
            var suggestions = search.Length < 2
                ? Array.Empty<WebClientLibrarySearchSuggestion>()
                : await _server.GetSearchSuggestionsAsync(search).ConfigureAwait(false);
            var results = search.Length == 0 && !hasFilters
                ? Array.Empty<MobileBroadcastItem>()
                : Convert((await _server.BrowseAsync(
                    search,
                    limit: 250,
                    collectionId: collectionId,
                    filter: filter,
                    year: year,
                    searchScope: searchScope,
                    hasTranscript: hasTranscript).ConfigureAwait(false)).Broadcasts);
            StatusText = search.Length == 0 && !hasFilters
                ? "Choose a way into the archive or browse by show."
                : results.Count == 1 ? "1 matching broadcast." : $"{results.Count:N0} matching broadcasts.";
            return (facets, suggestions, results);
        }
        catch (Exception exception)
        {
            StatusText = "Explore failed: " + exception.Message;
            return (null, [], []);
        }
        finally { SetBusy(false); }
    }

    public async Task<MobileExploreDashboard?> LoadExploreDashboardAsync()
    {
        var cached = BuildExploreDashboard();
        if (cached is not null)
        {
            if (IsLiveConnected) _ = RefreshExploreCacheAsync(warmEntireCache: false);
            return cached;
        }
        if (!IsPaired || IsBusy) return null;
        await RefreshExploreCacheAsync(warmEntireCache: false).ConfigureAwait(false);
        return BuildExploreDashboard();
    }

    public async Task<MobileWikiPageDocument?> LoadExplorePageAsync(Guid pageId)
    {
        var cached = _metadataCache.FindExploreDocument(pageId);
        if (cached is not null)
        {
            if (IsLiveConnected) _ = RefreshExplorePageAsync(pageId);
            return cached;
        }
        if (!IsPaired || IsBusy) return null;
        try
        {
            var page = await _server.GetWikiPageAsync(pageId).ConfigureAwait(false);
            if (page is not null)
            {
                _metadataCache.UpsertExploreDocuments([page]);
                await _metadataCache.SaveAsync().ConfigureAwait(false);
            }
            StatusText = page is null ? "That Explore article could not be found." : $"Loaded {page.Title}.";
            return page;
        }
        catch (Exception exception)
        {
            StatusText = "Explore article failed: " + exception.Message;
            return null;
        }
    }

    public IReadOnlyList<WebClientLibraryCollectionSummary> LibraryCollectionsFor(bool hideCompleted)
    {
        return CombineCollections(hideCompleted ? _incompleteLibraryCollections : LibraryCollections);
    }

    public async Task<IReadOnlyList<MobileExploreImage>> LoadExploreImagesAsync(
        MobileWikiPageDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var images = new List<MobileExploreImage>();
        foreach (var link in document.Images.OrderBy(value => value.SortOrder))
        {
            var bytes = _metadataCache.ReadImage(link.ImageId);
            if ((bytes is null || bytes.Length == 0) && IsLiveConnected)
            {
                try
                {
                    var downloaded = await _server.GetWikiImageAsync(link.ImageId).ConfigureAwait(false);
                    if (downloaded is not null)
                    {
                        await _metadataCache.SaveImageAsync(downloaded).ConfigureAwait(false);
                        bytes = downloaded.Content;
                    }
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Trace.WriteLine($"[iOS Explore image] {exception}");
                }
            }
            if (bytes is null || bytes.Length == 0) continue;
            images.Add(ToExploreImage(document, link, bytes));
        }
        return images;
    }

    public Task RefreshMetadataAsync() => SynchronizeMetadataCacheAsync(forceExploreRefresh: false);

    public async Task RetrySyncAsync()
    {
        if (!IsPaired) return;
        if (IsLiveConnected) await FlushOfflineMutationsAsync().ConfigureAwait(false);
        await SynchronizeMetadataCacheAsync(forceExploreRefresh: false).ConfigureAwait(false);
    }

    public async Task<WebClientBroadcastDetails?> LoadBroadcastDetailsAsync(
        MobileBroadcastItem broadcast,
        bool announce = true)
    {
        ArgumentNullException.ThrowIfNull(broadcast);
        if (_broadcastDetails.TryGetValue(broadcast.EpisodeId, out var cached)) return cached;
        if (!IsPaired || !IsLiveConnected || announce && IsBusy) return null;
        if (announce) SetBusy(true, "Loading broadcast information…");
        try
        {
            var details = await _server.GetBroadcastDetailsAsync(broadcast.EpisodeId).ConfigureAwait(false);
            _broadcastDetails[broadcast.EpisodeId] = details;
            if (announce) StatusText = $"Broadcast information loaded from {ServerName}.";
            return details;
        }
        catch (Exception exception)
        {
            if (announce) StatusText = "Broadcast information failed: " + exception.Message;
            return null;
        }
        finally { if (announce) SetBusy(false); }
    }

    public async Task<MobileBroadcastItem?> SetFavouriteAsync(MobileBroadcastItem broadcast, bool favourite)
    {
        ArgumentNullException.ThrowIfNull(broadcast);
        if (!IsPaired || IsBusy) return null;
        SetBusy(true, favourite ? "Adding to Favourites…" : "Removing from Favourites…");
        try
        {
            var summary = broadcast.Source with { Favourite = favourite };
            var replacement = ApplyLocalBroadcastSummary(summary);
            await _offlineMutationSynchronization.EnqueueFavouriteAsync(
                CurrentServerInstanceId(), broadcast.EpisodeId, favourite).ConfigureAwait(false);
            await RefreshSyncDiagnosticsAsync().ConfigureAwait(false);
            if (IsLiveConnected) await FlushOfflineMutationsAsync().ConfigureAwait(false);
            StatusText = PendingSyncChanges > 0
                ? favourite ? "Added to Favourites · waiting to sync." : "Removed from Favourites · waiting to sync."
                : favourite ? "Added to Favourites." : "Removed from Favourites.";
            return replacement;
        }
        catch (Exception exception)
        {
            StatusText = "Favourite update failed: " + exception.Message;
            return null;
        }
        finally { SetBusy(false); }
    }

    public async Task<bool> AddMomentAsync(string title, string notes)
    {
        if (SelectedBroadcast is not { } broadcast || !IsPaired || IsBusy) return false;
        SetBusy(true, "Saving Moment…");
        try
        {
            var position = CaptureLogicalPosition();
            var momentTitle = string.IsNullOrWhiteSpace(title)
                ? $"Moment at {FormatTime(TimeSpan.FromMilliseconds(position))}"
                : title.Trim();
            var mutationId = Guid.NewGuid().ToString("N");
            await _offlineMutationSynchronization.EnqueueMomentAsync(
                CurrentServerInstanceId(), broadcast.EpisodeId, position,
                momentTitle, notes?.Trim() ?? string.Empty, mutationId).ConfigureAwait(false);
            var savedMoment = new WebMomentSummary(
                -DateTime.UtcNow.Ticks,
                broadcast.EpisodeId,
                broadcast.Source.CollectionName,
                broadcast.Title,
                broadcast.Source.AirDate?.ToDateTime(TimeOnly.MinValue),
                position,
                momentTitle,
                notes?.Trim() ?? string.Empty,
                DateTime.UtcNow);
            SavedMoments = new[] { savedMoment }
                .Concat(SavedMoments.Where(value => value.Id >= 0 || value.EpisodeId != broadcast.EpisodeId || value.PositionMs != position))
                .ToArray();
            _metadataCache.SetMoments(SavedMoments);
            await _metadataCache.SaveAsync().ConfigureAwait(false);
            await RefreshSyncDiagnosticsAsync().ConfigureAwait(false);
            if (IsLiveConnected) await FlushOfflineMutationsAsync().ConfigureAwait(false);
            StatusText = PendingSyncChanges > 0 ? "Moment saved on this iPhone · waiting to sync." : "Moment saved.";
            return true;
        }
        catch (Exception exception)
        {
            StatusText = "Moment could not be saved: " + exception.Message;
            return false;
        }
        finally { SetBusy(false); }
    }

    public async Task LoadSavedAsync()
    {
        SavedMoments = _metadataCache.Snapshot.Moments ?? [];
        Notify();
        if (!IsPaired || !IsLiveConnected) return;
        try
        {
            SavedMoments = await _server.GetMomentsAsync().ConfigureAwait(false);
            _metadataCache.SetMoments(SavedMoments);
            await _metadataCache.SaveAsync().ConfigureAwait(false);
            StatusText = $"{FavouriteBroadcasts:N0} favourite{(FavouriteBroadcasts == 1 ? string.Empty : "s")} · {SavedMoments.Count:N0} moment{(SavedMoments.Count == 1 ? string.Empty : "s")}";
        }
        catch (Exception exception)
        {
            StatusText = SavedMoments.Count > 0
                ? "Offline · showing saved Favourites and Moments"
                : "Saved items could not be loaded: " + exception.Message;
        }
        finally { Notify(); }
    }

    public async Task PlayMomentAsync(WebMomentSummary moment)
    {
        var broadcast = FindCachedBroadcast(moment.EpisodeId);
        if (broadcast is null && IsLiveConnected)
        {
            try
            {
                broadcast = new MobileBroadcastItem(
                    await _server.GetBroadcastSummaryAsync(moment.EpisodeId).ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                PlaybackStatus = "Moment could not be opened: " + exception.Message;
                NotifyPlayback();
                return;
            }
        }
        if (broadcast is null)
        {
            PlaybackStatus = "This Moment's broadcast is not in the saved catalogue.";
            NotifyPlayback();
            return;
        }
        await PlayAsync(new MobileBroadcastItem(broadcast.Source with
        {
            PositionMs = Math.Clamp(moment.PositionMs, 0, Math.Max(moment.PositionMs, broadcast.Source.DurationMs)),
            Completed = false
        })).ConfigureAwait(false);
    }

    public async Task<MobileKnowledgeSnapshot?> LoadKnowledgeAsync()
    {
        Knowledge = _metadataCache.Snapshot.Knowledge ?? BuildLibraryKnowledgeSnapshot();
        if (Knowledge is not null)
            KnowledgeStatusText = Knowledge.IsLibraryFallback
                ? $"Library coverage ready · {Knowledge.Overview.TotalRecords:N0} broadcasts"
                : $"Saved Knowledge snapshot · {Knowledge.Overview.TotalRecords:N0} records";
        Notify();
        if (!IsPaired)
        {
            KnowledgeStatusText = "Pair this iPhone with a Radio Vault Server to load Knowledge.";
            Notify();
            return Knowledge;
        }
        if (!IsLiveConnected)
        {
            KnowledgeStatusText = Knowledge is null
                ? "Knowledge has not been saved on this iPhone yet. Reconnect to the server and try again."
                : Knowledge.IsLibraryFallback
                    ? "Offline · showing coverage from the saved Library catalogue."
                    : "Offline · showing the latest saved Knowledge snapshot.";
            Notify();
            return Knowledge;
        }
        try
        {
            var overviewTask = _server.GetKnowledgeOverviewAsync();
            var collectionsTask = _server.GetKnowledgeCollectionsAsync();
            var reviewsTask = _server.GetKnowledgeDateReviewsAsync();
            await Task.WhenAll(overviewTask, collectionsTask, reviewsTask).ConfigureAwait(false);
            Knowledge = new MobileKnowledgeSnapshot(
                await overviewTask.ConfigureAwait(false),
                await collectionsTask.ConfigureAwait(false),
                await reviewsTask.ConfigureAwait(false),
                DateTimeOffset.UtcNow);
            _metadataCache.SetKnowledge(Knowledge);
            await _metadataCache.SaveAsync().ConfigureAwait(false);
            KnowledgeStatusText = $"Knowledge is up to date · {Knowledge.Overview.TotalRecords:N0} records";
            StatusText = KnowledgeStatusText;
        }
        catch (Exception exception)
        {
            KnowledgeStatusText = Knowledge is not null
                ? Knowledge.IsLibraryFallback
                    ? "Library coverage is ready. Update Radio Vault Server for research evidence and triage."
                    : "The live Knowledge update failed · showing the latest saved snapshot."
                : exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed }
                    ? "The paired server does not expose the Knowledge service. Update Radio Vault Server, then pull down to retry."
                    : "Knowledge could not be loaded: " + exception.Message;
            StatusText = KnowledgeStatusText;
        }
        finally { Notify(); }
        return Knowledge;
    }

    public async Task<MobileKnowledgeCoverage?> LoadKnowledgeCoverageAsync(int collectionId)
    {
        if (!IsPaired || !IsLiveConnected)
        {
            var saved = BuildLibraryKnowledgeCoverage(collectionId);
            StatusText = saved is null ? "No saved coverage is available for that show." : $"Saved Library coverage loaded for {saved.ShowName}.";
            Notify();
            return saved;
        }
        try
        {
            StatusText = "Building Knowledge coverage…";
            Notify();
            var coverage = await _server.GetKnowledgeCoverageAsync(collectionId).ConfigureAwait(false);
            StatusText = coverage is null ? "No dated coverage is available for that show." : $"Coverage loaded for {coverage.ShowName}.";
            return coverage;
        }
        catch (Exception exception)
        {
            var saved = BuildLibraryKnowledgeCoverage(collectionId);
            StatusText = saved is null
                ? "Coverage could not be loaded: " + exception.Message
                : $"Live Knowledge is unavailable · showing saved Library coverage for {saved.ShowName}.";
            return saved;
        }
        finally { Notify(); }
    }

    public async Task<bool> ResolveKnowledgeDateReviewAsync(MobileKnowledgeDateReview review, int action)
    {
        if (!IsPaired || !IsLiveConnected) return false;
        try
        {
            StatusText = action switch
            {
                0 => "Accepting the suggested date…",
                1 => "Keeping the current Library date…",
                2 => "Ignoring this suggestion…",
                6 => "Reopening the date suggestion…",
                _ => "Saving the Knowledge decision…"
            };
            Notify();
            await _server.ResolveKnowledgeDateReviewAsync(
                review.ResearchId,
                action,
                action == 0 ? review.ProposedDate : null).ConfigureAwait(false);
            await LoadKnowledgeAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            StatusText = "Knowledge decision failed: " + exception.Message;
            Notify();
            return false;
        }
    }

    public async Task<MobileBroadcastItem?> SetListeningStatusAsync(MobileBroadcastItem broadcast, bool played)
    {
        ArgumentNullException.ThrowIfNull(broadcast);
        if (!IsPaired || IsBusy) return null;
        SetBusy(true, played ? "Marking as listened…" : "Marking as unlistened…");
        try
        {
            var duration = Math.Max(0, broadcast.Source.DurationMs);
            var summary = broadcast.Source with
            {
                PositionMs = played ? duration : 0,
                Completed = played,
                InProgress = false,
                LastPlayedAt = DateTimeOffset.UtcNow
            };
            var replacement = ApplyLocalBroadcastSummary(summary);
            await _downloads.UpdateProgressAsync(
                broadcast.EpisodeId, summary.PositionMs, played,
                summary.LastPlayedAt.Value).ConfigureAwait(false);
            await _offlineMutationSynchronization.EnqueueListeningStatusAsync(
                CurrentServerInstanceId(), broadcast.EpisodeId, played).ConfigureAwait(false);
            await RefreshSyncDiagnosticsAsync().ConfigureAwait(false);
            if (IsLiveConnected) await FlushOfflineMutationsAsync().ConfigureAwait(false);
            StatusText = PendingSyncChanges > 0
                ? played ? "Marked as listened · waiting to sync." : "Marked as unlistened · waiting to sync."
                : played ? "Marked as listened." : "Marked as unlistened.";
            return replacement;
        }
        catch (Exception exception)
        {
            StatusText = "Listening status update failed: " + exception.Message;
            return null;
        }
        finally { SetBusy(false); }
    }

    public async Task<bool> AddToQueueAsync(MobileBroadcastItem broadcast, bool playNext = false)
    {
        ArgumentNullException.ThrowIfNull(broadcast);
        if (!IsPaired || IsBusy) return false;
        SetBusy(true, playNext ? "Adding to Up Next…" : "Adding to the shared queue…");
        try
        {
            var result = await _server.AddToQueueAsync(broadcast.EpisodeId, playNext).ConfigureAwait(false);
            if (!result.Changed) throw new InvalidOperationException(result.Message);
            QueueItems = result.Queue;
            CacheQueue();
            StatusText = string.IsNullOrWhiteSpace(result.Message)
                ? playNext ? "Added to Up Next." : "Added to the end of the shared queue."
                : result.Message;
            return true;
        }
        catch (Exception exception)
        {
            StatusText = "Queue update failed: " + exception.Message;
            return false;
        }
        finally { SetBusy(false); }
    }

    public async Task RefreshQueueAsync()
    {
        if (!IsPaired || IsBusy) return;
        SetBusy(true, "Refreshing Up Next…");
        try
        {
            QueueItems = await _server.GetQueueAsync().ConfigureAwait(false);
            CacheQueue();
            StatusText = QueueItems.Count == 0 ? "Up Next is empty." : $"{QueueItems.Count:N0} broadcast{(QueueItems.Count == 1 ? string.Empty : "s")} in Up Next.";
        }
        catch (Exception exception) { StatusText = "Queue refresh failed: " + exception.Message; }
        finally { SetBusy(false); }
    }

    public async Task RemoveQueueItemAsync(WebQueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!IsPaired || IsBusy) return;
        SetBusy(true, "Removing from Up Next…");
        try
        {
            var result = await _server.RemoveQueueItemAsync(item.QueueId).ConfigureAwait(false);
            QueueItems = result.Queue;
            CacheQueue();
            StatusText = result.Message;
        }
        catch (Exception exception) { StatusText = "Queue update failed: " + exception.Message; }
        finally { SetBusy(false); }
    }

    public async Task ClearQueueAsync()
    {
        if (!IsPaired || IsBusy) return;
        SetBusy(true, "Clearing Up Next…");
        try
        {
            var result = await _server.ClearQueueAsync().ConfigureAwait(false);
            QueueItems = result.Queue;
            CacheQueue();
            StatusText = result.Message;
        }
        catch (Exception exception) { StatusText = "Queue clear failed: " + exception.Message; }
        finally { SetBusy(false); }
    }

    public async Task MoveQueueItemAsync(WebQueueItem item, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!IsPaired || IsBusy || QueueItems.Count < 2) return;
        var currentIndex = QueueItems.ToList().FindIndex(value => value.QueueId == item.QueueId);
        targetIndex = Math.Clamp(targetIndex, 0, QueueItems.Count - 1);
        if (currentIndex < 0 || currentIndex == targetIndex) return;
        SetBusy(true, "Reordering Up Next…");
        try
        {
            var queue = QueueItems;
            while (currentIndex != targetIndex)
            {
                var direction = Math.Sign(targetIndex - currentIndex);
                var result = await _server.MoveQueueItemAsync(item.QueueId, direction).ConfigureAwait(false);
                if (!result.Changed) throw new InvalidOperationException(result.Message);
                queue = result.Queue;
                currentIndex = queue.ToList().FindIndex(value => value.QueueId == item.QueueId);
                if (currentIndex < 0) break;
            }
            QueueItems = queue;
            CacheQueue();
            StatusText = "Up Next reordered.";
        }
        catch (Exception exception) { StatusText = "Queue reorder failed: " + exception.Message; }
        finally { SetBusy(false); }
    }

    public async Task PlayQueueItemAsync(WebQueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!IsPaired || IsBusy) return;
        MobileBroadcastItem broadcast;
        try
        {
            broadcast = new MobileBroadcastItem(
                await _server.GetBroadcastSummaryAsync(item.Episode.Id).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            StatusText = "Queued broadcast failed to load: " + exception.Message;
            Notify();
            return;
        }
        await PlayAsync(broadcast).ConfigureAwait(false);
    }

    public async Task<bool> IsDownloadedAsync(long episodeId)
        => await _downloads.IsDownloadedAsync(episodeId).ConfigureAwait(false);

    public async Task DownloadAsync(MobileBroadcastItem broadcast)
    {
        ArgumentNullException.ThrowIfNull(broadcast);
        if (!IsPaired || IsDownloading) return;
        if (_downloadPolicy.WifiOnly && !_downloadPolicy.IsUsingWifi)
        {
            DownloadStatus = "Connect to Wi-Fi or turn off Wi-Fi Only before downloading.";
            Notify();
            return;
        }
        _pendingDownload = broadcast;
        _downloadPauseRequested = false;
        _downloadCancelRequested = false;
        IsDownloadPaused = false;
        IsDownloading = true;
        ActiveDownloadEpisodeId = broadcast.EpisodeId;
        DownloadProgressPercent = 0;
        DownloadStatus = $"Preparing {broadcast.Title}…";
        var cancellation = new CancellationTokenSource();
        _downloadCancellation?.Dispose();
        _downloadCancellation = cancellation;
        Notify();
        try
        {
            var progress = new Progress<MobileDownloadProgress>(value =>
            {
                DownloadProgressPercent = value.Percent;
                DownloadStatus = value.TotalBytes > 0
                    ? $"Downloading {value.Title} · part {value.PartNumber} of {value.PartCount} · {value.Percent}%"
                    : $"Downloading {value.Title} · part {value.PartNumber} of {value.PartCount}";
                Notify();
            });
            var record = await _downloads.DownloadAsync(
                broadcast, progress, cancellation.Token).ConfigureAwait(false);
            _ = await LoadArtworkAsync(broadcast).ConfigureAwait(false);
            DownloadedBroadcasts = await _downloads.GetBroadcastsAsync().ConfigureAwait(false);
            if (DownloadStorageLimitBytes > 0)
                await _downloads.TrimToLimitAsync(
                    DownloadStorageLimitBytes, broadcast.EpisodeId).ConfigureAwait(false);
            DownloadedBroadcasts = await _downloads.GetBroadcastsAsync().ConfigureAwait(false);
            _pendingDownload = null;
            DownloadProgressPercent = 100;
            DownloadStatus = $"Downloaded {broadcast.Title} · {FormatBytes(record.SizeBytes)}";
        }
        catch (OperationCanceledException) when (_downloadPauseRequested)
        {
            IsDownloadPaused = true;
            DownloadStatus = $"Paused {broadcast.Title} at {DownloadProgressPercent}%";
        }
        catch (OperationCanceledException) when (_downloadCancelRequested)
        {
            await _downloads.DiscardPendingAsync(broadcast.EpisodeId).ConfigureAwait(false);
            _pendingDownload = null;
            DownloadProgressPercent = 0;
            DownloadStatus = $"Cancelled {broadcast.Title}.";
        }
        catch (Exception exception)
        {
            IsDownloadPaused = true;
            DownloadStatus = "Download interrupted. Tap Resume to continue: " + exception.Message;
        }
        finally
        {
            IsDownloading = false;
            if (!IsDownloadPaused) ActiveDownloadEpisodeId = null;
            if (ReferenceEquals(_downloadCancellation, cancellation))
            {
                _downloadCancellation = null;
                cancellation.Dispose();
            }
            await RefreshDownloadStorageAsync().ConfigureAwait(false);
            Notify();
        }
    }

    public void PauseDownload()
    {
        if (!IsDownloading || _downloadCancellation is null) return;
        _downloadPauseRequested = true;
        DownloadStatus = "Pausing after the current data write…";
        _downloadCancellation.Cancel();
        Notify();
    }

    public async Task ResumeDownloadAsync()
    {
        if (!IsDownloadPaused || _pendingDownload is null || IsDownloading) return;
        await DownloadAsync(_pendingDownload).ConfigureAwait(false);
    }

    public void CancelDownload()
    {
        if (_pendingDownload is null) return;
        _downloadPauseRequested = false;
        _downloadCancelRequested = true;
        IsDownloadPaused = false;
        DownloadStatus = "Cancelling download…";
        if (_downloadCancellation is not null) _downloadCancellation.Cancel();
        else _ = CancelPausedDownloadAsync(_pendingDownload);
        Notify();
    }

    private async Task CancelPausedDownloadAsync(MobileBroadcastItem broadcast)
    {
        await _downloads.DiscardPendingAsync(broadcast.EpisodeId).ConfigureAwait(false);
        _pendingDownload = null;
        ActiveDownloadEpisodeId = null;
        DownloadProgressPercent = 0;
        DownloadStatus = $"Cancelled {broadcast.Title}.";
        await RefreshDownloadStorageAsync().ConfigureAwait(false);
        Notify();
    }

    private async Task RefreshDownloadStorageAsync()
    {
        var storage = await _downloads.GetStorageAsync().ConfigureAwait(false);
        DownloadStorageBytes = storage.TotalBytes;
        PendingDownloadBytes = storage.PendingBytes;
    }

    public async Task CleanupCompletedDownloadsAsync()
    {
        if (IsDownloading) return;
        var removed = await _downloads.RemoveCompletedAsync(
            _activeDownload?.EpisodeId).ConfigureAwait(false);
        DownloadedBroadcasts = await _downloads.GetBroadcastsAsync().ConfigureAwait(false);
        await RefreshDownloadStorageAsync().ConfigureAwait(false);
        DownloadStatus = removed == 0
            ? "No completed downloads needed removing."
            : $"Removed {removed:N0} completed download{(removed == 1 ? string.Empty : "s")}.";
        Notify();
    }

    public async Task RepairDownloadsAsync()
    {
        if (IsDownloading) return;
        var removed = await _downloads.RepairAsync().ConfigureAwait(false);
        DownloadedBroadcasts = await _downloads.GetBroadcastsAsync().ConfigureAwait(false);
        await RefreshDownloadStorageAsync().ConfigureAwait(false);
        DownloadStatus = removed == 0
            ? "All downloaded broadcasts passed their storage check."
            : $"Removed {removed:N0} damaged download{(removed == 1 ? string.Empty : "s")}; download them again when convenient.";
        Notify();
    }

    private async Task EnforceDownloadStorageLimitAsync()
    {
        if (IsDownloading || DownloadStorageLimitBytes <= 0) return;
        var removed = await _downloads.TrimToLimitAsync(
            DownloadStorageLimitBytes, _activeDownload?.EpisodeId).ConfigureAwait(false);
        if (removed == 0) return;
        DownloadedBroadcasts = await _downloads.GetBroadcastsAsync().ConfigureAwait(false);
        await RefreshDownloadStorageAsync().ConfigureAwait(false);
        DownloadStatus = $"Removed {removed:N0} older download{(removed == 1 ? string.Empty : "s")} to stay within the storage limit.";
        Notify();
    }

    public async Task RemoveDownloadAsync(MobileBroadcastItem broadcast)
    {
        ArgumentNullException.ThrowIfNull(broadcast);
        if (IsDownloading) return;
        if (_activeDownload?.EpisodeId == broadcast.EpisodeId && _playback.Current.IsOpen)
        {
            DownloadStatus = "This download is currently playing. Start another broadcast before removing it.";
            Notify();
            return;
        }
        await _downloads.RemoveAsync(broadcast.EpisodeId).ConfigureAwait(false);
        DownloadedBroadcasts = await _downloads.GetBroadcastsAsync().ConfigureAwait(false);
        await RefreshDownloadStorageAsync().ConfigureAwait(false);
        DownloadStatus = $"Removed {broadcast.Title} from this iPhone.";
        Notify();
    }

    public async Task PlayDownloadedAsync(MobileBroadcastItem broadcast)
    {
        ArgumentNullException.ThrowIfNull(broadcast);
        if (IsPreparingPlayback) return;
        PreparingPlaybackEpisodeId = broadcast.EpisodeId;
        PlaybackStatus = IsBusy ? "Finishing the startup sync…" : "Preparing downloaded broadcast…";
        Notify();
        var startupDeadline = DateTimeOffset.UtcNow.AddSeconds(12);
        while (IsBusy && DateTimeOffset.UtcNow < startupDeadline)
            await Task.Delay(100).ConfigureAwait(false);
        if (IsBusy)
        {
            PreparingPlaybackEpisodeId = null;
            PlaybackStatus = "Playback is waiting for the Library startup sync. Try again in a moment.";
            Notify();
            return;
        }
        IsBusy = true;
        try
        {
            if (SelectedBroadcast is not null && _playback.Current.IsOpen &&
                (_offlinePlayback || CanControlPlayback))
            {
                if (CanControlPlayback && _playback.Current.IsPlaying) _playback.Pause();
                await FlushPlaybackAsync().ConfigureAwait(false);
            }
            var record = await _downloads.GetAsync(broadcast.EpisodeId).ConfigureAwait(false)
                ?? throw new FileNotFoundException("This broadcast is not downloaded on the iPhone.");
            _playback.Pause();
            _activeDownload = record;
            _offlinePlayback = true;
            _ownsPlayback = true;
            _playbackSynchronization.ClearRemotePlayback();
            _playbackTimeline.Load(
                record.Parts.Select(part => new WebCanonicalMediaPart(
                    part.PartNumber,
                    part.PartTotal,
                    part.LogicalStartMs,
                    part.LogicalEndMs,
                    part.MediaFileId,
                    part.SizeBytes,
                    "Downloaded",
                    string.Empty)),
                record.DurationMs);
            SelectedBroadcast = new MobileBroadcastItem(record.Summary);
            NowPlayingTitle = SelectedBroadcast.Title;
            NowPlayingSubtitle = SelectedBroadcast.Subtitle + " · Downloaded";
            _pendingPlayCountEpisodes.TryAdd(SelectedBroadcast.EpisodeId, 0);
            _lastOfflineSave = DateTimeOffset.MinValue;
            var position = _playbackTimeline.ClampPosition(record.Summary.PositionMs);
            OpenLogicalPosition(position, play: true, muted: false);
            PlaybackStatus = "Playing download on this iPhone";
        }
        catch (Exception exception)
        {
            _offlinePlayback = false;
            _activeDownload = null;
            _ownsPlayback = false;
            PlaybackStatus = "Downloaded playback failed: " + exception.Message;
        }
        finally
        {
            PreparingPlaybackEpisodeId = null;
            IsBusy = false;
            Notify();
            NotifyPlayback();
        }
    }

    public void MiniPlayerAction()
    {
        if (MiniPlayerShowsHandoff && _playbackSynchronization.RemoteBroadcast is { } remote)
        {
            _ = PlayAsync(remote);
            return;
        }
        TogglePlayPause();
    }

    public async Task DiscoverAsync()
    {
        if (IsBusy) return;
        SetBusy(true, "Looking for Radio Vault Server on this network…");
        try
        {
            Servers = await _server.DiscoverAsync().ConfigureAwait(false);
            StatusText = Servers.Count == 0
                ? "No servers found. Enable native clients and create a pairing code on Radio Vault Server."
                : $"Found {Servers.Count} server{(Servers.Count == 1 ? string.Empty : "s")}.";
        }
        catch (Exception exception) { StatusText = "Discovery failed: " + exception.Message; }
        finally { SetBusy(false); }
    }

    public async Task PairAsync(DiscoveredRadioVaultServer server, string pairingCode)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (IsBusy) return;
        SetBusy(true, $"Pairing with {server.DisplayName}…");
        try
        {
            await _server.PairAsync(server, pairingCode).ConfigureAwait(false);
            IsPaired = true;
            StatusText = $"Paired with {ServerName}. Loading your library…";
            Notify();
            TabRequested?.Invoke(0);
        }
        catch (Exception exception)
        {
            StatusText = "Pairing failed: " + exception.Message;
            SetBusy(false);
            return;
        }

        SetBusy(false);
        await RefreshAsync().ConfigureAwait(false);
    }

    public async Task PairManuallyAsync(string serverAddress, int securePort, string pairingCode)
    {
        if (IsBusy) return;
        SetBusy(true, $"Pairing with {serverAddress.Trim()}…");
        try
        {
            await _server.PairManuallyAsync(serverAddress, securePort, pairingCode).ConfigureAwait(false);
            IsPaired = true;
            StatusText = $"Paired with {ServerName}. Loading your library…";
            Notify();
            TabRequested?.Invoke(0);
        }
        catch (Exception exception)
        {
            StatusText = "Manual pairing failed: " + exception.Message;
            SetBusy(false);
            return;
        }

        SetBusy(false);
        await RefreshAsync().ConfigureAwait(false);
    }

    public void Forget()
    {
        CancelDownload();
        _playback.Pause();
        _ownsPlayback = false;
        _nowPlaying.Clear();
        _server.Forget();
        _metadataCache.Clear();
        _ = _offlineMutationSynchronization.ClearAsync();
        _artworkRequests.Clear();
        _mediaProxy?.Dispose();
        _mediaProxy = null;
        IsPaired = false;
        Servers = [];
        ContinueListening = [];
        RecentBroadcasts = [];
        OnThisDay = [];
        UnheardBroadcasts = [];
        LibraryBroadcasts = [];
        LibraryCollections = [];
        _incompleteLibraryCollections = [];
        QueueItems = [];
        SavedMoments = [];
        Knowledge = null;
        TotalBroadcasts = 0;
        CompletedBroadcasts = 0;
        InProgressBroadcasts = 0;
        FavouriteBroadcasts = 0;
        StatusText = "Pairing removed from this iPhone.";
        Notify();
        TabRequested?.Invoke(0);
    }

    public MobileBroadcastItem? FindCachedBroadcast(long episodeId)
        => CurrentBroadcast?.EpisodeId == episodeId
            ? CurrentBroadcast
            : LibraryBroadcasts.FirstOrDefault(value => value.EpisodeId == episodeId)
              ?? DownloadedBroadcasts.FirstOrDefault(value => value.EpisodeId == episodeId)
              ?? _metadataCache.Snapshot.Broadcasts
                  .Where(value => value.RepresentativeEpisodeId == episodeId)
                  .Select(value => new MobileBroadcastItem(value))
                  .FirstOrDefault();

    public async Task PlayTimelineLinkAsync(MobileWikiTimelineBroadcastLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var broadcast = FindCachedBroadcast(link.EpisodeId);
        if (broadcast is null && IsLiveConnected)
        {
            try
            {
                broadcast = new MobileBroadcastItem(
                    await _server.GetBroadcastSummaryAsync(link.EpisodeId).ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                PlaybackStatus = "Timeline playback failed: " + exception.Message;
                NotifyPlayback();
                return;
            }
        }
        if (broadcast is null)
        {
            PlaybackStatus = "That timeline broadcast is not available in the saved catalogue.";
            NotifyPlayback();
            return;
        }
        if (link.StartMs is { } startMs)
            broadcast = new MobileBroadcastItem(broadcast.Source with
            {
                PositionMs = Math.Max(0, startMs),
                Completed = false
            });
        await PlayAsync(broadcast).ConfigureAwait(false);
    }

    public async Task PlayAsync(MobileBroadcastItem broadcast)
    {
        ArgumentNullException.ThrowIfNull(broadcast);
        TracePlayback($"Play requested: episode={broadcast.EpisodeId}; title={broadcast.Title}; paired={IsPaired}; preparing={IsPreparingPlayback}; busy={IsBusy}");
        if (!IsPaired || IsPreparingPlayback)
        {
            TracePlayback("Play request ignored by the paired/preparing guard.");
            return;
        }
        NowPlayingTitle = broadcast.Title;
        NowPlayingSubtitle = broadcast.Subtitle;
        _playbackSynchronization.ClearRemotePlayback();
        PreparingPlaybackEpisodeId = broadcast.EpisodeId;
        PlaybackStatus = IsBusy
            ? "Preparing playback while the Library continues syncing…"
            : "Preparing secure stream…";
        Notify();
        WebPlaybackTransferTicket? transfer = null;
        var transferCommitted = false;
        try
        {
            TracePlayback("Flushing previous playback state.");
            await FlushPlaybackAsync().ConfigureAwait(false);
            SelectedBroadcast = broadcast;
            try
            {
                broadcast = new MobileBroadcastItem(
                    await _server.GetBroadcastSummaryAsync(broadcast.EpisodeId).ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.WriteLine($"[iOS playback summary refresh] {exception}");
            }
            _offlinePlayback = false;
            _activeDownload = null;
            _ownsPlayback = false;
            TracePlayback($"Requesting media manifest for episode {broadcast.EpisodeId}.");
            var manifest = await _server.GetMediaManifestAsync(broadcast.EpisodeId).ConfigureAwait(false);
            TracePlayback($"Media manifest received: parts={manifest.Parts.Count}; durationMs={manifest.DurationMs}.");
            if (manifest.Parts.Count == 0)
                throw new InvalidOperationException("This broadcast has no playable media parts.");

            _playbackTimeline.Load(manifest.Parts, manifest.DurationMs);
            var logicalPosition = _playbackTimeline.ClampPosition(broadcast.Source.PositionMs);
            TracePlayback($"Requesting shared playback session at position {logicalPosition}.");
            var shared = await _server.GetPlaybackSessionAsync().ConfigureAwait(false);
            TracePlayback($"Shared playback received: generation={shared.Generation}; owner={shared.OwnerClientId}; active={_playbackOwnership.HasActivePlayback(shared)}.");
            _playbackGeneration = Math.Max(0, shared.Generation);
            var anotherDeviceOwnsPlayback = _playbackOwnership.HasActivePlayback(shared) &&
                                            !_playbackOwnership.IsOwnedByThisDevice(shared);
            var desiredPlaying = true;

            if (anotherDeviceOwnsPlayback)
            {
                if (shared.Player.EpisodeId == broadcast.EpisodeId)
                {
                    logicalPosition = _playbackSynchronization.ProjectPosition(shared.Player);
                    _speed = Math.Clamp(shared.Player.Speed, 0.5d, 3d);
                    desiredPlaying = shared.Player.IsPlaying;
                }
                PlaybackStatus = $"Preparing while {_playbackOwnership.OwnerName(shared)} keeps playing…";
                NotifyPlayback();
                var begin = await _server.BeginPlaybackTransferAsync(new WebPlaybackTransferBeginRequest(
                    _server.ClientId,
                    broadcast.EpisodeId,
                    logicalPosition,
                    _playbackTimeline.DurationMs,
                    _speed,
                    desiredPlaying,
                    _server.ClientDisplayName,
                    "iOSClient")).ConfigureAwait(false);
                transfer = RequireTransfer(begin);
                logicalPosition = transfer.ProtectedPositionMs;
                _speed = transfer.Speed;
                desiredPlaying = transfer.DesiredPlaying;
            }

            SelectedBroadcast = ApplyPlaybackProgress(
                broadcast.Source,
                logicalPosition,
                _playbackTimeline.DurationMs,
                completed: false,
                anotherDeviceOwnsPlayback && shared.Player.UpdatedAt is { } sharedUpdatedAt
                    ? sharedUpdatedAt
                    : DateTimeOffset.UtcNow);
            NowPlayingTitle = broadcast.Title;
            NowPlayingSubtitle = broadcast.Subtitle;
            _pendingPlayCountEpisodes.TryAdd(broadcast.EpisodeId, 0);
            TracePlayback($"Opening episode {broadcast.EpisodeId} at {logicalPosition} ms; transfer={transfer is not null}.");
            OpenLogicalPosition(logicalPosition, desiredPlaying, muted: transfer is not null);

            if (transfer is not null)
            {
                transfer = await AlignTransferAsync(transfer, desiredPlaying).ConfigureAwait(false);
                var committed = await _server.CommitPlaybackTransferAsync(new WebPlaybackTransferCommitRequest(
                    _server.ClientId,
                    transfer.TransferId,
                    transfer.ReadyRevision,
                    CaptureLogicalPosition(),
                    DecoderRunningMuted: true)).ConfigureAwait(false);
                ThrowIfConflict(committed.Conflict, committed.Message);
                transferCommitted = true;
                _playbackGeneration = Math.Max(0, committed.Session.Generation);
                await WaitForSourceStopAsync(committed.Session).ConfigureAwait(false);
                var ownership = await _server.GetPlaybackSessionAsync().ConfigureAwait(false);
                if (!_playbackOwnership.IsOwnedByThisDevice(ownership))
                    throw new InvalidOperationException("Playback moved again before this iPhone became audible.");
                _playbackGeneration = Math.Max(0, ownership.Generation);
                _ownsPlayback = true;
                _playback.SetMuted(false);
                if (transfer.DesiredPlaying && !_playback.Current.IsPlaying) _playback.Play();
                if (!transfer.DesiredPlaying && _playback.Current.IsPlaying) _playback.Pause();
            }
            else
            {
                var update = await ReportLivePlaybackAsync(
                    force: !_playbackOwnership.IsOwnedByThisDevice(shared)).ConfigureAwait(false);
                ThrowIfConflict(update.Conflict, update.Message);
                var claimed = await _server.GetPlaybackSessionAsync().ConfigureAwait(false);
                _playbackGeneration = Math.Max(0, claimed.Generation);
                _ownsPlayback = _playbackOwnership.IsOwnedByThisDevice(claimed);
                if (!_ownsPlayback) throw new InvalidOperationException("Another device owns playback.");
            }

            PlaybackStatus = IsPlaying ? $"Playing on {_server.ClientDisplayName}" : $"Paused on {_server.ClientDisplayName}";
            _playbackSynchronization.ClearRemotePlayback();
            await SaveDurableProgressAsync().ConfigureAwait(false);
            TracePlayback($"Play preparation completed: episode={SelectedBroadcast?.EpisodeId}; playing={IsPlaying}; owns={_ownsPlayback}.");
        }
        catch (Exception exception)
        {
            TracePlayback("Play preparation failed: " + exception);
            if (transfer is not null && !transferCommitted)
            {
                try
                {
                    await _server.CancelPlaybackTransferAsync(new WebPlaybackTransferCancelRequest(
                        _server.ClientId, transfer.TransferId, exception.Message)).ConfigureAwait(false);
                }
                catch { }
            }
            _playback.Pause();
            _playback.SetMuted(false);
            _ownsPlayback = false;
            PlaybackStatus = "Playback failed: " + exception.Message;
        }
        finally
        {
            TracePlayback($"Play request finished: selected={SelectedBroadcast?.EpisodeId}; status={PlaybackStatus}.");
            PreparingPlaybackEpisodeId = null;
            Notify();
            NotifyPlayback();
        }
    }

    public void TogglePlayPause()
    {
        if (!CanControlPlayback) return;
        if (_playback.Current.IsPlaying) _playback.Pause(); else _playback.Play();
        PlaybackStatus = _playback.Current.IsPlaying ? "Playing" : "Paused";
        _ = FlushPlaybackAsync();
    }

    public void SkipBack() => SeekRelative(TimeSpan.FromSeconds(-15));
    public void SkipForward() => SeekRelative(TimeSpan.FromSeconds(30));

    public void SeekToProgress(double progress)
    {
        if (!CanControlPlayback || _playbackTimeline.DurationMs <= 0) return;
        SeekLogical((long)Math.Round(Math.Clamp(progress, 0d, 1d) * _playbackTimeline.DurationMs));
    }

    public void CycleSpeed()
    {
        if (!CanControlPlayback) return;
        var next = Array.FindIndex(PlaybackSpeeds, value => value > _speed + 0.001d);
        _speed = next < 0 ? PlaybackSpeeds[0] : PlaybackSpeeds[next];
        _playback.SetRate(_speed);
        PlaybackStatus = $"Playback speed {SpeedText}";
        NotifyPlayback();
        _ = FlushPlaybackAsync();
    }

    public Task FlushPlaybackAsync()
        => SynchronizePlaybackAsync(forceDurable: true, allowWhileBusy: true);

    private async Task<WebPlaybackTransferTicket> AlignTransferAsync(
        WebPlaybackTransferTicket transfer,
        bool desiredPlaying)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            var ready = await _server.MarkPlaybackTransferReadyAsync(new WebPlaybackTransferReadyRequest(
                _server.ClientId,
                transfer.TransferId,
                CaptureLogicalPosition(),
                _playbackTimeline.DurationMs,
                DecoderReady: true,
                DesiredPlaying: desiredPlaying,
                OverrideDesiredPlaying: false,
                _speed,
                _server.ClientDisplayName,
                "iOSClient")).ConfigureAwait(false);
            ThrowIfConflict(ready.Conflict, ready.Message);
            transfer = RequireTransfer(ready);
            desiredPlaying = transfer.DesiredPlaying;
            _speed = transfer.Speed;
            _playback.SetRate(_speed);
            if (desiredPlaying && !_playback.Current.IsPlaying) _playback.Play();
            if (!desiredPlaying && _playback.Current.IsPlaying) _playback.Pause();
            if (Math.Abs(CaptureLogicalPosition() - transfer.CommitPositionMs) <= PlaybackTransferAlignmentToleranceMs)
                return transfer;
            PlaybackStatus = "Aligning with the latest shared playhead…";
            OpenLogicalPosition(transfer.CommitPositionMs, desiredPlaying, muted: true);
        }
        throw new InvalidOperationException("The iPhone could not stay aligned with the source device.");
    }

    private async Task WaitForSourceStopAsync(WebPlaybackSession committed)
    {
        if (committed.CommittedTransfer is not { SourceWasPlaying: true, SourceStopAcknowledged: false } receipt)
            return;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250).ConfigureAwait(false);
            var latest = await _server.GetPlaybackSessionAsync().ConfigureAwait(false);
            if (latest.Generation != committed.Generation || !_playbackOwnership.IsOwnedByThisDevice(latest))
                throw new InvalidOperationException("Playback moved again during handoff.");
            if (latest.CommittedTransfer?.TransferId == receipt.TransferId &&
                latest.CommittedTransfer.SourceStopAcknowledged) return;
        }
        PlaybackStatus = "Playback moved; the previous device did not confirm that it stopped.";
    }

    private void OpenLogicalPosition(long logicalPositionMs, bool play, bool muted)
    {
        if (!_playbackTimeline.HasParts || SelectedBroadcast is null) return;
        var part = _playbackTimeline.SelectPart(logicalPositionMs);
        logicalPositionMs = _playbackTimeline.PositionMs;
        string url;
        if (_activeDownload is { } download)
        {
            var localPart = download.Parts.FirstOrDefault(value => value.MediaFileId == part.MediaFileId)
                ?? throw new FileNotFoundException("A downloaded media part is missing from its index.");
            url = _downloads.GetPartUri(download, localPart);
            TracePlayback($"Opening downloaded part {part.MediaFileId} from {url}.");
        }
        else
        {
            var serverPath = WebApiRoutes.MediaPart(SelectedBroadcast.EpisodeId, part.MediaFileId);
            if (_playback is IMobileStreamingPlaybackEngine nativeStreaming)
            {
                TracePlayback($"Opening native streamed part {part.MediaFileId} from {serverPath}.");
                nativeStreaming.Open(new MobilePlaybackSource(
                    serverPath,
                    (range, cancellationToken) => _server.OpenResponseAsync(
                        serverPath, range, cancellationToken)));
                url = string.Empty;
            }
            else
            {
                TracePlayback($"Opening proxy streamed part {part.MediaFileId} from {serverPath}.");
                _mediaProxy ??= new MobileMediaProxy(_server);
                url = _mediaProxy.Register(serverPath);
            }
        }
        _playback.SetMuted(muted);
        if (url.Length > 0) _playback.Open(url);
        _playback.SetRate(_speed);
        var localPosition = _playbackTimeline.LocalPosition(logicalPositionMs);
        _playbackTimeline.PrepareDecoder(logicalPositionMs, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(8));
        if (localPosition > TimeSpan.Zero) _playback.Seek(localPosition);
        if (play) _playback.Play(); else _playback.Pause();
        PlaybackStatus = _playbackTimeline.Parts.Count > 1
            ? $"Playing part {_playbackTimeline.PartIndex + 1} of {_playbackTimeline.Parts.Count}"
            : "Playing";
    }

    private void TracePlayback(string message)
    {
        if (_playback is IMobilePlaybackDiagnostics diagnostics)
            diagnostics.WritePlaybackDiagnostic($"[RadioVault iOS session] {message}");
    }

    private void SeekRelative(TimeSpan amount)
        => SeekLogical(CaptureLogicalPosition() + (long)amount.TotalMilliseconds);

    private void SeekLogical(long logicalPositionMs)
    {
        if (!CanControlPlayback || SelectedBroadcast is null) return;
        logicalPositionMs = _playbackTimeline.ClampPosition(logicalPositionMs);
        var targetPart = _playbackTimeline.FindPartIndex(logicalPositionMs);
        var shouldPlay = _playback.Current.IsPlaying;
        if (targetPart != _playbackTimeline.PartIndex)
        {
            OpenLogicalPosition(logicalPositionMs, shouldPlay, muted: false);
        }
        else
        {
            _playback.Seek(_playbackTimeline.LocalPosition(logicalPositionMs));
        }
        _playbackTimeline.SetPosition(logicalPositionMs);
        _explicitSeekPending = true;
        NotifyPlayback();
        _ = FlushPlaybackAsync();
    }

    private async Task SynchronizePlaybackAsync(bool forceDurable = false, bool allowWhileBusy = false)
    {
        if (_disposed) return;
        if (!await _syncGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (_offlinePlayback)
            {
                if (IsPaired && IsLiveConnected && SelectedBroadcast is not null && _playback.Current.IsOpen)
                {
                    var shared = await _server.GetPlaybackSessionAsync().ConfigureAwait(false);
                    if (!await StopForCommittedTransferAsync(shared).ConfigureAwait(false))
                    {
                        if (_playbackOwnership.HasActivePlayback(shared) &&
                            !_playbackOwnership.IsOwnedByThisDevice(shared))
                        {
                            if (_playbackOwnership.ConfirmForeignOwner(shared))
                            {
                                if (_playback.Current.IsPlaying) _playback.Pause();
                                _ownsPlayback = false;
                                _playbackGeneration = Math.Max(0, shared.Generation);
                                await ObserveSharedPlaybackAsync(shared).ConfigureAwait(false);
                                PlaybackStatus = $"Playback moved to {_playbackOwnership.OwnerName(shared)}";
                                NotifyPlayback();
                            }
                        }
                        else
                        {
                            _playbackOwnership.Reset();
                            _playbackGeneration = Math.Max(0, shared.Generation);
                            if (_ownsPlayback)
                            {
                                var downloadedLive = await ReportLivePlaybackAsync(
                                    force: !_playbackOwnership.HasActivePlayback(shared)).ConfigureAwait(false);
                                if (downloadedLive.Conflict)
                                {
                                    _playback.Pause();
                                    _ownsPlayback = false;
                                    PlaybackStatus = downloadedLive.Message;
                                    NotifyPlayback();
                                }
                            }
                        }
                    }
                }

                if (SelectedBroadcast is not null && _playback.Current.IsOpen &&
                    (forceDurable || DateTimeOffset.UtcNow - _lastOfflineSave >= DurableProgressInterval))
                {
                    var snapshot = CaptureDownloadedProgress();
                    var changed = await _downloads.UpdateProgressAsync(
                        snapshot.EpisodeId,
                        snapshot.PositionMs,
                        snapshot.Completed,
                        snapshot.CapturedAt).ConfigureAwait(false);
                    if (changed)
                        DownloadedBroadcasts = await _downloads.GetBroadcastsAsync().ConfigureAwait(false);
                    _lastOfflineSave = DateTimeOffset.UtcNow;
                    if (IsPaired && IsLiveConnected && (changed || snapshot.IncrementPlayCount))
                        await SynchronizeDownloadedProgressWithServerAsync(snapshot).ConfigureAwait(false);
                    if (changed) Notify();
                }
                return;
            }
            if (!IsPaired) return;
            var session = await _server.GetPlaybackSessionAsync().ConfigureAwait(false);
            if (SelectedBroadcast is null || !_playback.Current.IsOpen)
            {
                _playbackOwnership.Reset();
                await ObserveSharedPlaybackAsync(session).ConfigureAwait(false);
                return;
            }
            if (await StopForCommittedTransferAsync(session).ConfigureAwait(false)) return;
            if (IsBusy && !allowWhileBusy) return;
            if (!_ownsPlayback)
            {
                // An open decoder is not authority to reclaim an idle shared
                // session. This is especially important after a transfer away:
                // the old phone output can remain prepared, but only a fresh user
                // play/handoff action may make it owner again.
                if (_playbackOwnership.IsOwnedByThisDevice(session))
                {
                    _ownsPlayback = true;
                }
                else
                {
                    if (_playback.Current.IsPlaying) _playback.Pause();
                    await ObserveSharedPlaybackAsync(session).ConfigureAwait(false);
                    NotifyPlayback();
                    return;
                }
            }
            if (_playbackOwnership.HasActivePlayback(session) &&
                !_playbackOwnership.IsOwnedByThisDevice(session))
            {
                if (!_playbackOwnership.ConfirmForeignOwner(session)) return;
                _playbackGeneration = Math.Max(0, session.Generation);
                _ownsPlayback = false;
                if (_playback.Current.IsPlaying) _playback.Pause();
                await ObserveSharedPlaybackAsync(session).ConfigureAwait(false);
                PlaybackStatus = $"Playback moved to {_playbackOwnership.OwnerName(session)}";
                NotifyPlayback();
                return;
            }

            _playbackOwnership.Reset();
            _playbackGeneration = Math.Max(0, session.Generation);
            await ObserveSharedPlaybackAsync(session).ConfigureAwait(false);
            var live = await ReportLivePlaybackAsync(
                force: !_playbackOwnership.HasActivePlayback(session)).ConfigureAwait(false);
            if (live.Conflict)
            {
                _playback.Pause();
                PlaybackStatus = live.Message;
                NotifyPlayback();
                return;
            }
            _ownsPlayback = true;
            if (SelectedBroadcast is { } selected)
            {
                var position = CaptureLogicalPosition();
                var completed = _playbackTimeline.IsCompleted();
                ApplyPlaybackProgress(
                    selected.Source,
                    position,
                    _playbackTimeline.DurationMs,
                    completed,
                    DateTimeOffset.UtcNow);
                Notify();
            }

            var now = DateTimeOffset.UtcNow;
            if (forceDurable || now - _lastDurableSave >= DurableProgressInterval)
                await SaveDurableProgressAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"[iOS playback sync] {exception}");
        }
        finally { _syncGate.Release(); }
    }

    private async Task ObserveSharedPlaybackAsync(WebPlaybackSession session)
    {
        var observation = await _playbackSynchronization.ObserveAsync(session).ConfigureAwait(false);
        ApplyRemotePlaybackObservation(observation);
    }

    private async Task ObserveSharedPlaybackSafelyAsync(WebPlaybackSession session)
    {
        var observation = await _playbackSynchronization.ObserveSafelyAsync(
            session,
            SelectedBroadcast is not null,
            _playback.Current.IsOpen,
            _ownsPlayback).ConfigureAwait(false);
        ApplyRemotePlaybackObservation(observation);
    }

    private async Task<bool> StopForCommittedTransferAsync(WebPlaybackSession session)
    {
        if (!_playbackSynchronization.RequiresSourceStop(session)) return false;
        // Ownership is relinquished as soon as the committed receipt requires
        // this source to stop. A transient acknowledgement failure must not
        // allow the old device to present itself as authoritative again.
        _ownsPlayback = false;
        var result = await _playbackSynchronization
            .StopForCommittedTransferAsync(session)
            .ConfigureAwait(false);
        if (!result.Stopped) return false;
        PlaybackStatus = result.Status;
        NotifyPlayback();
        return true;
    }

    private void ApplyRemotePlaybackObservation(MobilePlaybackObservation observation)
    {
        if (observation.Broadcast is { } remote)
        {
            ApplyPlaybackProgress(
                remote.Source,
                remote.Source.PositionMs,
                remote.Source.DurationMs,
                remote.Source.Completed,
                remote.Source.LastPlayedAt ?? DateTimeOffset.UtcNow);
        }
        if (observation.Changed) Notify();
    }

    private async Task<WebClientPlaybackResult> ReportLivePlaybackAsync(bool force)
    {
        var broadcast = SelectedBroadcast ?? throw new InvalidOperationException("No broadcast is loaded.");
        return await _server.UpdateLivePlaybackAsync(new WebClientPlaybackUpdate(
            _server.ClientId,
            broadcast.EpisodeId,
            CaptureLogicalPosition(),
            _playbackTimeline.DurationMs,
            _playback.Current.IsPlaying,
            _speed,
            Completed: IsCompleted(),
            Force: force,
            DeviceName: _server.ClientDisplayName,
            DeviceKind: "iOSClient",
            ExpectedGeneration: _playbackGeneration,
            ExplicitSeek: _explicitSeekPending)).ConfigureAwait(false);
    }

    private async Task SaveDurableProgressAsync()
    {
        var broadcast = SelectedBroadcast;
        if (broadcast is null) return;
        var explicitSeek = _explicitSeekPending;
        var incrementPlayCount = _pendingPlayCountEpisodes.ContainsKey(broadcast.EpisodeId);
        var position = CaptureLogicalPosition();
        var completed = _playbackTimeline.IsCompleted();
        if (completed && _playbackTimeline.DurationMs > 0) position = _playbackTimeline.DurationMs;
        var result = await _server.SaveProgressAsync(new WebOfflineProgressUpdate(
            _server.ClientId,
            broadcast.EpisodeId,
            position,
            _playbackTimeline.DurationMs,
            Completed: completed,
            Speed: _speed,
            CapturedAt: DateTimeOffset.UtcNow,
            AllowRewind: true,
            ExpectedGeneration: _playbackGeneration,
            ExplicitSeek: explicitSeek,
            IncrementPlayCount: incrementPlayCount)).ConfigureAwait(false);
        if (result.Conflict) throw new InvalidOperationException(result.Message);
        _explicitSeekPending = false;
        if (incrementPlayCount && result.Changed)
            _pendingPlayCountEpisodes.TryRemove(broadcast.EpisodeId, out _);
        _lastDurableSave = DateTimeOffset.UtcNow;
        if (!result.Changed && result.Episode is { PositionMs: > 0 } canonicalEpisode &&
            canonicalEpisode.PositionMs > position)
        {
            var canonical = await _server.GetBroadcastSummaryAsync(broadcast.EpisodeId).ConfigureAwait(false);
            ApplyPlaybackProgress(
                canonical,
                canonical.PositionMs,
                canonical.DurationMs,
                canonical.Completed,
                canonical.LastPlayedAt ?? _lastDurableSave);
        }
        else
        {
            ApplyPlaybackProgress(
                broadcast.Source,
                position,
                _playbackTimeline.DurationMs,
                completed,
                _lastDurableSave);
        }
        await _metadataCache.SaveAsync().ConfigureAwait(false);
    }

    private long CaptureLogicalPosition()
        => _playbackTimeline.CaptureDecoderPosition(_playback.Current.Position, DateTimeOffset.UtcNow);

    private bool IsCompleted()
    {
        CaptureLogicalPosition();
        return _playbackTimeline.IsCompleted();
    }

    private long MiniPlayerPositionMs => _playbackSynchronization.RemoteBroadcast?.Source.PositionMs
        ?? _playbackTimeline.PositionMs;

    private long MiniPlayerDurationMs => _playbackSynchronization.RemoteBroadcast?.Source.DurationMs
        ?? _playbackTimeline.DurationMs;

    private static WebPlaybackTransferTicket RequireTransfer(WebPlaybackTransferResult result)
    {
        ThrowIfConflict(result.Conflict, result.Message);
        return result.Transfer ?? throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(result.Message) ? "The server did not create a playback move." : result.Message);
    }

    private static void ThrowIfConflict(bool conflict, string message)
    {
        if (conflict)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message) ? "Playback changed on another Radio Vault device." : message);
    }

    private void PlaybackOnMediaEnded(object? sender, EventArgs eventArgs)
    {
        if (_playbackTimeline.TryGetNextPart(out var nextPart))
        {
            OpenLogicalPosition(nextPart!.LogicalStartMs, play: true, muted: false);
            return;
        }
        _playbackTimeline.MarkCompleted(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(15));
        PlaybackStatus = "Finished";
        NotifyPlayback();
        _ = FlushPlaybackAsync();
    }

    private void PlaybackOnStateChanged(object? sender, MobilePlaybackSnapshot snapshot)
    {
        var logicalMs = CaptureLogicalPosition();
        PlaybackProgress = _playbackTimeline.Progress;
        PlaybackTime = $"{FormatTime(TimeSpan.FromMilliseconds(logicalMs))} / {FormatTime(TimeSpan.FromMilliseconds(_playbackTimeline.DurationMs))}";
        if (!string.IsNullOrWhiteSpace(snapshot.Error)) PlaybackStatus = "Playback failed: " + snapshot.Error;
        NotifyPlayback();
    }

    private void NowPlayingOnCommandReceived(object? sender, MobileRemoteCommand command)
    {
        switch (command.Kind)
        {
            case MobileRemoteCommandKind.Play:
                if (CanControlPlayback && !_playback.Current.IsPlaying) _playback.Play();
                _ = FlushPlaybackAsync();
                break;
            case MobileRemoteCommandKind.Pause:
                if (CanControlPlayback && _playback.Current.IsPlaying) _playback.Pause();
                _ = FlushPlaybackAsync();
                break;
            case MobileRemoteCommandKind.TogglePlayPause:
                TogglePlayPause();
                break;
            case MobileRemoteCommandKind.SkipBack:
                SkipBack();
                break;
            case MobileRemoteCommandKind.SkipForward:
                SkipForward();
                break;
            case MobileRemoteCommandKind.Seek when command.Position is { } position:
                SeekLogical((long)position.TotalMilliseconds);
                break;
        }
    }

    private async Task SynchronizeMetadataCacheAsync(bool forceExploreRefresh = false)
    {
        if (_disposed || !IsPaired) return;
        var warmExplore = false;
        try
        {
            if (IsLiveConnected) await FlushOfflineMutationsAsync().ConfigureAwait(false);
            await _metadataSynchronization.SynchronizeLibraryAsync(
                forceExploreRefresh,
                async (synchronization, _) =>
                {
                    await SynchronizeStoredDownloadedProgressWithServerAsync().ConfigureAwait(false);
                    await _downloads.ReconcileSummariesAsync(_metadataCache.Snapshot.Broadcasts).ConfigureAwait(false);
                    DownloadedBroadcasts = await _downloads.GetBroadcastsAsync().ConfigureAwait(false);
                    ApplyMetadataSnapshot();

                    if (synchronization.ExploreRefreshRequired)
                    {
                        await RefreshExploreCacheAsync(warmEntireCache: false).ConfigureAwait(false);
                        warmExplore = true;
                    }
                    await _metadataCache.SaveAsync().ConfigureAwait(false);
                    await RefreshSyncDiagnosticsAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"[iOS metadata sync] {exception}");
            if (_metadataCache.Snapshot.Broadcasts.Count > 0)
            {
                StatusText = "Offline · showing the latest saved Radio Vault library";
                ApplyMetadataSnapshot();
            }
        }
        if (warmExplore) _ = RefreshExploreCacheAsync(warmEntireCache: true);
        if (DeleteCompletedDownloads) _ = CleanupCompletedDownloadsAsync();
        if (AutoDownloadNewBroadcasts) _ = TryAutomaticDownloadAsync();
    }

    private async Task TryAutomaticDownloadAsync()
    {
        if (_disposed || !AutoDownloadNewBroadcasts || !IsLiveConnected || IsDownloading) return;
        if (_downloadPolicy.WifiOnly && !_downloadPolicy.IsUsingWifi) return;
        var downloadedIds = DownloadedBroadcasts.Select(value => value.EpisodeId).ToHashSet();
        var since = _downloadPolicy.AutoDownloadSince;
        var candidate = _metadataCache.Snapshot.Broadcasts
            .Where(value => !value.Completed && !downloadedIds.Contains(value.RepresentativeEpisodeId) &&
                            value.DateAdded >= since)
            .OrderBy(value => value.DateAdded)
            .FirstOrDefault();
        if (candidate is null) return;
        await DownloadAsync(new MobileBroadcastItem(candidate)).ConfigureAwait(false);
    }

    private async Task FlushOfflineMutationsAsync()
    {
        if (!IsLiveConnected) return;
        var result = await _offlineMutationSynchronization
            .FlushAsync(CurrentServerInstanceId())
            .ConfigureAwait(false);
        _syncDiagnostics = result.Diagnostics;
        Notify();
    }

    private async Task ReconcileMutationBroadcastAsync(WebClientLibraryBroadcastSummary summary)
    {
        ApplyLocalBroadcastSummary(summary);
        await _downloads.ReconcileSummariesAsync([summary]).ConfigureAwait(false);
        DownloadedBroadcasts = await _downloads.GetBroadcastsAsync().ConfigureAwait(false);
    }

    private MobileBroadcastItem ApplyLocalBroadcastSummary(WebClientLibraryBroadcastSummary summary)
    {
        var replacement = new MobileBroadcastItem(summary);
        _metadataCache.UpsertBroadcast(summary);
        _ = _metadataCache.SaveAsync();
        ReplaceBroadcast(summary.RepresentativeEpisodeId, replacement);
        ApplyMetadataSnapshot();
        return replacement;
    }

    private async Task RefreshSyncDiagnosticsAsync()
    {
        _syncDiagnostics = await _offlineMutationSynchronization.GetDiagnosticsAsync().ConfigureAwait(false);
        Notify();
    }

    private string CurrentServerInstanceId()
        => _server.Connection?.ServerInstanceId ?? string.Empty;

    private async Task SynchronizeDownloadedProgressWithServerAsync(DownloadedProgressSnapshot snapshot)
    {
        try
        {
            var result = await _server.SaveProgressAsync(new WebOfflineProgressUpdate(
                _server.ClientId,
                snapshot.EpisodeId,
                snapshot.PositionMs,
                snapshot.DurationMs,
                Completed: snapshot.Completed,
                Speed: snapshot.Speed,
                CapturedAt: snapshot.CapturedAt,
                AllowRewind: false,
                ExpectedGeneration: 0,
                ExplicitSeek: false,
                IncrementPlayCount: snapshot.IncrementPlayCount)).ConfigureAwait(false);
            if (result.Conflict) return;
            if (snapshot.IncrementPlayCount && result.Changed)
                _pendingPlayCountEpisodes.TryRemove(snapshot.EpisodeId, out _);
            var canonical = await _server.GetBroadcastSummaryAsync(snapshot.EpisodeId).ConfigureAwait(false);
            _metadataCache.UpsertBroadcast(canonical);
            await _metadataCache.SaveAsync().ConfigureAwait(false);
            await _downloads.ReconcileSummariesAsync([canonical]).ConfigureAwait(false);
            DownloadedBroadcasts = await _downloads.GetBroadcastsAsync().ConfigureAwait(false);
            ApplyMetadataSnapshot();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"[iOS downloaded progress sync] {exception}");
        }
    }

    private async Task SynchronizeStoredDownloadedProgressWithServerAsync()
    {
        if (!IsLiveConnected) return;
        var serverByEpisode = _metadataCache.Snapshot.Broadcasts
            .GroupBy(value => value.RepresentativeEpisodeId)
            .ToDictionary(group => group.Key, group => group.Last());
        var localDownloads = await _downloads.GetBroadcastsAsync().ConfigureAwait(false);
        foreach (var local in localDownloads)
        {
            if (!serverByEpisode.TryGetValue(local.EpisodeId, out var server)) continue;
            var localPlayedAt = local.Source.LastPlayedAt ?? DateTimeOffset.MinValue;
            var serverPlayedAt = server.LastPlayedAt ?? DateTimeOffset.MinValue;
            var hasNewerOfflineProgress = localPlayedAt > serverPlayedAt ||
                                          (serverPlayedAt == DateTimeOffset.MinValue &&
                                           (local.Source.PositionMs > server.PositionMs ||
                                            (local.Source.Completed && !server.Completed)));
            if (!hasNewerOfflineProgress) continue;
            try
            {
                var result = await _server.SaveProgressAsync(new WebOfflineProgressUpdate(
                    _server.ClientId,
                    local.EpisodeId,
                    local.Source.PositionMs,
                    Math.Max(local.Source.DurationMs, server.DurationMs),
                    Completed: local.Source.Completed,
                    Speed: _speed,
                    CapturedAt: local.Source.LastPlayedAt,
                    AllowRewind: false,
                    ExpectedGeneration: 0,
                    IncrementPlayCount: _pendingPlayCountEpisodes.ContainsKey(local.EpisodeId))).ConfigureAwait(false);
                if (result.Conflict) continue;
                if (result.Changed)
                    _pendingPlayCountEpisodes.TryRemove(local.EpisodeId, out _);
                var canonical = await _server.GetBroadcastSummaryAsync(local.EpisodeId).ConfigureAwait(false);
                _metadataCache.UpsertBroadcast(canonical);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.WriteLine($"[iOS stored downloaded progress sync] {exception}");
            }
        }
    }

    private DownloadedProgressSnapshot CaptureDownloadedProgress()
    {
        var broadcast = SelectedBroadcast ?? throw new InvalidOperationException("No downloaded broadcast is loaded.");
        var episodeId = broadcast.EpisodeId;
        var durationMs = _playbackTimeline.DurationMs;
        var positionMs = CaptureLogicalPosition();
        var completed = _playbackTimeline.IsCompleted();
        if (completed && durationMs > 0) positionMs = durationMs;
        return new DownloadedProgressSnapshot(
            episodeId,
            positionMs,
            durationMs,
            completed,
            _speed,
            DateTimeOffset.UtcNow,
            _pendingPlayCountEpisodes.ContainsKey(episodeId));
    }

    private async Task<byte[]?> LoadArtworkCoreAsync(long episodeId)
    {
        var cached = _metadataCache.ReadArtwork(episodeId);
        if (cached is { Length: > 0 }) return cached;
        if (!IsPaired || !IsLiveConnected) return null;
        try
        {
            var content = await _server.GetArtworkAsync(episodeId).ConfigureAwait(false);
            if (content.Length == 0) return null;
            await _metadataCache.SaveArtworkAsync(episodeId, content).ConfigureAwait(false);
            return content;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"[iOS artwork load] {exception}");
            return null;
        }
    }

    private MobileBroadcastItem ApplyPlaybackProgress(
        WebClientLibraryBroadcastSummary source,
        long positionMs,
        long durationMs,
        bool completed,
        DateTimeOffset playedAt)
    {
        var duration = Math.Max(source.DurationMs, Math.Max(0, durationMs));
        var position = duration > 0
            ? Math.Clamp(positionMs, 0, duration)
            : Math.Max(0, positionMs);
        var updated = source with
        {
            PositionMs = position,
            DurationMs = duration,
            Completed = completed,
            InProgress = position > 0 && !completed,
            LastPlayedAt = playedAt
        };
        var item = new MobileBroadcastItem(updated);
        try { _metadataCache.UpsertBroadcast(updated); }
        catch (Exception exception)
        {
            TracePlayback("Metadata progress cache repair failed without stopping playback: " + exception);
            System.Diagnostics.Trace.WriteLine($"[iOS playback metadata cache] {exception}");
        }
        if (SelectedBroadcast?.EpisodeId == item.EpisodeId) SelectedBroadcast = item;
        _playbackSynchronization.ReplaceRemoteBroadcast(item.EpisodeId, item);
        LibraryBroadcasts = ReplaceBroadcast(LibraryBroadcasts, item);
        RecentBroadcasts = ReplaceBroadcast(RecentBroadcasts, item);
        OnThisDay = ReplaceBroadcast(OnThisDay, item);
        UnheardBroadcasts = position > 0 || completed
            ? UnheardBroadcasts.Where(value => value.EpisodeId != item.EpisodeId).ToArray()
            : ReplaceBroadcast(UnheardBroadcasts, item);
        ContinueListening = completed || position <= 0
            ? ContinueListening.Where(value => value.EpisodeId != item.EpisodeId).ToArray()
            : new[] { item }
                .Concat(ContinueListening.Where(value => value.EpisodeId != item.EpisodeId))
                .Take(50)
                .ToArray();
        return item;
    }

    private static IReadOnlyList<MobileBroadcastItem> ReplaceBroadcast(
        IReadOnlyList<MobileBroadcastItem> values,
        MobileBroadcastItem replacement)
        => values.Select(value => value.EpisodeId == replacement.EpisodeId ? replacement : value).ToArray();

    private void ApplyMetadataSnapshot()
    {
        var snapshot = _metadataCache.Snapshot;
        var broadcasts = snapshot.Broadcasts;
        if (broadcasts.Count > 0)
        {
            TotalBroadcasts = broadcasts.Count;
            CompletedBroadcasts = broadcasts.Count(value => value.Completed);
            InProgressBroadcasts = broadcasts.Count(value => value.InProgress && !value.Completed);
            FavouriteBroadcasts = broadcasts.Count(value => value.Favourite);
            LibraryCollections = broadcasts
                .GroupBy(value => new { value.CollectionId, value.CollectionName })
                .Select(group => new WebClientLibraryCollectionSummary(
                    group.Key.CollectionId,
                    group.Key.CollectionName,
                    group.Count()))
                .OrderBy(value => value.CollectionName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            _incompleteLibraryCollections = broadcasts
                .Where(value => !value.Completed)
                .GroupBy(value => new { value.CollectionId, value.CollectionName })
                .Select(group => new WebClientLibraryCollectionSummary(
                    group.Key.CollectionId,
                    group.Key.CollectionName,
                    group.Count()))
                .OrderBy(value => value.CollectionName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            ContinueListening = Convert(broadcasts
                .Where(value => value.InProgress && !value.Completed)
                .OrderByDescending(value => value.LastPlayedAt)
                .Take(50));
            RecentBroadcasts = Convert(broadcasts
                .OrderByDescending(value => value.DateAdded)
                .Take(50));
            var today = DateTime.Today;
            OnThisDay = Convert(broadcasts
                .Where(value => value.AirDate is { } date && date.Month == today.Month && date.Day == today.Day)
                .OrderByDescending(value => value.AirDate)
                .Take(50));
            UnheardBroadcasts = Convert(broadcasts
                .Where(value => !value.Completed && !value.InProgress)
                .OrderByDescending(value => value.AirDate)
                .Take(50));
            if (LibraryBroadcasts.Count == 0)
                LibraryBroadcasts = Convert(broadcasts.OrderByDescending(value => value.AirDate).Take(250));
        }
        else if (snapshot.Overview is { } overview)
        {
            TotalBroadcasts = overview.TotalBroadcasts;
            CompletedBroadcasts = overview.CompletedBroadcasts;
            InProgressBroadcasts = overview.InProgressBroadcasts;
            FavouriteBroadcasts = overview.FavouriteBroadcasts;
            LibraryCollections = overview.Collections;
            _incompleteLibraryCollections = overview.Collections;
            ContinueListening = Convert(overview.ContinueListening);
            RecentBroadcasts = Convert(overview.RecentBroadcasts);
            OnThisDay = Convert(overview.OnThisDay);
        }
        QueueItems = snapshot.Queue;
        SavedMoments = snapshot.Moments ?? [];
        Knowledge = snapshot.Knowledge;
        Notify();
    }

    private void ApplyOnlineOverview(WebClientLibraryOverview overview)
    {
        foreach (var broadcast in overview.ContinueListening
                     .Concat(overview.RecentBroadcasts)
                     .Concat(overview.OnThisDay)
                     .GroupBy(value => value.RepresentativeEpisodeId)
                     .Select(group => group.OrderByDescending(value => value.LastPlayedAt).First()))
            _metadataCache.UpsertBroadcast(broadcast);
        TotalBroadcasts = overview.TotalBroadcasts;
        CompletedBroadcasts = overview.CompletedBroadcasts;
        InProgressBroadcasts = overview.InProgressBroadcasts;
        FavouriteBroadcasts = overview.FavouriteBroadcasts;
        LibraryCollections = overview.Collections;
        _incompleteLibraryCollections = overview.Collections;
        ContinueListening = Convert(overview.ContinueListening);
        RecentBroadcasts = Convert(overview.RecentBroadcasts);
        OnThisDay = Convert(overview.OnThisDay);
        Notify();
    }

    private IReadOnlyList<MobileBroadcastItem> QueryCachedBroadcasts(
        int? collectionId,
        string searchText,
        string filter,
        int? year,
        int? month,
        bool hideCompleted,
        string? collectionName = null)
    {
        IEnumerable<WebClientLibraryBroadcastSummary> values = _metadataCache.Snapshot.Broadcasts;
        if (!string.IsNullOrWhiteSpace(collectionName))
        {
            var collectionKey = NormalizeCollectionName(collectionName);
            values = values.Where(value => NormalizeCollectionName(value.CollectionName) == collectionKey);
        }
        else if (collectionId is > 0) values = values.Where(value => value.CollectionId == collectionId.Value);
        if (year is > 0) values = values.Where(value => value.AirDate?.Year == year.Value);
        if (month is > 0) values = values.Where(value => value.AirDate?.Month == month.Value);
        if (hideCompleted) values = values.Where(value => !value.Completed);
        values = filter.Trim().ToLowerInvariant() switch
        {
            "favourites" or "favorites" => values.Where(value => value.Favourite),
            "continuelistening" or "continue" => values.Where(value => value.InProgress && !value.Completed),
            "unplayed" => values.Where(value => !value.Completed && !value.InProgress),
            "completed" => values.Where(value => value.Completed),
            _ => values
        };
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var query = searchText.Trim();
            values = values.Where(value =>
                Contains(value.Title, query) ||
                Contains(value.Description, query) ||
                Contains(value.CollectionName, query) ||
                Contains(value.BroadcastId, query) ||
                Contains(value.SearchContext, query));
        }
        values = filter.Equals("RecentlyAdded", StringComparison.OrdinalIgnoreCase)
            ? values.OrderByDescending(value => value.DateAdded)
            : values.OrderByDescending(value => value.AirDate).ThenByDescending(value => value.DateAdded);
        return Convert(values);
    }

    private IReadOnlyList<WebClientLibraryArchivePeriodSummary> BuildCachedArchivePeriods(
        int? collectionId,
        int? year,
        bool hideCompleted,
        string? collectionName = null)
    {
        IEnumerable<WebClientLibraryBroadcastSummary> source = _metadataCache.Snapshot.Broadcasts;
        if (!string.IsNullOrWhiteSpace(collectionName))
        {
            var collectionKey = NormalizeCollectionName(collectionName);
            source = source.Where(value => NormalizeCollectionName(value.CollectionName) == collectionKey);
        }
        else if (collectionId is > 0) source = source.Where(value => value.CollectionId == collectionId.Value);
        if (hideCompleted) source = source.Where(value => !value.Completed);
        if (year is > 0) source = source.Where(value => value.AirDate?.Year == year.Value);
        var groups = year.HasValue
            ? source.Where(value => value.AirDate.HasValue).GroupBy(value => value.AirDate!.Value.Month)
            : source.Where(value => value.AirDate.HasValue).GroupBy(value => value.AirDate!.Value.Year);
        return groups
            .OrderByDescending(group => group.Key)
            .Select(group =>
            {
                var items = group.ToArray();
                var progress = items.Length == 0 ? 0 : (int)Math.Round(items.Average(value =>
                    value.Completed ? 100d : value.DurationMs <= 0 ? 0d :
                    Math.Clamp(value.PositionMs * 100d / value.DurationMs, 0d, 100d)));
                var title = year.HasValue
                    ? new DateTime(2000, group.Key, 1).ToString("MMMM")
                    : group.Key.ToString();
                return new WebClientLibraryArchivePeriodSummary(
                    group.Key,
                    title,
                    items.Length,
                    items.Count(value => value.Completed),
                    items.Count(value => value.Favourite),
                    progress,
                    $"{progress:N0}% listened",
                    $"{items.Select(value => value.CollectionName).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} show(s)",
                    items.Select(value => value.ArtworkPath).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)));
            })
            .ToArray();
    }

    private IReadOnlyList<int?> ResolveCollectionIds(int? collectionId, string? collectionName)
    {
        if (string.IsNullOrWhiteSpace(collectionName)) return [collectionId];
        var key = NormalizeCollectionName(collectionName);
        var ids = LibraryCollections
            .Where(value => NormalizeCollectionName(value.CollectionName) == key)
            .Select(value => (int?)value.CollectionId)
            .Distinct()
            .ToArray();
        return ids.Length > 0 ? ids : [collectionId];
    }

    private static IReadOnlyList<WebClientLibraryCollectionSummary> CombineCollections(
        IEnumerable<WebClientLibraryCollectionSummary> values)
        => values
            .GroupBy(value => NormalizeCollectionName(value.CollectionName))
            .Where(group => group.Key.Length > 0)
            .Select(group =>
            {
                var distinct = group.GroupBy(value => value.CollectionId).Select(value => value.First()).ToArray();
                var display = distinct.OrderByDescending(value => value.BroadcastCount)
                    .ThenBy(value => value.CollectionName, StringComparer.CurrentCultureIgnoreCase)
                    .First();
                return new WebClientLibraryCollectionSummary(
                    display.CollectionId,
                    display.CollectionName.Trim(),
                    distinct.Sum(value => value.BroadcastCount));
            })
            .OrderBy(value => value.CollectionName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private static IReadOnlyList<WebClientLibraryArchivePeriodSummary> CombineArchivePeriods(
        IEnumerable<WebClientLibraryArchivePeriodSummary> values)
        => values
            .GroupBy(value => value.Value)
            .OrderByDescending(group => group.Key)
            .Select(group =>
            {
                var periods = group.ToArray();
                var broadcasts = periods.Sum(value => value.BroadcastCount);
                var progress = broadcasts == 0 ? 0 : (int)Math.Round(periods.Sum(value => value.ProgressPercent * value.BroadcastCount) / (double)broadcasts);
                return new WebClientLibraryArchivePeriodSummary(
                    group.Key,
                    periods[0].Title,
                    broadcasts,
                    periods.Sum(value => value.CompletedCount),
                    periods.Sum(value => value.FavouriteCount),
                    progress,
                    $"{progress:N0}% listened",
                    periods[0].ShowsText,
                    periods.Select(value => value.ArtworkPath).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)));
            })
            .ToArray();

    private MobileKnowledgeSnapshot? BuildLibraryKnowledgeSnapshot()
    {
        var broadcasts = _metadataCache.Snapshot.Broadcasts;
        if (broadcasts.Count == 0) return null;
        var dated = broadcasts.Where(value => value.AirDate.HasValue).ToArray();
        var collections = CombineCollections(broadcasts
                .GroupBy(value => new { value.CollectionId, value.CollectionName })
                .Select(group => new WebClientLibraryCollectionSummary(group.Key.CollectionId, group.Key.CollectionName, group.Count())))
            .Select(value => new MobileKnowledgeCollection(value.CollectionId, value.CollectionName, value.BroadcastCount))
            .ToArray();
        var overview = new MobileKnowledgeOverview(
            broadcasts.Count,
            broadcasts.Count,
            0,
            broadcasts.Count(value => value.NeedsAttention),
            0,
            0,
            broadcasts.Count(value => !string.IsNullOrWhiteSpace(value.Description)),
            0,
            0,
            0,
            null,
            dated.Select(value => value.AirDate).Min(),
            dated.Select(value => value.AirDate).Max());
        return new MobileKnowledgeSnapshot(overview, collections, [], _metadataCache.Snapshot.UpdatedAt, true);
    }

    private MobileKnowledgeCoverage? BuildLibraryKnowledgeCoverage(int collectionId)
    {
        var collection = Knowledge?.Collections.FirstOrDefault(value => value.CollectionId == collectionId);
        var collectionName = collection?.Name ?? LibraryCollections.FirstOrDefault(value => value.CollectionId == collectionId)?.CollectionName;
        if (string.IsNullOrWhiteSpace(collectionName)) return null;
        var key = NormalizeCollectionName(collectionName);
        var broadcasts = _metadataCache.Snapshot.Broadcasts
            .Where(value => value.AirDate.HasValue && NormalizeCollectionName(value.CollectionName) == key)
            .ToArray();
        if (broadcasts.Length == 0) return null;
        var first = broadcasts.Min(value => value.AirDate!.Value);
        var last = broadcasts.Max(value => value.AirDate!.Value);
        var byDate = broadcasts.GroupBy(value => value.AirDate!.Value).ToDictionary(group => group.Key, group => group.ToArray());
        var days = new List<MobileKnowledgeCoverageDay>();
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            byDate.TryGetValue(date, out var dated);
            dated ??= [];
            var score = dated.Length == 0 ? 0 : (int)Math.Round(dated.Average(MetadataScore));
            days.Add(new MobileKnowledgeCoverageDay(
                date,
                date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                dated.Length > 0,
                false,
                false,
                dated.Length,
                score,
                dated.Length == 0 ? "No saved broadcast" : score >= 75 ? string.Empty : "More metadata can be added",
                dated.FirstOrDefault()?.RepresentativeEpisodeId,
                null));
        }
        return new MobileKnowledgeCoverage(collectionId, collectionName, first, last, days);
    }

    private static double MetadataScore(WebClientLibraryBroadcastSummary value)
    {
        var score = 25d;
        if (!string.IsNullOrWhiteSpace(value.Title)) score += 15;
        if (!string.IsNullOrWhiteSpace(value.Description)) score += 25;
        if (!string.IsNullOrWhiteSpace(value.ArtworkPath)) score += 15;
        if (value.RecordingCount > 0) score += 10;
        if (value.SegmentCount > 0) score += 10;
        return Math.Min(100, score);
    }

    private static string NormalizeCollectionName(string? value)
        => new((value ?? string.Empty).Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private async Task RefreshExploreCacheAsync(bool warmEntireCache)
    {
        if (!IsPaired || !IsLiveConnected) return;
        await _exploreSyncGate.WaitAsync().ConfigureAwait(false);
        var metadataActivity = _metadataSynchronization.BeginActivity();
        try
        {
            var overview = await _server.GetWikiOverviewAsync().ConfigureAwait(false);
            var pages = await _server.BrowseWikiAsync(limit: 5000).ConfigureAwait(false);
            var today = DateTime.Today;
            var highlights = await _server.GetWikiDashboardHighlightsAsync(today.Month, today.Day).ConfigureAwait(false);
            _metadataCache.SetExplore(overview, pages, highlights);

            var existing = _metadataCache.Snapshot.ExploreDocuments.ToDictionary(value => value.PageId);
            var candidates = warmEntireCache
                ? pages
                : pages.Where(value => value.ImageCount > 0)
                    .OrderByDescending(value => value.Status.Equals("Published", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(value => value.ImageCount)
                    .Take(8)
                    .ToArray();
            var refreshed = new List<MobileWikiPageDocument>();
            foreach (var page in candidates)
            {
                if (existing.TryGetValue(page.PageId, out var document) && document.Revision == page.Revision)
                {
                    refreshed.Add(document);
                    continue;
                }
                var loaded = await _server.GetWikiPageAsync(page.PageId).ConfigureAwait(false);
                if (loaded is not null) refreshed.Add(loaded);
            }
            _metadataCache.UpsertExploreDocuments(refreshed);

            foreach (var imageId in refreshed
                         .SelectMany(value => value.Images)
                         .Select(value => value.ImageId)
                         .Distinct())
            {
                if (_metadataCache.HasImage(imageId)) continue;
                var image = await _server.GetWikiImageAsync(imageId).ConfigureAwait(false);
                if (image is not null) await _metadataCache.SaveImageAsync(image).ConfigureAwait(false);
            }
            await _metadataCache.SaveAsync().ConfigureAwait(false);
            Notify();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"[iOS Explore cache] {exception}");
        }
        finally
        {
            metadataActivity.Dispose();
            _exploreSyncGate.Release();
        }
    }

    private async Task RefreshExplorePageAsync(Guid pageId)
    {
        try
        {
            var page = await _server.GetWikiPageAsync(pageId).ConfigureAwait(false);
            if (page is null) return;
            _metadataCache.UpsertExploreDocuments([page]);
            await LoadExploreImagesAsync(page).ConfigureAwait(false);
            await _metadataCache.SaveAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"[iOS Explore page refresh] {exception}");
        }
    }

    private MobileExploreDashboard? BuildExploreDashboard()
    {
        var snapshot = _metadataCache.Snapshot;
        if (snapshot.ExploreOverview is not { } overview ||
            snapshot.ExploreHighlights is not { } highlights) return null;
        var all = snapshot.ExplorePages;
        var featured = all
            .OrderByDescending(value => value.Status.Equals("Published", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(value => value.CitationCount + value.ImageCount + value.TimelineEventCount)
            .ThenByDescending(value => value.UpdatedAt)
            .Take(6)
            .ToArray();
        var order = featured.Select((value, index) => (value.PageId, index)).ToDictionary(value => value.PageId, value => value.index);
        var gallery = snapshot.ExploreDocuments
            .Where(value => value.Images.Count > 0)
            .OrderBy(value => order.GetValueOrDefault(value.PageId, int.MaxValue))
            .ThenBy(value => value.Title)
            .SelectMany(document => document.Images.OrderBy(link => link.SortOrder).Take(1)
                .Select(link => (document, link, bytes: _metadataCache.ReadImage(link.ImageId))))
            .Where(value => value.bytes is { Length: > 0 })
            .Take(8)
            .Select(value => ToExploreImage(value.document, value.link, value.bytes!))
            .ToArray();
        return new MobileExploreDashboard(
            overview,
            all,
            featured,
            all.OrderByDescending(value => value.UpdatedAt).Take(8).ToArray(),
            all.Where(value => value.PageType.Equals("Show", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(value => value.TimelineEventCount).ThenBy(value => value.Title).Take(10).ToArray(),
            all.Where(value => value.PageType.Equals("Person", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(value => value.CitationCount).ThenBy(value => value.Title).Take(10).ToArray(),
            all.Where(value => value.PageType.Equals("Topic", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(value => value.CitationCount).ThenBy(value => value.Title).Take(10).ToArray(),
            all.Where(value => value.PageType.Equals("Show", StringComparison.OrdinalIgnoreCase) &&
                               value.TimelineEventCount > 0)
                .OrderBy(value => value.Title, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            highlights,
            gallery);
    }

    private static MobileExploreImage ToExploreImage(
        MobileWikiPageDocument document,
        MobileWikiPageImageLink link,
        byte[] bytes)
        => new(
            link.ImageId,
            document.PageId,
            document.Title,
            string.IsNullOrWhiteSpace(link.Image?.Caption) ? document.Title : link.Image.Caption,
            string.IsNullOrWhiteSpace(link.Image?.AltText) ? document.Title : link.Image.AltText,
            link.Image?.MediaType ?? "image/jpeg",
            bytes);

    private static bool Contains(string? value, string query)
        => value?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true;

    private void ServerOnConnectivityChanged(object? sender, EventArgs eventArgs)
    {
        if (ShowsOfflineIndicator && _metadataCache.Snapshot.Broadcasts.Count > 0)
            StatusText = "Offline · showing saved Radio Vault data";
        else if (IsLiveConnected &&
                 (StatusText.StartsWith("Offline", StringComparison.OrdinalIgnoreCase) ||
                  StatusText.StartsWith("Could not reach", StringComparison.OrdinalIgnoreCase)))
            StatusText = $"Connected to {ServerName} · checking for updates";
        Notify();
        if (IsLiveConnected) _ = RetrySyncAsync();
    }

    private void CacheQueue()
    {
        _metadataCache.SetQueue(QueueItems);
        _ = _metadataCache.SaveAsync();
    }

    private void SetBusy(bool busy, string? status = null)
    {
        IsBusy = busy;
        if (status is not null) StatusText = status;
        Notify();
    }

    private void Notify() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void NotifyPlayback()
    {
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        EnsureNowPlayingArtwork();
        _nowPlaying.Update(new MobileNowPlayingSnapshot(
            NowPlayingTitle,
            NowPlayingSubtitle,
            TimeSpan.FromMilliseconds(_playbackTimeline.PositionMs),
            TimeSpan.FromMilliseconds(_playbackTimeline.DurationMs),
            _speed,
            _playback.Current.IsPlaying,
            SelectedBroadcast is not null && _playback.Current.IsOpen && _ownsPlayback,
            _nowPlayingArtwork));
    }

    private void EnsureNowPlayingArtwork()
    {
        var broadcast = SelectedBroadcast;
        if (broadcast is null)
        {
            _nowPlayingArtworkEpisodeId = 0;
            _nowPlayingArtwork = null;
            return;
        }
        if (_nowPlayingArtworkEpisodeId == broadcast.EpisodeId) return;
        _nowPlayingArtworkEpisodeId = broadcast.EpisodeId;
        _nowPlayingArtwork = null;
        _ = LoadNowPlayingArtworkAsync(broadcast);
    }

    private async Task LoadNowPlayingArtworkAsync(MobileBroadcastItem broadcast)
    {
        var artwork = await LoadArtworkAsync(broadcast).ConfigureAwait(false);
        if (_disposed || SelectedBroadcast?.EpisodeId != broadcast.EpisodeId) return;
        _nowPlayingArtwork = artwork;
        NotifyPlayback();
    }

    private void ReplaceBroadcast(long episodeId, MobileBroadcastItem replacement)
    {
        ContinueListening = Replace(ContinueListening, episodeId, replacement);
        RecentBroadcasts = Replace(RecentBroadcasts, episodeId, replacement);
        OnThisDay = Replace(OnThisDay, episodeId, replacement);
        UnheardBroadcasts = Replace(UnheardBroadcasts, episodeId, replacement);
        LibraryBroadcasts = Replace(LibraryBroadcasts, episodeId, replacement);
        DownloadedBroadcasts = Replace(DownloadedBroadcasts, episodeId, replacement);
        if (SelectedBroadcast?.EpisodeId == episodeId) SelectedBroadcast = replacement;
        _playbackSynchronization.ReplaceRemoteBroadcast(episodeId, replacement);
    }

    private static IReadOnlyList<MobileBroadcastItem> Replace(
        IReadOnlyList<MobileBroadcastItem> source,
        long episodeId,
        MobileBroadcastItem replacement)
        => source.Select(item => item.EpisodeId == episodeId ? replacement : item).ToArray();

    private static IReadOnlyList<MobileBroadcastItem> Convert(IEnumerable<WebClientLibraryBroadcastSummary> values)
        => values.Select(value => new MobileBroadcastItem(value)).ToArray();

    private static string FormatTime(TimeSpan value)
        => value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");

    private static string FormatBytes(long value)
        => value >= 1024L * 1024L * 1024L ? $"{value / (1024d * 1024d * 1024d):0.0} GB"
            : value >= 1024L * 1024L ? $"{value / (1024d * 1024d):0.0} MB"
            : $"{Math.Max(0, value) / 1024d:0} KB";

    private readonly record struct DownloadedProgressSnapshot(
        long EpisodeId,
        long PositionMs,
        long DurationMs,
        bool Completed,
        double Speed,
        DateTimeOffset CapturedAt,
        bool IncrementPlayCount);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _downloadCancelRequested = true;
        _downloadCancellation?.Cancel();
        _syncTimer.Dispose();
        _metadataSyncTimer.Dispose();
        _metadataSynchronization.Dispose();
        _offlineMutationSynchronization.Dispose();
        _playback.StateChanged -= PlaybackOnStateChanged;
        _playback.MediaEnded -= PlaybackOnMediaEnded;
        _nowPlaying.CommandReceived -= NowPlayingOnCommandReceived;
        _server.ConnectivityChanged -= ServerOnConnectivityChanged;
        _mediaProxy?.Dispose();
        _nowPlaying.Dispose();
        _playback.Dispose();
        _server.Dispose();
        _syncGate.Dispose();
        _exploreSyncGate.Dispose();
    }
}
