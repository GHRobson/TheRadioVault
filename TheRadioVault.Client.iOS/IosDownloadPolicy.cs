using CoreFoundation;
using Foundation;
using Network;
using TheRadioVault.Client.Mobile.Platform;

namespace TheRadioVault.Client.iOS;

public sealed class IosDownloadPolicy : IMobileDownloadPolicy, IDisposable
{
    private const string WifiOnlyKey = "RadioVault.Downloads.WifiOnly";
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

    public void Dispose()
    {
        _monitor.Cancel();
        _monitor.Dispose();
        _queue.Dispose();
    }
}
