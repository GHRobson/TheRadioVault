using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using TheRadioVault.Core.Domain;

namespace TheRadioVault.Web.Models;

public sealed class WebServerOptions
{
    public string AppVersion { get; init; } = "unknown";
    public string ServerInstanceId { get; init; } = string.Empty;
    public string ServerDisplayName { get; init; } = "Radio Vault";
    public int DatabaseSchemaVersion { get; init; }
    public int CapabilityGeneration { get; init; } = 41;
    public int Port { get; init; } = 8765;
    public string AccessToken { get; init; } = string.Empty;
    public bool LoopbackOnly { get; init; }
    public bool SecureAccessEnabled { get; init; }
    public int SecurePort { get; init; } = 8766;
    public X509Certificate2? ServerCertificate { get; init; }
    public SslStreamCertificateContext? ServerCertificateContext { get; init; }
    public string ServerCertificateThumbprint { get; init; } = string.Empty;
    public byte[] RootCertificateDer { get; init; } = Array.Empty<byte>();
    public byte[] MobileConfigurationProfile { get; init; } = Array.Empty<byte>();
    public string RootCertificateThumbprint { get; init; } = string.Empty;
    public bool LanFederationEnabled { get; init; }
    public int LanDiscoveryPort { get; init; } = 30829;
    public IReadOnlyList<WebPairedDesktopClient> PairedDesktopClients { get; init; } = Array.Empty<WebPairedDesktopClient>();
    public Action<WebPairedDesktopClient>? PairedDesktopClientAdded { get; init; }
    public string MutationLedgerPath { get; init; } = string.Empty;
    public Func<WebScheduledBackupStatus>? ScheduledBackupStatus { get; init; }
}

public sealed record WebPairedDesktopClient(
    string ClientId,
    string DisplayName,
    string Token,
    DateTimeOffset PairedAt);

public sealed record WebDesktopPairingSession(
    string Code,
    DateTimeOffset ExpiresAt);

public sealed record WebDesktopPairingRequest(
    string Code,
    string ClientId,
    string DisplayName);

public sealed record WebDesktopPairingResult(
    bool Paired,
    string Message,
    string InstanceId,
    string DisplayName,
    string AccessToken,
    string CertificateThumbprint,
    int SecurePort,
    int CapabilityGeneration,
    DateTimeOffset? PairedAt);

public sealed record WebMutationAcknowledgement(
    string ClientId,
    string MutationId,
    DateTimeOffset ProcessedAt);

public sealed record WebDeviceSyncStatus(
    string ClientId,
    int AcknowledgedChanges,
    DateTimeOffset? LastAcknowledgedAt,
    string PersistenceError = "");

public sealed record WebScheduledBackupStatus(
    bool Enabled,
    bool IsRunning,
    DateTimeOffset? LastCompletedAt,
    DateTimeOffset? NextDueAt,
    string LatestBackupPath,
    bool LastBackupVerified,
    string LastError);

public sealed record WebLanDiscoveryAnnouncement(
    string Protocol,
    string InstanceId,
    string DisplayName,
    string AppVersion,
    string ApiVersion,
    int DatabaseSchemaVersion,
    int CapabilityGeneration,
    int SecurePort,
    string CertificateThumbprint,
    bool PairingAvailable,
    int PairedDesktopClients,
    DateTimeOffset GeneratedAt);

/// <summary>
/// Lightweight authenticated handshake used by paired remote clients. It
/// intentionally avoids playback, queue-item and dashboard projections so a
/// trust check cannot fail because an unrelated web-companion section is
/// temporarily unavailable.
/// </summary>
public sealed record WebFederationBootstrap(
    WebServerInfo Server,
    WebLibrarySummary Library,
    int QueueCount,
    DateTimeOffset GeneratedAt);

/// <summary>
/// Stable identity and feature contract for one Radio Vault server
/// instance. Future web and remote clients can inspect this before using
/// optional application services instead of inferring features from routes.
/// </summary>
public sealed record WebServerCapability(
    string Id,
    string Name,
    string Access,
    bool Available,
    string Notes = "");

public sealed record WebServerInfo(
    string InstanceId,
    string DisplayName,
    string AppVersion,
    string ApiVersion,
    int DatabaseSchemaVersion,
    int CapabilityGeneration,
    bool SecureAccess,
    DateTimeOffset StartedAt,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<WebServerCapability> Capabilities);


