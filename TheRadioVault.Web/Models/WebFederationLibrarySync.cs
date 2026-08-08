namespace TheRadioVault.Web.Models;

/// <summary>
/// Server-side cache synchronisation response. The portable client protocol
/// deliberately omits this envelope because its settings snapshot is backed
/// by server-administration models.
/// </summary>
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
