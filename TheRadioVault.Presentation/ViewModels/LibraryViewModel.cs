using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using TheRadioVault.Core.Services;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;
using TheRadioVault.Transcription.Services;

namespace TheRadioVault.Presentation.ViewModels;

public enum LibraryViewMode
{
    List,
    Grid
}

public sealed class LibraryViewModel : ObservableObject
{
    private readonly ILibraryBrowseService _library;
    private readonly ILibraryActionService _actions;
    private readonly IBroadcastDetailsService _details;
    private readonly PlaybackViewModel _playback;
    private readonly QueueViewModel _queue;
    private readonly ITranscriptionCoordinator _transcription;
    private readonly DownloadsViewModel _downloads;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private bool _isBusy;
    private bool _isLoaded;
    private bool _collectionsLoaded;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _collectionsLoadedAt = DateTimeOffset.MinValue;
    private string _searchText = string.Empty;
    private string _statusText = "Library has not been loaded yet.";
    private LibraryFilterOptionViewModel _selectedFilter;
    private BroadcastRowViewModel? _selectedBroadcast;
    private string _libraryModelText = string.Empty;
    private int? _selectedCollectionId;
    private string _selectedCollectionName = "All broadcasts";
    private LibraryViewMode _viewMode = LibraryViewMode.List;
    private int? _archiveYear;
    private int? _archiveMonth;
    private Func<long, Task>? _openBroadcastInfo;
    private int _detailsGeneration;
    private int _queryVersion;
    private CancellationTokenSource? _queryDebounce;
    private bool _suppressAutomaticRefresh;
    private bool _hideCompleted;
    private readonly SemaphoreSlim _liveMembershipGate = new(1, 1);
    private long _lastLiveEpisodeId;
    private bool _lastLiveCompleted;
    private bool _lastLiveInProgress;

    public LibraryViewModel(
        ILibraryBrowseService library,
        ILibraryActionService actions,
        IBroadcastDetailsService details,
        PlaybackViewModel playback,
        QueueViewModel queue,
        ITranscriptionCoordinator transcription,
        DownloadsViewModel downloads,
        IWikiService? wiki = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _details = details ?? throw new ArgumentNullException(nameof(details));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _transcription = transcription ?? throw new ArgumentNullException(nameof(transcription));
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        RelatedWiki = new RelatedWikiPagesViewModel(wiki);
        Filters = new ObservableCollection<LibraryFilterOptionViewModel>(new[]
        {
            new LibraryFilterOptionViewModel("All broadcasts", LibraryListeningFilter.All),
            new LibraryFilterOptionViewModel("Continue listening", LibraryListeningFilter.ContinueListening),
            new LibraryFilterOptionViewModel("Favourites", LibraryListeningFilter.Favourites),
            new LibraryFilterOptionViewModel("Completed", LibraryListeningFilter.Completed),
            new LibraryFilterOptionViewModel("Unplayed", LibraryListeningFilter.Unplayed),
            new LibraryFilterOptionViewModel("Needs attention", LibraryListeningFilter.NeedsAttention),
            new LibraryFilterOptionViewModel("Recently added", LibraryListeningFilter.RecentlyAdded),
            new LibraryFilterOptionViewModel("On this day", LibraryListeningFilter.OnThisDay)
        });
        _selectedFilter = Filters[0];
        SearchCommand = new AsyncCommand(() => LoadAsync(force: true), () => !IsBusy, SetError);
        RefreshCommand = SearchCommand;
        ClearSearchCommand = new AsyncCommand(ClearSearchAsync, () => !IsBusy, SetError);
        ShowListCommand = new AsyncCommand(ShowListAsync, () => !IsBusy, SetError);
        ShowGridCommand = new AsyncCommand(ShowGridAsync, () => !IsBusy, SetError);
        ArchiveBackCommand = new AsyncCommand(ArchiveBackAsync, () => !IsBusy && (_archiveYear.HasValue || _archiveMonth.HasValue), SetError);
        ArchiveRootCommand = new AsyncCommand(ArchiveRootAsync, () => !IsBusy && _archiveYear.HasValue, SetError);
        ArchiveYearCommand = new AsyncCommand(ArchiveYearAsync, () => !IsBusy && _archiveYear.HasValue && _archiveMonth.HasValue, SetError);
        _playback.FavouriteChanged += PlaybackOnFavouriteChanged;
        _playback.PropertyChanged += PlaybackOnPropertyChanged;
        _downloads.DownloadsChanged += (_, _) => SyncDownloadState();
    }

