using System.Collections.ObjectModel;
using System.Windows.Input;
using TheRadioVault.Application.Abstractions;
using TheRadioVault.Application.Models;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private const string LibraryRoute = "library";
    private readonly INavigationService _navigation;
    private readonly ShellNavigationItemViewModel _libraryNavigation;
    private object _currentPage;
    private string _currentRoute = "dashboard";
    private string _pageTitle = "Dashboard";
    private string _pageDescription = "A calm overview of the archive and listening state.";
    private string _broadcastInfoReturnRoute = LibraryRoute;
    private string _navigationErrorText = string.Empty;

    public MainWindowViewModel(
        string version,
        string sessionDescription,
        DashboardViewModel dashboard,
        LibraryViewModel library,
        SearchViewModel search,
        QueueViewModel queue,
        MomentsViewModel moments,
        CollectionsViewModel collections,
        TranscriptsViewModel transcripts,
        ResearchWorkspaceViewModel research,
        WikiViewModel wiki,
        DownloadsViewModel downloads,
        PlaybackViewModel playback,
        NowPlayingViewModel nowPlaying,
        FullBroadcastInfoViewModel broadcastInfo,
        DesktopToolsViewModel tools,
        INavigationService navigation)
    {
        Version = version;
        SessionDescription = sessionDescription;
        Dashboard = dashboard;
        Library = library;
        Search = search ?? throw new ArgumentNullException(nameof(search));
        Queue = queue;
        Moments = moments;
        Collections = collections ?? throw new ArgumentNullException(nameof(collections));
        Saved = new SavedViewModel(Library, Moments, Collections);
        Transcripts = transcripts ?? throw new ArgumentNullException(nameof(transcripts));
        Research = research ?? throw new ArgumentNullException(nameof(research));
        Wiki = wiki ?? throw new ArgumentNullException(nameof(wiki));
        Downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        Playback = playback ?? throw new ArgumentNullException(nameof(playback));
        NowPlaying = nowPlaying ?? throw new ArgumentNullException(nameof(nowPlaying));
        BroadcastInfo = broadcastInfo ?? throw new ArgumentNullException(nameof(broadcastInfo));
        Tools = tools ?? throw new ArgumentNullException(nameof(tools));
        Activity = new ShellActivityViewModel(
            Dashboard,
            Library,
            Search,
            Queue,
            Moments,
            Transcripts,
            Research,
            Downloads,
            Playback,
            NowPlaying,
            BroadcastInfo,
            Tools);
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        OpenNowPlayingCommand = new AsyncCommand(() => NavigateToAsync("now-playing"));
        OpenToolsCommand = new AsyncCommand(() => NavigateToAsync("tools"));
        _currentPage = Dashboard;
        _libraryNavigation = new ShellNavigationItemViewModel(
            LibraryRoute,
            "Library",
            "All broadcasts and shows",
            "▤",
            NavigateToAsync,
            iconTone: "accent",
            isExpandable: true,
            isExpanded: true);
        NavigationItems = new ObservableCollection<ShellNavigationItemViewModel>(new[]
        {
            new ShellNavigationItemViewModel("dashboard", "Dashboard", "Overview and likely next actions", "⌂", NavigateToAsync, iconTone: "accent"),
            new ShellNavigationItemViewModel("search", "Search", "Search, shows and collections", "⌕", NavigateToAsync, iconTone: "search"),
            _libraryNavigation,
            new ShellNavigationItemViewModel("wiki", "Explore", "People, shows, history and cited timelines", "W", NavigateToAsync, iconTone: "wiki"),
            new ShellNavigationItemViewModel("saved", "Saved", "Favourite broadcasts and listening Moments", "◷", NavigateToAsync, iconTone: "moment"),
            new ShellNavigationItemViewModel("research", "Knowledge", "Evidence, metadata and transcription", "◫", NavigateToAsync, iconTone: "research"),
            new ShellNavigationItemViewModel("downloads", "Downloads", "Broadcasts stored on this PC", "↓", NavigateToAsync, iconTone: "progress"),
            new ShellNavigationItemViewModel("tools", "Settings", "Local Library, playback and maintenance", "⚙", NavigateToAsync, iconTone: "settings"),
            new ShellNavigationItemViewModel("now-playing", "Now Playing", "Current broadcast and queue", "▶", NavigateToAsync, iconTone: "progress")
        });
        NavigationItems[0].IsSelected = true;
        Library.SetOpenBroadcastInfoHandler(OpenBroadcastInfoAsync);
        Search.SetOpenLibraryHandler(OpenLibraryPresetAsync);
        NowPlaying.SetOpenFullInfoHandler(OpenBroadcastInfoAsync);
        NowPlaying.SetOpenTranscriptHandler(OpenTranscriptAsync);
        BroadcastInfo.SetBackHandler(ReturnFromBroadcastInfoAsync);
        BroadcastInfo.SetOpenTranscriptHandler(OpenTranscriptAsync);
        Research.SetOpenTranscriptionHandler(() => NavigateToAsync("research/transcription"));
        Transcripts.SetOpenResearchHandler(() => NavigateToAsync("research"));
        Wiki.SetOpenBroadcastInfoHandler(OpenBroadcastInfoAsync);
        foreach (var relatedWiki in new[]
                 {
                     Dashboard.RelatedWiki, Library.RelatedWiki, Search.RelatedWiki, NowPlaying.RelatedWiki,
                     BroadcastInfo.RelatedWiki
                 })
        {
            relatedWiki.SetOpenHandler(OpenWikiPageAsync);
            relatedWiki.SetOpenEntityHandler(OpenWikiEntityAsync);
        }
        Transcripts.SetOpenSettingsHandler(OpenTranscriptionSettingsAsync);
        Tools.LibraryScanCompleted += OnLibraryScanCompleted;
    }

    public string Version { get; }
    public string SessionDescription { get; }
    public DashboardViewModel Dashboard { get; }
    public LibraryViewModel Library { get; }
    public SearchViewModel Search { get; }
    public QueueViewModel Queue { get; }
    public MomentsViewModel Moments { get; }
    public CollectionsViewModel Collections { get; }
    public SavedViewModel Saved { get; }
    public TranscriptsViewModel Transcripts { get; }
    public ResearchWorkspaceViewModel Research { get; }
    public WikiViewModel Wiki { get; }
    public DownloadsViewModel Downloads { get; }
    public PlaybackViewModel Playback { get; }
    public NowPlayingViewModel NowPlaying { get; }
    public FullBroadcastInfoViewModel BroadcastInfo { get; }
    public DesktopToolsViewModel Tools { get; }
    public ShellActivityViewModel Activity { get; }
    public ObservableCollection<ShellNavigationItemViewModel> NavigationItems { get; }
    public ICommand OpenNowPlayingCommand { get; }
    public ICommand OpenToolsCommand { get; }
    public object CurrentPage { get => _currentPage; private set => SetProperty(ref _currentPage, value); }
    public string CurrentRoute
    {
        get => _currentRoute;
        private set
        {
            if (SetProperty(ref _currentRoute, value))
                RaisePropertyChanged(nameof(ShowPageHeader));
        }
    }
    public bool ShowPageHeader
    {
        get
        {
            if (string.Equals(CurrentRoute, "dashboard", StringComparison.OrdinalIgnoreCase)
                || string.Equals(CurrentRoute, "now-playing", StringComparison.OrdinalIgnoreCase)
                || string.Equals(CurrentRoute, "saved", StringComparison.OrdinalIgnoreCase)
                || string.Equals(CurrentRoute, "transcripts", StringComparison.OrdinalIgnoreCase)
                || CurrentRoute.StartsWith("research", StringComparison.OrdinalIgnoreCase)
                || string.Equals(CurrentRoute, "wiki", StringComparison.OrdinalIgnoreCase)
                || string.Equals(CurrentRoute, "downloads", StringComparison.OrdinalIgnoreCase)
                || string.Equals(CurrentRoute, "tools", StringComparison.OrdinalIgnoreCase))
                return false;
            return !CurrentRoute.StartsWith("library", StringComparison.OrdinalIgnoreCase);
        }
    }
    public string PageTitle { get => _pageTitle; private set => SetProperty(ref _pageTitle, value); }
    public string PageDescription { get => _pageDescription; private set => SetProperty(ref _pageDescription, value); }
    public string NavigationErrorText
    {
        get => _navigationErrorText;
        private set
        {
            if (!SetProperty(ref _navigationErrorText, value)) return;
            RaisePropertyChanged(nameof(HasNavigationError));
        }
    }
    public bool HasNavigationError => !string.IsNullOrWhiteSpace(NavigationErrorText);

    public async Task InitializeAsync(bool warmCachedViews = false)
    {
        await Downloads.LoadAsync().ConfigureAwait(true);
        await RefreshLibraryNavigationAsync().ConfigureAwait(true);
        await NavigateToAsync("dashboard").ConfigureAwait(true);
        if (warmCachedViews) await WarmCachedNavigationAsync().ConfigureAwait(true);
    }

    private async Task WarmCachedNavigationAsync()
    {
        try
        {
            await Task.WhenAll(
                Search.LoadAsync(),
                Moments.LoadAsync(),
                Collections.LoadAsync(),
                Queue.LoadAsync()).ConfigureAwait(true);
        }
        catch
        {
            // Every view remains available on demand. A missing saved response
            // must never prevent the primary Dashboard from opening.
        }
    }

    public async Task RefreshAfterServerSyncAsync(
        bool fullRefresh,
        IReadOnlySet<string> changedKinds)
    {
        changedKinds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Changed(params string[] kinds) => fullRefresh || kinds.Any(changedKinds.Contains);

        if (Changed("library", "metadata", "research", "listening-status", "favourite", "offline-progress"))
        {
            await RefreshLibraryNavigationAsync().ConfigureAwait(true);
            await Dashboard.LoadAsync(force: true).ConfigureAwait(true);
            await Search.LoadAsync(force: true).ConfigureAwait(true);
            if (string.Equals(CurrentRoute, "saved", StringComparison.OrdinalIgnoreCase) && Saved.IsFavouritesSelected)
                await Saved.LoadAsync(force: true).ConfigureAwait(true);
        }
        if (Changed("moment"))
        {
            if (string.Equals(CurrentRoute, "saved", StringComparison.OrdinalIgnoreCase) && Saved.IsMomentsSelected)
                await Saved.LoadAsync(force: true).ConfigureAwait(true);
            else
                await Moments.LoadAsync(force: true).ConfigureAwait(true);
        }
        if (Changed("saved-collection"))
        {
            if (string.Equals(CurrentRoute, "saved", StringComparison.OrdinalIgnoreCase) && Saved.IsCollectionsSelected)
                await Saved.LoadAsync(force: true).ConfigureAwait(true);
            else
                await Collections.LoadAsync(force: true).ConfigureAwait(true);
        }
        if (Changed("queue"))
            await Queue.LoadAsync(force: true).ConfigureAwait(true);
        if (Changed("transcription", "transcription-control", "job"))
            await Transcripts.LoadAsync(force: true).ConfigureAwait(true);
        if (Changed("wiki"))
            await Wiki.LoadAsync(force: true).ConfigureAwait(true);
    }

    public async Task NavigateToAsync(string route)
    {
        NavigationErrorText = string.Empty;
        try
        {
            await NavigateToCoreAsync(route).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Window shutdown can cancel an in-flight navigation safely.
        }
        catch (Exception exception)
        {
            NavigationErrorText = IsServerConnectionFailure(exception)
                ? "This view needs a Radio Vault Server. Open Settings › Server connection to find and pair with your server."
                : $"This view could not be loaded: {exception.Message}";
        }
    }

    private async Task NavigateToCoreAsync(string route)
    {
        if (string.Equals(route, "queue", StringComparison.OrdinalIgnoreCase)) route = "now-playing";
        if (string.Equals(route, "transcripts", StringComparison.OrdinalIgnoreCase)) route = "research/transcription";
        SavedSection? savedSection = string.Equals(route, "moments", StringComparison.OrdinalIgnoreCase)
            ? SavedSection.Moments
            : string.Equals(route, "collections", StringComparison.OrdinalIgnoreCase)
                ? SavedSection.Collections
            : string.Equals(route, "favourites", StringComparison.OrdinalIgnoreCase)
                ? SavedSection.Favourites
                : null;
        if (string.Equals(route, "moments", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(route, "collections", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(route, "favourites", StringComparison.OrdinalIgnoreCase))
            route = "saved";
        if (string.Equals(route, LibraryRoute, StringComparison.OrdinalIgnoreCase))
            await RefreshLibraryNavigationAsync().ConfigureAwait(true);
        await _navigation.NavigateAsync(NavigationRequest.To(route)).ConfigureAwait(true);
        CurrentRoute = route;
        UpdateNavigationSelection(route);

        if (TryParseLibraryRoute(route, out var collectionId))
        {
            var collectionName = collectionId.HasValue
                ? _libraryNavigation.Children.FirstOrDefault(x => string.Equals(x.Route, route, StringComparison.OrdinalIgnoreCase))?.Title
                : null;
            CurrentPage = Library;
            PageTitle = collectionId.HasValue && !string.IsNullOrWhiteSpace(collectionName)
                ? collectionName
                : "Library";
            PageDescription = collectionId.HasValue
                ? $"Browse {collectionName} by year and month, or switch to a complete list."
                : "Browse every canonical broadcast in the archive.";
            if (collectionId.HasValue) _libraryNavigation.IsExpanded = true;
            await Library.SelectCollectionAsync(collectionId, collectionName, force: false).ConfigureAwait(true);
            if (Library.SelectedFilter.Filter != LibraryListeningFilter.All)
                await Library.SetListeningFilterAsync(LibraryListeningFilter.All).ConfigureAwait(true);
            return;
        }

        switch (route)
        {
            case "search":
                CurrentPage = Search;
                PageTitle = "Search";
                PageDescription = "Search the archive, browse shows, or open a useful collection.";
                await Search.LoadAsync().ConfigureAwait(true);
                break;
            case "now-playing":
                CurrentPage = NowPlaying;
                PageTitle = "Now Playing";
                PageDescription = "Current playback, broadcast context and the queue in one place.";
                await NowPlaying.LoadAsync().ConfigureAwait(true);
                break;
            case "saved":
                CurrentPage = Saved;
                PageTitle = "Saved";
                PageDescription = "Favourite broadcasts and exact listening Moments in one place.";
                await Saved.SelectSectionAsync(savedSection ?? Saved.SelectedSection).ConfigureAwait(true);
                break;
            case "research/transcription":
                CurrentPage = Transcripts;
                PageTitle = "Transcription studio";
                PageDescription = "Create and edit transcripts as part of Knowledge.";
                await Transcripts.LoadAsync().ConfigureAwait(true);
                break;
            case "research":
                CurrentPage = Research;
                PageTitle = "Knowledge";
                PageDescription = "Evidence, provenance and protected metadata editing for the archive.";
                await Research.LoadAsync().ConfigureAwait(true);
                break;
            case "wiki":
                CurrentPage = Wiki;
                PageTitle = "Explore";
                PageDescription = "A cited, human-editable history of the archive.";
                await Wiki.LoadAsync().ConfigureAwait(true);
                break;
            case "downloads":
                CurrentPage = Downloads;
                PageTitle = "Downloads";
                PageDescription = "Broadcasts stored on this PC for offline playback.";
                await Downloads.LoadAsync().ConfigureAwait(true);
                break;
            case "tools":
                CurrentPage = Tools;
                PageTitle = "Settings";
                PageDescription = "Local Library folders, archive health, playback and maintenance.";
                await Tools.LoadAsync().ConfigureAwait(true);
                break;
            default:
                CurrentPage = Dashboard;
                PageTitle = "Dashboard";
                PageDescription = "Continue listening, rediscover the archive, or choose something unexpected.";
                await Dashboard.LoadAsync().ConfigureAwait(true);
                break;
        }
    }

    private static bool IsServerConnectionFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is InvalidOperationException &&
                (current.Message.Contains("Radio Vault Server connection", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("paired Radio Vault Server", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("Pair this client", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    public async Task OpenBroadcastInfoAsync(long representativeEpisodeId)
    {
        if (representativeEpisodeId <= 0) return;
        if (!string.Equals(CurrentRoute, "broadcast-info", StringComparison.OrdinalIgnoreCase))
            _broadcastInfoReturnRoute = CurrentRoute;
        CurrentRoute = "broadcast-info";
        CurrentPage = BroadcastInfo;
        PageTitle = "Broadcast information";
        PageDescription = "Full canonical, media, research and archive details.";
        await BroadcastInfo.LoadAsync(representativeEpisodeId).ConfigureAwait(true);
    }

    private Task ReturnFromBroadcastInfoAsync()
        => NavigateToAsync(string.IsNullOrWhiteSpace(_broadcastInfoReturnRoute) ? LibraryRoute : _broadcastInfoReturnRoute);

    private async Task OpenTranscriptAsync(long episodeId)
    {
        await NavigateToAsync("research/transcription").ConfigureAwait(true);
        await Transcripts.FocusEpisodeAsync(episodeId).ConfigureAwait(true);
    }

    private async Task OpenWikiPageAsync(WikiPageSummary page)
    {
        await NavigateToAsync("wiki").ConfigureAwait(true);
        Wiki.SelectedPage = page;
    }

    private async Task OpenWikiEntityAsync(string entity)
    {
        if (string.IsNullOrWhiteSpace(entity)) return;
        await NavigateToAsync("wiki").ConfigureAwait(true);
        await Wiki.OpenEntityAsync(entity).ConfigureAwait(true);
    }

    private async Task OpenTranscriptionSettingsAsync()
    {
        await NavigateToAsync("tools").ConfigureAwait(true);
        Tools.SelectSectionByKey("transcription");
    }

    private async Task OpenLibraryPresetAsync(int? collectionId, LibraryListeningFilter filter)
    {
        var route = collectionId.HasValue ? $"{LibraryRoute}/{collectionId.Value}" : LibraryRoute;
        await NavigateToAsync(route).ConfigureAwait(true);
        if (filter != LibraryListeningFilter.All)
        {
            await Library.SetListeningFilterAsync(filter).ConfigureAwait(true);
            PageTitle = filter switch
            {
                LibraryListeningFilter.ContinueListening => "Continue listening",
                LibraryListeningFilter.Favourites => "Favourites",
                LibraryListeningFilter.Unplayed => "Unplayed",
                LibraryListeningFilter.RecentlyAdded => "Recently added",
                LibraryListeningFilter.OnThisDay => "On this day",
                _ => PageTitle
            };
            PageDescription = filter switch
            {
                LibraryListeningFilter.ContinueListening => "Unfinished broadcasts ordered by your latest listening activity.",
                LibraryListeningFilter.Favourites => "Broadcasts you have saved for easy return.",
                LibraryListeningFilter.Unplayed => "Broadcasts you have not started yet.",
                LibraryListeningFilter.RecentlyAdded => "The newest items discovered by Library scans.",
                LibraryListeningFilter.OnThisDay => "Broadcasts from today's date in earlier years.",
                _ => PageDescription
            };
        }
    }

    private async void OnLibraryScanCompleted(object? sender, EventArgs eventArgs)
    {
        try
        {
            await RefreshLibraryNavigationAsync().ConfigureAwait(true);
            await Search.LoadAsync(force: true).ConfigureAwait(true);
            if (string.Equals(CurrentRoute, "research", StringComparison.OrdinalIgnoreCase))
                await Research.LoadAsync().ConfigureAwait(true);
        }
        catch
        {
            // A completed Library scan must remain successful even if a
            // secondary navigation refresh is interrupted by window shutdown.
        }
    }

    private async Task RefreshLibraryNavigationAsync()
    {
        try
        {
            var collections = await Library.LoadCollectionsAsync().ConfigureAwait(true);
            _libraryNavigation.ReplaceChildren(collections.Select(collection =>
                new ShellNavigationItemViewModel(
                    $"{LibraryRoute}/{collection.CollectionId}",
                    collection.CollectionName,
                    $"{collection.BroadcastCount:N0} broadcasts",
                    "•",
                    NavigateToAsync)));
        }
        catch
        {
            _libraryNavigation.ReplaceChildren(Array.Empty<ShellNavigationItemViewModel>());
        }
    }

    private void UpdateNavigationSelection(string route)
    {
        foreach (var item in NavigationItems)
        {
            item.IsSelected = item.Route is LibraryRoute or "research"
                ? route.StartsWith(item.Route, StringComparison.OrdinalIgnoreCase)
                : string.Equals(item.Route, route, StringComparison.OrdinalIgnoreCase);
            foreach (var child in item.Children)
                child.IsSelected = string.Equals(child.Route, route, StringComparison.OrdinalIgnoreCase);
        }
    }


    public void Dispose()
    {
        Tools.LibraryScanCompleted -= OnLibraryScanCompleted;
        Activity.Dispose();
    }

    private static bool TryParseLibraryRoute(string route, out int? collectionId)
    {
        collectionId = null;
        if (string.Equals(route, LibraryRoute, StringComparison.OrdinalIgnoreCase)) return true;
        if (!route.StartsWith($"{LibraryRoute}/", StringComparison.OrdinalIgnoreCase)) return false;
        var value = route[(LibraryRoute.Length + 1)..];
        if (!int.TryParse(value, out var parsed)) return false;
        collectionId = parsed;
        return true;
    }
}
