using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Client.Mobile.Platform;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile.Downloads;

/// <summary>
/// Owns download transfer state, retention policy and durable-index projection.
/// The mobile session remains responsible only for deciding when a download is
/// allowed by the wider paired/playback workflow.
/// </summary>
internal sealed class MobileDownloadCoordinator : IDisposable
{
    private readonly IMobileDownloadStore _store;
    private readonly IMobileDownloadPolicy _policy;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private MobileBroadcastItem? _pendingBroadcast;
    private bool _pauseRequested;
    private bool _cancelRequested;
    private bool _disposed;

    public MobileDownloadCoordinator(
        IMobileDownloadStore store,
        IMobileDownloadPolicy policy,
        Func<DateTimeOffset>? utcNow = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public event EventHandler? StateChanged;

    public IReadOnlyList<MobileBroadcastItem> Broadcasts { get; private set; } = [];
    public MobileDownloadStorage Storage { get; private set; } = new(0, 0, 0);
    public bool IsDownloading { get; private set; }
    public bool IsPaused { get; private set; }
    public long? ActiveEpisodeId { get; private set; }
    public int ProgressPercent { get; private set; }
    public string Status { get; private set; } = "Downloads are stored on this iPhone.";

    public bool WifiOnly
    {
        get => _policy.WifiOnly;
        set
        {
            if (_policy.WifiOnly == value) return;
            _policy.WifiOnly = value;
            Notify();
        }
    }

    public bool AutoDownloadNewBroadcasts
    {
        get => _policy.AutoDownloadNewBroadcasts;
        set
        {
            if (_policy.AutoDownloadNewBroadcasts == value) return;
            _policy.AutoDownloadNewBroadcasts = value;
            if (value)
            {
                _policy.AutoDownloadSince = _utcNow();
                _policy.AutoDownloadWatermarkEpisodeId = 0;
            }
            Notify();
        }
    }

    public bool DeleteCompletedDownloads
    {
        get => _policy.DeleteCompletedDownloads;
        set
        {
            if (_policy.DeleteCompletedDownloads == value) return;
            _policy.DeleteCompletedDownloads = value;
            Notify();
        }
    }

    public long StorageLimitBytes
    {
        get => _policy.StorageLimitBytes;
        set
        {
            var normalized = Math.Max(0, value);
            if (_policy.StorageLimitBytes == normalized) return;
            _policy.StorageLimitBytes = normalized;
            Notify();
        }
    }

    public int DownloadExpiryDays
    {
        get => _policy.DownloadExpiryDays;
        set
        {
            var normalized = value is 1 or 7 or 30 or 90 ? value : 0;
            if (_policy.DownloadExpiryDays == normalized) return;
            _policy.DownloadExpiryDays = normalized;
            Notify();
        }
    }

    public string StorageText =>
        $"{Broadcasts.Count:N0} download{(Broadcasts.Count == 1 ? string.Empty : "s")} · {FormatBytes(Storage.TotalBytes)} stored";

    public string StorageLimitText => StorageLimitBytes <= 0
        ? "No storage limit"
        : $"Up to {FormatBytes(StorageLimitBytes)}";

    public string DownloadExpiryText => DownloadExpiryDays <= 0
        ? "Never"
        : DownloadExpiryDays == 1 ? "After 1 day" : $"After {DownloadExpiryDays} days";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
        => await RefreshAsync(cancellationToken).ConfigureAwait(false);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Broadcasts = await _store.GetBroadcastsAsync(cancellationToken).ConfigureAwait(false);
        Storage = await _store.GetStorageAsync(cancellationToken).ConfigureAwait(false);
        Notify();
    }

    public async Task<MobileDownloadStorage> GetStorageAsync(CancellationToken cancellationToken = default)
    {
        Storage = await _store.GetStorageAsync(cancellationToken).ConfigureAwait(false);
        return Storage;
    }

    public Task<bool> IsDownloadedAsync(long episodeId, CancellationToken cancellationToken = default)
        => _store.IsDownloadedAsync(episodeId, cancellationToken);

