using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Platform;
using UIKit;

namespace TheRadioVault.Client.iOS;

[Register("SceneDelegate")]
#pragma warning disable CA1711
public sealed class SceneDelegate : UIResponder, IUIWindowSceneDelegate
#pragma warning restore CA1711
{
    private MobileClientSession? _session;

    [Export("window")]
    public UIWindow? Window { get; set; }

    [Export("scene:willConnectToSession:options:")]
    public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene) return;

        var services = MobilePlatformServices.Current;
        _session = new MobileClientSession(
            new MobileServerClient(services.ConnectionStore),
            services.PlaybackEngine,
            services.NowPlayingService);
        var tabs = new RadioVaultTabBarController(_session);
        Window = new UIWindow(windowScene) { RootViewController = tabs };
        Window.MakeKeyAndVisible();
        _ = _session.InitializeAsync();
    }

    [Export("sceneDidEnterBackground:")]
    public void DidEnterBackground(UIScene scene)
    {
        if (_session is not null) _ = _session.FlushPlaybackAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session?.Dispose();
            _session = null;
        }
        base.Dispose(disposing);
    }
}
