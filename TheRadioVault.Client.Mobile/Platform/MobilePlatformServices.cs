namespace TheRadioVault.Client.Mobile.Platform;

public sealed record MobilePlatformServiceSet(
    IMobileConnectionStore ConnectionStore,
    IMobilePlaybackEngine PlaybackEngine,
    IMobileNowPlayingService NowPlayingService,
    IMobileDownloadPolicy DownloadPolicy);

public static class MobilePlatformServices
{
    private static MobilePlatformServiceSet? _current;

    public static MobilePlatformServiceSet Current => _current
        ?? throw new InvalidOperationException("The mobile platform services were not configured before the app started.");

    public static void Configure(MobilePlatformServiceSet services)
        => _current = services ?? throw new ArgumentNullException(nameof(services));
}
