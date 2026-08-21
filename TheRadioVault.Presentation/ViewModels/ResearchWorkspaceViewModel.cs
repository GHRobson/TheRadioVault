using System.Collections.ObjectModel;
using System.Windows.Input;
using TheRadioVault.Application.Abstractions;
using TheRadioVault.Application.Models;
using TheRadioVault.Core.Services;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Presentation.ViewModels;

public enum ResearchWorkspaceMode
{
    Dashboard,
    Records,
    DateReview,
    Undated,
    Coverage
}

public enum DateReviewQueue
{
    Active,
    Ignored,
    Completed
}

public sealed class ResearchWorkspaceViewModel : ObservableObject
{
    private readonly IResearchWorkspaceService _research;
    private readonly IResearchPackTransferService _transfers;
    private readonly IFileSelectionService _files;
    private readonly IExternalLauncherService _launcher;
    private readonly PlaybackViewModel _playback;
    private readonly AsyncCommand _saveCommand;
    private readonly AsyncCommand _revertCommand;
    private readonly AsyncCommand _chooseArtworkCommand;
    private readonly AsyncCommand _playCommand;
    private readonly AsyncCommand _openSourceCommand;
    private readonly AsyncCommand _toggleReviewCommand;
    private readonly AsyncCommand _chooseImportCommand;
    private readonly AsyncCommand _retryImportCommand;
    private readonly AsyncCommand _applyImportCommand;
    private readonly AsyncCommand _cancelImportCommand;
    private readonly AsyncCommand _exportCommand;
    private readonly AsyncCommand _exportUndatedCommand;
    private readonly AsyncCommand _exportMissingResearchCommand;
    private readonly AsyncCommand _showRecordsCommand;
    private readonly AsyncCommand _showDashboardCommand;
    private readonly AsyncCommand _showDateReviewCommand;
    private readonly AsyncCommand _showUndatedCommand;
    private readonly AsyncCommand _showCoverageCommand;
    private readonly AsyncCommand _assignUndatedDateCommand;
    private readonly AsyncCommand _approveDateCommand;
    private readonly AsyncCommand _keepExistingDateCommand;
    private readonly AsyncCommand _ignoreDateCommand;
    private readonly AsyncCommand _undoDateDecisionCommand;
    private readonly AsyncCommand _keepRecordingDateCommand;
    private readonly AsyncCommand _keepReleaseDateCommand;
    private readonly AsyncCommand _leaveUndatedCommand;
    private readonly AsyncCommand _reopenDateDecisionCommand;
    private readonly DelegateCommand _showActiveDateReviewsCommand;
    private readonly DelegateCommand _showIgnoredDateReviewsCommand;
    private readonly DelegateCommand _showCompletedDateReviewsCommand;
    private Func<Task>? _openTranscription;
    private bool _isBusy;
    private string _statusText = "Ready";
    private string _searchText = string.Empty;
    private ResearchCollectionOption? _selectedCollection;
    private ResearchStatusOption? _selectedStatus;
    private bool _needsReviewOnly;
    private ResearchBrowseItem? _selectedRecord;
    private ResearchSourceItem? _selectedSource;
    private ResearchRecordDetails? _details;
    private int _selectionVersion;
    private int _filterVersion;
    private CancellationTokenSource? _filterDebounce;
    private bool _suppressAutomaticRefresh;
    private bool _workspaceLoaded;
    private bool _hasBrowseError;
    private ResearchPackPreviewSummary? _importPreview;
    private string? _selectedImportPath;
    private string _importErrorText = string.Empty;
    private bool _isImportPreviewBusy;
    private bool _isImportApplyBusy;
    private double _importProgressPercent;
    private string _importProgressText = string.Empty;
    private string _importProgressCountText = string.Empty;
    private CancellationTokenSource? _activeImportCancellation;
    private ResearchWorkspaceMode _workspaceMode = ResearchWorkspaceMode.Dashboard;
    private UndatedBroadcastItem? _selectedUndatedBroadcast;
    private DateTimeOffset? _selectedUndatedDate;
    private CatalogueDateReviewItem? _selectedDateReview;
    private DateTimeOffset? _selectedDateReviewDate;
    private readonly List<CatalogueDateReviewItem> _allDateReviews = new();
    private DateReviewQueue _dateReviewQueue = DateReviewQueue.Active;
    private long? _lastDateDecisionResearchId;
    private string _undoDateDecisionText = string.Empty;
    private ResearchCoverageShow? _coverage;

    private string _editorHeadline = string.Empty;
    private string _editorSummary = string.Empty;
    private string _editorStation = string.Empty;
    private string _editorEdition = string.Empty;
    private string _editorVariant = string.Empty;
    private string _editorEra = string.Empty;
    private string _editorEpisodeType = string.Empty;
    private string _editorArchiveNotes = string.Empty;
    private double _editorConfidence;
    private string _editorConfidenceReason = string.Empty;
    private bool _editorNeedsReview;
    private string _editorHosts = string.Empty;
    private string _editorGuests = string.Empty;
    private string _editorCallers = string.Empty;
    private string _editorMentionedPeople = string.Empty;
    private string _editorTopics = string.Empty;
    private string _editorCatalogueSeries = string.Empty;
    private string _editorCatalogueProgramme = string.Empty;
    private string _editorCatalogueFormat = string.Empty;
    private string _editorOriginalReleaseDate = string.Empty;
    private string _editorRecordingDate = string.Empty;
    private string _editorVenue = string.Empty;
    private string _editorEvent = string.Empty;
    private string _editorNetwork = string.Empty;
    private string _editorCatalogueNumber = string.Empty;
    private string _editorOriginalFilename = string.Empty;
    private string _editorProvenance = string.Empty;
    private string _editorResearchNotes = string.Empty;
    private string? _editorArtworkPath;

