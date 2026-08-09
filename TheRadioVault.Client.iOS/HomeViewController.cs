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

    private MobileBroadcastItem? FeaturedContinue => Session.ContinueListening.FirstOrDefault();
    private IReadOnlyList<MobileBroadcastItem> UpNext => Session.ContinueListening.Skip(1).Take(4).ToArray();
    protected override string? PageHeading => "Dashboard";
    protected override string PageDescription => "Continue listening, rediscover the archive, or choose something unexpected.";

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

    public override nint NumberOfSections(UITableView tableView) => 7;

    public override nint RowsInSection(UITableView tableView, nint section) => section switch
    {
        0 => 1,
        1 => 1,
        2 => 1,
        3 => Math.Max(1, UpNext.Count),
        4 => Math.Max(1, Session.OnThisDay.Count),
        5 => Math.Max(1, Session.RecentBroadcasts.Take(5).Count()),
        6 => Math.Max(1, Session.UnheardBroadcasts.Take(5).Count()),
        _ => 0
    };

    public override string? TitleForHeader(UITableView tableView, nint section) => section switch
    {
        0 => "Your Library",
        1 => "Continue listening",
        2 => "Not sure what to play?",
        3 => "Up next",
        4 => "On this day",
        5 => "Recently added",
        6 => "Unheard broadcasts",
        _ => null
    };

    public override string? TitleForFooter(UITableView tableView, nint section) => section switch
    {
        1 when FeaturedContinue is null => "Choose something from the Library or let Radio Vault pick for you.",
        2 => "Choose a random unheard broadcast.",
        _ => null
    };

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (indexPath.Section == 0)
        {
            var stats = new DashboardStatsCell();
            stats.ConfigureInteractive(
                ("Broadcasts", Session.TotalBroadcasts, RadioVaultIcon.Library,
                    () => OpenLibrarySection("All Broadcasts", "All")),
                ("In progress", Session.InProgressBroadcasts, RadioVaultIcon.Play,
                    () => OpenLibrarySection("Continue Listening", "ContinueListening")),
                ("Completed", Session.CompletedBroadcasts, RadioVaultIcon.Completed,
                    () => OpenLibrarySection("Completed", "Completed")),
                ("Favourites", Session.FavouriteBroadcasts, RadioVaultIcon.Favourite,
                    () => OpenLibrarySection("Favourites", "Favourites")));
            return stats;
        }

        if (indexPath.Section == 1)
        {
            if (FeaturedContinue is not { } featured)
                return DetailCell("dashboard-featured-empty", "Nothing waiting to resume", Session.StatusText);
            var cell = new DashboardContinueCell();
            cell.Configure(featured, () => _ = Session.PlayAsync(featured));
            return cell;
        }

        if (indexPath.Section == 2)
        {
            var cell = new UITableViewCell(UITableViewCellStyle.Default, "dashboard-surprise");
            var content = cell.DefaultContentConfiguration;
            content.Text = "Surprise me";
            content.SecondaryText = "Play a random unheard broadcast";
            content.Image = RadioVaultIcons.Image(RadioVaultIcon.Radio);
            RadioVaultTheme.StyleCell(cell, content);
            cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
            cell.SelectionStyle = Session.UnheardBroadcasts.Count == 0
                ? UITableViewCellSelectionStyle.None
                : UITableViewCellSelectionStyle.Default;
            return cell;
        }

        var values = ValuesForSection(indexPath.Section);
        if (values.Count == 0)
        {
            var empty = indexPath.Section switch
            {
                3 => "Nothing else waiting to resume",
                4 => "No broadcasts aired on this date",
                5 => "No recently added broadcasts",
                _ => "You have heard everything in the Library"
            };
            return DetailCell("dashboard-empty", empty, "Pull down to refresh the Dashboard.");
        }

        var item = values[indexPath.Row];
        var broadcastCell = new BroadcastProgressCell("dashboard-broadcast");
        broadcastCell.Configure(item, indexPath.Section == 3
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
            _ = Session.PlayAsync(featured);
            return;
        }
        if (indexPath.Section == 2)
        {
            var pool = Session.UnheardBroadcasts;
            if (pool.Count > 0) _ = Session.PlayAsync(pool[Random.Shared.Next(pool.Count)]);
            return;
        }
        if (indexPath.Section < 3) return;
        var values = ValuesForSection(indexPath.Section);
        if (indexPath.Row < values.Count)
            NavigationController?.PushViewController(
                new BroadcastDetailsViewController(Session, values[indexPath.Row]), true);
    }

    protected override MobileBroadcastItem? ContextBroadcastForRow(NSIndexPath indexPath)
    {
        if (indexPath.Section == 1) return FeaturedContinue;
        if (indexPath.Section < 3) return null;
        var values = ValuesForSection(indexPath.Section);
        return indexPath.Row < values.Count ? values[indexPath.Row] : null;
    }

    private IReadOnlyList<MobileBroadcastItem> ValuesForSection(nint section) => section switch
    {
        3 => UpNext,
        4 => Session.OnThisDay,
        5 => Session.RecentBroadcasts.Take(5).ToArray(),
        6 => Session.UnheardBroadcasts.Take(5).ToArray(),
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
}
