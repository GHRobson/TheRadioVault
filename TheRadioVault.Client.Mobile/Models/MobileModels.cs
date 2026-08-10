using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile.Models;

public sealed record RadioVaultMobileConnection(
    string ClientId,
    string ClientDisplayName,
    string ServerInstanceId,
    string ServerDisplayName,
    string ServerAddress,
    int SecurePort,
    string CertificateThumbprint,
    string AccessToken,
    int CapabilityGeneration,
    DateTimeOffset PairedAt)
{
    public bool IsConfigured =>
        Guid.TryParse(ClientId, out _) &&
        Guid.TryParse(ServerInstanceId, out _) &&
        !string.IsNullOrWhiteSpace(ServerAddress) &&
        SecurePort is >= 1024 and <= 65535 &&
        CertificateThumbprint.Length >= 32 &&
        AccessToken.Length >= 32;
}

public sealed record DiscoveredRadioVaultServer(
    string InstanceId,
    string DisplayName,
    string Address,
    int SecurePort,
    string CertificateThumbprint,
    string AppVersion,
    bool PairingAvailable,
    int PairedClients)
{
    public string Detail => $"{Address}:{SecurePort} · v{AppVersion}";
    public string PairingText => PairingAvailable ? "Pairing code ready" : "Create a pairing code on the server";
}

public sealed record MobileFavouriteMutation(bool Favourite);

public sealed record MobileListeningStatusMutation(bool Played);

public sealed record MobileQueueAddMutation(long EpisodeId, bool PlayNext = false);

public sealed record MobileQueueMoveMutation(int Direction);

public sealed record MobileEmptyMutation;

public sealed record MobileLibrarySyncEnvelope(MobileLibrarySync Sync);

public sealed record MobileLibrarySync(
    string ServerInstanceId,
    string SessionId,
    long Sequence,
    string LibraryRevision,
    bool ResetRequired,
    bool NoChanges,
    IReadOnlyList<WebChangeEvent> Changes,
    DateTimeOffset GeneratedAt);

public sealed record MobileMetadataCacheSnapshot(
    int Version,
    string ServerInstanceId,
    string SyncSessionId,
    long SyncSequence,
    string SyncRevision,
    IReadOnlyList<WebClientLibraryBroadcastSummary> Broadcasts,
    WebClientLibraryOverview? Overview,
    IReadOnlyList<WebQueueItem> Queue,
    MobileWikiOverview? ExploreOverview,
    IReadOnlyList<MobileWikiPageSummary> ExplorePages,
    MobileWikiDashboardHighlights? ExploreHighlights,
    IReadOnlyList<MobileWikiPageDocument> ExploreDocuments,
    DateTimeOffset UpdatedAt)
{
    public static MobileMetadataCacheSnapshot Empty(string serverInstanceId) => new(
        1,
        serverInstanceId,
        string.Empty,
        0,
        string.Empty,
        [],
        null,
        [],
        null,
        [],
        null,
        [],
        DateTimeOffset.MinValue);
}

public sealed class MobileBroadcastItem
{
    public MobileBroadcastItem(WebClientLibraryBroadcastSummary value)
    {
        Source = value;
        Title = string.IsNullOrWhiteSpace(value.Title) ? value.CollectionName : value.Title.Trim();
        Subtitle = value.AirDate is DateOnly date
            ? $"{value.CollectionName} · {date:dd MMM yyyy}"
            : value.CollectionName;
        Progress = value.DurationMs <= 0 ? 0 : Math.Clamp(value.PositionMs * 100d / value.DurationMs, 0d, 100d);
    }

    public WebClientLibraryBroadcastSummary Source { get; }
    public long EpisodeId => Source.RepresentativeEpisodeId;
    public string Title { get; }
    public string Subtitle { get; }
    public string Description => Source.Description ?? string.Empty;
    public double Progress { get; }
    public double DisplayProgress => Source.Completed ? 100d : Progress;
    public bool HasProgress => Progress > 0.5d;
    public string Status => Source.Completed ? "Played" : Source.InProgress ? $"{Progress:0}% listened" : "Unplayed";
}
