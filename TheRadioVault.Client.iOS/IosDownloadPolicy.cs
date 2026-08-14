using CoreFoundation;
using Foundation;
using Network;
using TheRadioVault.Client.Mobile.Platform;

namespace TheRadioVault.Client.iOS;

public sealed class IosDownloadPolicy : IMobileDownloadPolicy, IDisposable
{
    private const string WifiOnlyKey = "RadioVault.Downloads.WifiOnly";
    private const string AutoDownloadKey = "RadioVault.Downloads.AutoDownloadNew";
    private const string AutoDownloadSinceKey = "RadioVault.Downloads.AutoDownloadSince";
    private const string AutoDownloadWatermarkEpisodeKey = "RadioVault.Downloads.AutoDownloadWatermarkEpisode";
    private const string DeleteCompletedKey = "RadioVault.Downloads.DeleteCompleted";
    private const string DownloadExpiryDaysKey = "RadioVault.Downloads.ExpiryDays";
    private const string StorageLimitKey = "RadioVault.Downloads.StorageLimitBytes";
    private readonly NWPathMonitor _monitor = new();
    private readonly DispatchQueue _queue = new("com.ghrobson.theradiovault.download-network");
    private bool _isUsingWifi = true;

    public IosDownloadPolicy()
    {
        _monitor.SnapshotHandler = path =>
            _isUsingWifi = path.Status == NWPathStatus.Satisfied && path.UsesInterfaceType(NWInterfaceType.Wifi);
        _monitor.SetQueue(_queue);
        _monitor.Start();
    }

    public bool WifiOnly
    {
        get
        {
            return NSUserDefaults.StandardUserDefaults.BoolForKey(WifiOnlyKey);
        }
        set => NSUserDefaults.StandardUserDefaults.SetBool(value, WifiOnlyKey);
    }

    public bool IsUsingWifi => _isUsingWifi;

    public bool AutoDownloadNewBroadcasts
    {
        get => NSUserDefaults.StandardUserDefaults.BoolForKey(AutoDownloadKey);
        set => NSUserDefaults.StandardUserDefaults.SetBool(value, AutoDownloadKey);
    }

    public DateTimeOffset AutoDownloadSince
    {
        get
        {
            var seconds = NSUserDefaults.StandardUserDefaults.DoubleForKey(AutoDownloadSinceKey);
            return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)seconds) : DateTimeOffset.MinValue;
        }
        set => NSUserDefaults.StandardUserDefaults.SetDouble(
            value <= DateTimeOffset.MinValue ? 0 : value.ToUnixTimeSeconds(), AutoDownloadSinceKey);
    }

    public long AutoDownloadWatermarkEpisodeId
    {
        get => Math.Max(0, (long)NSUserDefaults.StandardUserDefaults.DoubleForKey(AutoDownloadWatermarkEpisodeKey));
        set => NSUserDefaults.StandardUserDefaults.SetDouble(Math.Max(0, value), AutoDownloadWatermarkEpisodeKey);
    }

    public bool DeleteCompletedDownloads
    {
        get => NSUserDefaults.StandardUserDefaults.BoolForKey(DeleteCompletedKey);
        set => NSUserDefaults.StandardUserDefaults.SetBool(value, DeleteCompletedKey);
    }

    public int DownloadExpiryDays
    {
        get
        {
            var value = (int)NSUserDefaults.StandardUserDefaults.IntForKey(DownloadExpiryDaysKey);
            return value is 1 or 7 or 30 or 90 ? value : 0;
        }
        set => NSUserDefaults.StandardUserDefaults.SetInt(value is 1 or 7 or 30 or 90 ? value : 0, DownloadExpiryDaysKey);
    }

    public long StorageLimitBytes
    {
        get => Math.Max(0, (long)NSUserDefaults.StandardUserDefaults.DoubleForKey(StorageLimitKey));
        set => NSUserDefaults.StandardUserDefaults.SetDouble(Math.Max(0, value), StorageLimitKey);
    }

    public void Dispose()
    {
        _monitor.Cancel();
        _monitor.Dispose();
        _queue.Dispose();
    }
}
