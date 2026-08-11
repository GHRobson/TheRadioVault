namespace TheRadioVault.Application.Services;

/// <summary>
/// Stabilizes projected progress between authoritative remote heartbeats. Small
/// backwards corrections within one owner generation are treated as heartbeat
/// latency; larger corrections remain visible as intentional seeks.
/// </summary>
public sealed class RemotePlaybackProgressInterpolator
{
    public const long BackwardsSeekThresholdMs = 3_000;

    private long? _episodeId;
    private long _generation = -1;
    private string _ownerDeviceId = string.Empty;
    private long _positionMs;

    public long Project(
        long? episodeId,
        long generation,
        string? ownerDeviceId,
        bool isPlaying,
        long projectedPositionMs)
    {
        var normalizedOwnerId = ownerDeviceId ?? string.Empty;
        var sameRemoteRun = isPlaying &&
            episodeId == _episodeId &&
            generation == _generation &&
            string.Equals(normalizedOwnerId, _ownerDeviceId, StringComparison.Ordinal);

        if (sameRemoteRun && projectedPositionMs >= _positionMs - BackwardsSeekThresholdMs)
            projectedPositionMs = Math.Max(projectedPositionMs, _positionMs);

        _episodeId = episodeId;
        _generation = generation;
        _ownerDeviceId = normalizedOwnerId;
        _positionMs = projectedPositionMs;
        return projectedPositionMs;
    }

    public void Reset()
    {
        _episodeId = null;
        _generation = -1;
        _ownerDeviceId = string.Empty;
        _positionMs = 0;
    }
}
