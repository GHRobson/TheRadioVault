using CoreGraphics;
using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public abstract class SessionTableViewController : UITableViewController
{
    private const nint ConnectionIndicatorTag = 8247;
    protected SessionTableViewController(MobileClientSession session)
        : base(UITableViewStyle.InsetGrouped)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Session.StateChanged += SessionOnStateChanged;
    }

    protected MobileClientSession Session { get; }
    protected virtual string? PageHeading => null;
    protected virtual string PageDescription => string.Empty;
    protected virtual bool UsesInlinePageHeading => !string.IsNullOrWhiteSpace(PageHeading);

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = RadioVaultTheme.Background;
        TableView.BackgroundColor = RadioVaultTheme.Background;
        TableView.SeparatorColor = RadioVaultTheme.Border;
        TableView.SectionHeaderTopPadding = 12;
        TableView.CellLayoutMarginsFollowReadableWidth = true;
        NavigationItem.Title = string.Empty;
        NavigationItem.BackButtonDisplayMode = UINavigationItemBackButtonDisplayMode.Minimal;
        if (!string.IsNullOrWhiteSpace(PageHeading))
            TableView.TableHeaderView = new PageHeaderView(PageHeading, PageDescription);
    }

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        NavigationItem.Title = string.Empty;
        NavigationController?.SetNavigationBarHidden(false, animated);
        UpdateConnectionIndicator();
    }

    protected virtual void ReloadSession() => TableView.ReloadData();

    protected virtual MobileBroadcastItem? ContextBroadcastForRow(NSIndexPath indexPath) => null;

    public override UIContextMenuConfiguration? GetContextMenuConfiguration(
        UITableView tableView,
        NSIndexPath indexPath,
        CGPoint point)
    {
        var broadcast = ContextBroadcastForRow(indexPath);
        if (broadcast is null) return null;

        return UIContextMenuConfiguration.Create(null, null, suggestedActions =>
        {
            var downloaded = Session.DownloadedBroadcasts.Any(item => item.EpisodeId == broadcast.EpisodeId);
            var play = UIAction.Create(
                broadcast.HasProgress ? "Resume" : "Play",
                RadioVaultIcons.Image(RadioVaultIcon.Play),
                "radiovault.play",
                action => _ = Session.PlayAsync(broadcast));
            var playNext = UIAction.Create(
                "Play Next",
                RadioVaultIcons.Image(RadioVaultIcon.PlayNext),
                "radiovault.play-next",
                action => _ = Session.AddToQueueAsync(broadcast, true));
            var addToQueue = UIAction.Create(
                "Add to Queue",
                RadioVaultIcons.Image(RadioVaultIcon.Queue),
                "radiovault.queue",
                action => _ = Session.AddToQueueAsync(broadcast));
            var favourite = UIAction.Create(
                broadcast.Source.Favourite ? "Remove from Favourites" : "Add to Favourites",
                RadioVaultIcons.Image(RadioVaultIcon.Favourite),
                "radiovault.favourite",
                action => _ = Session.SetFavouriteAsync(broadcast, !broadcast.Source.Favourite));
            var listeningStatus = UIAction.Create(
                broadcast.Source.Completed ? "Mark as Unlistened" : "Mark as Listened",
                RadioVaultIcons.Image(RadioVaultIcon.Completed),
                "radiovault.listening-status",
                action => _ = Session.SetListeningStatusAsync(broadcast, !broadcast.Source.Completed));
            var download = UIAction.Create(
                downloaded ? "Remove Download" : "Download to this iPhone",
                RadioVaultIcons.Image(downloaded ? RadioVaultIcon.Remove : RadioVaultIcon.Download),
                "radiovault.download",
                action =>
                {
                    if (downloaded) _ = Session.RemoveDownloadAsync(broadcast);
                    else _ = Session.DownloadAsync(broadcast);
                });
            if (downloaded) download.Attributes = UIMenuElementAttributes.Destructive;
            var information = UIAction.Create(
                "Broadcast Information",
                RadioVaultIcons.Image(RadioVaultIcon.Info),
                "radiovault.information",
                action => NavigationController?.PushViewController(
                    new BroadcastDetailsViewController(Session, broadcast), true));
            return UIMenu.Create("", [play, playNext, addToQueue, favourite, listeningStatus, download, information]);
        });
    }

    protected static UITableViewCell DetailCell(string reuseIdentifier, string title, string detail)
    {
        var cell = new UITableViewCell(UITableViewCellStyle.Default, reuseIdentifier);
        var content = cell.DefaultContentConfiguration;
        content.Text = title;
        content.SecondaryText = detail;
        content.SecondaryTextProperties.NumberOfLines = 2;
        RadioVaultTheme.StyleCell(cell, content);
        return cell;
    }

    private void SessionOnStateChanged(object? sender, EventArgs eventArgs)
        => BeginInvokeOnMainThread(() =>
        {
            UpdateConnectionIndicator();
            ReloadSession();
        });

    private void UpdateConnectionIndicator()
    {
        var current = (NavigationItem.RightBarButtonItems ?? [])
            .Where(item => item.Tag != ConnectionIndicatorTag)
            .ToList();
        if (Session.ShowsSyncIndicator)
        {
            var syncing = new UIBarButtonItem(
                RadioVaultIcons.Image(RadioVaultIcon.Sync, RadioVaultTheme.ActivityBlue),
                UIBarButtonItemStyle.Plain,
                (_, _) => PresentSyncExplanation())
            {
                Tag = ConnectionIndicatorTag,
                AccessibilityLabel = "Syncing the saved Radio Vault catalogue"
            };
            current.Add(syncing);
        }
        else if (Session.ShowsOfflineIndicator)
        {
            var offline = new UIBarButtonItem(
                RadioVaultIcons.Image(RadioVaultIcon.Offline, RadioVaultTheme.Settings),
                UIBarButtonItemStyle.Plain,
                (_, _) => PresentOfflineExplanation())
            {
                Tag = ConnectionIndicatorTag,
                AccessibilityLabel = "Offline · showing saved Radio Vault data"
            };
            current.Add(offline);
        }
        NavigationItem.RightBarButtonItems = current.Count == 0 ? null : current.ToArray();
    }

    private void PresentSyncExplanation()
    {
        var alert = UIAlertController.Create(
            "Syncing Radio Vault",
            "This iPhone is updating its complete saved catalogue and Explore archive. You can continue using the app while it finishes.",
            UIAlertControllerStyle.Alert);
        alert.AddAction(UIAlertAction.Create("OK", UIAlertActionStyle.Default, null));
        PresentViewController(alert, true, null);
    }

    private void PresentOfflineExplanation()
    {
        var alert = UIAlertController.Create(
            "Offline mode",
            Session.PendingSyncChanges == 0
                ? "This iPhone cannot currently reach the paired Radio Vault Server. Saved Library and Explore data remain available and will update automatically when the connection returns."
                : $"This iPhone cannot currently reach the paired Radio Vault Server. {Session.PendingSyncChanges:N0} saved change{(Session.PendingSyncChanges == 1 ? string.Empty : "s")} will upload automatically when the connection returns.",
            UIAlertControllerStyle.Alert);
        alert.AddAction(UIAlertAction.Create("OK", UIAlertActionStyle.Default, null));
        PresentViewController(alert, true, null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Session.StateChanged -= SessionOnStateChanged;
        base.Dispose(disposing);
    }
}
