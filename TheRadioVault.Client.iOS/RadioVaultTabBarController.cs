using TheRadioVault.Client.Mobile;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class RadioVaultTabBarController : UITabBarController
{
    private readonly MobileClientSession _session;

    public RadioVaultTabBarController(MobileClientSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ViewControllers =
        [
            Wrap(new HomeViewController(session), "Home", "house", 0),
            Wrap(new LibraryViewController(session), "Library", "books.vertical", 1),
            Wrap(new NowPlayingViewController(session), "Playing", "play.circle", 2),
            Wrap(new ServerViewController(session), "Server", "externaldrive.connected.to.line.below", 3)
        ];
        _session.TabRequested += SessionOnTabRequested;
    }

    private static UINavigationController Wrap(UIViewController controller, string title, string symbol, nint tag)
    {
        var navigation = new UINavigationController(controller)
        {
            TabBarItem = new UITabBarItem(title, UIImage.GetSystemImage(symbol), tag)
        };
        return navigation;
    }

    private void SessionOnTabRequested(int index)
        => BeginInvokeOnMainThread(() => SelectedIndex = index);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _session.TabRequested -= SessionOnTabRequested;
        base.Dispose(disposing);
    }
}
