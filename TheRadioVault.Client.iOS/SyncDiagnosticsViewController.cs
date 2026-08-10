using Foundation;
using TheRadioVault.Client.Mobile;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class SyncDiagnosticsViewController : SessionTableViewController
{
    public SyncDiagnosticsViewController(MobileClientSession session) : base(session) => Title = "Sync";

    protected override string? PageHeading => "Sync";
    protected override string PageDescription => "Connection, saved catalogue and pending changes.";

    public override nint NumberOfSections(UITableView tableView) => 3;

    public override nint RowsInSection(UITableView tableView, nint section) => section switch
    {
        0 => 3,
        1 => string.IsNullOrWhiteSpace(Session.LastSyncError) ? 3 : 4,
        _ => 1
    };

    public override string? TitleForHeader(UITableView tableView, nint section) => section switch
    {
        0 => "Saved on this iPhone",
        1 => "Last synchronisation",
        _ => "Actions"
    };

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (indexPath.Section == 0)
            return indexPath.Row switch
            {
                0 => DetailCell("sync-connection", "Connection", Session.IsLiveConnected ? "Live connection" : "Offline · using saved data"),
                1 => DetailCell("sync-library", "Library Catalogue", $"{Session.CachedBroadcastCount:N0} broadcasts cached"),
                _ => DetailCell("sync-explore", "Explore Archive", $"{Session.CachedExplorePageCount:N0} pages cached")
            };
        if (indexPath.Section == 1)
            return indexPath.Row switch
            {
                0 => DetailCell("sync-pending", "Pending Changes", Session.PendingSyncChanges == 0 ? "None" : $"{Session.PendingSyncChanges:N0} waiting"),
                1 => DetailCell("sync-success", "Last Successful Sync", FormatDate(Session.LastSuccessfulSyncAt)),
                2 => DetailCell("sync-attempt", "Last Attempt", FormatDate(Session.LastSyncAttemptAt)),
                _ => DetailCell("sync-error", "Last Error", Session.LastSyncError)
            };
        var cell = DetailCell("sync-now", Session.IsMetadataSyncing ? "Syncing…" : "Sync Now", "Uploads saved changes and checks the complete catalogue");
        cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return cell;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Section == 2 && !Session.IsMetadataSyncing) _ = Session.RetrySyncAsync();
    }

    private static string FormatDate(DateTimeOffset? value)
        => value is null ? "Not yet" : value.Value.ToLocalTime().ToString("g");
}
