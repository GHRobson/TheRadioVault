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

    public override nint NumberOfSections(UITableView tableView) => _document is null ? 1 : 5;

    public override nint RowsInSection(UITableView tableView, nint section) => section switch
    {
        0 => 1,
        1 => _images.Count,
        2 => _document is null ? 0 : 1,
        3 => _document?.Timeline.Count ?? 0,
        4 => _document?.Aliases.Count > 0 ? 1 : 0,
        _ => 0
    };

    public override string? TitleForHeader(UITableView tableView, nint section) => section switch
    {
        1 when _images.Count > 0 => "Images",
        2 => "Article",
        3 when _document?.Timeline.Count > 0 => "Timeline",
        4 when _document?.Aliases.Count > 0 => "Also known as",
        _ => null
    };

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (indexPath.Section == 0)
        {
            var document = _document;
            return ArticleTextCell(
                "explore-article-summary",
                document?.Title ?? _summary.Title,
                document is null
                    ? (_loading ? "Loading article…" : Session.StatusText)
                    : $"{document.Summary}\n\n{document.PageType} · {document.Status} · revision {document.Revision:N0}\nLast edited by {document.LastEditor}",
                23);
        }
        if (indexPath.Section == 1)
            return new ExploreArticleImageCell(_images[indexPath.Row]);
        if (indexPath.Section == 2)
            return ArticleTextCell(
                "explore-article-body",
                string.Empty,
                FormatMarkdown(_document!.BodyMarkdown),
                17);
        if (indexPath.Section == 3)
        {
            var item = _document!.Timeline[indexPath.Row];
            return ArticleTextCell(
                "explore-article-timeline",
                $"{item.YearText} · {item.Title}",
                item.Summary,
                16);
        }
        return ArticleTextCell(
            "explore-article-aliases",
            string.Empty,
            string.Join(" · ", _document!.Aliases),
            15);
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

    private static string FormatMarkdown(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "This article does not have any body text yet.";
        var lines = markdown.Replace("\r", string.Empty).Split('\n');
        return string.Join("\n", lines.Select(line =>
        {
            var value = line.TrimEnd();
            while (value.StartsWith('#')) value = value[1..].TrimStart();
            value = value.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty);
            if (value.StartsWith("- ")) value = "• " + value[2..];
            return value;
        })).Trim();
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
