using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Web.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class ShowLibraryViewController : SessionTableViewController, IUISearchBarDelegate
{
    private enum ArchiveViewMode { Years, Months, Broadcasts }

    private readonly int? _collectionId;
    private readonly string _filter;
    private readonly bool _hideCompleted;
    private readonly UISearchController _searchController = new((UIViewController?)null)
    {
        ObscuresBackgroundDuringPresentation = false
    };
    private readonly UISegmentedControl _viewSelector = new(["Years", "Months", "Broadcasts"]);
    private IReadOnlyList<MobileBroadcastItem> _broadcasts = [];
    private IReadOnlyList<WebClientLibraryArchivePeriodSummary> _periods = [];
    private ArchiveViewMode _mode;
    private int? _year;
    private int? _month;
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
        _filter = filter;
        _hideCompleted = hideCompleted && !filter.Equals("Completed", StringComparison.OrdinalIgnoreCase);
        _mode = filter.Equals("All", StringComparison.OrdinalIgnoreCase)
            ? ArchiveViewMode.Years
            : ArchiveViewMode.Broadcasts;
        Title = title;
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        _searchController.SearchBar.Placeholder = $"Search {Title}";
        _searchController.SearchBar.Delegate = this;
        NavigationItem.SearchController = _searchController;
        NavigationItem.HidesSearchBarWhenScrolling = false;
        DefinesPresentationContext = true;

        if (_filter.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            _viewSelector.SelectedSegment = (nint)_mode;
            _viewSelector.SelectedSegmentTintColor = RadioVaultTheme.Accent;
            _viewSelector.SetTitleTextAttributes(
                new UIStringAttributes { ForegroundColor = RadioVaultTheme.Background },
                UIControlState.Selected);
            _viewSelector.SetTitleTextAttributes(
                new UIStringAttributes { ForegroundColor = RadioVaultTheme.MutedText },
                UIControlState.Normal);
            _viewSelector.ValueChanged += ViewSelectorChanged;
            var header = new UIView(new CoreGraphics.CGRect(0, 0, 1, 54))
            {
                BackgroundColor = RadioVaultTheme.Background
            };
            _viewSelector.TranslatesAutoresizingMaskIntoConstraints = false;
            header.AddSubview(_viewSelector);
            NSLayoutConstraint.ActivateConstraints([
                _viewSelector.LeadingAnchor.ConstraintEqualTo(header.LeadingAnchor, 16),
                _viewSelector.TrailingAnchor.ConstraintEqualTo(header.TrailingAnchor, -16),
                _viewSelector.TopAnchor.ConstraintEqualTo(header.TopAnchor, 9),
                _viewSelector.HeightAnchor.ConstraintEqualTo(36)
            ]);
            TableView.TableHeaderView = header;
        }

        RefreshControl = new UIRefreshControl();
        RefreshControl.ValueChanged += (_, _) => _ = LoadAsync(_searchController.SearchBar.Text);
        _ = LoadAsync();
    }

    public override nint NumberOfSections(UITableView tableView) => 1;
    public override nint RowsInSection(UITableView tableView, nint section)
        => Math.Max(1, _mode == ArchiveViewMode.Broadcasts ? _broadcasts.Count : _periods.Count);

    public override string? TitleForHeader(UITableView tableView, nint section) => _mode switch
    {
        ArchiveViewMode.Years => _hideCompleted ? "Years · completed hidden" : "Years",
        ArchiveViewMode.Months => $"Months in {_year}",
        _ when _year.HasValue && _month.HasValue =>
            $"Broadcasts · {new DateTime(_year.Value, _month.Value, 1):MMMM yyyy}",
        _ => _hideCompleted ? "Broadcasts · completed hidden" : "Broadcasts"
    };

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (_mode != ArchiveViewMode.Broadcasts)
        {
            if (_periods.Count == 0)
                return DetailCell("empty-period", _loading ? "Loading archive…" : "No archive periods found", Session.StatusText);
            var period = _periods[indexPath.Row];
            var cell = DetailCell(
                "archive-period",
                period.Title,
                $"{period.BroadcastCount:N0} broadcasts · {period.ProgressText} · {period.FavouriteCount:N0} favourites");
            cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
            return cell;
        }

        if (_broadcasts.Count == 0)
            return DetailCell("empty-show", _loading ? "Loading broadcasts…" : "No broadcasts found", Session.StatusText);
        var item = _broadcasts[indexPath.Row];
        var broadcastCell = DetailCell("show-broadcast", item.Title, $"{item.Subtitle} · {item.Status}");
        broadcastCell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return broadcastCell;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (_mode == ArchiveViewMode.Years && indexPath.Row < _periods.Count)
        {
            _year = _periods[indexPath.Row].Value;
            _month = null;
            SelectMode(ArchiveViewMode.Months);
            return;
        }
        if (_mode == ArchiveViewMode.Months && indexPath.Row < _periods.Count)
        {
            _month = _periods[indexPath.Row].Value;
            SelectMode(ArchiveViewMode.Broadcasts);
            return;
        }
        if (indexPath.Row < _broadcasts.Count)
            NavigationController?.PushViewController(
                new BroadcastDetailsViewController(Session, _broadcasts[indexPath.Row]), true);
    }

    [Export("searchBarSearchButtonClicked:")]
    public void SearchButtonClicked(UISearchBar searchBar)
    {
        searchBar.ResignFirstResponder();
        SelectMode(ArchiveViewMode.Broadcasts, searchBar.Text);
    }

    [Export("searchBarCancelButtonClicked:")]
    public void CancelButtonClicked(UISearchBar searchBar) => _ = LoadAsync();

    private void ViewSelectorChanged(object? sender, EventArgs eventArgs)
    {
        var requested = (ArchiveViewMode)_viewSelector.SelectedSegment;
        if (requested == ArchiveViewMode.Months && !_year.HasValue)
            _year = _mode == ArchiveViewMode.Years ? _periods.FirstOrDefault()?.Value : null;
        if (requested == ArchiveViewMode.Years)
        {
            _year = null;
            _month = null;
        }
        else if (requested == ArchiveViewMode.Months)
        {
            _month = null;
        }
        SelectMode(requested);
    }

    private void SelectMode(ArchiveViewMode mode, string? searchText = null)
    {
        _mode = mode;
        _viewSelector.SelectedSegment = (nint)mode;
        _ = LoadAsync(searchText);
    }

    private async Task LoadAsync(string? searchText = null)
    {
        if (_loading) return;
        _loading = true;
        BeginInvokeOnMainThread(() => TableView.ReloadData());
        if (_mode == ArchiveViewMode.Broadcasts)
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
            _mode == ArchiveViewMode.Months ? _year : null,
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
        if (disposing)
        {
            _viewSelector.ValueChanged -= ViewSelectorChanged;
            _searchController.Dispose();
        }
        base.Dispose(disposing);
    }
}
