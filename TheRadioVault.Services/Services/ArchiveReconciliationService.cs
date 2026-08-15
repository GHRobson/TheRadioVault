using TheRadioVault.Data.Database;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

/// <summary>
/// Stable server orchestration boundary for archive reconciliation. The existing
/// parser, evidence and recording-ranking engine remains an internal implementation
/// detail until it can be replaced incrementally without changing the server UI.
/// </summary>
public sealed class ArchiveReconciliationService
{
    private readonly LibraryTruthEngine _engine;
    private readonly ResearchDateAuthorityEvidenceService _dateAuthorityEvidence;

    public ArchiveReconciliationService(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _engine = new LibraryTruthEngine(database);
        _dateAuthorityEvidence = new ResearchDateAuthorityEvidenceService(database);
    }

    public ArchiveReconciliationSnapshot GetSnapshot()
    {
        var run = _engine.GetLatestSummary();
        if (run.RunId == 0 || !string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase))
            return ArchiveReconciliationSnapshot.NotAnalysed with
            {
                AnalysisId = run.RunId,
                AnalysisState = run.RunId == 0 ? "Not analysed" : run.Status,
                Message = run.RunId == 0 ? ArchiveReconciliationSnapshot.NotAnalysed.Message : run.Message
            };

        var adoption = _engine.GetAdoptionSummary();
        return ToSnapshot(run, adoption);
    }

    public ArchiveReconciliationSnapshot Reconcile(
        IProgress<(double Percent, string Message)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = _engine.BuildShadowIndex(progress, cancellationToken);
        return ToSnapshot(result.Summary, _engine.GetAdoptionSummary());
    }

    public ArchiveReconciliationAudit GetAudit(int detailLimit = 250)
    {
        var snapshot = GetSnapshot();
        if (!snapshot.HasCompletedAnalysis) return ArchiveReconciliationAudit.NotAnalysed;

        var limit = Math.Clamp(detailLimit, 25, 2_000);
        var run = _engine.GetLatestSummary();
        var adoption = _engine.GetAdoptionSummary();
        var dispositions = _engine.GetDispositionSummary();
        var changes = new ArchiveReconciliationChangeBreakdown(
            dispositions.MetadataCorrectionFiles,
            dispositions.MultipartCorrectionFiles,
            dispositions.BroadcastSplitFiles,
            dispositions.BroadcastMergeFiles,
            dispositions.RecoveredDateFiles,
            dispositions.NeedsAttentionFiles,
            dispositions.OtherChangedFiles);
        var years = _engine.GetYears()
            .Where(year => year.ProposedBroadcasts != year.CurrentBroadcasts || year.MergeGroups > 0 || year.SplitGroups > 0)
            .Select(year => new ArchiveReconciliationYearDifference(
                year.Year, year.CurrentBroadcasts, year.ProposedBroadcasts, year.MergeGroups, year.SplitGroups))
            .OrderByDescending(year => Math.Abs(year.Difference))
            .ThenBy(year => year.Year, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ArchiveReconciliationAudit(
            snapshot,
            changes,
            run.MergeGroups,
            run.SplitGroups,
            run.ExactDuplicateGroups,
            run.StrongDuplicateGroups,
            adoption.SuspiciousMergeGroups,
            years,
            MapReviewItems(_engine.GetBroadcasts("split-candidates", limit),
                "One live broadcast record is being interpreted as more than one canonical broadcast."),
            MapReviewItems(_engine.GetBroadcasts("review-recommended", limit),
                "This proposed broadcast should be checked before adoption."),
            MapReviewItems(_engine.GetBroadcasts("blocked", limit),
                "This proposed broadcast is blocked from automatic adoption."),
            limit);
    }

    public void ExportReport(string path, string appVersion)
        => _engine.ExportLatest(path, appVersion);

    public void ExportDateAuthorityEvidence(string path, string appVersion)
        => _dateAuthorityEvidence.ExportLatest(path, appVersion);

    private static IReadOnlyList<ArchiveReconciliationReviewItem> MapReviewItems(
        IReadOnlyList<LibraryTruthBroadcastView> broadcasts,
        string fallbackReason)
        => broadcasts.Select(broadcast => new ArchiveReconciliationReviewItem(
                broadcast.IdentityDisplay,
                broadcast.StructureDisplay,
                broadcast.AdoptionState,
                string.IsNullOrWhiteSpace(broadcast.AdoptionReason) ? fallbackReason : broadcast.AdoptionReason))
            .ToArray();

    private static ArchiveReconciliationSnapshot ToSnapshot(
        LibraryTruthRunSummary run,
        LibraryTruthAdoptionSummary adoption)
        => new(
            true,
            run.RunId,
            "Complete",
            run.CompletedAt,
            run.PhysicalFiles,
            run.CurrentBroadcasts,
            run.ProposedBroadcasts,
            run.UnchangedFiles,
            run.ChangedFiles,
            run.RecoveredDates,
            run.UnknownDates,
            run.NeedsReview,
            run.ExactDuplicateGroups + run.StrongDuplicateGroups,
            run.MultipartBroadcasts,
            adoption.AdoptionReadyTotal,
            adoption.ReviewRecommendedBroadcasts,
            adoption.BlockedBroadcasts,
            adoption.PartialRecordings + adoption.FragmentRecordings + adoption.TruncatedRecordings,
            adoption.CrossIdentityConflicts,
            run.Message);
}
