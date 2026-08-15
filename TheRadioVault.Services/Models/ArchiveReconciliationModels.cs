namespace TheRadioVault.Services.Models;

/// <summary>
/// Server-facing status for the non-destructive archive reconciliation index.
/// This deliberately hides the legacy Library Truth implementation from UI code.
/// </summary>
public sealed record ArchiveReconciliationSnapshot(
    bool HasCompletedAnalysis,
    long AnalysisId,
    string AnalysisState,
    DateTimeOffset? CompletedAt,
    int PhysicalFiles,
    int CurrentBroadcasts,
    int CanonicalBroadcasts,
    int UnchangedFiles,
    int ProposedFileChanges,
    int RecoveredDates,
    int UnknownDates,
    int NeedsReview,
    int DuplicateGroups,
    int MultipartBroadcasts,
    int ReadyBroadcasts,
    int ReviewRecommendedBroadcasts,
    int BlockedBroadcasts,
    int PartialOrDamagedRecordings,
    int IdentityConflicts,
    string Message)
{
    public static ArchiveReconciliationSnapshot NotAnalysed { get; } = new(
        false, 0, "Not analysed", null,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
        "Run archive reconciliation to build a non-destructive view of every physical recording.");

    public int AttentionTotal => ReviewRecommendedBroadcasts + BlockedBroadcasts;
    public string CompletedDisplay => CompletedAt?.ToLocalTime().ToString("g") ?? "Never";
}
