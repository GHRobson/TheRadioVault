using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile.Synchronization;

/// <summary>
/// Owns serialized synchronization of the server's library change feed into
/// the durable mobile metadata cache. Download, Explore and UI reconciliation
/// remain explicit consumers of the resulting cache snapshot.
/// </summary>
internal sealed class MobileMetadataSynchronizationCoordinator : IDisposable
{
    private const int CompleteLibraryPageSize = 10_000;
    private readonly IMobileMetadataSynchronizationTransport _transport;
    private readonly MobileMetadataCache _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action _stateChanged;
    private readonly Func<DateTimeOffset> _utcNow;
    private int _activity;
    private bool _disposed;

    public MobileMetadataSynchronizationCoordinator(
        IMobileMetadataSynchronizationTransport transport,
        MobileMetadataCache cache,
        Action stateChanged,
        Func<DateTimeOffset>? utcNow = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _stateChanged = stateChanged ?? throw new ArgumentNullException(nameof(stateChanged));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public bool IsSynchronizing => Volatile.Read(ref _activity) > 0;
    public DateTimeOffset? LastSuccessfulSynchronizationAt { get; private set; }

    public Task LoadAsync(string serverInstanceId)
        => _cache.LoadAsync(serverInstanceId);

    public IDisposable BeginActivity()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Increment(ref _activity) == 1) _stateChanged();
        return new ActivityLease(this);
    }

    public async Task<MobileMetadataSynchronizationResult> SynchronizeLibraryAsync(
        bool forceExploreRefresh = false,
        Func<MobileMetadataSynchronizationResult, CancellationToken, Task>? afterCacheApplied = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var activity = BeginActivity();
            var snapshot = _cache.Snapshot;
            var sync = await _transport.GetLibrarySyncAsync(
                snapshot.SyncSessionId,
                snapshot.SyncSequence,
                snapshot.SyncRevision,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<WebClientLibraryBroadcastSummary>? completeLibrary = null;
            var changed = new List<WebClientLibraryBroadcastSummary>();
            var deleted = new HashSet<long>();
            var episodeIds = sync.Changes
                .Where(value => value.EpisodeId is > 0)
                .Select(value => value.EpisodeId!.Value)
                .Distinct()
                .ToArray();

            if (sync.ResetRequired || snapshot.Broadcasts.Count == 0)
            {
                completeLibrary = await FetchCompleteLibraryAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (!sync.NoChanges)
            {
                foreach (var episodeId in episodeIds)
                {
                    try
                    {
                        changed.Add(await _transport
                            .GetBroadcastSummaryAsync(episodeId, cancellationToken)
                            .ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        deleted.Add(episodeId);
                    }
                }
            }

            var libraryChanged = sync.ResetRequired || snapshot.Overview is null || !sync.NoChanges;
            var overview = libraryChanged
                ? await _transport.GetOverviewAsync(cancellationToken).ConfigureAwait(false)
                : null;
            _cache.ApplyLibrarySync(sync, completeLibrary, changed, deleted, overview);
            await _cache.SaveAsync().ConfigureAwait(false);

            var exploreRefreshRequired = forceExploreRefresh || snapshot.ExploreOverview is null ||
                                         sync.Changes.Any(value =>
                                             value.Kind.Equals("wiki", StringComparison.OrdinalIgnoreCase) ||
                                             value.Kind.Equals("research", StringComparison.OrdinalIgnoreCase));
            var result = new MobileMetadataSynchronizationResult(
                _cache.Snapshot,
                exploreRefreshRequired,
                completeLibrary is not null);
            if (afterCacheApplied is not null)
                await afterCacheApplied(result, cancellationToken).ConfigureAwait(false);
            LastSuccessfulSynchronizationAt = _utcNow();
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MobileMetadataCacheSnapshot> BootstrapEmptyCacheAsync(
        string serverInstanceId,
        WebClientLibraryOverview overview,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverInstanceId);
        ArgumentNullException.ThrowIfNull(overview);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var activity = BeginActivity();
            if (_cache.Snapshot.Broadcasts.Count > 0) return _cache.Snapshot;
            var completeLibrary = await FetchCompleteLibraryAsync(cancellationToken).ConfigureAwait(false);
            _cache.ReplaceCompleteLibrary(serverInstanceId, completeLibrary, overview);
            await _cache.SaveAsync().ConfigureAwait(false);
            return _cache.Snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<WebClientLibraryBroadcastSummary>> FetchCompleteLibraryAsync(
        CancellationToken cancellationToken)
    {
        var values = new List<WebClientLibraryBroadcastSummary>();
        var offset = 0;
        while (true)
        {
            var page = await _transport.BrowseAsync(
                CompleteLibraryPageSize,
                offset,
                cancellationToken).ConfigureAwait(false);
            values.AddRange(page.Broadcasts);
            offset += page.Broadcasts.Count;
            if (page.Broadcasts.Count == 0 || offset >= page.TotalMatching) break;
        }
        return values;
    }

    private void EndActivity()
    {
        if (Interlocked.Decrement(ref _activity) == 0) _stateChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    private sealed class ActivityLease(MobileMetadataSynchronizationCoordinator owner) : IDisposable
    {
        private MobileMetadataSynchronizationCoordinator? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndActivity();
    }
}

internal sealed record MobileMetadataSynchronizationResult(
    MobileMetadataCacheSnapshot Snapshot,
    bool ExploreRefreshRequired,
    bool CompleteLibraryReloaded);

internal interface IMobileMetadataSynchronizationTransport
{
    Task<MobileLibrarySync> GetLibrarySyncAsync(
        string sessionId,
        long sequence,
        string revision,
        CancellationToken cancellationToken = default);

    Task<WebClientLibraryBrowseResult> BrowseAsync(
        int limit,
        int offset,
        CancellationToken cancellationToken = default);

    Task<WebClientLibraryBroadcastSummary> GetBroadcastSummaryAsync(
        long episodeId,
        CancellationToken cancellationToken = default);

    Task<WebClientLibraryOverview> GetOverviewAsync(CancellationToken cancellationToken = default);
}

internal sealed class MobileMetadataSynchronizationTransport(
    MobileServerClient server) : IMobileMetadataSynchronizationTransport
{
    private readonly MobileServerClient _server = server ?? throw new ArgumentNullException(nameof(server));

    public Task<MobileLibrarySync> GetLibrarySyncAsync(
        string sessionId,
        long sequence,
        string revision,
        CancellationToken cancellationToken = default)
        => _server.GetLibrarySyncAsync(sessionId, sequence, revision, cancellationToken);

    public Task<WebClientLibraryBrowseResult> BrowseAsync(
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
        => _server.BrowseAsync(
            string.Empty,
            limit: limit,
            offset: offset,
            cancellationToken: cancellationToken);

    public Task<WebClientLibraryBroadcastSummary> GetBroadcastSummaryAsync(
        long episodeId,
        CancellationToken cancellationToken = default)
        => _server.GetBroadcastSummaryAsync(episodeId, cancellationToken);

    public Task<WebClientLibraryOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        => _server.GetOverviewAsync(cancellationToken);
}
