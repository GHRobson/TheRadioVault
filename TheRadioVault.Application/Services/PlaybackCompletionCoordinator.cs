namespace TheRadioVault.Application.Services;

/// <summary>
/// Tracks natural playback into the completion window. Deliberate seeks,
/// backend corrections and large jumps reset the natural-progress evidence so
/// they cannot inflate completion history.
/// </summary>
public sealed class PlaybackCompletionCoordinator
{
    private bool _completionCounted;
    private long _lastObservedPositionMs;
    private long _naturalPlaybackSinceSeekMs;

    public bool CompletionCounted => _completionCounted;
    public long LastObservedPositionMs => _lastObservedPositionMs;
    public long NaturalPlaybackSinceSeekMs => _naturalPlaybackSinceSeekMs;

    public void BeginSession(long initialPositionMs)
    {
        _completionCounted = false;
        ResetNaturalProgress(initialPositionMs);
    }

    public void ResetNaturalProgress(long positionMs)
    {
        _lastObservedPositionMs = Math.Max(0, positionMs);
        _naturalPlaybackSinceSeekMs = 0;
    }

    public void SetObservedPosition(long positionMs)
        => _lastObservedPositionMs = Math.Max(0, positionMs);

    public bool Observe(
        long positionMs,
        long durationMs,
        bool isPlaying,
        bool isSeeking,
        int completionThresholdSeconds)
    {
        positionMs = Math.Max(0, positionMs);
        durationMs = Math.Max(0, durationMs);

        if (!isPlaying || isSeeking || durationMs <= 0)
        {
            _lastObservedPositionMs = positionMs;
            return false;
        }

        var delta = positionMs - _lastObservedPositionMs;
        _lastObservedPositionMs = positionMs;
        if (delta >= 0 && delta <= 2_000)
            _naturalPlaybackSinceSeekMs += delta;
        else
            _naturalPlaybackSinceSeekMs = 0;

        if (_completionCounted || _naturalPlaybackSinceSeekMs < 1_000) return false;
        var remainingMs = Math.Max(0, durationMs - positionMs);
        return remainingMs <= Math.Max(0, completionThresholdSeconds) * 1_000L;
    }

    public void MarkCompleted() => _completionCounted = true;
}
