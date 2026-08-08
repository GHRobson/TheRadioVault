namespace TheRadioVault.Services.Models;

public enum ConnectedAccessState
{
    LocalLibrary,
    Disconnected,
    Discovering,
    Pairing,
    Connecting,
    Updating,
    Live,
    CachedReadOnly,
    Unavailable
}

public sealed record ConnectedServerOption(
    string InstanceId,
    string DisplayName,
    string Address,
    int SecurePort,
    string AppVersion,
    bool PairingAvailable,
    int PairedDesktopClients)
{
    public string DisplayText => $"{DisplayName} · {Address}:{SecurePort}";
    public string DetailText => PairingAvailable
        ? $"Radio Vault {AppVersion} · pairing ready"
        : $"Radio Vault {AppVersion} · enable desktop pairing on the server";
}

public sealed record ConnectedAccessSnapshot(
    ConnectedAccessState State,
    bool IsRemoteSession,
    bool IsLive,
    bool IsCachedReadOnly,
    bool HasSavedServer,
    bool UseRemoteOnStartup,
    string ServerDisplayName,
    string ServerAddress,
    string SavedServerDisplayName,
    string SavedServerAddress,
    string StatusText,
    string DetailText,
    DateTimeOffset? LastLiveAt,
    DateTimeOffset? NextReconnectAt,
    int BroadcastCount,
    int ShowCount,
    int CapabilityGeneration,
    long CacheSizeBytes,
    string LastError)
{
    public string ModeLabel => IsRemoteSession ? "REMOTE SERVER" : "THIS COMPUTER";
    public string PlaybackLabel => IsRemoteSession ? "REMOTE SERVER PLAYBACK" : "LOCAL SERVER PLAYBACK";
    public string StateLabel => State switch
    {
        ConnectedAccessState.Live => "LIVE",
        ConnectedAccessState.CachedReadOnly => "CACHED",
        ConnectedAccessState.Connecting => "CONNECTING",
        ConnectedAccessState.Updating => "UPDATING",
        ConnectedAccessState.Unavailable => "UNAVAILABLE",
        ConnectedAccessState.Discovering => "DISCOVERING",
        ConnectedAccessState.Pairing => "PAIRING",
        ConnectedAccessState.Disconnected => "DISCONNECTED",
        _ => "LOCAL"
    };
    public string CacheSizeText => CacheSizeBytes <= 0
        ? "No encrypted cache saved"
        : CacheSizeBytes >= 1024L * 1024L
            ? $"{CacheSizeBytes / (1024d * 1024d):0.0} MB encrypted cache"
            : $"{CacheSizeBytes / 1024d:0} KB encrypted cache";
}
