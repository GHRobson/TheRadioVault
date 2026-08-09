using Foundation;
using UIKit;

namespace TheRadioVault.Client.iOS;

[Register("AppDelegate")]
#pragma warning disable CA1711
public sealed class AppDelegate : UIResponder, IUIApplicationDelegate
#pragma warning restore CA1711
{
    [Export("application:didFinishLaunchingWithOptions:")]
    public bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        RadioVaultTheme.ApplyGlobalAppearance();
        return true;
    }

    [Export("application:configurationForConnectingSceneSession:options:")]
    public UISceneConfiguration GetConfiguration(
        UIApplication application,
        UISceneSession connectingSceneSession,
        UISceneConnectionOptions options)
        => UISceneConfiguration.Create("Default Configuration", connectingSceneSession.Role);
}
