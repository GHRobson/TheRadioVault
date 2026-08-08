namespace TheRadioVault.Services.Models;

/// <summary>
/// Result of promoting newly scanned legacy episode rows into the already
/// adopted canonical library. This is intentionally incremental: it never
/// rewrites or re-adopts the sealed Library Truth baseline.
/// </summary>
public sealed record CanonicalScanPromotionResult(
    int BroadcastsAdded,
    int RecordingsAdded,
    int EpisodesMapped,
    int ItemsNeedingReview)
{
    public static CanonicalScanPromotionResult Empty { get; } = new(0, 0, 0, 0);
}
