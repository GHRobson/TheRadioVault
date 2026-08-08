using System.Text.Json;

namespace TheRadioVault.Services;

/// <summary>
/// Keeps one tiny, atomic recovery record for the most recently active broadcast.
/// It is deliberately separate from the SQLite database so a crash, forced close,
/// or temporarily blocked database writer cannot silently discard the last known
/// listening position. The journal only ever restores positive/newer progress.
/// </summary>
public static class PlaybackRecoveryJournalService
{
    private static readonly object Gate = new();
    private static string JournalPath => Path.Combine(AppPaths.DataDirectory, "playback-recovery.json");

    public static void Save(
        long episodeId,
        string? canonicalKey,
        string? broadcastUid,
        long positionMs,
        long durationMs,
        bool completed,
        double playbackSpeed)
    {
        if (episodeId <= 0 || positionMs <= 0) return;
        var entry = new PlaybackRecoveryEntry(
            episodeId,
            canonicalKey?.Trim() ?? string.Empty,
            broadcastUid?.Trim() ?? string.Empty,
            Math.Max(0, positionMs),
            Math.Max(0, durationMs),
            completed,
            playbackSpeed > 0 ? playbackSpeed : 1d,
            DateTime.UtcNow);

        lock (Gate)
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var path = JournalPath;
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entry));
            File.Move(temp, path, overwrite: true);
        }
    }

    public static bool Restore(DatabaseService database)
    {
        ArgumentNullException.ThrowIfNull(database);
        PlaybackRecoveryEntry? entry;
        lock (Gate)
        {
            if (!File.Exists(JournalPath)) return false;
            try
            {
                entry = JsonSerializer.Deserialize<PlaybackRecoveryEntry>(File.ReadAllText(JournalPath));
            }
            catch
            {
                return false;
            }
        }

        if (entry is null || entry.PositionMs <= 0) return false;
        var targetEpisodeId = database.ResolvePlaybackRecoveryEpisodeId(entry.EpisodeId, entry.CanonicalKey, entry.BroadcastUid);
        if (!targetEpisodeId.HasValue) return false;
        var current = database.GetPlaybackState(targetEpisodeId.Value);
        var journalIsNewer = !current.LastPlayedAt.HasValue || entry.SavedAtUtc > current.LastPlayedAt.Value.ToUniversalTime();
        var progressWasLost = current.PositionMs == 0 || entry.PositionMs > current.PositionMs;
        var completionWasLost = entry.Completed && !current.Completed;
        if (!journalIsNewer || (!progressWasLost && !completionWasLost)) return false;

        database.SavePlaybackState(
            targetEpisodeId.Value,
            Math.Max(current.PositionMs, entry.PositionMs),
            Math.Max(current.DurationMs, entry.DurationMs),
            current.Completed || entry.Completed,
            entry.PlaybackSpeed,
            allowPositionReset: false);
        return true;
    }


    public static object ReadDiagnosticSummary()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(JournalPath)) return new { available = false };
                var entry = JsonSerializer.Deserialize<PlaybackRecoveryEntry>(File.ReadAllText(JournalPath));
                if (entry is null) return new { available = false };
                return new
                {
                    available = true,
                    entry.EpisodeId,
                    hasCanonicalKey = !string.IsNullOrWhiteSpace(entry.CanonicalKey),
                    hasBroadcastUid = !string.IsNullOrWhiteSpace(entry.BroadcastUid),
                    entry.PositionMs,
                    entry.DurationMs,
                    entry.Completed,
                    entry.PlaybackSpeed,
                    entry.SavedAtUtc
                };
            }
            catch (Exception ex) { return new { available = false, error = ex.Message }; }
        }
    }

    private sealed record PlaybackRecoveryEntry(
        long EpisodeId,
        string CanonicalKey,
        string BroadcastUid,
        long PositionMs,
        long DurationMs,
        bool Completed,
        double PlaybackSpeed,
        DateTime SavedAtUtc);
}