public sealed record WebRemoteClientParityFeature(
    string Id,
    string Name,
    string Access,
    bool Available,
    string Notes = "");

public sealed record WebRemoteClientParitySnapshot(
    string ServerInstanceId,
    string ServerDisplayName,
    string ServerVersion,
    int CapabilityGeneration,
    string ApiVersion,
    string LibraryRevision,
    long ChangeSequence,
    IReadOnlyList<WebRemoteClientParityFeature> Features,
    DateTimeOffset GeneratedAt)
{
    public int AvailableCount => Features.Count(x => x.Available);
    public int TotalCount => Features.Count;
    public bool FullParity => Features.Count > 0 && Features.All(x => x.Available);
}

public sealed record WebLibrarySummary(
    int BroadcastCount,
    int ShowCount,
    int FavouriteCount,
    int ContinueListeningCount,
    int CompletedCount,
    DateTime? NewestAirDate,
    DateTime? LastPlayedAt);

public sealed record WebShowSummary(string Name, int Count);

/// <summary>
/// Compact first-contact payload shared by the PWA and future LAN remote-client
/// clients. It intentionally contains only lightweight, already-cached views.
/// </summary>
public sealed class WebAnywhereBootstrap
{
    public required WebServerInfo Server { get; init; }
    public required WebLibrarySummary Library { get; init; }
    public IReadOnlyList<WebShowSummary> Shows { get; init; } = Array.Empty<WebShowSummary>();
    public IReadOnlyList<int> Years { get; init; } = Array.Empty<int>();
    public IReadOnlyList<WebEpisode> ContinueListening { get; init; } = Array.Empty<WebEpisode>();
    public IReadOnlyList<WebEpisode> Recent { get; init; } = Array.Empty<WebEpisode>();
    public IReadOnlyList<WebEpisode> Favourites { get; init; } = Array.Empty<WebEpisode>();
    public IReadOnlyList<WebEpisode> OnThisDay { get; init; } = Array.Empty<WebEpisode>();
    public IReadOnlyList<WebEpisode> Unheard { get; init; } = Array.Empty<WebEpisode>();
    public required WebPlaybackSession Playback { get; init; }
    public IReadOnlyList<WebQueueItem> Queue { get; init; } = Array.Empty<WebQueueItem>();
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record WebEpisode(
    long Id,
    string Show,
    string Title,
    DateTime? AirDate,
    string Summary,
    string PeopleSearchText,
    string TopicSearchText,
    long DurationMs,
    long PositionMs,
    string Status,
    bool Favourite,
    DateTime? LastPlayedAt,
    DateTime DateAdded,
    [property: JsonIgnore] string AudioPath,
    [property: JsonIgnore] string ArtworkPath)
{
    /// <summary>
    /// The web API exposes one row per canonical broadcast. Id remains for
    /// backwards compatibility while CanonicalBroadcastId makes that contract
    /// explicit to Anywhere and future LAN remote clients.
    /// </summary>
    public long CanonicalBroadcastId => Id;
    public string IdentityKind => "canonical-broadcast";
    public bool HasArtwork => !string.IsNullOrWhiteSpace(ArtworkPath);
    public int ProgressPercent => DurationMs > 0
        ? Math.Clamp((int)Math.Round(PositionMs * 100d / DurationMs), 0, 100)
        : 0;
}

public sealed record WebPerson(string Name, string Role);
public sealed record WebMetadataField(string Label, string Value);

public sealed record WebMoment(long Id, long PositionMs, string Title, string Notes)
{
    public int PositionSeconds => (int)Math.Max(0, PositionMs / 1000);
}

public sealed record WebMomentSummary(
    long Id,
    long EpisodeId,
    string Show,
    string EpisodeTitle,
    DateTime? AirDate,
    long PositionMs,
    string Title,
    string Notes,
    DateTime CreatedAt);

public sealed record WebResearchSource(string Title, string Url, string Publisher, string SourceType, int Confidence);

public sealed class WebResearchDetails
{
    public long ResearchBroadcastId { get; init; }
    public int Confidence { get; init; }
    public string ResearchState { get; init; } = string.Empty;
    public string ExistenceStatus { get; init; } = string.Empty;
    public bool NeedsReview { get; init; }
    public int ConflictCount { get; init; }
    public IReadOnlyList<WebResearchSource> Sources { get; init; } = Array.Empty<WebResearchSource>();
}

public sealed class WebBroadcastDetails
{
    public required WebEpisode Episode { get; init; }
    public long CanonicalBroadcastId => Episode.CanonicalBroadcastId;
    public string BroadcastUid { get; init; } = string.Empty;
    public string Station { get; init; } = string.Empty;
    public string Slot { get; init; } = string.Empty;
    public int PartNumber { get; init; } = 1;
    public int? TotalParts { get; init; }
    public string ArchiveNotes { get; init; } = string.Empty;
    public string PersonalNotes { get; init; } = string.Empty;
    public IReadOnlyList<WebPerson> People { get; init; } = Array.Empty<WebPerson>();
    public IReadOnlyList<string> Topics { get; init; } = Array.Empty<string>();
    public IReadOnlyList<WebMetadataField> CatalogueFields { get; init; } = Array.Empty<WebMetadataField>();
    public IReadOnlyList<WebMoment> Moments { get; init; } = Array.Empty<WebMoment>();
    public WebResearchDetails? Research { get; init; }
}

// Full-fidelity read contracts used by native and future remote clients. These
// stay presentation-neutral so every client can render the same server-owned
// library without opening the archive database itself.
public sealed record WebClientLibraryBrowseRequest(
    string SearchText,
    int? CollectionId,
    string Filter,
    int? Year,
    int? Month,
    int Limit,
    int Offset,
    bool NewestFirst,
    string SearchScope,
    bool HasTranscript,
    bool HideCompleted = false);

public sealed record WebClientLibraryBroadcastSummary(
    string CanonicalKey,
    long RepresentativeEpisodeId,
    string BroadcastId,
    int CollectionId,
    string CollectionName,
    DateOnly? AirDate,
    DateTimeOffset DateAdded,
    string BroadcastSlot,
    string? Title,
    string? Description,
    bool Favourite,
    bool Completed,
    bool InProgress,
    long PositionMs,
    long DurationMs,
    DateTimeOffset? LastPlayedAt,
    string? ArtworkPath,
    int RecordingCount,
    int SegmentCount,
    int PhysicalFileCount,
    bool NeedsAttention,
    string AttentionReason,
    string SearchContext,
    int SearchScore);

public sealed record WebClientLibraryCollectionSummary(int CollectionId, string CollectionName, int BroadcastCount);

public sealed record WebClientLibraryArchivePeriodSummary(
    int Value,
    string Title,
    int BroadcastCount,
    int CompletedCount,
    int FavouriteCount,
    int ProgressPercent,
    string ProgressText,
    string ShowsText,
    string? ArtworkPath);

public sealed record WebClientLibraryOverview(
    int TotalBroadcasts,
    int CompletedBroadcasts,
    int InProgressBroadcasts,
    int FavouriteBroadcasts,
    int NeedsAttentionBroadcasts,
    bool UsesCanonicalLibrary,
    IReadOnlyList<WebClientLibraryCollectionSummary> Collections,
    IReadOnlyList<WebClientLibraryBroadcastSummary> ContinueListening,
    IReadOnlyList<WebClientLibraryBroadcastSummary> RecentBroadcasts,
    IReadOnlyList<WebClientLibraryBroadcastSummary> OnThisDay);

public sealed record WebLiveRadioProgramme(
    long ScheduleEntryId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    long PositionMs,
    long RemainingMs,
    string SelectionReason,
    WebClientLibraryBroadcastSummary Broadcast);

public sealed record WebLiveRadioSnapshot(
    string StationKey,
    string StationName,
    string TimeZoneId,
    DateTimeOffset ServerTime,
    long ScheduleRevision,
    WebLiveRadioProgramme? Current,
    IReadOnlyList<WebLiveRadioProgramme> Upcoming);

public sealed record WebClientLibraryBrowseResult(
    IReadOnlyList<WebClientLibraryBroadcastSummary> Broadcasts,
    int TotalMatching,
    bool UsesCanonicalLibrary);

public sealed record WebClientLibrarySearchFacets(IReadOnlyList<int> Years, int TranscriptCount);
public sealed record WebClientLibrarySearchSuggestion(string Value, string Kind, int MatchCount);

public sealed record WebSavedCollectionRule(
    string? SearchText = null,
    int? CollectionId = null,
    string Filter = "All",
    int? Year = null,
    int? Month = null,
    string SearchScope = "All",
    bool HasTranscript = false,
    bool HideCompleted = false,
    bool NewestFirst = true,
    int Limit = 250);

public sealed record WebSavedCollectionSummary(
    long Id,
    string Name,
    string Kind,
    int? ItemCount,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record WebSavedCollectionDetails(
    WebSavedCollectionSummary Summary,
    WebSavedCollectionRule? Rule,
    IReadOnlyList<WebClientLibraryBroadcastSummary> Broadcasts);

public sealed record WebSavedCollectionCreateRequest(
    string Name,
    string Kind = "Manual",
    WebSavedCollectionRule? Rule = null,
    bool FromQueue = false,
    IReadOnlyList<long>? EpisodeIds = null);

public sealed record WebSavedCollectionUpdateRequest(
    string Name,
    WebSavedCollectionRule? Rule,
    long ExpectedRevision);

public sealed record WebSavedCollectionItemMutation(
    long EpisodeId,
    long ExpectedRevision,
    int? TargetIndex = null);

public sealed record WebSavedCollectionDeleteRequest(long ExpectedRevision);

public sealed record WebSavedCollectionMutationResult(
    bool Changed,
    bool Conflict,
    bool NotFound,
    string Message,
    WebSavedCollectionDetails? Collection,
    long? CurrentRevision = null);

public sealed record WebClientBroadcastDetails(
    long RepresentativeEpisodeId,
    string CanonicalKey,
    string BroadcastId,
    int CollectionId,
    string CollectionName,
    DateOnly? AirDate,
    string Slot,
    string Title,
    string Summary,
    string Station,
    string Edition,
    string BroadcastVariant,
    string BroadcastEra,
    string EpisodeType,
    string ArchiveNotes,
    string CatalogueSeries,
    string CatalogueProgramme,
    string CatalogueFormat,
    string OriginalReleaseDate,
    string RecordingDate,
    string Venue,
    string Event,
    string Network,
    string CatalogueNumber,
    string OriginalFilename,
    string Provenance,
    string ResearchNotes,
    string PersonalNotes,
    string Hosts,
    string Guests,
    string Callers,
    string MentionedPeople,
    IReadOnlyList<string> Topics,
    string? ArtworkPath,
    int RecordingCount,
    int SegmentCount,
    int PhysicalFileCount,
    IReadOnlyList<ArchiveEntityLink>? EntityLinks = null);


public sealed record WebTranscriptSummary(
    long TranscriptId,
    long EpisodeId,
    string Show,
    DateTime? AirDate,
    string EpisodeTitle,
    string Status,
    string Language,
    string EngineId,
    string ModelId,
    string Source,
    int WordCount,
    int SegmentCount,
    int SpeakerCount,
    int IdentifiedSpeakerCount,
    long DurationMs,
    DateTimeOffset UpdatedAt);

public sealed record WebTranscriptSegment(
    int Index,
    long StartMs,
    long EndMs,
    string Text,
    string Speaker,
    string SpeakerKey,
    string ContentKind,
    bool IsReviewed,
    double? Confidence);

public sealed class WebTranscriptDetails
{
    public long CanonicalBroadcastId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
    public int WordCount { get; init; }
    public long DurationMs { get; init; }
    public bool HasSpeakerDiarization { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public IReadOnlyList<WebTranscriptSegment> Segments { get; init; } = Array.Empty<WebTranscriptSegment>();
}

public sealed record WebBroadcastMetadataMutation(
    string Title,
    string Description,
    string Notes,
    string Edition,
    string Hosts,
    string Guests,
    string Callers,
    string MentionedPeople,
    string Tags);

public sealed record WebMomentMutation(long PositionMs, string Title, string Notes, string ClientMutationId = "");
public sealed record WebMomentEditMutation(string Title, string Notes, string ClientMutationId = "");
public sealed record WebMomentMutationResult(bool Changed, bool Duplicate, string Message, WebMoment? Moment);

public sealed record WebArchiveHealthSummary(
    int OverallScore,
    int CollectionScore,
    int MetadataScore,
    int ResearchScore,
    int PreservationScore,
    int ActionableIssues,
    int MissingBroadcasts,
    int ResearchNeedsReview,
    int PendingReconciliation,
    DateTime? LastCompletedScanAt,
    bool ResearchAssessed = true);

public sealed record WebPlaybackState(
    long? EpisodeId,
    string Show,
    string Title,
    long PositionMs,
    long DurationMs,
    string Status,
    DateTime? LastPlayedAt,
    bool IsPlaying = false,
    DateTimeOffset? UpdatedAt = null,
    string Device = "Server",
    double Speed = 1d,
    long Revision = 0,
    string ControllerClientId = "")
{
    public int ProgressPercent => DurationMs > 0
        ? Math.Clamp((int)Math.Round(PositionMs * 100d / DurationMs), 0, 100)
        : 0;
}

/// <summary>
/// Versioned remote playback command. ExpectedRevision prevents a stale phone
/// from overwriting a newer server or second-client action.
/// </summary>
public sealed record WebPlaybackCommand(
    string Command,
    string ClientId,
    long? EpisodeId = null,
    long? PositionMs = null,
    int? DeltaSeconds = null,
    double? Speed = null,
    long? ExpectedRevision = null,
    bool Force = false,
    string DeviceName = "",
    string DeviceKind = "");

public sealed record WebPlaybackCommandResult(
    bool Changed,
    bool Conflict,
    string Message,
    WebPlaybackState Player);

/// <summary>
/// One synchronized Radio Vault playback session with a single active output.
/// OwnerDevice remains authoritative while paused so the inactive client can
/// render a transfer control instead of accidentally operating the other device.
/// </summary>
public sealed record WebPlaybackSession(
    WebPlaybackState Player,
    WebPlaybackState Desktop,
    WebPlaybackState Phone,
    string OwnerDevice,
    string OwnerClientId,
    long Generation)
{
    public IReadOnlyList<WebPlaybackDevice> Devices { get; init; } = Array.Empty<WebPlaybackDevice>();
    public WebPlaybackTransferTicket? PendingTransfer { get; init; }
    public WebPlaybackCommittedTransfer? CommittedTransfer { get; init; }
}

/// <summary>
/// Immutable snapshot of one transactional playback move. The current output
/// remains authoritative until the target has prepared, aligned and committed.
/// </summary>
public sealed record WebPlaybackTransferTicket(
    Guid TransferId,
    string TargetClientId,
    string TargetDeviceName,
    string TargetDeviceKind,
    long TargetEpisodeId,
    long ProtectedPositionMs,
    long CommitPositionMs,
    long DurationMs,
    double Speed,
    bool DesiredPlaying,
    bool DesiredPlayingOverridden,
    string SourceOwnerDevice,
    string SourceOwnerClientId,
    long? SourceEpisodeId,
    long SourceGeneration,
    long ReadyRevision,
    bool IsReady,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt);

public sealed record WebPlaybackTransferBeginRequest(
    string ClientId,
    long EpisodeId,
    long PositionMs,
    long DurationMs,
    double Speed,
    bool DesiredPlaying,
    string DeviceName = "",
    string DeviceKind = "");

public sealed record WebPlaybackTransferReadyRequest(
    string ClientId,
    Guid TransferId,
    long PreparedPositionMs,
    long PreparedDurationMs,
    bool DecoderReady,
    bool DesiredPlaying,
    bool OverrideDesiredPlaying,
    double Speed,
    string DeviceName = "",
    string DeviceKind = "");

public sealed record WebPlaybackTransferCommitRequest(
    string ClientId,
    Guid TransferId,
    long ReadyRevision,
    long PreparedPositionMs,
    bool DecoderRunningMuted,
    bool DecoderRunningAudibly = false);

public sealed record WebPlaybackTransferCancelRequest(
    string ClientId,
    Guid TransferId,
    string Reason = "");

public sealed record WebPlaybackTransferSourceStoppedRequest(
    string ClientId,
    Guid TransferId,
    long Generation);

/// <summary>
/// Receipt for the most recently committed move. Remote sources acknowledge only
/// after their physical decoder has stopped, allowing the target to remain muted
/// until the old output is genuinely quiescent rather than merely marked inactive.
/// </summary>
public sealed record WebPlaybackCommittedTransfer(
    Guid TransferId,
    string SourceClientId,
    string SourceDeviceName,
    string TargetClientId,
    string TargetDeviceName,
    long Generation,
    bool SourceWasPlaying,
    bool SourceStopAcknowledged,
    DateTimeOffset CommittedAt,
    DateTimeOffset? SourceStoppedAt);

public sealed record WebPlaybackTransferResult(
    bool Changed,
    bool Conflict,
    string Message,
    WebPlaybackTransferTicket? Transfer,
    WebPlaybackSession Session);

public sealed record WebPlaybackDevice(
    string DeviceId,
    string DisplayName,
    string Kind,
    WebPlaybackState State,
    DateTimeOffset LastSeenAt,
    bool IsOnline,
    bool IsOwner);

/// <summary>
/// Disposable live-state heartbeat from a browser or remote desktop player.
/// It updates the in-memory shared playhead and ownership lease only; durable
/// listening progress is committed through WebOfflineProgressUpdate boundaries.
/// </summary>
public sealed record WebClientPlaybackUpdate(
    string ClientId,
    long EpisodeId,
    long PositionMs,
    long DurationMs,
    bool IsPlaying,
    double Speed = 1d,
    bool Completed = false,
    bool Force = false,
    string DeviceName = "Phone",
    string DeviceKind = "Phone",
    long ExpectedGeneration = 0,
    bool ExplicitSeek = false);

public sealed record WebClientPlaybackResult(
    bool Changed,
    bool Conflict,
    string Message,
    WebPlaybackState Player);

/// <summary>
/// Durable canonical listening-progress boundary. Offline downloads may submit
/// monotonic reconciliation records; the committed live owner may additionally
/// submit generation-bound pause, seek, stop, completion and timer boundaries.
/// </summary>
public sealed record WebOfflineProgressUpdate(
    string ClientId,
    long EpisodeId,
    long PositionMs,
    long DurationMs,
    bool Completed = false,
    double Speed = 1d,
    DateTimeOffset? CapturedAt = null,
    bool AllowRewind = false,
    long ExpectedGeneration = 0,
    bool ExplicitSeek = false,
    bool IncrementPlayCount = false);

public sealed record WebOfflineProgressResult(
    bool Changed,
    string Message,
    WebEpisode? Episode = null,
    bool Conflict = false);

public sealed record WebQueueItem(long QueueId, int Position, WebEpisode Episode);

public sealed record WebQueueMutationResult(
    bool Changed,
    string Message,
    IReadOnlyList<WebQueueItem> Queue);

public sealed record WebChangeEvent(
    long Sequence,
    string Kind,
    long? EpisodeId,
    string Reason,
    DateTimeOffset OccurredAt);


/// <summary>
/// Bounded change-feed state used by persistent remote-client library caches.
/// EarliestAvailableSequence allows a client to detect when it has fallen
/// behind the retained journal and must request a safe full reset.
/// </summary>
public sealed record WebChangeFeedSnapshot(
    long CurrentSequence,
    long EarliestAvailableSequence,
    IReadOnlyList<WebChangeEvent> Changes);

/// <summary>
/// One cache synchronisation response for a paired remote client. A reset
/// contains the complete canonical library. A delta contains only changed
/// broadcasts plus deletions, while Bootstrap refreshes dashboard, queue and
/// library summary projections.
/// </summary>
#if !RADIOVAULT_PROTOCOL
public sealed record WebFederationLibrarySync(
    string ServerInstanceId,
    string SessionId,
    long Sequence,
    string LibraryRevision,
    bool ResetRequired,
    bool NoChanges,
    IReadOnlyList<WebChangeEvent> Changes,
    IReadOnlyList<WebEpisode> Episodes,
    IReadOnlyList<long> DeletedEpisodeIds,
    WebAnywhereBootstrap? Bootstrap,
    IReadOnlyList<WebTranscriptSummary>? TranscriptSummaries,
    IReadOnlyList<WebMomentSummary>? Moments,
    WebAuthoritativeSettingsSnapshot? SettingsSnapshot,
    DateTimeOffset GeneratedAt);
#endif

public sealed record WebJobSummary(
    Guid JobId,
    string Name,
    string Category,
    string State,
    double? Percent,
    string Message,
    bool CanCancel,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record WebMutationResult(bool Changed, string Message, WebEpisode? Episode = null);

public sealed record WebJobActionResult(bool Changed, string Message);


public sealed record WebCanonicalMediaPart(
    int PartNumber,
    int? PartTotal,
    long LogicalStartMs,
    long LogicalEndMs,
    long MediaFileId,
    long SizeBytes,
    string StorageState,
    [property: JsonIgnore] string AudioPath);

public sealed record WebCanonicalMediaManifest(
    long EpisodeId,
    string CanonicalKey,
    string RecordingKey,
    string Label,
    long DurationMs,
    IReadOnlyList<WebCanonicalMediaPart> Parts)
{
    public bool IsMultipart => Parts.Count > 1;
    public long TotalSizeBytes => Parts.Sum(x => Math.Max(0, x.SizeBytes));
}
