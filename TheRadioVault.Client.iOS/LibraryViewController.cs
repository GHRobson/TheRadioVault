using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class LibraryViewController : SessionTableViewController, IUISearchResultsUpdating, IUISearchBarDelegate
{
    private UISearchController? _searchController;
    private bool _hideCompleted;
    private bool IsShowingSearchResults => !string.IsNullOrWhiteSpace(_searchController?.SearchBar.Text);

    public LibraryViewController(MobileClientSession session) : base(session) => Title = "Library";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Always;
        TableView.RowHeight = UITableView.AutomaticDimension;
        TableView.EstimatedRowHeight = 74;
        _searchController = new UISearchController((UIViewController?)null)
        {
            ObscuresBackgroundDuringPresentation = false,
            SearchResultsUpdater = this
        };
        _searchController.SearchBar.Placeholder = "Search broadcasts";
        _searchController.SearchBar.Delegate = this;
        NavigationItem.SearchController = _searchController;
        NavigationItem.HidesSearchBarWhenScrolling = false;
        NavigationItem.RightBarButtonItem = new UIBarButtonItem(
            RadioVaultIcons.Image(RadioVaultIcon.Completed),
            UIBarButtonItemStyle.Plain,
            (_, _) => ToggleHideCompleted());
        UpdateHideCompletedButton();
        DefinesPresentationContext = true;
        RefreshControl = new UIRefreshControl();
        RefreshControl.ValueChanged += async (_, _) =>
        {
            await Session.SearchAsync(_searchController?.SearchBar.Text ?? string.Empty, _hideCompleted);
            BeginInvokeOnMainThread(() => RefreshControl?.EndRefreshing());
        };
    }

    public override nint NumberOfSections(UITableView tableView) => IsShowingSearchResults ? 1 : 3;

    public override nint RowsInSection(UITableView tableView, nint section)
    {
        if (IsShowingSearchResults) return Math.Max(1, Session.LibraryBroadcasts.Count);
        return section switch
        {
            0 => 6,
            1 => 1,
            _ => Math.Max(1, Session.LibraryCollections.Count)
        };
    }

    public override string? TitleForHeader(UITableView tableView, nint section)
        => IsShowingSearchResults ? "Search results" : section switch
        {
            0 => "Your Library",
            1 => "Browse",
            _ => "Shows"
        };

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (IsShowingSearchResults)
        {
            if (Session.LibraryBroadcasts.Count == 0)
                return DetailCell("empty-library", Session.IsPaired ? "No broadcasts found" : "Pair a server first", Session.StatusText);
            var item = Session.LibraryBroadcasts[indexPath.Row];
            var resultCell = new BroadcastProgressCell("library-broadcast");
            resultCell.Configure(item);
            resultCell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
            return resultCell;
        }

        if (indexPath.Section == 0)
        {
            var unplayed = Math.Max(0, Session.TotalBroadcasts - Session.CompletedBroadcasts - Session.InProgressBroadcasts);
            var values = new[]
            {
                ("Up Next", $"{Session.QueueItems.Count:N0} queued", RadioVaultIcon.UpNext),
                ("Favourites", $"{Session.FavouriteBroadcasts:N0} broadcasts", RadioVaultIcon.Favourite),
                ("Continue Listening", $"{Session.InProgressBroadcasts:N0} broadcasts", RadioVaultIcon.Play),
                ("Recently Added", "Newest broadcasts", RadioVaultIcon.Download),
                ("Unplayed", $"{unplayed:N0} broadcasts", RadioVaultIcon.Radio),
                ("Completed", $"{Session.CompletedBroadcasts:N0} broadcasts", RadioVaultIcon.Completed)
            };
            var value = values[indexPath.Row];
            var smart = new UITableViewCell(UITableViewCellStyle.Default, "smart-library");
            var content = smart.DefaultContentConfiguration;
            content.Text = value.Item1;
            content.SecondaryText = value.Item2;
            content.Image = RadioVaultIcons.Image(value.Item3);
            RadioVaultTheme.StyleCell(smart, content);
            smart.Accessory = UITableViewCellAccessory.DisclosureIndicator;
            return smart;
        }

        if (indexPath.Section == 1)
        {
            var all = DetailCell("all-broadcasts", "All Broadcasts", $"{Session.TotalBroadcasts:N0} broadcasts");
            all.Accessory = UITableViewCellAccessory.DisclosureIndicator;
            return all;
        }

        if (Session.LibraryCollections.Count == 0)
            return DetailCell("empty-shows", Session.IsPaired ? "No shows found" : "Pair a server first", Session.StatusText);
        var show = Session.LibraryCollections[indexPath.Row];
        var cell = DetailCell("library-show", show.CollectionName, $"{show.BroadcastCount:N0} broadcasts");
        cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return cell;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (IsShowingSearchResults)
        {
            if (indexPath.Row < Session.LibraryBroadcasts.Count)
                NavigationController?.PushViewController(
                    new BroadcastDetailsViewController(Session, Session.LibraryBroadcasts[indexPath.Row]), true);
            return;
        }

        if (indexPath.Section == 0)
        {
            if (indexPath.Row == 0)
            {
                NavigationController?.PushViewController(new UpNextViewController(Session), true);
                return;
            }
            var filters = new[]
            {
                ("Favourites", "Favourites"),
                ("ContinueListening", "Continue Listening"),
                ("RecentlyAdded", "Recently Added"),
                ("Unplayed", "Unplayed"),
                ("Completed", "Completed")
            };
            var filter = filters[indexPath.Row - 1];
            NavigationController?.PushViewController(
                new ShowLibraryViewController(Session, null, filter.Item2, filter.Item1, _hideCompleted), true);
            return;
        }

        if (indexPath.Section == 1)
        {
            NavigationController?.PushViewController(
                new ShowLibraryViewController(Session, null, "All Broadcasts", hideCompleted: _hideCompleted), true);
            return;
        }

        if (indexPath.Row < Session.LibraryCollections.Count)
        {
            var show = Session.LibraryCollections[indexPath.Row];
            NavigationController?.PushViewController(
                new ShowLibraryViewController(Session, show.CollectionId, show.CollectionName, hideCompleted: _hideCompleted), true);
        }
    }

    public void UpdateSearchResultsForSearchController(UISearchController searchController) { }

    [Export("searchBarSearchButtonClicked:")]
    public void SearchButtonClicked(UISearchBar searchBar)
    {
        searchBar.ResignFirstResponder();
        _ = Session.SearchAsync(searchBar.Text ?? string.Empty, _hideCompleted);
    }

    [Export("searchBarCancelButtonClicked:")]
    public void CancelButtonClicked(UISearchBar searchBar) => _ = Session.SearchAsync(string.Empty, _hideCompleted);

    protected override MobileBroadcastItem? ContextBroadcastForRow(NSIndexPath indexPath)
        => IsShowingSearchResults && indexPath.Row < Session.LibraryBroadcasts.Count
            ? Session.LibraryBroadcasts[indexPath.Row]
            : null;

    private void ToggleHideCompleted()
    {
        _hideCompleted = !_hideCompleted;
        UpdateHideCompletedButton();
        if (IsShowingSearchResults)
            _ = Session.SearchAsync(_searchController?.SearchBar.Text ?? string.Empty, _hideCompleted);
        else
            TableView.ReloadData();
    }

    private void UpdateHideCompletedButton()
    {
        var button = NavigationItem.RightBarButtonItem;
        if (button is null) return;
        button.Image = RadioVaultIcons.Image(
            RadioVaultIcon.Completed,
            _hideCompleted ? RadioVaultTheme.Accent : RadioVaultTheme.MutedText);
        button.AccessibilityLabel = _hideCompleted ? "Show completed broadcasts" : "Hide completed broadcasts";
        button.AccessibilityValue = _hideCompleted ? "Completed broadcasts hidden" : "Completed broadcasts shown";
    }

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
