using System.Collections.ObjectModel;
using System.Windows.Input;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;
using TheRadioVault.Transcription.Services;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class SearchViewModel : ObservableObject
{
    private readonly ILibraryBrowseService _library;
    private readonly ILibraryActionService _actions;
    private readonly PlaybackViewModel _playback;
    private readonly QueueViewModel _queue;
    private readonly ITranscriptionCoordinator _transcription;
    private CancellationTokenSource? _debounce;
    private bool _isBusy;
    private bool _isLoaded;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private string _searchText = string.Empty;
    private string _statusText = "Search across every show in your archive.";
    private Func<int?, LibraryListeningFilter, Task>? _openLibrary;
    private SearchShowFacetViewModel? _selectedShow;
    private SearchYearFacetViewModel? _selectedYear;
    private LibrarySearchScope _searchScope = LibrarySearchScope.All;
    private LibraryListeningFilter _listeningFilter = LibraryListeningFilter.All;
    private bool _hasTranscriptOnly;
    private bool _suppressSuggestionOnce;

    public SearchViewModel(
        ILibraryBrowseService library,
        ILibraryActionService actions,
        PlaybackViewModel playback,
        QueueViewModel queue,
        ITranscriptionCoordinator transcription,
        IWikiService? wiki = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _transcription = transcription ?? throw new ArgumentNullException(nameof(transcription));
        RelatedWiki = new RelatedWikiPagesViewModel(wiki);
        SearchCommand = new AsyncCommand(SearchAsync, () => !IsBusy, SetError);
        ClearCommand = new AsyncCommand(ClearAsync, () => !IsBusy, SetError);
        ScopeFilters.Add(new SearchFacetChipViewModel("Everything", LibrarySearchScope.All, SelectScope));
        ScopeFilters.Add(new SearchFacetChipViewModel("Titles & summaries", LibrarySearchScope.TitlesAndSummaries, SelectScope));
        ScopeFilters.Add(new SearchFacetChipViewModel("People", LibrarySearchScope.People, SelectScope));
        ScopeFilters.Add(new SearchFacetChipViewModel("Topics", LibrarySearchScope.Topics, SelectScope));
        ScopeFilters.Add(new SearchFacetChipViewModel("Research", LibrarySearchScope.Research, SelectScope));
        ScopeFilters.Add(new SearchFacetChipViewModel("Transcripts", LibrarySearchScope.Transcripts, SelectScope));
        ScopeFilters[0].IsSelected = true;
        StatusFilters.Add(new SearchStatusChipViewModel("Any status", LibraryListeningFilter.All, SelectStatus));
        StatusFilters.Add(new SearchStatusChipViewModel("Unplayed", LibraryListeningFilter.Unplayed, SelectStatus));
        StatusFilters.Add(new SearchStatusChipViewModel("In progress", LibraryListeningFilter.ContinueListening, SelectStatus));
        StatusFilters.Add(new SearchStatusChipViewModel("Completed", LibraryListeningFilter.Completed, SelectStatus));
        StatusFilters.Add(new SearchStatusChipViewModel("Favourites", LibraryListeningFilter.Favourites, SelectStatus));
        StatusFilters[0].IsSelected = true;
    }

    public ObservableCollection<BroadcastRowViewModel> Results { get; } = new();
    public ObservableCollection<SearchCollectionCardViewModel> Shows { get; } = new();
    public ObservableCollection<SearchCollectionCardViewModel> QuickCollections { get; } = new();
    public ObservableCollection<SearchFacetChipViewModel> ScopeFilters { get; } = new();
    public ObservableCollection<SearchStatusChipViewModel> StatusFilters { get; } = new();
    public ObservableCollection<SearchShowFacetViewModel> ShowFilters { get; } = new();
    public ObservableCollection<SearchYearFacetViewModel> YearFilters { get; } = new();
    public ObservableCollection<SearchSuggestionViewModel> Suggestions { get; } = new();
    public RelatedWikiPagesViewModel RelatedWiki { get; }
    public ICommand SearchCommand { get; }
    public ICommand ClearCommand { get; }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommands(); } }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            RaisePropertyChanged(nameof(HasSearchText));
            RaisePropertyChanged(nameof(HasSuggestions));
            RaiseFilterState();
            ScheduleSearch();
        }
    }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);
    public SearchShowFacetViewModel? SelectedShow
    {
        get => _selectedShow;
        set { if (SetProperty(ref _selectedShow, value)) OnFilterChanged(); }
    }
    public SearchYearFacetViewModel? SelectedYear
    {
        get => _selectedYear;
        set { if (SetProperty(ref _selectedYear, value)) OnFilterChanged(); }
    }
    public bool HasTranscriptOnly
    {
        get => _hasTranscriptOnly;
        set { if (SetProperty(ref _hasTranscriptOnly, value)) OnFilterChanged(); }
    }
    public bool HasActiveFilters => ListeningFilter != LibraryListeningFilter.All
        || SelectedShow?.CollectionId is not null
        || SelectedYear?.Year is not null
        || HasTranscriptOnly
        || (HasSearchText && SearchScope != LibrarySearchScope.All);
    public bool HasActiveSearch => HasSearchText || HasActiveFilters;
    public bool ShowDiscovery => !HasActiveSearch;
    public bool HasSuggestions => Suggestions.Count > 0 && HasSearchText;
    public LibrarySearchScope SearchScope => _searchScope;
    public LibraryListeningFilter ListeningFilter => _listeningFilter;
    public bool HasResults => Results.Count > 0;
    public bool HasNoResults => HasActiveSearch && !IsBusy && Results.Count == 0;
    public bool HasShows => Shows.Count > 0;

    public void SetOpenLibraryHandler(Func<int?, LibraryListeningFilter, Task> handler)
        => _openLibrary = handler ?? throw new ArgumentNullException(nameof(handler));

    public async Task LoadAsync(bool force = false)
    {
        if (_isLoaded && !force && ConnectedViewRefreshPolicy.IsFresh(_loadedAt)) return;
        IsBusy = true;
        try
        {
            var selectedCollectionId = _selectedShow?.CollectionId;
            var selectedYearValue = _selectedYear?.Year;
            var overview = await _library.GetOverviewAsync().ConfigureAwait(true);
            var facets = await _library.GetSearchFacetsAsync().ConfigureAwait(true);
            ShowFilters.Clear();
            ShowFilters.Add(new SearchShowFacetViewModel(null, "All shows"));
            foreach (var collection in overview.Collections.OrderBy(x => x.CollectionName, StringComparer.CurrentCultureIgnoreCase))
                ShowFilters.Add(new SearchShowFacetViewModel(collection.CollectionId, collection.CollectionName));
            _selectedShow = ShowFilters.FirstOrDefault(x => x.CollectionId == selectedCollectionId) ?? ShowFilters[0];
            RaisePropertyChanged(nameof(SelectedShow));
            YearFilters.Clear();
            YearFilters.Add(new SearchYearFacetViewModel(null, "All years"));
            foreach (var year in facets.Years)
                YearFilters.Add(new SearchYearFacetViewModel(year, year.ToString()));
            _selectedYear = YearFilters.FirstOrDefault(x => x.Year == selectedYearValue) ?? YearFilters[0];
            RaisePropertyChanged(nameof(SelectedYear));
            Shows.Clear();
            foreach (var collection in overview.Collections.OrderBy(x => x.CollectionName, StringComparer.CurrentCultureIgnoreCase))
            {
                var captured = collection;
                Shows.Add(new SearchCollectionCardViewModel(
                    captured.CollectionName,
                    "Browse this show by year, month, or list.",
                    "▤",
                    () => OpenLibraryAsync(captured.CollectionId, LibraryListeningFilter.All),
                    captured.BroadcastCount));
            }

            QuickCollections.Clear();
            QuickCollections.Add(new SearchCollectionCardViewModel(
                "Continue listening", "Unfinished broadcasts, ordered by the last time you listened.", "▶",
                () => OpenLibraryAsync(null, LibraryListeningFilter.ContinueListening), overview.InProgressBroadcasts));
            QuickCollections.Add(new SearchCollectionCardViewModel(
                "Favourites", "Broadcasts you have saved for easy return.", "♥",
                () => OpenLibraryAsync(null, LibraryListeningFilter.Favourites), overview.FavouriteBroadcasts));
            QuickCollections.Add(new SearchCollectionCardViewModel(
                "On this day", "Broadcasts from today's date in earlier years.", "◷",
                () => OpenLibraryAsync(null, LibraryListeningFilter.OnThisDay), overview.OnThisDay.Count));
            QuickCollections.Add(new SearchCollectionCardViewModel(
                "Recently added", "The newest items discovered by your library scans.", "+",
                () => OpenLibraryAsync(null, LibraryListeningFilter.RecentlyAdded), overview.RecentBroadcasts.Count));
            QuickCollections.Add(new SearchCollectionCardViewModel(
                "Unplayed", "Something in your archive that you have not started yet.", "○",
                () => OpenLibraryAsync(null, LibraryListeningFilter.Unplayed)));

            RaisePropertyChanged(nameof(HasShows));
            StatusText = "Search across broadcasts, or start with a show or collection.";
            _isLoaded = true;
            _loadedAt = DateTimeOffset.UtcNow;
        }
        finally { IsBusy = false; }
    }

    private void ScheduleSearch()
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = new CancellationTokenSource();
        var token = _debounce.Token;
        _ = SearchAfterPauseAsync(token);
    }

    private async Task SearchAfterPauseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(220, cancellationToken).ConfigureAwait(true);
            await UpdateSuggestionsAsync(cancellationToken).ConfigureAwait(true);
            await SearchAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
    }

    private async Task SearchAsync()
    {
        var query = SearchText.Trim();
        if (query.Length == 0 && !HasActiveFilters)
        {
            Results.Clear();
            RaiseResultState();
            StatusText = "Search across broadcasts, or start with a show or collection.";
            return;
        }

        IsBusy = true;
        StatusText = query.Length > 0 ? $"Searching for “{query}”…" : "Applying search filters…";
        try
        {
            var result = await _library.BrowseAsync(new LibraryBrowseRequest(
                SearchText: query.Length == 0 ? null : query,
                CollectionId: SelectedShow?.CollectionId,
                Filter: ListeningFilter,
                Year: SelectedYear?.Year,
                Limit: 250,
                NewestFirst: true,
                SearchScope: SearchScope,
                HasTranscript: HasTranscriptOnly)).ConfigureAwait(true);
            Results.Clear();
            foreach (var item in result.Broadcasts)
                Results.Add(new BroadcastRowViewModel(item, _playback.LoadAndPlayAsync, ToggleFavouriteAsync, AddToQueueAsync,
                    transcribe: TranscribeAsync, setPlayed: SetPlayedAsync));
            RaiseResultState();
            await RelatedWiki.LoadAsync(query, SelectedShow?.Title).ConfigureAwait(true);
            StatusText = result.TotalMatching == 1
                ? "1 matching broadcast."
                : $"{result.TotalMatching:N0} matching broadcasts." +
                  (result.TotalMatching > result.Broadcasts.Count ? $" Showing the first {result.Broadcasts.Count:N0}." : string.Empty);
        }
        finally { IsBusy = false; }
    }

    private async Task ClearAsync()
    {
        _debounce?.Cancel();
        _searchText = string.Empty;
        RaisePropertyChanged(nameof(SearchText));
        RaisePropertyChanged(nameof(HasSearchText));
        _searchScope = LibrarySearchScope.All;
        _listeningFilter = LibraryListeningFilter.All;
        foreach (var chip in ScopeFilters) chip.IsSelected = chip.Scope == LibrarySearchScope.All;
        foreach (var chip in StatusFilters) chip.IsSelected = chip.Filter == LibraryListeningFilter.All;
        _selectedShow = ShowFilters.FirstOrDefault();
        _selectedYear = YearFilters.FirstOrDefault();
        _hasTranscriptOnly = false;
        RaisePropertyChanged(nameof(SelectedShow));
        RaisePropertyChanged(nameof(SelectedYear));
        RaisePropertyChanged(nameof(HasTranscriptOnly));
        RaiseFilterState();
        Suggestions.Clear();
        RaisePropertyChanged(nameof(HasSuggestions));
        Results.Clear();
        RaiseResultState();
        StatusText = "Search across broadcasts, or start with a show or collection.";
        await Task.CompletedTask;
    }

    private async Task UpdateSuggestionsAsync(CancellationToken cancellationToken)
    {
        if (_suppressSuggestionOnce)
        {
            _suppressSuggestionOnce = false;
            return;
        }
        var query = SearchText.Trim();
        var suggestions = query.Length < 2
            ? Array.Empty<LibrarySearchSuggestion>()
            : await _library.GetSearchSuggestionsAsync(query, 10, cancellationToken).ConfigureAwait(true);
        Suggestions.Clear();
        foreach (var suggestion in suggestions)
            Suggestions.Add(new SearchSuggestionViewModel(suggestion, SelectSuggestion));
        RaisePropertyChanged(nameof(HasSuggestions));
    }

    private void SelectSuggestion(string value)
    {
        _suppressSuggestionOnce = true;
        SearchText = value;
        Suggestions.Clear();
        RaisePropertyChanged(nameof(HasSuggestions));
    }

    private void SelectScope(SearchFacetChipViewModel selected)
    {
        _searchScope = selected.Scope;
        foreach (var chip in ScopeFilters) chip.IsSelected = ReferenceEquals(chip, selected);
        RaisePropertyChanged(nameof(SearchScope));
        OnFilterChanged();
    }

    private void SelectStatus(SearchStatusChipViewModel selected)
    {
        _listeningFilter = selected.Filter;
        foreach (var chip in StatusFilters) chip.IsSelected = ReferenceEquals(chip, selected);
        RaisePropertyChanged(nameof(ListeningFilter));
        OnFilterChanged();
    }

    private void OnFilterChanged()
    {
        RaiseFilterState();
        ScheduleSearch();
    }

    private void RaiseFilterState()
    {
        RaisePropertyChanged(nameof(HasActiveFilters));
        RaisePropertyChanged(nameof(HasActiveSearch));
        RaisePropertyChanged(nameof(ShowDiscovery));
        RaisePropertyChanged(nameof(HasNoResults));
    }

    private Task OpenLibraryAsync(int? collectionId, LibraryListeningFilter filter)
        => _openLibrary?.Invoke(collectionId, filter) ?? Task.CompletedTask;

    private async Task ToggleFavouriteAsync(BroadcastRowViewModel row)
    {
        var next = !row.IsFavourite;
        await _actions.SetFavouriteAsync(row.Source.RepresentativeEpisodeId, next).ConfigureAwait(true);
        row.SetFavourite(next);
        _playback.SetFavouriteStateFromExternal(row.Source.RepresentativeEpisodeId, next);
    }

    private Task AddToQueueAsync(BroadcastRowViewModel row, bool playNext)
        => _queue.AddAsync(row.Source.RepresentativeEpisodeId, playNext);

    private async Task SetPlayedAsync(BroadcastRowViewModel row, bool played)
    {
        await _actions.SetPlayedAsync(row.Source.RepresentativeEpisodeId, played).ConfigureAwait(true);
        row.ApplyLiveProgress(
            played ? Math.Max(row.Source.DurationMs, row.Source.PositionMs) : 0,
            row.Source.DurationMs,
            played);
        StatusText = played ? "Marked as listened." : "Marked as unlistened.";
    }

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

    private void RaiseResultState()
    {
        RaisePropertyChanged(nameof(HasResults));
        RaisePropertyChanged(nameof(HasNoResults));
        RaisePropertyChanged(nameof(HasActiveSearch));
        RaisePropertyChanged(nameof(ShowDiscovery));
    }

    private void RaiseCommands()
    {
        ((AsyncCommand)SearchCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)ClearCommand).RaiseCanExecuteChanged();
    }

    private void SetError(Exception exception) => StatusText = $"Search could not complete: {exception.Message}";
}