    public ResearchWorkspaceViewModel(
        IResearchWorkspaceService research,
        IResearchPackTransferService transfers,
        IFileSelectionService files,
        IExternalLauncherService launcher,
        PlaybackViewModel playback,
        IWikiService? wiki = null)
    {
        _research = research ?? throw new ArgumentNullException(nameof(research));
        _transfers = transfers ?? throw new ArgumentNullException(nameof(transfers));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));

        Statuses = new ObservableCollection<ResearchStatusOption>(new[]
        {
            new ResearchStatusOption("attention", "Needs attention"),
            new ResearchStatusOption("all", "All knowledge"),
            new ResearchStatusOption("review", "Needs review"),
            new ResearchStatusOption("conflicts", "Metadata conflicts"),
            new ResearchStatusOption("missing", "Missing recordings"),
            new ResearchStatusOption("unsourced", "Without sources"),
            new ResearchStatusOption("in_library", "In library")
        });
        _selectedStatus = Statuses[0];

        RefreshCommand = new AsyncCommand(LoadAsync, onError: SetError);
        SearchCommand = new AsyncCommand(LoadRecordsAsync, onError: SetError);
        ClearFiltersCommand = new AsyncCommand(ClearFiltersAsync, onError: SetError);
        ShowAllResearchCommand = new DelegateCommand(() =>
            SelectedStatus = Statuses.FirstOrDefault(x => string.Equals(x.Key, "all", StringComparison.OrdinalIgnoreCase))
                ?? Statuses.FirstOrDefault());
        _saveCommand = new AsyncCommand(SaveAsync, () => HasSelection && !IsBusy, SetError);
        _revertCommand = new AsyncCommand(ReloadSelectionAsync, () => HasSelection && !IsBusy, SetError);
        _chooseArtworkCommand = new AsyncCommand(ChooseArtworkAsync, () => CanEditArtwork && !IsBusy, SetError);
        ClearArtworkCommand = new DelegateCommand(ClearArtwork, () => CanEditArtwork && !IsBusy);
        _playCommand = new AsyncCommand(PlayAsync, () => SelectedRecord?.EpisodeId is > 0 && !IsBusy, SetError);
        _openSourceCommand = new AsyncCommand(OpenSourceAsync, () => SelectedSource?.HasUrl == true && !IsBusy, SetError);
        _toggleReviewCommand = new AsyncCommand(ToggleReviewAsync, () => HasSelection && CanEditReviewState && !IsBusy, SetError);
        _chooseImportCommand = new AsyncCommand(ChooseImportAsync, () => _transfers.IsAvailable && !IsBusy, SetError);
        _retryImportCommand = new AsyncCommand(RetryImportAsync, () => CanRetryImport && !IsBusy, SetError);
        _applyImportCommand = new AsyncCommand(ApplyImportAsync, () => HasPendingImport && !IsBusy, SetError);
        _cancelImportCommand = new AsyncCommand(CancelImportAsync, () => HasPendingImport && (!IsBusy || IsImportApplyBusy), SetError);
        _exportCommand = new AsyncCommand(ExportAsync, () => _transfers.IsAvailable && !IsBusy, SetError);
        _exportUndatedCommand = new AsyncCommand(
            () => ExportKnowledgeDatabaseAsync(KnowledgeExportScope.UndatedBroadcasts),
            () => _transfers.IsAvailable && !IsBusy,
            SetError);
        _exportMissingResearchCommand = new AsyncCommand(
            () => ExportKnowledgeDatabaseAsync(KnowledgeExportScope.MissingTopicsOrSummaries),
            () => _transfers.IsAvailable && !IsBusy,
            SetError);
        _showDashboardCommand = new AsyncCommand(() => SwitchModeAsync(ResearchWorkspaceMode.Dashboard), () => !IsBusy, SetError);
        _showRecordsCommand = new AsyncCommand(() => SwitchModeAsync(ResearchWorkspaceMode.Records), () => !IsBusy, SetError);
        _showDateReviewCommand = new AsyncCommand(() => SwitchModeAsync(ResearchWorkspaceMode.DateReview), () => !IsBusy, SetError);
        _showUndatedCommand = new AsyncCommand(() => SwitchModeAsync(ResearchWorkspaceMode.Undated), () => !IsBusy, SetError);
        _showCoverageCommand = new AsyncCommand(() => SwitchModeAsync(ResearchWorkspaceMode.Coverage), () => !IsBusy, SetError);
        OpenTranscriptionCommand = new AsyncCommand(() => _openTranscription?.Invoke() ?? Task.CompletedTask);
        _assignUndatedDateCommand = new AsyncCommand(AssignUndatedDateAsync,
            () => SelectedUndatedBroadcast is not null && SelectedUndatedDate.HasValue && !IsBusy, SetError);
        _approveDateCommand = new AsyncCommand(() => ResolveDateReviewAsync(CatalogueDateReviewAction.ApproveLibraryDate),
            () => SelectedDateReview?.IsPending == true && SelectedDateReviewDate.HasValue && !IsBusy, SetError);
        _keepExistingDateCommand = new AsyncCommand(() => ResolveDateReviewAsync(CatalogueDateReviewAction.KeepExisting),
            () => SelectedDateReview?.IsPending == true && !IsBusy, SetError);
        _ignoreDateCommand = new AsyncCommand(() => ResolveDateReviewAsync(CatalogueDateReviewAction.Ignore),
            () => SelectedDateReview?.IsPending == true && !IsBusy, SetError);
        _undoDateDecisionCommand = new AsyncCommand(UndoDateDecisionAsync,
            () => _lastDateDecisionResearchId.HasValue && !IsBusy, SetError);
        _keepRecordingDateCommand = new AsyncCommand(() => ResolveDateReviewAsync(CatalogueDateReviewAction.KeepAsRecordingDate),
            () => SelectedDateReview?.IsPending == true && !IsBusy, SetError);
        _keepReleaseDateCommand = new AsyncCommand(() => ResolveDateReviewAsync(CatalogueDateReviewAction.KeepAsReleaseDate),
            () => SelectedDateReview?.IsPending == true && !IsBusy, SetError);
        _leaveUndatedCommand = new AsyncCommand(() => ResolveDateReviewAsync(CatalogueDateReviewAction.LeaveUndated),
            () => SelectedDateReview?.IsPending == true && !IsBusy, SetError);
        _reopenDateDecisionCommand = new AsyncCommand(() => ResolveDateReviewAsync(CatalogueDateReviewAction.Reopen),
            () => SelectedDateReview?.IsResolved == true && !IsBusy, SetError);
        _showActiveDateReviewsCommand = new DelegateCommand(() => SwitchDateReviewQueue(DateReviewQueue.Active));
        _showIgnoredDateReviewsCommand = new DelegateCommand(() => SwitchDateReviewQueue(DateReviewQueue.Ignored));
        _showCompletedDateReviewsCommand = new DelegateCommand(() => SwitchDateReviewQueue(DateReviewQueue.Completed));

        SaveCommand = _saveCommand;
        RevertCommand = _revertCommand;
        ChooseArtworkCommand = _chooseArtworkCommand;
        PlayCommand = _playCommand;
        OpenSourceCommand = _openSourceCommand;
        ToggleReviewCommand = _toggleReviewCommand;
        ChooseImportCommand = _chooseImportCommand;
        RetryImportCommand = _retryImportCommand;
        ApplyImportCommand = _applyImportCommand;
        CancelImportCommand = _cancelImportCommand;
        ExportCommand = _exportCommand;
        ExportUndatedCommand = _exportUndatedCommand;
        ExportMissingResearchCommand = _exportMissingResearchCommand;
        ShowDashboardCommand = _showDashboardCommand;
        ShowRecordsCommand = _showRecordsCommand;
        ShowDateReviewCommand = _showDateReviewCommand;
        ShowUndatedCommand = _showUndatedCommand;
        ShowCoverageCommand = _showCoverageCommand;
        AssignUndatedDateCommand = _assignUndatedDateCommand;
        ApproveDateCommand = _approveDateCommand;
        KeepExistingDateCommand = _keepExistingDateCommand;
        IgnoreDateCommand = _ignoreDateCommand;
        UndoDateDecisionCommand = _undoDateDecisionCommand;
        KeepRecordingDateCommand = _keepRecordingDateCommand;
        KeepReleaseDateCommand = _keepReleaseDateCommand;
        LeaveUndatedCommand = _leaveUndatedCommand;
        ReopenDateDecisionCommand = _reopenDateDecisionCommand;
        ShowActiveDateReviewsCommand = _showActiveDateReviewsCommand;
        ShowIgnoredDateReviewsCommand = _showIgnoredDateReviewsCommand;
        ShowCompletedDateReviewsCommand = _showCompletedDateReviewsCommand;
    }

    public ObservableCollection<ResearchBrowseItem> Records { get; } = new();
    public ObservableCollection<ResearchCollectionOption> Collections { get; } = new();
    public ObservableCollection<ResearchStatusOption> Statuses { get; }
    public ObservableCollection<ResearchSourceItem> Sources { get; } = new();
    public ObservableCollection<ResearchSourceDiagnostic> SourceDiagnostics { get; } = new();
    public ObservableCollection<ResearchImportRunSummary> ImportHistory { get; } = new();
    public ObservableCollection<UndatedBroadcastItem> UndatedBroadcasts { get; } = new();
    public ObservableCollection<CatalogueDateReviewItem> DateReviews { get; } = new();
    public ObservableCollection<ResearchCoverageYearViewModel> CoverageYears { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public ICommand ShowAllResearchCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand RevertCommand { get; }
    public ICommand ChooseArtworkCommand { get; }
    public ICommand ClearArtworkCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand OpenSourceCommand { get; }
    public ICommand ToggleReviewCommand { get; }
    public ICommand ChooseImportCommand { get; }
    public ICommand RetryImportCommand { get; }
    public ICommand ApplyImportCommand { get; }
    public ICommand CancelImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ExportUndatedCommand { get; }
    public ICommand ExportMissingResearchCommand { get; }
    public ICommand ShowDashboardCommand { get; }
    public ICommand ShowRecordsCommand { get; }
    public ICommand ShowDateReviewCommand { get; }
    public ICommand ShowUndatedCommand { get; }
    public ICommand ShowCoverageCommand { get; }
    public ICommand OpenTranscriptionCommand { get; }
    public ICommand AssignUndatedDateCommand { get; }
    public ICommand ApproveDateCommand { get; }
    public ICommand KeepExistingDateCommand { get; }
    public ICommand IgnoreDateCommand { get; }
    public ICommand UndoDateDecisionCommand { get; }
    public ICommand KeepRecordingDateCommand { get; }
    public ICommand KeepReleaseDateCommand { get; }
    public ICommand LeaveUndatedCommand { get; }
    public ICommand ReopenDateDecisionCommand { get; }
    public ICommand ShowActiveDateReviewsCommand { get; }
    public ICommand ShowIgnoredDateReviewsCommand { get; }
    public ICommand ShowCompletedDateReviewsCommand { get; }
    public bool IsPackTransferAvailable => _transfers.IsAvailable;

    public void SetOpenTranscriptionHandler(Func<Task> handler)
        => _openTranscription = handler ?? throw new ArgumentNullException(nameof(handler));
    public bool IsImportBusy => IsImportPreviewBusy || IsImportApplyBusy;
    public bool IsImportPreviewBusy
    {
        get => _isImportPreviewBusy;
        private set
        {
            if (!SetProperty(ref _isImportPreviewBusy, value)) return;
            RaisePropertyChanged(nameof(IsImportBusy));
            RaisePropertyChanged(nameof(ChooseImportButtonText));
        }
    }
    public bool IsImportApplyBusy
    {
        get => _isImportApplyBusy;
        private set
        {
            if (!SetProperty(ref _isImportApplyBusy, value)) return;
            RaisePropertyChanged(nameof(IsImportBusy));
            RaisePropertyChanged(nameof(ApplyImportButtonText));
        }
    }
    public string ChooseImportButtonText => IsImportPreviewBusy ? "Checking…" : "Import Knowledge Database";
    public string ApplyImportButtonText => IsImportApplyBusy ? "Importing…" : "Import now";

    public double ImportProgressPercent
    {
        get => _importProgressPercent;
        private set
        {
            if (!SetProperty(ref _importProgressPercent, Math.Clamp(value, 0, 100))) return;
            RaisePropertyChanged(nameof(ImportProgressPercentText));
        }
    }
    public string ImportProgressPercentText => $"{ImportProgressPercent:0}%";
    public string ImportProgressText
    {
        get => _importProgressText;
        private set => SetProperty(ref _importProgressText, value);
    }
    public string ImportProgressCountText
    {
        get => _importProgressCountText;
        private set => SetProperty(ref _importProgressCountText, value);
    }

    public ResearchWorkspaceMode WorkspaceMode
    {
        get => _workspaceMode;
        private set
        {
            if (!SetProperty(ref _workspaceMode, value)) return;
            RaisePropertyChanged(nameof(IsRecordsMode));
            RaisePropertyChanged(nameof(IsDashboardMode));
            RaisePropertyChanged(nameof(IsDateReviewMode));
            RaisePropertyChanged(nameof(IsUndatedMode));
            RaisePropertyChanged(nameof(IsCoverageMode));
            RaisePropertyChanged(nameof(WorkspaceCountText));
            RaisePropertyChanged(nameof(ShowCoverageEmptyState));
        }
    }
    public bool IsDashboardMode => WorkspaceMode == ResearchWorkspaceMode.Dashboard;
    public bool IsRecordsMode => WorkspaceMode == ResearchWorkspaceMode.Records;
    public bool IsDateReviewMode => WorkspaceMode == ResearchWorkspaceMode.DateReview;
    public bool IsUndatedMode => WorkspaceMode == ResearchWorkspaceMode.Undated;
    public bool IsCoverageMode => WorkspaceMode == ResearchWorkspaceMode.Coverage;
    public bool HasUndatedBroadcasts => UndatedBroadcasts.Count > 0;
    public bool HasDateReviews => DateReviews.Count > 0;
    public bool HasCoverage => _coverage is not null && CoverageYears.Count > 0;
    public bool NeedsCoverageShow => IsCoverageMode && SelectedCollection?.CollectionId is not > 0;
    public bool ShowCoverageEmptyState => IsCoverageMode && !NeedsCoverageShow && !HasCoverage;
    public ResearchCoverageShow? Coverage => _coverage;
    public string WorkspaceCountText => WorkspaceMode switch
    {
        ResearchWorkspaceMode.DateReview => $"{DateReviews.Count:N0} date decision{(DateReviews.Count == 1 ? string.Empty : "s")}",
        ResearchWorkspaceMode.Undated => $"{UndatedBroadcasts.Count:N0} undated broadcast{(UndatedBroadcasts.Count == 1 ? string.Empty : "s")}",
        ResearchWorkspaceMode.Coverage => _coverage?.SummaryText ?? "Choose a show to build its metadata heatmap",
        ResearchWorkspaceMode.Dashboard => Overview.KnowledgeStateText,
        _ => RecordCountText
    };
    public string KnowledgeCoverageText => $"{Overview.CoveragePercent}%";
    public string KnowledgeDateRangeText => Overview.DateRangeText;
    public string KnowledgeShowsText => $"{Collections.Count(x => x.CollectionId.HasValue):N0}";
    public string KnowledgeAttentionText => $"{Overview.NeedsReviewRecords + Overview.ConflictRecords:N0}";
    public string KnowledgeSourcesText => $"{Overview.WithSources:N0}";
    public string KnowledgeSummariesText => $"{Overview.WithSummaries:N0}";
    public double SummaryCoveragePercent => Overview.TotalRecords <= 0 ? 0 : 100d * Overview.WithSummaries / Overview.TotalRecords;
    public double PeopleCoveragePercent => Overview.TotalRecords <= 0 ? 0 : 100d * Overview.WithPeople / Overview.TotalRecords;
    public double TopicCoveragePercent => Overview.TotalRecords <= 0 ? 0 : 100d * Overview.WithTopics / Overview.TotalRecords;
    public double SourceCoveragePercent => Overview.TotalRecords <= 0 ? 0 : 100d * Overview.WithSources / Overview.TotalRecords;
    public CatalogueDateReviewItem? SelectedDateReview
    {
        get => _selectedDateReview;
        set
        {
            if (!SetProperty(ref _selectedDateReview, value)) return;
            SelectedDateReviewDate = value?.ProposedDate is { } proposed
                ? new DateTimeOffset(proposed.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                : null;
            RaisePropertyChanged(nameof(HasDateReviewSelection));
            RaisePropertyChanged(nameof(DateReviewActionHelpText));
            RaiseCommandState();
        }
    }
    public bool HasDateReviewSelection => SelectedDateReview is not null;
    public DateTimeOffset? SelectedDateReviewDate
    {
        get => _selectedDateReviewDate;
        set
        {
            if (!SetProperty(ref _selectedDateReviewDate, value)) return;
            _approveDateCommand.RaiseCanExecuteChanged();
        }
    }
    public DateReviewQueue CurrentDateReviewQueue => _dateReviewQueue;
    public bool IsActiveDateReviewQueue => CurrentDateReviewQueue == DateReviewQueue.Active;
    public bool IsIgnoredDateReviewQueue => CurrentDateReviewQueue == DateReviewQueue.Ignored;
    public bool IsCompletedDateReviewQueue => CurrentDateReviewQueue == DateReviewQueue.Completed;
    public int ActiveDateReviewCount => _allDateReviews.Count(item => item.IsPending);
    public int IgnoredDateReviewCount => _allDateReviews.Count(item => item.IsIgnored);
    public int CompletedDateReviewCount => _allDateReviews.Count(item => item.IsCompleted);
    public string ActiveDateReviewLabel => $"Active ({ActiveDateReviewCount:N0})";
    public string IgnoredDateReviewLabel => $"Ignored ({IgnoredDateReviewCount:N0})";
    public string CompletedDateReviewLabel => $"Completed ({CompletedDateReviewCount:N0})";
    public string DateReviewEmptyTitle => CurrentDateReviewQueue switch
    {
        DateReviewQueue.Ignored => "No ignored date suggestions",
        DateReviewQueue.Completed => "No completed date decisions",
        _ => "No broadcast dates need a decision"
    };
    public string DateReviewEmptyHelp => CurrentDateReviewQueue switch
    {
        DateReviewQueue.Ignored => "Suggestions you ignore remain recoverable here.",
        DateReviewQueue.Completed => "Approved and preserved decisions will appear here.",
        _ => "Research-backed date changes will appear here when they need your approval."
    };
    public bool CanUndoDateDecision => _lastDateDecisionResearchId.HasValue;
    public string UndoDateDecisionText => string.IsNullOrWhiteSpace(_undoDateDecisionText)
        ? "Undo last decision"
        : _undoDateDecisionText;
    public string DateReviewActionHelpText => SelectedDateReview?.IsResolved == true
        ? "This decision is stored with the research evidence. Return it to Active if you want to decide again."
        : KnownShowCatalog.SupportsUndatedCatalogueItems(SelectedDateReview?.ShowName)
            ? "Approve the proposal, keep the current Library date, or ignore this suggestion for now."
            : "Approve only when this is the actual broadcast date; otherwise keep the current date or ignore the suggestion.";

    public UndatedBroadcastItem? SelectedUndatedBroadcast
    {
        get => _selectedUndatedBroadcast;
        set
        {
            if (!SetProperty(ref _selectedUndatedBroadcast, value)) return;
            SelectedUndatedDate = value?.ProposedDate is { } proposed
                ? new DateTimeOffset(proposed.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                : null;
            RaisePropertyChanged(nameof(HasUndatedSelection));
            _assignUndatedDateCommand.RaiseCanExecuteChanged();
        }
    }
    public bool HasUndatedSelection => SelectedUndatedBroadcast is not null;
    public DateTimeOffset? SelectedUndatedDate
    {
        get => _selectedUndatedDate;
        set
        {
            if (!SetProperty(ref _selectedUndatedDate, value)) return;
            _assignUndatedDateCommand.RaiseCanExecuteChanged();
        }
    }

    public ResearchWorkspaceOverview Overview { get; private set; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null);
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RaiseCommandState();
            RaisePropertyChanged(nameof(ShowAttentionEmptyState));
            RaisePropertyChanged(nameof(ShowFilteredEmptyState));
            RaisePropertyChanged(nameof(ShowCoverageEmptyState));
        }
    }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            OnFiltersChanged();
        }
    }
    public ResearchCollectionOption? SelectedCollection
    {
        get => _selectedCollection;
        set
        {
            if (!SetProperty(ref _selectedCollection, value)) return;
            _exportCommand?.RaiseCanExecuteChanged();
            RaisePropertyChanged(nameof(NeedsCoverageShow));
            RaisePropertyChanged(nameof(ShowCoverageEmptyState));
            OnFiltersChanged();
        }
    }
    public ResearchStatusOption? SelectedStatus
    {
        get => _selectedStatus;
        set
        {
            if (!SetProperty(ref _selectedStatus, value)) return;
            RaisePropertyChanged(nameof(IsAttentionView));
            RaisePropertyChanged(nameof(ShowAttentionEmptyState));
            RaisePropertyChanged(nameof(ShowFilteredEmptyState));
            RaisePropertyChanged(nameof(BrowseHeading));
            RaisePropertyChanged(nameof(BrowseDescription));
            RaisePropertyChanged(nameof(RecordCountText));
            OnFiltersChanged();
        }
    }
    public bool NeedsReviewOnly
    {
        get => _needsReviewOnly;
        set
        {
            if (!SetProperty(ref _needsReviewOnly, value)) return;
            OnFiltersChanged();
        }
    }
    public bool IsAttentionView => string.Equals(SelectedStatus?.Key, "attention", StringComparison.OrdinalIgnoreCase);
    public bool HasActiveFilters => !string.IsNullOrWhiteSpace(SearchText) ||
        SelectedCollection is { CollectionId: > 0 } ||
        !IsAttentionView ||
        NeedsReviewOnly;
    public bool ShowAttentionEmptyState => !IsBusy && !_hasBrowseError && !HasRecords && IsAttentionView;
    public bool ShowFilteredEmptyState => !IsBusy && !_hasBrowseError && !HasRecords && !IsAttentionView;
    public string BrowseHeading => IsAttentionView ? "Needs attention" : SelectedStatus?.Name ?? "Knowledge records";
    public string BrowseDescription => IsAttentionView
        ? "Only ambiguous matches, review flags and unresolved metadata conflicts appear here."
        : "Browse the full archive or narrow it to a specific research state.";
    public bool HasPendingImport => _importPreview is not null;
    public bool HasImportError => !string.IsNullOrWhiteSpace(_importErrorText);
    public bool CanRetryImport => !string.IsNullOrWhiteSpace(_selectedImportPath) && HasImportError;
    public string ImportErrorText => _importErrorText;
    public string SelectedImportFileText => string.IsNullOrWhiteSpace(_selectedImportPath)
        ? string.Empty
        : Path.GetFileName(_selectedImportPath);
    public bool IsAuthoritativeImport => _importPreview?.AuthoritativeAudit == true;
    public string ImportPreviewText => _importPreview?.SummaryText ?? string.Empty;
    public string ImportModeText => IsAuthoritativeImport
        ? "Authoritative audit mode: audited research fields and intentional blanks replace stale values. A restorable database backup is created before import; ambiguous identities still require review."
        : ResearchTransferOwnershipText;
    public string ResearchTransferOwnershipText => _transfers.IsRemoteOwned
        ? "The connected Radio Vault Server will apply this pack transactionally and protect manual edits."
        : "The unified Knowledge and Explore database is applied with human edits protected.";
    public bool HasRecords => Records.Count > 0;
    public bool HasSelection => SelectedRecord is not null;
    public bool HasSources => Sources.Count > 0;
    public bool HasDiagnostics => SourceDiagnostics.Count > 0;
    public bool HasImportHistory => ImportHistory.Count > 0;
    public bool CanEditArtwork => _details?.CanEditArtwork == true;
    public bool CanEditAdvancedMetadata => _details?.CanEditAdvancedMetadata == true;
    public bool CanEditReviewState => _details?.CanEditReviewState == true;
    public bool HasArtwork => !string.IsNullOrWhiteSpace(EditorArtworkPath) && File.Exists(EditorArtworkPath);
    public string RecordCountText => IsAttentionView
        ? Records.Count == 0
            ? "Nothing needs your attention"
            : $"{Records.Count:N0} item{(Records.Count == 1 ? string.Empty : "s")} {(Records.Count == 1 ? "needs" : "need")} attention"
        : $"{Records.Count:N0} research record{(Records.Count == 1 ? string.Empty : "s")}";
    public string SelectionTitle => SelectedRecord?.Headline?.Trim() ?? string.Empty;
    public string SelectionIdentity => SelectedRecord is null
        ? string.Empty
        : string.Join(" · ", new[] { SelectedRecord.ShowName, SelectedRecord.DateText, SelectedRecord.IdentityText }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string ReviewButtonText => EditorNeedsReview ? "Clear review flag" : "Mark for review";
    public string ArtworkHelpText => CanEditArtwork
        ? "Artwork is stored on the linked library broadcast."
        : _details?.Record.EpisodeId is not > 0
            ? "Artwork can be edited after this research record is linked to a library broadcast."
            : "Artwork is read-only for this unlinked or protected record.";
    public string MetadataOwnershipHelpText => CanEditAdvancedMetadata
        ? "Manual edits are recorded with protected provenance."
        : "Protected classification fields are read-only for this record. Normal broadcast metadata can still be saved here.";
    public bool IsCatalogueResearch => KnownShowCatalog.SupportsUndatedCatalogueItems(SelectedRecord?.ShowName);
    public string CataloguePanelTitle => SelectedRecord?.ShowName switch
    {
        KnownShowCatalog.RonBenningtonInterviews => "Interview details",
        KnownShowCatalog.Unmasked => "Unmasked programme details",
        KnownShowCatalog.RonRon => "Archive item details",
        _ => "Catalogue details"
    };
    public string CataloguePanelHelpText => SelectedRecord?.ShowName switch
    {
        KnownShowCatalog.RonBenningtonInterviews => "Capture the interview strand, release and recording dates, venue, platform and archive provenance.",
        KnownShowCatalog.Unmasked => "Capture the original programme context, guest event, release details and archival source.",
        _ => "Capture details that do not fit the normal dated daily-broadcast model."
    };

    public ResearchBrowseItem? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (!SetProperty(ref _selectedRecord, value)) return;
            RaisePropertyChanged(nameof(HasSelection));
            RaisePropertyChanged(nameof(SelectionTitle));
            RaisePropertyChanged(nameof(SelectionIdentity));
            RaisePropertyChanged(nameof(IsCatalogueResearch));
            RaisePropertyChanged(nameof(CataloguePanelTitle));
            RaisePropertyChanged(nameof(CataloguePanelHelpText));
            RaiseCommandState();
            var version = ++_selectionVersion;
            _ = LoadSelectionAsync(value, version);
        }
    }

    public ResearchSourceItem? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (SetProperty(ref _selectedSource, value)) _openSourceCommand.RaiseCanExecuteChanged();
        }
    }

    public string EditorHeadline { get => _editorHeadline; set => SetProperty(ref _editorHeadline, value); }
    public string EditorSummary { get => _editorSummary; set => SetProperty(ref _editorSummary, value); }
    public string EditorStation { get => _editorStation; set => SetProperty(ref _editorStation, value); }
    public string EditorEdition { get => _editorEdition; set => SetProperty(ref _editorEdition, value); }
    public string EditorVariant { get => _editorVariant; set => SetProperty(ref _editorVariant, value); }
    public string EditorEra { get => _editorEra; set => SetProperty(ref _editorEra, value); }
    public string EditorEpisodeType { get => _editorEpisodeType; set => SetProperty(ref _editorEpisodeType, value); }
    public string EditorArchiveNotes { get => _editorArchiveNotes; set => SetProperty(ref _editorArchiveNotes, value); }
    public double EditorConfidence
    {
        get => _editorConfidence;
        set
        {
            if (SetProperty(ref _editorConfidence, Math.Clamp(value, 0d, 100d)))
                RaisePropertyChanged(nameof(EditorConfidenceText));
        }
    }
    public string EditorConfidenceText => $"{EditorConfidence:0}%";
    public string EditorConfidenceReason { get => _editorConfidenceReason; set => SetProperty(ref _editorConfidenceReason, value); }
    public bool EditorNeedsReview
    {
        get => _editorNeedsReview;
        set
        {
            if (!SetProperty(ref _editorNeedsReview, value)) return;
            RaisePropertyChanged(nameof(ReviewButtonText));
        }
    }
    public string EditorHosts { get => _editorHosts; set => SetProperty(ref _editorHosts, value); }
    public string EditorGuests { get => _editorGuests; set => SetProperty(ref _editorGuests, value); }
    public string EditorCallers { get => _editorCallers; set => SetProperty(ref _editorCallers, value); }
    public string EditorMentionedPeople { get => _editorMentionedPeople; set => SetProperty(ref _editorMentionedPeople, value); }
    public string EditorTopics { get => _editorTopics; set => SetProperty(ref _editorTopics, value); }
    public string EditorCatalogueSeries { get => _editorCatalogueSeries; set => SetProperty(ref _editorCatalogueSeries, value); }
    public string EditorCatalogueProgramme { get => _editorCatalogueProgramme; set => SetProperty(ref _editorCatalogueProgramme, value); }
    public string EditorCatalogueFormat { get => _editorCatalogueFormat; set => SetProperty(ref _editorCatalogueFormat, value); }
    public string EditorOriginalReleaseDate { get => _editorOriginalReleaseDate; set => SetProperty(ref _editorOriginalReleaseDate, value); }
    public string EditorRecordingDate { get => _editorRecordingDate; set => SetProperty(ref _editorRecordingDate, value); }
    public string EditorVenue { get => _editorVenue; set => SetProperty(ref _editorVenue, value); }
    public string EditorEvent { get => _editorEvent; set => SetProperty(ref _editorEvent, value); }
    public string EditorNetwork { get => _editorNetwork; set => SetProperty(ref _editorNetwork, value); }
    public string EditorCatalogueNumber { get => _editorCatalogueNumber; set => SetProperty(ref _editorCatalogueNumber, value); }
    public string EditorOriginalFilename { get => _editorOriginalFilename; set => SetProperty(ref _editorOriginalFilename, value); }
    public string EditorProvenance { get => _editorProvenance; set => SetProperty(ref _editorProvenance, value); }
    public string EditorResearchNotes { get => _editorResearchNotes; set => SetProperty(ref _editorResearchNotes, value); }
    public string? EditorArtworkPath
    {
        get => _editorArtworkPath;
        set
        {
            if (!SetProperty(ref _editorArtworkPath, value)) return;
            RaisePropertyChanged(nameof(HasArtwork));
        }
    }

    public Task LoadAsync()
    {
        if (IsBusy) return Task.CompletedTask;
        return LoadCoreAsync();
    }

    private async Task LoadCoreAsync()
    {
        IsBusy = true;
        _hasBrowseError = false;
        RaisePropertyChanged(nameof(ShowAttentionEmptyState));
        RaisePropertyChanged(nameof(ShowFilteredEmptyState));
        StatusText = "Loading the Knowledge workspace…";
        try
        {
            var overviewTask = _research.GetOverviewAsync();
            var collectionsTask = _research.GetCollectionsAsync();
            var diagnosticsTask = _research.GetSourceDiagnosticsAsync();
            var importsTask = _research.GetImportHistoryAsync();
            await Task.WhenAll(overviewTask, collectionsTask, diagnosticsTask, importsTask).ConfigureAwait(true);

            Overview = await overviewTask.ConfigureAwait(true);
            RaisePropertyChanged(nameof(Overview));
            var selectedCollectionId = SelectedCollection?.CollectionId;
            Replace(Collections, await collectionsTask.ConfigureAwait(true));
            SelectedCollection = Collections.FirstOrDefault(option => option.CollectionId == selectedCollectionId)
                ?? Collections.FirstOrDefault();
            Replace(SourceDiagnostics, await diagnosticsTask.ConfigureAwait(true));
            Replace(ImportHistory, await importsTask.ConfigureAwait(true));
            RaiseKnowledgeDashboardProperties();
            RaisePropertyChanged(nameof(HasDiagnostics));
            RaisePropertyChanged(nameof(HasImportHistory));
            await LoadCurrentModeCoreAsync().ConfigureAwait(true);
            _workspaceLoaded = true;
            StatusText = WorkspaceCountText;
        }
        catch (Exception exception)
        {
            _hasBrowseError = true;
            SetError(exception);
            Records.Clear();
            DateReviews.Clear();
            _allDateReviews.Clear();
            UndatedBroadcasts.Clear();
            CoverageYears.Clear();
            _coverage = null;
            SourceDiagnostics.Clear();
            ImportHistory.Clear();
            RaisePropertyChanged(nameof(HasRecords));
            RaisePropertyChanged(nameof(HasDateReviews));
            RaisePropertyChanged(nameof(HasUndatedBroadcasts));
            RaisePropertyChanged(nameof(Coverage));
            RaisePropertyChanged(nameof(HasCoverage));
            RaisePropertyChanged(nameof(WorkspaceCountText));
            RaisePropertyChanged(nameof(ShowAttentionEmptyState));
            RaisePropertyChanged(nameof(ShowFilteredEmptyState));
            RaisePropertyChanged(nameof(ShowCoverageEmptyState));
            RaisePropertyChanged(nameof(HasDiagnostics));
            RaisePropertyChanged(nameof(HasImportHistory));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SwitchModeAsync(ResearchWorkspaceMode mode)
    {
        if (WorkspaceMode == mode && _workspaceLoaded)
        {
            await LoadCurrentModeAsync().ConfigureAwait(true);
            return;
        }
        WorkspaceMode = mode;
        RaisePropertyChanged(nameof(NeedsCoverageShow));
        await LoadCurrentModeAsync().ConfigureAwait(true);
    }

    private async Task LoadCurrentModeAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _hasBrowseError = false;
        StatusText = WorkspaceMode switch
        {
            ResearchWorkspaceMode.DateReview => "Loading date decisions for every show…",
            ResearchWorkspaceMode.Undated => "Loading broadcasts without reliable dates…",
            ResearchWorkspaceMode.Coverage => "Building the metadata coverage heatmap…",
            _ => "Filtering research records…"
        };
        try
        {
            await LoadCurrentModeCoreAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _hasBrowseError = true;
            SetError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task LoadCurrentModeCoreAsync() => WorkspaceMode switch
    {
        ResearchWorkspaceMode.Dashboard => Task.CompletedTask,
        ResearchWorkspaceMode.DateReview => LoadDateReviewsCoreAsync(),
        ResearchWorkspaceMode.Undated => LoadUndatedCoreAsync(),
        ResearchWorkspaceMode.Coverage => LoadCoverageCoreAsync(),
        _ => LoadRecordsCoreAsync()
    };

    private void RaiseKnowledgeDashboardProperties()
    {
        RaisePropertyChanged(nameof(KnowledgeCoverageText));
        RaisePropertyChanged(nameof(KnowledgeDateRangeText));
        RaisePropertyChanged(nameof(KnowledgeShowsText));
        RaisePropertyChanged(nameof(KnowledgeAttentionText));
        RaisePropertyChanged(nameof(KnowledgeSourcesText));
        RaisePropertyChanged(nameof(KnowledgeSummariesText));
        RaisePropertyChanged(nameof(SummaryCoveragePercent));
        RaisePropertyChanged(nameof(PeopleCoveragePercent));
        RaisePropertyChanged(nameof(TopicCoveragePercent));
        RaisePropertyChanged(nameof(SourceCoveragePercent));
    }

    private async Task LoadDateReviewsCoreAsync(int? preferredIndex = null, long? preferredId = null)
    {
        var previousId = preferredId ?? SelectedDateReview?.ResearchId;
        var items = await _research.GetCatalogueDateReviewsAsync(
            SelectedCollection?.CollectionId, includeResolved: true).ConfigureAwait(true);
        _allDateReviews.Clear();
        _allDateReviews.AddRange(items);
        ApplyDateReviewQueue();
        SelectedDateReview = previousId.HasValue
            ? DateReviews.FirstOrDefault(x => x.ResearchId == previousId.Value)
                ?? DateReviews.ElementAtOrDefault(Math.Min(preferredIndex ?? 0, Math.Max(0, DateReviews.Count - 1)))
            : DateReviews.ElementAtOrDefault(Math.Min(preferredIndex ?? 0, Math.Max(0, DateReviews.Count - 1)));
        StatusText = WorkspaceCountText;
    }

    private void SwitchDateReviewQueue(DateReviewQueue queue)
    {
        if (_dateReviewQueue == queue) return;
        _dateReviewQueue = queue;
        RaisePropertyChanged(nameof(CurrentDateReviewQueue));
        RaisePropertyChanged(nameof(IsActiveDateReviewQueue));
        RaisePropertyChanged(nameof(IsIgnoredDateReviewQueue));
        RaisePropertyChanged(nameof(IsCompletedDateReviewQueue));
        RaisePropertyChanged(nameof(DateReviewEmptyTitle));
        RaisePropertyChanged(nameof(DateReviewEmptyHelp));
        ApplyDateReviewQueue();
        StatusText = WorkspaceCountText;
    }

    private void ApplyDateReviewQueue()
    {
        var filtered = _dateReviewQueue switch
        {
            DateReviewQueue.Ignored => _allDateReviews.Where(item => item.IsIgnored),
            DateReviewQueue.Completed => _allDateReviews.Where(item => item.IsCompleted),
            _ => _allDateReviews.Where(item => item.IsPending)
        };
        Replace(DateReviews, filtered);
        SelectedDateReview = DateReviews.FirstOrDefault();
        RaisePropertyChanged(nameof(HasDateReviews));
        RaisePropertyChanged(nameof(WorkspaceCountText));
        RaisePropertyChanged(nameof(ActiveDateReviewCount));
        RaisePropertyChanged(nameof(IgnoredDateReviewCount));
        RaisePropertyChanged(nameof(CompletedDateReviewCount));
        RaisePropertyChanged(nameof(ActiveDateReviewLabel));
        RaisePropertyChanged(nameof(IgnoredDateReviewLabel));
        RaisePropertyChanged(nameof(CompletedDateReviewLabel));
    }

    private async Task LoadUndatedCoreAsync()
    {
        var previousEpisodeId = SelectedUndatedBroadcast?.EpisodeId;
        var items = await _research.GetUndatedBroadcastsAsync(SelectedCollection?.CollectionId).ConfigureAwait(true);
        Replace(UndatedBroadcasts, items);
        SelectedUndatedBroadcast = previousEpisodeId.HasValue
            ? UndatedBroadcasts.FirstOrDefault(x => x.EpisodeId == previousEpisodeId.Value) ?? UndatedBroadcasts.FirstOrDefault()
            : UndatedBroadcasts.FirstOrDefault();
        RaisePropertyChanged(nameof(HasUndatedBroadcasts));
        RaisePropertyChanged(nameof(WorkspaceCountText));
        StatusText = WorkspaceCountText;
    }

    private async Task LoadCoverageCoreAsync()
    {
        CoverageYears.Clear();
        _coverage = null;
        RaisePropertyChanged(nameof(Coverage));
        RaisePropertyChanged(nameof(HasCoverage));
        RaisePropertyChanged(nameof(NeedsCoverageShow));
        RaisePropertyChanged(nameof(ShowCoverageEmptyState));
        if (SelectedCollection?.CollectionId is not > 0)
        {
            StatusText = "Choose a show to build its full-run metadata heatmap.";
            RaisePropertyChanged(nameof(WorkspaceCountText));
            return;
        }

        _coverage = await _research.GetCoverageAsync(SelectedCollection.CollectionId.Value).ConfigureAwait(true);
        if (_coverage is not null)
        {
            foreach (var group in _coverage.Days.GroupBy(x => x.Date.Year).OrderByDescending(x => x.Key))
                CoverageYears.Add(new ResearchCoverageYearViewModel(group.Key, group));
        }
        RaisePropertyChanged(nameof(Coverage));
        RaisePropertyChanged(nameof(HasCoverage));
        RaisePropertyChanged(nameof(NeedsCoverageShow));
        RaisePropertyChanged(nameof(ShowCoverageEmptyState));
        RaisePropertyChanged(nameof(WorkspaceCountText));
        StatusText = WorkspaceCountText;
    }

    private async Task ResolveDateReviewAsync(CatalogueDateReviewAction action)
    {
        if (SelectedDateReview is null) return;
        IsBusy = true;
        var selectedId = SelectedDateReview.ResearchId;
        var selectedIndex = Math.Max(0, DateReviews.IndexOf(SelectedDateReview));
        var hadCurrentLibraryDate = SelectedDateReview.HasCurrentLibraryDate;
        var date = SelectedDateReviewDate.HasValue
            ? DateOnly.FromDateTime(SelectedDateReviewDate.Value.Date)
            : (DateOnly?)null;
        var approvedDateText = date.HasValue ? date.Value.ToString("dd MMM yyyy") : "the selected date";
        StatusText = action switch
        {
            CatalogueDateReviewAction.ApproveLibraryDate => "Approving the Library date…",
            CatalogueDateReviewAction.KeepExisting => "Keeping the current Library date…",
            CatalogueDateReviewAction.Ignore => "Ignoring this suggestion…",
            CatalogueDateReviewAction.KeepAsRecordingDate => "Keeping the date as recording evidence…",
            CatalogueDateReviewAction.KeepAsReleaseDate => "Keeping the date as release/archive evidence…",
            CatalogueDateReviewAction.Reopen => "Reopening the date decision…",
            _ => "Leaving the Library item undated…"
        };
        try
        {
            await _research.ResolveCatalogueDateReviewAsync(selectedId, action, date).ConfigureAwait(true);
            if (action != CatalogueDateReviewAction.Reopen)
            {
                _lastDateDecisionResearchId = selectedId;
                _undoDateDecisionText = action switch
                {
                    CatalogueDateReviewAction.ApproveLibraryDate => "Undo approval",
                    CatalogueDateReviewAction.KeepExisting => "Undo keep existing",
                    CatalogueDateReviewAction.Ignore => "Undo ignore",
                    _ => "Undo last decision"
                };
                RaisePropertyChanged(nameof(CanUndoDateDecision));
                RaisePropertyChanged(nameof(UndoDateDecisionText));
                _undoDateDecisionCommand.RaiseCanExecuteChanged();
            }
            else if (_lastDateDecisionResearchId == selectedId)
            {
                ClearDateDecisionUndo();
            }
            await LoadDateReviewsCoreAsync(selectedIndex).ConfigureAwait(true);
            Overview = await _research.GetOverviewAsync().ConfigureAwait(true);
            RaisePropertyChanged(nameof(Overview));
            StatusText = action switch
            {
                CatalogueDateReviewAction.ApproveLibraryDate => $"Approved {approvedDateText} as the Library date.",
                CatalogueDateReviewAction.KeepExisting => hadCurrentLibraryDate
                    ? "Kept the existing Library date."
                    : "Kept this Library item undated.",
                CatalogueDateReviewAction.Ignore => "Suggestion moved to Ignored. You can restore it at any time.",
                CatalogueDateReviewAction.KeepAsRecordingDate => hadCurrentLibraryDate
                    ? "Date kept as recording evidence; the existing Library date was left unchanged."
                    : "Date kept as recording evidence; the Library item remains undated.",
                CatalogueDateReviewAction.KeepAsReleaseDate => hadCurrentLibraryDate
                    ? "Date kept as release/archive evidence; the existing Library date was left unchanged."
                    : "Date kept as release/archive evidence; the Library item remains undated.",
                CatalogueDateReviewAction.Reopen => "Date decision reopened.",
                _ => "Item left undated with the decision preserved."
            };
        }
        finally { IsBusy = false; }
    }

    private async Task UndoDateDecisionAsync()
    {
        if (_lastDateDecisionResearchId is not { } researchId) return;
        IsBusy = true;
        StatusText = "Undoing the last date decision…";
        try
        {
            await _research.ResolveCatalogueDateReviewAsync(researchId, CatalogueDateReviewAction.Reopen).ConfigureAwait(true);
            _dateReviewQueue = DateReviewQueue.Active;
            RaisePropertyChanged(nameof(CurrentDateReviewQueue));
            RaisePropertyChanged(nameof(IsActiveDateReviewQueue));
            RaisePropertyChanged(nameof(IsIgnoredDateReviewQueue));
            RaisePropertyChanged(nameof(IsCompletedDateReviewQueue));
            ClearDateDecisionUndo();
            await LoadDateReviewsCoreAsync(preferredId: researchId).ConfigureAwait(true);
            StatusText = "Last date decision undone and returned to Active.";
        }
        finally { IsBusy = false; }
    }

    private void ClearDateDecisionUndo()
    {
        _lastDateDecisionResearchId = null;
        _undoDateDecisionText = string.Empty;
        RaisePropertyChanged(nameof(CanUndoDateDecision));
        RaisePropertyChanged(nameof(UndoDateDecisionText));
        _undoDateDecisionCommand.RaiseCanExecuteChanged();
    }

    private async Task AssignUndatedDateAsync()
    {
        if (SelectedUndatedBroadcast is null || !SelectedUndatedDate.HasValue) return;
        IsBusy = true;
        var selectedEpisodeId = SelectedUndatedBroadcast.EpisodeId;
        var date = DateOnly.FromDateTime(SelectedUndatedDate.Value.Date);
        StatusText = $"Assigning {date:dd MMM yyyy}…";
        try
        {
            await _research.AssignBroadcastDateAsync(selectedEpisodeId, date).ConfigureAwait(true);
            await LoadUndatedCoreAsync().ConfigureAwait(true);
            Overview = await _research.GetOverviewAsync().ConfigureAwait(true);
            RaisePropertyChanged(nameof(Overview));
            StatusText = $"Broadcast date set to {date:dd MMM yyyy} with protected manual provenance.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnFiltersChanged()
    {
        _filterVersion++;
        RaisePropertyChanged(nameof(HasActiveFilters));
        if (_suppressAutomaticRefresh || !_workspaceLoaded) return;

        _filterDebounce?.Cancel();
        _filterDebounce?.Dispose();
        _filterDebounce = new CancellationTokenSource();
        var version = _filterVersion;
        var token = _filterDebounce.Token;
        _ = RefreshFiltersAfterPauseAsync(version, token);
    }

    private async Task RefreshFiltersAfterPauseAsync(int version, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(true);
            while (IsBusy && version == _filterVersion)
                await Task.Delay(80, cancellationToken).ConfigureAwait(true);
            if (version == _filterVersion)
                await LoadCurrentModeAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadRecordsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _hasBrowseError = false;
        StatusText = "Filtering research records…";
        try
        {
            await LoadRecordsCoreAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _hasBrowseError = true;
            SetError(exception);
            RaisePropertyChanged(nameof(ShowAttentionEmptyState));
            RaisePropertyChanged(nameof(ShowFilteredEmptyState));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadRecordsCoreAsync()
    {
        var previousId = SelectedRecord?.ResearchId;
        var items = await _research.BrowseAsync(new ResearchBrowseQuery(
            SearchText, SelectedCollection?.CollectionId, SelectedStatus?.Key ?? "attention", NeedsReviewOnly)).ConfigureAwait(true);
        Replace(Records, items);
        RaisePropertyChanged(nameof(HasRecords));
        RaisePropertyChanged(nameof(ShowAttentionEmptyState));
        RaisePropertyChanged(nameof(ShowFilteredEmptyState));
        RaisePropertyChanged(nameof(RecordCountText));
        RaisePropertyChanged(nameof(BrowseHeading));
        RaisePropertyChanged(nameof(BrowseDescription));
        SelectedRecord = previousId.HasValue ? Records.FirstOrDefault(x => x.ResearchId == previousId.Value) : Records.FirstOrDefault();
        RaisePropertyChanged(nameof(WorkspaceCountText));
        StatusText = RecordCountText;
    }

    private async Task ClearFiltersAsync()
    {
        _suppressAutomaticRefresh = true;
        try
        {
            SearchText = string.Empty;
            SelectedCollection = Collections.FirstOrDefault();
            SelectedStatus = Statuses.FirstOrDefault(x => string.Equals(x.Key, "attention", StringComparison.OrdinalIgnoreCase))
                ?? Statuses.FirstOrDefault();
            NeedsReviewOnly = false;
        }
        finally { _suppressAutomaticRefresh = false; }
        await LoadCurrentModeAsync().ConfigureAwait(true);
    }

    private async Task LoadSelectionAsync(ResearchBrowseItem? record, int version)
    {
        Sources.Clear();
        SelectedSource = null;
        _details = null;
        ClearEditor();
        if (record is null)
        {
            RaiseSelectionState();
            return;
        }

        try
        {
            var details = await _research.GetDetailsAsync(record.ResearchId).ConfigureAwait(true);
            if (version != _selectionVersion || SelectedRecord?.ResearchId != record.ResearchId) return;
            _details = details;
            if (details is null)
            {
                StatusText = "The selected Knowledge record no longer exists.";
                RaiseSelectionState();
                return;
            }
            ApplyDetails(details);
            Replace(Sources, details.Sources);
            SelectedSource = Sources.FirstOrDefault();
            StatusText = $"Loaded {details.Record.ShowName} {details.Record.DateText}";
            RaiseSelectionState();
        }
        catch (Exception exception)
        {
            if (version != _selectionVersion) return;
            SetError(exception);
        }
    }

    private async Task ReloadSelectionAsync()
    {
        if (SelectedRecord is null) return;
        var version = ++_selectionVersion;
        await LoadSelectionAsync(SelectedRecord, version).ConfigureAwait(true);
    }

    private async Task SaveAsync()
    {
        if (SelectedRecord is null) return;
        IsBusy = true;
        StatusText = "Saving Metadata Studio changes…";
        try
        {
            await _research.SaveMetadataAsync(new ResearchMetadataUpdate(
                SelectedRecord.ResearchId, EditorHeadline, EditorSummary, EditorStation, EditorEdition,
                EditorVariant, EditorEra, EditorEpisodeType, EditorArchiveNotes, (int)Math.Round(EditorConfidence),
                EditorConfidenceReason, EditorNeedsReview, EditorHosts, EditorGuests, EditorCallers,
                EditorMentionedPeople, EditorTopics, EditorCatalogueSeries, EditorCatalogueProgramme,
                EditorCatalogueFormat, EditorOriginalReleaseDate, EditorRecordingDate, EditorVenue,
                EditorEvent, EditorNetwork, EditorCatalogueNumber, EditorOriginalFilename,
                EditorProvenance, EditorResearchNotes, EditorArtworkPath)).ConfigureAwait(true);
            await LoadRecordsCoreAsync().ConfigureAwait(true);
            StatusText = "Metadata saved with manual provenance.";
        }
        finally { IsBusy = false; }
    }

    private async Task ChooseImportAsync()
    {
        var path = await _files.PickOpenFileAsync(new FileSelectionRequest(
            Title: "Choose a Radio Vault research pack",
            Filter: "Radio Vault Archive Knowledge Databases|*.trvknowledge|All files|*.*")).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return;
        _selectedImportPath = path;
        RaisePropertyChanged(nameof(SelectedImportFileText));
        await PreviewImportAsync(path).ConfigureAwait(true);
    }

    private Task RetryImportAsync()
        => string.IsNullOrWhiteSpace(_selectedImportPath)
            ? Task.CompletedTask
            : PreviewImportAsync(_selectedImportPath);

    private async Task PreviewImportAsync(string path)
    {
        _importErrorText = string.Empty;
        _importPreview = null;
        RaiseImportFeedbackState();
        IsImportPreviewBusy = true;
        IsBusy = true;
        ResetImportProgress("Opening the Knowledge Database…");
        StatusText = "Checking the research pack against the archive…";
        // Give Avalonia a render turn before pack parsing or upload begins.
        await Task.Yield();
        try
        {
            var progress = new Progress<ResearchPackTransferProgress>(UpdateImportProgress);
            _importPreview = await _transfers.PreviewImportAsync(path, progress).ConfigureAwait(true);
            RaiseImportFeedbackState();
            StatusText = _importPreview.SummaryText;
        }
        catch (Exception exception)
        {
            _importPreview = null;
            _importErrorText = string.IsNullOrWhiteSpace(exception.Message)
                ? "Radio Vault could not analyse the selected Archive Knowledge Database."
                : exception.Message.Trim();
            StatusText = _importErrorText;
            RaiseImportFeedbackState();
        }
        finally
        {
            IsBusy = false;
            IsImportPreviewBusy = false;
            RaiseTransferCommandState();
        }
    }

    private async Task ApplyImportAsync()
    {
        if (!HasPendingImport) return;
        IsImportApplyBusy = true;
        IsBusy = true;
        _activeImportCancellation?.Dispose();
        _activeImportCancellation = new CancellationTokenSource();
        ResetImportProgress("Starting the server-owned import job…");
        StatusText = IsAuthoritativeImport
            ? "Applying authoritative audited research replacement…"
            : "Importing research with protected manual metadata…";
        // Local imports create a safety backup before their first asynchronous
        // database operation; yield first so the busy UI cannot appear frozen.
        await Task.Yield();
        try
        {
            var progress = new Progress<ResearchPackTransferProgress>(UpdateImportProgress);
            var result = await _transfers.ApplyImportAsync(progress, _activeImportCancellation.Token).ConfigureAwait(true);
            _importPreview = null;
            _importErrorText = string.Empty;
            _selectedImportPath = null;
            RaiseImportFeedbackState();
            await LoadCoreAsync().ConfigureAwait(true);
            StatusText = result.SummaryText;
        }
        catch (OperationCanceledException)
        {
            _importErrorText = string.Empty;
            StatusText = "Knowledge Database import cancelled. No partial changes were kept.";
            RaiseImportFeedbackState();
        }
        catch (Exception exception)
        {
            _importErrorText = string.IsNullOrWhiteSpace(exception.Message)
                ? "Radio Vault could not finish importing the Archive Knowledge Database."
                : exception.Message.Trim();
            StatusText = _importErrorText;
            RaiseImportFeedbackState();
        }
        finally
        {
            _activeImportCancellation?.Dispose();
            _activeImportCancellation = null;
            IsBusy = false;
            IsImportApplyBusy = false;
            RaiseTransferCommandState();
        }
    }

    private async Task CancelImportAsync()
    {
        await _transfers.CancelImportAsync().ConfigureAwait(true);
        _activeImportCancellation?.Cancel();
        _importPreview = null;
        _importErrorText = string.Empty;
        _selectedImportPath = null;
        RaiseImportFeedbackState();
        StatusText = "Knowledge import cancelled.";
        RaiseTransferCommandState();
    }

    private void ResetImportProgress(string message)
    {
        ImportProgressPercent = 0;
        ImportProgressText = message;
        ImportProgressCountText = string.Empty;
    }

    private void UpdateImportProgress(ResearchPackTransferProgress value)
    {
        ImportProgressPercent = value.ClampedPercent;
        ImportProgressText = value.Message;
        ImportProgressCountText = value.CountText;
        StatusText = value.Message;
    }

    private Task ExportAsync()
        => ExportKnowledgeDatabaseAsync(KnowledgeExportScope.Complete);

    public Task ExportKnowledgeDatabaseAsync(CancellationToken cancellationToken = default)
        => ExportKnowledgeDatabaseAsync(KnowledgeExportScope.Complete, cancellationToken);

    public async Task ExportKnowledgeDatabaseAsync(
        KnowledgeExportScope scope,
        CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        StatusText = $"Preparing {scope.DisplayName()} for export…";
        try
        {
            var export = await _transfers.ExportAsync(scope, cancellationToken).ConfigureAwait(true);
            var path = await _files.PickSaveFileAsync(new FileSelectionRequest(
                Title: "Export Radio Vault Archive Knowledge Database",
                Filter: "Radio Vault Archive Knowledge Databases|*.trvknowledge",
                DefaultExtension: ".trvknowledge",
                SuggestedFileName: export.SuggestedFileName,
                CheckFileExists: false)).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusText = "Knowledge export cancelled.";
                return;
            }
            await File.WriteAllBytesAsync(path, export.Bytes, cancellationToken).ConfigureAwait(true);
            StatusText = $"Exported {export.BroadcastCount:N0} broadcasts, {export.TranscriptCount:N0} matching transcripts and {export.MissingCount:N0} research gaps.";
        }
        finally { IsBusy = false; }
    }

    public void ReportTransferError(Exception exception) => SetError(exception);

    private async Task ChooseArtworkAsync()
    {
        if (!CanEditArtwork) return;
        var path = await _files.PickOpenFileAsync(new FileSelectionRequest(
            Title: "Choose broadcast artwork",
            Filter: "Image files|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All files|*.*")).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(path)) EditorArtworkPath = path;
    }

    private void ClearArtwork() => EditorArtworkPath = null;

    private async Task PlayAsync()
    {
        if (SelectedRecord?.EpisodeId is not > 0) return;
        await _playback.LoadAndPlayAsync(SelectedRecord.EpisodeId.Value).ConfigureAwait(true);
    }

    private async Task OpenSourceAsync()
    {
        if (SelectedSource?.HasUrl != true) return;
        await _launcher.LaunchAsync(ExternalLaunchRequest.Uri(new Uri(SelectedSource.Url, UriKind.Absolute))).ConfigureAwait(true);
    }

    private async Task ToggleReviewAsync()
    {
        if (SelectedRecord is null) return;
        var next = !EditorNeedsReview;
        await _research.SetNeedsReviewAsync(SelectedRecord.ResearchId, next).ConfigureAwait(true);
        EditorNeedsReview = next;
        await LoadRecordsCoreAsync().ConfigureAwait(true);
        StatusText = next ? "Record marked for review." : "Review flag cleared.";
    }

    private void ApplyDetails(ResearchRecordDetails details)
    {
        EditorHeadline = details.Record.Headline;
        EditorSummary = details.Record.Summary;
        EditorStation = details.Station;
        EditorEdition = details.Edition;
        EditorVariant = details.BroadcastVariant;
        EditorEra = details.BroadcastEra;
        EditorEpisodeType = details.EpisodeType;
        EditorArchiveNotes = details.ArchiveNotes;
        EditorConfidence = details.Record.Confidence;
        EditorConfidenceReason = details.ConfidenceReason;
        EditorNeedsReview = details.Record.NeedsReview;
        EditorHosts = details.Hosts;
        EditorGuests = details.Guests;
        EditorCallers = details.Callers;
        EditorMentionedPeople = details.MentionedPeople;
        EditorTopics = details.Topics;
        EditorCatalogueSeries = details.CatalogueSeries;
        EditorCatalogueProgramme = details.CatalogueProgramme;
        EditorCatalogueFormat = details.CatalogueFormat;
        EditorOriginalReleaseDate = details.OriginalReleaseDate;
        EditorRecordingDate = details.RecordingDate;
        EditorVenue = details.Venue;
        EditorEvent = details.Event;
        EditorNetwork = details.Network;
        EditorCatalogueNumber = details.CatalogueNumber;
        EditorOriginalFilename = details.OriginalFilename;
        EditorProvenance = details.Provenance;
        EditorResearchNotes = details.ResearchNotes;
        EditorArtworkPath = details.ArtworkPath;
    }

    private void ClearEditor()
    {
        EditorHeadline = string.Empty;
        EditorSummary = string.Empty;
        EditorStation = string.Empty;
        EditorEdition = string.Empty;
        EditorVariant = string.Empty;
        EditorEra = string.Empty;
        EditorEpisodeType = string.Empty;
        EditorArchiveNotes = string.Empty;
        EditorConfidence = 0;
        EditorConfidenceReason = string.Empty;
        EditorNeedsReview = false;
        EditorHosts = string.Empty;
        EditorGuests = string.Empty;
        EditorCallers = string.Empty;
        EditorMentionedPeople = string.Empty;
        EditorTopics = string.Empty;
        EditorCatalogueSeries = string.Empty;
        EditorCatalogueProgramme = string.Empty;
        EditorCatalogueFormat = string.Empty;
        EditorOriginalReleaseDate = string.Empty;
        EditorRecordingDate = string.Empty;
        EditorVenue = string.Empty;
        EditorEvent = string.Empty;
        EditorNetwork = string.Empty;
        EditorCatalogueNumber = string.Empty;
        EditorOriginalFilename = string.Empty;
        EditorProvenance = string.Empty;
        EditorResearchNotes = string.Empty;
        EditorArtworkPath = null;
    }

    private void RaiseSelectionState()
    {
        RaisePropertyChanged(nameof(HasSelection));
        RaisePropertyChanged(nameof(HasSources));
        RaisePropertyChanged(nameof(CanEditArtwork));
        RaisePropertyChanged(nameof(CanEditAdvancedMetadata));
        RaisePropertyChanged(nameof(CanEditReviewState));
        RaisePropertyChanged(nameof(HasArtwork));
        RaisePropertyChanged(nameof(ArtworkHelpText));
        RaisePropertyChanged(nameof(MetadataOwnershipHelpText));
        RaisePropertyChanged(nameof(SelectionTitle));
        RaisePropertyChanged(nameof(SelectionIdentity));
        RaisePropertyChanged(nameof(IsCatalogueResearch));
        RaisePropertyChanged(nameof(CataloguePanelTitle));
        RaisePropertyChanged(nameof(CataloguePanelHelpText));
        RaiseCommandState();
    }

    private void RaiseCommandState()
    {
        _saveCommand.RaiseCanExecuteChanged();
        _revertCommand.RaiseCanExecuteChanged();
        _chooseArtworkCommand.RaiseCanExecuteChanged();
        _playCommand.RaiseCanExecuteChanged();
        _openSourceCommand.RaiseCanExecuteChanged();
        _toggleReviewCommand.RaiseCanExecuteChanged();
        _showDashboardCommand.RaiseCanExecuteChanged();
        _showRecordsCommand.RaiseCanExecuteChanged();
        _showDateReviewCommand.RaiseCanExecuteChanged();
        _showUndatedCommand.RaiseCanExecuteChanged();
        _showCoverageCommand.RaiseCanExecuteChanged();
        _assignUndatedDateCommand.RaiseCanExecuteChanged();
        _approveDateCommand.RaiseCanExecuteChanged();
        _keepExistingDateCommand.RaiseCanExecuteChanged();
        _ignoreDateCommand.RaiseCanExecuteChanged();
        _undoDateDecisionCommand.RaiseCanExecuteChanged();
        _keepRecordingDateCommand.RaiseCanExecuteChanged();
        _keepReleaseDateCommand.RaiseCanExecuteChanged();
        _leaveUndatedCommand.RaiseCanExecuteChanged();
        _reopenDateDecisionCommand.RaiseCanExecuteChanged();
        RaiseTransferCommandState();
        if (ClearArtworkCommand is DelegateCommand clear) clear.RaiseCanExecuteChanged();
    }

    private void RaiseTransferCommandState()
    {
        _chooseImportCommand.RaiseCanExecuteChanged();
        _retryImportCommand.RaiseCanExecuteChanged();
        _applyImportCommand.RaiseCanExecuteChanged();
        _cancelImportCommand.RaiseCanExecuteChanged();
        _exportCommand.RaiseCanExecuteChanged();
        _exportUndatedCommand.RaiseCanExecuteChanged();
        _exportMissingResearchCommand.RaiseCanExecuteChanged();
    }

    private void RaiseImportFeedbackState()
    {
        RaisePropertyChanged(nameof(HasPendingImport));
        RaisePropertyChanged(nameof(HasImportError));
        RaisePropertyChanged(nameof(CanRetryImport));
        RaisePropertyChanged(nameof(ImportErrorText));
        RaisePropertyChanged(nameof(SelectedImportFileText));
        RaisePropertyChanged(nameof(IsAuthoritativeImport));
        RaisePropertyChanged(nameof(ImportPreviewText));
        RaisePropertyChanged(nameof(ImportModeText));
        RaiseTransferCommandState();
    }

    private void SetError(Exception exception) => StatusText = exception.Message;

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }
}


public sealed class ResearchCoverageYearViewModel
{
    public ResearchCoverageYearViewModel(int year, IEnumerable<ResearchCoverageDay> days)
    {
        Year = year;
        var byDate = days.ToDictionary(x => x.Date);
        var first = new DateOnly(year, 1, 1);
        var sundayOffset = (int)first.DayOfWeek;
        var cells = new List<ResearchCoverageCellViewModel>(7 * 54);
        for (var dayOfWeek = 0; dayOfWeek < 7; dayOfWeek++)
        {
            for (var week = 0; week < 54; week++)
            {
                var dayIndex = week * 7 + dayOfWeek - sundayOffset;
                var date = first.AddDays(dayIndex);
                if (date.Year == year && byDate.TryGetValue(date, out var day))
                    cells.Add(new ResearchCoverageCellViewModel(day));
                else
                    cells.Add(ResearchCoverageCellViewModel.Padding);
            }
        }
        Cells = cells;
    }

    public int Year { get; }
    public IReadOnlyList<ResearchCoverageCellViewModel> Cells { get; }
}

public sealed class ResearchCoverageCellViewModel
{
    private ResearchCoverageCellViewModel() { IsPadding = true; }
    public ResearchCoverageCellViewModel(ResearchCoverageDay day) => Day = day;
    public static ResearchCoverageCellViewModel Padding { get; } = new();
    public ResearchCoverageDay? Day { get; }
    public bool IsPadding { get; }
    public bool IsGap => Day?.IsGap == true;
    public bool IsWeekend => Day?.IsEmptyWeekend == true;
    public bool IsKnownMissing => Day?.IsKnownMissing == true;
    public bool IsCritical => Day?.IsCritical == true;
    public bool IsSparse => Day?.IsSparse == true;
    public bool IsPartial => Day?.IsPartial == true;
    public bool IsComplete => Day?.IsComplete == true;
    public string ToolTipText => Day?.ToolTipText ?? string.Empty;
}
