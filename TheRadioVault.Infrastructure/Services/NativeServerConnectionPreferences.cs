using System.Text.Json;

namespace TheRadioVault.Services;

public sealed class NativeServerConnectionPreferences
{
    public string ClientId { get; set; } = Guid.NewGuid().ToString("D");
    public string ClientDisplayName { get; set; } = $"Radio Vault on {Environment.MachineName}";
    public string ServerInstanceId { get; set; } = string.Empty;
    public string ServerDisplayName { get; set; } = string.Empty;
    public string ServerAddress { get; set; } = string.Empty;
    public int SecurePort { get; set; }
    public string CertificateThumbprint { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public int CapabilityGeneration { get; set; }
    public DateTimeOffset? PairedAt { get; set; }
    public DateTimeOffset? LastConnectedAt { get; set; }
    public bool UseRemoteOnStartup { get; set; }
    public string LibrarySyncSessionId { get; set; } = string.Empty;
    public long LibrarySyncSequence { get; set; }
    public string LibrarySyncRevision { get; set; } = string.Empty;
    public DateTimeOffset? LibraryCacheSynchronizedAt { get; set; }

    public bool HasSavedServer =>
        Guid.TryParse(ServerInstanceId, out _) &&
        !string.IsNullOrWhiteSpace(ServerAddress) &&
        SecurePort is >= 1024 and <= 65535 &&
        CertificateThumbprint.Length >= 32 &&
        AccessToken.Length >= 32;

    public static string FilePath => Path.Combine(AppPaths.DataDirectory, "native-server-connection.json");

    public static NativeServerConnectionPreferences Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                var created = new NativeServerConnectionPreferences();
                created.Save();
                return created;
            }

            var value = JsonSerializer.Deserialize<NativeServerConnectionPreferences>(File.ReadAllText(FilePath))
                        ?? new NativeServerConnectionPreferences();
            if (!Guid.TryParse(value.ClientId, out _)) value.ClientId = Guid.NewGuid().ToString("D");
            value.ClientDisplayName = NormalizeDisplayName(value.ClientDisplayName);
            value.ServerAddress = value.ServerAddress.Trim();
            value.CertificateThumbprint = NormalizeThumbprint(value.CertificateThumbprint);
            value.AccessToken = value.AccessToken.Trim();
            value.UseRemoteOnStartup &= value.HasSavedServer;
            value.Save();
            return value;
        }
        catch
        {
            return new NativeServerConnectionPreferences();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, overwrite: true);
    }

    public void Forget()
    {
        NativeServerResponseCache.DeleteForServer(ServerInstanceId);
        ServerInstanceId = string.Empty;
        ServerDisplayName = string.Empty;
        ServerAddress = string.Empty;
        SecurePort = 0;
        CertificateThumbprint = string.Empty;
        AccessToken = string.Empty;
        CapabilityGeneration = 0;
        PairedAt = null;
        LastConnectedAt = null;
        UseRemoteOnStartup = false;
        LibrarySyncSessionId = string.Empty;
        LibrarySyncSequence = 0;
        LibrarySyncRevision = string.Empty;
        LibraryCacheSynchronizedAt = null;
        Save();
    }

    public static string NormalizeThumbprint(string? value)
        => new((value ?? string.Empty).Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

    private static string NormalizeDisplayName(string? value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? $"Radio Vault on {Environment.MachineName}" : value.Trim();
        name = new string(name.Where(ch => !char.IsControl(ch)).ToArray());
        return name.Length <= 80 ? name : name[..80];
    }
}