    public ObservableCollection<BroadcastRowViewModel> Broadcasts { get; } = new();
    public ObservableCollection<LibraryArchivePeriodViewModel> ArchivePeriods { get; } = new();
    public ObservableCollection<LibraryFilterOptionViewModel> Filters { get; }
    public ObservableCollection<LibraryCollectionSummary> Collections { get; } = new();
    public RelatedWikiPagesViewModel RelatedWiki { get; }
    public ICommand SearchCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ShowListCommand { get; }
    public ICommand ShowGridCommand { get; }
    public ICommand ArchiveBackCommand { get; }
    public ICommand ArchiveRootCommand { get; }
    public ICommand ArchiveYearCommand { get; }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommandState(); } }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            OnQueryChanged();
        }
    }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string LibraryModelText { get => _libraryModelText; private set => SetProperty(ref _libraryModelText, value); }
    public int? SelectedCollectionId => _selectedCollectionId;
    public string SelectedCollectionName
    {
        get => _selectedCollectionName;
        private set
        {
            if (!SetProperty(ref _selectedCollectionName, value)) return;
            RaisePropertyChanged(nameof(PageTitle));
            RaisePropertyChanged(nameof(PageDescription));
            RaisePropertyChanged(nameof(IsCatalogueCollection));
            RaisePropertyChanged(nameof(CanUseGridView));
        }
    }
    public bool IsCollectionFiltered => _selectedCollectionId.HasValue;
    public string PageTitle => SelectedFilter.Filter switch
    {
        LibraryListeningFilter.ContinueListening => "Continue listening",
        LibraryListeningFilter.Favourites => "Favourites",
        LibraryListeningFilter.Completed => "Completed",
        LibraryListeningFilter.Unplayed => "Unplayed",
        LibraryListeningFilter.NeedsAttention => "Needs attention",
        LibraryListeningFilter.RecentlyAdded => "Recently added",
        LibraryListeningFilter.OnThisDay => "On this day",
        _ => IsCollectionFiltered ? SelectedCollectionName : "Library"
    };
    public string PageDescription => SelectedFilter.Filter switch
    {
        LibraryListeningFilter.ContinueListening => "Unfinished broadcasts ordered by your latest listening activity.",
        LibraryListeningFilter.Favourites => "Broadcasts you have saved for easy return.",
        LibraryListeningFilter.Completed => "Broadcasts you have listened to in full.",
        LibraryListeningFilter.Unplayed => "Broadcasts you have not started yet.",
        LibraryListeningFilter.NeedsAttention => "Library records that still need review or repair.",
        LibraryListeningFilter.RecentlyAdded => "The newest broadcasts discovered by Library scans.",
        LibraryListeningFilter.OnThisDay => "Broadcasts from today's date in earlier years.",
        _ => IsCollectionFiltered
            ? IsCatalogueCollection
                ? $"Browse every imported {SelectedCollectionName} interview or segment, including items whose original file has no broadcast date."
                : $"Browse {SelectedCollectionName} by year and month, or switch to a complete list."
            : "Browse every canonical broadcast in the archive."
    };
    public LibraryViewMode ViewMode
    {
        get => _viewMode;
        private set
        {
            if (!SetProperty(ref _viewMode, value)) return;
            RaiseViewState();
        }
    }
    public bool IsListView => ViewMode == LibraryViewMode.List;
    public bool IsGridView => ViewMode == LibraryViewMode.Grid;
    public bool IsCatalogueCollection => IsCollectionFiltered &&
        KnownShowCatalog.SupportsUndatedCatalogueItems(SelectedCollectionName);
    public bool CanUseGridView => SelectedFilter.Filter != LibraryListeningFilter.Favourites &&
        !IsCatalogueCollection;
    public bool IsArchivePeriodView => IsGridView && !_archiveMonth.HasValue;
    public bool IsArchiveBroadcastList => IsGridView && _archiveMonth.HasValue;
    public bool IsBroadcastListVisible => IsListView || IsArchiveBroadcastList;
    public bool IsDetailsPanelVisible => IsListView || IsArchiveBroadcastList;
    public string ArchiveHeading => !_archiveYear.HasValue
        ? "Browse by year"
        : !_archiveMonth.HasValue
            ? $"Months in {_archiveYear.Value}"
            : $"Broadcasts in {ArchiveMonthText} {_archiveYear.Value}";
    public string ArchiveDescription => !_archiveYear.HasValue
        ? "Choose a year to explore the archive without scrolling through thousands of broadcasts."
        : !_archiveMonth.HasValue
            ? "Choose a month to see its broadcasts as a simple list."
            : $"Browse every dated broadcast from {ArchiveMonthText} {_archiveYear.Value}.";
    public string ArchiveBreadcrumb => !_archiveYear.HasValue
        ? SelectedCollectionName
        : _archiveMonth.HasValue
            ? $"{SelectedCollectionName}  ›  {_archiveYear.Value}  ›  {ArchiveMonthText}"
            : $"{SelectedCollectionName}  ›  {_archiveYear.Value}";
    public string ArchiveRootText => "All";
    public string ArchiveYearText => _archiveYear?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
    public string ArchiveMonthText => _archiveMonth.HasValue
        ? CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(_archiveMonth.Value)
        : string.Empty;
    public bool HasArchiveYear => _archiveYear.HasValue;
    public bool HasArchiveMonth => _archiveMonth.HasValue;
    public bool CanGoArchiveBack => _archiveYear.HasValue || _archiveMonth.HasValue;
    public bool CanGoArchiveRoot => _archiveYear.HasValue;
    public bool CanGoArchiveYear => _archiveYear.HasValue && _archiveMonth.HasValue;
    public LibraryFilterOptionViewModel SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (!SetProperty(ref _selectedFilter, value)) return;
            RaisePropertyChanged(nameof(PageTitle));
            RaisePropertyChanged(nameof(PageDescription));
            RaisePropertyChanged(nameof(CanUseGridView));
            if (!CanUseGridView && ViewMode != LibraryViewMode.List)
            {
                ViewMode = LibraryViewMode.List;
                ResetArchive();
            }
            OnQueryChanged();
        }
    }
    public bool HideCompleted
    {
        get => _hideCompleted;
        set
        {
            if (!SetProperty(ref _hideCompleted, value)) return;
            RaisePropertyChanged(nameof(HasActiveFilters));
            if (_suppressAutomaticRefresh) return;
            _queryVersion++;
            if (IsGridView)
                _ = LoadArchivePeriodsAsync();
            else if (_isLoaded)
            {
                _isLoaded = false;
                _ = LoadAsync(force: true);
            }
        }
    }
    public bool HasActiveFilters => !string.IsNullOrWhiteSpace(SearchText) || SelectedFilter.Filter != LibraryListeningFilter.All || HideCompleted;
    public BroadcastRowViewModel? SelectedBroadcast
    {
        get => _selectedBroadcast;
        set
        {
            if (!SetProperty(ref _selectedBroadcast, value)) return;
            RaisePropertyChanged(nameof(HasSelection));
            _ = LoadSelectedDetailsAsync(value);
        }
    }
    public bool HasBroadcasts => Broadcasts.Count > 0;
    public bool HasArchivePeriods => ArchivePeriods.Count > 0;
    public bool HasSelection => SelectedBroadcast is not null;

    public void SetOpenBroadcastInfoHandler(Func<long, Task> handler)
        => _openBroadcastInfo = handler ?? throw new ArgumentNullException(nameof(handler));

    public async Task<IReadOnlyList<LibraryCollectionSummary>> LoadCollectionsAsync(bool force = false)
    {
        if (_collectionsLoaded && !force && ConnectedViewRefreshPolicy.IsFresh(_collectionsLoadedAt))
            return Collections.ToArray();
        var overview = await _library.GetOverviewAsync().ConfigureAwait(true);
        Collections.Clear();
        foreach (var collection in overview.Collections)
            Collections.Add(collection);
        _collectionsLoaded = true;
        _collectionsLoadedAt = DateTimeOffset.UtcNow;
        return Collections.ToArray();
    }

    public async Task SelectCollectionAsync(int? collectionId, string? collectionName, bool force = false)
    {
        var normalizedName = collectionId.HasValue && !string.IsNullOrWhiteSpace(collectionName)
            ? collectionName.Trim()
            : "All broadcasts";
        var changed = _selectedCollectionId != collectionId ||
            !string.Equals(SelectedCollectionName, normalizedName, StringComparison.CurrentCulture);
        _selectedCollectionId = collectionId;
        SelectedCollectionName = normalizedName;
        await RelatedWiki.LoadAsync(normalizedName).ConfigureAwait(true);
        if (changed)
        {
            _queryVersion++;
            ResetArchive();
        }
        RaisePropertyChanged(nameof(SelectedCollectionId));
        RaisePropertyChanged(nameof(IsCollectionFiltered));
        RaisePropertyChanged(nameof(PageTitle));
        RaisePropertyChanged(nameof(PageDescription));
        RaisePropertyChanged(nameof(IsCatalogueCollection));
        RaisePropertyChanged(nameof(CanUseGridView));
        if (IsCatalogueCollection && ViewMode != LibraryViewMode.List)
        {
            ViewMode = LibraryViewMode.List;
            ResetArchive();
        }
        RaiseViewState();
        await LoadAsync(force: force || changed || !_isLoaded).ConfigureAwait(true);
    }

    public async Task SetListeningFilterAsync(LibraryListeningFilter filter)
    {
        _suppressAutomaticRefresh = true;
        try
        {
            SelectedFilter = Filters.FirstOrDefault(x => x.Filter == filter) ?? Filters[0];
            SearchText = string.Empty;
        }
        finally { _suppressAutomaticRefresh = false; }
        ViewMode = LibraryViewMode.List;
        ResetArchive();
        _queryVersion++;
        _isLoaded = false;
        await LoadAsync(force: true).ConfigureAwait(true);
    }

    public async Task LoadAsync(bool force = false)
    {
        if (IsArchivePeriodView)
        {
            await LoadArchivePeriodsAsync().ConfigureAwait(true);
            return;
        }

        var requestVersion = _queryVersion;
        await _loadGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_isLoaded && !force && ConnectedViewRefreshPolicy.IsFresh(_loadedAt)) return;
            IsBusy = true;
            StatusText = IsArchiveBroadcastList
                ? $"Loading {ArchiveBreadcrumb}…"
                : $"Searching {SelectedCollectionName}…";
            var selectedKey = SelectedBroadcast?.Source.CanonicalKey;
            var result = await _library.BrowseAsync(new LibraryBrowseRequest(
                SearchText: SearchText,
                CollectionId: _selectedCollectionId,
                Filter: SelectedFilter.Filter,
                Year: IsArchiveBroadcastList ? _archiveYear : null,
                Month: IsArchiveBroadcastList ? _archiveMonth : null,
                Limit: 10000,
                NewestFirst: true)).ConfigureAwait(true);
            if (requestVersion != _queryVersion) return;

            var visibleBroadcasts = HideCompleted && SelectedFilter.Filter != LibraryListeningFilter.Completed
                ? result.Broadcasts.Where(item => !item.Completed).ToArray()
                : result.Broadcasts.ToArray();
            var hiddenCompletedCount = result.Broadcasts.Count - visibleBroadcasts.Length;

            Broadcasts.Clear();
            foreach (var item in visibleBroadcasts)
                Broadcasts.Add(CreateRow(item));
            SelectedBroadcast = Broadcasts.FirstOrDefault(x => string.Equals(x.Source.CanonicalKey, selectedKey, StringComparison.OrdinalIgnoreCase))
                ?? Broadcasts.FirstOrDefault();
            RaisePropertyChanged(nameof(HasBroadcasts));
            RaisePropertyChanged(nameof(HasSelection));
            LibraryModelText = result.UsesCanonicalLibrary ? "Canonical Library Truth" : "Legacy compatibility view";
            var scopeSuffix = IsArchiveBroadcastList
                ? $" in {CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(_archiveMonth!.Value)} {_archiveYear}"
                : _selectedCollectionId.HasValue ? $" in {SelectedCollectionName}" : string.Empty;
            StatusText = hiddenCompletedCount > 0
                ? $"Showing {Broadcasts.Count:N0} broadcasts{scopeSuffix}; {hiddenCompletedCount:N0} completed hidden."
                : result.TotalMatching > Broadcasts.Count
                    ? $"Showing {Broadcasts.Count:N0} of {result.TotalMatching:N0} broadcasts{scopeSuffix}."
                    : $"{result.TotalMatching:N0} broadcast{(result.TotalMatching == 1 ? string.Empty : "s")}{scopeSuffix}.";
            _isLoaded = true;
            _loadedAt = DateTimeOffset.UtcNow;
            SyncPlaybackState();
        }
        catch (Exception ex) { SetError(ex); }
        finally
        {
            IsBusy = false;
            _loadGate.Release();
        }
    }

    private async Task ClearSearchAsync()
    {
        _suppressAutomaticRefresh = true;
        try
        {
            SearchText = string.Empty;
            SelectedFilter = Filters[0];
            HideCompleted = false;
        }
        finally { _suppressAutomaticRefresh = false; }
        await LoadAsync(force: true).ConfigureAwait(true);
    }

    private async Task ShowListAsync()
    {
        ViewMode = LibraryViewMode.List;
        ResetArchive();
        _isLoaded = false;
        await LoadAsync(force: true).ConfigureAwait(true);
    }

    private async Task ShowGridAsync()
    {
        if (!CanUseGridView) return;
        ViewMode = LibraryViewMode.Grid;
        ResetArchive();
        await LoadArchivePeriodsAsync().ConfigureAwait(true);
    }

    private async Task LoadArchivePeriodsAsync()
    {
        IsBusy = true;
        try
        {
            StatusText = !_archiveYear.HasValue
                ? $"Loading years in {SelectedCollectionName}…"
                : $"Loading months in {_archiveYear.Value}…";
            var periods = await _library.GetArchivePeriodsAsync(
                _selectedCollectionId,
                _archiveYear,
                HideCompleted && SelectedFilter.Filter != LibraryListeningFilter.Completed).ConfigureAwait(true);
            ArchivePeriods.Clear();
            foreach (var period in periods)
                ArchivePeriods.Add(new LibraryArchivePeriodViewModel(period, OpenArchivePeriodAsync));
            RaisePropertyChanged(nameof(HasArchivePeriods));
            RaiseViewState();
            var hiddenSuffix = HideCompleted && SelectedFilter.Filter != LibraryListeningFilter.Completed
                ? " · completed broadcasts hidden"
                : string.Empty;
            StatusText = periods.Count == 0
                ? $"No dated broadcasts are available in this view{hiddenSuffix}."
                : !_archiveYear.HasValue
                    ? $"{periods.Count:N0} years in {SelectedCollectionName}{hiddenSuffix}."
                    : $"{periods.Count:N0} months in {_archiveYear.Value}{hiddenSuffix}.";
        }
        catch (Exception ex) { SetError(ex); }
        finally { IsBusy = false; }
    }

    private async Task OpenArchivePeriodAsync(LibraryArchivePeriodViewModel period)
    {
        if (!_archiveYear.HasValue)
        {
            _archiveYear = period.Value;
            _archiveMonth = null;
            RaiseViewState();
            await LoadArchivePeriodsAsync().ConfigureAwait(true);
            return;
        }

        _archiveMonth = period.Value;
        _isLoaded = false;
        RaiseViewState();
        await LoadAsync(force: true).ConfigureAwait(true);
    }

    private async Task ArchiveBackAsync()
    {
        if (_archiveMonth.HasValue)
        {
            _archiveMonth = null;
            _isLoaded = false;
            RaiseViewState();
            await LoadArchivePeriodsAsync().ConfigureAwait(true);
            return;
        }
        if (_archiveYear.HasValue)
        {
            _archiveYear = null;
            RaiseViewState();
            await LoadArchivePeriodsAsync().ConfigureAwait(true);
        }
    }

    private async Task ArchiveRootAsync()
    {
        _archiveYear = null;
        _archiveMonth = null;
        RaiseViewState();
        await LoadArchivePeriodsAsync().ConfigureAwait(true);
    }

    private async Task ArchiveYearAsync()
    {
        if (!_archiveYear.HasValue) return;
        _archiveMonth = null;
        _isLoaded = false;
        RaiseViewState();
        await LoadArchivePeriodsAsync().ConfigureAwait(true);
    }

    private void ResetArchive()
    {
        _archiveYear = null;
        _archiveMonth = null;
        ArchivePeriods.Clear();
        RaiseViewState();
    }

    private void OnQueryChanged()
    {
        _queryVersion++;
        RaisePropertyChanged(nameof(HasActiveFilters));
        if (_suppressAutomaticRefresh || !_isLoaded) return;

        if (IsGridView)
        {
            ViewMode = LibraryViewMode.List;
            ResetArchive();
        }

        _queryDebounce?.Cancel();
        _queryDebounce?.Dispose();
        _queryDebounce = new CancellationTokenSource();
        var token = _queryDebounce.Token;
        _ = RefreshAfterPauseAsync(token);
    }

    private async Task RefreshAfterPauseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(220, cancellationToken).ConfigureAwait(true);
            await LoadAsync(force: true).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
    }

    private BroadcastRowViewModel CreateRow(LibraryBroadcastSummary source)
    {
        var row = new BroadcastRowViewModel(
            source,
            _playback.LoadAndPlayAsync,
            ToggleFavouriteAsync,
            AddToQueueAsync,
            PlayOrToggleAsync,
            OpenFullInfoAsync,
            TranscribeAsync,
            SetPlayedAsync,
            DownloadAsync,
            showCollectionText: !IsCollectionFiltered);
        row.SetDownloaded(_downloads.IsDownloaded(source.RepresentativeEpisodeId));
        SetPlaybackState(row);
        return row;
    }

    private async Task DownloadAsync(BroadcastRowViewModel row)
    {
        await _downloads.DownloadBroadcastAsync(row.Source.RepresentativeEpisodeId, row.Title).ConfigureAwait(true);
        row.SetDownloaded(_downloads.IsDownloaded(row.Source.RepresentativeEpisodeId));
        StatusText = row.IsDownloaded
            ? $"{row.Title} is stored on this PC."
            : _downloads.StatusText;
    }

    private void SyncDownloadState()
    {
        foreach (var row in Broadcasts)
            row.SetDownloaded(_downloads.IsDownloaded(row.Source.RepresentativeEpisodeId));
    }

    private async Task LoadSelectedDetailsAsync(BroadcastRowViewModel? row)
    {
        var generation = ++_detailsGeneration;
        if (row is null) return;
        row.SetDetailsLoading(true);
        try
        {
            var details = await _details.GetAsync(row.Source.RepresentativeEpisodeId).ConfigureAwait(true);
            if (generation != _detailsGeneration || !ReferenceEquals(SelectedBroadcast, row)) return;
            row.SetDetails(details);
            if (details is not null)
                await RelatedWiki.LoadAsync(details.CollectionName, details.Hosts, details.Guests, details.Callers,
                    details.MentionedPeople, string.Join(",", details.Topics), details.Event).ConfigureAwait(true);
        }
        catch
        {
            if (generation == _detailsGeneration && ReferenceEquals(SelectedBroadcast, row))
                row.SetDetails(null);
        }
    }

    private Task OpenFullInfoAsync(BroadcastRowViewModel row)
        => _openBroadcastInfo?.Invoke(row.Source.RepresentativeEpisodeId) ?? Task.CompletedTask;

    private async Task TranscribeAsync(BroadcastRowViewModel row)
    {
        try
        {
            var settings = (_transcription.Engine as WhisperCppTranscriptionEngine)?.GetSettings() ?? new WhisperCppEngineSettings();
            await _transcription.QueueAsync(row.Source.RepresentativeEpisodeId, new TranscriptionJobOptions(
                settings.DefaultLanguage,
                settings.ModelId,
                EnableSpeakerDiarization: settings.EnableMultiSpeakerDiarization,
                UseVoiceActivityDetection: settings.UseVoiceActivityDetection)).ConfigureAwait(true);
            StatusText = $"Transcription queued for {row.Title}.";
        }
        catch (Exception exception)
        {
            StatusText = $"Could not start transcription: {exception.Message}";
        }
    }

    private async Task PlayOrToggleAsync(BroadcastRowViewModel row)
    {
        if (_playback.IsLoaded && _playback.CurrentBroadcastId == row.Source.RepresentativeEpisodeId)
        {
            _playback.Toggle();
            SyncPlaybackState();
            return;
        }

        await _playback.LoadAndPlayAsync(row).ConfigureAwait(true);
        SyncPlaybackState();
    }

    private async Task ToggleFavouriteAsync(BroadcastRowViewModel row)
    {
        var next = !row.IsFavourite;
        await _actions.SetFavouriteAsync(row.Source.RepresentativeEpisodeId, next).ConfigureAwait(true);
        row.SetFavourite(next);
        _playback.SetFavouriteStateFromExternal(row.Source.RepresentativeEpisodeId, next);
        StatusText = next ? "Added to favourites." : "Removed from favourites.";
        if (SelectedFilter.Filter == LibraryListeningFilter.Favourites && !next)
            await LoadAsync(force: true).ConfigureAwait(true);
    }

    private async Task SetPlayedAsync(BroadcastRowViewModel row, bool played)
    {
        await _actions.SetPlayedAsync(row.Source.RepresentativeEpisodeId, played).ConfigureAwait(true);
        row.ApplyLiveProgress(
            played ? Math.Max(row.Source.DurationMs, row.Source.PositionMs) : 0,
            row.Source.DurationMs,
            played);
        StatusText = played ? "Marked as listened." : "Marked as unlistened.";
        if ((SelectedFilter.Filter == LibraryListeningFilter.Completed && !played) ||
            (SelectedFilter.Filter == LibraryListeningFilter.Unplayed && played) ||
            HideCompleted && played)
            await LoadAsync(force: true).ConfigureAwait(true);
    }

    private async Task AddToQueueAsync(BroadcastRowViewModel row, bool playNext)
    {
        await _queue.AddAsync(row.Source.RepresentativeEpisodeId, playNext).ConfigureAwait(true);
        StatusText = playNext ? "Added to play next." : "Added to queue.";
    }

    private void PlaybackOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackViewModel.LiveProgress)
            or nameof(PlaybackViewModel.CurrentBroadcastId)
            or nameof(PlaybackViewModel.IsPlaying)
            or nameof(PlaybackViewModel.IsLoaded))
        {
            SyncPlaybackState();
        }
    }

    private void SyncPlaybackState()
    {
        foreach (var row in Broadcasts)
            SetPlaybackState(row);

        var live = _playback.LiveProgress;
        if (live is null || live.RepresentativeEpisodeId <= 0) return;
        var inProgress = live.PositionMs > 0 && !live.Completed;
        var membershipChanged = _lastLiveEpisodeId != live.RepresentativeEpisodeId
                                || _lastLiveCompleted != live.Completed
                                || _lastLiveInProgress != inProgress;
        _lastLiveEpisodeId = live.RepresentativeEpisodeId;
        _lastLiveCompleted = live.Completed;
        _lastLiveInProgress = inProgress;
        if (membershipChanged)
            _ = ReconcileLiveMembershipAsync(live);
    }

    private void SetPlaybackState(BroadcastRowViewModel row)
    {
        var live = _playback.LiveProgress;
        var isCurrent = live?.Matches(row.Source) == true;
        row.SetPlaybackState(isCurrent, isCurrent && live!.IsPlaying);
        if (isCurrent)
            row.ApplyLiveProgress(live!.PositionMs, live.DurationMs, live.Completed);
    }

    private async Task ReconcileLiveMembershipAsync(PlaybackLiveProgress requested)
    {
        await _liveMembershipGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var live = _playback.LiveProgress;
            if (live is null || live.RepresentativeEpisodeId != requested.RepresentativeEpisodeId) return;

            if (IsArchivePeriodView)
            {
                await LoadArchivePeriodsAsync().ConfigureAwait(true);
                return;
            }

            var source = await _library.GetBroadcastAsync(live.RepresentativeEpisodeId).ConfigureAwait(true);
            live = _playback.LiveProgress;
            if (source is null || live is null || !live.Matches(source)) return;

            var duration = live.DurationMs > 0 ? live.DurationMs : source.DurationMs;
            var effective = source with
            {
                Completed = live.Completed,
                InProgress = live.PositionMs > 0 && !live.Completed,
                PositionMs = live.PositionMs,
                DurationMs = duration,
                LastPlayedAt = DateTimeOffset.UtcNow
            };
            var existing = Broadcasts.FirstOrDefault(row => live.Matches(row.Source));
            var shouldBeVisible = MatchesCurrentView(effective);

            if (!shouldBeVisible)
            {
                if (existing is not null)
                {
                    var wasSelected = ReferenceEquals(existing, SelectedBroadcast);
                    Broadcasts.Remove(existing);
                    if (wasSelected) SelectedBroadcast = Broadcasts.FirstOrDefault();
                    RaisePropertyChanged(nameof(HasBroadcasts));
                    RaisePropertyChanged(nameof(HasSelection));
                }
                return;
            }

            if (existing is null)
            {
                var row = CreateRow(effective);
                row.ApplyLiveProgress(live.PositionMs, duration, live.Completed);
                row.SetPlaybackState(true, live.IsPlaying);
                Broadcasts.Insert(FindInsertionIndex(effective), row);
                SelectedBroadcast ??= row;
                RaisePropertyChanged(nameof(HasBroadcasts));
                RaisePropertyChanged(nameof(HasSelection));
            }
            else
            {
                existing.ApplyLiveProgress(live.PositionMs, duration, live.Completed);
                existing.SetPlaybackState(true, live.IsPlaying);
            }
        }
        catch
        {
            // A normal navigation refresh still reconstructs the list from the
            // canonical store if a transient server/cache read fails here.
        }
        finally
        {
            _liveMembershipGate.Release();
        }
    }

    private bool MatchesCurrentView(LibraryBroadcastSummary item)
    {
        if (_selectedCollectionId.HasValue && item.CollectionId != _selectedCollectionId.Value) return false;
        if (IsArchiveBroadcastList && item.AirDate?.Year != _archiveYear) return false;
        if (IsArchiveBroadcastList && item.AirDate?.Month != _archiveMonth) return false;
        if (HideCompleted && SelectedFilter.Filter != LibraryListeningFilter.Completed && item.Completed) return false;

        var today = DateOnly.FromDateTime(DateTime.Today);
        var filterMatch = SelectedFilter.Filter switch
        {
            LibraryListeningFilter.ContinueListening => item.InProgress && !item.Completed,
            LibraryListeningFilter.Favourites => item.Favourite,
            LibraryListeningFilter.Completed => item.Completed,
            LibraryListeningFilter.Unplayed => !item.Completed && !item.InProgress,
            LibraryListeningFilter.NeedsAttention => item.NeedsAttention,
            LibraryListeningFilter.OnThisDay => item.AirDate.HasValue
                                                 && item.AirDate.Value.Month == today.Month
                                                 && item.AirDate.Value.Day == today.Day,
            _ => true
        };
        if (!filterMatch) return false;

        var search = SearchText.Trim();
        if (search.Length == 0) return true;
        return Contains(item.Title, search)
               || Contains(item.Description, search)
               || Contains(item.CollectionName, search)
               || Contains(item.BroadcastId, search)
               || Contains(item.BroadcastSlot, search)
               || (item.AirDate?.ToString("yyyy-MM-dd").Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private int FindInsertionIndex(LibraryBroadcastSummary item)
    {
        for (var index = 0; index < Broadcasts.Count; index++)
        {
            var comparison = SelectedFilter.Filter == LibraryListeningFilter.RecentlyAdded
                ? CompareDescending(item.DateAdded, Broadcasts[index].Source.DateAdded)
                : CompareDescending(item.AirDate ?? DateOnly.MinValue, Broadcasts[index].Source.AirDate ?? DateOnly.MinValue);
            if (comparison < 0) return index;
            if (comparison == 0 && item.RepresentativeEpisodeId > Broadcasts[index].Source.RepresentativeEpisodeId)
                return index;
        }
        return Broadcasts.Count;
    }

    private static int CompareDescending<T>(T left, T right) where T : IComparable<T>
        => right.CompareTo(left);

    private static bool Contains(string? value, string search)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    private void PlaybackOnFavouriteChanged(object? sender, PlaybackFavouriteChangedEventArgs e)
    {
        foreach (var row in Broadcasts.Where(x => x.Source.RepresentativeEpisodeId == e.RepresentativeEpisodeId))
            row.SetFavourite(e.Favourite);
        if (SelectedFilter.Filter == LibraryListeningFilter.Favourites && !e.Favourite)
        {
            _isLoaded = false;
            _ = LoadAsync(force: true);
        }
    }

    private void RaiseViewState()
    {
        RaisePropertyChanged(nameof(IsListView));
        RaisePropertyChanged(nameof(IsGridView));
        RaisePropertyChanged(nameof(IsArchivePeriodView));
        RaisePropertyChanged(nameof(IsArchiveBroadcastList));
        RaisePropertyChanged(nameof(IsBroadcastListVisible));
        RaisePropertyChanged(nameof(IsDetailsPanelVisible));
        RaisePropertyChanged(nameof(ArchiveHeading));
        RaisePropertyChanged(nameof(ArchiveDescription));
        RaisePropertyChanged(nameof(ArchiveBreadcrumb));
        RaisePropertyChanged(nameof(ArchiveRootText));
        RaisePropertyChanged(nameof(ArchiveYearText));
        RaisePropertyChanged(nameof(ArchiveMonthText));
        RaisePropertyChanged(nameof(HasArchiveYear));
        RaisePropertyChanged(nameof(HasArchiveMonth));
        RaisePropertyChanged(nameof(CanGoArchiveBack));
        RaisePropertyChanged(nameof(CanGoArchiveRoot));
        RaisePropertyChanged(nameof(CanGoArchiveYear));
        RaiseCommandState();
    }

    private void SetError(Exception exception) => StatusText = $"Library could not load: {exception.Message}";
    private void RaiseCommandState()
    {
        ((AsyncCommand)SearchCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)ClearSearchCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)ShowListCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)ShowGridCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)ArchiveBackCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)ArchiveRootCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)ArchiveYearCommand).RaiseCanExecuteChanged();
    }
}
