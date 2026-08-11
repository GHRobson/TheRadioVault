using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class ExploreTimelineViewController : SessionTableViewController
{
    private readonly IReadOnlyList<MobileWikiPageSummary> _pages;
    private MobileWikiPageSummary? _selectedPage;
    private MobileWikiPageDocument? _document;
    private bool _loading;

    public ExploreTimelineViewController(
        MobileClientSession session,
        IReadOnlyList<MobileWikiPageSummary> pages,
        MobileWikiPageSummary? selected = null) : base(session)
    {
        var shows = pages
            .Where(value => value.PageType.Equals("Show", StringComparison.OrdinalIgnoreCase))
            .OrderBy(value => value.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        _pages = shows.Length > 0 ? shows : pages.OrderBy(value => value.Title).ToArray();
        _selectedPage = selected is not null && _pages.Any(value => value.PageId == selected.PageId)
            ? selected
            : _pages.FirstOrDefault();
        Title = "Show Timelines";
    }

    protected override string? PageHeading => "Show Timelines";
    protected override string PageDescription => "Travel through the history of each programme.";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        TableView.RowHeight = UITableView.AutomaticDimension;
        TableView.EstimatedRowHeight = 132;
        NavigationItem.RightBarButtonItem = new UIBarButtonItem(
            "Article",
            UIBarButtonItemStyle.Plain,
            (_, _) => OpenArticle());
        NavigationItem.RightBarButtonItem.Enabled = _selectedPage is not null;
        _ = LoadSelectedAsync();
    }

    public override nint NumberOfSections(UITableView tableView) => 2;

    public override nint RowsInSection(UITableView tableView, nint section) => section switch
    {
        0 => 1,
        1 when _selectedPage is null => 1,
        1 when _document is null => 1,
        1 => Math.Max(1, _document.Timeline.Count),
        _ => 0
    };

    public override string? TitleForHeader(UITableView tableView, nint section) => section switch
    {
        0 => "Choose a show",
        1 => _selectedPage?.Title ?? "Timeline",
        _ => null
    };

    public override string? TitleForFooter(UITableView tableView, nint section)
        => section == 0 && _pages.Count > 1
            ? $"{_pages.Count:N0} show timelines are available."
            : null;

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (indexPath.Section == 0)
        {
            if (_selectedPage is null)
                return DetailCell("timeline-no-shows", "No show timelines yet", "Explore articles can gain dated events from the desktop app.");
            var cell = new ExplorePageCardCell("timeline-show-selector");
            cell.Configure(_selectedPage);
            cell.Accessory = _pages.Count > 1
                ? UITableViewCellAccessory.DisclosureIndicator
                : UITableViewCellAccessory.None;
            return cell;
        }
        if (_document is null)
            return DetailCell(
                "timeline-loading",
                _loading ? "Loading timeline…" : "Timeline unavailable",
                _loading ? "Reading the saved Explore archive." : Session.StatusText);
        if (_document.Timeline.Count == 0)
            return DetailCell("timeline-empty", "No dated events yet", "Open the article to read its current history.");
        var eventCell = new ExploreTimelineEventCell();
        eventCell.Configure(_document.Timeline[indexPath.Row]);
        return eventCell;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Section == 0)
        {
            PresentShowPicker();
            return;
        }
        if (_document is null || indexPath.Row >= _document.Timeline.Count) return;
        PresentTimelineLinks(_document.Timeline[indexPath.Row]);
    }

    private void PresentShowPicker()
    {
        if (_pages.Count <= 1) return;
        var picker = UIAlertController.Create("Choose a show", null, UIAlertControllerStyle.ActionSheet);
        foreach (var page in _pages)
        {
            var selected = page;
            picker.AddAction(UIAlertAction.Create(page.Title, UIAlertActionStyle.Default, action =>
            {
                _selectedPage = selected;
                _document = null;
                TableView.ReloadData();
                _ = LoadSelectedAsync();
            }));
        }
        picker.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        if (picker.PopoverPresentationController is { } popover)
        {
            popover.SourceView = TableView;
            popover.SourceRect = TableView.Bounds;
        }
        PresentViewController(picker, true, null);
    }

    private void PresentTimelineLinks(MobileWikiTimelineEvent item)
    {
        var links = item.Broadcasts ?? [];
        if (links.Count == 0)
        {
            OpenArticle();
            return;
        }
        var alert = UIAlertController.Create(item.Title, item.DateDisplay, UIAlertControllerStyle.ActionSheet);
        foreach (var link in links.OrderBy(value => value.SortOrder))
        {
            var selected = link;
            alert.AddAction(UIAlertAction.Create(
                string.IsNullOrWhiteSpace(link.Label) ? "Play linked broadcast" : link.Label,
                UIAlertActionStyle.Default,
                action => _ = Session.PlayTimelineLinkAsync(selected)));
        }
        alert.AddAction(UIAlertAction.Create("Open full article", UIAlertActionStyle.Default, action => OpenArticle()));
        alert.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        if (alert.PopoverPresentationController is { } popover)
        {
            popover.SourceView = TableView;
            popover.SourceRect = TableView.Bounds;
        }
        PresentViewController(alert, true, null);
    }

    private void OpenArticle()
    {
        if (_selectedPage is not null)
            NavigationController?.PushViewController(new ExploreArticleViewController(Session, _selectedPage), true);
    }

    private async Task LoadSelectedAsync()
    {
        if (_loading || _selectedPage is null) return;
        _loading = true;
        var document = await Session.LoadExplorePageAsync(_selectedPage.PageId).ConfigureAwait(false);
        BeginInvokeOnMainThread(() =>
        {
            _document = document;
            _loading = false;
            NavigationItem.RightBarButtonItem!.Enabled = _selectedPage is not null;
            TableView.ReloadData();
        });
    }
}
