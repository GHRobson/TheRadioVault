using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Web.Models;
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
        _header.CollectionsButton.TouchUpInside += CollectionsButtonTapped;
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

    public override nint NumberOfSections(UITableView tableView) => IsShowingSearchResults ? 1 : 3;

    public override nint RowsInSection(UITableView tableView, nint section)
    {
        if (IsShowingSearchResults) return Math.Max(1, Session.LibraryBroadcasts.Count);
        return section switch
        {
            0 => 1,
            1 => Math.Max(1, Session.SavedCollections.Count),
            _ => Math.Max(1, VisibleCollections.Count)
        };
    }

    public override string? TitleForHeader(UITableView tableView, nint section)
        => IsShowingSearchResults ? "Search results" : section switch
        {
            0 => "Your Library",
            1 => "Playlists & Smart Collections",
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
            var searchDetail = string.IsNullOrWhiteSpace(item.Source.SearchContext)
                ? null
                : item.Source.SearchStartMs is { } startMs
                    ? $"{item.Source.SearchContext} · Tap to play from {FormatSearchTime(startMs)}"
                    : item.Source.SearchContext;
            resultCell.Configure(Session, item, searchDetail);
            resultCell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
            if (item.Source.SearchStartMs is { } matchStart)
                resultCell.AccessibilityHint = $"Plays this transcript match from {FormatSearchTime(matchStart)}";
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

        if (indexPath.Section == 1)
        {
            if (Session.SavedCollections.Count == 0)
                return DetailCell("empty-saved-collections", "No playlists yet", "Tap + to create one or save the current Up Next queue.");
            var saved = Session.SavedCollections[indexPath.Row];
            var kind = saved.Kind.Equals("Smart", StringComparison.OrdinalIgnoreCase)
                ? "Smart collection · updates automatically"
                : saved.ItemCount is { } count
                    ? $"Playlist · {count:N0} broadcast{(count == 1 ? string.Empty : "s")}"
                    : "Playlist";
            return IconDetailCell(
                "saved-collection",
                saved.Name,
                kind,
                saved.Kind.Equals("Smart", StringComparison.OrdinalIgnoreCase) ? RadioVaultIcon.Search : RadioVaultIcon.UpNext,
                disclosure: true);
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
            {
                var broadcast = Session.LibraryBroadcasts[indexPath.Row];
                if (broadcast.Source.SearchStartMs is { } startMs)
                    _ = Session.PlayAtAsync(broadcast, startMs);
                else
                    NavigationController?.PushViewController(
                        new BroadcastDetailsViewController(Session, broadcast), true);
            }
            return;
        }

        if (indexPath.Section == 0)
            return;

        if (indexPath.Section == 1)
        {
            if (indexPath.Row < Session.SavedCollections.Count)
                NavigationController?.PushViewController(
                    new SavedCollectionViewController(Session, Session.SavedCollections[indexPath.Row]), true);
            return;
        }

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

    private void ShowSavedCollectionActions()
    {
        var sheet = UIAlertController.Create("Saved Collections", null, UIAlertControllerStyle.ActionSheet);
        sheet.AddAction(UIAlertAction.Create("New Playlist", UIAlertActionStyle.Default, _ => PromptForPlaylistName(false)));
        sheet.AddAction(UIAlertAction.Create("New Smart Collection", UIAlertActionStyle.Default, _ => ChooseSmartCollectionFilter()));
        sheet.AddAction(UIAlertAction.Create(
            "Save Up Next as Playlist",
            UIAlertActionStyle.Default,
            _ => PromptForPlaylistName(true)));
        sheet.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        if (sheet.PopoverPresentationController is { } popover)
        {
            popover.SourceView = _header.CollectionsButton;
            popover.SourceRect = _header.CollectionsButton.Bounds;
        }
        PresentViewController(sheet, true, null);
    }

    private void ChooseSmartCollectionFilter()
    {
        var sheet = UIAlertController.Create(
            "Smart Collection",
            "Choose which part of your Library should update automatically.",
            UIAlertControllerStyle.ActionSheet);
        foreach (var option in new[]
                 {
                     (Title: "Every Broadcast", Filter: "All"),
                     (Title: "Favourites", Filter: "Favourites"),
                     (Title: "Continue Listening", Filter: "ContinueListening"),
                     (Title: "Unplayed", Filter: "Unplayed"),
                     (Title: "Recently Added", Filter: "RecentlyAdded"),
                     (Title: "Completed", Filter: "Completed")
                 })
        {
            sheet.AddAction(UIAlertAction.Create(
                option.Title,
                UIAlertActionStyle.Default,
                _ => PromptForSmartCollection(option.Title, option.Filter)));
        }
        sheet.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        if (sheet.PopoverPresentationController is { } popover)
        {
            popover.SourceView = _header.CollectionsButton;
            popover.SourceRect = _header.CollectionsButton.Bounds;
        }
        PresentViewController(sheet, true, null);
    }

    private void PromptForSmartCollection(string defaultName, string filter)
    {
        var alert = UIAlertController.Create(
            "New Smart Collection",
            "It will stay up to date as your Radio Vault Library changes.",
            UIAlertControllerStyle.Alert);
        alert.AddTextField(field =>
        {
            field.Placeholder = "Collection name";
            field.Text = defaultName;
            field.ClearButtonMode = UITextFieldViewMode.WhileEditing;
        });
        alert.AddTextField(field =>
        {
            field.Placeholder = "Optional words, person, topic or title";
            field.ClearButtonMode = UITextFieldViewMode.WhileEditing;
        });
        alert.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        alert.AddAction(UIAlertAction.Create("Create", UIAlertActionStyle.Default, async _ =>
        {
            var name = alert.TextFields?[0].Text?.Trim() ?? string.Empty;
            if (name.Length == 0) return;
            var search = alert.TextFields?[1].Text;
            var created = await Session.CreateSmartCollectionAsync(name, filter, search).ConfigureAwait(false);
            if (created is not null)
                BeginInvokeOnMainThread(() => NavigationController?.PushViewController(
                    new SavedCollectionViewController(Session, created.Summary), true));
        }));
        PresentViewController(alert, true, null);
    }

    private void PromptForPlaylistName(bool fromQueue)
    {
        var alert = UIAlertController.Create(
            fromQueue ? "Save Up Next" : "New Playlist",
            fromQueue ? "The shared queue will be copied into a reusable playlist." : "Give this playlist a short, memorable name.",
            UIAlertControllerStyle.Alert);
        alert.AddTextField(field =>
        {
            field.Placeholder = "Playlist name";
            field.Text = fromQueue ? "Up Next" : string.Empty;
            field.ClearButtonMode = UITextFieldViewMode.WhileEditing;
        });
        alert.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        alert.AddAction(UIAlertAction.Create("Save", UIAlertActionStyle.Default, async _ =>
        {
            var name = alert.TextFields?.FirstOrDefault()?.Text?.Trim() ?? string.Empty;
            if (name.Length == 0) return;
            var created = await Session.CreateSavedCollectionAsync(name, fromQueue).ConfigureAwait(false);
            if (created is not null)
                BeginInvokeOnMainThread(() => NavigationController?.PushViewController(
                    new SavedCollectionViewController(Session, created.Summary), true));
        }));
        PresentViewController(alert, true, null);
    }

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

    private void CollectionsButtonTapped(object? sender, EventArgs eventArgs) => ShowSavedCollectionActions();

    private void UpdateHideCompletedButton()
    {
        _header.SetHideCompleted(_hideCompleted);
    }

    private static string FormatSearchTime(long milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _header.CollectionsButton.TouchUpInside -= CollectionsButtonTapped;
            _header.CompletedButton.TouchUpInside -= CompletedButtonTapped;
            _header.Dispose();
        }
        base.Dispose(disposing);
    }
}
