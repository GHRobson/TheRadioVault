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
        var isRootTab = TabBarController is not null &&
                        NavigationController?.ViewControllers is { Length: > 0 } controllers &&
                        controllers[0] == this;
        NavigationController?.SetNavigationBarHidden(isRootTab, animated);
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
            var playlistActions = Session.SavedCollections
                .Where(collection => collection.Kind.Equals("Manual", StringComparison.OrdinalIgnoreCase))
                .Select(collection => UIAction.Create(
                    collection.Name,
                    RadioVaultIcons.Image(RadioVaultIcon.UpNext),
                    "radiovault.playlist." + collection.Id,
                    action => _ = Session.AddToSavedCollectionAsync(broadcast, collection)))
                .Cast<UIMenuElement>()
                .ToList();
            playlistActions.Add(UIAction.Create(
                "New Playlist…",
                RadioVaultIcons.Image(RadioVaultIcon.Add),
                "radiovault.playlist.new",
                action => PromptForNewPlaylistAndAdd(broadcast)));
            var addToPlaylist = UIMenu.Create(
                "Add to Playlist",
                RadioVaultIcons.Image(RadioVaultIcon.UpNext),
                UIMenuIdentifier.None,
                (UIMenuOptions)0,
                playlistActions.ToArray());
            var favourite = UIAction.Create(
                broadcast.Source.Favourite ? "Remove from Favourites" : "Add to Favourites",
                RadioVaultIcons.Image(RadioVaultIcon.Favourite),
                "radiovault.favourite",
                action => _ = Session.SetFavouriteAsync(broadcast, !broadcast.Source.Favourite));
            var markListened = UIAction.Create(
                "Mark as Listened",
                RadioVaultIcons.Image(RadioVaultIcon.Completed),
                "radiovault.mark-listened",
                action => _ = Session.SetListeningStatusAsync(broadcast, true));
            var markUnlistened = UIAction.Create(
                "Mark as Unlistened",
                RadioVaultIcons.Image(RadioVaultIcon.Radio, RadioVaultTheme.MutedText),
                "radiovault.mark-unlistened",
                action => _ = Session.SetListeningStatusAsync(broadcast, false));
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
            var actions = new List<UIMenuElement> { play, playNext, addToQueue };
            actions.Add(addToPlaylist);
            actions.AddRange([favourite, markListened, markUnlistened, download, information]);
            return UIMenu.Create("", actions.ToArray());
        });
    }

    private void PromptForNewPlaylistAndAdd(MobileBroadcastItem broadcast)
    {
        var alert = UIAlertController.Create(
            "New Playlist",
            "Create a playlist and add this broadcast to it.",
            UIAlertControllerStyle.Alert);
        alert.AddTextField(field =>
        {
            field.Placeholder = "Playlist name";
            field.ClearButtonMode = UITextFieldViewMode.WhileEditing;
        });
        alert.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        alert.AddAction(UIAlertAction.Create("Create", UIAlertActionStyle.Default, async _ =>
        {
            var name = alert.TextFields?.FirstOrDefault()?.Text?.Trim() ?? string.Empty;
            if (name.Length == 0) return;
            var created = await Session.CreateSavedCollectionAsync(name).ConfigureAwait(false);
            if (created is not null)
                await Session.AddToSavedCollectionAsync(broadcast, created.Summary).ConfigureAwait(false);
        }));
        PresentViewController(alert, true, null);
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

    protected static UITableViewCell IconDetailCell(
        string reuseIdentifier,
        string title,
        string detail,
        RadioVaultIcon icon,
        bool disclosure = false)
    {
        var cell = new UITableViewCell(UITableViewCellStyle.Default, reuseIdentifier);
        var content = cell.DefaultContentConfiguration;
        content.Text = title;
        content.SecondaryText = detail;
        content.SecondaryTextProperties.NumberOfLines = 2;
        content.Image = RadioVaultIcons.Image(icon);
        RadioVaultTheme.StyleCell(cell, content);
        cell.Accessory = disclosure ? UITableViewCellAccessory.DisclosureIndicator : UITableViewCellAccessory.None;
        cell.SelectionStyle = disclosure ? UITableViewCellSelectionStyle.Default : UITableViewCellSelectionStyle.None;
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
        if (TableView.TableHeaderView is IConnectionStatusView header)
        {
            header.SetConnectionState(Session.ShowsSyncIndicator, Session.ShowsOfflineIndicator);
        }
        else if (Session.ShowsSyncIndicator || Session.ShowsOfflineIndicator)
        {
            var image = new UIImageView
            {
                Image = Session.ShowsSyncIndicator
                    ? RadioVaultIcons.Image(RadioVaultIcon.Sync, RadioVaultTheme.ActivityBlue, 21)
                    : RadioVaultIcons.Image(RadioVaultIcon.Offline, RadioVaultTheme.Settings, 21),
                ContentMode = UIViewContentMode.Center,
                Frame = new CGRect(0, 0, 24, 32),
                AccessibilityLabel = Session.ShowsSyncIndicator
                    ? "Syncing the saved Radio Vault catalogue"
                    : "Offline · showing saved Radio Vault data"
            };
            current.Add(new UIBarButtonItem(image) { Tag = ConnectionIndicatorTag });
        }
        NavigationItem.RightBarButtonItems = current.Count == 0 ? null : current.ToArray();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Session.StateChanged -= SessionOnStateChanged;
        base.Dispose(disposing);
    }
}
