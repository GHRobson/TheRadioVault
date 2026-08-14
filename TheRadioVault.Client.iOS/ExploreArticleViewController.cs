using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Core.Domain;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class ExploreArticleViewController : SessionTableViewController
{
    private readonly MobileWikiPageSummary _summary;
    private MobileWikiPageDocument? _document;
    private IReadOnlyList<MobileExploreImage> _images = [];
    private IReadOnlyList<MobileWikiPageSummary> _linkPages = [];
    private bool _loading;

    public ExploreArticleViewController(MobileClientSession session, MobileWikiPageSummary summary) : base(session)
    {
        _summary = summary ?? throw new ArgumentNullException(nameof(summary));
        Title = summary.Title;
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        TableView.RowHeight = UITableView.AutomaticDimension;
        TableView.EstimatedRowHeight = 120;
        _ = LoadAsync();
    }

    public override nint NumberOfSections(UITableView tableView) => _document is null ? 1 : 6;

    public override nint RowsInSection(UITableView tableView, nint section) => section switch
    {
        0 => 1,
        1 => _images.Count > 0 ? 1 : 0,
        2 => _document is null ? 0 : 1,
        3 => Math.Max(0, _images.Count - 1),
        4 => _document?.Timeline.Count ?? 0,
        5 => _document is null ? 0 : 1,
        _ => 0
    };

    public override string? TitleForHeader(UITableView tableView, nint section) => section switch
    {
        3 when _images.Count > 1 => "More from the archive",
        4 when _document?.Timeline.Count > 0 => "Timeline",
        5 => "About this article",
        _ => null
    };

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (indexPath.Section == 0)
        {
            var header = new ExploreArticleHeaderCell();
            header.Configure(_summary, _document, _loading, Session.StatusText);
            return header;
        }
        if (indexPath.Section == 1)
            return new ExploreArticleImageCell(_images[0]);
        if (indexPath.Section == 2)
            return new ExploreArticleBodyCell(
                _document!.BodyMarkdown,
                InlineLinkTargets(),
                target => _ = OpenInlineLinkAsync(target));
        if (indexPath.Section == 3)
            return new ExploreArticleImageCell(_images[indexPath.Row + 1]);
        if (indexPath.Section == 4)
        {
            var item = _document!.Timeline[indexPath.Row];
            var timeline = new ExploreTimelineEventCell();
            timeline.Configure(item);
            return timeline;
        }
        return ArticleTextCell(
            "explore-article-about",
            "Article details",
            $"{_document!.PageType} · {_document.Status} · revision {_document.Revision:N0}\n" +
            $"Updated {_document.UpdatedAt:dd MMMM yyyy} by {_document.LastEditor}" +
            (_document.Aliases.Count == 0 ? string.Empty : $"\nAlso known as: {string.Join(" · ", _document.Aliases)}"),
            14);
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Section != 4 || _document is null || indexPath.Row >= _document.Timeline.Count) return;
        PresentTimelineLinks(_document.Timeline[indexPath.Row]);
    }

    private static UITableViewCell ArticleTextCell(
        string identifier,
        string title,
        string detail,
        nfloat detailSize)
    {
        var cell = new UITableViewCell(UITableViewCellStyle.Default, identifier);
        var content = cell.DefaultContentConfiguration;
        content.Text = title;
        content.SecondaryText = detail;
        content.TextProperties.Font = UIFont.BoldSystemFontOfSize(20)!;
        content.TextProperties.NumberOfLines = 0;
        content.SecondaryTextProperties.Font = UIFont.SystemFontOfSize(detailSize)!;
        content.SecondaryTextProperties.Color = RadioVaultTheme.Text;
        content.SecondaryTextProperties.NumberOfLines = 0;
        RadioVaultTheme.StyleCell(cell, content);
        cell.SelectionStyle = UITableViewCellSelectionStyle.None;
        return cell;
    }

    private void PresentTimelineLinks(MobileWikiTimelineEvent item)
    {
        var links = item.Broadcasts ?? [];
        if (links.Count == 0) return;
        var alert = UIAlertController.Create(item.Title, "Choose a preserved broadcast link.", UIAlertControllerStyle.ActionSheet);
        foreach (var link in links.OrderBy(value => value.SortOrder))
        {
            var selected = link;
            alert.AddAction(UIAlertAction.Create(
                string.IsNullOrWhiteSpace(link.Label) ? "Play linked broadcast" : link.Label,
                UIAlertActionStyle.Default,
                action => _ = Session.PlayTimelineLinkAsync(selected)));
        }
        alert.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        PresentViewController(alert, true, null);
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        TableView.ReloadData();
        var dashboardTask = Session.LoadExploreDashboardAsync();
        var document = await Session.LoadExplorePageAsync(_summary.PageId).ConfigureAwait(false);
        var dashboard = await dashboardTask.ConfigureAwait(false);
        var images = document is null
            ? Array.Empty<MobileExploreImage>()
            : await Session.LoadExploreImagesAsync(document).ConfigureAwait(false);
        BeginInvokeOnMainThread(() =>
        {
            _document = document;
            _images = images;
            _linkPages = dashboard?.AllPages ?? [];
            _loading = false;
            if (document is not null)
            {
                Title = document.Title;
            }
            TableView.ReloadData();
        });
    }

    private IReadOnlyList<string> InlineLinkTargets()
        => (_document?.EntityLinks ?? [])
            .Where(value => value.Relationship.StartsWith("inline", StringComparison.OrdinalIgnoreCase))
            .Select(value => value.Label)
            .Concat(_linkPages
            .Where(value => value.PageId != _summary.PageId)
            .Select(value => value.Title))
            .Concat(Session.LibraryCollectionsFor(false).Select(value => value.CollectionName))
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.Trim().Length >= 3)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(value => value.Length)
            .ToArray();

    private async Task OpenInlineLinkAsync(string target)
    {
        var normalized = NormalizeTarget(target);
        var typedLink = (_document?.EntityLinks ?? []).FirstOrDefault(value =>
            value.Relationship.StartsWith("inline", StringComparison.OrdinalIgnoreCase) &&
            NormalizeTarget(value.Label) == normalized);
        if (typedLink is not null)
        {
            await OpenEntityLinkAsync(typedLink).ConfigureAwait(false);
            return;
        }

        var page = _linkPages.FirstOrDefault(value =>
            NormalizeTarget(value.Title) == normalized || NormalizeTarget(value.Slug) == normalized);
        if (page is not null)
        {
            BeginInvokeOnMainThread(() => NavigationController?.PushViewController(
                new ExploreArticleViewController(Session, page), true));
            return;
        }

        var collection = Session.LibraryCollectionsFor(false).FirstOrDefault(value =>
            NormalizeTarget(value.CollectionName) == normalized);
        if (collection is not null)
        {
            BeginInvokeOnMainThread(() => NavigationController?.PushViewController(
                new ShowLibraryViewController(Session, collection.CollectionId, collection.CollectionName), true));
            return;
        }

        await Task.Yield();
        BeginInvokeOnMainThread(() => NavigationController?.PushViewController(
            new EntityBroadcastsViewController(Session, target), true));
    }

    private async Task OpenEntityLinkAsync(ArchiveEntityLink link)
    {
        var target = ArchiveEntityNavigation.Resolve(link);
        if (target.Destination == ArchiveEntityDestination.Broadcast &&
            long.TryParse(target.TargetId, out var episodeId))
        {
            var broadcast = await Session.LoadBroadcastAsync(episodeId).ConfigureAwait(false);
            if (broadcast is not null)
            {
                BeginInvokeOnMainThread(() => NavigationController?.PushViewController(
                    new BroadcastDetailsViewController(Session, broadcast), true));
                return;
            }
        }
        if (target.Destination == ArchiveEntityDestination.LibraryShow &&
            int.TryParse(target.TargetId, out var collectionId))
        {
            BeginInvokeOnMainThread(() => NavigationController?.PushViewController(
                new ShowLibraryViewController(Session, collectionId, target.Label), true));
            return;
        }
        if (Guid.TryParse(target.TargetId, out var pageId))
        {
            var page = _linkPages.FirstOrDefault(value => value.PageId == pageId);
            if (page is not null)
            {
                BeginInvokeOnMainThread(() => NavigationController?.PushViewController(
                    new ExploreArticleViewController(Session, page), true));
                return;
            }
        }
        await OpenInlineLinkFallbackAsync(target.Label).ConfigureAwait(false);
    }

    private async Task OpenInlineLinkFallbackAsync(string label)
    {
        await Task.Yield();
        BeginInvokeOnMainThread(() => NavigationController?.PushViewController(
            new EntityBroadcastsViewController(Session, label), true));
    }

    protected override void ReloadSession()
    {
        // Playback and catalogue synchronization do not change the immutable
        // article currently being read. Rebuilding its attributed text and
        // image cells on every sync tick caused visible flashes and scroll
        // jumps. Explicit article refreshes still update the table in LoadAsync.
    }

    private static string NormalizeTarget(string value)
    {
        var text = Uri.UnescapeDataString((value ?? string.Empty).Trim());
        if (text.StartsWith("wiki:", StringComparison.OrdinalIgnoreCase)) text = text[5..];
        return string.Join(' ', new string(text.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
