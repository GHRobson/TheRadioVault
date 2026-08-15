namespace TheRadioVault.Web.Models;

public static class WebResearchPackLimits
{
    // Deep Research Packs can contain complete timestamped transcripts for an
    // entire show. Keep a finite authenticated-upload boundary, but allow the
    // larger packages produced by the current exporter.
    public const int MaximumPackageBytes = 512 * 1024 * 1024;
}

public sealed record WebResearchPackPreview(
    string PackageName,
    string Show,
    int TotalRecords,
    int ExactMatches,
    int MissingRecords,
    int AmbiguousMatches,
    int NewPeople,
    int NewTopics,
    int NewSources,
    int IncomingSummaries,
    int ProtectedManualRecords,
    int PotentialConflicts,
    int FieldsExpectedToApply,
    int FieldsExpectedToMerge,
    int FieldsExpectedToPreserve,
    int FieldsProtectedByManualEdits,
    bool AuthoritativeAudit,
    bool PreviouslyImported,
    string PackageHash,
    int TranscriptCount = 0,
    int WikiPageCount = 0,
    int WikiImageCount = 0,
    int WikiTimelineEventCount = 0);

public sealed record WebResearchPackPreviewResponse(
    Guid SessionId,
    WebResearchPackPreview Preview,
    DateTimeOffset ExpiresAt);

public sealed record WebResearchPackApplyRequest(Guid SessionId);
public sealed record WebResearchPackCancelRequest(Guid SessionId);

public sealed record WebResearchPackImportJob(
    Guid SessionId,
    Guid JobId,
    string State,
    double Percent,
    string Message,
    int Current,
    int Total,
    bool CanCancel,
    WebResearchPackImportResult? Result = null,
    string? Error = null);

public sealed record WebResearchPackImportResult(
    int Total,
    int Matched,
    int Updated,
    int RetainedMissing,
    int Ambiguous,
    int ResolvedPreviousMissing,
    int ResearchRecordsStored,
    int AttachedResearchRecords,
    int ConfirmedMissing,
    int ProbableMissing,
    int UnknownGaps,
    int ConflictsCreated,
    long ImportRunId,
    int FieldsApplied,
    int FieldsMerged,
    int FieldsPreserved,
    int ManualFieldsProtected,
    int ChangeRecordsWritten,
    int WikiPagesChanged = 0,
    int WikiConflicts = 0);

public sealed record WebResearchPackExportRequest(string Scope = "complete");

public sealed record WebResearchPackExportPayload(
    byte[] Bytes,
    string FileName,
    int BroadcastCount,
    int MissingBroadcastCount,
    int TranscriptCount = 0,
    int WikiPageCount = 0);

public sealed record WebWikiPackPreview(
    string PackageName,
    string PackageSha256,
    int TotalPages,
    int NewPages,
    int ChangedPages,
    int UnchangedPages,
    int ConflictingPages,
    int SourceCount,
    int CitationCount,
    int ImageCount,
    int TimelineEventCount,
    IReadOnlyList<string> ConflictTitles,
    bool CanApply,
    string Summary,
    IReadOnlyList<WebWikiPackPageChangePreview> PageChanges);

public sealed record WebWikiPackPageChangePreview(
    Guid PageId,
    string Title,
    string PageType,
    string ChangeKind,
    int IncomingBaseRevision,
    int? CurrentRevision,
    string Detail);

public sealed record WebWikiPackImportResult(
    int CreatedPages,
    int UpdatedPages,
    int UnchangedPages,
    int SkippedConflicts,
    int SourcesStored,
    int CitationsStored,
    int ImagesStored,
    int TimelineEventsStored,
    long ImportRunId,
    string Summary);

public sealed record WebWikiPackExportPayload(byte[] Bytes, string FileName, int PageCount, int ImageCount);

public sealed record WebPlaybackPreferencesSnapshot(
    int SkipBackSeconds,
    int SkipForwardSeconds,
    int CompletionThresholdSeconds,
    DateTimeOffset UpdatedAt);

public sealed record WebArchiveFolderSnapshot(
    int Id,
    string Path,
    string CollectionName,
    bool Recursive,
    DateTime? LastScanAt)
{
    public string Display => $"{Path}  ·  {CollectionName}  ·  last scan {(LastScanAt.HasValue ? LastScanAt.Value.ToString("g") : "never")}";
}

public sealed record WebStorageSnapshot(
    int TotalFiles,
    int AvailableOffline,
    int CloudOnly,
    int Missing,
    long LogicalBytes);

public sealed record WebPreservationSnapshot(
    int TotalFiles,
    int LocalFiles,
    int MissingEvidence,
    int PartialFingerprints,
    int FullHashes,
    int StrongDuplicateFilesAwaitingFullHash,
    int InspectionErrors,
    DateTimeOffset? LastCompletedScanAt);

