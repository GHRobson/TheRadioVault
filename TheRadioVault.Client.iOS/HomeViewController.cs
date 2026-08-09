using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class HomeViewController : SessionTableViewController
{
    public HomeViewController(MobileClientSession session) : base(session)
    {
        Title = "Radio Vault";
        TabBarItem.AccessibilityLabel = "Home";
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Always;
        NavigationController?.NavigationBar.PrefersLargeTitles = true;
        RefreshControl = new UIRefreshControl();
        RefreshControl.ValueChanged += async (_, _) =>
        {
            await Session.RefreshAsync();
            BeginInvokeOnMainThread(() => RefreshControl?.EndRefreshing());
        };
    }

    public override nint NumberOfSections(UITableView tableView) => 4;

    public override nint RowsInSection(UITableView tableView, nint section) => section switch
    {
        0 => 1,
        1 => 3,
        2 => Math.Max(1, Session.ContinueListening.Count),
        3 => Math.Max(1, Session.RecentBroadcasts.Count),
        _ => 0
    };

    public override string? TitleForHeader(UITableView tableView, nint section) => section switch
    {
        0 => "Connection",
        1 => "Your library",
        2 => "Continue listening",
        3 => "Recently added",
        _ => null
    };

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (indexPath.Section == 0)
            return DetailCell("status", Session.IsPaired ? Session.ServerName : "Not paired", Session.StatusText);

        if (indexPath.Section == 1)
        {
            var values = new[]
            {
                ("Total broadcasts", Session.TotalBroadcasts.ToString("N0")),
                ("Completed", Session.CompletedBroadcasts.ToString("N0")),
                ("In progress", Session.InProgressBroadcasts.ToString("N0"))
            };
            var value = values[indexPath.Row];
            return DetailCell("metric", value.Item1, value.Item2);
        }

        var items = indexPath.Section == 2 ? Session.ContinueListening : Session.RecentBroadcasts;
        if (items.Count == 0)
            return DetailCell("empty", indexPath.Section == 2 ? "Nothing in progress" : "No recent broadcasts", "Pull down to refresh.");

        return BroadcastCell(items[indexPath.Row]);
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Section is not (2 or 3)) return;
        var items = indexPath.Section == 2 ? Session.ContinueListening : Session.RecentBroadcasts;
        if (indexPath.Row < items.Count)
            NavigationController?.PushViewController(
                new BroadcastDetailsViewController(Session, items[indexPath.Row]), true);
    }

    private static UITableViewCell BroadcastCell(MobileBroadcastItem item)
    {
        var cell = DetailCell("broadcast", item.Title, $"{item.Subtitle} · {item.Status}");
        cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return cell;
    }
}
