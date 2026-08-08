using TheRadioVault.Web.Models;

namespace TheRadioVault.Web.Services;

/// <summary>
/// Single-flight prepare/verify/commit state machine for shared playback.
/// It never changes playback ownership itself; the host applies the returned
/// commit only after the target decoder has proved that it is ready and aligned.
/// </summary>
public sealed class PlaybackTransferCoordinator
{
    private static readonly TimeSpan TransferLifetime = TimeSpan.FromSeconds(45);
    private const long CommitToleranceMs = 3_000;
    private WebPlaybackTransferTicket? _pending;

    public WebPlaybackTransferTicket? Pending(DateTimeOffset now)
    {
        Expire(now);
        return _pending;
    }

    public WebPlaybackTransferTicket Begin(
        WebPlaybackTransferBeginRequest request,
        PlaybackTransferAuthority source,
        DateTimeOffset now)
    {
        Expire(now);
        if (string.IsNullOrWhiteSpace(request.ClientId))
            throw new PlaybackTransferConflictException("A valid target device identity is required.");
        if (request.EpisodeId <= 0)
            throw new PlaybackTransferConflictException("A target broadcast is required.");

        if (_pending is not null)
        {
            // Begin is idempotent for the same target/broadcast/source generation.
            // This recovers safely when the server created the ticket but its HTTP
            // response was lost before the target received the transfer ID.
            if (string.Equals(_pending.TargetClientId, request.ClientId.Trim(), StringComparison.Ordinal) &&
                _pending.TargetEpisodeId == request.EpisodeId)
            {
                ValidateSource(_pending, source);
                return _pending;
            }

            throw new PlaybackTransferConflictException(
                "A playback move is already being prepared. Wait for it to finish or cancel it before trying again.");
        }

        var sameBroadcast = source.EpisodeId == request.EpisodeId;
        var protectedPosition = sameBroadcast
            ? Math.Max(Math.Max(0, request.PositionMs), source.ProjectedPositionMs(now))
            : Math.Max(0, request.PositionMs);
        var duration = Math.Max(0, request.DurationMs > 0 ? request.DurationMs : sameBroadcast ? source.DurationMs : 0);
        if (duration > 0) protectedPosition = Math.Clamp(protectedPosition, 0, duration);

        _pending = new WebPlaybackTransferTicket(
            Guid.NewGuid(),
            request.ClientId.Trim(),
            NormalizeDeviceName(request.DeviceName, request.DeviceKind),
            NormalizeDeviceKind(request.DeviceKind),
            request.EpisodeId,
            protectedPosition,
            protectedPosition,
            duration,
            Math.Clamp(request.Speed > 0 ? request.Speed : source.Speed, 0.5d, 3d),
            request.DesiredPlaying,
            DesiredPlayingOverridden: false,
            source.OwnerDevice,
            source.OwnerClientId,
            source.EpisodeId,
            source.Generation,
            ReadyRevision: 0,
            IsReady: false,
            StartedAt: now,
            ExpiresAt: now.Add(TransferLifetime));
        return _pending;
    }

    public WebPlaybackTransferTicket MarkReady(
        WebPlaybackTransferReadyRequest request,
        PlaybackTransferAuthority source,
        DateTimeOffset now)
    {
        var pending = Require(request.ClientId, request.TransferId, now);
        ValidateSource(pending, source);
        if (!request.DecoderReady)
            throw new PlaybackTransferConflictException("The target decoder is not ready to accept playback.");

        var sameBroadcast = source.EpisodeId == pending.TargetEpisodeId;
        var commitPosition = sameBroadcast
            ? Math.Max(pending.ProtectedPositionMs, source.ProjectedPositionMs(now))
            : pending.ProtectedPositionMs;
        var duration = Math.Max(pending.DurationMs, request.PreparedDurationMs);
        if (sameBroadcast) duration = Math.Max(duration, source.DurationMs);
        if (duration > 0) commitPosition = Math.Clamp(commitPosition, 0, duration);

        _pending = pending with
        {
            CommitPositionMs = commitPosition,
            DurationMs = duration,
            DesiredPlaying = request.OverrideDesiredPlaying
                ? request.DesiredPlaying
                : sameBroadcast ? source.IsPlaying : request.DesiredPlaying,
            DesiredPlayingOverridden = pending.DesiredPlayingOverridden || request.OverrideDesiredPlaying,
            Speed = sameBroadcast
                ? Math.Clamp(source.Speed, 0.5d, 3d)
                : Math.Clamp(request.Speed, 0.5d, 3d),
            ReadyRevision = pending.ReadyRevision + 1,
            IsReady = true,
            ExpiresAt = now.Add(TransferLifetime)
        };
        return _pending;
    }

