using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class SavedViewController : SessionTableViewController
{
    private IReadOnlyList<MobileBroadcastItem> _favourites = [];
    private bool _loading;

    public SavedViewController(MobileClientSession session) : base(session) => Title = "Saved";
    protected override string? PageHeading => "Saved";
    protected override string PageDescription => "Favourites and Moments, together.";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        TableView.RowHeight = UITableView.AutomaticDimension;
        TableView.EstimatedRowHeight = 78;
        RefreshControl = new UIRefreshControl();
        RefreshControl.ValueChanged += async (_, _) =>
        {
            await LoadAsync();
            BeginInvokeOnMainThread(() => RefreshControl?.EndRefreshing());
        };
        _ = LoadAsync();
    }

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        _ = LoadAsync();
    }

    public override nint NumberOfSections(UITableView tableView) => 2;

    public override nint RowsInSection(UITableView tableView, nint section)
        => section == 0 ? Math.Max(1, _favourites.Count) : Math.Max(1, Session.SavedMoments.Count);

    public override string? TitleForHeader(UITableView tableView, nint section)
        => section == 0 ? "Favourites" : "Moments";

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (indexPath.Section == 0)
        {
            if (_favourites.Count == 0)
                return DetailCell("saved-favourites-empty", "No favourites yet", "Favourite a broadcast and it will appear here.");
            var cell = new BroadcastProgressCell("saved-favourite");
            cell.Configure(Session, _favourites[indexPath.Row]);
            cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
            return cell;
        }

        if (Session.SavedMoments.Count == 0)
            return DetailCell("saved-moments-empty", "No Moments yet", "Add a Moment from Now Playing to remember an exact point.");
        var moment = Session.SavedMoments[indexPath.Row];
        var title = string.IsNullOrWhiteSpace(moment.Title) ? moment.EpisodeTitle : moment.Title;
        var date = moment.AirDate?.ToString("dd MMM yyyy") ?? "Date unknown";
        var detail = $"{moment.Show} · {date} · {FormatTime(moment.PositionMs)}";
        return IconDetailCell("saved-moment", title, detail, RadioVaultIcon.Moment, disclosure: true);
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Section == 0 && indexPath.Row < _favourites.Count)
        {
            NavigationController?.PushViewController(
                new BroadcastDetailsViewController(Session, _favourites[indexPath.Row]), true);
            return;
        }
        if (indexPath.Section == 1 && indexPath.Row < Session.SavedMoments.Count)
            _ = Session.PlayMomentAsync(Session.SavedMoments[indexPath.Row]);
    }

    protected override MobileBroadcastItem? ContextBroadcastForRow(NSIndexPath indexPath)
        => indexPath.Section == 0 && indexPath.Row < _favourites.Count ? _favourites[indexPath.Row] : null;

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var favourites = await Session.BrowseCollectionAsync(null, filter: "Favourites").ConfigureAwait(false);
            await Session.LoadSavedAsync().ConfigureAwait(false);
            BeginInvokeOnMainThread(() =>
            {
                _favourites = favourites;
                TableView.ReloadData();
            });
        }
        finally { _loading = false; }
    }

    private static string FormatTime(long milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}" : $"{duration.Minutes}:{duration.Seconds:00}";
    }
}
