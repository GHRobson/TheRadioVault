using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class ShowLibraryViewController : SessionTableViewController, IUISearchBarDelegate
{
    private readonly int? _collectionId;
    private readonly UISearchController _searchController = new((UIViewController?)null)
    {
        ObscuresBackgroundDuringPresentation = false
    };
    private IReadOnlyList<MobileBroadcastItem> _broadcasts = [];
    private bool _loading;

    public ShowLibraryViewController(MobileClientSession session, int? collectionId, string title)
        : base(session)
    {
        _collectionId = collectionId;
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
        RefreshControl = new UIRefreshControl();
        RefreshControl.ValueChanged += (_, _) => _ = LoadAsync(_searchController.SearchBar.Text);
        _ = LoadAsync();
    }

    public override nint NumberOfSections(UITableView tableView) => 1;
    public override nint RowsInSection(UITableView tableView, nint section) => Math.Max(1, _broadcasts.Count);

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (_broadcasts.Count == 0)
            return DetailCell("empty-show", _loading ? "Loading broadcasts…" : "No broadcasts found", Session.StatusText);
        var item = _broadcasts[indexPath.Row];
        var cell = DetailCell("show-broadcast", item.Title, $"{item.Subtitle} · {item.Status}");
        cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return cell;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Row < _broadcasts.Count)
            NavigationController?.PushViewController(
                new BroadcastDetailsViewController(Session, _broadcasts[indexPath.Row]), true);
    }

    [Export("searchBarSearchButtonClicked:")]
    public void SearchButtonClicked(UISearchBar searchBar)
    {
        searchBar.ResignFirstResponder();
        _ = LoadAsync(searchBar.Text);
    }

    [Export("searchBarCancelButtonClicked:")]
    public void CancelButtonClicked(UISearchBar searchBar) => _ = LoadAsync();

    private async Task LoadAsync(string? searchText = null)
    {
        if (_loading) return;
        _loading = true;
        BeginInvokeOnMainThread(() => TableView.ReloadData());
        var values = await Session.BrowseCollectionAsync(_collectionId, searchText).ConfigureAwait(false);
        BeginInvokeOnMainThread(() =>
        {
            _broadcasts = values;
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
