namespace TheRadioVault.Transcription.Services;

public static class TranscriptionSafety
{
    public const long LongFormVadLimitMs = 30 * 60 * 1000;

    public static bool ShouldUseVoiceActivityDetection(bool requested, long? selectedDurationMs)
        => requested && selectedDurationMs is > 0 and <= LongFormVadLimitMs;

    public static int MaximumPlausibleSpeakerCount(long durationMs)
    {
        var hours = Math.Max(1d, Math.Max(0, durationMs) / 3_600_000d);
        return Math.Clamp((int)Math.Ceiling(hours * 16d), 16, 96);
    }

    public static bool IsSpeakerCountPlausible(int speakerCount, long durationMs)
        => speakerCount >= 0 && speakerCount <= MaximumPlausibleSpeakerCount(durationMs);
}

public static class WhisperTimestampMapper
{
    public static (long StartMs, long EndMs) Map(
        long workerStartMs,
        long workerEndMs,
        long rangeStartMs,
        long? workerInputOffsetMs)
    {
        var start = Math.Max(0, workerStartMs);
        var end = Math.Max(start, workerEndMs);
        if (rangeStartMs <= 0) return (start, end);

        // When Radio Vault has already cut the selected range into a temporary WAV,
        // whisper.cpp receives that new file from zero and its offsets are always
        // relative to the cut. Map them back to the original broadcast timeline.
        if (workerInputOffsetMs == 0)
            return (start + rangeStartMs, end + rangeStartMs);

        // Direct inputs use --offset-t. Current workers return absolute timestamps,
        // while older workers returned range-relative values near zero.
        if (end <= rangeStartMs / 2)
            return (start + rangeStartMs, end + rangeStartMs);
        return (start, end);
    }
}
