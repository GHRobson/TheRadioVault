namespace TheRadioVault.Services.Models;

public sealed record WikiPackOperationProgress(
    double Percent,
    int Current,
    int Total,
    string Message);

public static class WikiPageTypes
{
    public static readonly IReadOnlyList<string> All =
        new[] { "Person", "Show", "Organisation", "Event", "Place", "Topic", "Custom" };

    public static string Normalize(string? value)
    {
        var match = All.FirstOrDefault(x => string.Equals(x, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? "Custom";
    }
}

public static class WikiPageStatuses
{
    public static readonly IReadOnlyList<string> All = new[] { "Draft", "Published", "Archived" };

    public static string Normalize(string? value)
    {
        var match = All.FirstOrDefault(x => string.Equals(x, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? "Draft";
    }
}

public sealed record WikiOverview(
    int PageCount,
    int PublishedCount,
    int DraftCount,
    int SourceCount,
    int CitationCount,
    int ImageCount,
    int TimelineEventCount,
    DateTimeOffset? LastUpdatedAt,
    DateTimeOffset? LastImportedAt);

public sealed record WikiBrowseQuery(
    string Search = "",
    string PageType = "",
    string Status = "",
    int Limit = 500);

public sealed record WikiPageSummary(
    Guid PageId,
    string Slug,
    string Title,
    string PageType,
    string Summary,
    string Status,
    int Revision,
    DateTimeOffset UpdatedAt,
    int CitationCount,
    int ImageCount,
    int TimelineEventCount)
{
    public string TypeAndStatus => $"{PageType} · {Status}";
    public string EvidenceSummary => $"{CitationCount:N0} sources · {ImageCount:N0} images · {TimelineEventCount:N0} events";
}

public sealed record WikiPageDraft(
    Guid? PageId,
    string Slug,
    string Title,
    string PageType,
    string Summary,
    string BodyMarkdown,
    string Status,
    int ExpectedRevision,
    string ChangeSummary,
    string Editor,
    IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<WikiCitationRecord>? Citations = null,
    IReadOnlyList<WikiImageDraft>? Images = null,
    IReadOnlyList<WikiTimelineEventRecord>? Timeline = null);

public sealed record WikiImageDraft(WikiPageImageLink Link, byte[]? Content = null);

public sealed record WikiPageSaveResult(Guid PageId, int Revision, DateTimeOffset UpdatedAt, bool Created);

public sealed record WikiImageContent(Guid ImageId, string MediaType, string FileName, byte[] Content);

public sealed record WikiRevisionRecord(
    Guid PageId,
    int Revision,
    string Slug,
    string Title,
    string PageType,
    string Summary,
    string BodyMarkdown,
    string Status,
    string ChangeSummary,
    string Author,
    long? ImportRunId,
    DateTimeOffset CreatedAt)
{
    public string DisplayText => $"Revision {Revision:N0} · {CreatedAt:dd MMM yyyy HH:mm} · {Author}";
}

public sealed record WikiRevisionRestoreRequest(Guid PageId, int Revision, int ExpectedCurrentRevision, string Editor);

public sealed record WikiCitationAuditIssue(Guid PageId, string PageTitle, string Severity, string Code, string Message);

public sealed record WikiCitationAuditReport(
    DateTimeOffset GeneratedAt,
    int TotalPages,
    int PagesWithCitations,
    int TotalCitations,
    int TotalSources,
    IReadOnlyList<WikiCitationAuditIssue> Issues)
{
    public int ErrorCount => Issues.Count(x => string.Equals(x.Severity, "Error", StringComparison.OrdinalIgnoreCase));
    public int WarningCount => Issues.Count(x => string.Equals(x.Severity, "Warning", StringComparison.OrdinalIgnoreCase));
    public string Summary => $"Audited {TotalPages:N0} pages and {TotalCitations:N0} citations: {ErrorCount:N0} errors and {WarningCount:N0} warnings.";
}

public sealed record WikiMissingLink(
    Guid PageId,
    string PageTitle,
    string Target,
    string Label);

public sealed record WikiDuplicatePageCandidate(
    WikiPageSummary First,
    WikiPageSummary Second,
    string Reason);

public sealed record WikiNavigationContext(
    IReadOnlyList<WikiPageSummary> RelatedPages,
    IReadOnlyList<WikiPageSummary> Backlinks,
    IReadOnlyList<WikiMissingLink> MissingLinks);

public sealed record WikiOnThisDayItem(
    WikiPageSummary Page,
    WikiTimelineEventRecord Event);

public sealed record WikiEraSummary(
    int StartYear,
    int EndYear,
    int EventCount,
    int PageCount)
{
    public string Label => $"{StartYear}s";
    public string Summary => $"{EventCount:N0} events across {PageCount:N0} pages";
}

public sealed record WikiDashboardHighlights(
    IReadOnlyList<WikiOnThisDayItem> OnThisDay,
    IReadOnlyList<WikiEraSummary> Eras);

public sealed record WikiTimelineShowSummary(
    WikiPageSummary Page,
    int EventCount,
    int? FirstYear,
    int? LastYear)
{
    public string DateRange => FirstYear is null ? "Undated" : FirstYear == LastYear ? FirstYear.ToString()! : $"{FirstYear}-{LastYear}";
    public string Summary => $"{EventCount:N0} events | {DateRange}";
}

public sealed record WikiQualityAuditReport(
    DateTimeOffset GeneratedAt,
    WikiCitationAuditReport Citations,
    IReadOnlyList<WikiPageSummary> OrphanPages,
    IReadOnlyList<WikiMissingLink> BrokenLinks,
    IReadOnlyList<WikiDuplicatePageCandidate> DuplicatePages)
{
    public int IssueCount => Citations.Issues.Count + OrphanPages.Count + BrokenLinks.Count + DuplicatePages.Count;
    public string Summary => $"{IssueCount:N0} Wiki quality issues: {Citations.Issues.Count:N0} citation, {BrokenLinks.Count:N0} broken-link, {DuplicatePages.Count:N0} duplicate and {OrphanPages.Count:N0} orphan-page issues.";
}

public sealed record TopicMergeSuggestion(
    string CanonicalName,
    IReadOnlyList<string> Variants,
    int Confidence,
    bool SafeToAutomate,
    int BroadcastReferences,
    int WikiPageCount,
    string Reason)
{
    public string VariantsText => string.Join(" | ", Variants);
    public string EvidenceText => $"{BroadcastReferences:N0} broadcast references | {WikiPageCount:N0} Wiki pages";
    public string ConfidenceText => $"{Confidence:N0}% {(SafeToAutomate ? "safe automatic match" : "review suggested")}";
}

public sealed record TopicMergeHistoryRecord(
    Guid MergeId,
    Guid TopicId,
    string CanonicalName,
    IReadOnlyList<string> Aliases,
    string Reason,
    int Confidence,
    bool Automatic,
    int AffectedResearchRows,
    int AffectedTagLinks,
    int ArchivedWikiPages,
    DateTimeOffset CreatedAt,
    string CreatedBy)
{
    public string Summary => $"{CanonicalName}: {Aliases.Count:N0} names consolidated across {AffectedResearchRows + AffectedTagLinks:N0} archive links";
}

public sealed record TopicCleanupReport(
    DateTimeOffset GeneratedAt,
    int DistinctRawTopics,
    int CanonicalTopics,
    IReadOnlyList<TopicMergeSuggestion> Suggestions,
    IReadOnlyList<TopicMergeHistoryRecord> RecentMerges)
{
    public int AutomaticCount => Suggestions.Count(x => x.SafeToAutomate);
    public int ReviewCount => Suggestions.Count - AutomaticCount;
    public string Summary => $"{DistinctRawTopics:N0} topic names: {AutomaticCount:N0} safe automatic groups and {ReviewCount:N0} suggested merges.";
}

public sealed record TopicMergeRequest(
    string CanonicalName,
    IReadOnlyList<string> Variants,
    int Confidence,
    string Reason,
    bool Automatic,
    string Editor);

public sealed record TopicMergeResult(
    Guid MergeId,
    Guid TopicId,
    string CanonicalName,
    int AliasesStored,
    int ResearchRowsChanged,
    int TagLinksChanged,
    int WikiPagesArchived,
    string Summary);

public sealed record TopicAutomaticCleanupResult(
    int GroupsMerged,
    int ResearchRowsChanged,
    int TagLinksChanged,
    int WikiPagesArchived,
    IReadOnlyList<TopicMergeResult> Merges)
{
    public string Summary => GroupsMerged == 0
        ? "Topic names are already safely normalised."
        : $"Automatically consolidated {GroupsMerged:N0} safe topic groups across {ResearchRowsChanged + TagLinksChanged:N0} archive links and {WikiPagesArchived:N0} Wiki pages.";
}

public sealed record WikiStarterPageCandidate(
    string PageType,
    string Title,
    string Slug,
    int ArchiveReferences,
    string Context,
    bool AlreadyExists);

public sealed record WikiStarterPagePreview(
    int ShowCount,
    int PersonCount,
    int TopicCount,
    int ExistingCount,
    IReadOnlyList<WikiStarterPageCandidate> Candidates)
{
    public int NewPageCount => Candidates.Count(x => !x.AlreadyExists);
    public string Summary => $"{NewPageCount:N0} starter pages are ready: {ShowCount:N0} shows, {PersonCount:N0} people and {TopicCount:N0} topics; {ExistingCount:N0} already exist.";
}

public sealed record WikiStarterGenerationResult(
    int CreatedPages,
    int PreservedPages,
    IReadOnlyList<Guid> CreatedPageIds,
    string Summary);

public sealed record WikiArchiveBrowseQuery(string Search = "", int Limit = 250);

public sealed record WikiArchiveBroadcastCandidate(
    long EpisodeId,
    long CollectionId,
    string CollectionName,
    string Title,
    DateOnly? AirDate,
    string BroadcastUid,
    long DurationMs)
{
    public string DisplayText => $"{CollectionName} · {(AirDate?.ToString("dd MMM yyyy") ?? "Undated")} · {Title}";
}

public sealed record WikiArchiveMomentCandidate(
    long MomentId,
    long EpisodeId,
    string CollectionName,
    string BroadcastTitle,
    string MomentTitle,
    long PositionMs,
    DateOnly? AirDate)
{
    public string DisplayText => $"{CollectionName} · {(AirDate?.ToString("dd MMM yyyy") ?? "Undated")} · {MomentTitle}";
}

public sealed record WikiArchiveLinkResults(
    IReadOnlyList<WikiArchiveBroadcastCandidate> Broadcasts,
    IReadOnlyList<WikiArchiveMomentCandidate> Moments);

public sealed record WikiRelationshipRecord(
    Guid RelationshipId,
    Guid FromPageId,
    Guid ToPageId,
    string RelationshipType,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    string DatePrecision,
    string Notes,
    int SortOrder);

public sealed record WikiSourceRecord(
    Guid SourceId,
    string SourceType,
    string Title,
    string Author,
    string Publisher,
    string Url,
    string ArchivedUrl,
    DateOnly? PublishedDate,
    string DatePrecision,
    DateTimeOffset? AccessedAt,
    long? EpisodeId,
    string BroadcastUid,
    long? StartMs,
    long? EndMs,
    long? TranscriptSegmentId,
    long? MomentId,
    string Locator,
    string Notes);

public sealed record WikiCitationRecord(
    Guid CitationId,
    Guid PageId,
    Guid SourceId,
    int Ordinal,
    string SectionAnchor,
    string QuotedText,
    string Note,
    WikiSourceRecord? Source = null)
{
    public string DisplayNumber => $"[{Ordinal}]";
    public bool HasQuotedText => !string.IsNullOrWhiteSpace(QuotedText);
    public bool HasArchiveAudio => Source?.EpisodeId is > 0;
    public string ArchiveAudioLabel => "Open linked broadcast information";
    public string DisplayText => Source is null ? Note : string.IsNullOrWhiteSpace(Source.Publisher)
        ? Source.Title
        : $"{Source.Title} — {Source.Publisher}";
}

public sealed record WikiImageRecord(
    Guid ImageId,
    string OriginalFileName,
    string MediaType,
    string Sha256,
    long ByteCount,
    string Caption,
    string AltText,
    string Creator,
    string CopyrightHolder,
    string Licence,
    Guid? SourceId,
    DateOnly? CapturedDate,
    DateOnly? RepresentativeFrom,
    DateOnly? RepresentativeTo,
    string DatePrecision,
    string DateNotes);

public sealed record WikiPageImageLink(
    Guid PageId,
    Guid ImageId,
    string Role,
    int SortOrder,
    WikiImageRecord? Image = null);

public sealed record WikiTimelineBroadcastLink(
    Guid EventId,
    long EpisodeId,
    long? MomentId,
    long? StartMs,
    long? EndMs,
    string Label,
    int SortOrder);

public sealed record WikiTimelineEventRecord(
    Guid EventId,
    Guid PageId,
    string Title,
    string Summary,
    string Category,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string DatePrecision,
    string DateDisplay,
    int Significance,
    int SortOrder,
    IReadOnlyList<Guid> SourceIds,
    IReadOnlyList<Guid> ImageIds,
    IReadOnlyList<WikiTimelineBroadcastLink> Broadcasts)
{
    public string YearText => StartDate?.Year.ToString() ?? DateDisplay;
    public string EvidenceSummary => $"{SourceIds.Count:N0} sources · {ImageIds.Count:N0} images · {Broadcasts.Count:N0} broadcasts";
}

public sealed record WikiPageDocument(
    Guid PageId,
    string Slug,
    string Title,
    string PageType,
    string Summary,
    string BodyMarkdown,
    string Status,
    int Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string CreatedBy,
    string LastEditor,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<WikiRelationshipRecord> Relationships,
    IReadOnlyList<WikiCitationRecord> Citations,
    IReadOnlyList<WikiPageImageLink> Images,
    IReadOnlyList<WikiTimelineEventRecord> Timeline);

public sealed record WikiAuthoringPackManifest(
    int SchemaVersion,
    string AppVersion,
    Guid PackageId,
    DateTimeOffset ExportedAt,
    string DatabaseIdentity,
    int PageCount,
    int SourceCount,
    int CitationCount,
    int ImageCount,
    int TimelineEventCount,
    IReadOnlyDictionary<string, string> FileSha256);

public sealed record WikiAuthoringPageRecord(
    Guid PageId,
    int BaseRevision,
    string Slug,
    string Title,
    string PageType,
    string Summary,
    string Status,
    string CreatedBy,
    string LastEditor,
    IReadOnlyList<string> Aliases);

public sealed record WikiAuthoringImageRecord(WikiImageRecord Image, string ArchivePath);

public sealed record WikiAuthoringSnapshot(
    WikiAuthoringPackManifest Manifest,
    IReadOnlyList<WikiAuthoringPageRecord> Pages,
    IReadOnlyDictionary<Guid, string> PageMarkdown,
    IReadOnlyList<WikiRelationshipRecord> Relationships,
    IReadOnlyList<WikiSourceRecord> Sources,
    IReadOnlyList<WikiCitationRecord> Citations,
    IReadOnlyList<WikiAuthoringImageRecord> Images,
    IReadOnlyDictionary<Guid, byte[]> ImageBytes,
    IReadOnlyList<WikiPageImageLink> PageImages,
    IReadOnlyList<WikiTimelineEventRecord> TimelineEvents,
    WikiArchiveContext? ArchiveContext = null);

public sealed record WikiArchiveContext(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<WikiArchiveShowContext> Shows,
    IReadOnlyList<WikiArchiveBroadcastContext> Broadcasts,
    int TranscriptCount,
    int TranscriptSegmentCount);

public sealed record WikiArchiveShowContext(
    long CollectionId,
    string Name,
    int BroadcastCount,
    DateOnly? FirstBroadcast,
    DateOnly? LastBroadcast);

public sealed record WikiArchiveBroadcastContext(
    long EpisodeId,
    long CollectionId,
    string Show,
    string Title,
    DateOnly? AirDate,
    string BroadcastUid,
    long DurationMs,
    bool HasTranscript,
    int TranscriptSegments,
    IReadOnlyList<string> People,
    IReadOnlyList<string> Topics);

public sealed record WikiPackPageChangePreview(
    Guid PageId,
    string Title,
    string PageType,
    string ChangeKind,
    int IncomingBaseRevision,
    int? CurrentRevision,
    string Detail)
{
    public bool IsProtected => string.Equals(ChangeKind, "Protected", StringComparison.OrdinalIgnoreCase);
}

public sealed record WikiPackPreview(
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
    IReadOnlyList<WikiPackPageChangePreview>? PageChanges = null);

public sealed record WikiPackImportResult(
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

public sealed record WikiPackExportPayload(byte[] Bytes, string FileName, int PageCount, int ImageCount);
