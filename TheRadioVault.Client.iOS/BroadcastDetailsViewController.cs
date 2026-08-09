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
        _ = LoadAsync();
    }

    public override nint NumberOfSections(UITableView tableView) => 4;

    public override nint RowsInSection(UITableView tableView, nint section) => section switch
    {
        0 => 3,
        1 => 5,
        2 => 1,
        3 => DetailFields().Count,
        _ => 0
    };

    public override string? TitleForHeader(UITableView tableView, nint section) => section switch
    {
        0 => _broadcast.Title,
        1 => "Actions",
        2 => "Summary",
        3 => DetailFields().Count > 0 ? "Programme details" : null,
        _ => null
    };

    public override string? TitleForFooter(UITableView tableView, nint section)
        => section == 1 ? Session.DownloadStatus : null;

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (indexPath.Section == 0)
        {
            var date = _broadcast.Source.AirDate?.ToString("dddd, d MMMM yyyy") ?? "Date unknown";
            return indexPath.Row switch
            {
                0 => DetailCell("show", "Show", _broadcast.Source.CollectionName),
                1 => DetailCell("date", "Air date", date),
                _ => DetailCell("listening", "Listening", _broadcast.Status)
            };
        }

        if (indexPath.Section == 1)
        {
            var cell = new UITableViewCell(UITableViewCellStyle.Default, "broadcast-action");
            var content = cell.DefaultContentConfiguration;
            (content.Text, content.Image) = indexPath.Row switch
            {
                0 => (_broadcast.HasProgress ? "Resume" : "Play", UIImage.GetSystemImage("play.fill")),
                1 => (_broadcast.Source.Favourite ? "Remove from Favourites" : "Add to Favourites",
                    UIImage.GetSystemImage(_broadcast.Source.Favourite ? "heart.fill" : "heart")),
                2 => ("Play Next", UIImage.GetSystemImage("text.line.first.and.arrowtriangle.forward")),
                3 => ("Play Last", UIImage.GetSystemImage("text.badge.plus")),
                _ when Session.IsDownloading && Session.ActiveDownloadEpisodeId == _broadcast.EpisodeId
                    => ("Downloading…", UIImage.GetSystemImage("arrow.down.circle")),
                _ when Session.IsDownloadPaused && Session.ActiveDownloadEpisodeId == _broadcast.EpisodeId
                    => ("Resume Download", UIImage.GetSystemImage("arrow.clockwise.circle")),
                _ when _isDownloaded => ("Remove Download", UIImage.GetSystemImage("trash")),
                _ => ("Download to this iPhone", UIImage.GetSystemImage("arrow.down.circle"))
            };
            content.TextProperties.Color = indexPath.Row == 4 && _isDownloaded
                ? UIColor.SystemRed
                : UIColor.SystemBlue;
            cell.ContentConfiguration = content;
            cell.SelectionStyle = Session.IsBusy || Session.IsDownloading
                ? UITableViewCellSelectionStyle.None
                : UITableViewCellSelectionStyle.Default;
            return cell;
        }

        if (indexPath.Section == 2)
        {
            var text = _details is null
                ? (Session.IsBusy ? "Loading from Radio Vault Server…" : "No summary is available.")
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
        if (indexPath.Section != 1 || Session.IsBusy || Session.IsDownloading) return;
        switch (indexPath.Row)
        {
            case 0:
                _ = Session.PlayAsync(_broadcast);
                break;
            case 1:
                _ = ToggleFavouriteAsync();
                break;
            case 2:
                _ = AddToQueueAsync(playNext: true);
                break;
            case 3:
                _ = AddToQueueAsync(playNext: false);
                break;
            case 4 when _isDownloaded:
                ConfirmRemoveDownload();
                break;
            case 4 when Session.IsDownloadPaused && Session.ActiveDownloadEpisodeId == _broadcast.EpisodeId:
                _ = Session.ResumeDownloadAsync();
                break;
            case 4:
                _ = DownloadAsync();
                break;
        }
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
        content.TextProperties.Color = UIColor.SecondaryLabel;
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
