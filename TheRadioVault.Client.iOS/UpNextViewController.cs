using CoreGraphics;
using Foundation;
using TheRadioVault.Client.Mobile;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class UpNextViewController : SessionTableViewController
{
    public UpNextViewController(MobileClientSession session) : base(session) => Title = "Up Next";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Always;
        TableView.RowHeight = UITableView.AutomaticDimension;
        TableView.EstimatedRowHeight = 74;
        NavigationItem.LeftBarButtonItem = new UIBarButtonItem(
            "Now Playing",
            UIBarButtonItemStyle.Plain,
            (_, _) => ReturnToNowPlaying());
        NavigationItem.LeftBarButtonItem.AccessibilityLabel = "Back to Now Playing";
        var clearButton = new UIBarButtonItem(
            "Clear",
            UIBarButtonItemStyle.Plain,
            (_, _) => ConfirmClear());
        NavigationItem.RightBarButtonItems = [EditButtonItem, clearButton];
        RefreshControl = new UIRefreshControl();
        RefreshControl.ValueChanged += async (_, _) =>
        {
            await Session.RefreshQueueAsync().ConfigureAwait(false);
            BeginInvokeOnMainThread(() => RefreshControl?.EndRefreshing());
        };
    }

    public override nint NumberOfSections(UITableView tableView) => 1;
    public override nint RowsInSection(UITableView tableView, nint section)
        => Math.Max(1, Session.QueueItems.Count);

    public override string? TitleForFooter(UITableView tableView, nint section)
        => Session.QueueItems.Count == 0
            ? "Choose Play Next or Play Last from any broadcast. The queue is shared with every Radio Vault client."
            : "Drag while editing to reorder the server-owned queue.";

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (Session.QueueItems.Count == 0)
            return DetailCell("empty-queue", "Nothing Up Next", "Add a broadcast from Library or Broadcast Details.");
        var item = Session.QueueItems[indexPath.Row];
        var cell = new BroadcastProgressCell("queue-item");
        cell.Configure(item.Episode, $"{item.Position + 1}. {item.Episode.Show} · {item.Episode.Status}");
        cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return cell;
    }

    public override UIContextMenuConfiguration? GetContextMenuConfiguration(
        UITableView tableView,
        NSIndexPath indexPath,
        CGPoint point)
    {
        if (indexPath.Row >= Session.QueueItems.Count) return null;
        var queueItem = Session.QueueItems[indexPath.Row];
        return UIContextMenuConfiguration.Create(null, null, suggestedActions =>
        {
            var play = UIAction.Create(
                "Play Now",
                RadioVaultIcons.Image(RadioVaultIcon.Play),
                "radiovault.queue.play",
                action => _ = Session.PlayQueueItemAsync(queueItem));
            var remove = UIAction.Create(
                "Remove from Up Next",
                RadioVaultIcons.Image(RadioVaultIcon.Remove),
                "radiovault.queue.remove",
                action => _ = Session.RemoveQueueItemAsync(queueItem));
            remove.Attributes = UIMenuElementAttributes.Destructive;
            return UIMenu.Create("", [play, remove]);
        });
    }

    public override bool CanEditRow(UITableView tableView, NSIndexPath indexPath)
        => indexPath.Row < Session.QueueItems.Count;

    public override bool CanMoveRow(UITableView tableView, NSIndexPath indexPath)
        => indexPath.Row < Session.QueueItems.Count;

    public override void MoveRow(UITableView tableView, NSIndexPath sourceIndexPath, NSIndexPath destinationIndexPath)
    {
        if (sourceIndexPath.Row >= Session.QueueItems.Count) return;
        _ = Session.MoveQueueItemAsync(Session.QueueItems[sourceIndexPath.Row], (int)destinationIndexPath.Row);
    }

    public override void CommitEditingStyle(
        UITableView tableView,
        UITableViewCellEditingStyle editingStyle,
        NSIndexPath indexPath)
    {
        if (editingStyle == UITableViewCellEditingStyle.Delete && indexPath.Row < Session.QueueItems.Count)
            _ = Session.RemoveQueueItemAsync(Session.QueueItems[indexPath.Row]);
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Row < Session.QueueItems.Count)
            _ = Session.PlayQueueItemAsync(Session.QueueItems[indexPath.Row]);
    }

    private void ConfirmClear()
    {
        if (Session.QueueItems.Count == 0) return;
        var alert = UIAlertController.Create(
            "Clear Up Next?",
            "This removes every item from the shared Radio Vault queue.",
            UIAlertControllerStyle.Alert);
        alert.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        alert.AddAction(UIAlertAction.Create("Clear Queue", UIAlertActionStyle.Destructive, action =>
        {
            _ = Session.ClearQueueAsync();
        }));
        PresentViewController(alert, true, null);
    }

    private void ReturnToNowPlaying()
    {
        var existing = NavigationController?.ViewControllers?
            .OfType<NowPlayingViewController>()
            .LastOrDefault();
        if (existing is not null)
        {
            NavigationController?.PopToViewController(existing, true);
            return;
        }
        NavigationController?.PushViewController(new NowPlayingViewController(Session), true);
    }
}