    public Task<MobileDownloadRecord?> GetAsync(long episodeId, CancellationToken cancellationToken = default)
        => _store.GetAsync(episodeId, cancellationToken);

    public string GetPartUri(MobileDownloadRecord record, MobileDownloadPart part)
        => _store.GetPartUri(record, part);

    public async Task DownloadAsync(
        MobileBroadcastItem broadcast,
        Func<MobileBroadcastItem, Task>? cacheArtwork = null)
    {
        ArgumentNullException.ThrowIfNull(broadcast);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsDownloading) return;
        if (_policy.WifiOnly && !_policy.IsUsingWifi)
        {
            Status = "Connect to Wi-Fi or turn off Wi-Fi Only before downloading.";
            Notify();
            return;
        }

        _pendingBroadcast = broadcast;
        _pauseRequested = false;
        _cancelRequested = false;
        IsPaused = false;
        IsDownloading = true;
        ActiveEpisodeId = broadcast.EpisodeId;
        ProgressPercent = 0;
        Status = $"Preparing {broadcast.Title}…";
        var cancellation = new CancellationTokenSource();
        _cancellation?.Dispose();
        _cancellation = cancellation;
        Notify();
        try
        {
            var progress = new Progress<MobileDownloadProgress>(value =>
            {
                ProgressPercent = value.Percent;
                Status = value.TotalBytes > 0
                    ? $"Downloading {value.Title} · part {value.PartNumber} of {value.PartCount} · {value.Percent}%"
                    : $"Downloading {value.Title} · part {value.PartNumber} of {value.PartCount}";
                Notify();
            });
            var record = await _store
                .DownloadAsync(broadcast, progress, cancellation.Token)
                .ConfigureAwait(false);
            if (cacheArtwork is not null) await cacheArtwork(broadcast).ConfigureAwait(false);
            Broadcasts = await _store.GetBroadcastsAsync().ConfigureAwait(false);
            if (StorageLimitBytes > 0)
                await _store.TrimToLimitAsync(StorageLimitBytes, broadcast.EpisodeId).ConfigureAwait(false);
            Broadcasts = await _store.GetBroadcastsAsync().ConfigureAwait(false);
            _pendingBroadcast = null;
            ProgressPercent = 100;
            Status = $"Downloaded {broadcast.Title} · {FormatBytes(record.SizeBytes)}";
        }
        catch (OperationCanceledException) when (_pauseRequested)
        {
            IsPaused = true;
            Status = $"Paused {broadcast.Title} at {ProgressPercent}%";
        }
        catch (OperationCanceledException) when (_cancelRequested)
        {
            await _store.DiscardPendingAsync(broadcast.EpisodeId).ConfigureAwait(false);
            _pendingBroadcast = null;
            ProgressPercent = 0;
            Status = $"Cancelled {broadcast.Title}.";
        }
        catch (Exception exception)
        {
            IsPaused = true;
            Status = "Download interrupted. Tap Resume to continue: " + exception.Message;
        }
        finally
        {
            IsDownloading = false;
            if (!IsPaused) ActiveEpisodeId = null;
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
                cancellation.Dispose();
            }
            Storage = await _store.GetStorageAsync().ConfigureAwait(false);
            Notify();
        }
    }

    public void Pause()
    {
        if (!IsDownloading || _cancellation is null) return;
        _pauseRequested = true;
        Status = "Pausing after the current data write…";
        _cancellation.Cancel();
        Notify();
    }

    public async Task ResumeAsync(Func<MobileBroadcastItem, Task>? cacheArtwork = null)
    {
        if (!IsPaused || _pendingBroadcast is null || IsDownloading) return;
        await DownloadAsync(_pendingBroadcast, cacheArtwork).ConfigureAwait(false);
    }

    public void Cancel()
    {
        if (_pendingBroadcast is null) return;
        _pauseRequested = false;
        _cancelRequested = true;
        IsPaused = false;
        Status = "Cancelling download…";
        if (_cancellation is not null) _cancellation.Cancel();
        else _ = CancelPausedAsync(_pendingBroadcast);
        Notify();
    }

    public async Task<int> CleanupCompletedAsync(long? protectedEpisodeId = null)
    {
        if (IsDownloading) return 0;
        var removed = await _store.RemoveCompletedAsync(protectedEpisodeId).ConfigureAwait(false);
        await RefreshStateAsync().ConfigureAwait(false);
        Status = removed == 0
            ? "No completed downloads needed removing."
            : $"Removed {removed:N0} completed download{(removed == 1 ? string.Empty : "s")}.";
        Notify();
        return removed;
    }

    public async Task<int> RepairAsync()
    {
        if (IsDownloading) return 0;
        var removed = await _store.RepairAsync().ConfigureAwait(false);
        await RefreshStateAsync().ConfigureAwait(false);
        Status = removed == 0
            ? "All downloaded broadcasts passed their storage check."
            : $"Removed {removed:N0} damaged download{(removed == 1 ? string.Empty : "s")}; download them again when convenient.";
        Notify();
        return removed;
    }

    public async Task<int> EnforceStorageLimitAsync(long? protectedEpisodeId = null)
    {
        if (IsDownloading || StorageLimitBytes <= 0) return 0;
        var removed = await _store
            .TrimToLimitAsync(StorageLimitBytes, protectedEpisodeId)
            .ConfigureAwait(false);
        if (removed == 0) return 0;
        await RefreshStateAsync().ConfigureAwait(false);
        Status = $"Removed {removed:N0} older download{(removed == 1 ? string.Empty : "s")} to stay within the storage limit.";
        Notify();
        return removed;
    }

    public async Task<int> MaintainStorageAsync(long? protectedEpisodeId = null)
    {
        if (IsDownloading || !await _maintenanceGate.WaitAsync(0).ConfigureAwait(false)) return 0;
        try
        {
            var removed = 0;
            if (DeleteCompletedDownloads)
                removed += await _store.RemoveCompletedAsync(protectedEpisodeId).ConfigureAwait(false);
            if (DownloadExpiryDays > 0)
            {
                var cutoff = _utcNow().AddDays(-DownloadExpiryDays);
                removed += await _store.RemoveExpiredAsync(cutoff, protectedEpisodeId).ConfigureAwait(false);
            }
            if (StorageLimitBytes > 0)
                removed += await _store.TrimToLimitAsync(StorageLimitBytes, protectedEpisodeId).ConfigureAwait(false);
            if (removed > 0)
            {
                await RefreshStateAsync().ConfigureAwait(false);
                Status = $"Freed space from {removed:N0} older download{(removed == 1 ? string.Empty : "s")} using this iPhone’s storage rules.";
                Notify();
            }
            return removed;
        }
        finally { _maintenanceGate.Release(); }
    }

    public async Task<bool> RemoveAsync(MobileBroadcastItem broadcast, long? protectedEpisodeId = null)
    {
        ArgumentNullException.ThrowIfNull(broadcast);
        if (IsDownloading) return false;
        if (protectedEpisodeId == broadcast.EpisodeId)
        {
            Status = "This download is currently playing. Start another broadcast before removing it.";
            Notify();
            return false;
        }
        await _store.RemoveAsync(broadcast.EpisodeId).ConfigureAwait(false);
        await RefreshStateAsync().ConfigureAwait(false);
        Status = $"Removed {broadcast.Title} from this iPhone.";
        Notify();
        return true;
    }

    public async Task<bool> UpdateProgressAsync(
        long episodeId,
        long positionMs,
        bool completed,
        DateTimeOffset capturedAt)
    {
        var changed = await _store
            .UpdateProgressAsync(episodeId, positionMs, completed, capturedAt)
            .ConfigureAwait(false);
        if (changed) Broadcasts = await _store.GetBroadcastsAsync().ConfigureAwait(false);
        return changed;
    }

    public async Task ReconcileSummariesAsync(
        IEnumerable<WebClientLibraryBroadcastSummary> summaries,
        CancellationToken cancellationToken = default)
    {
        await _store.ReconcileSummariesAsync(summaries, cancellationToken).ConfigureAwait(false);
        Broadcasts = await _store.GetBroadcastsAsync(cancellationToken).ConfigureAwait(false);
    }

    public MobileBroadcastItem? SelectAutomaticDownload(
        IEnumerable<WebClientLibraryBroadcastSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        if (!AutoDownloadNewBroadcasts || IsDownloading || (_policy.WifiOnly && !_policy.IsUsingWifi))
            return null;
        var downloadedIds = Broadcasts.Select(value => value.EpisodeId).ToHashSet();
        var candidate = summaries
            .Where(value => !value.Completed &&
                            !downloadedIds.Contains(value.RepresentativeEpisodeId) &&
                            (value.DateAdded > _policy.AutoDownloadSince ||
                             (value.DateAdded == _policy.AutoDownloadSince &&
                              value.RepresentativeEpisodeId > _policy.AutoDownloadWatermarkEpisodeId)))
            .OrderBy(value => value.DateAdded)
            .ThenBy(value => value.RepresentativeEpisodeId)
            .FirstOrDefault();
        return candidate is null ? null : new MobileBroadcastItem(candidate);
    }

    public async Task<bool> DownloadAutomaticallyAsync(
        MobileBroadcastItem broadcast,
        Func<MobileBroadcastItem, Task>? cacheArtwork = null)
    {
        ArgumentNullException.ThrowIfNull(broadcast);
        await DownloadAsync(broadcast, cacheArtwork).ConfigureAwait(false);
        if (!await _store.IsDownloadedAsync(broadcast.EpisodeId).ConfigureAwait(false)) return false;
        _policy.AutoDownloadSince = broadcast.Source.DateAdded;
        _policy.AutoDownloadWatermarkEpisodeId = broadcast.EpisodeId;
        return true;
    }

    public void ReplaceBroadcast(long episodeId, MobileBroadcastItem replacement)
        => Broadcasts = Broadcasts
            .Select(item => item.EpisodeId == episodeId ? replacement : item)
            .ToArray();

    private async Task CancelPausedAsync(MobileBroadcastItem broadcast)
    {
        await _store.DiscardPendingAsync(broadcast.EpisodeId).ConfigureAwait(false);
        _pendingBroadcast = null;
        ActiveEpisodeId = null;
        ProgressPercent = 0;
        Status = $"Cancelled {broadcast.Title}.";
        Storage = await _store.GetStorageAsync().ConfigureAwait(false);
        Notify();
    }

    private async Task RefreshStateAsync()
    {
        Broadcasts = await _store.GetBroadcastsAsync().ConfigureAwait(false);
        Storage = await _store.GetStorageAsync().ConfigureAwait(false);
    }

    private void Notify() => StateChanged?.Invoke(this, EventArgs.Empty);

    private static string FormatBytes(long value)
        => value >= 1024L * 1024L * 1024L ? $"{value / (1024d * 1024d * 1024d):0.0} GB"
            : value >= 1024L * 1024L ? $"{value / (1024d * 1024d):0.0} MB"
            : $"{Math.Max(0, value) / 1024d:0} KB";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancelRequested = true;
        _cancellation?.Cancel();
    }
}

