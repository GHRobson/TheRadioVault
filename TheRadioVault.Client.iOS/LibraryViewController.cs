using Foundation;
using TheRadioVault.Client.Mobile;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class LibraryViewController : SessionTableViewController, IUISearchResultsUpdating, IUISearchBarDelegate
{
    private UISearchController? _searchController;

    public LibraryViewController(MobileClientSession session) : base(session) => Title = "Library";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Always;
        _searchController = new UISearchController((UIViewController?)null)
        {
            ObscuresBackgroundDuringPresentation = false,
            SearchResultsUpdater = this
        };
        _searchController.SearchBar.Placeholder = "Search broadcasts";
        _searchController.SearchBar.Delegate = this;
        NavigationItem.SearchController = _searchController;
        NavigationItem.HidesSearchBarWhenScrolling = false;
        DefinesPresentationContext = true;
        RefreshControl = new UIRefreshControl();
        RefreshControl.ValueChanged += async (_, _) =>
        {
            await Session.SearchAsync(_searchController?.SearchBar.Text ?? string.Empty);
            BeginInvokeOnMainThread(() => RefreshControl?.EndRefreshing());
        };
    }

    public override nint NumberOfSections(UITableView tableView) => 1;
    public override nint RowsInSection(UITableView tableView, nint section) => Math.Max(1, Session.LibraryBroadcasts.Count);

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (Session.LibraryBroadcasts.Count == 0)
            return DetailCell("empty-library", Session.IsPaired ? "No broadcasts found" : "Pair a server first", Session.StatusText);
        var item = Session.LibraryBroadcasts[indexPath.Row];
        var cell = DetailCell("library-broadcast", item.Title, $"{item.Subtitle} · {item.Status}");
        cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return cell;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Row < Session.LibraryBroadcasts.Count)
            _ = Session.PlayAsync(Session.LibraryBroadcasts[indexPath.Row]);
    }

    public void UpdateSearchResultsForSearchController(UISearchController searchController) { }

    [Export("searchBarSearchButtonClicked:")]
    public void SearchButtonClicked(UISearchBar searchBar)
    {
        searchBar.ResignFirstResponder();
        _ = Session.SearchAsync(searchBar.Text ?? string.Empty);
    }

    [Export("searchBarCancelButtonClicked:")]
    public void CancelButtonClicked(UISearchBar searchBar) => _ = Session.SearchAsync(string.Empty);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _searchController?.Dispose();
            _searchController = null;
        }
        base.Dispose(disposing);
    }
}
