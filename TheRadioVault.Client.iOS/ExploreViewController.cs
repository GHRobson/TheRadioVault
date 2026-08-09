using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Web.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class ExploreViewController : SessionTableViewController, IUISearchBarDelegate
{
    private readonly UISearchController _searchController = new((UIViewController?)null)
    {
        ObscuresBackgroundDuringPresentation = false
    };
    private readonly UISwitch _transcriptSwitch = new();
    private WebClientLibrarySearchFacets? _facets;
    private IReadOnlyList<WebClientLibrarySearchSuggestion> _suggestions = [];
    private IReadOnlyList<MobileBroadcastItem> _results = [];
    private int? _collectionId;
    private string _showTitle = "All shows";
    private string _filter = "All";
    private string _filterTitle = "Any status";
    private int? _year;
    private string _scope = "All";
    private string _scopeTitle = "Everything";
    private bool _loading;

    public ExploreViewController(MobileClientSession session) : base(session) => Title = "Explore";

    private bool HasActiveSearch => !string.IsNullOrWhiteSpace(_searchController.SearchBar.Text) ||
                                    _collectionId.HasValue || _year.HasValue || _transcriptSwitch.On ||
                                    !_filter.Equals("All", StringComparison.OrdinalIgnoreCase) ||
                                    !_scope.Equals("All", StringComparison.OrdinalIgnoreCase);

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Always;
        _searchController.SearchBar.Placeholder = "Show, person, topic, date or transcript phrase";
        _searchController.SearchBar.Delegate = this;
        NavigationItem.SearchController = _searchController;
        NavigationItem.HidesSearchBarWhenScrolling = false;
        DefinesPresentationContext = true;
        _transcriptSwitch.ValueChanged += TranscriptFilterChanged;
        RefreshControl = new UIRefreshControl();
        RefreshControl.ValueChanged += (_, _) => _ = LoadAsync();
        _ = LoadAsync();
    }

    public override nint NumberOfSections(UITableView tableView) => 4;

    public override nint RowsInSection(UITableView tableView, nint section) => section switch
    {
        0 => 5,
        1 => _suggestions.Count,
        2 => HasActiveSearch ? Math.Max(1, _results.Count) : 5,
        3 => HasActiveSearch ? 0 : Math.Max(1, Session.LibraryCollections.Count),
        _ => 0
    };

    public override string? TitleForHeader(UITableView tableView, nint section) => section switch
    {
        0 => "Narrow the results",
        1 when _suggestions.Count > 0 => "Suggestions",
        2 => HasActiveSearch ? "Search results · most relevant first" : "Ways into the archive",
        3 when !HasActiveSearch => "Browse by show",
        _ => null
    };

    public override string? TitleForFooter(UITableView tableView, nint section)
        => section == 0 ? Session.StatusText : null;

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (indexPath.Section == 0)
        {
            if (indexPath.Row == 4)
            {
                var transcript = DetailCell(
                    "explore-transcript",
                    "Has transcript",
                    _facets is null ? "Only broadcasts with transcripts" : $"{_facets.TranscriptCount:N0} available");
                transcript.AccessoryView = _transcriptSwitch;
                transcript.SelectionStyle = UITableViewCellSelectionStyle.None;
                return transcript;
            }
            var values = new[]
            {
                ("Search in", _scopeTitle),
                ("Listening status", _filterTitle),
                ("Show", _showTitle),
                ("Year", _year?.ToString() ?? "All years")
            };
            var value = values[indexPath.Row];
            var cell = DetailCell("explore-filter", value.Item1, value.Item2);
            cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
            return cell;
        }

        if (indexPath.Section == 1)
        {
            var suggestion = _suggestions[indexPath.Row];
            var cell = DetailCell(
                "explore-suggestion",
                suggestion.Value,
                $"{suggestion.Kind} · {suggestion.MatchCount:N0} matches");
            cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
            return cell;
        }

        if (indexPath.Section == 2 && HasActiveSearch)
        {
            if (_results.Count == 0)
                return DetailCell(
                    "explore-empty",
                    _loading ? "Searching the archive…" : "No matching broadcasts",
                    _loading ? Session.StatusText : "Try a broader phrase or remove a filter.");
            var result = _results[indexPath.Row];
            var context = string.IsNullOrWhiteSpace(result.Source.SearchContext)
                ? $"{result.Subtitle} · {result.Status}"
                : $"{result.Subtitle} · {result.Source.SearchContext}";
            var resultCell = DetailCell("explore-result", result.Title, context);
            resultCell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
            return resultCell;
        }

        if (indexPath.Section == 2)
        {
            var quick = new[]
            {
                ("Continue listening", "Broadcasts already in progress", RadioVaultIcon.Play),
                ("Favourites", "Broadcasts saved for an easy return", RadioVaultIcon.Favourite),
                ("Recently added", "The newest additions to the archive", RadioVaultIcon.Download),
                ("Unplayed", "Broadcasts you have not started", RadioVaultIcon.Radio),
                ("On this day", "Broadcasts from today's date", RadioVaultIcon.Completed)
            };
            var value = quick[indexPath.Row];
            var cell = new UITableViewCell(UITableViewCellStyle.Default, "explore-quick");
            var content = cell.DefaultContentConfiguration;
            content.Text = value.Item1;
            content.SecondaryText = value.Item2;
            content.Image = RadioVaultIcons.Image(value.Item3);
            RadioVaultTheme.StyleCell(cell, content);
            cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
            return cell;
        }

        if (Session.LibraryCollections.Count == 0)
            return DetailCell("explore-no-shows", "No shows found", Session.StatusText);
        var show = Session.LibraryCollections[indexPath.Row];
        var showCell = DetailCell("explore-show", show.CollectionName, $"{show.BroadcastCount:N0} broadcasts");
        showCell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return showCell;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Section == 0 && indexPath.Row < 4)
        {
            ShowFilter(indexPath);
            return;
        }
        if (indexPath.Section == 1 && indexPath.Row < _suggestions.Count)
        {
            _searchController.SearchBar.Text = _suggestions[indexPath.Row].Value;
            _searchController.SearchBar.ResignFirstResponder();
            _ = LoadAsync();
            return;
        }
        if (indexPath.Section == 2 && HasActiveSearch && indexPath.Row < _results.Count)
        {
            NavigationController?.PushViewController(
                new BroadcastDetailsViewController(Session, _results[indexPath.Row]), true);
            return;
        }
        if (indexPath.Section == 2 && !HasActiveSearch)
        {
            var filters = new[]
            {
                ("ContinueListening", "Continue Listening"),
                ("Favourites", "Favourites"),
                ("RecentlyAdded", "Recently Added"),
                ("Unplayed", "Unplayed"),
                ("OnThisDay", "On This Day")
            };
            var filter = filters[indexPath.Row];
            NavigationController?.PushViewController(
                new ShowLibraryViewController(Session, null, filter.Item2, filter.Item1), true);
            return;
        }
        if (indexPath.Section == 3 && indexPath.Row < Session.LibraryCollections.Count)
        {
            var show = Session.LibraryCollections[indexPath.Row];
            NavigationController?.PushViewController(
                new ShowLibraryViewController(Session, show.CollectionId, show.CollectionName), true);
        }
    }

    [Export("searchBarSearchButtonClicked:")]
    public void SearchButtonClicked(UISearchBar searchBar)
    {
        searchBar.ResignFirstResponder();
        _ = LoadAsync();
    }

    [Export("searchBarCancelButtonClicked:")]
    public void CancelButtonClicked(UISearchBar searchBar)
    {
        ClearFilters();
        _ = LoadAsync();
    }

    private void ShowFilter(NSIndexPath indexPath)
    {
        IEnumerable<(string Title, Action Select)> choices = indexPath.Row switch
        {
            0 => new[]
            {
                ("Everything", (Action)(() => SetScope("All", "Everything"))),
                ("Titles & summaries", (Action)(() => SetScope("TitlesAndSummaries", "Titles & summaries"))),
                ("People", (Action)(() => SetScope("People", "People"))),
                ("Topics", (Action)(() => SetScope("Topics", "Topics"))),
                ("Research", (Action)(() => SetScope("Research", "Research"))),
                ("Transcripts", (Action)(() => SetScope("Transcripts", "Transcripts")))
            },
            1 => new[]
            {
                ("Any status", (Action)(() => SetFilter("All", "Any status"))),
                ("Unplayed", (Action)(() => SetFilter("Unplayed", "Unplayed"))),
                ("In progress", (Action)(() => SetFilter("ContinueListening", "In progress"))),
                ("Completed", (Action)(() => SetFilter("Completed", "Completed"))),
                ("Favourites", (Action)(() => SetFilter("Favourites", "Favourites")))
            },
            2 => new[] { ("All shows", (Action)(() => SetShow(null, "All shows"))) }
                .Concat(Session.LibraryCollections.Select(show =>
                    (show.CollectionName, (Action)(() => SetShow(show.CollectionId, show.CollectionName))))),
            _ => new[] { ("All years", (Action)(() => SetYear(null))) }
                .Concat((_facets?.Years ?? []).Select(year =>
                    (year.ToString(), (Action)(() => SetYear(year)))))
        };

        var alert = UIAlertController.Create("Choose " + FilterTitle(indexPath.Row), null, UIAlertControllerStyle.ActionSheet);
        foreach (var choice in choices)
            alert.AddAction(UIAlertAction.Create(choice.Title, UIAlertActionStyle.Default, _ => choice.Select()));
        alert.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        if (alert.PopoverPresentationController is { } popover)
        {
            popover.SourceView = TableView;
            popover.SourceRect = TableView.RectForRowAtIndexPath(indexPath);
        }
        PresentViewController(alert, true, null);
    }

    private static string FilterTitle(nint row) => row switch
    {
        0 => "search scope",
        1 => "listening status",
        2 => "show",
        _ => "year"
    };

    private void SetScope(string value, string title) { _scope = value; _scopeTitle = title; _ = LoadAsync(); }
    private void SetFilter(string value, string title) { _filter = value; _filterTitle = title; _ = LoadAsync(); }
    private void SetShow(int? value, string title) { _collectionId = value; _showTitle = title; _ = LoadAsync(); }
    private void SetYear(int? value) { _year = value; _ = LoadAsync(); }

    private void TranscriptFilterChanged(object? sender, EventArgs eventArgs) => _ = LoadAsync();

    private void ClearFilters()
    {
        _collectionId = null;
        _showTitle = "All shows";
        _filter = "All";
        _filterTitle = "Any status";
        _year = null;
        _scope = "All";
        _scopeTitle = "Everything";
        _transcriptSwitch.On = false;
        _suggestions = [];
        _results = [];
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        BeginInvokeOnMainThread(() => TableView.ReloadData());
        var value = await Session.ExploreAsync(
            _searchController.SearchBar.Text,
            _collectionId,
            _filter,
            _year,
            _scope,
            _transcriptSwitch.On).ConfigureAwait(false);
        BeginInvokeOnMainThread(() =>
        {
            _facets = value.Facets ?? _facets;
            _suggestions = value.Suggestions;
            _results = value.Results;
            _loading = false;
            RefreshControl?.EndRefreshing();
            TableView.ReloadData();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _transcriptSwitch.ValueChanged -= TranscriptFilterChanged;
            _searchController.Dispose();
        }
        base.Dispose(disposing);
    }
}
