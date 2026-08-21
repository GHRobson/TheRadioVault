using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Core.Domain;
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
            hero.Configure(Session, _broadcast);
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
        if (field.IsEntity)
        {
            var pills = new MetadataPillsCell();
            var links = EntityLinks(field.Label);
            if (links.Count > 0)
                pills.Configure(field.Label, links, EntityColor(field.Label), link => _ = OpenEntityAsync(link));
            else
                pills.Configure(field.Label, EntityValues(field.Value), EntityColor(field.Label), entity => _ = OpenEntityAsync(entity));
            return pills;
        }
        var cell = DetailCell("programme-field", field.Label, field.Value);
        cell.SelectionStyle = UITableViewCellSelectionStyle.None;
        return cell;
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

    private IReadOnlyList<(string Label, string Value, bool IsEntity)> DetailFields()
    {
        if (_details is null) return [];
        return new[]
            {
                ("Broadcast slot", _details.Slot, false),
                ("Edition", _details.Edition, false),
                ("Hosts", _details.Hosts, true),
                ("Guests", _details.Guests, true),
                ("Callers", _details.Callers, true),
                ("Mentioned people", _details.MentionedPeople, true),
                ("Topics", string.Join(", ", _details.Topics), true),
                ("Archive notes", _details.ArchiveNotes, false),
                ("Research notes", _details.ResearchNotes, false),
                ("Personal notes", _details.PersonalNotes, false)
            }
            .Where(field => !string.IsNullOrWhiteSpace(field.Item2))
            .ToArray();
    }

    private void PresentEntityOptions(string label, string value)
    {
        var values = value
            .Split([',', ';', '|', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (values.Length == 0) return;
        if (values.Length == 1)
        {
            _ = OpenEntityAsync(values[0]);
            return;
        }
        var menu = UIAlertController.Create(label, "Choose what to explore", UIAlertControllerStyle.ActionSheet);
        foreach (var entity in values)
            menu.AddAction(UIAlertAction.Create(entity, UIAlertActionStyle.Default, action => { _ = OpenEntityAsync(entity); }));
        menu.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        if (menu.PopoverPresentationController is { } popover)
        {
            popover.SourceView = TableView;
            popover.SourceRect = TableView.Bounds;
        }
        PresentViewController(menu, true, null);
    }

    private static IReadOnlyList<string> EntityValues(string value)
        => value
            .Split([',', ';', '|', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private IReadOnlyList<ArchiveEntityLink> EntityLinks(string label)
    {
        var links = _details?.EntityLinks ?? [];
        return label switch
        {
            "Hosts" => PeopleLinks(links, "host"),
            "Guests" => PeopleLinks(links, "guest"),
            "Callers" => PeopleLinks(links, "caller"),
            "Mentioned people" => PeopleLinks(links, "mentioned"),
            "Topics" => links.Where(link => link.Kind == ArchiveEntityKind.Topic)
                .DistinctBy(link => link.EntityKey).ToArray(),
            _ => []
        };
    }

    private static IReadOnlyList<ArchiveEntityLink> PeopleLinks(
        IReadOnlyList<ArchiveEntityLink> links,
        string relationship)
        => links.Where(link => link.Kind == ArchiveEntityKind.Person &&
                               string.Equals(link.Relationship, relationship, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(link => link.EntityKey)
            .ToArray();

    private static UIColor EntityColor(string label) => label switch
    {
        "Hosts" => RadioVaultTheme.ActivityBlue,
        "Guests" => RadioVaultTheme.Moment,
        "Topics" => RadioVaultTheme.Research,
        "Callers" => RadioVaultTheme.Accent,
        _ => RadioVaultTheme.Wiki
    };

    private async Task OpenEntityAsync(string entity)
    {
        var dashboard = await Session.LoadExploreDashboardAsync().ConfigureAwait(false);
        var page = dashboard?.AllPages.FirstOrDefault(value =>
            value.Title.Equals(entity, StringComparison.CurrentCultureIgnoreCase) ||
            value.Slug.Equals(entity.Replace(' ', '-'), StringComparison.OrdinalIgnoreCase));
        BeginInvokeOnMainThread(() => NavigationController?.PushViewController(
            page is not null
                ? new ExploreArticleViewController(Session, page)
                : new EntityBroadcastsViewController(Session, entity),
            true));
    }

    private async Task OpenEntityAsync(ArchiveEntityLink link)
    {
        var target = ArchiveEntityNavigation.Resolve(link);
        if (target.Destination == ArchiveEntityDestination.LibraryShow)
        {
            BeginInvokeOnMainThread(() => NavigationController?.PushViewController(
                new EntityBroadcastsViewController(Session, target.Label), true));
            return;
        }
        if (target.Destination == ArchiveEntityDestination.Broadcast &&
            long.TryParse(target.TargetId, out var episodeId) && episodeId == _broadcast.EpisodeId)
            return;
        await OpenEntityAsync(target.Label).ConfigureAwait(false);
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
