using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class HomeViewController : SessionTableViewController
{
    public HomeViewController(MobileClientSession session) : base(session)
    {
        Title = "Dashboard";
        TabBarItem.AccessibilityLabel = "Dashboard";
    }

    private MobileBroadcastItem? FeaturedContinue
    {
        get
        {
            var current = Session.CurrentBroadcast;
            return current is not null && !current.Source.Completed &&
                   (Session.MiniPlayerShowsHandoff || Session.CanControlPlayback)
                ? current
                : Session.ContinueListening.FirstOrDefault();
        }
    }

    private IReadOnlyList<MobileBroadcastItem> UpNext
    {
        get
        {
            var featuredId = FeaturedContinue?.EpisodeId;
            return Session.ContinueListening
                .Where(value => value.EpisodeId != featuredId)
                .Take(4)
                .ToArray();
        }
    }
    protected override string? PageHeading => "Dashboard";
    protected override string PageDescription => "Your archive at a glance.";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        TableView.RowHeight = UITableView.AutomaticDimension;
        TableView.EstimatedRowHeight = 88;
        RefreshControl = new UIRefreshControl();
        RefreshControl.ValueChanged += async (_, _) =>
        {
            await Session.RefreshAsync();
            BeginInvokeOnMainThread(() => RefreshControl?.EndRefreshing());
        };
    }

    public override nint NumberOfSections(UITableView tableView) => 6;

    public override nint RowsInSection(UITableView tableView, nint section) => section switch
    {
        0 => 1,
        1 => 1,
        2 => Math.Max(1, UpNext.Count),
        3 => Math.Max(1, Session.OnThisDay.Count),
        4 => Math.Max(1, Session.RecentBroadcasts.Take(5).Count()),
        5 => Math.Max(1, Session.UnheardBroadcasts.Take(5).Count()),
        _ => 0
    };

    public override string? TitleForHeader(UITableView tableView, nint section) => section switch
    {
        1 => "Continue listening",
        2 => "Up next",
        3 => "On this day",
        4 => "Recently added",
        5 => "Unheard broadcasts",
        _ => null
    };

    public override string? TitleForFooter(UITableView tableView, nint section) => section switch
    {
        1 when FeaturedContinue is null => "Choose something from the Library or let Radio Vault pick for you.",
        _ => null
    };

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (indexPath.Section == 0)
        {
            var overview = new DashboardOverviewCell();
            overview.Configure(
                Session.UnheardBroadcasts.Count > 0,
                () =>
                {
                    var pool = Session.UnheardBroadcasts;
                    if (pool.Count > 0) _ = Session.PlayAsync(pool[Random.Shared.Next(pool.Count)]);
                },
                ("Broadcasts", Session.TotalBroadcasts, RadioVaultIcon.Library,
                    () => OpenLibrarySection("All Broadcasts", "All")),
                ("In progress", Session.InProgressBroadcasts, RadioVaultIcon.InProgress,
                    () => OpenLibrarySection("Continue Listening", "ContinueListening")),
                ("Completed", Session.CompletedBroadcasts, RadioVaultIcon.Completed,
                    () => OpenLibrarySection("Completed", "Completed")),
                ("Favourites", Session.FavouriteBroadcasts, RadioVaultIcon.Favourite,
                    () => OpenLibrarySection("Favourites", "Favourites")));
            return overview;
        }

        if (indexPath.Section == 1)
        {
            if (FeaturedContinue is not { } featured)
                return DetailCell("dashboard-featured-empty", "Nothing waiting to resume", Session.StatusText);
            var cell = new DashboardContinueCell();
            cell.Configure(
                Session,
                featured,
                Session.PreparingPlaybackEpisodeId == featured.EpisodeId,
                Session.IsPlayingBroadcast(featured.EpisodeId),
                () => HandleFeaturedPlayback(featured));
            return cell;
        }

        var values = ValuesForSection(indexPath.Section);
        if (values.Count == 0)
        {
            var empty = indexPath.Section switch
            {
                2 => "Nothing else waiting to resume",
                3 => "No broadcasts aired on this date",
                4 => "No recently added broadcasts",
                _ => "You have heard everything in the Library"
            };
            return DetailCell("dashboard-empty", empty, "Pull down to refresh the Dashboard.");
        }

        var item = values[indexPath.Row];
        var broadcastCell = new BroadcastProgressCell("dashboard-broadcast");
        broadcastCell.Configure(Session, item, indexPath.Section == 2
            ? $"{item.Source.CollectionName} · ready to resume"
            : $"{item.Subtitle} · {item.Status}");
        broadcastCell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return broadcastCell;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Section == 1 && FeaturedContinue is { } featured)
        {
            HandleFeaturedPlayback(featured);
            return;
        }
        if (indexPath.Section < 2) return;
        var values = ValuesForSection(indexPath.Section);
        if (indexPath.Row < values.Count)
            NavigationController?.PushViewController(
                new BroadcastDetailsViewController(Session, values[indexPath.Row]), true);
    }

    protected override MobileBroadcastItem? ContextBroadcastForRow(NSIndexPath indexPath)
    {
        if (indexPath.Section == 1) return FeaturedContinue;
        if (indexPath.Section < 2) return null;
        var values = ValuesForSection(indexPath.Section);
        return indexPath.Row < values.Count ? values[indexPath.Row] : null;
    }

    private IReadOnlyList<MobileBroadcastItem> ValuesForSection(nint section) => section switch
    {
        2 => UpNext,
        3 => Session.OnThisDay,
        4 => Session.RecentBroadcasts.Take(5).ToArray(),
        5 => Session.UnheardBroadcasts.Take(5).ToArray(),
        _ => []
    };

    private void OpenLibrarySection(string title, string filter)
    {
        if (TabBarController?.ViewControllers is not { Length: > 1 } controllers ||
            controllers[1] is not UINavigationController libraryNavigation) return;
        TabBarController.SelectedIndex = 1;
        libraryNavigation.PopToRootViewController(false);
        libraryNavigation.PushViewController(
            new ShowLibraryViewController(Session, null, title, filter),
            true);
    }

    private void HandleFeaturedPlayback(MobileBroadcastItem featured)
    {
        if (Session.PreparingPlaybackEpisodeId == featured.EpisodeId) return;
        if (Session.CanToggleBroadcast(featured.EpisodeId)) Session.TogglePlayPause();
        else _ = Session.PlayAsync(featured);
    }
}
