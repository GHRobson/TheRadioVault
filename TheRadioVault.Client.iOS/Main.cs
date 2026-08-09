using TheRadioVault.Client.Mobile.Platform;
using UIKit;

namespace TheRadioVault.Client.iOS;

public static class Application
{
    public static void Main(string[] args)
    {
        MobilePlatformServices.Configure(new MobilePlatformServiceSet(
            new IosKeychainConnectionStore(),
            new IosAvPlayerEngine(),
            new IosNowPlayingService()));
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
