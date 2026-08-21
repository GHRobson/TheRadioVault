namespace TheRadioVault.Services.Models;

/// <summary>
/// A deliberately narrow, read-only export used to recover authoritative dates
/// without copying the full Knowledge database or any playback/client state.
/// </summary>
public sealed class ResearchDateAuthorityEvidenceReport
{
    public int SchemaVersion { get; set; } = 1;
    public string AppVersion { get; set; } = string.Empty;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public long ReconciliationRunId { get; set; }
    public string PrivacyNotice { get; set; } =
        "Contains archive filenames, paths and Research metadata. It excludes credentials, pairing state, private RSS configuration, playback history and transcripts.";
    public ResearchDateAuthorityEvidenceSummary Summary { get; set; } = ResearchDateAuthorityEvidenceSummary.Empty;
    public IReadOnlyList<UnresolvedDateBroadcastEvidence> UnresolvedBroadcasts { get; set; } = Array.Empty<UnresolvedDateBroadcastEvidence>();
    public IReadOnlyList<ResearchDateRecordEvidence> ResearchRecords { get; set; } = Array.Empty<ResearchDateRecordEvidence>();
    public IReadOnlyList<ResearchDateProvenanceEvidence> Provenance { get; set; } = Array.Empty<ResearchDateProvenanceEvidence>();
    public IReadOnlyList<ResearchDateImportChangeEvidence> ImportHistory { get; set; } = Array.Empty<ResearchDateImportChangeEvidence>();
    public IReadOnlyList<LegacyMissingDateEvidence> LegacyMissingResearch { get; set; } = Array.Empty<LegacyMissingDateEvidence>();
    public IReadOnlyList<LegacyMissingDateRevisionEvidence> LegacyMissingResearchRevisions { get; set; } = Array.Empty<LegacyMissingDateRevisionEvidence>();
}

public sealed record ResearchDateAuthorityEvidenceSummary(
    int UnresolvedBroadcasts,
    int CurrentEpisodes,
    int PhysicalFiles,
    int LinkedResearchRecords,
    int OrphanResearchRecords,
    int StructuredResearchDates,
    int ProvenanceEntries,
    int ImportHistoryEntries,
    int LegacyMissingRecords,
    int LegacyRevisionRecords)
{
    public static ResearchDateAuthorityEvidenceSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

public sealed record UnresolvedDateBroadcastEvidence(
    string CanonicalKey,
    string CollectionName,
    string BroadcastSlot,
    string AdoptionReason,
    IReadOnlyList<UnresolvedDateEpisodeEvidence> Episodes);

public sealed record UnresolvedDateEpisodeEvidence(
    long EpisodeId,
    int CollectionId,
    string CollectionName,
    string BroadcastUid,
    string CurrentAirDate,
    string DateConfidence,
    string Title,
    int PartNumber,
    int? TotalParts,
    IReadOnlyList<string> Filenames,
    IReadOnlyList<string> Paths);

public sealed record ResearchDateRecordEvidence(
    long ResearchId,
    long? EpisodeId,
    int CollectionId,
    string CollectionName,
    long? LegacyMissingResearchId,
    string IdentityKey,
    string SourceBroadcastId,
    string StructuredAirDate,
    string Slot,
    int PartNumber,
    int? TotalParts,
    string Headline,
    int Confidence,
    bool NeedsReview,
    string ResearchState,
    string ExistenceStatus,
    string ResearchJson);

public sealed record ResearchDateProvenanceEvidence(
    long Id,
    long? ResearchId,
    long? EpisodeId,
    string FieldName,
    string Value,
    string SourceKind,
    string SourceLabel,
    long? ImportRunId,
    int Confidence,
    bool Protected,
    bool Active,
    string CreatedAt,
    string SupersededAt);

public sealed record ResearchDateImportChangeEvidence(
    long Id,
    long ImportRunId,
    long? ResearchId,
    long? EpisodeId,
    string PackageName,
    string ImportedAt,
    string RecordIdentity,
    string FieldName,
    string BeforeValue,
    string AfterValue,
    string Decision,
    string Reason,
    string CreatedAt);

public sealed record LegacyMissingDateEvidence(
    long Id,
    string StableKey,
    string BroadcastUid,
    string ShowName,
    string BroadcastDate,
    string Slot,
    int PartNumber,
    int? TotalParts,
    string Headline,
    int Confidence,
    string Status,
    long? MatchedEpisodeId,
    string MatchNotes,
    string ResearchJson,
    string UpdatedAt);

public sealed record LegacyMissingDateRevisionEvidence(
    long Id,
    long MissingResearchId,
    string Status,
    long? MatchedEpisodeId,
    string MatchNotes,
    string ResearchJson,
    string SavedAt);
