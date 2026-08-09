using TheRadioVault.Client.Mobile;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class RadioVaultTabBarController : UITabBarController
{
    private readonly MobileClientSession _session;
    private readonly RadioVaultMiniPlayerView _miniPlayer;
    private bool _miniPlayerInstalled;

    public RadioVaultTabBarController(MobileClientSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _miniPlayer = new RadioVaultMiniPlayerView(session);
        ViewControllers =
        [
            Wrap(new HomeViewController(session), "Home", "house", 0),
            Wrap(new LibraryViewController(session), "Library", "books.vertical", 1),
            Wrap(new DownloadsViewController(session), "Downloads", "arrow.down.circle", 2),
            Wrap(new ServerViewController(session), "Server", "externaldrive.connected.to.line.below", 3)
        ];
        _session.TabRequested += SessionOnTabRequested;
        _session.StateChanged += SessionOnStateChanged;
        _session.PlaybackStateChanged += SessionOnStateChanged;
        _miniPlayer.Tapped += MiniPlayerOnTapped;
        InstallMiniPlayer();
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        InstallMiniPlayer();
    }

    private void InstallMiniPlayer()
    {
        if (_miniPlayerInstalled || _miniPlayer is null || View is not { } view) return;
        view.AddSubview(_miniPlayer);
        NSLayoutConstraint.ActivateConstraints([
            _miniPlayer.LeadingAnchor.ConstraintEqualTo(view.SafeAreaLayoutGuide.LeadingAnchor, 8),
            _miniPlayer.TrailingAnchor.ConstraintEqualTo(view.SafeAreaLayoutGuide.TrailingAnchor, -8),
            _miniPlayer.BottomAnchor.ConstraintEqualTo(TabBar.TopAnchor, -4),
            _miniPlayer.HeightAnchor.ConstraintEqualTo(64)
        ]);
        _miniPlayerInstalled = true;
        UpdateMiniPlayerInsets();
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

    private void SessionOnStateChanged(object? sender, EventArgs eventArgs)
        => BeginInvokeOnMainThread(UpdateMiniPlayerInsets);

    private void UpdateMiniPlayerInsets()
    {
        var bottom = _session.HasMiniPlayer ? 68 : 0;
        foreach (var controller in ViewControllers ?? [])
            controller.AdditionalSafeAreaInsets = new UIEdgeInsets(0, 0, bottom, 0);
    }

    private void MiniPlayerOnTapped(object? sender, EventArgs eventArgs)
    {
        if (!_session.HasMiniPlayer || PresentedViewController is not null) return;
        var nowPlaying = new NowPlayingViewController(_session);
        var navigation = new UINavigationController(nowPlaying)
        {
            ModalPresentationStyle = UIModalPresentationStyle.FullScreen
        };
        nowPlaying.NavigationItem.RightBarButtonItem = new UIBarButtonItem(
            UIBarButtonSystemItem.Close,
            (_, _) => navigation.DismissViewController(true, null));
        PresentViewController(navigation, true, null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.TabRequested -= SessionOnTabRequested;
            _session.StateChanged -= SessionOnStateChanged;
            _session.PlaybackStateChanged -= SessionOnStateChanged;
            _miniPlayer.Tapped -= MiniPlayerOnTapped;
        }
        base.Dispose(disposing);
    }
}
