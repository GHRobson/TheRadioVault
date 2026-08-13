namespace TheRadioVault.Client.Mobile.Platform;

public interface IMobileDownloadPolicy
{
    bool WifiOnly { get; set; }
    bool IsUsingWifi { get; }
    bool AutoDownloadNewBroadcasts { get; set; }
    DateTimeOffset AutoDownloadSince { get; set; }
    long AutoDownloadWatermarkEpisodeId { get; set; }
    bool DeleteCompletedDownloads { get; set; }
    int DownloadExpiryDays { get; set; }
    long StorageLimitBytes { get; set; }
}
