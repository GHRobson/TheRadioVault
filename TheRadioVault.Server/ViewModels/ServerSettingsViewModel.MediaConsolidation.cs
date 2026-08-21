using System.ComponentModel;
using System.Windows.Input;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Server.ViewModels;

public sealed partial class ServerSettingsViewModel
{
    private string _managedArchivePath = string.Empty;
    private string _consolidationQuarantinePath = string.Empty;
    private string _consolidationConfirmationText = string.Empty;
    private string _mediaConsolidationStatusText =
        "Choose two new, separate folders. Preview and rehearsal never move audio.";
    private double _mediaConsolidationProgressPercent;
    private bool _isMediaConsolidationBusy;
    private MediaConsolidationPlan? _mediaConsolidationPlan;
    private MediaConsolidationRehearsalResult? _mediaConsolidationRehearsal;
    private CancellationTokenSource? _mediaConsolidationCancellation;
    private ServerCommand? _chooseManagedArchiveCommand;
    private ServerCommand? _chooseQuarantineCommand;
    private ServerCommand? _prepareMediaConsolidationCommand;
    private ServerCommand? _rehearseMediaConsolidationCommand;
    private ServerCommand? _commitMediaConsolidationCommand;
    private ServerCommand? _cancelMediaConsolidationCommand;

    public string ManagedArchivePath
    {
        get => _managedArchivePath;
        private set
        {
            if (!Set(ref _managedArchivePath, value)) return;
            InvalidateMediaConsolidationPlan();
        }
    }

    public string ConsolidationQuarantinePath
    {
        get => _consolidationQuarantinePath;
        private set
        {
            if (!Set(ref _consolidationQuarantinePath, value)) return;
            InvalidateMediaConsolidationPlan();
        }
    }

    public string ConsolidationConfirmationText
    {
        get => _consolidationConfirmationText;
        set
        {
            if (!Set(ref _consolidationConfirmationText, value)) return;
            RaiseMediaConsolidationCommandState();
        }
    }

    public string MediaConsolidationStatusText
    {
        get => _mediaConsolidationStatusText;
        private set => Set(ref _mediaConsolidationStatusText, value);
    }

    public double MediaConsolidationProgressPercent
    {
        get => _mediaConsolidationProgressPercent;
        private set => Set(ref _mediaConsolidationProgressPercent, Math.Clamp(value, 0, 100));
    }

