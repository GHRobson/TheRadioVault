using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class SavedViewController : SessionTableViewController
{
    private readonly SavedControlsHeaderView _header = new();
    private IReadOnlyList<MobileBroadcastItem> _favourites = [];
    private bool _showFavourites = true;
    private bool _loading;

    public SavedViewController(MobileClientSession session) : base(session) => Title = "Saved";
    protected override string? PageHeading => "Saved";
    protected override string PageDescription => "Favourites and Moments, together.";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        TableView.RowHeight = UITableView.AutomaticDimension;
        TableView.EstimatedRowHeight = 78;
        TableView.TableHeaderView = _header;
        _header.FavouritesButton.TouchUpInside += FavouritesButtonTapped;
        _header.MomentsButton.TouchUpInside += MomentsButtonTapped;
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

    public override nint NumberOfSections(UITableView tableView) => 1;

    public override nint RowsInSection(UITableView tableView, nint section)
        => _showFavourites ? Math.Max(1, _favourites.Count) : Math.Max(1, Session.SavedMoments.Count);

    public override string? TitleForHeader(UITableView tableView, nint section)
        => _showFavourites ? "Favourites" : "Moments";

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (_showFavourites)
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
        if (_showFavourites && indexPath.Row < _favourites.Count)
        {
            NavigationController?.PushViewController(
                new BroadcastDetailsViewController(Session, _favourites[indexPath.Row]), true);
            return;
        }
        if (!_showFavourites && indexPath.Row < Session.SavedMoments.Count)
            _ = Session.PlayMomentAsync(Session.SavedMoments[indexPath.Row]);
    }

    protected override MobileBroadcastItem? ContextBroadcastForRow(NSIndexPath indexPath)
        => _showFavourites && indexPath.Row < _favourites.Count ? _favourites[indexPath.Row] : null;

    private void FavouritesButtonTapped(object? sender, EventArgs eventArgs) => SetMode(showFavourites: true);

    private void MomentsButtonTapped(object? sender, EventArgs eventArgs) => SetMode(showFavourites: false);

    private void SetMode(bool showFavourites)
    {
        if (_showFavourites == showFavourites) return;
        _showFavourites = showFavourites;
        _header.SetMode(showFavourites);
        TableView.ReloadData();
    }

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _header.FavouritesButton.TouchUpInside -= FavouritesButtonTapped;
            _header.MomentsButton.TouchUpInside -= MomentsButtonTapped;
            _header.Dispose();
        }
        base.Dispose(disposing);
    }
}
