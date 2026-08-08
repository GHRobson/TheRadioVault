using TheRadioVault.Application.Models;

namespace TheRadioVault.Application.Services;

/// <summary>
/// Builds a persistence-safe playback progress plan without depending on a UI,
/// database or media backend. In particular, transient backend zeroes may not
/// erase a known positive listening position.
/// </summary>
public sealed class PlaybackProgressCoordinator
{
    public PlaybackProgressPlan CreatePlan(PlaybackProgressRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var position = Math.Max(0, request.ReportedPositionMs);
        if (position == 0)
        {
            position = new[]
            {
                request.EpisodePositionMs,
                request.PlayerStatePositionMs,
                request.LastObservedPositionMs,
                request.LogicalResumePositionMs
            }.Max(value => Math.Max(0, value));
        }

        var duration = Math.Max(0, Math.Max(request.ReportedDurationMs, request.EpisodeDurationMs));
        if (duration > 0) position = Math.Min(position, duration);

        return new PlaybackProgressPlan(
            position,
            duration,
            request.Completed,
            request.Speed <= 0 ? 1d : request.Speed,
            request.IncrementPlayCount);
    }
}
