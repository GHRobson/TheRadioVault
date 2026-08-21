using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class ExploreViewController : SessionTableViewController, IUISearchBarDelegate
{
    private readonly ExploreControlsHeaderView _header = new();
    private MobileExploreDashboard? _dashboard;
    private IReadOnlyList<MobileWikiPageSummary> _visiblePages = [];
    private bool _browseAll;
    private bool _loading;

    public ExploreViewController(MobileClientSession session) : base(session)
    {
        Title = "Explore";
    }

    private bool IsBrowseMode => _browseAll || !string.IsNullOrWhiteSpace(_header.SearchBar.Text);
    private IReadOnlyList<ExploreDashboardSection> DashboardSections
    {
        get
        {
            var result = new List<ExploreDashboardSection> { ExploreDashboardSection.Hero };
            if (_dashboard is not { } dashboard) return result;
            if (dashboard.TimelinePages.Count > 0) result.Add(ExploreDashboardSection.Timeline);
            if (dashboard.FeaturedPages.Count > 0) result.Add(ExploreDashboardSection.Featured);
            if (dashboard.Gallery.Count > 0) result.Add(ExploreDashboardSection.Gallery);
            if (dashboard.Highlights.OnThisDay.Count > 0) result.Add(ExploreDashboardSection.OnThisDate);
            if (dashboard.ShowPages.Count > 0) result.Add(ExploreDashboardSection.Shows);
            if (dashboard.PeoplePages.Count > 0) result.Add(ExploreDashboardSection.People);
            if (dashboard.TopicPages.Count > 0) result.Add(ExploreDashboardSection.Topics);
            if (dashboard.Highlights.Eras.Count > 0) result.Add(ExploreDashboardSection.Eras);
            if (dashboard.RecentPages.Count > 0) result.Add(ExploreDashboardSection.Recent);
            return result;
        }
    }
    protected override string? PageHeading => "Explore";
    protected override string PageDescription => "Stories behind your broadcasts.";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        _header.SearchBar.Placeholder = "Search Explore";
        _header.SearchBar.Delegate = this;
        _header.BrowseButton.TouchUpInside += BrowseButtonTapped;
        _header.ClearRequested += HeaderClearRequested;
        TableView.TableHeaderView = _header;
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
        return DashboardSections.Count;
    }

    public override nint RowsInSection(UITableView tableView, nint section)
    {
        if (IsBrowseMode) return Math.Max(1, _visiblePages.Count);
        if (section < 0 || section >= DashboardSections.Count) return 0;
        return DashboardSections[(int)section] switch
        {
            ExploreDashboardSection.Hero => 1,
            ExploreDashboardSection.Timeline => 1,
            ExploreDashboardSection.Featured => _dashboard?.FeaturedPages.Count ?? 0,
            ExploreDashboardSection.Gallery => 1,
            ExploreDashboardSection.OnThisDate => _dashboard?.Highlights.OnThisDay.Count ?? 0,
            ExploreDashboardSection.Shows => _dashboard?.ShowPages.Count ?? 0,
            ExploreDashboardSection.People => _dashboard?.PeoplePages.Count ?? 0,
            ExploreDashboardSection.Topics => _dashboard?.TopicPages.Count ?? 0,
            ExploreDashboardSection.Eras => _dashboard?.Highlights.Eras.Count ?? 0,
            ExploreDashboardSection.Recent => _dashboard?.RecentPages.Count ?? 0,
            _ => 0
        };
    }

    public override string? TitleForHeader(UITableView tableView, nint section)
    {
        if (IsBrowseMode) return _browseAll && string.IsNullOrWhiteSpace(_header.SearchBar.Text)
            ? "All Explore articles"
            : "Search results";
        if (section < 0 || section >= DashboardSections.Count) return null;
        return DashboardSections[(int)section] switch
        {
            ExploreDashboardSection.Timeline => "Show timelines",
            ExploreDashboardSection.Featured => "Featured articles",
            ExploreDashboardSection.Gallery => "Images from the archive",
            ExploreDashboardSection.OnThisDate => "On this date",
            ExploreDashboardSection.Shows => "Shows",
            ExploreDashboardSection.People => "People",
            ExploreDashboardSection.Topics => "Topics and stories",
            ExploreDashboardSection.Eras => "Explore by era",
            ExploreDashboardSection.Recent => "Recently updated",
            _ => null
        };
    }

    public override string? TitleForFooter(UITableView tableView, nint section) => null;

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

        var section = DashboardSections[(int)indexPath.Section];
        if (_dashboard is null)
            return HeroCell(
                _loading ? "Loading the story of the archive…" : "Explore is unavailable",
                Session.StatusText);

        if (section == ExploreDashboardSection.Hero)
        {
            var hero = new ExploreDashboardHeroCell();
            hero.Configure(_dashboard.Overview, _dashboard.Gallery.FirstOrDefault());
            return hero;
        }

        if (section == ExploreDashboardSection.Timeline)
            return new ExploreTimelinePromoCell(
                _dashboard.TimelinePages.Count,
                _dashboard.TimelinePages.Sum(value => value.TimelineEventCount));

        if (section == ExploreDashboardSection.Featured)
            return PageCell("explore-featured", _dashboard.FeaturedPages[indexPath.Row]);

        if (section == ExploreDashboardSection.Gallery)
        {
            var gallery = new ExploreImageGalleryCell();
            gallery.Configure(_dashboard.Gallery, OpenPageById);
            return gallery;
        }

        if (section == ExploreDashboardSection.OnThisDate)
        {
            var item = _dashboard.Highlights.OnThisDay[indexPath.Row];
            var timeline = new ExploreTimelineEventCell();
            timeline.Configure(item.Event);
            return timeline;
        }

        if (section is ExploreDashboardSection.Shows or ExploreDashboardSection.People or
            ExploreDashboardSection.Topics or ExploreDashboardSection.Recent)
            return PageCell("explore-page", PagesForSection(section)[indexPath.Row]);

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
        if (_dashboard is null) return;
        var section = DashboardSections[(int)indexPath.Section];
        if (section == ExploreDashboardSection.Hero) return;
        if (section == ExploreDashboardSection.Timeline)
        {
            NavigationController?.PushViewController(
                new ExploreTimelineViewController(Session, _dashboard.TimelinePages), true);
            return;
        }
        if (section == ExploreDashboardSection.Featured)
        {
            OpenPage(_dashboard.FeaturedPages[indexPath.Row]);
            return;
        }
        if (section == ExploreDashboardSection.Gallery) return;
        if (section == ExploreDashboardSection.OnThisDate)
        {
            OpenPage(_dashboard.Highlights.OnThisDay[indexPath.Row].Page);
            return;
        }
        if (section is ExploreDashboardSection.Shows or ExploreDashboardSection.People or
            ExploreDashboardSection.Topics or ExploreDashboardSection.Recent)
        {
            OpenPage(PagesForSection(section)[indexPath.Row]);
            return;
        }
        if (section == ExploreDashboardSection.Eras)
        {
            _browseAll = true;
            _visiblePages = _dashboard.TimelinePages;
            _header.SearchBar.Text = string.Empty;
            _header.SetBrowseMode(browsing: true);
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
            _header.SearchBar.Text = string.Empty;
            _browseAll = false;
            ShowDashboard();
            return;
        }
        _browseAll = true;
        _visiblePages = _dashboard?.AllPages ?? [];
        _header.SetBrowseMode(browsing: true);
        TableView.ReloadData();
    }

    private void ShowDashboard()
    {
        Title = "Explore";
        _header.SetBrowseMode(browsing: false);
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
            _header.SetBrowseMode(browsing: true);
        }
        TableView.ReloadData();
    }

    private void BrowseButtonTapped(object? sender, EventArgs eventArgs) => ToggleBrowseAll();

    private void HeaderClearRequested(object? sender, EventArgs eventArgs)
    {
        _browseAll = false;
        ApplySearch(string.Empty);
    }

    private static bool Contains(string? value, string query)
        => value?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true;

    private IReadOnlyList<MobileWikiPageSummary> PagesForSection(ExploreDashboardSection section)
        => _dashboard is null ? [] : section switch
        {
            ExploreDashboardSection.Shows => _dashboard.ShowPages,
            ExploreDashboardSection.People => _dashboard.PeoplePages,
            ExploreDashboardSection.Topics => _dashboard.TopicPages,
            ExploreDashboardSection.Recent => _dashboard.RecentPages,
            _ => []
        };

    private void OpenPage(MobileWikiPageSummary page)
        => NavigationController?.PushViewController(new ExploreArticleViewController(Session, page), true);

    private void OpenPageById(Guid pageId)
    {
        var page = _dashboard?.AllPages.FirstOrDefault(value => value.PageId == pageId);
        if (page is not null) OpenPage(page);
    }

    private UITableViewCell PageCell(string identifier, MobileWikiPageSummary page)
    {
        var cell = new ExplorePageCardCell(identifier);
        cell.Configure(page, _dashboard?.Gallery.FirstOrDefault(value => value.PageId == page.PageId));
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
        content.Image = RadioVaultIcons.Image(RadioVaultIcon.Explore, size: 42);
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

    protected override void ReloadSession()
    {
        // Explore data changes through its cache coordinator and explicit
        // refresh path. Playback/sync notifications arrive far more often and
        // used to recreate image and timeline cells, causing visible flashing
        // and losing the reader's position.
        if (_dashboard is null && !_loading && !Session.IsBusy) _ = LoadAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _header.BrowseButton.TouchUpInside -= BrowseButtonTapped;
            _header.ClearRequested -= HeaderClearRequested;
            _header.Dispose();
        }
        base.Dispose(disposing);
    }

    private enum ExploreDashboardSection
    {
        Hero,
        Timeline,
        Featured,
        Gallery,
        OnThisDate,
        Shows,
        People,
        Topics,
        Eras,
        Recent
    }
}
