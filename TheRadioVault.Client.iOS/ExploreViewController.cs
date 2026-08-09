using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class ExploreViewController : SessionTableViewController, IUISearchBarDelegate
{
    private readonly UISearchController _searchController = new((UIViewController?)null)
    {
        ObscuresBackgroundDuringPresentation = false
    };
    private MobileExploreDashboard? _dashboard;
    private IReadOnlyList<MobileWikiPageSummary> _visiblePages = [];
    private bool _browseAll;
    private bool _loading;

    public ExploreViewController(MobileClientSession session) : base(session)
    {
        Title = "Explore";
        NavigationItem.Title = "Explore";
    }

    private bool IsBrowseMode => _browseAll || !string.IsNullOrWhiteSpace(_searchController.SearchBar.Text);

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Always;
        NavigationController!.NavigationBar.PrefersLargeTitles = true;
        _searchController.SearchBar.Placeholder = "Search people, shows, places, events or articles";
        _searchController.SearchBar.Delegate = this;
        NavigationItem.SearchController = _searchController;
        NavigationItem.HidesSearchBarWhenScrolling = false;
        NavigationItem.RightBarButtonItem = new UIBarButtonItem(
            "Browse All",
            UIBarButtonItemStyle.Plain,
            (_, _) => ToggleBrowseAll());
        DefinesPresentationContext = true;
        RefreshControl = new UIRefreshControl();
        RefreshControl.ValueChanged += (_, _) => _ = LoadAsync();
        _ = LoadAsync();
    }

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        if (_dashboard is null && !_loading && !Session.IsBusy) _ = LoadAsync();
    }

    public override nint NumberOfSections(UITableView tableView)
    {
        if (IsBrowseMode) return 1;
        return 10;
    }

    public override nint RowsInSection(UITableView tableView, nint section)
    {
        if (IsBrowseMode) return Math.Max(1, _visiblePages.Count);
        if (_dashboard is null) return section == 0 ? 1 : 0;
        return section switch
        {
            0 => 1,
            1 => 4,
            2 => _dashboard.Highlights.OnThisDay.Count,
            3 => _dashboard.FeaturedPages.Count,
            4 => _dashboard.RecentPages.Count,
            5 => _dashboard.ShowPages.Count,
            6 => _dashboard.PeoplePages.Count,
            7 => _dashboard.TopicPages.Count,
            8 => _dashboard.Highlights.Eras.Count,
            9 => _dashboard.TimelinePages.Count,
            _ => 0
        };
    }

    public override string? TitleForHeader(UITableView tableView, nint section)
    {
        if (IsBrowseMode) return _browseAll && string.IsNullOrWhiteSpace(_searchController.SearchBar.Text)
            ? "All Explore articles"
            : "Search results";
        return section switch
        {
            1 => "The Explore archive",
            2 => "On this date",
            3 => "Featured starting points",
            4 => "Recently updated",
            5 => "Shows",
            6 => "People",
            7 => "Topics and stories",
            8 => "Explore by era",
            9 => "Travel through the timelines",
            _ => null
        };
    }

    public override string? TitleForFooter(UITableView tableView, nint section)
        => !IsBrowseMode && section == 0
            ? "A human-editable history of the archive, grounded in citations, dated images and broadcasts."
            : null;

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (IsBrowseMode)
        {
            if (_visiblePages.Count == 0)
                return DetailCell(
                    "explore-empty",
                    _loading ? "Loading Explore…" : "No matching articles",
                    _loading ? Session.StatusText : "Try another person, show, place, event or phrase.");
            return PageCell("explore-browse", _visiblePages[indexPath.Row]);
        }

        if (_dashboard is null)
            return HeroCell(
                _loading ? "Loading the story of the archive…" : "Explore is unavailable",
                Session.StatusText);

        if (indexPath.Section == 0)
        {
            var overview = _dashboard.Overview;
            return HeroCell(
                "Explore the stories behind the archive",
                $"Shows, people, stories and turning points—connected directly to the broadcasts that preserve them.\n\nExplore {overview.PageCount:N0} articles, {overview.TimelineEventCount:N0} dated events, {overview.SourceCount:N0} sources and {overview.ImageCount:N0} historical images.");
        }

        if (indexPath.Section == 1)
        {
            var overview = _dashboard.Overview;
            var stats = new[]
            {
                ("Articles", overview.PageCount),
                ("Dated events", overview.TimelineEventCount),
                ("Sources", overview.SourceCount),
                ("Images", overview.ImageCount)
            };
            var stat = stats[indexPath.Row];
            var cell = DetailCell("explore-stat", stat.Item1, stat.Item2.ToString("N0"));
            cell.SelectionStyle = UITableViewCellSelectionStyle.None;
            return cell;
        }

        if (indexPath.Section == 2)
        {
            var item = _dashboard.Highlights.OnThisDay[indexPath.Row];
            return DetailCell(
                "explore-today",
                $"{item.Event.YearText} · {item.Event.Title}",
                string.IsNullOrWhiteSpace(item.Event.Summary) ? item.Page.Title : item.Event.Summary);
        }

        if (indexPath.Section is >= 3 and <= 7 or 9)
            return PageCell("explore-page", PagesForSection(indexPath.Section)[indexPath.Row]);

        var era = _dashboard.Highlights.Eras[indexPath.Row];
        var eraCell = DetailCell("explore-era", era.Label, era.Summary);
        eraCell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return eraCell;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (IsBrowseMode)
        {
            if (indexPath.Row < _visiblePages.Count) OpenPage(_visiblePages[indexPath.Row]);
            return;
        }
        if (_dashboard is null || indexPath.Section < 2) return;
        if (indexPath.Section == 2)
        {
            OpenPage(_dashboard.Highlights.OnThisDay[indexPath.Row].Page);
            return;
        }
        if (indexPath.Section is >= 3 and <= 7 or 9)
        {
            OpenPage(PagesForSection(indexPath.Section)[indexPath.Row]);
            return;
        }
        if (indexPath.Section == 8)
        {
            var era = _dashboard.Highlights.Eras[indexPath.Row];
            _browseAll = true;
            _visiblePages = _dashboard.TimelinePages;
            _searchController.SearchBar.Text = string.Empty;
            NavigationItem.RightBarButtonItem!.Title = "Dashboard";
            Title = era.Label;
            NavigationItem.Title = era.Label;
            TableView.ReloadData();
        }
    }

    [Export("searchBar:searchTextDidChange:")]
    public void TextChanged(UISearchBar searchBar, string searchText)
    {
        ApplySearch(searchText);
    }

    [Export("searchBarSearchButtonClicked:")]
    public void SearchButtonClicked(UISearchBar searchBar)
    {
        searchBar.ResignFirstResponder();
        ApplySearch(searchBar.Text);
    }

    [Export("searchBarCancelButtonClicked:")]
    public void CancelButtonClicked(UISearchBar searchBar)
    {
        searchBar.Text = string.Empty;
        _browseAll = false;
        ShowDashboard();
    }

    private void ToggleBrowseAll()
    {
        if (IsBrowseMode)
        {
            _searchController.SearchBar.Text = string.Empty;
            _browseAll = false;
            ShowDashboard();
            return;
        }
        _browseAll = true;
        _visiblePages = _dashboard?.AllPages ?? [];
        NavigationItem.RightBarButtonItem!.Title = "Dashboard";
        TableView.ReloadData();
    }

    private void ShowDashboard()
    {
        Title = "Explore";
        NavigationItem.Title = "Explore";
        NavigationItem.RightBarButtonItem!.Title = "Browse All";
        _visiblePages = [];
        TableView.ReloadData();
    }

    private void ApplySearch(string? text)
    {
        var query = text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            if (_browseAll) _visiblePages = _dashboard?.AllPages ?? [];
            else ShowDashboard();
        }
        else
        {
            _visiblePages = (_dashboard?.AllPages ?? [])
                .Where(page => Contains(page.Title, query) || Contains(page.Summary, query) ||
                               Contains(page.PageType, query) || Contains(page.Slug, query))
                .OrderByDescending(page => page.Title.Equals(query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(page => page.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            NavigationItem.RightBarButtonItem!.Title = "Dashboard";
        }
        TableView.ReloadData();
    }

    private static bool Contains(string? value, string query)
        => value?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true;

    private IReadOnlyList<MobileWikiPageSummary> PagesForSection(nint section)
        => _dashboard is null ? [] : section switch
        {
            3 => _dashboard.FeaturedPages,
            4 => _dashboard.RecentPages,
            5 => _dashboard.ShowPages,
            6 => _dashboard.PeoplePages,
            7 => _dashboard.TopicPages,
            9 => _dashboard.TimelinePages,
            _ => []
        };

    private void OpenPage(MobileWikiPageSummary page)
        => NavigationController?.PushViewController(new ExploreArticleViewController(Session, page), true);

    private static UITableViewCell PageCell(string identifier, MobileWikiPageSummary page)
    {
        var cell = DetailCell(identifier, page.Title,
            string.IsNullOrWhiteSpace(page.Summary)
                ? $"{page.PageType} · {page.EvidenceSummary}"
                : $"{page.Summary}\n{page.PageType} · {page.EvidenceSummary}");
        cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return cell;
    }

    private static UITableViewCell HeroCell(string title, string detail)
    {
        var cell = new UITableViewCell(UITableViewCellStyle.Default, "explore-hero");
        var content = cell.DefaultContentConfiguration;
        content.Text = title;
        content.SecondaryText = detail;
        content.TextProperties.Font = UIFont.BoldSystemFontOfSize(22)!;
        content.TextProperties.Color = RadioVaultTheme.Text;
        content.SecondaryTextProperties.Font = UIFont.SystemFontOfSize(13)!;
        content.SecondaryTextProperties.Color = RadioVaultTheme.MutedText;
        content.SecondaryTextProperties.NumberOfLines = 0;
        content.Image = RadioVaultIcons.Image(RadioVaultIcon.Knowledge, size: 42);
        RadioVaultTheme.StyleCell(cell, content);
        cell.BackgroundColor = RadioVaultTheme.SurfaceRaised;
        cell.SelectionStyle = UITableViewCellSelectionStyle.None;
        return cell;
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        TableView.ReloadData();
        var dashboard = await Session.LoadExploreDashboardAsync().ConfigureAwait(false);
        BeginInvokeOnMainThread(() =>
        {
            _dashboard = dashboard ?? _dashboard;
            if (_browseAll) _visiblePages = _dashboard?.AllPages ?? [];
            _loading = false;
            RefreshControl?.EndRefreshing();
            TableView.ReloadData();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _searchController.Dispose();
        base.Dispose(disposing);
    }
}
