using System.Text.Json;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Web.Contracts;

public interface IWebArchiveProvider
{
    IReadOnlyList<WebEpisode> GetEpisodes();
    WebEpisode? GetEpisode(long episodeId);
    WebBroadcastDetails? GetBroadcastDetails(long episodeId);
    WebClientLibraryOverview GetClientLibraryOverview();
    WebClientLibraryBroadcastSummary? GetClientLibraryBroadcast(long episodeId);
    WebClientLibraryBrowseResult BrowseClientLibrary(WebClientLibraryBrowseRequest request);
    IReadOnlyList<WebClientLibraryArchivePeriodSummary> GetClientLibraryArchivePeriods(int? collectionId, int? year, bool hideCompleted);
    WebClientLibrarySearchFacets GetClientLibrarySearchFacets();
    IReadOnlyList<WebClientLibrarySearchSuggestion> GetClientLibrarySearchSuggestions(string prefix, int limit);
    WebClientBroadcastDetails? GetClientBroadcastDetails(long episodeId);
    Task<object?> ExecuteClientResearchAsync(string operation, JsonElement payload, CancellationToken cancellationToken = default);
    Task<object?> ExecuteClientTranscriptAsync(string operation, JsonElement payload, CancellationToken cancellationToken = default);
    Task<object?> ExecuteClientSpeakerAsync(string operation, JsonElement payload, CancellationToken cancellationToken = default);
    Task<object?> ExecuteClientTranscriptionAsync(string operation, JsonElement payload, CancellationToken cancellationToken = default);
    Task<object?> ExecuteClientWikiAsync(string operation, JsonElement payload, CancellationToken cancellationToken = default);
    WebTranscriptDetails? GetTranscript(long episodeId);
    IReadOnlyList<WebTranscriptSummary> GetTranscripts();
    IReadOnlyList<WebMomentSummary> GetMoments();
    WebMomentMutationResult AddMoment(long episodeId, WebMomentMutation mutation);
    WebMutationResult UpdateMoment(long momentId, WebMomentEditMutation mutation);
    WebMutationResult DeleteMoment(long episodeId, long momentId);
    WebCanonicalMediaManifest? GetCanonicalMediaManifest(long episodeId, string? recordingKey = null);
    WebCanonicalMediaPart? GetCanonicalMediaPart(long episodeId, long mediaFileId, string? recordingKey = null);
    WebArchiveHealthSummary GetArchiveHealth();
    WebPlaybackState GetPlaybackState();
    WebPlaybackState GetWebPlaybackState();
    WebPlaybackSession GetPlaybackSession();
    WebPlaybackTransferResult BeginPlaybackTransfer(WebPlaybackTransferBeginRequest request);
    WebPlaybackTransferResult MarkPlaybackTransferReady(WebPlaybackTransferReadyRequest request);
    WebPlaybackTransferResult CommitPlaybackTransfer(WebPlaybackTransferCommitRequest request);
    WebPlaybackTransferResult CancelPlaybackTransfer(WebPlaybackTransferCancelRequest request);
    WebPlaybackTransferResult AcknowledgePlaybackTransferSourceStopped(WebPlaybackTransferSourceStoppedRequest request);
    WebPlaybackCommandResult ExecutePlaybackCommand(WebPlaybackCommand command);
    WebClientPlaybackResult UpdateWebPlayback(WebClientPlaybackUpdate update);
    WebOfflineProgressResult SyncOfflineProgress(WebOfflineProgressUpdate update);
    IReadOnlyList<WebQueueItem> GetQueue();
    WebQueueMutationResult AddToQueue(long episodeId, bool playNext);
    WebQueueMutationResult RemoveFromQueue(long queueId);
    WebQueueMutationResult ClearQueue();
    WebQueueMutationResult MoveQueueItem(long queueId, int direction);
    IReadOnlyList<WebChangeEvent> GetChanges(long afterSequence, int limit);
    WebChangeFeedSnapshot GetChangeFeed(long afterSequence, int limit);
    IReadOnlyList<WebJobSummary> GetJobs();
    WebJobActionResult CancelJob(Guid jobId);
    WebMutationResult SetFavourite(long episodeId, bool favourite);
    WebMutationResult SetPlayed(long episodeId, bool played);
    WebMutationResult UpdateBroadcastMetadata(long episodeId, WebBroadcastMetadataMutation mutation);
    WebAuthoritativeSettingsSnapshot GetAuthoritativeSettings();
    WebLibraryScanSnapshot GetLibraryScanStatus();
    Task<WebLibraryScanSnapshot> RunLibraryScanAsync(string trigger, CancellationToken cancellationToken = default);
    WebResearchWorkspaceSnapshot GetResearchWorkspace();
    WebResearchWorkspaceRecordDetails? GetResearchWorkspaceRecord(long researchBroadcastId);
    IReadOnlyList<WebUndatedBroadcast> GetUndatedResearchBroadcasts(int? collectionId = null);
    WebAssignBroadcastDateResult AssignResearchBroadcastDate(long episodeId, DateTime broadcastDate);
    WebResearchCoverageShow? GetResearchCoverage(int collectionId);
    WebResearchCoverageShow? GetResearchCoverageByShow(string show);
    WebPlaybackPreferencesSnapshot SetPlaybackPreferences(WebPlaybackPreferencesSnapshot preferences);
    Task<WebResearchPackPreviewResponse> PreviewResearchPackAsync(Stream packageStream, string sourceName, CancellationToken cancellationToken = default);
    WebResearchPackImportJob StartResearchPackImport(Guid sessionId);
    WebResearchPackImportJob GetResearchPackImportStatus(Guid sessionId);
    bool CancelResearchPackImport(Guid sessionId);
    Task<WebResearchPackExportPayload> ExportResearchPackAsync(CancellationToken cancellationToken = default);
    Task<WebWikiPackPreview> PreviewWikiPackAsync(Stream packageStream, string sourceName, CancellationToken cancellationToken = default);
    Task<WebWikiPackImportResult> ApplyWikiPackAsync(Stream packageStream, string sourceName, string expectedSha256, CancellationToken cancellationToken = default);
    Task<WebWikiPackExportPayload> ExportWikiPackAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Server-shell adapter used by the platform-neutral web assembly. The WPF
/// implementation marshals commands to its dispatcher and owns the actual
/// playback engine; the HTTP server never touches WPF controls directly.
/// </summary>
public interface IWebPlaybackController
{
    WebPlaybackState GetPlaybackState();
    WebPlaybackCommandResult ExecutePlaybackCommand(WebPlaybackCommand command);
}
