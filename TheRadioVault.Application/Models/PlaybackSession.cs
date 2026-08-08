using TheRadioVault.Core.Playback;

namespace TheRadioVault.Application.Models;

public sealed record PlaybackSessionSnapshot(
    long? BroadcastId,
    PlaybackStatus Status,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan? Duration,
    double Volume,
    double Speed,
    string? MediaPath)
{
    public static PlaybackSessionSnapshot From(long? broadcastId, PlaybackEngineSnapshot engine)
        => new(
            broadcastId,
            engine.Status,
            engine.IsPlaying,
            engine.Position,
            engine.Duration,
            engine.Volume,
            engine.Speed,
            engine.MediaPath);
}

public sealed record PlaybackProgressRequest(
    long ReportedPositionMs,
    long EpisodePositionMs,
    long PlayerStatePositionMs,
    long LastObservedPositionMs,
    long LogicalResumePositionMs,
    long ReportedDurationMs,
    long EpisodeDurationMs,
    bool Completed,
    double Speed,
    bool IncrementPlayCount);

public sealed record PlaybackProgressPlan(
    long PositionMs,
    long DurationMs,
    bool Completed,
    double Speed,
    bool IncrementPlayCount);
