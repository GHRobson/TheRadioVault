using System.Text.Json;
using Foundation;
using Security;
using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Client.Mobile.Platform;

namespace TheRadioVault.Client.iOS;

public sealed class IosKeychainConnectionStore : IMobileConnectionStore
{
    private const string Service = "com.ghrobson.theradiovault";
    private const string Account = "server-connection";

    public RadioVaultMobileConnection? Load()
    {
        using var query = CreateQuery();
        using var data = SecKeyChain.QueryAsData(query);
        if (data is null) return null;
        try { return JsonSerializer.Deserialize(data.ToArray(), MobileJsonContext.Default.RadioVaultMobileConnection); }
        catch (JsonException) { return null; }
    }

    public void Save(RadioVaultMobileConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Delete();
        using var data = NSData.FromArray(JsonSerializer.SerializeToUtf8Bytes(
            connection, MobileJsonContext.Default.RadioVaultMobileConnection));
        using var record = new SecRecord(SecKind.GenericPassword)
        {
            Service = Service,
            Account = Account,
            Label = "Radio Vault paired server",
            Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly,
            ValueData = data
        };
        var status = SecKeyChain.Add(record);
        if (status != SecStatusCode.Success)
            throw new InvalidOperationException($"The paired server could not be saved in iOS Keychain ({status}).");
    }

    public void Delete()
    {
        using var query = CreateQuery();
        var status = SecKeyChain.Remove(query);
        if (status is not SecStatusCode.Success and not SecStatusCode.ItemNotFound)
            throw new InvalidOperationException($"The paired server could not be removed from iOS Keychain ({status}).");
    }

    private static SecRecord CreateQuery() => new(SecKind.GenericPassword)
    {
        Service = Service,
        Account = Account
    };
}
