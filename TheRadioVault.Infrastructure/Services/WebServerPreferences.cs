using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Services;

public sealed class PairedDesktopClientPreference
{
    public string ClientId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public DateTimeOffset PairedAt { get; set; } = DateTimeOffset.UtcNow;

    public WebPairedDesktopClient ToContract()
        => new(ClientId, DisplayName, Token, PairedAt);

    public static PairedDesktopClientPreference FromContract(WebPairedDesktopClient client)
        => new()
        {
            ClientId = client.ClientId,
            DisplayName = client.DisplayName,
            Token = client.Token,
            PairedAt = client.PairedAt
        };
}

public sealed class WebServerPreferences
{
    private const int CurrentWebShellGeneration = 14;
    public bool Enabled { get; set; }
    public bool StartAutomatically { get; set; }
    public int Port { get; set; } = 8765;
    public bool SecureAccessEnabled { get; set; }
    public int SecurePort { get; set; } = 8766;
    public string AccessToken { get; set; } = CreateToken();
    public string CertificatePassword { get; set; } = CreateToken();
    public string ServerInstanceId { get; set; } = Guid.NewGuid().ToString("D");
    public string ServerDisplayName { get; set; } = $"Radio Vault on {Environment.MachineName}";
    public int WebShellGeneration { get; set; } = CurrentWebShellGeneration;
    public bool LanFederationEnabled { get; set; }
    public int LanDiscoveryPort { get; set; } = 30829;
    public List<PairedDesktopClientPreference> PairedDesktopClients { get; set; } = new();

    private static string FilePath => Path.Combine(AppPaths.DataDirectory, "web-server.json");

    public static WebServerPreferences Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                var created = new WebServerPreferences();
                created.Save();
                return created;
            }

            var json = File.ReadAllText(FilePath);
            var value = JsonSerializer.Deserialize<WebServerPreferences>(json) ?? new WebServerPreferences();
            var changed = false;
            using var document = JsonDocument.Parse(json);
            var hasShellGeneration = document.RootElement.TryGetProperty(nameof(WebShellGeneration), out _);
            var hasServerInstanceId = document.RootElement.TryGetProperty(nameof(ServerInstanceId), out _);
            var hasServerDisplayName = document.RootElement.TryGetProperty(nameof(ServerDisplayName), out _);
            var hasDiscoveryPort = document.RootElement.TryGetProperty(nameof(LanDiscoveryPort), out _);
            var hasPairedClients = document.RootElement.TryGetProperty(nameof(PairedDesktopClients), out _);

            if (!hasShellGeneration || value.WebShellGeneration < CurrentWebShellGeneration)
            {
                // Keep the accepted secure origin stable while advancing the
                // installed shell after a Radio Vault Web client repair.
                value.WebShellGeneration = CurrentWebShellGeneration;
                changed = true;
            }

            var clampedPort = Math.Clamp(value.Port, 1024, 65535);
            var clampedSecurePort = Math.Clamp(value.SecurePort, 1024, 65535);
            var clampedDiscoveryPort = Math.Clamp(value.LanDiscoveryPort, 1024, 65535);
            if (clampedPort != value.Port) { value.Port = clampedPort; changed = true; }
            if (clampedSecurePort != value.SecurePort) { value.SecurePort = clampedSecurePort; changed = true; }
            if (!hasDiscoveryPort || clampedDiscoveryPort != value.LanDiscoveryPort) { value.LanDiscoveryPort = clampedDiscoveryPort; changed = true; }
            if (value.SecurePort == value.Port)
            {
                value.SecurePort = value.Port == 65535 ? 65534 : value.Port + 1;
                changed = true;
            }
            if (value.LanDiscoveryPort == value.Port || value.LanDiscoveryPort == value.SecurePort)
            {
                value.LanDiscoveryPort = 30829;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(value.AccessToken) || value.AccessToken.Length < 24)
            {
                value.AccessToken = CreateToken();
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(value.CertificatePassword) || value.CertificatePassword.Length < 24)
            {
                value.CertificatePassword = CreateToken();
                changed = true;
            }
            if (!hasServerInstanceId || !Guid.TryParse(value.ServerInstanceId, out _))
            {
                value.ServerInstanceId = Guid.NewGuid().ToString("D");
                changed = true;
            }
            if (!hasServerDisplayName || string.IsNullOrWhiteSpace(value.ServerDisplayName))
            {
                value.ServerDisplayName = $"Radio Vault on {Environment.MachineName}";
                changed = true;
            }

            var trimmedDisplayName = value.ServerDisplayName.Trim();
            if (trimmedDisplayName.Length > 80) trimmedDisplayName = trimmedDisplayName[..80];
            if (!string.Equals(trimmedDisplayName, value.ServerDisplayName, StringComparison.Ordinal))
            {
                value.ServerDisplayName = trimmedDisplayName;
                changed = true;
            }

            if (!hasPairedClients || value.PairedDesktopClients is null)
            {
                value.PairedDesktopClients = new();
                changed = true;
            }
            else
            {
                var cleaned = value.PairedDesktopClients
                    .Where(x => x is not null)
                    .Where(x => !string.IsNullOrWhiteSpace(x.ClientId) &&
                                !string.IsNullOrWhiteSpace(x.DisplayName) &&
                                !string.IsNullOrWhiteSpace(x.Token) &&
                                x.Token.Length >= 32)
                    .GroupBy(x => x.ClientId.Trim(), StringComparer.Ordinal)
                    .Select(group => group.OrderByDescending(x => x.PairedAt).First())
                    .Select(x => new PairedDesktopClientPreference
                    {
                        ClientId = x.ClientId.Trim(),
                        DisplayName = x.DisplayName.Trim(),
                        Token = x.Token.Trim(),
                        PairedAt = x.PairedAt
                    })
                    .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (cleaned.Count != value.PairedDesktopClients.Count ||
                    !cleaned.Select(x => $"{x.ClientId}|{x.DisplayName}|{x.Token}|{x.PairedAt:O}")
                        .SequenceEqual(value.PairedDesktopClients.Select(x => $"{x.ClientId}|{x.DisplayName}|{x.Token}|{x.PairedAt:O}"), StringComparer.Ordinal))
                {
                    value.PairedDesktopClients = cleaned;
                    changed = true;
                }
            }

            if (value.LanFederationEnabled && !value.SecureAccessEnabled)
            {
                value.LanFederationEnabled = false;
                changed = true;
            }

            if (changed) value.Save();
            return value;
        }
        catch
        {
            return new WebServerPreferences();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void RegenerateToken() => AccessToken = CreateToken();

    public void AddOrUpdatePairedDesktopClient(WebPairedDesktopClient client)
    {
        PairedDesktopClients ??= new();
        PairedDesktopClients.RemoveAll(x => string.Equals(x.ClientId, client.ClientId, StringComparison.Ordinal));
        PairedDesktopClients.Add(PairedDesktopClientPreference.FromContract(client));
        PairedDesktopClients = PairedDesktopClients
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Save();
    }

    public void RevokeAllPairedDesktopClients()
    {
        PairedDesktopClients.Clear();
        Save();
    }

    public bool RevokePairedDesktopClient(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return false;
        var removed = PairedDesktopClients.RemoveAll(x =>
            string.Equals(x.ClientId, clientId.Trim(), StringComparison.Ordinal)) > 0;
        if (removed) Save();
        return removed;
    }

    private static string CreateToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}
