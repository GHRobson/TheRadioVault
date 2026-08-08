using System.ComponentModel;
using System.Windows.Input;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;
using TheRadioVault.Transcription.Services;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class NowPlayingViewModel : ObservableObject, IDisposable
{
    private readonly IBroadcastDetailsService _detailsService;
    private readonly ITranscriptRepository _transcripts;
    private readonly ITranscriptionCoordinator _transcription;
    private Func<long, Task>? _openFullInfo;
    private Func<long, Task>? _openTranscript;
    private BroadcastDetails? _details;
    private bool _isBusy;
    private string _detailsStatus = "Choose a broadcast to see its full Now Playing information.";
    private int _loadGeneration;
    private bool _hasTranscript;
    private bool _transcriptionPending;
    private TranscriptDocument? _transcript;

    public NowPlayingViewModel(
        PlaybackViewModel playback,
        MomentsViewModel moments,
        QueueViewModel queue,
        IBroadcastDetailsService detailsService,
        ITranscriptRepository transcripts,
        ITranscriptionCoordinator transcription,
        IWikiService? wiki = null)
    {
        Playback = playback ?? throw new ArgumentNullException(nameof(playback));
        Moments = moments ?? throw new ArgumentNullException(nameof(moments));
        Queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _detailsService = detailsService ?? throw new ArgumentNullException(nameof(detailsService));
        _transcripts = transcripts ?? throw new ArgumentNullException(nameof(transcripts));
        _transcription = transcription ?? throw new ArgumentNullException(nameof(transcription));
        RelatedWiki = new RelatedWikiPagesViewModel(wiki);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => Playback.HasCurrentBroadcast && !IsBusy, SetError);
        OpenFullInfoCommand = new AsyncCommand(OpenFullInfoAsync, () => Playback.HasCurrentBroadcast && !IsBusy, SetError);
        OpenTranscriptCommand = new AsyncCommand(OpenTranscriptAsync, () => Playback.HasCurrentBroadcast && HasTranscript && !IsBusy, SetError);
        StartTranscriptionCommand = new AsyncCommand(StartTranscriptionAsync, () => Playback.HasCurrentBroadcast && CanStartTranscription && !IsBusy, SetError);
        PlayTranscriptSegmentCommand = new DelegateCommand(parameter =>
        {
            if (parameter is TranscriptSegment segment) _ = PlayTranscriptSegmentAsync(segment);
        });
        Playback.PropertyChanged += PlaybackOnPropertyChanged;
    }

    public PlaybackViewModel Playback { get; }
    public MomentsViewModel Moments { get; }
    public QueueViewModel Queue { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenFullInfoCommand { get; }
    public ICommand OpenTranscriptCommand { get; }
    public ICommand StartTranscriptionCommand { get; }
    public ICommand PlayTranscriptSegmentCommand { get; }
    public RelatedWikiPagesViewModel RelatedWiki { get; }
    public BroadcastDetails? Details { get => _details; private set { if (SetProperty(ref _details, value)) RaiseDetailProperties(); } }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) { RaiseCommandState(); RaisePropertyChanged(nameof(IsWorking)); } } }
    public bool IsWorking => IsBusy || Playback.IsPrimaryTransportLoading;
    public string ActivityText => Playback.IsPrimaryTransportLoading ? Playback.StatusText : DetailsStatus;
    public string DetailsStatus { get => _detailsStatus; private set { if (SetProperty(ref _detailsStatus, value)) RaisePropertyChanged(nameof(ActivityText)); } }
    public bool HasDetails => Details is not null;
    public bool HasSummary => Details?.HasSummary == true;
    public bool HasPeople => Details?.HasPeople == true;
    public bool HasTopics => Details?.HasTopics == true;
    public bool HasNotes => Details?.HasArchiveNotes == true || Details?.HasPersonalNotes == true;
    public bool HasHosts => Details?.HasHosts == true;
    public bool HasGuests => Details?.HasGuests == true;
    public bool HasCallers => Details?.HasCallers == true;
    public bool HasMentionedPeople => Details?.HasMentionedPeople == true;
    public string SummaryText => Details?.Summary ?? string.Empty;
    public string HostsText => Details?.Hosts ?? string.Empty;
    public string GuestsText => Details?.Guests ?? string.Empty;
    public string CallersText => Details?.Callers ?? string.Empty;
    public string MentionedPeopleText => Details?.MentionedPeople ?? string.Empty;
    public IReadOnlyList<string> Hosts => SplitPeople(Details?.Hosts);
    public IReadOnlyList<string> Guests => SplitPeople(Details?.Guests);
    public IReadOnlyList<string> Callers => SplitPeople(Details?.Callers);
    public IReadOnlyList<string> MentionedPeople => SplitPeople(Details?.MentionedPeople);
    public string ArchiveNotesText => Details?.ArchiveNotes ?? string.Empty;
    public string PersonalNotesText => Details?.PersonalNotes ?? string.Empty;
    public bool HasArchiveNotes => Details?.HasArchiveNotes == true;
    public bool HasPersonalNotes => Details?.HasPersonalNotes == true;
    public IReadOnlyList<string> Topics => Details?.Topics ?? Array.Empty<string>();
    public bool HasCatalogueDetails => Details?.HasCatalogueDetails == true;
    public IReadOnlyList<BroadcastMetadataField> CatalogueFields => Details?.CatalogueFields ?? Array.Empty<BroadcastMetadataField>();
    public IReadOnlyList<TranscriptSegment> TranscriptSegments => _transcript?.Segments ?? Array.Empty<TranscriptSegment>();
    public bool HasTranscriptSegments => TranscriptSegments.Count > 0;
    public string TranscriptDetailsText => _transcript is null
        ? string.Empty
        : $"{_transcript.WordCount:N0} words · {_transcript.Segments.Count:N0} timed phrases";
    public bool HasTranscript { get => _hasTranscript; private set { if (SetProperty(ref _hasTranscript, value)) RaiseTranscriptState(); } }
    public bool HasNoTranscript => !HasTranscript;
    public bool TranscriptionPending { get => _transcriptionPending; private set { if (SetProperty(ref _transcriptionPending, value)) RaiseTranscriptState(); } }
    public bool CanStartTranscription => HasNoTranscript && !TranscriptionPending && _transcription.Engine.IsAvailable;
    public string TranscriptStateText => HasTranscript
        ? "A timed transcript is available for this broadcast."
        : TranscriptionPending ? "A transcription job is already queued or running." : $"No transcript yet. {_transcription.Engine.AvailabilityMessage}";

    public void SetOpenFullInfoHandler(Func<long, Task> handler)
        => _openFullInfo = handler ?? throw new ArgumentNullException(nameof(handler));
    public void SetOpenTranscriptHandler(Func<long, Task> handler)
        => _openTranscript = handler ?? throw new ArgumentNullException(nameof(handler));

    public async Task LoadAsync()
    {
        await Task.WhenAll(RefreshAsync(), Queue.LoadAsync()).ConfigureAwait(true);
    }

    private async Task RefreshAsync()
    {
        var generation = ++_loadGeneration;
        if (!Playback.HasCurrentBroadcast)
        {
            Details = null;
            HasTranscript = false;
            _transcript = null;
            RaiseTranscriptDocumentProperties();
            TranscriptionPending = false;
            DetailsStatus = "Choose a broadcast to see its full Now Playing information.";
            return;
        }

        IsBusy = true;
        HasTranscript = false;
        _transcript = null;
        RaiseTranscriptDocumentProperties();
        TranscriptionPending = false;
        DetailsStatus = "Loading broadcast information…";
        try
        {
            var details = await _detailsService.GetAsync(Playback.CurrentBroadcastId).ConfigureAwait(true);
            if (generation != _loadGeneration) return;
            Details = details;
            await RefreshTranscriptStateAsync(Playback.CurrentBroadcastId).ConfigureAwait(true);
            if (details is not null)
                await RelatedWiki.LoadAsync(details.CollectionName, details.Hosts, details.Guests, details.Callers,
                    details.MentionedPeople, string.Join(",", details.Topics), details.Event).ConfigureAwait(true);
            else RelatedWiki.Clear();
            DetailsStatus = details is null
                ? "No additional broadcast information is available."
                : "Broadcast information loaded.";
        }
        catch (Exception exception)
        {
            if (generation != _loadGeneration) return;
            Details = null;
            DetailsStatus = $"Broadcast information could not load: {exception.Message}";
        }
        finally
        {
            if (generation == _loadGeneration) IsBusy = false;
        }
    }

    private Task OpenFullInfoAsync()
        => Playback.HasCurrentBroadcast && _openFullInfo is not null
            ? _openFullInfo(Playback.CurrentBroadcastId)
            : Task.CompletedTask;

    private Task OpenTranscriptAsync()
        => Playback.HasCurrentBroadcast && _openTranscript is not null
            ? _openTranscript(Playback.CurrentBroadcastId)
            : Task.CompletedTask;

    private async Task StartTranscriptionAsync()
    {
        if (!Playback.HasCurrentBroadcast) return;
        var settings = (_transcription.Engine as WhisperCppTranscriptionEngine)?.GetSettings() ?? new WhisperCppEngineSettings();
        await _transcription.QueueAsync(Playback.CurrentBroadcastId, new TranscriptionJobOptions(
            settings.DefaultLanguage,
            settings.ModelId,
            EnableSpeakerDiarization: settings.EnableMultiSpeakerDiarization,
            UseVoiceActivityDetection: settings.UseVoiceActivityDetection)).ConfigureAwait(true);
        TranscriptionPending = true;
        DetailsStatus = "Full transcription queued.";
    }

    private async Task RefreshTranscriptStateAsync(long episodeId)
    {
        _transcript = await _transcripts.GetForEpisodeAsync(episodeId).ConfigureAwait(true);
        HasTranscript = _transcript is not null;
        RaiseTranscriptDocumentProperties();
        var jobs = await _transcription.GetJobsAsync(1000).ConfigureAwait(true);
        TranscriptionPending = jobs.Any(x => x.EpisodeId == episodeId && x.State is TranscriptionJobState.Queued or TranscriptionJobState.Running);
    }

    private async Task PlayTranscriptSegmentAsync(TranscriptSegment segment)
    {
        if (!Playback.HasCurrentBroadcast) return;
        try { await Playback.LoadAndPlayAtAsync(Playback.CurrentBroadcastId, segment.StartMs).ConfigureAwait(true); }
        catch (Exception exception) { SetError(exception); }
    }

    private void RaiseTranscriptDocumentProperties()
    {
        RaisePropertyChanged(nameof(TranscriptSegments));
        RaisePropertyChanged(nameof(HasTranscriptSegments));
        RaisePropertyChanged(nameof(TranscriptDetailsText));
    }

    private void PlaybackOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackViewModel.IsPrimaryTransportLoading)
            or nameof(PlaybackViewModel.StatusText))
        {
            RaisePropertyChanged(nameof(IsWorking));
            RaisePropertyChanged(nameof(ActivityText));
        }

        if (e.PropertyName is nameof(PlaybackViewModel.CurrentBroadcastId)
            or nameof(PlaybackViewModel.HasCurrentBroadcast))
        {
            RaisePropertyChanged(nameof(Playback));
            RaiseCommandState();
            _ = RefreshAsync();
        }
    }

    private void RaiseDetailProperties()
    {
        RaisePropertyChanged(nameof(HasDetails));
        RaisePropertyChanged(nameof(HasSummary));
        RaisePropertyChanged(nameof(HasPeople));
        RaisePropertyChanged(nameof(HasTopics));
        RaisePropertyChanged(nameof(HasNotes));
        RaisePropertyChanged(nameof(HasHosts));
        RaisePropertyChanged(nameof(HasGuests));
        RaisePropertyChanged(nameof(HasCallers));
        RaisePropertyChanged(nameof(HasMentionedPeople));
        RaisePropertyChanged(nameof(SummaryText));
        RaisePropertyChanged(nameof(HostsText));
        RaisePropertyChanged(nameof(GuestsText));
        RaisePropertyChanged(nameof(CallersText));
        RaisePropertyChanged(nameof(MentionedPeopleText));
        RaisePropertyChanged(nameof(Hosts));
        RaisePropertyChanged(nameof(Guests));
        RaisePropertyChanged(nameof(Callers));
        RaisePropertyChanged(nameof(MentionedPeople));
        RaisePropertyChanged(nameof(ArchiveNotesText));
        RaisePropertyChanged(nameof(PersonalNotesText));
        RaisePropertyChanged(nameof(HasArchiveNotes));
        RaisePropertyChanged(nameof(HasPersonalNotes));
        RaisePropertyChanged(nameof(Topics));
        RaisePropertyChanged(nameof(HasCatalogueDetails));
        RaisePropertyChanged(nameof(CatalogueFields));
    }

    private void RaiseCommandState()
    {
        ((AsyncCommand)RefreshCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)OpenFullInfoCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)OpenTranscriptCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)StartTranscriptionCommand).RaiseCanExecuteChanged();
    }

    private void RaiseTranscriptState()
    {
        RaisePropertyChanged(nameof(HasNoTranscript));
        RaisePropertyChanged(nameof(CanStartTranscription));
        RaisePropertyChanged(nameof(TranscriptStateText));
        RaiseCommandState();
    }

    private static IReadOnlyList<string> SplitPeople(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
        return value
            .Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private void SetError(Exception exception) => DetailsStatus = exception.Message;

    public void Dispose() => Playback.PropertyChanged -= PlaybackOnPropertyChanged;
}
