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

    public ArchiveReconciliationService(SqliteDatabase database)
        => _engine = new LibraryTruthEngine(database ?? throw new ArgumentNullException(nameof(database)));

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
