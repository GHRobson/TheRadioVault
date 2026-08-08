using TheRadioVault.Core.Domain;

namespace TheRadioVault.Core.Services;

public static class PlaybackProgressService
{
    public static double CalculatePercent(long positionMs, long durationMs)
        => durationMs > 0 ? Math.Clamp((double)positionMs / durationMs * 100d, 0d, 100d) : 0d;

    public static ListeningStatus GetStatus(long positionMs, long durationMs, bool completed = false)
    {
        if (completed || IsCompletionThresholdReached(positionMs, durationMs)) return ListeningStatus.Completed;
        return positionMs > 0 ? ListeningStatus.InProgress : ListeningStatus.Unplayed;
    }

    public static bool IsCompletionThresholdReached(long positionMs, long durationMs, double percentThreshold = 95d, TimeSpan? finalWindow = null)
    {
        if (durationMs <= 0 || positionMs <= 0) return false;
        var percent = CalculatePercent(positionMs, durationMs);
        var remaining = durationMs - positionMs;
        var window = finalWindow ?? TimeSpan.FromMinutes(5);
        return percent >= percentThreshold ||
               (percent >= 80d && remaining <= window.TotalMilliseconds);
    }

    public static string GetDashboardAction(long positionMs, long durationMs, bool completed = false, bool isPlaying = false)
    {
        if (isPlaying) return "Pause";
        return GetStatus(positionMs, durationMs, completed) switch
        {
            ListeningStatus.Completed => "Play Again",
            ListeningStatus.InProgress => "Resume",
            _ => "Play"
        };
    }
}
