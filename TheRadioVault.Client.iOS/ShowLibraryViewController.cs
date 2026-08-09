using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Web.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class ShowLibraryViewController : SessionTableViewController, IUISearchBarDelegate
{
    private enum ArchiveLevel { Years, Months, Broadcasts }

    private readonly int? _collectionId;
    private readonly string _rootTitle;
    private readonly string _filter;
    private readonly UISearchController _searchController = new((UIViewController?)null)
    {
        ObscuresBackgroundDuringPresentation = false
    };
    private IReadOnlyList<MobileBroadcastItem> _broadcasts = [];
    private IReadOnlyList<WebClientLibraryArchivePeriodSummary> _periods = [];
    private ArchiveLevel _level;
    private int? _year;
    private int? _month;
    private string? _searchText;
    private bool _hideCompleted;
    private bool _loading;

    public ShowLibraryViewController(
        MobileClientSession session,
        int? collectionId,
        string title,
        string filter = "All",
        bool hideCompleted = false)
        : base(session)
    {
        _collectionId = collectionId;
        _rootTitle = title;
        _filter = filter;
        _hideCompleted = hideCompleted && !filter.Equals("Completed", StringComparison.OrdinalIgnoreCase);
        _level = filter.Equals("All", StringComparison.OrdinalIgnoreCase)
            ? ArchiveLevel.Years
            : ArchiveLevel.Broadcasts;
        Title = title;
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        TableView.RowHeight = UITableView.AutomaticDimension;
        TableView.EstimatedRowHeight = 96;
        _searchController.SearchBar.Placeholder = $"Search {_rootTitle}";
        _searchController.SearchBar.Delegate = this;
        NavigationItem.SearchController = _searchController;
        NavigationItem.HidesSearchBarWhenScrolling = false;
        DefinesPresentationContext = true;
        NavigationItem.RightBarButtonItem = new UIBarButtonItem(
            RadioVaultIcons.Image(RadioVaultIcon.Completed),
            UIBarButtonItemStyle.Plain,
            (_, _) => ToggleHideCompleted());
        UpdateHideCompletedButton();

        RefreshControl = new UIRefreshControl();
        RefreshControl.ValueChanged += (_, _) => _ = LoadAsync(_searchText);
        UpdateNavigation();
        _ = LoadAsync();
    }

    public override nint NumberOfSections(UITableView tableView) => 1;

    public override nint RowsInSection(UITableView tableView, nint section)
    {
        if (_level == ArchiveLevel.Broadcasts) return Math.Max(1, _broadcasts.Count);
        return Math.Max(1, (int)Math.Ceiling(_periods.Count / 2d));
    }

    public override string? TitleForHeader(UITableView tableView, nint section) => _level switch
    {
        ArchiveLevel.Years => _hideCompleted ? "Browse by year · completed hidden" : "Browse by year",
        ArchiveLevel.Months => $"{_year} · choose a month",
        _ when _year.HasValue && _month.HasValue =>
            $"{new DateTime(_year.Value, _month.Value, 1):MMMM yyyy} · broadcasts",
        _ => _hideCompleted ? "Broadcasts · completed hidden" : "Broadcasts"
    };

    public override string? TitleForFooter(UITableView tableView, nint section)
        => _level == ArchiveLevel.Years
            ? "Choose a year, then a month, to reach its broadcasts. Press and hold any broadcast for playback, queue, favourite and download actions."
            : null;

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (_level != ArchiveLevel.Broadcasts)
        {
            if (_periods.Count == 0)
                return DetailCell("empty-period", _loading ? "Loading archive…" : "No archive periods found", Session.StatusText);
            var start = (int)indexPath.Row * 2;
            var leading = _periods[start];
            var trailing = start + 1 < _periods.Count ? _periods[start + 1] : null;
            var cell = new ArchiveGridRowCell();
            cell.Configure(leading, trailing, OpenPeriod);
            return cell;
        }

        if (_broadcasts.Count == 0)
            return DetailCell("empty-show", _loading ? "Loading broadcasts…" : "No broadcasts found", Session.StatusText);
        var item = _broadcasts[indexPath.Row];
        var broadcastCell = new BroadcastProgressCell("show-broadcast");
        broadcastCell.Configure(item);
        broadcastCell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return broadcastCell;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (_level == ArchiveLevel.Broadcasts && indexPath.Row < _broadcasts.Count)
            NavigationController?.PushViewController(
                new BroadcastDetailsViewController(Session, _broadcasts[indexPath.Row]), true);
    }

    protected override MobileBroadcastItem? ContextBroadcastForRow(NSIndexPath indexPath)
        => _level == ArchiveLevel.Broadcasts && indexPath.Row < _broadcasts.Count
            ? _broadcasts[indexPath.Row]
            : null;

    [Export("searchBarSearchButtonClicked:")]
    public void SearchButtonClicked(UISearchBar searchBar)
    {
        searchBar.ResignFirstResponder();
        _searchText = searchBar.Text?.Trim();
        if (string.IsNullOrWhiteSpace(_searchText)) return;
        _year = null;
        _month = null;
        _level = ArchiveLevel.Broadcasts;
        UpdateNavigation();
        _ = LoadAsync(_searchText);
    }

    [Export("searchBarCancelButtonClicked:")]
    public void CancelButtonClicked(UISearchBar searchBar)
    {
        _searchText = null;
        _year = null;
        _month = null;
        _level = _filter.Equals("All", StringComparison.OrdinalIgnoreCase)
            ? ArchiveLevel.Years
            : ArchiveLevel.Broadcasts;
        UpdateNavigation();
        _ = LoadAsync();
    }

    private void OpenPeriod(WebClientLibraryArchivePeriodSummary period)
    {
        if (_level == ArchiveLevel.Years)
        {
            _year = period.Value;
            _month = null;
            _level = ArchiveLevel.Months;
        }
        else if (_level == ArchiveLevel.Months)
        {
            _month = period.Value;
            _level = ArchiveLevel.Broadcasts;
        }
        UpdateNavigation();
        _ = LoadAsync();
    }

    private void NavigateUp()
    {
        _searchController.SearchBar.Text = string.Empty;
        _searchText = null;
        if (_level == ArchiveLevel.Broadcasts && _year.HasValue)
        {
            _month = null;
            _level = ArchiveLevel.Months;
        }
        else
        {
            _year = null;
            _month = null;
            _level = ArchiveLevel.Years;
        }
        UpdateNavigation();
        _ = LoadAsync();
    }

    private void UpdateNavigation()
    {
        Title = _level switch
        {
            ArchiveLevel.Months when _year.HasValue => _year.Value.ToString(),
            ArchiveLevel.Broadcasts when _year.HasValue && _month.HasValue =>
                new DateTime(_year.Value, _month.Value, 1).ToString("MMMM yyyy"),
            _ => _rootTitle
        };
        NavigationItem.Title = Title;
        NavigationItem.LeftBarButtonItem = _level switch
        {
            ArchiveLevel.Months => new UIBarButtonItem("Years", UIBarButtonItemStyle.Plain, (_, _) => NavigateUp()),
            ArchiveLevel.Broadcasts when _year.HasValue =>
                new UIBarButtonItem("Months", UIBarButtonItemStyle.Plain, (_, _) => NavigateUp()),
            _ => null
        };
    }

    private void ToggleHideCompleted()
    {
        if (_filter.Equals("Completed", StringComparison.OrdinalIgnoreCase)) return;
        _hideCompleted = !_hideCompleted;
        UpdateHideCompletedButton();
        _ = LoadAsync(_searchText);
    }

    private void UpdateHideCompletedButton()
    {
        var button = NavigationItem.RightBarButtonItem;
        if (button is null) return;
        button.Enabled = !_filter.Equals("Completed", StringComparison.OrdinalIgnoreCase);
        button.Image = RadioVaultIcons.Image(
            RadioVaultIcon.Completed,
            _hideCompleted ? RadioVaultTheme.Accent : RadioVaultTheme.MutedText);
        button.AccessibilityLabel = _hideCompleted ? "Show completed broadcasts" : "Hide completed broadcasts";
        button.AccessibilityValue = _hideCompleted ? "Completed broadcasts hidden" : "Completed broadcasts shown";
    }

    private async Task LoadAsync(string? searchText = null)
    {
        if (_loading) return;
        _loading = true;
        BeginInvokeOnMainThread(() => TableView.ReloadData());
        if (_level == ArchiveLevel.Broadcasts)
        {
            var values = await Session.BrowseCollectionAsync(
                _collectionId,
                searchText,
                _filter,
                _year,
                _month,
                _hideCompleted).ConfigureAwait(false);
            BeginInvokeOnMainThread(() =>
            {
                _broadcasts = values;
                FinishLoad();
            });
            return;
        }

        var periods = await Session.LoadArchivePeriodsAsync(
            _collectionId,
            _level == ArchiveLevel.Months ? _year : null,
            _hideCompleted).ConfigureAwait(false);
        BeginInvokeOnMainThread(() =>
        {
            _periods = periods;
            FinishLoad();
        });
    }

    private void FinishLoad()
    {
        _loading = false;
        RefreshControl?.EndRefreshing();
        TableView.ReloadData();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _searchController.Dispose();
        base.Dispose(disposing);
    }
}
