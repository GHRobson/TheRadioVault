namespace TheRadioVault.Client.Mobile.Platform;

public interface IMobileDownloadPolicy
{
    bool WifiOnly { get; set; }
    bool IsUsingWifi { get; }
    bool AutoDownloadNewBroadcasts { get; set; }
    DateTimeOffset AutoDownloadSince { get; set; }
    bool DeleteCompletedDownloads { get; set; }
    long StorageLimitBytes { get; set; }
}
