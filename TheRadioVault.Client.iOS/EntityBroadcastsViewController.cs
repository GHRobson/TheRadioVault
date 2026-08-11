using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class EntityBroadcastsViewController : SessionTableViewController
{
    private readonly string _entity;
    private IReadOnlyList<MobileBroadcastItem> _broadcasts = [];

    public EntityBroadcastsViewController(MobileClientSession session, string entity) : base(session)
    {
        _entity = entity;
        Title = entity;
    }

    protected override string? PageHeading => _entity;
    protected override string PageDescription => "Across your radio archive.";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        TableView.RowHeight = UITableView.AutomaticDimension;
        TableView.EstimatedRowHeight = 78;
        _ = LoadAsync();
    }

    public override nint RowsInSection(UITableView tableView, nint section) => Math.Max(1, _broadcasts.Count);

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (_broadcasts.Count == 0)
            return DetailCell("entity-empty", "Looking through the archive…", Session.StatusText);
        var cell = new BroadcastProgressCell("entity-broadcast");
        cell.Configure(Session, _broadcasts[indexPath.Row]);
        cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return cell;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Row < _broadcasts.Count)
            NavigationController?.PushViewController(
                new BroadcastDetailsViewController(Session, _broadcasts[indexPath.Row]), true);
    }

    protected override MobileBroadcastItem? ContextBroadcastForRow(NSIndexPath indexPath)
        => indexPath.Row < _broadcasts.Count ? _broadcasts[indexPath.Row] : null;

    private async Task LoadAsync()
    {
        var result = await Session.BrowseCollectionAsync(null, _entity).ConfigureAwait(false);
        BeginInvokeOnMainThread(() =>
        {
            _broadcasts = result;
            TableView.ReloadData();
        });
    }
}
