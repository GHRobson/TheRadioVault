using CoreGraphics;
using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public abstract class SessionTableViewController : UITableViewController
{
    protected SessionTableViewController(MobileClientSession session)
        : base(UITableViewStyle.InsetGrouped)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Session.StateChanged += SessionOnStateChanged;
    }

    protected MobileClientSession Session { get; }
    protected virtual string? PageHeading => null;
    protected virtual string PageDescription => string.Empty;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = RadioVaultTheme.Background;
        TableView.BackgroundColor = RadioVaultTheme.Background;
        TableView.SeparatorColor = RadioVaultTheme.Border;
        TableView.SectionHeaderTopPadding = 12;
        if (!string.IsNullOrWhiteSpace(PageHeading))
            TableView.TableHeaderView = new PageHeaderView(PageHeading, PageDescription);
    }

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        NavigationItem.Title = Title;
        NavigationController?.SetNavigationBarHidden(false, animated);
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
                UIImage.GetSystemImage("play.fill"),
                "radiovault.play",
                action => _ = Session.PlayAsync(broadcast));
            var playNext = UIAction.Create(
                "Play Next",
                UIImage.GetSystemImage("text.insert"),
                "radiovault.play-next",
                action => _ = Session.AddToQueueAsync(broadcast, true));
            var addToQueue = UIAction.Create(
                "Add to Queue",
                UIImage.GetSystemImage("text.badge.plus"),
                "radiovault.queue",
                action => _ = Session.AddToQueueAsync(broadcast));
            var favourite = UIAction.Create(
                broadcast.Source.Favourite ? "Remove from Favourites" : "Add to Favourites",
                UIImage.GetSystemImage(broadcast.Source.Favourite ? "heart.slash" : "heart"),
                "radiovault.favourite",
                action => _ = Session.SetFavouriteAsync(broadcast, !broadcast.Source.Favourite));
            var download = UIAction.Create(
                downloaded ? "Remove Download" : "Download to this iPhone",
                UIImage.GetSystemImage(downloaded ? "trash" : "arrow.down.circle"),
                "radiovault.download",
                action =>
                {
                    if (downloaded) _ = Session.RemoveDownloadAsync(broadcast);
                    else _ = Session.DownloadAsync(broadcast);
                });
            if (downloaded) download.Attributes = UIMenuElementAttributes.Destructive;
            var information = UIAction.Create(
                "Broadcast Information",
                UIImage.GetSystemImage("info.circle"),
                "radiovault.information",
                action => NavigationController?.PushViewController(
                    new BroadcastDetailsViewController(Session, broadcast), true));
            return UIMenu.Create("", [play, playNext, addToQueue, favourite, download, information]);
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
        => BeginInvokeOnMainThread(ReloadSession);

    protected override void Dispose(bool disposing)
    {
        if (disposing) Session.StateChanged -= SessionOnStateChanged;
        base.Dispose(disposing);
    }
}
