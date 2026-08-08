using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class DashboardViewModel : ObservableObject
{
    private readonly ILibraryBrowseService _library;
    private readonly ILibraryActionService _actions;
    private readonly IBroadcastDetailsService _details;
    private readonly PlaybackViewModel _playback;
    private readonly QueueViewModel _queue;
    private readonly IWikiService? _wiki;
    private bool _isBusy;
    private bool _isLoaded;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private string _statusText = "Loading the Library…";
    private int _totalBroadcasts;
    private int _completedBroadcasts;
    private int _inProgressBroadcasts;
    private int _favouriteBroadcasts;
    private int _needsAttentionBroadcasts;
    private string _libraryModelText = "Checking Library…";
    private BroadcastRowViewModel? _featuredContinue;
    private BroadcastRowViewModel? _currentOnThisDay;
    private int _onThisDayIndex;
    private readonly SemaphoreSlim _liveRowGate = new(1, 1);
    private readonly Dictionary<long, (bool Completed, bool InProgress)> _liveListeningStates = new();

    public DashboardViewModel(
        ILibraryBrowseService library,
        ILibraryActionService actions,
        IBroadcastDetailsService details,
        PlaybackViewModel playback,
        QueueViewModel queue,
        IWikiService? wiki = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _details = details ?? throw new ArgumentNullException(nameof(details));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _wiki = wiki;
        RelatedWiki = new RelatedWikiPagesViewModel(wiki);
        RefreshCommand = new AsyncCommand(() => LoadAsync(force: true), () => !IsBusy, SetError);
        SurpriseMeCommand = new AsyncCommand(SurpriseMeAsync, () => !IsBusy, SetError);
        PreviousOnThisDayCommand = new DelegateCommand(() => MoveOnThisDay(-1), () => HasOnThisDay);
        NextOnThisDayCommand = new DelegateCommand(() => MoveOnThisDay(1), () => HasOnThisDay);
        _playback.FavouriteChanged += PlaybackOnFavouriteChanged;
        _playback.PropertyChanged += PlaybackOnPropertyChanged;
    }

    public ObservableCollection<BroadcastRowViewModel> ContinueListening { get; } = new();
    public ObservableCollection<BroadcastRowViewModel> RecentBroadcasts { get; } = new();
    public ObservableCollection<BroadcastRowViewModel> UnheardBroadcasts { get; } = new();
    public ObservableCollection<BroadcastRowViewModel> OnThisDay { get; } = new();
    public ObservableCollection<DashboardCarouselDotViewModel> OnThisDayDots { get; } = new();
    public RelatedWikiPagesViewModel RelatedWiki { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SurpriseMeCommand { get; }
    public ICommand PreviousOnThisDayCommand { get; }
    public ICommand NextOnThisDayCommand { get; }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommandState(); } }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public int TotalBroadcasts
    {
        get => _totalBroadcasts;
        private set
        {
            if (SetProperty(ref _totalBroadcasts, value))
                RaisePropertyChanged(nameof(TotalBroadcastsText));
        }
    }
    public int CompletedBroadcasts
    {
        get => _completedBroadcasts;
        private set
        {
            if (SetProperty(ref _completedBroadcasts, value))
                RaisePropertyChanged(nameof(CompletedBroadcastsText));
        }
    }
    public int InProgressBroadcasts
    {
        get => _inProgressBroadcasts;
        private set
        {
            if (SetProperty(ref _inProgressBroadcasts, value))
                RaisePropertyChanged(nameof(InProgressBroadcastsText));
        }
    }
    public int FavouriteBroadcasts
    {
        get => _favouriteBroadcasts;
        private set
        {
            if (SetProperty(ref _favouriteBroadcasts, value))
                RaisePropertyChanged(nameof(FavouriteBroadcastsText));
        }
    }
    public int NeedsAttentionBroadcasts { get => _needsAttentionBroadcasts; private set => SetProperty(ref _needsAttentionBroadcasts, value); }
    public string LibraryModelText { get => _libraryModelText; private set => SetProperty(ref _libraryModelText, value); }
    public BroadcastRowViewModel? FeaturedContinue
    {
        get => _featuredContinue;
        private set { if (SetProperty(ref _featuredContinue, value)) RaisePropertyChanged(nameof(HasFeaturedContinue)); }
    }
    public BroadcastRowViewModel? CurrentOnThisDay
    {
        get => _currentOnThisDay;
        private set => SetProperty(ref _currentOnThisDay, value);
    }
    public int OnThisDayIndex
    {
        get => _onThisDayIndex;
        private set
        {
            if (!SetProperty(ref _onThisDayIndex, value)) return;
            for (var i = 0; i < OnThisDayDots.Count; i++) OnThisDayDots[i].SetActive(i == value);
        }
    }
    public string TotalBroadcastsText => TotalBroadcasts.ToString("N0");
    public string CompletedBroadcastsText => CompletedBroadcasts.ToString("N0");
    public string InProgressBroadcastsText => InProgressBroadcasts.ToString("N0");
    public string FavouriteBroadcastsText => FavouriteBroadcasts.ToString("N0");
    public int CompletionPercent => TotalBroadcasts <= 0 ? 0 : (int)Math.Round(CompletedBroadcasts * 100d / TotalBroadcasts);
    public string CompletionPercentText => $"{CompletionPercent}%";
    public int UnplayedBroadcasts => Math.Max(0, TotalBroadcasts - CompletedBroadcasts - InProgressBroadcasts);
    public string CompletionSummary => TotalBroadcasts <= 0
        ? "Listening progress will appear once broadcasts are available."
        : $"{CompletedBroadcasts:N0} of {TotalBroadcasts:N0} broadcasts completed";
    public bool HasFeaturedContinue => FeaturedContinue is not null;
    public bool HasContinueListening => ContinueListening.Count > 0;
    public bool HasRecentBroadcasts => RecentBroadcasts.Count > 0;
    public bool HasUnheardBroadcasts => UnheardBroadcasts.Count > 0;
    public bool HasOnThisDay => OnThisDay.Count > 0;

    public async Task LoadAsync(bool force = false)
    {
        if (_isLoaded && !force && ConnectedViewRefreshPolicy.IsFresh(_loadedAt)) return;
        IsBusy = true;
        StatusText = "Reading the Library…";
        try
        {
            var overview = await _library.GetOverviewAsync().ConfigureAwait(true);
            var unheard = await _library.BrowseAsync(new LibraryBrowseRequest(
                Filter: LibraryListeningFilter.Unplayed,
                Limit: 5,
                NewestFirst: true)).ConfigureAwait(true);
            TotalBroadcasts = overview.TotalBroadcasts;
            CompletedBroadcasts = overview.CompletedBroadcasts;
            InProgressBroadcasts = overview.InProgressBroadcasts;
            FavouriteBroadcasts = overview.FavouriteBroadcasts;
            NeedsAttentionBroadcasts = overview.NeedsAttentionBroadcasts;
            LibraryModelText = overview.UsesCanonicalLibrary ? "Library ready" : "Compatibility library";

            var continueRows = overview.ContinueListening.Take(5).Select(CreateRow).ToArray();
            FeaturedContinue = continueRows.FirstOrDefault();
            Replace(ContinueListening, continueRows.Skip(1));
            Replace(RecentBroadcasts, overview.RecentBroadcasts.Take(5).Select(CreateRow));
            Replace(UnheardBroadcasts, unheard.Broadcasts.Take(5).Select(CreateRow));
            Replace(OnThisDay, overview.OnThisDay.Select(CreateRow));
            _liveListeningStates.Clear();
            BuildDots();
            OnThisDayIndex = 0;
            CurrentOnThisDay = OnThisDay.FirstOrDefault();
            ApplyLivePlayback();

            RaiseSummaryProperties();
            StatusText = overview.TotalBroadcasts == 0
                ? "The library is ready, but it does not contain any broadcasts yet."
                : $"{overview.TotalBroadcasts:N0} broadcasts across {overview.Collections.Count:N0} shows.";
            _isLoaded = true;
            _loadedAt = DateTimeOffset.UtcNow;
            _ = EnrichDashboardKnowledgeInBackgroundAsync();
        }
        catch (Exception ex) { SetError(ex); }
        finally { IsBusy = false; }
    }

    public void MoveOnThisDay(int direction)
    {
        if (OnThisDay.Count == 0) return;
        var next = (OnThisDayIndex + direction) % OnThisDay.Count;
        if (next < 0) next += OnThisDay.Count;
        SelectOnThisDay(next);
    }

    private void SelectOnThisDay(int index)
    {
        if (index < 0 || index >= OnThisDay.Count) return;
        OnThisDayIndex = index;
        CurrentOnThisDay = OnThisDay[index];
        _ = LoadCurrentOnThisDayDetailsAsync(CurrentOnThisDay);
    }

    private void BuildDots()
    {
        OnThisDayDots.Clear();
        for (var i = 0; i < OnThisDay.Count; i++)
            OnThisDayDots.Add(new DashboardCarouselDotViewModel(i, SelectOnThisDay));
    }

    private async Task EnrichDashboardKnowledgeInBackgroundAsync()
    {
        // The Dashboard shell and its global loading bar should not wait for
        // optional topic/people enrichment. Each card handles its own failure
        // and updates independently after the primary overview is usable.
        foreach (var row in OnThisDay.Take(12).ToArray())
        {
            if (!OnThisDay.Contains(row)) break;
            await LoadCurrentOnThisDayDetailsAsync(row).ConfigureAwait(true);
        }
        if (_wiki is null) return;
        try
        {
            var now = DateTimeOffset.Now;
            var highlights = await _wiki.GetDashboardHighlightsAsync(now.Month, now.Day).ConfigureAwait(true);
            foreach (var row in OnThisDay.ToArray())
            {
                var match = highlights.OnThisDay.FirstOrDefault(item => item.Event.Broadcasts.Any(link =>
                    link.EpisodeId == row.Source.RepresentativeEpisodeId));
                if (match is not null)
                    row.SetWikiSummary(string.IsNullOrWhiteSpace(match.Event.Summary) ? match.Page.Summary : match.Event.Summary,
                        match.Page.Title);
                else
                    await EnrichRowFromWikiAsync(row).ConfigureAwait(true);
            }

            var listeningRows = ContinueListening.AsEnumerable();
            if (FeaturedContinue is not null) listeningRows = listeningRows.Prepend(FeaturedContinue);
            foreach (var row in listeningRows.DistinctBy(x => x.Source.RepresentativeEpisodeId).Take(5))
                await EnrichRowFromWikiAsync(row).ConfigureAwait(true);
        }
        catch
        {
            // Wiki context is progressive enrichment; playback and the core Dashboard remain available.
        }
    }

    private async Task EnrichRowFromWikiAsync(BroadcastRowViewModel row)
    {
        if (_wiki is null || row.HasWikiSummary) return;
        var queries = new[] { row.CollectionText, row.Title }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var query in queries)
        {
            var pages = await _wiki.BrowseAsync(new WikiBrowseQuery(query, Limit: 20)).ConfigureAwait(true);
            var page = pages
                .Where(x => !string.IsNullOrWhiteSpace(x.Summary))
                .OrderByDescending(x => string.Equals(x.Title, row.CollectionText, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => string.Equals(x.Title, row.Title, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.Status == "Published")
                .FirstOrDefault();
            if (page is null) continue;
            row.SetWikiSummary(page.Summary, page.Title);
            return;
        }
    }

    private async Task LoadCurrentOnThisDayDetailsAsync(BroadcastRowViewModel? row)
    {
        if (row is null || row.Details is not null || row.IsDetailsLoading) return;
        row.SetDetailsLoading(true);
        try { row.SetDetails(await _details.GetAsync(row.Source.RepresentativeEpisodeId).ConfigureAwait(true)); }
        catch { row.SetDetails(null); }
    }

    private async Task SurpriseMeAsync()
    {
        IsBusy = true;
        StatusText = "Choosing something from the archive…";
        try
        {
            var result = await _library.BrowseAsync(new LibraryBrowseRequest(Filter: LibraryListeningFilter.Unplayed, Limit: 10000, NewestFirst: true)).ConfigureAwait(true);
            var candidates = result.Broadcasts;
            if (candidates.Count == 0)
                candidates = (await _library.BrowseAsync(new LibraryBrowseRequest(Limit: 10000)).ConfigureAwait(true)).Broadcasts;
            if (candidates.Count == 0) { StatusText = "There are no available broadcasts to play yet."; return; }
            var selected = candidates[Random.Shared.Next(candidates.Count)];
            await _playback.LoadAndPlayAsync(selected.RepresentativeEpisodeId).ConfigureAwait(true);
            StatusText = $"Surprise: {selected.Title ?? selected.CollectionName}.";
        }
        finally { IsBusy = false; }
    }

    private BroadcastRowViewModel CreateRow(LibraryBroadcastSummary source)
        => new(source, _playback.LoadAndPlayAsync, ToggleFavouriteAsync, AddToQueueAsync);

    private async Task ToggleFavouriteAsync(BroadcastRowViewModel row)
    {
        var next = !row.IsFavourite;
        await _actions.SetFavouriteAsync(row.Source.RepresentativeEpisodeId, next).ConfigureAwait(true);
        row.SetFavourite(next);
        _playback.SetFavouriteStateFromExternal(row.Source.RepresentativeEpisodeId, next);
        FavouriteBroadcasts = Math.Max(0, FavouriteBroadcasts + (next ? 1 : -1));
        StatusText = next ? "Added to favourites." : "Removed from favourites.";
    }

    private async Task AddToQueueAsync(BroadcastRowViewModel row, bool playNext)
    {
        await _queue.AddAsync(row.Source.RepresentativeEpisodeId, playNext).ConfigureAwait(true);
        StatusText = playNext ? "Added to play next." : "Added to queue.";
    }

    private void PlaybackOnFavouriteChanged(object? sender, PlaybackFavouriteChangedEventArgs e)
    {
        var sources = AllRows();
        var matches = sources.Where(x => x.Source.RepresentativeEpisodeId == e.RepresentativeEpisodeId).ToArray();
        if (matches.Length == 0) return;
        var previous = matches[0].IsFavourite;
        foreach (var row in matches) row.SetFavourite(e.Favourite);
        if (previous != e.Favourite) FavouriteBroadcasts = Math.Max(0, FavouriteBroadcasts + (e.Favourite ? 1 : -1));
    }

    private void PlaybackOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackViewModel.LiveProgress)
            or nameof(PlaybackViewModel.IsPlaying)
            or nameof(PlaybackViewModel.CurrentBroadcastId))
            ApplyLivePlayback();
    }

    private void ApplyLivePlayback()
    {
        var live = _playback.LiveProgress;
        BroadcastRowViewModel? matchingRow = null;
        bool? previousCompleted = null;
        bool? previousInProgress = null;
        foreach (var row in AllRows())
        {
            var current = live?.Matches(row.Source) == true;
            row.SetPlaybackState(current, current && live!.IsPlaying);
            if (!current) continue;
            if (matchingRow is null)
            {
                matchingRow = row;
                previousCompleted = row.IsCompleted;
                previousInProgress = row.IsInProgress;
            }
            row.ApplyLiveProgress(live!.PositionMs, live.DurationMs, live.Completed);
        }

        if (live is not null)
            UpdateListeningTotals(live, previousCompleted, previousInProgress);
        ReconcileContinueListening(live, matchingRow);
    }

    private void ReconcileContinueListening(PlaybackLiveProgress? live, BroadcastRowViewModel? matchingRow)
    {
        if (live is null || live.RepresentativeEpisodeId <= 0) return;
        var shouldContinue = live.PositionMs > 0 && !live.Completed;

        if (!shouldContinue)
        {
            RemoveFromContinueListening(live);
            if (matchingRow is null && !_liveListeningStates.ContainsKey(live.RepresentativeEpisodeId))
                _ = EnsureLiveDashboardStateAsync(live);
            return;
        }

        RemoveFromUnheard(live);

        if (matchingRow is not null)
        {
            PromoteContinueListening(matchingRow);
            return;
        }

        _ = EnsureLiveDashboardStateAsync(live);
    }

    private void RemoveFromUnheard(PlaybackLiveProgress live)
    {
        var changed = false;
        for (var index = UnheardBroadcasts.Count - 1; index >= 0; index--)
        {
            if (!live.Matches(UnheardBroadcasts[index].Source)) continue;
            UnheardBroadcasts.RemoveAt(index);
            changed = true;
        }
        if (changed) RaisePropertyChanged(nameof(HasUnheardBroadcasts));
    }

    private async Task EnsureLiveDashboardStateAsync(PlaybackLiveProgress requested)
    {
        if (!await _liveRowGate.WaitAsync(0).ConfigureAwait(true)) return;
        try
        {
            var current = _playback.LiveProgress;
            if (current is null || current.RepresentativeEpisodeId != requested.RepresentativeEpisodeId)
                return;

            var source = await _library.GetBroadcastAsync(current.RepresentativeEpisodeId).ConfigureAwait(true);
            current = _playback.LiveProgress;
            if (source is null || current is null || !current.Matches(source))
                return;

            UpdateListeningTotals(current, source.Completed, source.InProgress);
            if (current.PositionMs <= 0 || current.Completed) return;

            var existing = AllRows().FirstOrDefault(row => current.Matches(row.Source));
            var row = existing ?? CreateRow(source);
            row.ApplyLiveProgress(current.PositionMs, current.DurationMs, current.Completed);
            row.SetPlaybackState(true, current.IsPlaying);
            PromoteContinueListening(row);
        }
        catch
        {
            // Live projection is best-effort; the next library/dashboard refresh
            // will still reconstruct Continue Listening from canonical state.
        }
        finally
        {
            _liveRowGate.Release();
        }
    }

    private void PromoteContinueListening(BroadcastRowViewModel row)
    {
        if (ReferenceEquals(FeaturedContinue, row)) return;

        for (var index = ContinueListening.Count - 1; index >= 0; index--)
        {
            if (SameBroadcast(ContinueListening[index], row))
                ContinueListening.RemoveAt(index);
        }
        var previous = FeaturedContinue;
        FeaturedContinue = row;
        if (previous is not null && !SameBroadcast(previous, row)
            && previous.IsInProgress && !previous.IsCompleted)
        {
            ContinueListening.Remove(previous);
            ContinueListening.Insert(0, previous);
        }

        while (ContinueListening.Count > 4)
            ContinueListening.RemoveAt(ContinueListening.Count - 1);
        RaisePropertyChanged(nameof(HasContinueListening));
    }

    private static bool SameBroadcast(BroadcastRowViewModel left, BroadcastRowViewModel right)
        => left.Source.RepresentativeEpisodeId == right.Source.RepresentativeEpisodeId
           || (!string.IsNullOrWhiteSpace(left.Source.CanonicalKey)
               && string.Equals(left.Source.CanonicalKey, right.Source.CanonicalKey, StringComparison.OrdinalIgnoreCase));

    private void UpdateListeningTotals(
        PlaybackLiveProgress live,
        bool? sourceCompleted,
        bool? sourceInProgress)
    {
        if (!_liveListeningStates.TryGetValue(live.RepresentativeEpisodeId, out var previous))
        {
            if (!sourceCompleted.HasValue || !sourceInProgress.HasValue) return;
            previous = (sourceCompleted.Value, sourceInProgress.Value);
        }

        var current = (Completed: live.Completed, InProgress: live.PositionMs > 0 && !live.Completed);
        if (previous == current)
        {
            _liveListeningStates[live.RepresentativeEpisodeId] = current;
            return;
        }

        CompletedBroadcasts = Math.Max(0, CompletedBroadcasts
            + (current.Completed ? 1 : 0)
            - (previous.Completed ? 1 : 0));
        InProgressBroadcasts = Math.Max(0, InProgressBroadcasts
            + (current.InProgress ? 1 : 0)
            - (previous.InProgress ? 1 : 0));
        _liveListeningStates[live.RepresentativeEpisodeId] = current;
        RaiseSummaryProperties();
    }

    private void RemoveFromContinueListening(PlaybackLiveProgress live)
    {
        for (var index = ContinueListening.Count - 1; index >= 0; index--)
        {
            if (live.Matches(ContinueListening[index].Source))
                ContinueListening.RemoveAt(index);
        }

        if (FeaturedContinue is not null && live.Matches(FeaturedContinue.Source))
        {
            FeaturedContinue = ContinueListening.FirstOrDefault();
            if (FeaturedContinue is not null) ContinueListening.RemoveAt(0);
        }
        RaisePropertyChanged(nameof(HasContinueListening));
    }

    private IEnumerable<BroadcastRowViewModel> AllRows()
    {
        var rows = ContinueListening
            .Concat(RecentBroadcasts)
            .Concat(UnheardBroadcasts)
            .Concat(OnThisDay);
        return FeaturedContinue is null ? rows : rows.Prepend(FeaturedContinue);
    }

    private void RaiseSummaryProperties()
    {
        RaisePropertyChanged(nameof(CompletionPercent));
        RaisePropertyChanged(nameof(CompletionPercentText));
        RaisePropertyChanged(nameof(UnplayedBroadcasts));
        RaisePropertyChanged(nameof(CompletionSummary));
        RaisePropertyChanged(nameof(HasContinueListening));
        RaisePropertyChanged(nameof(HasRecentBroadcasts));
        RaisePropertyChanged(nameof(HasUnheardBroadcasts));
        RaisePropertyChanged(nameof(HasOnThisDay));
        RaiseCommandState();
    }

    private void RaiseCommandState()
    {
        ((AsyncCommand)RefreshCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)SurpriseMeCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)PreviousOnThisDayCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)NextOnThisDayCommand).RaiseCanExecuteChanged();
    }

    private void SetError(Exception exception) => StatusText = $"Dashboard could not load: {exception.Message}";
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }
}