    public bool IsMediaConsolidationBusy
    {
        get => _isMediaConsolidationBusy;
        private set
        {
            if (!Set(ref _isMediaConsolidationBusy, value)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMediaConsolidationIdle)));
            RaiseMediaConsolidationCommandState();
            RaiseArchiveReconciliationCommandState();
        }
    }

    public bool IsMediaConsolidationIdle => !IsMediaConsolidationBusy;
    public bool HasMediaConsolidationPlan => _mediaConsolidationPlan is not null;
    public bool HasPassedMediaConsolidationRehearsal => _mediaConsolidationRehearsal?.CanCommit == true;
    public string MediaConsolidationPlanText => _mediaConsolidationPlan is null
        ? "No consolidation plan has been prepared."
        : $"Plan {_mediaConsolidationPlan.PlanId}: {_mediaConsolidationPlan.EligibleBroadcasts:N0} broadcasts · " +
          $"{_mediaConsolidationPlan.ManagedFiles:N0} managed files · {_mediaConsolidationPlan.RejectedFiles:N0} files retained for review · " +
          $"{_mediaConsolidationPlan.HeldBroadcasts:N0} broadcasts / {_mediaConsolidationPlan.HeldSourceFiles:N0} files held unchanged · " +
          $"all {_mediaConsolidationPlan.InventoryAvailableFiles:N0} available physical files accounted for " +
          $"({_mediaConsolidationPlan.InventoryMissingFiles:N0} missing records excluded).";
    public string MediaConsolidationConfirmationHint => _mediaConsolidationPlan is null
        ? "Prepare a plan to receive its confirmation phrase."
        : $"After rehearsal and after stopping the server, enter exactly: {_mediaConsolidationPlan.ConfirmationText}";

    public ICommand ChooseManagedArchiveCommand { get; private set; } = null!;
    public ICommand ChooseQuarantineCommand { get; private set; } = null!;
    public ICommand PrepareMediaConsolidationCommand { get; private set; } = null!;
    public ICommand RehearseMediaConsolidationCommand { get; private set; } = null!;
    public ICommand CommitMediaConsolidationCommand { get; private set; } = null!;
    public ICommand CancelMediaConsolidationCommand { get; private set; } = null!;

    private void InitializeMediaConsolidationCommands()
    {
        _chooseManagedArchiveCommand = new ServerCommand(
            () => _ = ChooseManagedArchiveAsync(),
            () => !IsMediaConsolidationBusy);
        _chooseQuarantineCommand = new ServerCommand(
            () => _ = ChooseConsolidationQuarantineAsync(),
            () => !IsMediaConsolidationBusy);
        _prepareMediaConsolidationCommand = new ServerCommand(
            () => _ = PrepareMediaConsolidationAsync(),
            () => !IsMediaConsolidationBusy && !IsArchiveReconciliationBusy &&
                  !string.IsNullOrWhiteSpace(ManagedArchivePath) &&
                  !string.IsNullOrWhiteSpace(ConsolidationQuarantinePath));
        _rehearseMediaConsolidationCommand = new ServerCommand(
            () => _ = RehearseMediaConsolidationAsync(),
            () => !IsMediaConsolidationBusy && !IsArchiveReconciliationBusy && _mediaConsolidationPlan is not null);
        _commitMediaConsolidationCommand = new ServerCommand(
            () => _ = CommitMediaConsolidationAsync(),
            () => !IsMediaConsolidationBusy && !IsArchiveReconciliationBusy && !IsServerRunning &&
                  _mediaConsolidationPlan is not null &&
                  _mediaConsolidationRehearsal?.CanCommit == true &&
                  string.Equals(
                      ConsolidationConfirmationText.Trim(),
                      _mediaConsolidationPlan.ConfirmationText,
                      StringComparison.Ordinal));
        _cancelMediaConsolidationCommand = new ServerCommand(
            CancelMediaConsolidation,
            () => IsMediaConsolidationBusy);
        ChooseManagedArchiveCommand = _chooseManagedArchiveCommand;
        ChooseQuarantineCommand = _chooseQuarantineCommand;
        PrepareMediaConsolidationCommand = _prepareMediaConsolidationCommand;
        RehearseMediaConsolidationCommand = _rehearseMediaConsolidationCommand;
        CommitMediaConsolidationCommand = _commitMediaConsolidationCommand;
        CancelMediaConsolidationCommand = _cancelMediaConsolidationCommand;
    }

    private async Task ChooseManagedArchiveAsync()
    {
        if (_folderSelection is null) return;
        var path = await _folderSelection.PickLibraryFolderAsync(
            "Choose a new empty folder for the consolidated Radio Vault archive").ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(path)) ManagedArchivePath = path;
    }

    private async Task ChooseConsolidationQuarantineAsync()
    {
        if (_folderSelection is null) return;
        var path = await _folderSelection.PickLibraryFolderAsync(
            "Choose a separate folder that will retain every original and rejected duplicate").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return;
        ConsolidationQuarantinePath = path;
        if (_runtime is null) return;
        try
        {
            var interrupted = await Task.Run(() => _runtime.LoadInterruptedMediaConsolidation(path)).ConfigureAwait(true);
            if (interrupted is null) return;
            ManagedArchivePath = interrupted.ManagedRoot;
            _mediaConsolidationPlan = interrupted;
            _mediaConsolidationRehearsal = null;
            MediaConsolidationStatusText =
                $"Recovered interrupted plan {interrupted.PlanId}. Run the no-move rehearsal again, then use the original exact confirmation phrase to resume safely.";
            RaiseMediaConsolidationPlanState();
        }
        catch (Exception exception)
        {
            MediaConsolidationStatusText = exception.Message;
        }
    }

    private async Task PrepareMediaConsolidationAsync()
    {
        if (_runtime is null) return;
        await RunMediaConsolidationOperationAsync(
            "Reading complete file identities and ranking recordings…",
            (progress, cancellationToken) =>
            {
                var plan = _runtime.PrepareMediaConsolidation(
                    ManagedArchivePath,
                    ConsolidationQuarantinePath,
                    progress,
                    cancellationToken);
                _mediaConsolidationPlan = plan;
                _mediaConsolidationRehearsal = null;
                return $"Preview ready. All {plan.InventoryAvailableFiles:N0} available physical files are accounted for: " +
                       $"{plan.ManagedFiles:N0} files would form the managed archive; " +
                       $"{plan.RejectedFiles:N0} originals or alternates would remain in quarantine. " +
                       $"{plan.HeldBroadcasts:N0} unsafe broadcasts containing {plan.HeldSourceFiles:N0} file(s) will not be touched. " +
                       $"{plan.InventoryMissingFiles:N0} database record(s) already marked missing are explicitly outside the move.";
            }).ConfigureAwait(true);
        RaiseMediaConsolidationPlanState();
        await RefreshArchiveReconciliationAsync().ConfigureAwait(true);
    }

    private async Task RehearseMediaConsolidationAsync()
    {
        if (_runtime is null || _mediaConsolidationPlan is null) return;
        await RunMediaConsolidationOperationAsync(
            "Rehearsing every source, destination and storage requirement without moving audio…",
            (progress, cancellationToken) =>
            {
                var result = _runtime.RehearseMediaConsolidation(
                    _mediaConsolidationPlan,
                    progress,
                    cancellationToken);
                _mediaConsolidationRehearsal = result;
                return result.Message + (result.Problems.Count == 0
                    ? string.Empty
                    : " " + string.Join(" ", result.Problems.Take(3)));
            }).ConfigureAwait(true);
        RaiseMediaConsolidationPlanState();
    }

    private async Task CommitMediaConsolidationAsync()
    {
        if (_runtime is null || _mediaConsolidationPlan is null || _mediaConsolidationRehearsal is null) return;
        await RunMediaConsolidationOperationAsync(
            "Creating a verified database backup and consolidated copies…",
            (progress, cancellationToken) => _runtime.CommitMediaConsolidation(
                _mediaConsolidationPlan,
                _mediaConsolidationRehearsal,
                ConsolidationConfirmationText,
                progress,
                cancellationToken).Message).ConfigureAwait(true);
        ConsolidationConfirmationText = string.Empty;
        RaiseMediaConsolidationPlanState();
        await LoadLibraryFoldersAsync().ConfigureAwait(true);
        await LoadRssFeedsAsync().ConfigureAwait(true);
    }

    private async Task RunMediaConsolidationOperationAsync(
        string initialStatus,
        Func<IProgress<MediaConsolidationProgress>, CancellationToken, string> operation)
    {
        if (IsMediaConsolidationBusy) return;
        _mediaConsolidationCancellation?.Dispose();
        _mediaConsolidationCancellation = new CancellationTokenSource();
        var cancellationToken = _mediaConsolidationCancellation.Token;
        var progress = new Progress<MediaConsolidationProgress>(value =>
        {
            MediaConsolidationProgressPercent = value.Percent;
            MediaConsolidationStatusText = value.Message;
        });
        IsMediaConsolidationBusy = true;
        MediaConsolidationProgressPercent = 0;
        MediaConsolidationStatusText = initialStatus;
        try
        {
            var message = await Task.Run(
                () => operation(progress, cancellationToken),
                cancellationToken).ConfigureAwait(true);
            MediaConsolidationProgressPercent = 100;
            MediaConsolidationStatusText = message;
        }
        catch (OperationCanceledException)
        {
            MediaConsolidationStatusText = "Media consolidation stopped safely. Completed verified steps remain journalled; no files were deleted.";
        }
        catch (Exception exception)
        {
            MediaConsolidationStatusText = exception.Message;
        }
        finally
        {
            IsMediaConsolidationBusy = false;
        }
    }

    private void CancelMediaConsolidation()
        => _mediaConsolidationCancellation?.Cancel();

    private void InvalidateMediaConsolidationPlan()
    {
        _mediaConsolidationPlan = null;
        _mediaConsolidationRehearsal = null;
        ConsolidationConfirmationText = string.Empty;
        RaiseMediaConsolidationPlanState();
    }

    private void RaiseMediaConsolidationPlanState()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasMediaConsolidationPlan)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPassedMediaConsolidationRehearsal)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MediaConsolidationPlanText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MediaConsolidationConfirmationHint)));
        RaiseMediaConsolidationCommandState();
    }

    private void RaiseMediaConsolidationCommandState()
    {
        _chooseManagedArchiveCommand?.RaiseCanExecuteChanged();
        _chooseQuarantineCommand?.RaiseCanExecuteChanged();
        _prepareMediaConsolidationCommand?.RaiseCanExecuteChanged();
        _rehearseMediaConsolidationCommand?.RaiseCanExecuteChanged();
        _commitMediaConsolidationCommand?.RaiseCanExecuteChanged();
        _cancelMediaConsolidationCommand?.RaiseCanExecuteChanged();
    }

    private void DisposeMediaConsolidation()
    {
        _mediaConsolidationCancellation?.Cancel();
        _mediaConsolidationCancellation?.Dispose();
        _mediaConsolidationCancellation = null;
    }
}
