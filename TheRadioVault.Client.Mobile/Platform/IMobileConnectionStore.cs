using TheRadioVault.Client.Mobile.Models;

namespace TheRadioVault.Client.Mobile.Platform;

public interface IMobileConnectionStore
{
    RadioVaultMobileConnection? Load();
    void Save(RadioVaultMobileConnection connection);
    void Delete();
}
