using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class LiveRadioViewController : SessionTableViewController
{
    public LiveRadioViewController(MobileClientSession session) : base(session)
    {
        Title = "Radio Vault Live";
    }

    protected override string? PageHeading => "Radio Vault Live";
    protected override string PageDescription => "Your archive, playing like live radio.";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        TableView.RowHeight = UITableView.AutomaticDimension;
        TableView.EstimatedRowHeight = 140;
        RefreshControl = new UIRefreshControl();
        RefreshControl.ValueChanged += async (_, _) =>
        {
            await Session.RefreshLiveRadioAsync();
            BeginInvokeOnMainThread(() => RefreshControl?.EndRefreshing());
        };
        if (Session.LiveRadio is null) _ = Session.RefreshLiveRadioAsync(announce: false);
    }

    public override nint NumberOfSections(UITableView tableView) => 2;

    public override nint RowsInSection(UITableView tableView, nint section) => section switch
    {
        0 => 1,
        1 => Math.Max(1, Session.LiveRadioUpcoming.Count),
        _ => 0
    };

    public override string? TitleForHeader(UITableView tableView, nint section)
        => section == 1 ? "Coming up" : null;

    public override string? TitleForFooter(UITableView tableView, nint section)
        => section == 0
            ? "Live listening never changes played status, progress, Continue Listening or your normal queue. Save a Moment to return to the exact point later."
            : null;

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (indexPath.Section == 0)
        {
            if (Session.LiveRadio?.Current is not { } current)
                return DetailCell("live-radio-empty", "Preparing the station", "Pull down to refresh the Radio Vault Live schedule.");
            var cell = new LiveRadioOnAirCell();
            cell.Configure(
                current,
                Session.IsLiveRadioTunedIn,
                Session.IsLiveRadioLoading,
                () =>
                {
                    if (Session.IsLiveRadioTunedIn) Session.LeaveLiveRadio();
                    else _ = Session.TuneIntoLiveRadioAsync();
                },
                () => Session.AddMomentAsync(string.Empty, "Saved from Radio Vault Live"));
            return cell;
        }

        if (Session.LiveRadioUpcoming.Count == 0)
            return DetailCell("live-radio-upcoming-empty", "The next programmes are being selected", "The station schedule will appear here shortly.");
        var programme = Session.LiveRadioUpcoming[indexPath.Row];
        var broadcast = new MobileBroadcastItem(programme.Broadcast);
        return IconDetailCell(
            "live-radio-upcoming",
            broadcast.Title,
            $"{programme.StartsAt.ToLocalTime():HH:mm} · {broadcast.Subtitle}",
            RadioVaultIcon.Radio,
            disclosure: true);
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Section != 1 || indexPath.Row >= Session.LiveRadioUpcoming.Count) return;
        NavigationController?.PushViewController(
            new BroadcastDetailsViewController(Session, new MobileBroadcastItem(Session.LiveRadioUpcoming[indexPath.Row].Broadcast)),
            true);
    }

    protected override MobileBroadcastItem? ContextBroadcastForRow(NSIndexPath indexPath)
        => indexPath.Section == 1 && indexPath.Row < Session.LiveRadioUpcoming.Count
            ? new MobileBroadcastItem(Session.LiveRadioUpcoming[indexPath.Row].Broadcast)
            : null;
}
