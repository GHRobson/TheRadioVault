using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows.Input;
using TheRadioVault.Application.Abstractions;
using TheRadioVault.Application.Models;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Jobs;
using TheRadioVault.Services.Models;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;
using TheRadioVault.Transcription.Services;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class TranscriptsViewModel : ObservableObject, IDisposable
{
    private readonly ITranscriptRepository _repository;
    private readonly ISpeakerIdentityRepository _speakers;
    private readonly IVoiceLearningCoordinator _voiceLearning;
    private readonly ITranscriptionCoordinator _coordinator;
    private readonly ITranscriptionBatchCoordinator _batchCoordinator;
    private readonly IServerTranscriptionAdministrationService _transcriptionAdministration;
    private readonly ILibraryBrowseService _library;
    private readonly IBackgroundJobQueue _backgroundJobs;
    private readonly IUiDispatcher _dispatcher;
    private readonly IFileSelectionService _files;
    private readonly PlaybackViewModel _playback;
    private readonly TranscriptReviewService _review = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly HashSet<(long EpisodeId, int Revision)> _voiceMatchesAttempted = new();
    private bool _isLoaded;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private readonly List<int> _searchMatches = new();
    private TranscriptionBatchCollectionOption? _selectedBatchCollection;
    private TranscriptionBatchYearOption? _selectedBatchYear;
    private DateTimeOffset? _batchFromDate;
    private DateTimeOffset? _batchToDate;
    private TranscriptionBatchRecord? _selectedBatch;
    private TranscriptionBatchItemRecord? _selectedBatchItem;
    private bool _isBatchBuilderOpen;
    private bool _isBatchPreviewBusy;
    private string _batchPreviewText = "Choose a show or date range, then preview the batch.";
    private DateTimeOffset _lastBatchProgressRefresh = DateTimeOffset.MinValue;
    private TranscriptSummary? _selectedTranscriptSummary;
    private TranscriptDocument? _selectedTranscript;
    private TranscriptSegment? _selectedSegment;
    private TranscriptPersonCandidate? _selectedSpeakerCandidate;
    private TranscriptionJobRecord? _selectedJob;
    private bool _isBusy;
    private bool _isTranscriptionActive;
    private bool _isSpeakerAnalysisBusy;
    private bool _currentHasTranscript;
    private string _statusText = "Transcripts have not been loaded yet.";
    private string _activityText = string.Empty;
    private string _phraseEditorText = string.Empty;
    private string _speakerEditorName = string.Empty;
    private string _transcriptSearchText = string.Empty;
    private int _activeSearchMatch = -1;
    private Func<Task>? _openSettings;
    private Func<Task>? _openResearch;
    private int _selectionVersion;
    private bool _syncingEditor;
    private bool _disposed;
    private ServerTranscriptionAdministrationSnapshot _transcriptionSnapshot = new(
        new ServerTranscriptionAdministrationStatus(
            false, "Checking the active server's transcription service…", "", "", "", false, "", false),
        new WhisperCppEngineSettings());

    public TranscriptsViewModel(
        ITranscriptRepository repository,
        ISpeakerIdentityRepository speakers,
        IVoiceLearningCoordinator voiceLearning,
        ITranscriptionCoordinator coordinator,
        ITranscriptionBatchCoordinator batchCoordinator,
        IServerTranscriptionAdministrationService transcriptionAdministration,
        ILibraryBrowseService library,
        IBackgroundJobQueue backgroundJobs,
        IUiDispatcher dispatcher,
        IFileSelectionService files,
        PlaybackViewModel playback)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _speakers = speakers ?? throw new ArgumentNullException(nameof(speakers));
        _voiceLearning = voiceLearning ?? throw new ArgumentNullException(nameof(voiceLearning));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _batchCoordinator = batchCoordinator ?? throw new ArgumentNullException(nameof(batchCoordinator));
        _transcriptionAdministration = transcriptionAdministration ?? throw new ArgumentNullException(nameof(transcriptionAdministration));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _backgroundJobs = backgroundJobs ?? throw new ArgumentNullException(nameof(backgroundJobs));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));

        RefreshCommand = new AsyncCommand(() => LoadAsync(force: true), () => !IsBusy, SetError);
        TranscribeCurrentCommand = new AsyncCommand(() => QueueCurrentAsync(sample: false), () => CanStartTranscription, SetError);
        TranscribeSampleCommand = new AsyncCommand(() => QueueCurrentAsync(sample: true), () => CanStartSample, SetError);
        PlaySelectedSegmentCommand = new AsyncCommand(PlaySelectedSegmentAsync, () => SelectedSegment is not null && SelectedTranscriptSummary is not null, SetError);
        SavePhraseCommand = new AsyncCommand(SaveSelectedPhraseAsync, () => CanSavePhrase, SetError);
        TogglePhraseReviewCommand = new AsyncCommand(ToggleSelectedPhraseReviewAsync, () => SelectedSegment is not null && !IsBusy, SetError);
        SplitPhraseCommand = new AsyncCommand(SplitSelectedPhraseAsync, () => CanSplitPhrase, SetError);
        MergeNextPhraseCommand = new AsyncCommand(MergeSelectedPhraseAsync, () => CanMergePhrase, SetError);
        ConfirmSpeakerCommand = new AsyncCommand(ConfirmSelectedSpeakerAsync, () => CanConfirmSpeaker, SetError);
        ClearSpeakerCommand = new AsyncCommand(ClearSelectedSpeakerAsync, () => CanClearSpeaker, SetError);
        PreviousMatchCommand = new DelegateCommand(() => MoveSearchMatch(-1), () => HasSearchMatches);
        NextMatchCommand = new DelegateCommand(() => MoveSearchMatch(1), () => HasSearchMatches);
        ExportTextCommand = new AsyncCommand(() => ExportTranscriptAsync("txt"), () => SelectedTranscript is not null && !IsBusy, SetError);
        ExportSrtCommand = new AsyncCommand(() => ExportTranscriptAsync("srt"), () => SelectedTranscript is not null && !IsBusy, SetError);
        ExportVttCommand = new AsyncCommand(() => ExportTranscriptAsync("vtt"), () => SelectedTranscript is not null && !IsBusy, SetError);
        RetrySelectedJobCommand = new AsyncCommand(RetrySelectedJobAsync, () => SelectedJob?.CanRetry == true, SetError);
        PauseSelectedJobCommand = new AsyncCommand(PauseSelectedJobAsync, () => SelectedJob?.CanPause == true, SetError);
        ResumeSelectedJobCommand = new AsyncCommand(ResumeSelectedJobAsync, () => SelectedJob?.CanResume == true, SetError);
        CancelSelectedJobCommand = new DelegateCommand(CancelSelectedJob, () => CanCancelSelectedJob);
        OpenSettingsCommand = new AsyncCommand(() => _openSettings?.Invoke() ?? Task.CompletedTask);
        OpenResearchCommand = new AsyncCommand(() => _openResearch?.Invoke() ?? Task.CompletedTask);
        ToggleBatchBuilderCommand = new DelegateCommand(ToggleBatchBuilder);
        PreviewBatchCommand = new AsyncCommand(PreviewBatchAsync, () => !IsBatchPreviewBusy && !IsBusy, SetError);
        StartBatchCommand = new AsyncCommand(StartBatchAsync, () => CanStartBatch, SetError);
        PauseBatchCommand = new AsyncCommand(PauseSelectedBatchAsync, () => SelectedBatch?.CanPause == true && !IsBusy, SetError);
        ResumeBatchCommand = new AsyncCommand(ResumeSelectedBatchAsync, () => SelectedBatch?.CanResume == true && !IsBusy, SetError);
        CancelBatchCommand = new AsyncCommand(CancelSelectedBatchAsync, () => SelectedBatch?.CanCancel == true && !IsBusy, SetError);
        RetryFailedBatchCommand = new AsyncCommand(RetryFailedBatchAsync, () => SelectedBatch?.CanRetryFailed == true && !IsBusy, SetError);
        MoveBatchItemUpCommand = new AsyncCommand(() => MoveSelectedBatchItemAsync(-1), () => SelectedBatchItem?.CanReorder == true && !IsBusy, SetError);
        MoveBatchItemDownCommand = new AsyncCommand(() => MoveSelectedBatchItemAsync(1), () => SelectedBatchItem?.CanReorder == true && !IsBusy, SetError);

        _backgroundJobs.ProgressChanged += BackgroundJobsOnProgressChanged;
        _playback.PropertyChanged += PlaybackOnPropertyChanged;
    }

    public ObservableCollection<TranscriptSummary> Transcripts { get; } = new();
    public ObservableCollection<TranscriptionJobRecord> Jobs { get; } = new();
    public ObservableCollection<TranscriptPersonCandidate> SpeakerCandidates { get; } = new();
    public ObservableCollection<VoiceProfileSummary> RememberedVoices { get; } = new();
    public ObservableCollection<TranscriptionBatchCollectionOption> BatchCollections { get; } = new();
    public ObservableCollection<TranscriptionBatchYearOption> BatchYears { get; } = new();
    public ObservableCollection<TranscriptionBatchCandidate> BatchPreview { get; } = new();
    public ObservableCollection<TranscriptionBatchRecord> Batches { get; } = new();
    public ObservableCollection<TranscriptionBatchItemRecord> BatchItems { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand TranscribeCurrentCommand { get; }
    public ICommand TranscribeSampleCommand { get; }
    public ICommand PlaySelectedSegmentCommand { get; }
    public ICommand SavePhraseCommand { get; }
    public ICommand TogglePhraseReviewCommand { get; }
    public ICommand SplitPhraseCommand { get; }
    public ICommand MergeNextPhraseCommand { get; }
    public ICommand ConfirmSpeakerCommand { get; }
    public ICommand ClearSpeakerCommand { get; }
    public ICommand PreviousMatchCommand { get; }
    public ICommand NextMatchCommand { get; }
    public ICommand ExportTextCommand { get; }
    public ICommand ExportSrtCommand { get; }
    public ICommand ExportVttCommand { get; }
    public ICommand RetrySelectedJobCommand { get; }
    public ICommand PauseSelectedJobCommand { get; }
    public ICommand ResumeSelectedJobCommand { get; }
    public ICommand CancelSelectedJobCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenResearchCommand { get; }
    public ICommand ToggleBatchBuilderCommand { get; }
    public ICommand PreviewBatchCommand { get; }
    public ICommand StartBatchCommand { get; }
    public ICommand PauseBatchCommand { get; }
    public ICommand ResumeBatchCommand { get; }
    public ICommand CancelBatchCommand { get; }
    public ICommand RetryFailedBatchCommand { get; }
    public ICommand MoveBatchItemUpCommand { get; }
    public ICommand MoveBatchItemDownCommand { get; }

    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommandState(); } }
    public bool IsTranscriptionActive { get => _isTranscriptionActive; private set { if (SetProperty(ref _isTranscriptionActive, value)) RaiseCommandState(); } }
    public bool IsSpeakerAnalysisBusy { get => _isSpeakerAnalysisBusy; private set { if (SetProperty(ref _isSpeakerAnalysisBusy, value)) RaiseCommandState(); } }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string ActivityText { get => _activityText; private set => SetProperty(ref _activityText, value); }
    public bool HasTranscripts => Transcripts.Count > 0;
    public bool HasJobs => Jobs.Count > 0;
    public bool HasSelectedTranscript => SelectedTranscript is not null;
    public bool HasSegments => SelectedTranscript?.Segments.Count > 0;
    public bool HasSelectedSegment => SelectedSegment is not null;
    public bool HasSpeakerCandidates => SpeakerCandidates.Count > 0;
    public bool HasRememberedVoices => RememberedVoices.Count > 0;
    public bool EngineReady => _transcriptionSnapshot.Status.IsAvailable;
    public string EngineStatus => _transcriptionSnapshot.Status.AvailabilityMessage;
    public bool HasCurrentBroadcast => _playback.HasCurrentBroadcast;
    public string CurrentBroadcastText => HasCurrentBroadcast
        ? $"{_playback.Title} · {_playback.Subtitle}"
        : "Play or select a broadcast before starting transcription.";
    public string CurrentPositionText => _playback.PositionText;
    public bool CurrentHasTranscript
    {
        get => _currentHasTranscript;
        private set
        {
            if (!SetProperty(ref _currentHasTranscript, value)) return;
            RaisePropertyChanged(nameof(CurrentTranscriptionActionText));
            RaiseCommandState();
        }
    }
    public string CurrentTranscriptionActionText => CurrentHasTranscript ? "Re-transcribe full broadcast" : "Transcribe full broadcast";
    public bool CanStartTranscription => !IsBusy && !IsTranscriptionActive && EngineReady && HasCurrentBroadcast;
    public bool CanStartSample => CanStartTranscription && !CurrentHasTranscript && (_playback.DurationMs <= 0 || _playback.PositionMs + 1_000 < _playback.DurationMs);
    public bool CanCancelSelectedJob => SelectedJob?.CanCancel == true;
    public bool CanSavePhrase => SelectedSegment is not null && !IsBusy && !string.IsNullOrWhiteSpace(PhraseEditorText);
    public bool CanSplitPhrase => SelectedSegment is { Text.Length: > 1 } segment && !IsBusy && segment.EndMs - segment.StartMs >= 2;
    public bool CanMergePhrase => !IsBusy && GetFollowingSegment() is not null && SelectedSegment is { } selected && SameSpeaker(selected, GetFollowingSegment()!);
    public bool CanConfirmSpeaker => SelectedSegment is { SpeakerKey.Length: > 0 } && !IsBusy && !IsSpeakerAnalysisBusy && !string.IsNullOrWhiteSpace(SpeakerEditorName);
    public bool CanClearSpeaker => SelectedSegment is { SpeakerKey.Length: > 0, AssignmentState: not SpeakerAssignmentState.Unassigned } && !IsBusy;
    public bool IsBatchBuilderOpen { get => _isBatchBuilderOpen; private set => SetProperty(ref _isBatchBuilderOpen, value); }
    public bool IsBatchPreviewBusy { get => _isBatchPreviewBusy; private set { if (SetProperty(ref _isBatchPreviewBusy, value)) RaiseCommandState(); } }
    public bool HasBatchPreview => BatchPreview.Count > 0;
    public bool HasBatches => Batches.Count > 0;
    public bool HasBatchItems => BatchItems.Count > 0;
    public int BatchReadyCount => BatchPreview.Count(x => !x.HasTranscript);
    public int BatchSkippedCount => BatchPreview.Count(x => x.HasTranscript);
    public bool CanStartBatch => !IsBusy && !IsBatchPreviewBusy && EngineReady && BatchReadyCount > 0;
    public string BatchPreviewText { get => _batchPreviewText; private set => SetProperty(ref _batchPreviewText, value); }
    public string BatchBuilderActionText => IsBatchBuilderOpen ? "Close batch builder" : "New batch";

    public TranscriptionBatchCollectionOption? SelectedBatchCollection
    {
        get => _selectedBatchCollection;
        set { if (SetProperty(ref _selectedBatchCollection, value)) InvalidateBatchPreview(); }
    }

    public TranscriptionBatchYearOption? SelectedBatchYear
    {
        get => _selectedBatchYear;
        set { if (SetProperty(ref _selectedBatchYear, value)) InvalidateBatchPreview(); }
    }

    public DateTimeOffset? BatchFromDate
    {
        get => _batchFromDate;
        set { if (SetProperty(ref _batchFromDate, value)) InvalidateBatchPreview(); }
    }

    public DateTimeOffset? BatchToDate
    {
        get => _batchToDate;
        set { if (SetProperty(ref _batchToDate, value)) InvalidateBatchPreview(); }
    }

    public TranscriptionBatchRecord? SelectedBatch
    {
        get => _selectedBatch;
        set
        {
            if (!SetProperty(ref _selectedBatch, value)) return;
            _ = LoadSelectedBatchItemsAsync(value?.BatchId);
            RaiseBatchCommandState();
        }
    }

    public TranscriptionBatchItemRecord? SelectedBatchItem
    {
        get => _selectedBatchItem;
        set
        {
            if (!SetProperty(ref _selectedBatchItem, value)) return;
            RaiseBatchCommandState();
        }
    }
    public bool SelectedPhraseIsReviewed => SelectedSegment?.IsReviewed == true;
    public string ReviewActionText => SelectedPhraseIsReviewed ? "Mark as needs attention" : "Mark reviewed";
    public string SelectedPhraseStatus => SelectedSegment is null
        ? "Select a phrase to review it."
        : $"{SelectedSegment.TimeDisplay} · {SelectedSegment.QualityDisplay}";

    public TranscriptSummary? SelectedTranscriptSummary
    {
        get => _selectedTranscriptSummary;
        set
        {
            if (!SetProperty(ref _selectedTranscriptSummary, value)) return;
            var version = ++_selectionVersion;
            _ = LoadSelectedTranscriptAsync(value, version);
        }
    }

    public TranscriptDocument? SelectedTranscript
    {
        get => _selectedTranscript;
        private set
        {
            if (!SetProperty(ref _selectedTranscript, value)) return;
            SelectedSegment = value?.Segments.FirstOrDefault();
            RebuildSearchMatches(selectFirst: false);
            RaisePropertyChanged(nameof(HasSelectedTranscript));
            RaisePropertyChanged(nameof(HasSegments));
            RaiseCommandState();
        }
    }

    public TranscriptSegment? SelectedSegment
    {
        get => _selectedSegment;
        set
        {
            if (!SetProperty(ref _selectedSegment, value)) return;
            SyncEditorFromSelection();
            UpdateActiveSearchPosition();
            RaiseSelectionProperties();
        }
    }

    public string PhraseEditorText
    {
        get => _phraseEditorText;
        set { if (SetProperty(ref _phraseEditorText, value)) RaiseCommandState(); }
    }

    public string SpeakerEditorName
    {
        get => _speakerEditorName;
        set
        {
            if (!SetProperty(ref _speakerEditorName, value)) return;
            if (!_syncingEditor) SelectedSpeakerCandidate = null;
            RaiseCommandState();
        }
    }

    public TranscriptPersonCandidate? SelectedSpeakerCandidate
    {
        get => _selectedSpeakerCandidate;
        set
        {
            if (!SetProperty(ref _selectedSpeakerCandidate, value) || value is null) return;
            _syncingEditor = true;
            try { SpeakerEditorName = value.Name; }
            finally { _syncingEditor = false; }
        }
    }

    public string TranscriptSearchText
    {
        get => _transcriptSearchText;
        set
        {
            if (!SetProperty(ref _transcriptSearchText, value)) return;
            RebuildSearchMatches(selectFirst: true);
        }
    }

    public bool HasSearchMatches => _searchMatches.Count > 0;
    public string SearchMatchText => string.IsNullOrWhiteSpace(TranscriptSearchText)
        ? "Search this transcript"
        : _searchMatches.Count == 0 ? "No matches" : $"{Math.Max(1, _activeSearchMatch + 1)} of {_searchMatches.Count}";

    public TranscriptionJobRecord? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (!SetProperty(ref _selectedJob, value)) return;
            RaisePropertyChanged(nameof(CanCancelSelectedJob));
            RaiseCommandState();
        }
    }

    public void SetOpenSettingsHandler(Func<Task> handler)
        => _openSettings = handler ?? throw new ArgumentNullException(nameof(handler));

    public void SetOpenResearchHandler(Func<Task> handler)
        => _openResearch = handler ?? throw new ArgumentNullException(nameof(handler));

    public async Task FocusEpisodeAsync(long episodeId)
    {
        if (episodeId <= 0) return;
        await LoadAsync(force: true).ConfigureAwait(true);
        var match = Transcripts.FirstOrDefault(x => x.EpisodeId == episodeId);
        if (match is null)
        {
            StatusText = "That broadcast does not have a transcript yet.";
            return;
        }
        SelectedTranscriptSummary = match;
        StatusText = $"Opened transcript for {match.EpisodeTitle}.";
    }

    public async Task LoadAsync(bool force = false)
    {
        await _loadGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_disposed) return;
            if (_isLoaded && !force && ConnectedViewRefreshPolicy.IsFresh(_loadedAt)) return;
            IsBusy = true;
            StatusText = "Loading transcripts and jobs…";
            _transcriptionSnapshot = await _transcriptionAdministration.GetAsync().ConfigureAwait(true);
            var selectedEpisodeId = SelectedTranscriptSummary?.EpisodeId;
            var selectedJobId = SelectedJob?.JobId;
            var summaries = await _repository.GetSummariesAsync().ConfigureAwait(true);
            var jobs = await _coordinator.GetJobsAsync().ConfigureAwait(true);

            Replace(Transcripts, summaries);
            Replace(Jobs, jobs);
            SelectedTranscriptSummary = Transcripts.FirstOrDefault(x => x.EpisodeId == selectedEpisodeId) ?? Transcripts.FirstOrDefault();
            SelectedJob = Jobs.FirstOrDefault(x => x.JobId == selectedJobId) ?? Jobs.FirstOrDefault();
            IsTranscriptionActive = jobs.Any(x => x.State is TranscriptionJobState.Queued or TranscriptionJobState.Running);
            ActivityText = jobs.FirstOrDefault(x => x.State is TranscriptionJobState.Running or TranscriptionJobState.Queued)?.Message ?? string.Empty;
            await RefreshCurrentTranscriptStateAsync().ConfigureAwait(true);
            if (BatchCollections.Count == 0) await LoadBatchFiltersAsync().ConfigureAwait(true);
            await LoadBatchesAsync().ConfigureAwait(true);
            RaiseCollectionProperties();
            StatusText = HasTranscripts
                ? $"{Transcripts.Count:N0} transcript{(Transcripts.Count == 1 ? string.Empty : "s")} · {Jobs.Count:N0} recent job{(Jobs.Count == 1 ? string.Empty : "s")}"
                : EngineReady ? "No transcripts yet. Start with the current broadcast." : EngineStatus;
            _isLoaded = true;
            _loadedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            IsBusy = false;
            _loadGate.Release();
        }
    }

    private void ToggleBatchBuilder()
    {
        IsBatchBuilderOpen = !IsBatchBuilderOpen;
        RaisePropertyChanged(nameof(BatchBuilderActionText));
    }

    private async Task LoadBatchFiltersAsync()
    {
        var overview = await _library.GetOverviewAsync().ConfigureAwait(true);
        var facets = await _library.GetSearchFacetsAsync().ConfigureAwait(true);
        var previousCollection = SelectedBatchCollection?.CollectionId;
        var previousYear = SelectedBatchYear?.Year;
        Replace(BatchCollections, new[] { new TranscriptionBatchCollectionOption(null, "All shows", overview.TotalBroadcasts) }
            .Concat(overview.Collections.Select(x => new TranscriptionBatchCollectionOption(x.CollectionId, x.CollectionName, x.BroadcastCount))));
        Replace(BatchYears, new[] { new TranscriptionBatchYearOption(null) }
            .Concat(facets.Years.Select(x => new TranscriptionBatchYearOption(x))));
        _selectedBatchCollection = BatchCollections.FirstOrDefault(x => x.CollectionId == previousCollection) ?? BatchCollections.FirstOrDefault();
        _selectedBatchYear = BatchYears.FirstOrDefault(x => x.Year == previousYear) ?? BatchYears.FirstOrDefault();
        RaisePropertyChanged(nameof(SelectedBatchCollection));
        RaisePropertyChanged(nameof(SelectedBatchYear));
    }

    private async Task PreviewBatchAsync()
    {
        if (BatchFromDate.HasValue && BatchToDate.HasValue && BatchFromDate.Value.Date > BatchToDate.Value.Date)
            throw new InvalidOperationException("The batch start date must not be after its end date.");
        IsBatchPreviewBusy = true;
        BatchPreviewText = "Finding broadcasts and checking existing transcripts…";
        try
        {
            var result = await _library.BrowseAsync(new LibraryBrowseRequest(
                CollectionId: SelectedBatchCollection?.CollectionId,
                Year: SelectedBatchYear?.Year,
                Limit: 10_000,
                NewestFirst: false)).ConfigureAwait(true);
            IEnumerable<LibraryBroadcastSummary> broadcasts = result.Broadcasts;
            if (BatchFromDate.HasValue)
            {
                var from = DateOnly.FromDateTime(BatchFromDate.Value.Date);
                broadcasts = broadcasts.Where(x => x.AirDate.HasValue && x.AirDate.Value >= from);
            }
            if (BatchToDate.HasValue)
            {
                var to = DateOnly.FromDateTime(BatchToDate.Value.Date);
                broadcasts = broadcasts.Where(x => x.AirDate.HasValue && x.AirDate.Value <= to);
            }
            var transcriptEpisodes = (await _repository.GetSummariesAsync().ConfigureAwait(true))
                .Select(x => x.EpisodeId)
                .ToHashSet();
            var candidates = broadcasts.Select(x => new TranscriptionBatchCandidate(
                    x.RepresentativeEpisodeId,
                    x.CollectionName,
                    x.AirDate,
                    string.IsNullOrWhiteSpace(x.Title) ? x.BroadcastId : x.Title!,
                    x.DurationMs,
                    transcriptEpisodes.Contains(x.RepresentativeEpisodeId)))
                .ToArray();
            Replace(BatchPreview, candidates);
            RaiseBatchPreviewState();
            BatchPreviewText = candidates.Length == 0
                ? "No broadcasts match this selection."
                : $"{BatchReadyCount:N0} ready to transcribe · {BatchSkippedCount:N0} already completed and will be skipped";
        }
        finally { IsBatchPreviewBusy = false; }
    }

    private async Task StartBatchAsync()
    {
        if (!CanStartBatch) return;
        var settings = _transcriptionSnapshot.Settings;
        if (settings.EnableMultiSpeakerDiarization && !_transcriptionSnapshot.Status.DiarizationAvailable)
            throw new InvalidOperationException(_transcriptionSnapshot.Status.DiarizationMessage);
        var selectionLabel = BuildBatchSelectionLabel();
        var request = new TranscriptionBatchCreateRequest(
            selectionLabel,
            selectionLabel,
            new TranscriptionJobOptions(
                settings.DefaultLanguage,
                settings.ModelId,
                EnableSpeakerDiarization: settings.EnableMultiSpeakerDiarization,
                UseVoiceActivityDetection: settings.UseVoiceActivityDetection),
            BatchPreview.ToArray());
        IsBusy = true;
        try
        {
            var batch = await _batchCoordinator.CreateAndStartAsync(request).ConfigureAwait(true);
            await LoadBatchesAsync(batch.BatchId).ConfigureAwait(true);
            IsBatchBuilderOpen = false;
            RaisePropertyChanged(nameof(BatchBuilderActionText));
            StatusText = $"Batch started · {batch.PendingCount:N0} broadcasts queued locally.";
        }
        finally { IsBusy = false; }
    }

    private async Task LoadBatchesAsync(Guid? selectBatchId = null)
    {
        var selectedId = selectBatchId ?? SelectedBatch?.BatchId;
        var batches = await _batchCoordinator.GetBatchesAsync().ConfigureAwait(true);
        Replace(Batches, batches);
        SelectedBatch = selectedId.HasValue ? Batches.FirstOrDefault(x => x.BatchId == selectedId.Value) : Batches.FirstOrDefault();
        RaisePropertyChanged(nameof(HasBatches));
        RaiseBatchCommandState();
    }

    private async Task LoadSelectedBatchItemsAsync(Guid? batchId)
    {
        try
        {
            var selectedItemId = SelectedBatchItem?.Id;
            var items = batchId.HasValue
                ? await _batchCoordinator.GetItemsAsync(batchId.Value).ConfigureAwait(true)
                : Array.Empty<TranscriptionBatchItemRecord>();
            if (SelectedBatch?.BatchId != batchId) return;
            Replace(BatchItems, items);
            SelectedBatchItem = BatchItems.FirstOrDefault(x => x.Id == selectedItemId) ?? BatchItems.FirstOrDefault();
            RaisePropertyChanged(nameof(HasBatchItems));
        }
        catch (Exception exception) { SetError(exception); }
    }

    private async Task PauseSelectedBatchAsync()
    {
        if (SelectedBatch is null) return;
        var id = SelectedBatch.BatchId;
        await _batchCoordinator.PauseAsync(id).ConfigureAwait(true);
        await LoadBatchesAsync(id).ConfigureAwait(true);
        StatusText = "Batch paused. The active local worker has been paused where supported.";
    }

    private async Task ResumeSelectedBatchAsync()
    {
        if (SelectedBatch is null) return;
        var id = SelectedBatch.BatchId;
        await _batchCoordinator.ResumeAsync(id).ConfigureAwait(true);
        await LoadBatchesAsync(id).ConfigureAwait(true);
        StatusText = "Batch resumed from its next unfinished broadcast.";
    }

    private async Task CancelSelectedBatchAsync()
    {
        if (SelectedBatch is null) return;
        var id = SelectedBatch.BatchId;
        await _batchCoordinator.CancelAsync(id).ConfigureAwait(true);
        await LoadBatchesAsync(id).ConfigureAwait(true);
        StatusText = "Batch cancellation requested. Completed transcripts are preserved.";
    }

    private async Task RetryFailedBatchAsync()
    {
        if (SelectedBatch is null) return;
        var id = SelectedBatch.BatchId;
        await _batchCoordinator.RetryFailedAsync(id).ConfigureAwait(true);
        await LoadBatchesAsync(id).ConfigureAwait(true);
        StatusText = "Only failed broadcasts were returned to the batch queue.";
    }

    private async Task MoveSelectedBatchItemAsync(int direction)
    {
        if (SelectedBatch is null || SelectedBatchItem is null) return;
        var batchId = SelectedBatch.BatchId;
        var itemId = SelectedBatchItem.Id;
        if (await _batchCoordinator.MoveItemAsync(batchId, itemId, direction).ConfigureAwait(true))
        {
            await LoadSelectedBatchItemsAsync(batchId).ConfigureAwait(true);
            SelectedBatchItem = BatchItems.FirstOrDefault(x => x.Id == itemId);
            StatusText = direction < 0 ? "Broadcast moved higher in the batch." : "Broadcast moved lower in the batch.";
        }
    }

    private void InvalidateBatchPreview()
    {
        BatchPreview.Clear();
        BatchPreviewText = "Selection changed. Preview the batch to check what will run.";
        RaiseBatchPreviewState();
    }

    private string BuildBatchSelectionLabel()
    {
        var parts = new List<string> { SelectedBatchCollection?.Name ?? "All shows" };
        if (SelectedBatchYear?.Year is int year) parts.Add(year.ToString());
        if (BatchFromDate.HasValue || BatchToDate.HasValue)
        {
            var from = BatchFromDate?.ToString("dd MMM yyyy") ?? "start";
            var to = BatchToDate?.ToString("dd MMM yyyy") ?? "present";
            parts.Add($"{from} to {to}");
        }
        return string.Join(" · ", parts);
    }

    private void RaiseBatchPreviewState()
    {
        RaisePropertyChanged(nameof(HasBatchPreview));
        RaisePropertyChanged(nameof(BatchReadyCount));
        RaisePropertyChanged(nameof(BatchSkippedCount));
        RaisePropertyChanged(nameof(CanStartBatch));
        RaiseCommandState();
    }

    private async Task QueueCurrentAsync(bool sample)
    {
        if (!HasCurrentBroadcast) return;
        var engineSettings = _transcriptionSnapshot.Settings;
        long? duration = null;
        if (sample)
        {
            const long fiveMinutes = 5 * 60 * 1000;
            var remaining = _playback.DurationMs > 0 ? Math.Max(0, _playback.DurationMs - _playback.PositionMs) : fiveMinutes;
            duration = Math.Min(fiveMinutes, remaining);
            if (duration <= 0) throw new InvalidOperationException("The current playback position is at the end of the broadcast.");
        }
        var options = new TranscriptionJobOptions(
            engineSettings.DefaultLanguage,
            engineSettings.ModelId,
            sample ? _playback.PositionMs : 0,
            duration,
            engineSettings.EnableMultiSpeakerDiarization,
            engineSettings.UseVoiceActivityDetection,
            ReplaceExistingTranscript: !sample && CurrentHasTranscript);
        await _coordinator.QueueAsync(_playback.CurrentBroadcastId, options).ConfigureAwait(true);
        StatusText = sample
            ? "Five-minute local transcription sample queued."
            : CurrentHasTranscript
                ? "Replacement transcription queued. The existing transcript stays available until the replacement succeeds."
                : "Full local transcription queued.";
        await LoadAsync(force: true).ConfigureAwait(true);
    }

    private async Task SaveSelectedPhraseAsync()
    {
        if (SelectedTranscript is null || SelectedSegment is null) return;
        IsBusy = true;
        try
        {
            var selectedIndex = SelectedSegment.Index;
            var changed = _review.UpdatePhrase(SelectedTranscript, selectedIndex, PhraseEditorText);
            await SaveReviewDocumentAsync(changed, selectedIndex).ConfigureAwait(true);
            StatusText = "Phrase correction saved and marked reviewed.";
        }
        finally { IsBusy = false; }
    }

    private async Task ToggleSelectedPhraseReviewAsync()
    {
        if (SelectedTranscript is null || SelectedSegment is null) return;
        IsBusy = true;
        try
        {
            var selectedIndex = SelectedSegment.Index;
            var next = !SelectedSegment.IsReviewed;
            var changed = _review.SetReviewed(SelectedTranscript, selectedIndex, next);
            await SaveReviewDocumentAsync(changed, selectedIndex).ConfigureAwait(true);
            StatusText = next ? "Phrase marked reviewed." : "Phrase marked as needing attention.";
        }
        finally { IsBusy = false; }
    }

    private async Task SplitSelectedPhraseAsync()
    {
        if (SelectedTranscript is null || SelectedSegment is null) return;
        IsBusy = true;
        try
        {
            var selectedIndex = SelectedSegment.Index;
            var changed = _review.SplitPhrase(SelectedTranscript, selectedIndex);
            await SaveReviewDocumentAsync(changed, selectedIndex).ConfigureAwait(true);
            StatusText = "Phrase split at its nearest middle word. You can fine-tune both halves now.";
        }
        finally { IsBusy = false; }
    }

    private async Task MergeSelectedPhraseAsync()
    {
        if (SelectedTranscript is null || SelectedSegment is null) return;
        IsBusy = true;
        try
        {
            var selectedIndex = SelectedSegment.Index;
            var changed = _review.MergeWithNext(SelectedTranscript, selectedIndex);
            await SaveReviewDocumentAsync(changed, selectedIndex).ConfigureAwait(true);
            StatusText = "The consecutive phrases were merged.";
        }
        finally { IsBusy = false; }
    }

    private async Task ConfirmSelectedSpeakerAsync()
    {
        var summary = SelectedTranscriptSummary;
        var segment = SelectedSegment;
        if (summary is null || segment is null || string.IsNullOrWhiteSpace(segment.SpeakerKey)) return;
        IsBusy = true;
        try
        {
            var result = await _speakers.AssignClusterAsync(summary.EpisodeId, segment.SpeakerKey, SpeakerEditorName, trainVoice: true).ConfigureAwait(true);
            var learned = 0;
            if (result.PendingSamplesCreated > 0)
                learned = await _voiceLearning.ProcessPendingAsync().ConfigureAwait(true);
            await ReloadSelectedTranscriptAsync(segment.Index).ConfigureAwait(true);
            await LoadSpeakerChoicesAsync(summary.EpisodeId).ConfigureAwait(true);
            StatusText = learned > 0
                ? $"Confirmed {result.PersonName} and the server learned {learned:N0} voice sample{(learned == 1 ? string.Empty : "s")}."
                : $"Confirmed {result.PersonName}. Voice evidence is retained by the server.";
        }
        finally { IsBusy = false; }
    }

    private async Task ClearSelectedSpeakerAsync()
    {
        var summary = SelectedTranscriptSummary;
        var segment = SelectedSegment;
        if (summary is null || segment is null || string.IsNullOrWhiteSpace(segment.SpeakerKey)) return;
        IsBusy = true;
        try
        {
            await _speakers.ClearAssignmentAsync(summary.EpisodeId, segment.SpeakerKey).ConfigureAwait(true);
            await ReloadSelectedTranscriptAsync(segment.Index).ConfigureAwait(true);
            StatusText = "Speaker identity cleared. The anonymous speaker label is restored.";
        }
        finally { IsBusy = false; }
    }

    private async Task ExportTranscriptAsync(string format)
    {
        var document = SelectedTranscript;
        var summary = SelectedTranscriptSummary;
        if (document is null || summary is null) return;
        var extension = format.ToLowerInvariant();
        var content = extension switch
        {
            "srt" => _review.ExportSrt(document),
            "vtt" => _review.ExportVtt(document),
            _ => _review.ExportPlainText(document, summary)
        };
        var filterName = extension switch { "srt" => "SubRip subtitles", "vtt" => "WebVTT captions", _ => "Text transcript" };
        var path = await _files.PickSaveFileAsync(new FileSelectionRequest(
            Title: $"Export {filterName.ToLowerInvariant()}",
            Filter: $"{filterName}|*.{extension}",
            DefaultExtension: $".{extension}",
            SuggestedFileName: $"{SafeFileName(summary.Show)}-{SafeFileName(summary.EpisodeTitle)}.{extension}",
            CheckFileExists: false)).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText = "Transcript export cancelled.";
            return;
        }
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false)).ConfigureAwait(true);
        StatusText = $"Exported {filterName.ToLowerInvariant()} to {Path.GetFileName(path)}.";
    }

    private async Task SaveReviewDocumentAsync(TranscriptDocument changed, int selectedIndex)
    {
        var saved = await _repository.SaveAsync(changed).ConfigureAwait(true);
        SelectedTranscript = saved;
        SelectedSegment = saved.Segments.FirstOrDefault(x => x.Index == selectedIndex) ?? saved.Segments.FirstOrDefault();
        await RefreshSelectedSummaryAsync(saved.EpisodeId).ConfigureAwait(true);
    }

    private async Task RefreshSelectedSummaryAsync(long episodeId)
    {
        var refreshed = await _repository.GetSummaryForEpisodeAsync(episodeId).ConfigureAwait(true);
        if (refreshed is null) return;
        var existing = Transcripts.FirstOrDefault(x => x.EpisodeId == episodeId);
        if (existing is not null)
        {
            var index = Transcripts.IndexOf(existing);
            Transcripts[index] = refreshed;
        }
        _selectedTranscriptSummary = refreshed;
        RaisePropertyChanged(nameof(SelectedTranscriptSummary));
    }

    private async Task RetrySelectedJobAsync()
    {
        var selected = SelectedJob;
        if (selected is null) return;
        await _coordinator.RetryAsync(selected.JobId).ConfigureAwait(true);
        StatusText = "Transcription retry queued.";
        await LoadAsync(force: true).ConfigureAwait(true);
    }

    private void CancelSelectedJob()
    {
        var selected = SelectedJob;
        if (selected is null) return;
        StatusText = _coordinator.Cancel(selected.JobId)
            ? "Cancellation requested."
            : "That transcription job is no longer running.";
    }

    private async Task PauseSelectedJobAsync()
    {
        var selected = SelectedJob;
        if (selected is null) return;
        if (SelectedBatch is not null && BatchItems.Any(x => x.TranscriptionJobId == selected.JobId))
        {
            await _batchCoordinator.PauseAsync(SelectedBatch.BatchId).ConfigureAwait(true);
            await LoadBatchesAsync(SelectedBatch.BatchId).ConfigureAwait(true);
            StatusText = "The entire batch was paused with its active transcription.";
            await LoadAsync(force: true).ConfigureAwait(true);
            return;
        }
        StatusText = await _coordinator.PauseAsync(selected.JobId).ConfigureAwait(true)
            ? "Transcription paused. Completed progress has been preserved."
            : "That transcription could not be paused.";
        await LoadAsync(force: true).ConfigureAwait(true);
    }

    private async Task ResumeSelectedJobAsync()
    {
        var selected = SelectedJob;
        if (selected is null) return;
        if (SelectedBatch is not null && BatchItems.Any(x => x.TranscriptionJobId == selected.JobId))
        {
            await _batchCoordinator.ResumeAsync(SelectedBatch.BatchId).ConfigureAwait(true);
            await LoadBatchesAsync(SelectedBatch.BatchId).ConfigureAwait(true);
            StatusText = "The batch was resumed with its active transcription.";
            await LoadAsync(force: true).ConfigureAwait(true);
            return;
        }
        StatusText = await _coordinator.ResumeAsync(selected.JobId).ConfigureAwait(true)
            ? "Transcription resumed from where it paused."
            : "That transcription could not be resumed.";
        await LoadAsync(force: true).ConfigureAwait(true);
    }

    private async Task PlaySelectedSegmentAsync()
    {
        var summary = SelectedTranscriptSummary;
        var segment = SelectedSegment;
        if (summary is null || segment is null) return;
        await _playback.LoadAndPlayAtAsync(summary.EpisodeId, segment.StartMs).ConfigureAwait(true);
        StatusText = $"Playing from {segment.TimeDisplay}.";
    }

    private async Task LoadSelectedTranscriptAsync(TranscriptSummary? summary, int version)
    {
        try
        {
            var document = summary is null ? null : await _repository.GetForEpisodeAsync(summary.EpisodeId).ConfigureAwait(true);
            if (version != _selectionVersion || _disposed) return;
            SelectedTranscript = document;
            SpeakerCandidates.Clear();
            RememberedVoices.Clear();
            RaiseSpeakerCollections();
            if (summary is null || document is null) return;
            await LoadSpeakerChoicesAsync(summary.EpisodeId).ConfigureAwait(true);
            if (version != _selectionVersion || _disposed) return;
            _ = SuggestRememberedVoicesAsync(document, version);
        }
        catch (Exception exception) { SetError(exception); }
    }

    private async Task LoadSpeakerChoicesAsync(long episodeId)
    {
        var candidates = await _speakers.GetEpisodePeopleAsync(episodeId).ConfigureAwait(true);
        var profiles = await _speakers.GetVoiceProfilesAsync().ConfigureAwait(true);
        Replace(SpeakerCandidates, candidates);
        Replace(RememberedVoices, profiles);
        RaiseSpeakerCollections();
    }

    private async Task SuggestRememberedVoicesAsync(TranscriptDocument document, int version)
    {
        var key = (document.EpisodeId, document.Revision);
        if (_disposed || !_voiceLearning.Engine.IsAvailable || !RememberedVoices.Any(x => x.ReadySampleCount >= 2)
            || !_voiceMatchesAttempted.Add(key)) return;
        var clusters = document.Speakers.Where(x => x.AssignmentState == SpeakerAssignmentState.Unassigned).ToArray();
        if (clusters.Length == 0) return;

        IsSpeakerAnalysisBusy = true;
        try
        {
            var audioPath = await _repository.GetPreferredMediaPathAsync(document.EpisodeId).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath)) return;
            foreach (var cluster in clusters)
            {
                if (version != _selectionVersion || _disposed) return;
                var sample = document.Segments
                    .Where(x => string.Equals(x.SpeakerKey, cluster.SpeakerKey, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.EndMs - x.StartMs)
                    .FirstOrDefault();
                if (sample is null || sample.EndMs - sample.StartMs < 1_000) continue;
                var embedding = await _voiceLearning.Engine.CreateEmbeddingAsync(
                    new VoiceEmbeddingRequest(document.EpisodeId, audioPath, sample.StartMs, sample.EndMs, cluster.SpeakerKey),
                    CancellationToken.None).ConfigureAwait(true);
                await _speakers.MatchClusterAsync(document.EpisodeId, cluster.SpeakerKey, embedding).ConfigureAwait(true);
            }
            if (version != _selectionVersion || _disposed) return;
            var suggestions = (await _speakers.GetClustersForEpisodeAsync(document.EpisodeId).ConfigureAwait(true))
                .Count(x => x.AssignmentState == SpeakerAssignmentState.Suggested);
            if (suggestions > 0)
            {
                await ReloadSelectedTranscriptAsync(SelectedSegment?.Index ?? 0).ConfigureAwait(true);
                StatusText = "Remembered voice suggestions are ready. Confirm any name before it becomes permanent.";
            }
        }
        catch (Exception exception)
        {
            StatusText = $"Transcript loaded. Remembered voice matching was skipped: {exception.Message}";
        }
        finally { IsSpeakerAnalysisBusy = false; }
    }

    private async Task ReloadSelectedTranscriptAsync(int selectedIndex)
    {
        var episodeId = SelectedTranscriptSummary?.EpisodeId;
        if (episodeId is not > 0) return;
        var document = await _repository.GetForEpisodeAsync(episodeId.Value).ConfigureAwait(true);
        SelectedTranscript = document;
        SelectedSegment = document?.Segments.FirstOrDefault(x => x.Index == selectedIndex) ?? document?.Segments.FirstOrDefault();
        await RefreshSelectedSummaryAsync(episodeId.Value).ConfigureAwait(true);
    }

    private void RebuildSearchMatches(bool selectFirst)
    {
        _searchMatches.Clear();
        _activeSearchMatch = -1;
        var query = TranscriptSearchText.Trim();
        if (query.Length > 0 && SelectedTranscript is not null)
        {
            _searchMatches.AddRange(SelectedTranscript.Segments
                .Where(x => x.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || x.DisplaySpeaker.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Index));
            if (_searchMatches.Count > 0)
            {
                _activeSearchMatch = 0;
                if (selectFirst)
                    SelectedSegment = SelectedTranscript.Segments.FirstOrDefault(x => x.Index == _searchMatches[0]);
            }
        }
        RaiseSearchProperties();
    }

    private void MoveSearchMatch(int direction)
    {
        if (_searchMatches.Count == 0 || SelectedTranscript is null) return;
        _activeSearchMatch = (_activeSearchMatch + direction + _searchMatches.Count) % _searchMatches.Count;
        var segmentIndex = _searchMatches[_activeSearchMatch];
        SelectedSegment = SelectedTranscript.Segments.FirstOrDefault(x => x.Index == segmentIndex);
        RaisePropertyChanged(nameof(SearchMatchText));
    }

    private void UpdateActiveSearchPosition()
    {
        if (SelectedSegment is null || _searchMatches.Count == 0) return;
        var position = _searchMatches.IndexOf(SelectedSegment.Index);
        if (position >= 0) _activeSearchMatch = position;
        RaisePropertyChanged(nameof(SearchMatchText));
    }

    private void SyncEditorFromSelection()
    {
        _syncingEditor = true;
        try
        {
            PhraseEditorText = SelectedSegment?.Text ?? string.Empty;
            SpeakerEditorName = SelectedSegment?.AssignedPersonName ?? string.Empty;
            SelectedSpeakerCandidate = SpeakerCandidates.FirstOrDefault(x =>
                string.Equals(x.Name, SpeakerEditorName, StringComparison.OrdinalIgnoreCase));
        }
        finally { _syncingEditor = false; }
    }

    private TranscriptSegment? GetFollowingSegment()
    {
        if (SelectedTranscript is null || SelectedSegment is null) return null;
        var position = SelectedTranscript.Segments.ToList().FindIndex(x => x.Index == SelectedSegment.Index);
        return position >= 0 && position + 1 < SelectedTranscript.Segments.Count
            ? SelectedTranscript.Segments[position + 1]
            : null;
    }

    private static bool SameSpeaker(TranscriptSegment first, TranscriptSegment second)
    {
        if (!string.IsNullOrWhiteSpace(first.SpeakerKey) || !string.IsNullOrWhiteSpace(second.SpeakerKey))
            return string.Equals(first.SpeakerKey, second.SpeakerKey, StringComparison.OrdinalIgnoreCase);
        return string.Equals(first.DisplaySpeaker, second.DisplaySpeaker, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshCurrentTranscriptStateAsync()
    {
        var episodeId = _playback.CurrentBroadcastId;
        CurrentHasTranscript = episodeId > 0 && await _repository.GetSummaryForEpisodeAsync(episodeId).ConfigureAwait(true) is not null;
        RaiseCurrentProperties();
    }

    private void BackgroundJobsOnProgressChanged(object? sender, BackgroundJobProgress progress)
    {
        if (progress.Category != BackgroundJobCategory.Transcription || _disposed) return;
        _ = _dispatcher.InvokeAsync(() =>
        {
            IsTranscriptionActive = progress.State is BackgroundJobState.Queued or BackgroundJobState.Running;
            ActivityText = progress.Message ?? progress.Name;
            StatusText = progress.Message ?? progress.Name;
            RaisePropertyChanged(nameof(EngineStatus));
            var now = DateTimeOffset.UtcNow;
            if (now - _lastBatchProgressRefresh >= TimeSpan.FromMilliseconds(750))
            {
                _lastBatchProgressRefresh = now;
                _ = LoadBatchesAsync();
            }
            if (progress.State is BackgroundJobState.Completed or BackgroundJobState.Failed or BackgroundJobState.Cancelled)
                _ = LoadAsync(force: true);
        });
    }

    private void PlaybackOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackViewModel.CurrentBroadcastId) or nameof(PlaybackViewModel.HasCurrentBroadcast))
            _ = RefreshCurrentTranscriptStateAsync();
        if (e.PropertyName is nameof(PlaybackViewModel.PositionMs) or nameof(PlaybackViewModel.PositionText) or nameof(PlaybackViewModel.DurationMs))
            RaiseCurrentProperties();
    }

    private void RaiseCurrentProperties()
    {
        RaisePropertyChanged(nameof(HasCurrentBroadcast));
        RaisePropertyChanged(nameof(CurrentBroadcastText));
        RaisePropertyChanged(nameof(CurrentPositionText));
        RaisePropertyChanged(nameof(CanStartTranscription));
        RaisePropertyChanged(nameof(CanStartSample));
        RaiseCommandState();
    }

    private void RaiseCollectionProperties()
    {
        RaisePropertyChanged(nameof(HasTranscripts));
        RaisePropertyChanged(nameof(HasJobs));
        RaisePropertyChanged(nameof(EngineReady));
        RaisePropertyChanged(nameof(EngineStatus));
    }

    private void RaiseSpeakerCollections()
    {
        RaisePropertyChanged(nameof(HasSpeakerCandidates));
        RaisePropertyChanged(nameof(HasRememberedVoices));
    }

    private void RaiseSelectionProperties()
    {
        RaisePropertyChanged(nameof(HasSelectedSegment));
        RaisePropertyChanged(nameof(SelectedPhraseIsReviewed));
        RaisePropertyChanged(nameof(ReviewActionText));
        RaisePropertyChanged(nameof(SelectedPhraseStatus));
        RaisePropertyChanged(nameof(CanMergePhrase));
        RaisePropertyChanged(nameof(CanSplitPhrase));
        RaisePropertyChanged(nameof(CanConfirmSpeaker));
        RaisePropertyChanged(nameof(CanClearSpeaker));
        RaiseCommandState();
    }

    private void RaiseSearchProperties()
    {
        RaisePropertyChanged(nameof(HasSearchMatches));
        RaisePropertyChanged(nameof(SearchMatchText));
        if (PreviousMatchCommand is DelegateCommand previous) previous.RaiseCanExecuteChanged();
        if (NextMatchCommand is DelegateCommand next) next.RaiseCanExecuteChanged();
    }

    private void RaiseBatchCommandState()
    {
        RaisePropertyChanged(nameof(CanStartBatch));
        foreach (var command in new[]
                 {
                     PreviewBatchCommand, StartBatchCommand, PauseBatchCommand, ResumeBatchCommand,
                     CancelBatchCommand, RetryFailedBatchCommand, MoveBatchItemUpCommand, MoveBatchItemDownCommand
                 })
            if (command is AsyncCommand asyncCommand) asyncCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommandState()
    {
        foreach (var command in new[]
                 {
                     RefreshCommand, TranscribeCurrentCommand, TranscribeSampleCommand, PlaySelectedSegmentCommand,
                     SavePhraseCommand, TogglePhraseReviewCommand, SplitPhraseCommand, MergeNextPhraseCommand,
                     ConfirmSpeakerCommand, ClearSpeakerCommand, ExportTextCommand, ExportSrtCommand, ExportVttCommand,
                     RetrySelectedJobCommand, PauseSelectedJobCommand, ResumeSelectedJobCommand,
                     PreviewBatchCommand, StartBatchCommand, PauseBatchCommand, ResumeBatchCommand,
                     CancelBatchCommand, RetryFailedBatchCommand, MoveBatchItemUpCommand, MoveBatchItemDownCommand
                 })
            if (command is AsyncCommand asyncCommand) asyncCommand.RaiseCanExecuteChanged();
        if (CancelSelectedJobCommand is DelegateCommand cancel) cancel.RaiseCanExecuteChanged();
    }

    private void SetError(Exception exception)
    {
        StatusText = $"Transcription action failed: {exception.Message}";
        IsBusy = false;
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((value ?? string.Empty).Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "transcript" : cleaned;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _backgroundJobs.ProgressChanged -= BackgroundJobsOnProgressChanged;
        _playback.PropertyChanged -= PlaybackOnPropertyChanged;
        _loadGate.Dispose();
    }
}
