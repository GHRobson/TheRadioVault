using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class LibraryViewController : SessionTableViewController, IUISearchBarDelegate
{
    private readonly LibraryControlsHeaderView _header = new(includesPageHeading: true, includesViewModes: false);
    private bool _hideCompleted;
    private bool IsShowingSearchResults => !string.IsNullOrWhiteSpace(_header.SearchBar.Text);
    private IReadOnlyList<TheRadioVault.Web.Models.WebClientLibraryCollectionSummary> VisibleCollections
        => Session.LibraryCollectionsFor(_hideCompleted);

    public LibraryViewController(MobileClientSession session) : base(session) => Title = "Library";
    protected override bool UsesInlinePageHeading => true;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        TableView.RowHeight = UITableView.AutomaticDimension;
        TableView.EstimatedRowHeight = 74;
        _header.SearchBar.Placeholder = "Search broadcasts";
        _header.SearchBar.Delegate = this;
        _header.CompletedButton.TouchUpInside += CompletedButtonTapped;
        TableView.TableHeaderView = _header;
        UpdateHideCompletedButton();
        RefreshControl = new UIRefreshControl();
        RefreshControl.ValueChanged += async (_, _) =>
        {
            await Session.SearchAsync(_header.SearchBar.Text ?? string.Empty, _hideCompleted);
            BeginInvokeOnMainThread(() => RefreshControl?.EndRefreshing());
        };
    }

    public override nint NumberOfSections(UITableView tableView) => IsShowingSearchResults ? 1 : 2;

    public override nint RowsInSection(UITableView tableView, nint section)
    {
        if (IsShowingSearchResults) return Math.Max(1, Session.LibraryBroadcasts.Count);
        return section switch
        {
            0 => 1,
            _ => Math.Max(1, VisibleCollections.Count)
        };
    }

    public override string? TitleForHeader(UITableView tableView, nint section)
        => IsShowingSearchResults ? "Search results" : section switch
        {
            0 => "Your Library",
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
            resultCell.Configure(Session, item);
            resultCell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
            return resultCell;
        }

        if (indexPath.Section == 0)
        {
            var unplayed = Math.Max(0, Session.TotalBroadcasts - Session.CompletedBroadcasts - Session.InProgressBroadcasts);
            var quick = new LibraryQuickAccessCell();
            quick.Configure(
                ("All", $"{Session.TotalBroadcasts:N0}", RadioVaultIcon.Library,
                    () => OpenLibrarySection("All Broadcasts")),
                ("Up Next", $"{Session.QueueItems.Count:N0}", RadioVaultIcon.UpNext,
                    () => NavigationController?.PushViewController(new UpNextViewController(Session), true)),
                ("Favourites", $"{Session.FavouriteBroadcasts:N0}", RadioVaultIcon.Favourite,
                    () => OpenLibrarySection("Favourites", "Favourites")),
                ("Listening", $"{Session.InProgressBroadcasts:N0}", RadioVaultIcon.InProgress,
                    () => OpenLibrarySection("Continue Listening", "ContinueListening")),
                ("Recent", "New", RadioVaultIcon.Download,
                    () => OpenLibrarySection("Recently Added", "RecentlyAdded")),
                ("Unplayed", $"{unplayed:N0}", RadioVaultIcon.Radio,
                    () => OpenLibrarySection("Unplayed", "Unplayed")),
                ("Completed", $"{Session.CompletedBroadcasts:N0}", RadioVaultIcon.Completed,
                    () => OpenLibrarySection("Completed", "Completed")),
                ("Downloads", $"{Session.DownloadedBroadcasts.Count:N0}", RadioVaultIcon.Download,
                    () => NavigationController?.PushViewController(new DownloadsViewController(Session), true)));
            return quick;
        }

        if (VisibleCollections.Count == 0)
            return DetailCell("empty-shows", Session.IsPaired ? "No shows found" : "Pair a server first", Session.StatusText);
        var show = VisibleCollections[indexPath.Row];
        return IconDetailCell(
            "library-show",
            show.CollectionName,
            $"{show.BroadcastCount:N0} broadcasts",
            RadioVaultIcon.Radio,
            disclosure: true);
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
            return;

        if (indexPath.Row < VisibleCollections.Count)
        {
            var show = VisibleCollections[indexPath.Row];
            NavigationController?.PushViewController(
                new ShowLibraryViewController(Session, show.CollectionId, show.CollectionName, hideCompleted: _hideCompleted), true);
        }
    }

    private void OpenLibrarySection(string title, string filter = "All")
        => NavigationController?.PushViewController(
            new ShowLibraryViewController(Session, null, title, filter, _hideCompleted), true);

    [Export("searchBarSearchButtonClicked:")]
    public void SearchButtonClicked(UISearchBar searchBar)
    {
        searchBar.ResignFirstResponder();
        _ = Session.SearchAsync(searchBar.Text ?? string.Empty, _hideCompleted);
    }

    [Export("searchBar:textDidChange:")]
    public void TextChanged(UISearchBar searchBar, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            _ = Session.SearchAsync(string.Empty, _hideCompleted);
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
            _ = Session.SearchAsync(_header.SearchBar.Text ?? string.Empty, _hideCompleted);
        else
            TableView.ReloadData();
    }

    private void CompletedButtonTapped(object? sender, EventArgs eventArgs) => ToggleHideCompleted();

    private void UpdateHideCompletedButton()
    {
        _header.SetHideCompleted(_hideCompleted);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _header.CompletedButton.TouchUpInside -= CompletedButtonTapped;
            _header.Dispose();
        }
        base.Dispose(disposing);
    }
}
