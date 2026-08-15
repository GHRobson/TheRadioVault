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

public sealed record ArchiveReconciliationChangeBreakdown(
    int MetadataCorrectionFiles,
    int MultipartCorrectionFiles,
    int BroadcastSplitFiles,
    int BroadcastMergeFiles,
    int RecoveredDateFiles,
    int NeedsAttentionFiles,
    int OtherChangedFiles)
{
    public static ArchiveReconciliationChangeBreakdown Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
    public int InterpretedDifferentlyFiles => MetadataCorrectionFiles + MultipartCorrectionFiles +
                                              BroadcastSplitFiles + BroadcastMergeFiles + RecoveredDateFiles +
                                              NeedsAttentionFiles + OtherChangedFiles;
}

public sealed record ArchiveReconciliationYearDifference(
    string Year,
    int LiveBroadcasts,
    int ProposedBroadcasts,
    int MergeGroups,
    int SplitGroups)
{
    public int Difference => ProposedBroadcasts - LiveBroadcasts;
    public string DifferenceDisplay => Difference switch
    {
        > 0 => $"+{Difference:N0} proposed broadcasts",
        < 0 => $"{Math.Abs(Difference):N0} live entries consolidate",
        _ => "No net difference"
    };
    public string DetailDisplay => $"{LiveBroadcasts:N0} live → {ProposedBroadcasts:N0} proposed · {MergeGroups:N0} merge groups · {SplitGroups:N0} split groups";
}

public sealed record ArchiveReconciliationReviewItem(
    string Identity,
    string Structure,
    string State,
    string Reason);

public sealed record ArchiveReconciliationAudit(
    ArchiveReconciliationSnapshot Snapshot,
    ArchiveReconciliationChangeBreakdown ChangeBreakdown,
    int MergeGroups,
    int SplitGroups,
    int ExactDuplicateGroups,
    int StrongDuplicateGroups,
    int SuspiciousMergeGroups,
    IReadOnlyList<ArchiveReconciliationYearDifference> YearDifferences,
    IReadOnlyList<ArchiveReconciliationReviewItem> SplitCandidates,
    IReadOnlyList<ArchiveReconciliationReviewItem> ReviewRecommended,
    IReadOnlyList<ArchiveReconciliationReviewItem> Blocked,
    int DetailLimit)
{
    public static ArchiveReconciliationAudit NotAnalysed { get; } = new(
        ArchiveReconciliationSnapshot.NotAnalysed,
        ArchiveReconciliationChangeBreakdown.Empty,
        0, 0, 0, 0, 0,
        Array.Empty<ArchiveReconciliationYearDifference>(),
        Array.Empty<ArchiveReconciliationReviewItem>(),
        Array.Empty<ArchiveReconciliationReviewItem>(),
        Array.Empty<ArchiveReconciliationReviewItem>(),
        0);
}
