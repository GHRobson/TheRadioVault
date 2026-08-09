using Foundation;
using TheRadioVault.Client.Mobile;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class DownloadsViewController : SessionTableViewController
{
    public DownloadsViewController(MobileClientSession session) : base(session) => Title = "Downloads";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Always;
        NavigationItem.RightBarButtonItem = EditButtonItem;
    }

    public override nint NumberOfSections(UITableView tableView) => 1;
    public override nint RowsInSection(UITableView tableView, nint section)
        => Math.Max(1, Session.DownloadedBroadcasts.Count);

    public override string? TitleForFooter(UITableView tableView, nint section) => Session.DownloadStatus;

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (Session.DownloadedBroadcasts.Count == 0)
            return DetailCell("empty-downloads", "No downloads yet", "Open a broadcast in Library and choose Download to this iPhone.");
        var item = Session.DownloadedBroadcasts[indexPath.Row];
        var cell = DetailCell("download", item.Title, $"{item.Subtitle} · {item.Status}");
        cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return cell;
    }

    public override bool CanEditRow(UITableView tableView, NSIndexPath indexPath)
        => indexPath.Row < Session.DownloadedBroadcasts.Count;

    public override void CommitEditingStyle(
        UITableView tableView,
        UITableViewCellEditingStyle editingStyle,
        NSIndexPath indexPath)
    {
        if (editingStyle != UITableViewCellEditingStyle.Delete ||
            indexPath.Row >= Session.DownloadedBroadcasts.Count) return;
        _ = Session.RemoveDownloadAsync(Session.DownloadedBroadcasts[indexPath.Row]);
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Row < Session.DownloadedBroadcasts.Count)
            _ = Session.PlayDownloadedAsync(Session.DownloadedBroadcasts[indexPath.Row]);
    }
}