internal interface IMobileDownloadStore
{
    Task<IReadOnlyList<MobileBroadcastItem>> GetBroadcastsAsync(CancellationToken cancellationToken = default);
    Task<bool> IsDownloadedAsync(long episodeId, CancellationToken cancellationToken = default);
    Task<MobileDownloadRecord?> GetAsync(long episodeId, CancellationToken cancellationToken = default);
    Task<MobileDownloadRecord> DownloadAsync(
        MobileBroadcastItem broadcast,
        IProgress<MobileDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task RemoveAsync(long episodeId, CancellationToken cancellationToken = default);
    Task<int> RemoveCompletedAsync(long? protectedEpisodeId = null, CancellationToken cancellationToken = default);
    Task<int> RemoveExpiredAsync(DateTimeOffset cutoff, long? protectedEpisodeId = null, CancellationToken cancellationToken = default);
    Task<int> TrimToLimitAsync(long limitBytes, long? protectedEpisodeId = null, CancellationToken cancellationToken = default);
    Task<int> RepairAsync(CancellationToken cancellationToken = default);
    Task DiscardPendingAsync(long episodeId, CancellationToken cancellationToken = default);
    Task<MobileDownloadStorage> GetStorageAsync(CancellationToken cancellationToken = default);
    string GetPartUri(MobileDownloadRecord record, MobileDownloadPart part);
    Task<bool> UpdateProgressAsync(
        long episodeId,
        long positionMs,
        bool completed,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken = default);
    Task ReconcileSummariesAsync(
        IEnumerable<WebClientLibraryBroadcastSummary> summaries,
        CancellationToken cancellationToken = default);
}

internal sealed class MobileDownloadStore(MobileDownloadService service) : IMobileDownloadStore
{
    private readonly MobileDownloadService _service = service ?? throw new ArgumentNullException(nameof(service));

