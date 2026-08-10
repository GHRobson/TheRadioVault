using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class ServerViewController : SessionTableViewController
{
    protected override string? PageHeading => "Settings";
    protected override string PageDescription => "Connection, downloads and preferences.";
    private readonly UISwitch _wifiOnlySwitch = new();
    private readonly UISwitch _autoDownloadSwitch = new();
    private readonly UISwitch _deleteCompletedSwitch = new();
    private readonly UITextField _codeField = new()
    {
        Placeholder = "Six-digit pairing code",
        KeyboardType = UIKeyboardType.NumberPad,
        TextAlignment = UITextAlignment.Center,
        Font = UIFont.MonospacedDigitSystemFontOfSize(22, UIFontWeight.Semibold),
        TranslatesAutoresizingMaskIntoConstraints = false
    };
    private readonly UITextField _addressField = new()
    {
        Placeholder = "Server address, e.g. 192.168.1.20",
        KeyboardType = UIKeyboardType.NumbersAndPunctuation,
        AutocapitalizationType = UITextAutocapitalizationType.None,
        AutocorrectionType = UITextAutocorrectionType.No,
        ClearButtonMode = UITextFieldViewMode.WhileEditing,
        TranslatesAutoresizingMaskIntoConstraints = false
    };
    private readonly UITextField _portField = new()
    {
        Placeholder = "HTTPS port",
        Text = "8766",
        KeyboardType = UIKeyboardType.NumberPad,
        TextAlignment = UITextAlignment.Right,
        TranslatesAutoresizingMaskIntoConstraints = false
    };
    private UITableViewCell? _codeCell;
    private UITableViewCell? _addressCell;
    private UITableViewCell? _portCell;
    private DiscoveredRadioVaultServer? _selectedServer;

    public ServerViewController(MobileClientSession session) : base(session) => Title = "Settings";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        _codeField.TextColor = RadioVaultTheme.Text;
        _codeField.TintColor = RadioVaultTheme.Accent;
        _addressField.TextColor = RadioVaultTheme.Text;
        _addressField.TintColor = RadioVaultTheme.Accent;
        _portField.TextColor = RadioVaultTheme.Text;
        _portField.TintColor = RadioVaultTheme.Accent;
        _wifiOnlySwitch.On = Session.WifiOnlyDownloads;
        _wifiOnlySwitch.ValueChanged += DownloadPolicyChanged;
        _autoDownloadSwitch.On = Session.AutoDownloadNewBroadcasts;
        _autoDownloadSwitch.ValueChanged += AutoDownloadPolicyChanged;
        _deleteCompletedSwitch.On = Session.DeleteCompletedDownloads;
        _deleteCompletedSwitch.ValueChanged += DeleteCompletedPolicyChanged;
    }

    protected override void ReloadSession()
    {
        if (_selectedServer is null || !Session.Servers.Contains(_selectedServer))
            _selectedServer = Session.Servers.FirstOrDefault();
        if (Session.IsPaired) _codeField.Text = string.Empty;
        _wifiOnlySwitch.On = Session.WifiOnlyDownloads;
        _autoDownloadSwitch.On = Session.AutoDownloadNewBroadcasts;
        _deleteCompletedSwitch.On = Session.DeleteCompletedDownloads;
        base.ReloadSession();
    }

    public override nint NumberOfSections(UITableView tableView) => 5;

    public override nint RowsInSection(UITableView tableView, nint section) => section switch
    {
        0 => Session.IsPaired ? 2 : 1,
        1 => 7,
        2 => Session.IsPaired ? 1 : 3,
        3 => Math.Max(1, Session.Servers.Count),
        4 => Session.IsPaired ? 1 : 2,
        _ => 0
    };

    public override string? TitleForHeader(UITableView tableView, nint section) => section switch
    {
        0 => "Paired server",
        1 => "Download Settings",
        2 => Session.IsPaired ? "Discovery" : "Find or enter your server",
        3 => "Servers on this network",
        4 => Session.IsPaired ? "Connection" : "Pair this iPhone",
        _ => null
    };

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (indexPath.Section == 0)
        {
            if (indexPath.Row == 1)
                return DetailCell(
                    "sync-diagnostics",
                    "Sync Status",
                    Session.PendingSyncChanges == 0
                        ? "Up to date · view cache and connection details"
                        : $"{Session.PendingSyncChanges:N0} change{(Session.PendingSyncChanges == 1 ? string.Empty : "s")} waiting to sync");
            return DetailCell(
                "server-status",
                Session.ServerName,
                Session.IsPaired ? $"{Session.ServerAddress}\n{Session.StatusText}" : Session.StatusText);
        }

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
            if (indexPath.Row == 1)
                return SwitchCell(
                    "settings-auto-download", "Automatically Download New Broadcasts",
                    "Downloads broadcasts added after you enable this setting", _autoDownloadSwitch);
            if (indexPath.Row == 2)
                return SwitchCell(
                    "settings-delete-completed", "Remove Completed Downloads",
                    "Keeps listening history while freeing local storage", _deleteCompletedSwitch);
            if (indexPath.Row == 3)
                return DetailCell(
                    "settings-download-storage", "Radio Vault Storage",
                    Session.PendingDownloadBytes > 0
                        ? $"{Session.DownloadStorageText} · {FormatBytes(Session.PendingDownloadBytes)} resumable"
                        : Session.DownloadStorageText);
            if (indexPath.Row == 4)
            {
                var limit = DetailCell(
                    "settings-storage-limit", "Storage Limit", Session.DownloadStorageLimitText);
                limit.Accessory = UITableViewCellAccessory.DisclosureIndicator;
                return limit;
            }
            if (indexPath.Row == 5)
                return ActionCell("settings-check-downloads", "Check Downloaded Files", RadioVaultTheme.Accent);
            return ActionCell("settings-clean-downloads", "Remove Completed Downloads Now", RadioVaultTheme.Danger);
        }

        if (indexPath.Section == 2)
        {
            if (indexPath.Row == 1) return TextFieldCell(ref _addressCell, "manual-address", _addressField);
            if (indexPath.Row == 2) return TextFieldCell(ref _portCell, "manual-port", _portField, "HTTPS port");
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
        pairContent.Text = Session.IsBusy ? "Pairing…" : _selectedServer is null
            ? "Pair using entered address"
            : "Pair selected server";
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
        if (indexPath.Section == 0 && indexPath.Row == 1)
        {
            NavigationController?.PushViewController(new SyncDiagnosticsViewController(Session), true);
            return;
        }
        if (indexPath.Section == 1)
        {
            if (indexPath.Row == 4) PresentStorageLimitPicker();
            else if (indexPath.Row == 5) _ = Session.RepairDownloadsAsync();
            else if (indexPath.Row == 6) _ = Session.CleanupCompletedDownloadsAsync();
            return;
        }
        if (indexPath.Section == 2)
        {
            if (indexPath.Row == 0)
            {
                EndEditing();
                _ = Session.DiscoverAsync();
            }
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
        if (indexPath.Row == 1)
        {
            EndEditing();
            if (_selectedServer is { } server)
            {
                _ = Session.PairAsync(server, _codeField.Text ?? string.Empty);
                return;
            }
            if (!int.TryParse(_portField.Text, out var port)) port = 8766;
            _ = Session.PairManuallyAsync(_addressField.Text ?? string.Empty, port, _codeField.Text ?? string.Empty);
        }
    }

    private void EndEditing()
    {
        _codeField.ResignFirstResponder();
        _addressField.ResignFirstResponder();
        _portField.ResignFirstResponder();
    }

    private static UITableViewCell TextFieldCell(
        ref UITableViewCell? cached,
        string identifier,
        UITextField field,
        string? label = null)
    {
        if (cached is not null) return cached;
        cached = new UITableViewCell(UITableViewCellStyle.Default, identifier)
        {
            BackgroundColor = RadioVaultTheme.Surface,
            SelectionStyle = UITableViewCellSelectionStyle.None
        };
        if (label is not null)
        {
            var content = cached.DefaultContentConfiguration;
            content.Text = label;
            RadioVaultTheme.StyleCell(cached, content);
        }
        cached.ContentView.AddSubview(field);
        NSLayoutConstraint.ActivateConstraints([
            field.LeadingAnchor.ConstraintEqualTo(cached.ContentView.LayoutMarginsGuide.LeadingAnchor, label is null ? 0 : 120),
            field.TrailingAnchor.ConstraintEqualTo(cached.ContentView.LayoutMarginsGuide.TrailingAnchor),
            field.TopAnchor.ConstraintEqualTo(cached.ContentView.TopAnchor, 12),
            field.BottomAnchor.ConstraintEqualTo(cached.ContentView.BottomAnchor, -12)
        ]);
        return cached;
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

    private void AutoDownloadPolicyChanged(object? sender, EventArgs eventArgs)
        => Session.AutoDownloadNewBroadcasts = _autoDownloadSwitch.On;

    private void DeleteCompletedPolicyChanged(object? sender, EventArgs eventArgs)
        => Session.DeleteCompletedDownloads = _deleteCompletedSwitch.On;

    private static UITableViewCell SwitchCell(
        string identifier, string title, string detail, UISwitch control)
    {
        var cell = DetailCell(identifier, title, detail);
        cell.AccessoryView = control;
        cell.SelectionStyle = UITableViewCellSelectionStyle.None;
        return cell;
    }

    private static UITableViewCell ActionCell(string identifier, string title, UIColor color)
    {
        var cell = new UITableViewCell(UITableViewCellStyle.Default, identifier);
        var content = cell.DefaultContentConfiguration;
        content.Text = title;
        content.TextProperties.Color = color;
        content.TextProperties.Alignment = UIListContentTextAlignment.Center;
        RadioVaultTheme.StyleCell(cell, content);
        return cell;
    }

    private void PresentStorageLimitPicker()
    {
        var sheet = UIAlertController.Create(
            "Download Storage Limit",
            "When the limit is reached, Radio Vault removes completed and then oldest downloads first.",
            UIAlertControllerStyle.ActionSheet);
        foreach (var option in new (string Title, long Bytes)[]
                 {
                     ("2 GB", 2L * 1024 * 1024 * 1024),
                     ("5 GB", 5L * 1024 * 1024 * 1024),
                     ("10 GB", 10L * 1024 * 1024 * 1024),
                     ("20 GB", 20L * 1024 * 1024 * 1024),
                     ("No Limit", 0)
                 })
            sheet.AddAction(UIAlertAction.Create(option.Title, UIAlertActionStyle.Default, _ =>
                Session.DownloadStorageLimitBytes = option.Bytes));
        sheet.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        if (View is { } sourceView && sheet.PopoverPresentationController is { } popover)
        {
            popover.SourceView = sourceView;
            popover.SourceRect = sourceView.Bounds;
        }
        PresentViewController(sheet, true, null);
    }

    private static string FormatBytes(long value)
        => value >= 1024L * 1024L * 1024L ? $"{value / (1024d * 1024d * 1024d):0.0} GB"
            : value >= 1024L * 1024L ? $"{value / (1024d * 1024d):0.0} MB"
            : $"{Math.Max(0, value) / 1024d:0} KB";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _wifiOnlySwitch.ValueChanged -= DownloadPolicyChanged;
            _autoDownloadSwitch.ValueChanged -= AutoDownloadPolicyChanged;
            _deleteCompletedSwitch.ValueChanged -= DeleteCompletedPolicyChanged;
        }
        base.Dispose(disposing);
    }
}
