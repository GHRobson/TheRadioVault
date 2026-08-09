namespace TheRadioVault.Client.Mobile.Platform;

public interface IMobileDownloadPolicy
{
    bool WifiOnly { get; set; }
    bool IsUsingWifi { get; }
}
