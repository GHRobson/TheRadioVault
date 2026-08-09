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
            Wrap(new HomeViewController(session), "Dashboard", RadioVaultIcon.Home, 0),
            Wrap(new LibraryViewController(session), "Library", RadioVaultIcon.Library, 1),
            Wrap(new ExploreViewController(session), "Explore", RadioVaultIcon.Knowledge, 2),
            Wrap(new DownloadsViewController(session), "Downloads", RadioVaultIcon.Download, 3),
            Wrap(new ServerViewController(session), "Settings", RadioVaultIcon.Settings, 4)
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

    private static UINavigationController Wrap(UIViewController controller, string title, RadioVaultIcon icon, nint tag)
    {
        controller.Title = title;
        var tabColor = RadioVaultIcons.ColorFor(icon);
        var tabItem = new UITabBarItem(title, RadioVaultIcons.Image(icon), tag);
        tabItem.SetTitleTextAttributes(
            new UIStringAttributes { ForegroundColor = tabColor },
            UIControlState.Normal);
        tabItem.SetTitleTextAttributes(
            new UIStringAttributes { ForegroundColor = tabColor },
            UIControlState.Selected);
        var tabAppearance = CreateTabAppearance(tabColor);
        tabItem.StandardAppearance = tabAppearance;
        tabItem.ScrollEdgeAppearance = tabAppearance;
        var navigation = new UINavigationController(controller)
        {
            TabBarItem = tabItem
        };
        navigation.NavigationBar.PrefersLargeTitles = true;
        navigation.NavigationBar.Hidden = false;
        if (navigation.View is { } view) view.BackgroundColor = RadioVaultTheme.Background;
        return navigation;
    }

    private static UITabBarAppearance CreateTabAppearance(UIColor color)
    {
        var appearance = new UITabBarAppearance();
        appearance.ConfigureWithOpaqueBackground();
        appearance.BackgroundColor = RadioVaultTheme.Shell;
        appearance.ShadowColor = RadioVaultTheme.Border;
        ConfigureItemAppearance(appearance.StackedLayoutAppearance, color);
        ConfigureItemAppearance(appearance.InlineLayoutAppearance, color);
        ConfigureItemAppearance(appearance.CompactInlineLayoutAppearance, color);
        return appearance;
    }

    private static void ConfigureItemAppearance(UITabBarItemAppearance appearance, UIColor color)
    {
        var attributes = new UIStringAttributes { ForegroundColor = color };
        appearance.Normal.IconColor = color;
        appearance.Normal.TitleTextAttributes = attributes;
        appearance.Selected.IconColor = color;
        appearance.Selected.TitleTextAttributes = attributes;
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
            ModalPresentationStyle = UIModalPresentationStyle.PageSheet
        };
        if (navigation.SheetPresentationController is { } sheet)
        {
            sheet.Detents = [UISheetPresentationControllerDetent.CreateLargeDetent()];
            sheet.PrefersGrabberVisible = true;
        }
        nowPlaying.NavigationItem.RightBarButtonItem = new UIBarButtonItem(
            RadioVaultIcons.Image(RadioVaultIcon.Close, RadioVaultTheme.MutedText),
            UIBarButtonItemStyle.Plain,
            (_, _) => navigation.DismissViewController(true, null));
        nowPlaying.NavigationItem.RightBarButtonItem.AccessibilityLabel = "Close Now Playing";
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
