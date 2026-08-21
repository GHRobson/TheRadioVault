using System.Windows.Input;
using TheRadioVault.Core.Domain;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class BroadcastRowViewModel : ObservableObject
{
    private bool _isFavourite;
    private bool _isCurrentPlayback;
    private bool _isCurrentlyPlaying;
    private BroadcastDetails? _details;
    private bool _detailsLoading;
    private long _livePositionMs;
    private long _liveDurationMs;
    private bool _liveCompleted;
    private bool _isDownloaded;
    private string _wikiSummary = string.Empty;
    private string _wikiSummarySource = string.Empty;
    private readonly AsyncCommand _downloadCommand;

    public BroadcastRowViewModel(
        LibraryBroadcastSummary source,
        Func<BroadcastRowViewModel, Task>? play = null,
        Func<BroadcastRowViewModel, Task>? toggleFavourite = null,
        Func<BroadcastRowViewModel, bool, Task>? addToQueue = null,
        Func<BroadcastRowViewModel, Task>? playOrToggle = null,
        Func<BroadcastRowViewModel, Task>? openFullInfo = null,
        Func<BroadcastRowViewModel, Task>? transcribe = null,
        Func<BroadcastRowViewModel, bool, Task>? setPlayed = null,
        Func<BroadcastRowViewModel, Task>? download = null,
        bool showCollectionText = true)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        _isFavourite = source.Favourite;
        _livePositionMs = source.PositionMs;
        _liveDurationMs = source.DurationMs;
        _liveCompleted = source.Completed;
        Title = string.IsNullOrWhiteSpace(source.Title)
            ? source.AirDate?.ToString("dddd, d MMMM yyyy") ?? source.BroadcastId
            : source.Title.Trim();
        // Date text is computed from the canonical date first and can later
        // fall back to a non-invented catalogue date clue when details load.
        CollectionText = source.CollectionName;
        ShowCollectionText = showCollectionText;
        SlotText = string.IsNullOrWhiteSpace(source.BroadcastSlot) ? "Standard" : source.BroadcastSlot;

        StructureText = source.SegmentCount > 1
            ? $"{source.SegmentCount} parts"
            : source.RecordingCount > 1
                ? $"{source.RecordingCount} recordings"
                : "Single recording";
        AttentionText = source.NeedsAttention
            ? (string.IsNullOrWhiteSpace(source.AttentionReason) ? "Needs attention" : source.AttentionReason)
            : string.Empty;
        PlayCommand = new AsyncCommand(
            () => play is null ? Task.CompletedTask : play(this),
            () => play is not null);
        RowPlaybackCommand = new AsyncCommand(
            () => playOrToggle is not null
                ? playOrToggle(this)
                : play is not null ? play(this) : Task.CompletedTask,
            () => playOrToggle is not null || play is not null);
        ToggleFavouriteCommand = new AsyncCommand(
            () => toggleFavourite is null ? Task.CompletedTask : toggleFavourite(this),
            () => toggleFavourite is not null);
        AddToQueueCommand = new AsyncCommand(
            () => addToQueue is null ? Task.CompletedTask : addToQueue(this, false),
            () => addToQueue is not null);
        PlayNextCommand = new AsyncCommand(
            () => addToQueue is null ? Task.CompletedTask : addToQueue(this, true),
            () => addToQueue is not null);
        OpenFullInfoCommand = new AsyncCommand(
            () => openFullInfo is null ? Task.CompletedTask : openFullInfo(this),
            () => openFullInfo is not null);
        TranscribeCommand = new AsyncCommand(
            () => transcribe is null ? Task.CompletedTask : transcribe(this),
            () => transcribe is not null);
        MarkListenedCommand = new AsyncCommand(
            () => setPlayed is null ? Task.CompletedTask : setPlayed(this, true),
            () => setPlayed is not null);
        MarkUnlistenedCommand = new AsyncCommand(
            () => setPlayed is null ? Task.CompletedTask : setPlayed(this, false),
            () => setPlayed is not null);
        _downloadCommand = new AsyncCommand(
            () => download is null ? Task.CompletedTask : download(this),
            () => download is not null && !IsDownloaded);
        DownloadCommand = _downloadCommand;
    }

    public LibraryBroadcastSummary Source { get; }
    public ICommand PlayCommand { get; }
    public ICommand RowPlaybackCommand { get; }
    public ICommand ToggleFavouriteCommand { get; }
    public ICommand AddToQueueCommand { get; }
    public ICommand PlayNextCommand { get; }
    public ICommand OpenFullInfoCommand { get; }
    public ICommand TranscribeCommand { get; }
    public ICommand MarkListenedCommand { get; }
    public ICommand MarkUnlistenedCommand { get; }
    public ICommand DownloadCommand { get; }
    public string Title { get; }
    public string DateText => Source.AirDate?.ToString("ddd, d MMM yyyy")
        ?? (Details?.DateText is { Length: > 0 } detailsDate ? detailsDate : "Date unknown");
    public string DateDayText => Source.AirDate?.ToString("ddd").ToUpperInvariant() ?? "DATE";
    public string DateRestText => Source.AirDate?.ToString("d MMM yyyy")
        ?? (Details?.DateText is { Length: > 0 } detailsDate ? detailsDate : "Unknown");
    public string CollectionText { get; }
    public bool ShowCollectionText { get; }
    public string SlotText { get; }
    public string ProgressText => _liveCompleted
        ? "Completed"
        : _livePositionMs > 0
            ? $"{ProgressPercent}% listened"
            : "Unplayed";
    public string CompactProgressText => _liveCompleted
        ? "100%"
        : $"{ProgressPercent}%";
    public int ProgressPercent => _liveCompleted
        ? 100
        : _liveDurationMs > 0
        ? Math.Clamp((int)Math.Round(_livePositionMs * 100d / _liveDurationMs), 0, 99)
        : 0;
    public string StructureText { get; }
    public string AttentionText { get; }
    public string? ArtworkPath => Source.ArtworkPath;
    public bool HasArtwork => !string.IsNullOrWhiteSpace(ArtworkPath) && File.Exists(ArtworkPath);
    public bool IsFavourite => _isFavourite;
    public bool IsNotFavourite => !_isFavourite;
    public string FavouriteGlyph => IsFavourite ? "♥" : "♡";
    public string FavouriteToolTip => IsFavourite ? "Remove from favourites" : "Add to favourites";
    public bool NeedsAttention => Source.NeedsAttention;
    public bool IsCompleted => _liveCompleted;
    public bool IsInProgress => !_liveCompleted && _livePositionMs > 0;
    public bool IsUnplayed => !_liveCompleted && _livePositionMs <= 0;
    public bool IsDownloaded => _isDownloaded;
    public bool CanDownload => !_isDownloaded;
    public string DownloadActionText => IsDownloaded ? "Downloaded to this PC" : "Download to this PC";
    public string? Description => Source.Description;
    public string DescriptionText => Details?.HasSummary == true
        ? Details.Summary.Trim()
        : string.IsNullOrWhiteSpace(Source.Description)
            ? "No broadcast notes have been added yet."
            : Source.Description.Trim();
    public bool HasDescription => Details?.HasSummary == true || !string.IsNullOrWhiteSpace(Source.Description);
    public string WikiSummary => _wikiSummary;
    public string WikiSummarySource => _wikiSummarySource;
    public bool HasWikiSummary => !string.IsNullOrWhiteSpace(_wikiSummary);
    public string WikiSummaryHeading => string.IsNullOrWhiteSpace(_wikiSummarySource)
        ? "From Explore"
        : $"From Explore · {_wikiSummarySource}";
    public string BroadcastId => Source.BroadcastId;
    public bool IsCurrentPlayback => _isCurrentPlayback;
    public bool IsCurrentlyPlaying => _isCurrentlyPlaying;
    public string RowPlaybackGlyph => IsCurrentPlayback && IsCurrentlyPlaying ? "Ⅱ" : "▶";
    public string RowPlaybackToolTip => IsCurrentPlayback && IsCurrentlyPlaying ? "Pause broadcast" : "Play broadcast";
    public BroadcastDetails? Details => _details;
    public bool IsDetailsLoading { get => _detailsLoading; private set => SetProperty(ref _detailsLoading, value); }
    public bool HasPeople => Details?.HasPeople == true;
    public bool HasTopics => Details?.HasTopics == true;
    public string HostsText => Details?.Hosts ?? string.Empty;
    public string GuestsText => Details?.Guests ?? string.Empty;
    public string CallersText => Details?.Callers ?? string.Empty;
    public string MentionedPeopleText => Details?.MentionedPeople ?? string.Empty;
    public bool HasHosts => Details?.HasHosts == true;
    public bool HasGuests => Details?.HasGuests == true;
    public bool HasCallers => Details?.HasCallers == true;
    public bool HasMentionedPeople => Details?.HasMentionedPeople == true;
    public IReadOnlyList<string> Hosts => SplitPeople(Details?.Hosts);
    public IReadOnlyList<string> Guests => SplitPeople(Details?.Guests);
    public IReadOnlyList<string> Callers => SplitPeople(Details?.Callers);
    public IReadOnlyList<string> MentionedPeople => SplitPeople(Details?.MentionedPeople);
    public IReadOnlyList<ArchiveEntityLink> HostLinks => Details?.HostLinks ?? [];
    public IReadOnlyList<ArchiveEntityLink> GuestLinks => Details?.GuestLinks ?? [];
    public IReadOnlyList<ArchiveEntityLink> CallerLinks => Details?.CallerLinks ?? [];
    public IReadOnlyList<ArchiveEntityLink> MentionedPeopleLinks => Details?.MentionedPeopleLinks ?? [];
    public IReadOnlyList<string> People => Hosts.Concat(Guests).Concat(Callers).Concat(MentionedPeople)
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .ToArray();
    public IReadOnlyList<string> Topics => Details?.Topics ?? Array.Empty<string>();
    public IReadOnlyList<ArchiveEntityLink> TopicLinks => Details?.TopicLinks ?? [];
    public bool HasCatalogueDetails => Details?.HasCatalogueDetails == true;
    public IReadOnlyList<BroadcastMetadataField> CatalogueFields => Details?.CatalogueFields ?? Array.Empty<BroadcastMetadataField>();
    public string CatalogueContextText => Details?.CatalogueContextText ?? string.Empty;
    public string BroadcastContextText => string.Join(" · ", new[]
    {
        CollectionText,
        string.IsNullOrWhiteSpace(SlotText) || string.Equals(SlotText, "Standard", StringComparison.OrdinalIgnoreCase) ? null : SlotText,
        StructureText
    }.Where(x => !string.IsNullOrWhiteSpace(x)));

    public void ApplyLiveProgress(long positionMs, long durationMs, bool completed)
    {
        var nextPosition = Math.Max(0, positionMs);
        var nextDuration = Math.Max(0, durationMs);
        if (_livePositionMs == nextPosition && _liveDurationMs == nextDuration && _liveCompleted == completed) return;
        _livePositionMs = nextPosition;
        _liveDurationMs = nextDuration;
        _liveCompleted = completed;
        RaisePropertyChanged(nameof(ProgressPercent));
        RaisePropertyChanged(nameof(ProgressText));
        RaisePropertyChanged(nameof(CompactProgressText));
        RaisePropertyChanged(nameof(IsCompleted));
        RaisePropertyChanged(nameof(IsInProgress));
        RaisePropertyChanged(nameof(IsUnplayed));
    }

    public void SetPlaybackState(bool isCurrentPlayback, bool isCurrentlyPlaying)
    {
        var currentChanged = _isCurrentPlayback != isCurrentPlayback;
        var playingChanged = _isCurrentlyPlaying != isCurrentlyPlaying;
        if (!currentChanged && !playingChanged) return;
        _isCurrentPlayback = isCurrentPlayback;
        _isCurrentlyPlaying = isCurrentlyPlaying;
        if (currentChanged) RaisePropertyChanged(nameof(IsCurrentPlayback));
        if (playingChanged) RaisePropertyChanged(nameof(IsCurrentlyPlaying));
        RaisePropertyChanged(nameof(RowPlaybackGlyph));
        RaisePropertyChanged(nameof(RowPlaybackToolTip));
    }

    public void SetFavourite(bool favourite)
    {
        if (_isFavourite == favourite) return;
        _isFavourite = favourite;
        RaisePropertyChanged(nameof(IsFavourite));
        RaisePropertyChanged(nameof(IsNotFavourite));
        RaisePropertyChanged(nameof(FavouriteGlyph));
        RaisePropertyChanged(nameof(FavouriteToolTip));
    }

    public void SetDownloaded(bool downloaded)
    {
        if (_isDownloaded == downloaded) return;
        _isDownloaded = downloaded;
        RaisePropertyChanged(nameof(IsDownloaded));
        RaisePropertyChanged(nameof(CanDownload));
        RaisePropertyChanged(nameof(DownloadActionText));
        _downloadCommand.RaiseCanExecuteChanged();
    }

    public void SetWikiSummary(string? summary, string? source)
    {
        var nextSummary = summary?.Trim() ?? string.Empty;
        var nextSource = source?.Trim() ?? string.Empty;
        if (_wikiSummary == nextSummary && _wikiSummarySource == nextSource) return;
        _wikiSummary = nextSummary;
        _wikiSummarySource = nextSource;
        RaisePropertyChanged(nameof(WikiSummary));
        RaisePropertyChanged(nameof(WikiSummarySource));
        RaisePropertyChanged(nameof(HasWikiSummary));
        RaisePropertyChanged(nameof(WikiSummaryHeading));
    }

    public void SetDetailsLoading(bool loading) => IsDetailsLoading = loading;

    public void SetDetails(BroadcastDetails? details)
    {
        _details = details;
        IsDetailsLoading = false;
        RaisePropertyChanged(nameof(Details));
        RaisePropertyChanged(nameof(DateText));
        RaisePropertyChanged(nameof(DateDayText));
        RaisePropertyChanged(nameof(DateRestText));
        RaisePropertyChanged(nameof(DescriptionText));
        RaisePropertyChanged(nameof(HasDescription));
        RaisePropertyChanged(nameof(HasPeople));
        RaisePropertyChanged(nameof(HasTopics));
        RaisePropertyChanged(nameof(HostsText));
        RaisePropertyChanged(nameof(GuestsText));
        RaisePropertyChanged(nameof(CallersText));
        RaisePropertyChanged(nameof(MentionedPeopleText));
        RaisePropertyChanged(nameof(HasHosts));
        RaisePropertyChanged(nameof(HasGuests));
        RaisePropertyChanged(nameof(HasCallers));
        RaisePropertyChanged(nameof(HasMentionedPeople));
        RaisePropertyChanged(nameof(Hosts));
        RaisePropertyChanged(nameof(Guests));
        RaisePropertyChanged(nameof(Callers));
        RaisePropertyChanged(nameof(MentionedPeople));
        RaisePropertyChanged(nameof(HostLinks));
        RaisePropertyChanged(nameof(GuestLinks));
        RaisePropertyChanged(nameof(CallerLinks));
        RaisePropertyChanged(nameof(MentionedPeopleLinks));
        RaisePropertyChanged(nameof(People));
        RaisePropertyChanged(nameof(Topics));
        RaisePropertyChanged(nameof(TopicLinks));
        RaisePropertyChanged(nameof(HasCatalogueDetails));
        RaisePropertyChanged(nameof(CatalogueFields));
        RaisePropertyChanged(nameof(CatalogueContextText));
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
}
