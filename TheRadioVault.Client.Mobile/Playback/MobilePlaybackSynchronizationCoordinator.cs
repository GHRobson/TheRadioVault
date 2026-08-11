using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Client.Mobile.Platform;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile.Playback;

/// <summary>
/// Coordinates the observable side of shared playback: loading and projecting
/// another device's playhead, and stopping this decoder after a committed move.
/// The session remains responsible for presenting the resulting state to the UI.
/// </summary>
internal sealed class MobilePlaybackSynchronizationCoordinator
{
    private static readonly TimeSpan MaximumProjectionAge = TimeSpan.FromSeconds(15);
    private readonly IMobilePlaybackSynchronizationTransport _transport;
    private readonly IMobilePlaybackEngine _playback;
    private readonly MobilePlaybackOwnershipCoordinator _ownership;
    private readonly Func<DateTimeOffset> _utcNow;

    public MobilePlaybackSynchronizationCoordinator(
        IMobilePlaybackSynchronizationTransport transport,
        IMobilePlaybackEngine playback,
        MobilePlaybackOwnershipCoordinator ownership,
        Func<DateTimeOffset>? utcNow = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public MobileBroadcastItem? RemoteBroadcast { get; private set; }
    public string RemoteOwner { get; private set; } = string.Empty;

    public bool ClearRemotePlayback()
    {
        if (RemoteBroadcast is null && string.IsNullOrEmpty(RemoteOwner)) return false;
        RemoteBroadcast = null;
        RemoteOwner = string.Empty;
        return true;
    }

    public void ReplaceRemoteBroadcast(long episodeId, MobileBroadcastItem replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (RemoteBroadcast?.EpisodeId == episodeId) RemoteBroadcast = replacement;
    }

    public Task<MobilePlaybackObservation> ObserveSafelyAsync(
        WebPlaybackSession session,
        bool hasLocalBroadcast,
        bool decoderIsOpen,
        bool ownsPlayback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (hasLocalBroadcast && decoderIsOpen && ownsPlayback &&
            _ownership.HasActivePlayback(session) &&
            !_ownership.IsOwnedByThisDevice(session) &&
            !_ownership.WasCommittedAwayFromThisDevice(session))
        {
            return Task.FromResult(MobilePlaybackObservation.Unchanged(RemoteBroadcast));
        }

        return ObserveAsync(session, cancellationToken);
    }

    public async Task<MobilePlaybackObservation> ObserveAsync(
        WebPlaybackSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_ownership.HasActivePlayback(session) || _ownership.IsOwnedByThisDevice(session))
            return new MobilePlaybackObservation(ClearRemotePlayback(), null);

        var owner = _ownership.OwnerName(session);
        var episodeId = session.Player.EpisodeId.GetValueOrDefault();
        var changed = RemoteBroadcast?.EpisodeId != episodeId ||
                      !string.Equals(RemoteOwner, owner, StringComparison.Ordinal);
        if (RemoteBroadcast?.EpisodeId != episodeId)
        {
            try
            {
                RemoteBroadcast = new MobileBroadcastItem(
                    await _transport.GetBroadcastSummaryAsync(episodeId, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                RemoteBroadcast = null;
            }
        }

        if (RemoteBroadcast is { } remote)
        {
            var projectedPosition = ProjectPosition(session.Player);
            var projectedDuration = Math.Max(remote.Source.DurationMs, session.Player.DurationMs);
            var completed = string.Equals(
                session.Player.Status,
                "Completed",
                StringComparison.OrdinalIgnoreCase);
            changed |= remote.Source.PositionMs != projectedPosition ||
                       remote.Source.DurationMs != projectedDuration ||
                       remote.Source.Completed != completed;
            var duration = Math.Max(remote.Source.DurationMs, Math.Max(0, projectedDuration));
            var position = duration > 0
                ? Math.Clamp(projectedPosition, 0, duration)
                : Math.Max(0, projectedPosition);
            RemoteBroadcast = new MobileBroadcastItem(remote.Source with
            {
                PositionMs = position,
                DurationMs = duration,
                Completed = completed,
                InProgress = position > 0 && !completed,
                LastPlayedAt = session.Player.UpdatedAt ?? _utcNow()
            });
        }

        RemoteOwner = owner;
        return new MobilePlaybackObservation(changed, RemoteBroadcast);
    }

    public async Task<MobilePlaybackSourceStopResult> StopForCommittedTransferAsync(
        WebPlaybackSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!RequiresSourceStop(session))
            return MobilePlaybackSourceStopResult.NotStopped;

        var receipt = session.CommittedTransfer!;
        _playback.Pause();
        _playback.SetMuted(false);
        await _transport.AcknowledgePlaybackSourceStoppedAsync(
            new WebPlaybackTransferSourceStoppedRequest(
                _transport.ClientId,
                receipt.TransferId,
                receipt.Generation),
            cancellationToken).ConfigureAwait(false);
        return new MobilePlaybackSourceStopResult(
            true,
            $"Playback moved to {receipt.TargetDeviceName}");
    }

    public bool RequiresSourceStop(WebPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _ownership.NeedsSourceStopAcknowledgement(session);
    }

    public long ProjectPosition(WebPlaybackState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var position = Math.Max(0, state.PositionMs);
        if (state.IsPlaying && state.UpdatedAt is { } updated)
        {
            var elapsed = _utcNow() - updated;
            if (elapsed > TimeSpan.Zero && elapsed <= MaximumProjectionAge)
                position += (long)Math.Round(elapsed.TotalMilliseconds * Math.Clamp(state.Speed, 0.5d, 3d));
        }

        return state.DurationMs > 0 ? Math.Clamp(position, 0, state.DurationMs) : position;
    }
}

internal readonly record struct MobilePlaybackObservation(
    bool Changed,
    MobileBroadcastItem? Broadcast)
{
    public static MobilePlaybackObservation Unchanged(MobileBroadcastItem? broadcast)
        => new(false, broadcast);
}

internal readonly record struct MobilePlaybackSourceStopResult(
    bool Stopped,
    string Status)
{
    public static MobilePlaybackSourceStopResult NotStopped => new(false, string.Empty);
}

internal interface IMobilePlaybackSynchronizationTransport
{
    string ClientId { get; }

    Task<WebClientLibraryBroadcastSummary> GetBroadcastSummaryAsync(
        long episodeId,
        CancellationToken cancellationToken = default);

    Task AcknowledgePlaybackSourceStoppedAsync(
        WebPlaybackTransferSourceStoppedRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed class MobilePlaybackSynchronizationTransport(
    MobileServerClient server) : IMobilePlaybackSynchronizationTransport
{
    private readonly MobileServerClient _server = server ?? throw new ArgumentNullException(nameof(server));

    public string ClientId => _server.ClientId;

    public Task<WebClientLibraryBroadcastSummary> GetBroadcastSummaryAsync(
        long episodeId,
        CancellationToken cancellationToken = default)
        => _server.GetBroadcastSummaryAsync(episodeId, cancellationToken);

    public Task AcknowledgePlaybackSourceStoppedAsync(
        WebPlaybackTransferSourceStoppedRequest request,
        CancellationToken cancellationToken = default)
        => _server.AcknowledgePlaybackSourceStoppedAsync(request, cancellationToken);
}
