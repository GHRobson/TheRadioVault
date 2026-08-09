using Foundation;
using TheRadioVault.Client.Mobile;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class DownloadsViewController : SessionTableViewController
{
    private readonly UISwitch _wifiOnlySwitch = new();

    public DownloadsViewController(MobileClientSession session) : base(session) => Title = "Downloads";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Always;
        NavigationItem.RightBarButtonItem = EditButtonItem;
        _wifiOnlySwitch.ValueChanged += (_, _) => Session.WifiOnlyDownloads = _wifiOnlySwitch.On;
        _wifiOnlySwitch.On = Session.WifiOnlyDownloads;
    }

    protected override void ReloadSession()
    {
        _wifiOnlySwitch.On = Session.WifiOnlyDownloads;
        base.ReloadSession();
    }

    public override nint NumberOfSections(UITableView tableView) => 3;
    public override nint RowsInSection(UITableView tableView, nint section)
        => section switch
        {
            0 => Session.ActiveDownloadEpisodeId is null ? 1 : 3,
            1 => 2,
            _ => Math.Max(1, Session.DownloadedBroadcasts.Count)
        };

    public override string? TitleForHeader(UITableView tableView, nint section) => section switch
    {
        0 => "Activity",
        1 => "Storage & Network",
        _ => "Downloaded"
    };

    public override string? TitleForFooter(UITableView tableView, nint section) => section switch
    {
        0 => Session.DownloadStatus,
        1 => "When Wi-Fi Only is enabled, cellular downloads are blocked. Paused downloads retain verified partial media and continue from the saved byte position.",
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
            actionContent.TextProperties.Color = indexPath.Row == 2 ? UIColor.SystemRed : UIColor.SystemBlue;
            actionContent.TextProperties.Alignment = UIListContentTextAlignment.Center;
            action.ContentConfiguration = actionContent;
            return action;
        }

        if (indexPath.Section == 1)
        {
            if (indexPath.Row == 0)
            {
                var wifi = new UITableViewCell(UITableViewCellStyle.Default, "wifi-only");
                var content = wifi.DefaultContentConfiguration;
                content.Text = "Wi-Fi Only";
                content.SecondaryText = "Prevent downloads on cellular data";
                wifi.ContentConfiguration = content;
                wifi.AccessoryView = _wifiOnlySwitch;
                wifi.SelectionStyle = UITableViewCellSelectionStyle.None;
                return wifi;
            }
            return DetailCell(
                "download-storage",
                "Radio Vault Storage",
                Session.PendingDownloadBytes > 0
                    ? $"{Session.DownloadStorageText} · {FormatBytes(Session.PendingDownloadBytes)} resumable"
                    : Session.DownloadStorageText);
        }

        if (Session.DownloadedBroadcasts.Count == 0)
            return DetailCell("empty-downloads", "No downloads yet", "Open a broadcast in Library and choose Download to this iPhone.");
        var item = Session.DownloadedBroadcasts[indexPath.Row];
        var cell = DetailCell("download", item.Title, $"{item.Subtitle} · {item.Status}");
        cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return cell;
    }

    public override bool CanEditRow(UITableView tableView, NSIndexPath indexPath)
        => indexPath.Section == 2 && indexPath.Row < Session.DownloadedBroadcasts.Count;

    public override void CommitEditingStyle(
        UITableView tableView,
        UITableViewCellEditingStyle editingStyle,
        NSIndexPath indexPath)
    {
        if (editingStyle != UITableViewCellEditingStyle.Delete ||
            indexPath.Section != 2 || indexPath.Row >= Session.DownloadedBroadcasts.Count) return;
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
        if (indexPath.Section == 2 && indexPath.Row < Session.DownloadedBroadcasts.Count)
            _ = Session.PlayDownloadedAsync(Session.DownloadedBroadcasts[indexPath.Row]);
    }

    private static string FormatBytes(long value)
        => value >= 1024L * 1024L * 1024L ? $"{value / (1024d * 1024d * 1024d):0.0} GB"
            : value >= 1024L * 1024L ? $"{value / (1024d * 1024d):0.0} MB"
            : $"{Math.Max(0, value) / 1024d:0} KB";
}
