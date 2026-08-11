using TheRadioVault.Client.Mobile.Downloads;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile.Synchronization;

/// <summary>
/// Reconciles progress captured against downloaded media with the authoritative
/// server without allowing an older server snapshot to rewind offline listening.
/// </summary>
internal sealed class MobileDownloadedProgressSynchronizationCoordinator(
    IMobileDownloadedProgressTransport transport,
    MobileDownloadCoordinator downloads)
{
    private readonly IMobileDownloadedProgressTransport _transport =
        transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly MobileDownloadCoordinator _downloads =
        downloads ?? throw new ArgumentNullException(nameof(downloads));

    public async Task<bool> SynchronizeCurrentAsync(
        MobileDownloadedProgressSnapshot snapshot,
        Action<long> acknowledgePlayCount,
        Func<WebClientLibraryBroadcastSummary, CancellationToken, Task> adoptCanonical,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledgePlayCount);
        ArgumentNullException.ThrowIfNull(adoptCanonical);
        try
        {
            var result = await _transport.SaveProgressAsync(
                ToUpdate(snapshot), cancellationToken).ConfigureAwait(false);
            if (result.Conflict) return false;
            if (snapshot.IncrementPlayCount && result.Changed)
                acknowledgePlayCount(snapshot.EpisodeId);
            var canonical = await _transport
                .GetBroadcastSummaryAsync(snapshot.EpisodeId, cancellationToken)
                .ConfigureAwait(false);
            await adoptCanonical(canonical, cancellationToken).ConfigureAwait(false);
            await _downloads.ReconcileSummariesAsync([canonical], cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"[iOS downloaded progress sync] {exception}");
            return false;
        }
    }

    public async Task<int> SynchronizeStoredAsync(
        IEnumerable<WebClientLibraryBroadcastSummary> serverSummaries,
        double speed,
        Func<long, bool> shouldIncrementPlayCount,
        Action<long> acknowledgePlayCount,
        Action<WebClientLibraryBroadcastSummary> stageCanonical,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverSummaries);
        ArgumentNullException.ThrowIfNull(shouldIncrementPlayCount);
        ArgumentNullException.ThrowIfNull(acknowledgePlayCount);
        ArgumentNullException.ThrowIfNull(stageCanonical);
        var serverByEpisode = serverSummaries
            .GroupBy(value => value.RepresentativeEpisodeId)
            .ToDictionary(group => group.Key, group => group.Last());
        var synchronized = 0;
        foreach (var local in _downloads.Broadcasts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!serverByEpisode.TryGetValue(local.EpisodeId, out var server) ||
                !HasNewerOfflineProgress(local.Source, server)) continue;
            try
            {
                var incrementPlayCount = shouldIncrementPlayCount(local.EpisodeId);
                var result = await _transport.SaveProgressAsync(
                    new WebOfflineProgressUpdate(
                        _transport.ClientId,
                        local.EpisodeId,
                        local.Source.PositionMs,
                        Math.Max(local.Source.DurationMs, server.DurationMs),
                        Completed: local.Source.Completed,
                        Speed: speed,
                        CapturedAt: local.Source.LastPlayedAt,
                        AllowRewind: false,
                        ExpectedGeneration: 0,
                        IncrementPlayCount: incrementPlayCount),
                    cancellationToken).ConfigureAwait(false);
                if (result.Conflict) continue;
                if (incrementPlayCount && result.Changed) acknowledgePlayCount(local.EpisodeId);
                var canonical = await _transport
                    .GetBroadcastSummaryAsync(local.EpisodeId, cancellationToken)
                    .ConfigureAwait(false);
                stageCanonical(canonical);
                synchronized++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.WriteLine($"[iOS stored downloaded progress sync] {exception}");
            }
        }
        return synchronized;
    }

    internal static bool HasNewerOfflineProgress(
        WebClientLibraryBroadcastSummary local,
        WebClientLibraryBroadcastSummary server)
    {
        var localPlayedAt = local.LastPlayedAt ?? DateTimeOffset.MinValue;
        var serverPlayedAt = server.LastPlayedAt ?? DateTimeOffset.MinValue;
        return localPlayedAt > serverPlayedAt ||
               (serverPlayedAt == DateTimeOffset.MinValue &&
                (local.PositionMs > server.PositionMs || (local.Completed && !server.Completed)));
    }

    private WebOfflineProgressUpdate ToUpdate(MobileDownloadedProgressSnapshot snapshot)
        => new(
            _transport.ClientId,
            snapshot.EpisodeId,
            snapshot.PositionMs,
            snapshot.DurationMs,
            Completed: snapshot.Completed,
            Speed: snapshot.Speed,
            CapturedAt: snapshot.CapturedAt,
            AllowRewind: false,
            ExpectedGeneration: 0,
            ExplicitSeek: false,
            IncrementPlayCount: snapshot.IncrementPlayCount);
}

internal readonly record struct MobileDownloadedProgressSnapshot(
    long EpisodeId,
    long PositionMs,
    long DurationMs,
    bool Completed,
    double Speed,
    DateTimeOffset CapturedAt,
    bool IncrementPlayCount);

internal interface IMobileDownloadedProgressTransport
{
    string ClientId { get; }
    Task<WebOfflineProgressResult> SaveProgressAsync(
        WebOfflineProgressUpdate update,
        CancellationToken cancellationToken = default);
    Task<WebClientLibraryBroadcastSummary> GetBroadcastSummaryAsync(
        long episodeId,
        CancellationToken cancellationToken = default);
}

internal sealed class MobileDownloadedProgressTransport(
    MobileServerClient server) : IMobileDownloadedProgressTransport
{
    private readonly MobileServerClient _server = server ?? throw new ArgumentNullException(nameof(server));

    public string ClientId => _server.ClientId;

    public Task<WebOfflineProgressResult> SaveProgressAsync(
        WebOfflineProgressUpdate update,
        CancellationToken cancellationToken = default)
        => _server.SaveProgressAsync(update, cancellationToken);

    public Task<WebClientLibraryBroadcastSummary> GetBroadcastSummaryAsync(
        long episodeId,
        CancellationToken cancellationToken = default)
        => _server.GetBroadcastSummaryAsync(episodeId, cancellationToken);
}
