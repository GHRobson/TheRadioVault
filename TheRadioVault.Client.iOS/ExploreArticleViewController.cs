using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class ExploreArticleViewController : SessionTableViewController
{
    private readonly MobileWikiPageSummary _summary;
    private MobileWikiPageDocument? _document;
    private IReadOnlyList<MobileExploreImage> _images = [];
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
            return new ExploreArticleBodyCell(_document!.BodyMarkdown);
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
        var document = await Session.LoadExplorePageAsync(_summary.PageId).ConfigureAwait(false);
        var images = document is null
            ? Array.Empty<MobileExploreImage>()
            : await Session.LoadExploreImagesAsync(document).ConfigureAwait(false);
        BeginInvokeOnMainThread(() =>
        {
            _document = document;
            _images = images;
            _loading = false;
            if (document is not null)
            {
                Title = document.Title;
            }
            TableView.ReloadData();
        });
    }
}
