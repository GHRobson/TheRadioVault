using System.ComponentModel;
using System.Windows.Input;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Server.ViewModels;

public sealed partial class ServerSettingsViewModel
{
    private ArchiveReconciliationSnapshot _archiveReconciliation = ArchiveReconciliationSnapshot.NotAnalysed;
    private ArchiveReconciliationAudit _archiveReconciliationAudit = ArchiveReconciliationAudit.NotAnalysed;
    private string _archiveReconciliationStatusText = "Reading the latest archive reconciliation status…";
    private double _archiveReconciliationProgressPercent;
    private bool _isArchiveReconciliationBusy;
    private CancellationTokenSource? _archiveReconciliationCancellation;
    private ServerCommand? _refreshArchiveReconciliationCommand;
    private ServerCommand? _runArchiveReconciliationCommand;
    private ServerCommand? _cancelArchiveReconciliationCommand;
    private ServerCommand? _exportArchiveReconciliationReportCommand;
    private ServerCommand? _exportArchiveDateAuthorityEvidenceCommand;

    public bool HasArchiveReconciliation => _archiveReconciliation.HasCompletedAnalysis;
    public bool IsArchiveReconciliationBusy
    {
        get => _isArchiveReconciliationBusy;
        private set
        {
            if (!Set(ref _isArchiveReconciliationBusy, value)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsArchiveReconciliationIdle)));
            RaiseArchiveReconciliationCommandState();
            RaiseMediaConsolidationCommandState();
        }
    }
    public bool IsArchiveReconciliationIdle => !IsArchiveReconciliationBusy;
    public double ArchiveReconciliationProgressPercent
    {
        get => _archiveReconciliationProgressPercent;
        private set => Set(ref _archiveReconciliationProgressPercent, Math.Clamp(value, 0, 100));
    }
    public string ArchiveReconciliationStatusText
    {
        get => _archiveReconciliationStatusText;
        private set => Set(ref _archiveReconciliationStatusText, value);
    }
    public string ArchiveReconciliationStateLabel => HasArchiveReconciliation ? "INDEX READY" : "ANALYSIS NEEDED";
    public string ArchiveReconciliationStateBrush => HasArchiveReconciliation ? "#52D6A2" : "#E2A84A";
    public string ArchiveReconciliationHeadline => HasArchiveReconciliation
        ? $"Archive reconciled across {_archiveReconciliation.PhysicalFiles:N0} physical files"
        : "Build a safe physical archive inventory";
    public string ArchiveReconciliationCompletedText => HasArchiveReconciliation
        ? $"Analysis {_archiveReconciliation.AnalysisId:N0} completed {_archiveReconciliation.CompletedDisplay}"
        : "No completed reconciliation is available yet.";
    public string ArchiveReconciliationInventoryText => HasArchiveReconciliation
        ? $"{_archiveReconciliation.PhysicalFiles:N0} physical files inspected"
        : "Physical files and canonical broadcasts have not been compared yet.";
    public string ArchiveReconciliationBroadcastComparisonText => HasArchiveReconciliation
        ? $"{_archiveReconciliation.CurrentBroadcasts:N0} live broadcast records represented → {_archiveReconciliation.CanonicalBroadcasts:N0} proposed canonical broadcasts ({FormatSignedDifference(_archiveReconciliation.CanonicalBroadcasts - _archiveReconciliation.CurrentBroadcasts)})"
        : "The live and proposed broadcast totals will appear after analysis.";
    public string ArchiveReconciliationChangeText => HasArchiveReconciliation
        ? $"{_archiveReconciliation.UnchangedFiles:N0} unchanged files · {_archiveReconciliation.ProposedFileChanges:N0} files interpreted differently"
        : "The analysis is read-only and never changes live broadcasts or media.";
    public string ArchiveReconciliationFileBreakdownText => HasArchiveReconciliation
        ? $"{_archiveReconciliationAudit.ChangeBreakdown.MetadataCorrectionFiles:N0} metadata · {_archiveReconciliationAudit.ChangeBreakdown.MultipartCorrectionFiles:N0} multipart · {_archiveReconciliationAudit.ChangeBreakdown.BroadcastSplitFiles:N0} split · {_archiveReconciliationAudit.ChangeBreakdown.BroadcastMergeFiles:N0} merge · {_archiveReconciliationAudit.ChangeBreakdown.RecoveredDateFiles:N0} recovered dates · {_archiveReconciliationAudit.ChangeBreakdown.NeedsAttentionFiles:N0} needs attention · {_archiveReconciliationAudit.ChangeBreakdown.OtherChangedFiles:N0} other"
        : "File-level change categories will appear after analysis.";
    public string ArchiveReconciliationStructureText => HasArchiveReconciliation
        ? $"{_archiveReconciliationAudit.MergeGroups:N0} broadcast merge groups · {_archiveReconciliationAudit.SplitGroups:N0} broadcast split groups · {_archiveReconciliation.MultipartBroadcasts:N0} multipart broadcasts"
        : "Merge, split and multipart groups will appear after analysis.";
    public string ArchiveReconciliationAttentionText => HasArchiveReconciliation
        ? $"{_archiveReconciliation.ReadyBroadcasts:N0} ready · {_archiveReconciliation.ReviewRecommendedBroadcasts:N0} review · {_archiveReconciliation.BlockedBroadcasts:N0} blocked"
        : "Review and blocked counts will appear after analysis.";
    public string ArchiveReconciliationDuplicateText => HasArchiveReconciliation
        ? $"{_archiveReconciliationAudit.ExactDuplicateGroups:N0} exact duplicate groups · {_archiveReconciliationAudit.StrongDuplicateGroups:N0} strong duplicate groups · {_archiveReconciliation.PartialOrDamagedRecordings:N0} partial or damaged recordings"
        : "Duplicate and recording-quality evidence will appear here.";
    public string ArchiveReconciliationUnresolvedText => HasArchiveReconciliation
        ? $"{_archiveReconciliation.UnknownDates:N0} unresolved dates · {_archiveReconciliation.IdentityConflicts:N0} identity conflicts · {_archiveReconciliationAudit.SuspiciousMergeGroups:N0} suspicious merge groups"
        : "Unresolved evidence will appear after analysis.";
    public IReadOnlyList<ArchiveReconciliationYearDifference> ArchiveReconciliationYearDifferences
        => _archiveReconciliationAudit.YearDifferences;
    public IReadOnlyList<ArchiveReconciliationReviewItem> ArchiveReconciliationSplitCandidates
        => _archiveReconciliationAudit.SplitCandidates;
    public IReadOnlyList<ArchiveReconciliationReviewItem> ArchiveReconciliationReviewRecommended
        => _archiveReconciliationAudit.ReviewRecommended;
    public IReadOnlyList<ArchiveReconciliationReviewItem> ArchiveReconciliationBlocked
        => _archiveReconciliationAudit.Blocked;
    public string ArchiveReconciliationYearDifferenceSummary => HasArchiveReconciliation
        ? $"{_archiveReconciliationAudit.YearDifferences.Count:N0} year groups contain a net difference, merge or split."
        : "Year-by-year differences will appear after analysis.";
    public string ArchiveReconciliationSplitCandidateSummary => HasArchiveReconciliation
        ? $"Showing up to {_archiveReconciliationAudit.DetailLimit:N0} proposed identities involved in {_archiveReconciliationAudit.SplitGroups:N0} split groups."
        : "Split candidates will appear after analysis.";
    public string ArchiveReconciliationReviewSummary => HasArchiveReconciliation
        ? $"Showing up to {_archiveReconciliationAudit.DetailLimit:N0} of {_archiveReconciliation.ReviewRecommendedBroadcasts:N0} review-recommended broadcasts."
        : "Review cases will appear after analysis.";
    public string ArchiveReconciliationBlockedSummary => HasArchiveReconciliation
        ? $"Showing up to {_archiveReconciliationAudit.DetailLimit:N0} of {_archiveReconciliation.BlockedBroadcasts:N0} blocked broadcasts."
        : "Blocked cases will appear after analysis.";
    public string ArchiveReconciliationDashboardText => HasArchiveReconciliation
        ? $"{_archiveReconciliation.CanonicalBroadcasts:N0} canonical broadcasts · {_archiveReconciliation.AttentionTotal:N0} need attention · last run {_archiveReconciliation.CompletedDisplay}"
        : "Archive reconciliation has not been run. Consolidation will build a fresh inventory before it can prepare a plan.";

    public ICommand RefreshArchiveReconciliationCommand { get; private set; } = null!;
    public ICommand RunArchiveReconciliationCommand { get; private set; } = null!;
    public ICommand CancelArchiveReconciliationCommand { get; private set; } = null!;
    public ICommand ExportArchiveReconciliationReportCommand { get; private set; } = null!;
    public ICommand ExportArchiveDateAuthorityEvidenceCommand { get; private set; } = null!;

    private void InitializeArchiveReconciliationCommands()
    {
        _refreshArchiveReconciliationCommand = new ServerCommand(
            () => _ = RefreshArchiveReconciliationAsync(),
            () => !IsArchiveReconciliationBusy);
        _runArchiveReconciliationCommand = new ServerCommand(
            () => _ = RunArchiveReconciliationAsync(),
            () => !IsArchiveReconciliationBusy && !IsMediaConsolidationBusy);
        _cancelArchiveReconciliationCommand = new ServerCommand(
            CancelArchiveReconciliation,
            () => IsArchiveReconciliationBusy);
        _exportArchiveReconciliationReportCommand = new ServerCommand(
            () => _ = ExportArchiveReconciliationReportAsync(),
            () => HasArchiveReconciliation && !IsArchiveReconciliationBusy);
        _exportArchiveDateAuthorityEvidenceCommand = new ServerCommand(
            () => _ = ExportArchiveDateAuthorityEvidenceAsync(),
            () => HasArchiveReconciliation && _archiveReconciliation.UnknownDates > 0 && !IsArchiveReconciliationBusy);
        RefreshArchiveReconciliationCommand = _refreshArchiveReconciliationCommand;
        RunArchiveReconciliationCommand = _runArchiveReconciliationCommand;
        CancelArchiveReconciliationCommand = _cancelArchiveReconciliationCommand;
        ExportArchiveReconciliationReportCommand = _exportArchiveReconciliationReportCommand;
        ExportArchiveDateAuthorityEvidenceCommand = _exportArchiveDateAuthorityEvidenceCommand;
    }

    private async Task RefreshArchiveReconciliationAsync()
    {
        if (_runtime is null || IsArchiveReconciliationBusy) return;
        try
        {
            var audit = await Task.Run(() => _runtime.GetArchiveReconciliationAudit()).ConfigureAwait(true);
            ApplyArchiveReconciliationAudit(audit);
            ArchiveReconciliationStatusText = audit.Snapshot.Message;
        }
        catch (Exception exception)
        {
            ArchiveReconciliationStatusText = $"Archive reconciliation status could not be read: {exception.Message}";
        }
    }

    private async Task RunArchiveReconciliationAsync()
    {
        if (_runtime is null || IsArchiveReconciliationBusy) return;
        _archiveReconciliationCancellation?.Dispose();
        _archiveReconciliationCancellation = new CancellationTokenSource();
        var cancellationToken = _archiveReconciliationCancellation.Token;
        var progress = new Progress<(double Percent, string Message)>(value =>
        {
            ArchiveReconciliationProgressPercent = value.Percent;
            ArchiveReconciliationStatusText = value.Message;
        });
        IsArchiveReconciliationBusy = true;
        ArchiveReconciliationProgressPercent = 0;
        ArchiveReconciliationStatusText = "Starting a fresh read-only analysis of the physical archive…";
        try
        {
            var snapshot = await Task.Run(
                () => _runtime.ReconcileArchive(progress, cancellationToken),
                cancellationToken).ConfigureAwait(true);
            var audit = await Task.Run(() => _runtime.GetArchiveReconciliationAudit(), cancellationToken).ConfigureAwait(true);
            ApplyArchiveReconciliationAudit(audit);
            ArchiveReconciliationProgressPercent = 100;
            ArchiveReconciliationStatusText = snapshot.Message;
        }
        catch (OperationCanceledException)
        {
            ArchiveReconciliationStatusText = "Archive reconciliation cancelled. The live library and media were not changed.";
        }
        catch (Exception exception)
        {
            ArchiveReconciliationStatusText = exception.Message;
        }
        finally
        {
            IsArchiveReconciliationBusy = false;
        }
    }

    private void ApplyArchiveReconciliationAudit(ArchiveReconciliationAudit audit)
    {
        _archiveReconciliationAudit = audit;
        _archiveReconciliation = audit.Snapshot;
        foreach (var property in new[]
                 {
                     nameof(HasArchiveReconciliation), nameof(ArchiveReconciliationStateLabel),
                     nameof(ArchiveReconciliationStateBrush), nameof(ArchiveReconciliationHeadline),
                     nameof(ArchiveReconciliationCompletedText), nameof(ArchiveReconciliationInventoryText),
                     nameof(ArchiveReconciliationBroadcastComparisonText), nameof(ArchiveReconciliationChangeText),
                     nameof(ArchiveReconciliationFileBreakdownText), nameof(ArchiveReconciliationStructureText),
                     nameof(ArchiveReconciliationAttentionText), nameof(ArchiveReconciliationDuplicateText),
                     nameof(ArchiveReconciliationUnresolvedText), nameof(ArchiveReconciliationYearDifferences),
                     nameof(ArchiveReconciliationSplitCandidates), nameof(ArchiveReconciliationReviewRecommended),
                     nameof(ArchiveReconciliationBlocked), nameof(ArchiveReconciliationYearDifferenceSummary),
                     nameof(ArchiveReconciliationSplitCandidateSummary), nameof(ArchiveReconciliationReviewSummary),
                     nameof(ArchiveReconciliationBlockedSummary), nameof(ArchiveReconciliationDashboardText)
                 })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        RaiseArchiveReconciliationCommandState();
    }

    private async Task ExportArchiveReconciliationReportAsync()
    {
        if (_runtime is null || _knowledgeFiles is null || !HasArchiveReconciliation || IsArchiveReconciliationBusy) return;
        var path = await _knowledgeFiles.PickReconciliationReportExportAsync(
            $"RadioVault-Archive-Reconciliation-{DateTime.Now:yyyyMMdd-HHmmss}.trvreconcile.json").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return;

        IsArchiveReconciliationBusy = true;
        ArchiveReconciliationStatusText = "Exporting the complete reconciliation evidence report…";
        try
        {
            await Task.Run(() => _runtime.ExportArchiveReconciliationReport(path)).ConfigureAwait(true);
            ArchiveReconciliationStatusText = $"Reconciliation report exported to {path}";
        }
        catch (Exception exception)
        {
            ArchiveReconciliationStatusText = $"Reconciliation report could not be exported: {exception.Message}";
        }
        finally
        {
            IsArchiveReconciliationBusy = false;
        }
    }

    private async Task ExportArchiveDateAuthorityEvidenceAsync()
    {
        if (_runtime is null || _knowledgeFiles is null || !HasArchiveReconciliation ||
            _archiveReconciliation.UnknownDates <= 0 || IsArchiveReconciliationBusy) return;
        var path = await _knowledgeFiles.PickDateAuthorityEvidenceExportAsync(
            $"RadioVault-Date-Authority-Evidence-{DateTime.Now:yyyyMMdd-HHmmss}.trvdateevidence.json").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return;

        IsArchiveReconciliationBusy = true;
        ArchiveReconciliationStatusText = "Exporting unresolved-date authority evidence…";
        try
        {
            await Task.Run(() => _runtime.ExportArchiveDateAuthorityEvidence(path)).ConfigureAwait(true);
            ArchiveReconciliationStatusText = $"Date-authority evidence exported to {path}";
        }
        catch (Exception exception)
        {
            ArchiveReconciliationStatusText = $"Date-authority evidence could not be exported: {exception.Message}";
        }
        finally
        {
            IsArchiveReconciliationBusy = false;
        }
    }

    private void CancelArchiveReconciliation()
        => _archiveReconciliationCancellation?.Cancel();

    private void RaiseArchiveReconciliationCommandState()
    {
        _refreshArchiveReconciliationCommand?.RaiseCanExecuteChanged();
        _runArchiveReconciliationCommand?.RaiseCanExecuteChanged();
        _cancelArchiveReconciliationCommand?.RaiseCanExecuteChanged();
        _exportArchiveReconciliationReportCommand?.RaiseCanExecuteChanged();
        _exportArchiveDateAuthorityEvidenceCommand?.RaiseCanExecuteChanged();
    }

    private static string FormatSignedDifference(int value)
        => value > 0 ? $"+{value:N0}" : value.ToString("N0");

    private void DisposeArchiveReconciliation()
    {
        _archiveReconciliationCancellation?.Cancel();
        _archiveReconciliationCancellation?.Dispose();
        _archiveReconciliationCancellation = null;
    }
}
