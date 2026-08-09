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

    public override nint NumberOfSections(UITableView tableView) => 7;

    public override nint RowsInSection(UITableView tableView, nint section) => section switch
    {
        0 => 1,
        1 => 1,
        2 => 4,
        3 => Math.Max(1, UpNext.Count),
        4 => Math.Max(1, Session.OnThisDay.Count),
        5 => Math.Max(1, Session.RecentBroadcasts.Take(5).Count()),
        6 => Math.Max(1, Session.UnheardBroadcasts.Take(5).Count()),
        _ => 0
    };

    public override string? TitleForHeader(UITableView tableView, nint section) => section switch
    {
        0 => "Continue listening",
        1 => "Not sure what to play?",
        2 => "Your Library",
        3 => "Up next",
        4 => "On this day",
        5 => "Recently added",
        6 => "Unheard broadcasts",
        _ => null
    };

    public override string? TitleForFooter(UITableView tableView, nint section) => section switch
    {
        0 when FeaturedContinue is null => "Choose something from the Library or let Radio Vault pick for you.",
        1 => "Choose a random unheard broadcast.",
        _ => null
    };

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (indexPath.Section == 0)
        {
            var featured = FeaturedContinue;
            if (featured is null)
                return DetailCell("dashboard-featured-empty", "Nothing waiting to resume", Session.StatusText);
            var cell = DashboardBroadcastCell(
                "dashboard-featured",
                featured,
                $"Resume · {featured.Progress:0}% listened");
            cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
            return cell;
        }

        if (indexPath.Section == 1)
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

        if (indexPath.Section == 2)
        {
            var stats = new[]
            {
                ("Broadcasts", Session.TotalBroadcasts, RadioVaultIcon.Library),
                ("In progress", Session.InProgressBroadcasts, RadioVaultIcon.Play),
                ("Completed", Session.CompletedBroadcasts, RadioVaultIcon.Completed),
                ("Favourites", Session.FavouriteBroadcasts, RadioVaultIcon.Favourite)
            };
            var stat = stats[indexPath.Row];
            var cell = new UITableViewCell(UITableViewCellStyle.Default, "dashboard-stat");
            var content = cell.DefaultContentConfiguration;
            content.Text = stat.Item1;
            content.SecondaryText = stat.Item2.ToString("N0");
            content.Image = RadioVaultIcons.Image(stat.Item3);
            RadioVaultTheme.StyleCell(cell, content);
            cell.SelectionStyle = UITableViewCellSelectionStyle.None;
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

        return DashboardBroadcastCell(
            "dashboard-broadcast",
            values[indexPath.Row],
            indexPath.Section == 3 ? $"{values[indexPath.Row].Progress:0}% listened" : null);
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Section == 0 && FeaturedContinue is { } featured)
        {
            _ = Session.PlayAsync(featured);
            return;
        }
        if (indexPath.Section == 1)
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

    private IReadOnlyList<MobileBroadcastItem> ValuesForSection(nint section) => section switch
    {
        3 => UpNext,
        4 => Session.OnThisDay,
        5 => Session.RecentBroadcasts.Take(5).ToArray(),
        6 => Session.UnheardBroadcasts.Take(5).ToArray(),
        _ => []
    };

    private static UITableViewCell DashboardBroadcastCell(
        string identifier,
        MobileBroadcastItem item,
        string? action = null)
    {
        var detail = action is null
            ? $"{item.Subtitle} · {item.Status}"
            : $"{item.Subtitle} · {action}";
        var cell = DetailCell(identifier, item.Title, detail);
        cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return cell;
    }
}