    public Task<IReadOnlyList<MobileBroadcastItem>> GetBroadcastsAsync(CancellationToken cancellationToken = default)
        => _service.GetBroadcastsAsync(cancellationToken);
    public Task<bool> IsDownloadedAsync(long episodeId, CancellationToken cancellationToken = default)
        => _service.IsDownloadedAsync(episodeId, cancellationToken);
    public Task<MobileDownloadRecord?> GetAsync(long episodeId, CancellationToken cancellationToken = default)
        => _service.GetAsync(episodeId, cancellationToken);
    public Task<MobileDownloadRecord> DownloadAsync(
        MobileBroadcastItem broadcast,
        IProgress<MobileDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => _service.DownloadAsync(broadcast, progress, cancellationToken);
    public Task RemoveAsync(long episodeId, CancellationToken cancellationToken = default)
        => _service.RemoveAsync(episodeId, cancellationToken);
    public Task<int> RemoveCompletedAsync(long? protectedEpisodeId = null, CancellationToken cancellationToken = default)
        => _service.RemoveCompletedAsync(protectedEpisodeId, cancellationToken);
    public Task<int> RemoveExpiredAsync(DateTimeOffset cutoff, long? protectedEpisodeId = null, CancellationToken cancellationToken = default)
        => _service.RemoveExpiredAsync(cutoff, protectedEpisodeId, cancellationToken);
    public Task<int> TrimToLimitAsync(long limitBytes, long? protectedEpisodeId = null, CancellationToken cancellationToken = default)
        => _service.TrimToLimitAsync(limitBytes, protectedEpisodeId, cancellationToken);
    public Task<int> RepairAsync(CancellationToken cancellationToken = default)
        => _service.RepairAsync(cancellationToken);
    public Task DiscardPendingAsync(long episodeId, CancellationToken cancellationToken = default)
        => _service.DiscardPendingAsync(episodeId, cancellationToken);
    public Task<MobileDownloadStorage> GetStorageAsync(CancellationToken cancellationToken = default)
        => _service.GetStorageAsync(cancellationToken);
    public string GetPartUri(MobileDownloadRecord record, MobileDownloadPart part)
        => _service.GetPartUri(record, part);
    public Task<bool> UpdateProgressAsync(
        long episodeId,
        long positionMs,
        bool completed,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken = default)
        => _service.UpdateProgressAsync(episodeId, positionMs, completed, capturedAt, cancellationToken);
    public Task ReconcileSummariesAsync(
        IEnumerable<WebClientLibraryBroadcastSummary> summaries,
        CancellationToken cancellationToken = default)
        => _service.ReconcileSummariesAsync(summaries, cancellationToken);
}
