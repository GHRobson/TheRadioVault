using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class ServerViewController : SessionTableViewController
{
    private readonly UISwitch _wifiOnlySwitch = new();
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

    public ServerViewController(MobileClientSession session) : base(session) => Title = "Settings";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Always;
        _codeField.TextColor = RadioVaultTheme.Text;
        _codeField.TintColor = RadioVaultTheme.Accent;
        _wifiOnlySwitch.On = Session.WifiOnlyDownloads;
        _wifiOnlySwitch.ValueChanged += DownloadPolicyChanged;
    }

    protected override void ReloadSession()
    {
        if (_selectedServer is null || !Session.Servers.Contains(_selectedServer))
            _selectedServer = Session.Servers.FirstOrDefault();
        if (Session.IsPaired) _codeField.Text = string.Empty;
        _wifiOnlySwitch.On = Session.WifiOnlyDownloads;
        base.ReloadSession();
    }

    public override nint NumberOfSections(UITableView tableView) => 5;

    public override nint RowsInSection(UITableView tableView, nint section) => section switch
    {
        0 => 1,
        1 => 2,
        2 => 1,
        3 => Math.Max(1, Session.Servers.Count),
        4 => Session.IsPaired ? 1 : 2,
        _ => 0
    };

    public override string? TitleForHeader(UITableView tableView, nint section) => section switch
    {
        0 => "Paired server",
        1 => "Download Settings",
        2 => "Discovery",
        3 => "Servers on this network",
        4 => Session.IsPaired ? "Connection" : "Pair this iPhone",
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
            if (indexPath.Row == 0)
            {
                var wifi = new UITableViewCell(UITableViewCellStyle.Default, "settings-wifi-only");
                var wifiContent = wifi.DefaultContentConfiguration;
                wifiContent.Text = "Wi-Fi Only";
                wifiContent.SecondaryText = "Prevent downloads on cellular data";
                RadioVaultTheme.StyleCell(wifi, wifiContent);
                wifi.AccessoryView = _wifiOnlySwitch;
                wifi.SelectionStyle = UITableViewCellSelectionStyle.None;
                return wifi;
            }
            return DetailCell(
                "settings-download-storage",
                "Radio Vault Storage",
                Session.PendingDownloadBytes > 0
                    ? $"{Session.DownloadStorageText} · {FormatBytes(Session.PendingDownloadBytes)} resumable"
                    : Session.DownloadStorageText);
        }

        if (indexPath.Section == 2)
        {
            var cell = new UITableViewCell(UITableViewCellStyle.Default, "discover");
            var content = cell.DefaultContentConfiguration;
            content.Text = Session.IsBusy ? "Searching…" : "Find Radio Vault Servers";
            content.TextProperties.Color = RadioVaultTheme.Accent;
            content.TextProperties.Alignment = UIListContentTextAlignment.Center;
            cell.ContentConfiguration = content;
            cell.BackgroundColor = RadioVaultTheme.Surface;
            cell.SelectionStyle = Session.IsBusy ? UITableViewCellSelectionStyle.None : UITableViewCellSelectionStyle.Default;
            return cell;
        }

        if (indexPath.Section == 3)
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
            content.TextProperties.Color = RadioVaultTheme.Danger;
            content.TextProperties.Alignment = UIListContentTextAlignment.Center;
            cell.ContentConfiguration = content;
            cell.BackgroundColor = RadioVaultTheme.Surface;
            return cell;
        }

        if (indexPath.Row == 0)
        {
            if (_codeCell is not null) return _codeCell;
            _codeCell = new UITableViewCell(UITableViewCellStyle.Default, "code");
            _codeCell.ContentView.AddSubview(_codeField);
            _codeCell.BackgroundColor = RadioVaultTheme.Surface;
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
        pairContent.TextProperties.Color = RadioVaultTheme.Accent;
        pairContent.TextProperties.Alignment = UIListContentTextAlignment.Center;
        pairCell.ContentConfiguration = pairContent;
        pairCell.BackgroundColor = RadioVaultTheme.Surface;
        return pairCell;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (Session.IsBusy) return;
        if (indexPath.Section == 2)
        {
            _codeField.ResignFirstResponder();
            _ = Session.DiscoverAsync();
            return;
        }
        if (indexPath.Section == 3 && indexPath.Row < Session.Servers.Count)
        {
            _selectedServer = Session.Servers[indexPath.Row];
            TableView.ReloadSections(NSIndexSet.FromIndex(3), UITableViewRowAnimation.Automatic);
            return;
        }
        if (indexPath.Section != 4) return;
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

    private void DownloadPolicyChanged(object? sender, EventArgs eventArgs)
        => Session.WifiOnlyDownloads = _wifiOnlySwitch.On;

    private static string FormatBytes(long value)
        => value >= 1024L * 1024L * 1024L ? $"{value / (1024d * 1024d * 1024d):0.0} GB"
            : value >= 1024L * 1024L ? $"{value / (1024d * 1024d):0.0} MB"
            : $"{Math.Max(0, value) / 1024d:0} KB";

    protected override void Dispose(bool disposing)
    {
        if (disposing) _wifiOnlySwitch.ValueChanged -= DownloadPolicyChanged;
        base.Dispose(disposing);
    }
}
