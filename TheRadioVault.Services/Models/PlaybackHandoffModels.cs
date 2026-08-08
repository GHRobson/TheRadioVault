namespace TheRadioVault.Services.Models;

public sealed record PlaybackDeviceState(
    string DeviceId,
    string DisplayName,
    string Kind,
    long? RepresentativeEpisodeId,
    string Show,
    string Title,
    long PositionMs,
    long DurationMs,
    double Speed,
    bool IsPlaying,
    bool IsOwner,
    bool IsOnline,
    DateTimeOffset UpdatedAt)
{
    public string PlaybackStateText => !RepresentativeEpisodeId.HasValue
        ? "Idle"
        : IsPlaying ? "Playing" : PositionMs > 0 ? "Paused" : "Ready";

    /// <summary>
    /// Projects the last server-confirmed playhead to the requested instant. This
    /// keeps inactive devices visually aligned between network heartbeats without
    /// increasing durable database-write frequency.
    /// </summary>
    public long ProjectedPositionMs(DateTimeOffset now)
    {
        var position = Math.Max(0, PositionMs);
        if (IsPlaying && UpdatedAt != default)
        {
            var elapsedMs = (now - UpdatedAt).TotalMilliseconds;
            // Ignore negative clock skew and cap projection after a suspended or
            // disconnected endpoint; the next authoritative snapshot will correct it.
            if (elapsedMs > 0 && elapsedMs <= 15_000)
                position += (long)Math.Round(elapsedMs * Math.Clamp(Speed, 0.5d, 3d));
        }

        return DurationMs > 0
            ? Math.Clamp(position, 0, DurationMs)
            : position;
    }
}

public sealed record PlaybackHandoffSnapshot(
    string CurrentDeviceId,
    string CurrentDeviceName,
    string OwnerDeviceId,
    string OwnerDeviceName,
    long Generation,
    PlaybackDeviceState? ActivePlayback,
    IReadOnlyList<PlaybackDeviceState> Devices,
    DateTimeOffset RefreshedAt)
{
    public bool HasActivePlayback => ActivePlayback?.RepresentativeEpisodeId is > 0;
    public bool IsOwnedByCurrentDevice => !string.IsNullOrWhiteSpace(CurrentDeviceId) &&
        string.Equals(CurrentDeviceId, OwnerDeviceId, StringComparison.Ordinal);
    public bool IsPlayingElsewhere => HasActivePlayback && !IsOwnedByCurrentDevice;
    public PlaybackTransferCommitReceipt? CommittedTransfer { get; init; }
}

public sealed record PlaybackTransferCommitReceipt(
    Guid TransferId,
    string SourceClientId,
    string SourceDeviceName,
    string TargetClientId,
    string TargetDeviceName,
    long Generation,
    bool SourceWasPlaying,
    bool SourceStopAcknowledged,
    DateTimeOffset CommittedAt,
    DateTimeOffset? SourceStoppedAt);

/// <summary>
/// Client-side projection of a server-issued transactional playback ticket.
/// The source device remains active until this ticket is committed.
/// </summary>
public sealed record PlaybackTransferPlan(
    Guid TransferId,
    long TargetEpisodeId,
    long ProtectedPositionMs,
    long CommitPositionMs,
    long DurationMs,
    double Speed,
    bool DesiredPlaying,
    bool DesiredPlayingOverridden,
    string SourceOwnerDevice,
    string SourceOwnerClientId,
    long SourceGeneration,
    long ReadyRevision,
    bool IsReady,
    DateTimeOffset ExpiresAt);
