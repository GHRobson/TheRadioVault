namespace TheRadioVault.Application.Models;

public sealed record RadioVaultAnywhereClient(
    string ClientId,
    string DisplayName,
    DateTimeOffset PairedAt)
{
    public string PairedAtText => PairedAt.ToLocalTime().ToString("g");
}

public sealed record RadioVaultAnywhereSnapshot(
    bool IsRemoteSession,
    bool IsAvailable,
    bool IsRunning,
    bool IsSecure,
    bool Enabled,
    bool StartAutomatically,
    string ServerDisplayName,
    int HttpPort,
    int HttpsPort,
    int DiscoveryPort,
    string AccessUrl,
    string SetupUrl,
    string StatusText,
    string DetailText,
    string PairingCode,
    DateTimeOffset? PairingExpiresAt,
    IReadOnlyList<RadioVaultAnywhereClient> PairedClients,
    IReadOnlyList<string> DiagnosticChecks)
{
    public bool CanManage => !IsRemoteSession && IsAvailable;
    public bool HasAccessUrl => !string.IsNullOrWhiteSpace(AccessUrl);
    public bool HasSetupUrl => !string.IsNullOrWhiteSpace(SetupUrl);
    public bool HasPairingCode => !string.IsNullOrWhiteSpace(PairingCode);
    public bool HasPairedClients => PairedClients.Count > 0;
    public string RunningLabel => IsRunning ? "RUNNING" : "STOPPED";
    public string SecurityLabel => IsSecure ? "HTTPS" : "HTTP";
    public string PairingExpiryText => PairingExpiresAt.HasValue
        ? $"Expires {PairingExpiresAt.Value.ToLocalTime():t}"
        : string.Empty;
}

public sealed record RadioVaultAnywhereSettings(
    bool Enabled,
    bool StartAutomatically,
    string ServerDisplayName,
    int HttpPort,
    bool SecureAccessEnabled,
    int HttpsPort,
    int DiscoveryPort);
