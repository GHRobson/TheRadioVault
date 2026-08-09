using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Web.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class BroadcastDetailsViewController : SessionTableViewController
{
    private MobileBroadcastItem _broadcast;
    private WebClientBroadcastDetails? _details;
    private bool _isDownloaded;
    private bool _disposed;

    public BroadcastDetailsViewController(MobileClientSession session, MobileBroadcastItem broadcast)
        : base(session)
    {
        _broadcast = broadcast ?? throw new ArgumentNullException(nameof(broadcast));
        Title = "Broadcast";
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        TableView.RowHeight = UITableView.AutomaticDimension;
        TableView.EstimatedRowHeight = 92;
        _ = LoadAsync();
    }

    public override nint NumberOfSections(UITableView tableView) => 4;

    public override nint RowsInSection(UITableView tableView, nint section) => section switch
    {
        0 => 1,
        1 => 2,
        2 => 1,
        3 => DetailFields().Count,
        _ => 0
    };

    public override string? TitleForHeader(UITableView tableView, nint section) => section switch
    {
        1 => "Listen and save",
        2 => "About this broadcast",
        3 => DetailFields().Count > 0 ? "Programme details" : null,
        _ => null
    };

    public override string? TitleForFooter(UITableView tableView, nint section)
        => section == 1 ? Session.DownloadStatus : null;

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (indexPath.Section == 0)
        {
            var hero = new BroadcastHeroCell();
            hero.Configure(_broadcast);
            return hero;
        }

        if (indexPath.Section == 1 && indexPath.Row == 0)
        {
            var actions = new BroadcastActionStripCell();
            actions.Configure(
                _broadcast,
                _isDownloaded,
                () => _ = Session.PlayAsync(_broadcast),
                () => _ = ToggleFavouriteAsync(),
                HandleDownloadAction);
            return actions;
        }

        if (indexPath.Section == 1)
        {
            var queue = DetailCell(
                "broadcast-queue-actions",
                "Queue options",
                "Play next or add this broadcast to the end of Up Next");
            queue.Accessory = UITableViewCellAccessory.DisclosureIndicator;
            return queue;
        }

        if (indexPath.Section == 2)
        {
            var text = _details is null
                ? (Session.IsBusy ? "Loading from Radio Vault Server…" :
                    string.IsNullOrWhiteSpace(_broadcast.Description) ? "No summary is available." : _broadcast.Description)
                : string.IsNullOrWhiteSpace(_details.Summary)
                    ? "No summary is available."
                    : _details.Summary;
            return BodyCell("summary", text);
        }

        var field = DetailFields()[indexPath.Row];
        return DetailCell("programme-field", field.Label, field.Value);
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Section == 1 && indexPath.Row == 1) PresentQueueActions();
    }

    private void PresentQueueActions()
    {
        var menu = UIAlertController.Create(
            "Queue options",
            _broadcast.Title,
            UIAlertControllerStyle.ActionSheet);
        menu.AddAction(UIAlertAction.Create("Play Next", UIAlertActionStyle.Default,
            action => _ = AddToQueueAsync(playNext: true)));
        menu.AddAction(UIAlertAction.Create("Add to End of Queue", UIAlertActionStyle.Default,
            action => _ = AddToQueueAsync(playNext: false)));
        menu.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        if (menu.PopoverPresentationController is { } popover)
        {
            popover.SourceView = TableView;
            popover.SourceRect = TableView.Bounds;
        }
        PresentViewController(menu, true, null);
    }

    private async Task LoadAsync()
    {
        var downloadedTask = Session.IsDownloadedAsync(_broadcast.EpisodeId);
        var details = await Session.LoadBroadcastDetailsAsync(_broadcast).ConfigureAwait(false);
        var downloaded = await downloadedTask.ConfigureAwait(false);
        if (_disposed) return;
        BeginInvokeOnMainThread(() =>
        {
            _details = details;
            _isDownloaded = downloaded;
            TableView.ReloadData();
        });
    }

    private async Task ToggleFavouriteAsync()
    {
        var replacement = await Session.SetFavouriteAsync(
            _broadcast, !_broadcast.Source.Favourite).ConfigureAwait(false);
        if (_disposed || replacement is null) return;
        BeginInvokeOnMainThread(() =>
        {
            _broadcast = replacement;
            TableView.ReloadData();
        });
    }

    private async Task AddToQueueAsync(bool playNext)
    {
        await Session.AddToQueueAsync(_broadcast, playNext).ConfigureAwait(false);
        if (!_disposed) BeginInvokeOnMainThread(() => TableView.ReloadData());
    }

    private void HandleDownloadAction()
    {
        if (_isDownloaded)
        {
            ConfirmRemoveDownload();
            return;
        }
        if (Session.IsDownloadPaused && Session.ActiveDownloadEpisodeId == _broadcast.EpisodeId)
        {
            _ = Session.ResumeDownloadAsync();
            return;
        }
        if (!Session.IsDownloading) _ = DownloadAsync();
    }

    private async Task DownloadAsync()
    {
        await Session.DownloadAsync(_broadcast).ConfigureAwait(false);
        var downloaded = await Session.IsDownloadedAsync(_broadcast.EpisodeId).ConfigureAwait(false);
        if (_disposed) return;
        BeginInvokeOnMainThread(() =>
        {
            _isDownloaded = downloaded;
            TableView.ReloadData();
        });
    }

    private void ConfirmRemoveDownload()
    {
        var alert = UIAlertController.Create(
            "Remove download?",
            "The server copy remains unchanged and can be downloaded again.",
            UIAlertControllerStyle.Alert);
        alert.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        alert.AddAction(UIAlertAction.Create("Remove Download", UIAlertActionStyle.Destructive, action =>
        {
            _ = RemoveDownloadAsync();
        }));
        PresentViewController(alert, true, null);
    }

    private async Task RemoveDownloadAsync()
    {
        await Session.RemoveDownloadAsync(_broadcast).ConfigureAwait(false);
        if (_disposed) return;
        BeginInvokeOnMainThread(() =>
        {
            _isDownloaded = false;
            TableView.ReloadData();
        });
    }

    private IReadOnlyList<(string Label, string Value)> DetailFields()
    {
        if (_details is null) return [];
        return new[]
            {
                ("Broadcast slot", _details.Slot),
                ("Edition", _details.Edition),
                ("Hosts", _details.Hosts),
                ("Guests", _details.Guests),
                ("Callers", _details.Callers),
                ("Mentioned people", _details.MentionedPeople),
                ("Topics", string.Join(", ", _details.Topics)),
                ("Archive notes", _details.ArchiveNotes),
                ("Research notes", _details.ResearchNotes),
                ("Personal notes", _details.PersonalNotes)
            }
            .Where(field => !string.IsNullOrWhiteSpace(field.Item2))
            .ToArray();
    }

    private static UITableViewCell BodyCell(string identifier, string text)
    {
        var cell = new UITableViewCell(UITableViewCellStyle.Default, identifier);
        var content = cell.DefaultContentConfiguration;
        content.Text = text;
        content.TextProperties.NumberOfLines = 0;
        content.TextProperties.Color = RadioVaultTheme.MutedText;
        cell.BackgroundColor = RadioVaultTheme.Surface;
        cell.ContentConfiguration = content;
        cell.SelectionStyle = UITableViewCellSelectionStyle.None;
        return cell;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _disposed = true;
        base.Dispose(disposing);
    }
}
