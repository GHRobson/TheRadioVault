using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class ServerViewController : SessionTableViewController
{
    private readonly UITextField _codeField = new()
    {
        Placeholder = "Six-digit pairing code",
        KeyboardType = UIKeyboardType.NumberPad,
        TextAlignment = UITextAlignment.Center,
        Font = UIFont.MonospacedDigitSystemFontOfSize(22, UIFontWeight.Semibold),
        TranslatesAutoresizingMaskIntoConstraints = false
    };
    private UITableViewCell? _codeCell;
    private DiscoveredRadioVaultServer? _selectedServer;

    public ServerViewController(MobileClientSession session) : base(session) => Title = "Server";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Always;
    }

    protected override void ReloadSession()
    {
        if (_selectedServer is null || !Session.Servers.Contains(_selectedServer))
            _selectedServer = Session.Servers.FirstOrDefault();
        if (Session.IsPaired) _codeField.Text = string.Empty;
        base.ReloadSession();
    }

    public override nint NumberOfSections(UITableView tableView) => 4;

    public override nint RowsInSection(UITableView tableView, nint section) => section switch
    {
        0 => 1,
        1 => 1,
        2 => Math.Max(1, Session.Servers.Count),
        3 => Session.IsPaired ? 1 : 2,
        _ => 0
    };

    public override string? TitleForHeader(UITableView tableView, nint section) => section switch
    {
        0 => "Paired server",
        1 => "Discovery",
        2 => "Servers on this network",
        3 => Session.IsPaired ? "Connection" : "Pair this iPhone",
        _ => null
    };

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (indexPath.Section == 0)
            return DetailCell(
                "server-status",
                Session.ServerName,
                Session.IsPaired ? $"{Session.ServerAddress}\n{Session.StatusText}" : Session.StatusText);

        if (indexPath.Section == 1)
        {
            var cell = new UITableViewCell(UITableViewCellStyle.Default, "discover");
            var content = cell.DefaultContentConfiguration;
            content.Text = Session.IsBusy ? "Searching…" : "Find Radio Vault Servers";
            content.TextProperties.Color = UIColor.SystemBlue;
            content.TextProperties.Alignment = UIListContentTextAlignment.Center;
            cell.ContentConfiguration = content;
            cell.SelectionStyle = Session.IsBusy ? UITableViewCellSelectionStyle.None : UITableViewCellSelectionStyle.Default;
            return cell;
        }

        if (indexPath.Section == 2)
        {
            if (Session.Servers.Count == 0)
                return DetailCell("no-servers", "No servers found yet", "Tap Find Radio Vault Servers above.");
            var server = Session.Servers[indexPath.Row];
            var cell = DetailCell("found-server", server.DisplayName, $"{server.Detail} · {server.PairingText}");
            cell.Accessory = Equals(server, _selectedServer) ? UITableViewCellAccessory.Checkmark : UITableViewCellAccessory.None;
            return cell;
        }

        if (Session.IsPaired)
        {
            var cell = new UITableViewCell(UITableViewCellStyle.Default, "forget");
            var content = cell.DefaultContentConfiguration;
            content.Text = "Forget paired server";
            content.TextProperties.Color = UIColor.SystemRed;
            content.TextProperties.Alignment = UIListContentTextAlignment.Center;
            cell.ContentConfiguration = content;
            return cell;
        }

        if (indexPath.Row == 0)
        {
            if (_codeCell is not null) return _codeCell;
            _codeCell = new UITableViewCell(UITableViewCellStyle.Default, "code");
            _codeCell.ContentView.AddSubview(_codeField);
            NSLayoutConstraint.ActivateConstraints([
                _codeField.LeadingAnchor.ConstraintEqualTo(_codeCell.ContentView.LayoutMarginsGuide.LeadingAnchor),
                _codeField.TrailingAnchor.ConstraintEqualTo(_codeCell.ContentView.LayoutMarginsGuide.TrailingAnchor),
                _codeField.TopAnchor.ConstraintEqualTo(_codeCell.ContentView.TopAnchor, 12),
                _codeField.BottomAnchor.ConstraintEqualTo(_codeCell.ContentView.BottomAnchor, -12)
            ]);
            _codeCell.SelectionStyle = UITableViewCellSelectionStyle.None;
            return _codeCell;
        }

        var pairCell = new UITableViewCell(UITableViewCellStyle.Default, "pair");
        var pairContent = pairCell.DefaultContentConfiguration;
        pairContent.Text = Session.IsBusy ? "Pairing…" : "Pair selected server";
        pairContent.TextProperties.Color = UIColor.SystemBlue;
        pairContent.TextProperties.Alignment = UIListContentTextAlignment.Center;
        pairCell.ContentConfiguration = pairContent;
        return pairCell;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (Session.IsBusy) return;
        if (indexPath.Section == 1)
        {
            _codeField.ResignFirstResponder();
            _ = Session.DiscoverAsync();
            return;
        }
        if (indexPath.Section == 2 && indexPath.Row < Session.Servers.Count)
        {
            _selectedServer = Session.Servers[indexPath.Row];
            TableView.ReloadSections(NSIndexSet.FromIndex(2), UITableViewRowAnimation.Automatic);
            return;
        }
        if (indexPath.Section != 3) return;
        if (Session.IsPaired)
        {
            ConfirmForget();
            return;
        }
        if (indexPath.Row == 1 && _selectedServer is { } server)
        {
            _codeField.ResignFirstResponder();
            _ = Session.PairAsync(server, _codeField.Text ?? string.Empty);
        }
    }

    private void ConfirmForget()
    {
        var alert = UIAlertController.Create(
            "Forget this server?",
            "You will need a new pairing code to connect this iPhone again.",
            UIAlertControllerStyle.Alert);
        alert.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        alert.AddAction(UIAlertAction.Create("Forget", UIAlertActionStyle.Destructive, _ => Session.Forget()));
        PresentViewController(alert, true, null);
    }
}
