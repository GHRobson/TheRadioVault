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

    public override nint NumberOfSections(UITableView tableView) => 8;

    public override nint RowsInSection(UITableView tableView, nint section) => section switch
    {
        0 => 1,
        1 => 1,
        2 => 1,
        3 => 1,
        4 => Math.Max(1, UpNext.Count),
        5 => Math.Max(1, Session.OnThisDay.Count),
        6 => Math.Max(1, Session.RecentBroadcasts.Take(5).Count()),
        7 => Math.Max(1, Session.UnheardBroadcasts.Take(5).Count()),
        _ => 0
    };

    public override string? TitleForHeader(UITableView tableView, nint section) => section switch
    {
        1 => "Your Library",
        2 => "Continue listening",
        3 => "Not sure what to play?",
        4 => "Up next",
        5 => "On this day",
        6 => "Recently added",
        7 => "Unheard broadcasts",
        _ => null
    };

    public override string? TitleForFooter(UITableView tableView, nint section) => section switch
    {
        2 when FeaturedContinue is null => "Choose something from the Library or let Radio Vault pick for you.",
        3 => "Choose a random unheard broadcast.",
        _ => null
    };

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (indexPath.Section == 0) return new DashboardHeaderCell();

        if (indexPath.Section == 1)
        {
            var stats = new DashboardStatsCell();
            stats.Configure(
                ("Broadcasts", Session.TotalBroadcasts, RadioVaultIcon.Library),
                ("In progress", Session.InProgressBroadcasts, RadioVaultIcon.Play),
                ("Completed", Session.CompletedBroadcasts, RadioVaultIcon.Completed),
                ("Favourites", Session.FavouriteBroadcasts, RadioVaultIcon.Favourite));
            return stats;
        }

        if (indexPath.Section == 2)
        {
            if (FeaturedContinue is not { } featured)
                return DetailCell("dashboard-featured-empty", "Nothing waiting to resume", Session.StatusText);
            var cell = new DashboardContinueCell();
            cell.Configure(featured, () => _ = Session.PlayAsync(featured));
            return cell;
        }

        if (indexPath.Section == 3)
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
                4 => "Nothing else waiting to resume",
                5 => "No broadcasts aired on this date",
                6 => "No recently added broadcasts",
                _ => "You have heard everything in the Library"
            };
            return DetailCell("dashboard-empty", empty, "Pull down to refresh the Dashboard.");
        }

        var item = values[indexPath.Row];
        var broadcastCell = new BroadcastProgressCell("dashboard-broadcast");
        broadcastCell.Configure(item, indexPath.Section == 4
            ? $"{item.Source.CollectionName} · ready to resume"
            : $"{item.Subtitle} · {item.Status}");
        broadcastCell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return broadcastCell;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Section == 2 && FeaturedContinue is { } featured)
        {
            _ = Session.PlayAsync(featured);
            return;
        }
        if (indexPath.Section == 3)
        {
            var pool = Session.UnheardBroadcasts;
            if (pool.Count > 0) _ = Session.PlayAsync(pool[Random.Shared.Next(pool.Count)]);
            return;
        }
        if (indexPath.Section < 4) return;
        var values = ValuesForSection(indexPath.Section);
        if (indexPath.Row < values.Count)
            NavigationController?.PushViewController(
                new BroadcastDetailsViewController(Session, values[indexPath.Row]), true);
    }

    protected override MobileBroadcastItem? ContextBroadcastForRow(NSIndexPath indexPath)
    {
        if (indexPath.Section == 2) return FeaturedContinue;
        if (indexPath.Section < 4) return null;
        var values = ValuesForSection(indexPath.Section);
        return indexPath.Row < values.Count ? values[indexPath.Row] : null;
    }

    private IReadOnlyList<MobileBroadcastItem> ValuesForSection(nint section) => section switch
    {
        4 => UpNext,
        5 => Session.OnThisDay,
        6 => Session.RecentBroadcasts.Take(5).ToArray(),
        7 => Session.UnheardBroadcasts.Take(5).ToArray(),
        _ => []
    };
}
