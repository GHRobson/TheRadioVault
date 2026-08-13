using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Web.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class SavedCollectionViewController : SessionTableViewController
{
    private WebSavedCollectionSummary _summary;
    private WebSavedCollectionDetails? _details;
    private bool _loading;

    public SavedCollectionViewController(MobileClientSession session, WebSavedCollectionSummary summary)
        : base(session)
    {
        _summary = summary;
        Title = summary.Name;
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Always;
        TableView.RowHeight = UITableView.AutomaticDimension;
        TableView.EstimatedRowHeight = 88;
        NavigationItem.RightBarButtonItem = new UIBarButtonItem(
            RadioVaultIcons.Image(RadioVaultIcon.Remove),
            UIBarButtonItemStyle.Plain,
            (_, _) => ConfirmDelete());
        NavigationItem.RightBarButtonItem.AccessibilityLabel = "Delete saved collection";
        RefreshControl = new UIRefreshControl();
        RefreshControl.ValueChanged += (_, _) => _ = LoadAsync();
        _ = LoadAsync();
    }

    public override nint NumberOfSections(UITableView tableView) => 1;
    public override nint RowsInSection(UITableView tableView, nint section)
        => Math.Max(1, _details?.Broadcasts.Count ?? 0);

    public override string? TitleForHeader(UITableView tableView, nint section)
        => _summary.Kind.Equals("Smart", StringComparison.OrdinalIgnoreCase)
            ? "Smart collection · updates automatically"
            : "Playlist";

    public override string? TitleForFooter(UITableView tableView, nint section)
        => _summary.Kind.Equals("Manual", StringComparison.OrdinalIgnoreCase)
            ? "Press and hold a broadcast for playback actions, or swipe left to remove it from this playlist."
            : "Smart collections are rebuilt from their saved Library filters.";

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (_details is null || _details.Broadcasts.Count == 0)
            return DetailCell("empty-saved-collection", _loading ? "Loading…" : "No broadcasts", Session.StatusText);
        var item = new MobileBroadcastItem(_details.Broadcasts[indexPath.Row]);
        var cell = new BroadcastProgressCell("saved-collection-broadcast");
        cell.Configure(Session, item);
        cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return cell;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (_details is not null && indexPath.Row < _details.Broadcasts.Count)
            NavigationController?.PushViewController(
                new BroadcastDetailsViewController(Session, new MobileBroadcastItem(_details.Broadcasts[indexPath.Row])), true);
    }

    protected override MobileBroadcastItem? ContextBroadcastForRow(NSIndexPath indexPath)
        => _details is not null && indexPath.Row < _details.Broadcasts.Count
            ? new MobileBroadcastItem(_details.Broadcasts[indexPath.Row])
            : null;

    public override bool CanEditRow(UITableView tableView, NSIndexPath indexPath)
        => _summary.Kind.Equals("Manual", StringComparison.OrdinalIgnoreCase) &&
           _details is not null && indexPath.Row < _details.Broadcasts.Count;

    public override void CommitEditingStyle(
        UITableView tableView,
        UITableViewCellEditingStyle editingStyle,
        NSIndexPath indexPath)
    {
        if (editingStyle != UITableViewCellEditingStyle.Delete ||
            _details is null || indexPath.Row >= _details.Broadcasts.Count) return;
        var episodeId = _details.Broadcasts[indexPath.Row].RepresentativeEpisodeId;
        _ = RemoveAsync(episodeId);
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        BeginInvokeOnMainThread(() => TableView.ReloadData());
        var details = await Session.LoadSavedCollectionAsync(_summary.Id).ConfigureAwait(false);
        if (details is not null)
        {
            _details = details;
            _summary = details.Summary;
        }
        _loading = false;
        BeginInvokeOnMainThread(() =>
        {
            Title = _summary.Name;
            RefreshControl?.EndRefreshing();
            TableView.ReloadData();
        });
    }

    private async Task RemoveAsync(long episodeId)
    {
        if (_details is null) return;
        var updated = await Session.RemoveFromSavedCollectionAsync(_details, episodeId).ConfigureAwait(false);
        if (updated is null) await LoadAsync().ConfigureAwait(false);
        else
        {
            _details = updated;
            _summary = updated.Summary;
            BeginInvokeOnMainThread(() => TableView.ReloadData());
        }
    }

    private void ConfirmDelete()
    {
        var alert = UIAlertController.Create(
            $"Delete “{_summary.Name}”?",
            "The broadcasts stay in your Radio Vault Library.",
            UIAlertControllerStyle.Alert);
        alert.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        alert.AddAction(UIAlertAction.Create("Delete", UIAlertActionStyle.Destructive, async _ =>
        {
            if (await Session.DeleteSavedCollectionAsync(_summary).ConfigureAwait(false))
                BeginInvokeOnMainThread(() => NavigationController?.PopViewController(true));
            else
                await LoadAsync().ConfigureAwait(false);
        }));
        PresentViewController(alert, true, null);
    }
}
