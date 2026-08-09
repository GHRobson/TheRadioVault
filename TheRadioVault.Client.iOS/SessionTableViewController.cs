using TheRadioVault.Client.Mobile;
using UIKit;

namespace TheRadioVault.Client.iOS;

public abstract class SessionTableViewController : UITableViewController
{
    protected SessionTableViewController(MobileClientSession session)
        : base(UITableViewStyle.InsetGrouped)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Session.StateChanged += SessionOnStateChanged;
    }

    protected MobileClientSession Session { get; }

    protected virtual void ReloadSession() => TableView.ReloadData();

    protected static UITableViewCell DetailCell(string reuseIdentifier, string title, string detail)
    {
        var cell = new UITableViewCell(UITableViewCellStyle.Default, reuseIdentifier);
        var content = cell.DefaultContentConfiguration;
        content.Text = title;
        content.SecondaryText = detail;
        content.SecondaryTextProperties.NumberOfLines = 2;
        cell.ContentConfiguration = content;
        return cell;
    }

    private void SessionOnStateChanged(object? sender, EventArgs eventArgs)
        => BeginInvokeOnMainThread(ReloadSession);

    protected override void Dispose(bool disposing)
    {
        if (disposing) Session.StateChanged -= SessionOnStateChanged;
        base.Dispose(disposing);
    }
}
