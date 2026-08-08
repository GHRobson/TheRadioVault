using System.IO;
using System.Text.Json;

namespace TheRadioVault.Services;

public sealed class PlaybackPreferences
{
    public int SkipBackSeconds { get; set; } = 30;
    public int SkipForwardSeconds { get; set; } = 30;

    /// <summary>
    /// Remaining playback time at which naturally advancing playback is treated
    /// as complete. A short fixed window is more suitable than a percentage for
    /// long-form radio broadcasts.
    /// </summary>
    public int CompletionThresholdSeconds { get; set; } = 5;
}

public static class PlaybackPreferencesService
{
    private static readonly string FilePath = Path.Combine(AppPaths.DataDirectory, "playback.json");
    private static readonly int[] AllowedIntervals = [10, 15, 30, 60];
    private static readonly int[] AllowedCompletionThresholds = [1, 2, 5, 10, 30, 60];

    public static PlaybackPreferences Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new PlaybackPreferences();
            var value = JsonSerializer.Deserialize<PlaybackPreferences>(File.ReadAllText(FilePath))
                        ?? new PlaybackPreferences();
            value.SkipBackSeconds = Normalise(value.SkipBackSeconds);
            value.SkipForwardSeconds = Normalise(value.SkipForwardSeconds);
            value.CompletionThresholdSeconds = NormaliseCompletionThreshold(value.CompletionThresholdSeconds);
            return value;
        }
        catch
        {
            return new PlaybackPreferences();
        }
    }

    public static void Save(int skipBackSeconds, int skipForwardSeconds, int completionThresholdSeconds = 5)
    {
        var value = new PlaybackPreferences
        {
            SkipBackSeconds = Normalise(skipBackSeconds),
            SkipForwardSeconds = Normalise(skipForwardSeconds),
            CompletionThresholdSeconds = NormaliseCompletionThreshold(completionThresholdSeconds)
        };

        Directory.CreateDirectory(AppPaths.DataDirectory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }

    private static int Normalise(int value) => AllowedIntervals.Contains(value) ? value : 30;
    private static int NormaliseCompletionThreshold(int value)
        => AllowedCompletionThresholds.Contains(value) ? value : 5;
}
