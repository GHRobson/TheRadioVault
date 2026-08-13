using System.Windows.Input;
using TheRadioVault.Core.Domain;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;
using TheRadioVault.Transcription.Services;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class FullBroadcastInfoViewModel : ObservableObject
{
    private readonly IBroadcastDetailsService _detailsService;
    private readonly PlaybackViewModel _playback;
    private readonly QueueViewModel _queue;
    private readonly ITranscriptRepository _transcripts;
    private readonly ITranscriptionCoordinator _transcription;
    private Func<Task>? _back;
    private Func<long, Task>? _openTranscript;
    private BroadcastDetails? _details;
    private bool _isBusy;
    private string _statusText = "Select Broadcast information from a Library row.";
    private bool _hasTranscript;
    private bool _transcriptionPending;
    private TranscriptDocument? _transcript;

    public FullBroadcastInfoViewModel(
        IBroadcastDetailsService detailsService,
        PlaybackViewModel playback,
        QueueViewModel queue,
        ITranscriptRepository transcripts,
        ITranscriptionCoordinator transcription,
        IWikiService? wiki = null)
    {
        _detailsService = detailsService ?? throw new ArgumentNullException(nameof(detailsService));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _transcripts = transcripts ?? throw new ArgumentNullException(nameof(transcripts));
        _transcription = transcription ?? throw new ArgumentNullException(nameof(transcription));
        RelatedWiki = new RelatedWikiPagesViewModel(wiki);
        BackCommand = new AsyncCommand(() => _back?.Invoke() ?? Task.CompletedTask);
        PlayCommand = new AsyncCommand(PlayAsync, () => Details is not null && !IsBusy, SetError);
        PlayNextCommand = new AsyncCommand(() => QueueAsync(true), () => Details is not null && !IsBusy, SetError);
        AddToQueueCommand = new AsyncCommand(() => QueueAsync(false), () => Details is not null && !IsBusy, SetError);
        OpenTranscriptCommand = new AsyncCommand(OpenTranscriptAsync, () => Details is not null && HasTranscript && !IsBusy, SetError);
        StartTranscriptionCommand = new AsyncCommand(StartTranscriptionAsync, () => Details is not null && CanStartTranscription && !IsBusy, SetError);
        PlayTranscriptSegmentCommand = new DelegateCommand(parameter =>
        {
            if (parameter is TranscriptSegment segment) _ = PlayTranscriptSegmentAsync(segment);
        });
    }

    public ICommand BackCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand PlayNextCommand { get; }
    public ICommand AddToQueueCommand { get; }
    public ICommand OpenTranscriptCommand { get; }
    public ICommand StartTranscriptionCommand { get; }
    public ICommand PlayTranscriptSegmentCommand { get; }
    public RelatedWikiPagesViewModel RelatedWiki { get; }
    public BroadcastDetails? Details { get => _details; private set { if (SetProperty(ref _details, value)) RaiseDetailProperties(); } }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommandState(); } }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool HasDetails => Details is not null;
    public bool HasSummary => Details?.HasSummary == true;
    public bool HasPeople => Details?.HasPeople == true;
    public bool HasTopics => Details?.HasTopics == true;
    public bool HasArchiveNotes => Details?.HasArchiveNotes == true;
    public bool HasPersonalNotes => Details?.HasPersonalNotes == true;
    public bool HasSlot => !string.IsNullOrWhiteSpace(Details?.Slot);
    public bool HasStation => !string.IsNullOrWhiteSpace(Details?.Station);
    public bool HasEdition => !string.IsNullOrWhiteSpace(Details?.Edition);
    public bool HasVariant => !string.IsNullOrWhiteSpace(Details?.BroadcastVariant);
    public bool HasEra => !string.IsNullOrWhiteSpace(Details?.BroadcastEra);
    public bool HasEpisodeType => !string.IsNullOrWhiteSpace(Details?.EpisodeType);
    public bool HasCatalogueDetails => Details?.HasCatalogueDetails == true;
    public IReadOnlyList<BroadcastMetadataField> CatalogueFields => Details?.CatalogueFields ?? Array.Empty<BroadcastMetadataField>();
    public string Title => string.IsNullOrWhiteSpace(Details?.Title) ? Details?.DateText ?? "Broadcast information" : Details!.Title;
    public string DateText => Details?.DateText ?? string.Empty;
    public string SummaryText => Details?.Summary ?? string.Empty;
    public string HostsText => Details?.Hosts ?? string.Empty;
    public string GuestsText => Details?.Guests ?? string.Empty;
    public string CallersText => Details?.Callers ?? string.Empty;
    public string MentionedPeopleText => Details?.MentionedPeople ?? string.Empty;
    public IReadOnlyList<string> Hosts => SplitPeople(Details?.Hosts);
    public IReadOnlyList<string> Guests => SplitPeople(Details?.Guests);
    public IReadOnlyList<string> Callers => SplitPeople(Details?.Callers);
    public IReadOnlyList<string> MentionedPeople => SplitPeople(Details?.MentionedPeople);
    public IReadOnlyList<ArchiveEntityLink> HostLinks => Details?.HostLinks ?? [];
    public IReadOnlyList<ArchiveEntityLink> GuestLinks => Details?.GuestLinks ?? [];
    public IReadOnlyList<ArchiveEntityLink> CallerLinks => Details?.CallerLinks ?? [];
    public IReadOnlyList<ArchiveEntityLink> MentionedPeopleLinks => Details?.MentionedPeopleLinks ?? [];
    public bool HasHosts => Details?.HasHosts == true;
    public bool HasGuests => Details?.HasGuests == true;
    public bool HasCallers => Details?.HasCallers == true;
    public bool HasMentionedPeople => Details?.HasMentionedPeople == true;
    public IReadOnlyList<string> Topics => Details?.Topics ?? Array.Empty<string>();
    public IReadOnlyList<ArchiveEntityLink> TopicLinks => Details?.TopicLinks ?? [];
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

    public void SetBackHandler(Func<Task> handler) => _back = handler;
    public void SetOpenTranscriptHandler(Func<long, Task> handler) => _openTranscript = handler;

    private static IReadOnlyList<string> SplitPeople(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

    public async Task LoadAsync(long representativeEpisodeId)
    {
        IsBusy = true;
        HasTranscript = false;
        _transcript = null;
        RaisePropertyChanged(nameof(TranscriptSegments));
        RaisePropertyChanged(nameof(HasTranscriptSegments));
        RaisePropertyChanged(nameof(TranscriptDetailsText));
        TranscriptionPending = false;
        StatusText = "Loading full broadcast information…";
        try
        {
            Details = await _detailsService.GetAsync(representativeEpisodeId).ConfigureAwait(true);
            await RefreshTranscriptStateAsync(representativeEpisodeId).ConfigureAwait(true);
            if (Details is { } details)
                await RelatedWiki.LoadAsync(details.CollectionName, details.Hosts, details.Guests, details.Callers,
                    details.MentionedPeople, string.Join(",", details.Topics), details.Event).ConfigureAwait(true);
            else RelatedWiki.Clear();
            StatusText = Details is null ? "Broadcast information was not found." : "Full broadcast information";
        }
        catch (Exception exception)
        {
            Details = null;
            StatusText = $"Broadcast information could not load: {exception.Message}";
        }
        finally { IsBusy = false; }
    }

    private Task PlayAsync()
        => Details is null ? Task.CompletedTask : _playback.LoadAndPlayAsync(Details.RepresentativeEpisodeId);

    private Task QueueAsync(bool playNext)
        => Details is null ? Task.CompletedTask : _queue.AddAsync(Details.RepresentativeEpisodeId, playNext);

    private Task OpenTranscriptAsync()
        => Details is not null && _openTranscript is not null
            ? _openTranscript(Details.RepresentativeEpisodeId)
            : Task.CompletedTask;

    private async Task StartTranscriptionAsync()
    {
        if (Details is null) return;
        var settings = (_transcription.Engine as WhisperCppTranscriptionEngine)?.GetSettings() ?? new WhisperCppEngineSettings();
        await _transcription.QueueAsync(Details.RepresentativeEpisodeId, new TranscriptionJobOptions(
            settings.DefaultLanguage,
            settings.ModelId,
            EnableSpeakerDiarization: settings.EnableMultiSpeakerDiarization,
            UseVoiceActivityDetection: settings.UseVoiceActivityDetection)).ConfigureAwait(true);
        TranscriptionPending = true;
        StatusText = "Full transcription queued.";
    }

    private async Task RefreshTranscriptStateAsync(long episodeId)
    {
        _transcript = await _transcripts.GetForEpisodeAsync(episodeId).ConfigureAwait(true);
        HasTranscript = _transcript is not null;
        RaisePropertyChanged(nameof(TranscriptSegments));
        RaisePropertyChanged(nameof(HasTranscriptSegments));
        RaisePropertyChanged(nameof(TranscriptDetailsText));
        var jobs = await _transcription.GetJobsAsync(1000).ConfigureAwait(true);
        TranscriptionPending = jobs.Any(x => x.EpisodeId == episodeId && x.State is TranscriptionJobState.Queued or TranscriptionJobState.Running);
    }

    private async Task PlayTranscriptSegmentAsync(TranscriptSegment segment)
    {
        if (Details is null) return;
        try { await _playback.LoadAndPlayAtAsync(Details.RepresentativeEpisodeId, segment.StartMs).ConfigureAwait(true); }
        catch (Exception exception) { SetError(exception); }
    }

    private void RaiseDetailProperties()
    {
        RaisePropertyChanged(nameof(HasDetails));
        RaisePropertyChanged(nameof(HasSummary));
        RaisePropertyChanged(nameof(HasPeople));
        RaisePropertyChanged(nameof(HasTopics));
        RaisePropertyChanged(nameof(HasArchiveNotes));
        RaisePropertyChanged(nameof(HasPersonalNotes));
        RaisePropertyChanged(nameof(HasSlot));
        RaisePropertyChanged(nameof(HasStation));
        RaisePropertyChanged(nameof(HasEdition));
        RaisePropertyChanged(nameof(HasVariant));
        RaisePropertyChanged(nameof(HasEra));
        RaisePropertyChanged(nameof(HasEpisodeType));
        RaisePropertyChanged(nameof(HasCatalogueDetails));
        RaisePropertyChanged(nameof(CatalogueFields));
        RaisePropertyChanged(nameof(Title));
        RaisePropertyChanged(nameof(DateText));
        RaisePropertyChanged(nameof(SummaryText));
        RaisePropertyChanged(nameof(HostsText));
        RaisePropertyChanged(nameof(GuestsText));
        RaisePropertyChanged(nameof(CallersText));
        RaisePropertyChanged(nameof(MentionedPeopleText));
        RaisePropertyChanged(nameof(Hosts));
        RaisePropertyChanged(nameof(Guests));
        RaisePropertyChanged(nameof(Callers));
        RaisePropertyChanged(nameof(MentionedPeople));
        RaisePropertyChanged(nameof(HostLinks));
        RaisePropertyChanged(nameof(GuestLinks));
        RaisePropertyChanged(nameof(CallerLinks));
        RaisePropertyChanged(nameof(MentionedPeopleLinks));
        RaisePropertyChanged(nameof(HasHosts));
        RaisePropertyChanged(nameof(HasGuests));
        RaisePropertyChanged(nameof(HasCallers));
        RaisePropertyChanged(nameof(HasMentionedPeople));
        RaisePropertyChanged(nameof(Topics));
        RaisePropertyChanged(nameof(TopicLinks));
        RaiseCommandState();
    }

    private void RaiseCommandState()
    {
        ((AsyncCommand)PlayCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)PlayNextCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)AddToQueueCommand).RaiseCanExecuteChanged();
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

    private void SetError(Exception exception) => StatusText = exception.Message;
}