public sealed record WebAuthoritativeSettingsSnapshot(
    IReadOnlyList<WebArchiveFolderSnapshot> ArchiveFolders,
    WebStorageSnapshot Storage,
    WebPreservationSnapshot Preservation,
    WebArchiveHealthSummary ArchiveHealth,
    WebPlaybackPreferencesSnapshot Playback,
    int ResearchAttention,
    string DatabaseQuickCheck,
    DateTimeOffset? LatestBackupAt,
    DateTimeOffset GeneratedAt);


public sealed record WebLibraryScanRequest(string Trigger = "manual");

public sealed record WebLibraryScanSnapshot(
    bool IsRunning,
    bool Started,
    string Trigger,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string Message,
    int FilesFound,
    int Added,
    int Updated,
    int Unchanged,
    int Errors,
    int CanonicalBroadcastsAdded,
    int CanonicalRecordingsAdded,
    int CanonicalEpisodesMapped,
    int CanonicalItemsNeedingReview);

public sealed record WebResearchWorkspaceOverview(
    int TotalResearchRecords,
    int AttachedRecords,
    int ConfirmedMissing,
    int ProbableMissing,
    int UnknownGaps,
    int NeedsReview,
    int ConflictedRecords,
    int PendingDecisions,
    int AutomaticDecisions,
    int ManualApprovals);

public sealed record WebResearchWorkspaceRecord(
    long Id,
    long? EpisodeId,
    string BroadcastId,
    string Show,
    DateTime? BroadcastDate,
    string Slot,
    int PartNumber,
    int? TotalParts,
    string Headline,
    string Summary,
    string ResearchState,
    string ExistenceStatus,
    int Confidence,
    string ConfidenceReason,
    bool NeedsReview,
    int ConflictCount,
    int PendingDecisionCount,
    int SourceCount,
    int PeopleCount,
    int TopicCount,
    int MomentCount,
    DateTime UpdatedAt);

public sealed record WebResearchWorkspaceImport(
    long Id, string PackageName, string PackageHash, int SchemaVersion, string AppVersion,
    DateTime ImportedAt, int ImportedCount, int MatchedCount, int MissingCount, int ConflictCount,
    int FieldsApplied, int FieldsMerged, int FieldsPreserved, int ManualFieldsProtected,
    int ChangeCount, string Status, bool RollbackDataCaptured, int RestoredChangeCount,
    int BlockedRollbackCount, DateTime? LastRollbackAt);

public sealed record WebResearchWorkspaceSourceSummary(
    string Publisher, string SourceType, string Domain, int SourceCount, int BroadcastCount,
    int AverageConfidence, DateTime? LatestAccessedAt);

public sealed record WebResearchWorkspaceSource(
    string Url, string Title, string Publisher, string SourceType, int Confidence,
    string Accessed, IReadOnlyList<string> Supports, string Notes);

public sealed record WebResearchWorkspaceMoment(long PositionMs, string Title, string Notes);
public sealed record WebResearchWorkspaceConflict(
    long Id, string FieldName, string ExistingValue, string IncomingValue, string Resolution, DateTime CreatedAt);

public sealed record WebResearchWorkspaceRecordDetails(
    WebResearchWorkspaceRecord Record,
    string Station,
    string Variant,
    string Era,
    string EpisodeType,
    string ArchiveNotes,
    IReadOnlyList<string> Hosts,
    IReadOnlyList<string> Guests,
    IReadOnlyList<string> Callers,
    IReadOnlyList<string> MentionedPeople,
    IReadOnlyList<string> Topics,
    IReadOnlyList<WebResearchWorkspaceSource> Sources,
    IReadOnlyList<WebResearchWorkspaceMoment> Moments,
    IReadOnlyList<WebResearchWorkspaceConflict> Conflicts);

public sealed record WebResearchWorkspaceSnapshot(
    WebResearchWorkspaceOverview Overview,
    IReadOnlyList<WebResearchWorkspaceRecord> Records,
    IReadOnlyList<WebResearchWorkspaceImport> Imports,
    IReadOnlyList<WebResearchWorkspaceSourceSummary> Sources,
    DateTimeOffset GeneratedAt);


public sealed record WebUndatedBroadcast(
    long EpisodeId,
    int CollectionId,
    string ShowName,
    string Title,
    string DateConfidence,
    string PreferredFilename,
    string PreferredPath,
    int FileCount,
    DateTime? ProposedDate,
    string ParserEvidence,
    string ParserWarnings,
    DateTimeOffset UpdatedAt);

public sealed record WebResearchCoverageDay(
    DateTime Date,
    bool IsWeekend,
    bool HasAudio,
    bool HasResearch,
    bool IsKnownMissing,
    int BroadcastCount,
    int MetadataScore,
    string MissingFields,
    long? RepresentativeEpisodeId,
    long? ResearchId);

public sealed record WebResearchCoverageShow(
    int CollectionId,
    string ShowName,
    DateTime FirstDate,
    DateTime LastDate,
    IReadOnlyList<WebResearchCoverageDay> Days);

public sealed record WebAssignBroadcastDateRequest(DateTime BroadcastDate);
public sealed record WebAssignBroadcastDateResult(long EpisodeId, DateTime BroadcastDate, bool Updated);
