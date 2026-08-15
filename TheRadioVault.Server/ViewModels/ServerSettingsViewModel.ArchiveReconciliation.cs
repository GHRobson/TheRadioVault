using System.ComponentModel;
using System.Windows.Input;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Server.ViewModels;

public sealed partial class ServerSettingsViewModel
{
    private ArchiveReconciliationSnapshot _archiveReconciliation = ArchiveReconciliationSnapshot.NotAnalysed;
    private string _archiveReconciliationStatusText = "Reading the latest archive reconciliation status…";
    private double _archiveReconciliationProgressPercent;
    private bool _isArchiveReconciliationBusy;
    private CancellationTokenSource? _archiveReconciliationCancellation;
    private ServerCommand? _refreshArchiveReconciliationCommand;
    private ServerCommand? _runArchiveReconciliationCommand;
    private ServerCommand? _cancelArchiveReconciliationCommand;

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
        ? $"{_archiveReconciliation.PhysicalFiles:N0} physical files · {_archiveReconciliation.CanonicalBroadcasts:N0} canonical broadcasts"
        : "Physical files and canonical broadcasts have not been compared yet.";
    public string ArchiveReconciliationChangeText => HasArchiveReconciliation
        ? $"{_archiveReconciliation.UnchangedFiles:N0} unchanged · {_archiveReconciliation.ProposedFileChanges:N0} proposed corrections · {_archiveReconciliation.RecoveredDates:N0} dates recovered"
        : "The analysis is read-only and never changes live broadcasts or media.";
    public string ArchiveReconciliationAttentionText => HasArchiveReconciliation
        ? $"{_archiveReconciliation.ReadyBroadcasts:N0} ready · {_archiveReconciliation.ReviewRecommendedBroadcasts:N0} review · {_archiveReconciliation.BlockedBroadcasts:N0} blocked"
        : "Review and blocked counts will appear after analysis.";
    public string ArchiveReconciliationDuplicateText => HasArchiveReconciliation
        ? $"{_archiveReconciliation.DuplicateGroups:N0} duplicate groups · {_archiveReconciliation.PartialOrDamagedRecordings:N0} partial or damaged recordings · {_archiveReconciliation.IdentityConflicts:N0} identity conflicts"
        : "Duplicate and recording-quality evidence will appear here.";
    public string ArchiveReconciliationDashboardText => HasArchiveReconciliation
        ? $"{_archiveReconciliation.CanonicalBroadcasts:N0} canonical broadcasts · {_archiveReconciliation.AttentionTotal:N0} need attention · last run {_archiveReconciliation.CompletedDisplay}"
        : "Archive reconciliation has not been run. Consolidation will build a fresh inventory before it can prepare a plan.";

    public ICommand RefreshArchiveReconciliationCommand { get; private set; } = null!;
    public ICommand RunArchiveReconciliationCommand { get; private set; } = null!;
    public ICommand CancelArchiveReconciliationCommand { get; private set; } = null!;

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
        RefreshArchiveReconciliationCommand = _refreshArchiveReconciliationCommand;
        RunArchiveReconciliationCommand = _runArchiveReconciliationCommand;
        CancelArchiveReconciliationCommand = _cancelArchiveReconciliationCommand;
    }

    private async Task RefreshArchiveReconciliationAsync()
    {
        if (_runtime is null || IsArchiveReconciliationBusy) return;
        try
        {
            var snapshot = await Task.Run(_runtime.GetArchiveReconciliationSnapshot).ConfigureAwait(true);
            ApplyArchiveReconciliationSnapshot(snapshot);
            ArchiveReconciliationStatusText = snapshot.Message;
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
            ApplyArchiveReconciliationSnapshot(snapshot);
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

    private void ApplyArchiveReconciliationSnapshot(ArchiveReconciliationSnapshot snapshot)
    {
        _archiveReconciliation = snapshot;
        foreach (var property in new[]
                 {
                     nameof(HasArchiveReconciliation), nameof(ArchiveReconciliationStateLabel),
                     nameof(ArchiveReconciliationStateBrush), nameof(ArchiveReconciliationHeadline),
                     nameof(ArchiveReconciliationCompletedText), nameof(ArchiveReconciliationInventoryText),
                     nameof(ArchiveReconciliationChangeText), nameof(ArchiveReconciliationAttentionText),
                     nameof(ArchiveReconciliationDuplicateText), nameof(ArchiveReconciliationDashboardText)
                 })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    private void CancelArchiveReconciliation()
        => _archiveReconciliationCancellation?.Cancel();

    private void RaiseArchiveReconciliationCommandState()
    {
        _refreshArchiveReconciliationCommand?.RaiseCanExecuteChanged();
        _runArchiveReconciliationCommand?.RaiseCanExecuteChanged();
        _cancelArchiveReconciliationCommand?.RaiseCanExecuteChanged();
    }

    private void DisposeArchiveReconciliation()
    {
        _archiveReconciliationCancellation?.Cancel();
        _archiveReconciliationCancellation?.Dispose();
        _archiveReconciliationCancellation = null;
    }
}
