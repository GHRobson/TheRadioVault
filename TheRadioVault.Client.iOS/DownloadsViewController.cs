using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class DownloadsViewController : SessionTableViewController
{
    public DownloadsViewController(MobileClientSession session) : base(session) => Title = "Downloads";
    protected override string? PageHeading => "Downloads";
    protected override string PageDescription => "Saved for offline listening.";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        TableView.RowHeight = UITableView.AutomaticDimension;
        TableView.EstimatedRowHeight = 74;
        NavigationItem.RightBarButtonItem = EditButtonItem;
    }

    public override nint NumberOfSections(UITableView tableView) => 2;
    public override nint RowsInSection(UITableView tableView, nint section)
        => section switch
        {
            0 => Session.ActiveDownloadEpisodeId is null ? 1 : 3,
            _ => Math.Max(1, Session.DownloadedBroadcasts.Count)
        };

    public override string? TitleForHeader(UITableView tableView, nint section) => section switch
    {
        0 => "Activity",
        _ => "Downloaded"
    };

    public override string? TitleForFooter(UITableView tableView, nint section) => section switch
    {
        0 => Session.DownloadStatus,
        _ => null
    };

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (indexPath.Section == 0)
        {
            if (indexPath.Row == 0)
                return DetailCell(
                    "download-activity",
                    Session.ActiveDownloadEpisodeId is null ? "No active download" : Session.IsDownloadPaused ? "Download paused" : "Downloading",
                    Session.ActiveDownloadEpisodeId is null ? "Choose a broadcast in Library." : $"{Session.DownloadProgressPercent}% · {Session.DownloadStatus}");
            var action = new UITableViewCell(UITableViewCellStyle.Default, "download-action");
            var actionContent = action.DefaultContentConfiguration;
            actionContent.Text = indexPath.Row == 1
                ? Session.IsDownloadPaused ? "Resume Download" : "Pause Download"
                : "Cancel Download";
            actionContent.TextProperties.Color = indexPath.Row == 2 ? RadioVaultTheme.Danger : RadioVaultTheme.Accent;
            actionContent.TextProperties.Alignment = UIListContentTextAlignment.Center;
            action.ContentConfiguration = actionContent;
            action.BackgroundColor = RadioVaultTheme.Surface;
            return action;
        }

        if (Session.DownloadedBroadcasts.Count == 0)
            return DetailCell("empty-downloads", "No downloads yet", "Open a broadcast in Library and choose Download to this iPhone.");
        var item = Session.DownloadedBroadcasts[indexPath.Row];
        var cell = new BroadcastProgressCell("download");
        cell.Configure(Session, item, $"{item.Subtitle} · downloaded");
        cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return cell;
    }

    public override bool CanEditRow(UITableView tableView, NSIndexPath indexPath)
        => indexPath.Section == 1 && indexPath.Row < Session.DownloadedBroadcasts.Count;

    public override void CommitEditingStyle(
        UITableView tableView,
        UITableViewCellEditingStyle editingStyle,
        NSIndexPath indexPath)
    {
        if (editingStyle != UITableViewCellEditingStyle.Delete ||
            indexPath.Section != 1 || indexPath.Row >= Session.DownloadedBroadcasts.Count) return;
        _ = Session.RemoveDownloadAsync(Session.DownloadedBroadcasts[indexPath.Row]);
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Section == 0 && indexPath.Row == 1)
        {
            if (Session.IsDownloadPaused) _ = Session.ResumeDownloadAsync(); else Session.PauseDownload();
            return;
        }
        if (indexPath.Section == 0 && indexPath.Row == 2)
        {
            Session.CancelDownload();
            return;
        }
        if (indexPath.Section == 1 && indexPath.Row < Session.DownloadedBroadcasts.Count)
            _ = Session.PlayDownloadedAsync(Session.DownloadedBroadcasts[indexPath.Row]);
    }

    protected override MobileBroadcastItem? ContextBroadcastForRow(NSIndexPath indexPath)
        => indexPath.Section == 1 && indexPath.Row < Session.DownloadedBroadcasts.Count
            ? Session.DownloadedBroadcasts[indexPath.Row]
            : null;
}