    public WebPlaybackTransferTicket Commit(
        WebPlaybackTransferCommitRequest request,
        PlaybackTransferAuthority source,
        DateTimeOffset now)
    {
        var pending = Require(request.ClientId, request.TransferId, now);
        ValidateSource(pending, source);
        if (!pending.IsReady || request.ReadyRevision != pending.ReadyRevision)
            throw new PlaybackTransferConflictException("The target must re-confirm that its decoder is ready.");
        // The provider can retain its raw "Server" owner sentinel while the
        // public session correctly reports None. With no source episode and no
        // running output, there is nothing to overlap regardless of that stale
        // internal label.
        var sourceHasNoOutput = source.EpisodeId is null or <= 0 && !source.IsPlaying;
        if (pending.DesiredPlaying && !request.DecoderRunningMuted &&
            !(request.DecoderRunningAudibly && sourceHasNoOutput))
            throw new PlaybackTransferConflictException("The target decoder has not proved that it can play the broadcast.");

        var sameBroadcast = source.EpisodeId == pending.TargetEpisodeId;
        if (sameBroadcast)
        {
            var latestSourcePosition = source.ProjectedPositionMs(now);
            if (Math.Abs(latestSourcePosition - pending.CommitPositionMs) > CommitToleranceMs)
                throw new PlaybackTransferConflictException(
                    "The source playhead changed while the target was preparing. Align the target again before committing.");
            if (!pending.DesiredPlayingOverridden && source.IsPlaying != pending.DesiredPlaying)
                throw new PlaybackTransferConflictException(
                    "The source play/pause state changed while the target was preparing. Re-confirm the target before committing.");
            if (Math.Abs(Math.Clamp(source.Speed, 0.5d, 3d) - pending.Speed) > 0.01d)
                throw new PlaybackTransferConflictException(
                    "The source playback speed changed while the target was preparing. Re-confirm the target before committing.");
        }

        var prepared = Math.Max(0, request.PreparedPositionMs);
        if (pending.DurationMs > 0) prepared = Math.Clamp(prepared, 0, pending.DurationMs);
        if (Math.Abs(prepared - pending.CommitPositionMs) > CommitToleranceMs)
            throw new PlaybackTransferConflictException(
                "The source playhead moved while the target was preparing. Align the target again before committing.");
        if (pending.CommitPositionMs >= 10_000 && prepared < pending.CommitPositionMs - CommitToleranceMs)
            throw new PlaybackTransferConflictException("A transfer cannot replace established progress with a startup position.");

        var committed = pending with { CommitPositionMs = prepared };
        _pending = null;
        return committed;
    }

    public bool Cancel(WebPlaybackTransferCancelRequest request, DateTimeOffset now)
    {
        Expire(now);
        if (_pending is null) return false;
        if (_pending.TransferId != request.TransferId ||
            !string.Equals(_pending.TargetClientId, request.ClientId, StringComparison.Ordinal))
            return false;
        _pending = null;
        return true;
    }

    public void Restore(WebPlaybackTransferTicket ticket, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        _pending = ticket with { ExpiresAt = now.Add(TransferLifetime) };
    }

    public void Clear() => _pending = null;

    private WebPlaybackTransferTicket Require(string clientId, Guid transferId, DateTimeOffset now)
    {
        Expire(now);
        if (_pending is null || _pending.TransferId != transferId ||
            !string.Equals(_pending.TargetClientId, clientId, StringComparison.Ordinal))
            throw new PlaybackTransferConflictException("This playback move is no longer active.");
        return _pending;
    }

    private static void ValidateSource(WebPlaybackTransferTicket pending, PlaybackTransferAuthority source)
    {
        if (source.Generation != pending.SourceGeneration ||
            source.EpisodeId != pending.SourceEpisodeId ||
            !string.Equals(source.OwnerClientId, pending.SourceOwnerClientId, StringComparison.Ordinal) ||
            !string.Equals(source.OwnerDevice, pending.SourceOwnerDevice, StringComparison.OrdinalIgnoreCase))
            throw new PlaybackTransferConflictException("Playback changed on the source device while the target was preparing.");
    }

    private void Expire(DateTimeOffset now)
    {
        if (_pending is not null && now >= _pending.ExpiresAt) _pending = null;
    }

    private static string NormalizeDeviceName(string? name, string? kind)
        => !string.IsNullOrWhiteSpace(name) ? name.Trim()
            : string.Equals(kind, "DesktopClient", StringComparison.OrdinalIgnoreCase) ? "Radio Vault desktop"
            : "Phone";

    private static string NormalizeDeviceKind(string? kind)
        => string.IsNullOrWhiteSpace(kind) ? "Phone" : kind.Trim();
}

public sealed record PlaybackTransferAuthority(
    string OwnerDevice,
    string OwnerClientId,
    long Generation,
    long? EpisodeId,
    long PositionMs,
    long DurationMs,
    double Speed,
    bool IsPlaying,
    DateTimeOffset UpdatedAt)
{
    public long ProjectedPositionMs(DateTimeOffset now)
    {
        var position = Math.Max(0, PositionMs);
        if (IsPlaying && UpdatedAt != default)
        {
            var elapsed = (now - UpdatedAt).TotalMilliseconds;
            if (elapsed > 0 && elapsed <= 15_000)
                position += (long)Math.Round(elapsed * Math.Clamp(Speed, 0.5d, 3d));
        }
        return DurationMs > 0 ? Math.Clamp(position, 0, DurationMs) : position;
    }
}

public sealed class PlaybackTransferConflictException : InvalidOperationException
{
    public PlaybackTransferConflictException(string message) : base(message) { }
}
