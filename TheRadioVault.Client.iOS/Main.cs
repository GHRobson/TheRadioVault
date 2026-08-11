using TheRadioVault.Client.Mobile.Platform;
using UIKit;

namespace TheRadioVault.Client.iOS;

public static class Application
{
    public static void Main(string[] args)
    {
        IosPlaybackDiagnostics.Reset();
        MobilePlatformServices.Configure(new MobilePlatformServiceSet(
            new IosKeychainConnectionStore(),
            new IosAvPlayerEngine(),
            new IosNowPlayingService(),
            new IosDownloadPolicy()));
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
