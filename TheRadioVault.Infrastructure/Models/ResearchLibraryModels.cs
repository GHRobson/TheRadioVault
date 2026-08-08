using System;
using System.Collections.Generic;

namespace TheRadioVault.Models;

public enum ResearchBroadcastState
{
    InLibrary,
    MissingRecording,
    PartiallyResearched,
    FullyResearched,
    ConflictingInformation,
    AlternateCapture,
    EncoreOrReplay,
    SpecialEdition
}

public enum BroadcastExistenceStatus
{
    InLibrary,
    ConfirmedMissing,
    ProbableMissing,
    UnknownGap
}

public enum ResearchPersonRole
{
    Host,
    Guest,
    Caller,
    Mentioned
}

public enum ResearchSourceType
{
    Official,
    ArchiveIndex,
    Community,
    ListeningThread,
    MediaFile,
    User,
    Inference,
    Other
}

public sealed record ResearchBroadcastIdentity(
    int CollectionId,
    DateOnly? AirDate,
    string Slot,
    int PartNumber,
    string CaptureKey)
{
    public string ToStableKey()
    {
        var date = AirDate?.ToString("yyyy-MM-dd") ?? "unknown-date";
        var slot = NormaliseToken(Slot, "default");
        var capture = NormaliseToken(CaptureKey, "primary");
        return $"{CollectionId}:{date}:{slot}:{Math.Max(1, PartNumber)}:{capture}";
    }

    private static string NormaliseToken(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return value.Trim().ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-");
    }
}

public sealed class ResearchBroadcastRecord
{
    public long Id { get; init; }
    public required ResearchBroadcastIdentity Identity { get; init; }
    public string SourceBroadcastId { get; init; } = string.Empty;
    public long? EpisodeId { get; set; }

    public string Headline { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Station { get; set; } = string.Empty;
    public string Edition { get; set; } = string.Empty;
    public string BroadcastVariant { get; set; } = string.Empty;
    public string BroadcastEra { get; set; } = string.Empty;
    public string EpisodeType { get; set; } = string.Empty;
    public string ArchiveNotes { get; set; } = string.Empty;

    public ResearchBroadcastState State { get; set; } = ResearchBroadcastState.PartiallyResearched;
    public BroadcastExistenceStatus ExistenceStatus { get; set; } = BroadcastExistenceStatus.UnknownGap;
    public int Confidence { get; set; }
    public string ConfidenceReason { get; set; } = string.Empty;
    public bool UserModified { get; set; }
    public bool NeedsReview { get; set; }

    public List<ResearchSourceRecord> Sources { get; } = new();
    public List<ResearchPersonRecord> People { get; } = new();
    public List<ResearchTopicRecord> Topics { get; } = new();
    public List<ResearchMomentRecord> Moments { get; } = new();
    public List<ResearchAliasRecord> Aliases { get; } = new();
}

public sealed record ResearchSourceRecord(
    string Url,
    string Title,
    ResearchSourceType SourceType,
    int Confidence,
    string Notes,
    DateTimeOffset? AccessedAt = null);

public sealed record ResearchPersonRecord(
    string Name,
    ResearchPersonRole Role,
    int Confidence,
    string Notes = "",
    long? SourceId = null);

public sealed record ResearchTopicRecord(
    string Topic,
    int Confidence,
    string Notes = "",
    long? SourceId = null);

public sealed record ResearchMomentRecord(
    int TimestampSeconds,
    string Title,
    string Description,
    int Confidence,
    long? SourceId = null);

public sealed record ResearchAliasRecord(
    string AliasType,
    string AliasValue,
    int Confidence);

public sealed class EpisodeResearchSnapshot
{
    public long EpisodeId { get; init; }
    public int CollectionId { get; init; }
    public string BroadcastUid { get; init; } = string.Empty;
    public DateOnly? AirDate { get; init; }
    public string Slot { get; init; } = string.Empty;
    public int PartNumber { get; init; } = 1;
    public string OriginalFilename { get; init; } = string.Empty;

    public bool UserModified { get; init; }
    public string Headline { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<string> Hosts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Guests { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Callers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MentionedPeople { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Topics { get; init; } = Array.Empty<string>();
}

public sealed record ResearchMatchCandidate(
    long ResearchBroadcastId,
    long EpisodeId,
    int Score,
    string Reason,
    bool StrongMatch);

public sealed class ResearchLibraryOverview
{
    public int TotalResearchRecords { get; set; }
    public int AttachedRecords { get; set; }
    public int ConfirmedMissing { get; set; }
    public int ProbableMissing { get; set; }
    public int UnknownGaps { get; set; }
    public int NeedsReview { get; set; }
    public int ConflictedRecords { get; set; }
    public int MissingRecords => ConfirmedMissing + ProbableMissing + UnknownGaps;
}

public sealed class ResearchConflictRecord
{
    public long Id { get; set; }
    public long ResearchBroadcastId { get; set; }
    public long? EpisodeId { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string ExistingValue { get; set; } = string.Empty;
    public string IncomingValue { get; set; } = string.Empty;
    public string Resolution { get; set; } = "unresolved";
    public DateTime CreatedAt { get; set; }
    public string FieldDisplay => string.Join(" ", FieldName.Split('_', StringSplitOptions.RemoveEmptyEntries)
        .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
}

public sealed class ResearchConflictTriageItem
{
    public long Id { get; set; }
    public long ResearchBroadcastId { get; set; }
    public long? EpisodeId { get; set; }
    public string Show { get; set; } = string.Empty;
    public DateTime? BroadcastDate { get; set; }
    public string Slot { get; set; } = string.Empty;
    public int PartNumber { get; set; } = 1;
    public string Headline { get; set; } = string.Empty;
    public string FieldName { get; set; } = string.Empty;
    public string ExistingValue { get; set; } = string.Empty;
    public string IncomingValue { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public int SourceCount { get; set; }
    public int Confidence { get; set; }

    public string FieldDisplay => string.Join(" ", FieldName.Split('_', StringSplitOptions.RemoveEmptyEntries)
        .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    public string HeadlineDisplay => string.IsNullOrWhiteSpace(Headline) ? "Untitled researched broadcast" : Headline;
    public string IdentityDisplay
    {
        get
        {
            var bits = new List<string> { BroadcastDate?.ToString("dd MMM yyyy") ?? "Date unknown" };
            if (!string.IsNullOrWhiteSpace(Slot)) bits.Add(Slot);
            if (PartNumber > 1) bits.Add($"Part {PartNumber}");
            return string.Join(" · ", bits);
        }
    }
    public string EvidenceDisplay => $"{SourceCount:N0} source{(SourceCount == 1 ? "" : "s")} · {Confidence}% confidence";
}

public sealed class ResearchLibraryBrowseRecord
{
    public long Id { get; set; }
    public long? EpisodeId { get; set; }
    public long? LegacyMissingResearchId { get; set; }
    public string BroadcastId { get; set; } = string.Empty;
    public string Show { get; set; } = string.Empty;
    public DateTime? BroadcastDate { get; set; }
    public string Slot { get; set; } = string.Empty;
    public int PartNumber { get; set; } = 1;
    public int? TotalParts { get; set; }
    public string Headline { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string ResearchState { get; set; } = "partially_researched";
    public string ExistenceStatus { get; set; } = "unknown_gap";
    public int Confidence { get; set; }
    public string ConfidenceReason { get; set; } = string.Empty;
    public bool NeedsReview { get; set; }
    public int ConflictCount { get; set; }
    public int PendingDecisionCount { get; set; }
    public int SourceCount { get; set; }
    public int PeopleCount { get; set; }
    public int TopicCount { get; set; }
    public int MomentCount { get; set; }
    public DateTime UpdatedAt { get; set; }

    public bool HasAudio => EpisodeId.HasValue;
    public bool IsMissing => !EpisodeId.HasValue;
    public bool IsDiscoveryLead => IsMissing && !string.Equals(ExistenceStatus, "dismissed", StringComparison.OrdinalIgnoreCase);
    public string DiscoveryStatusDisplay => ExistenceStatus switch
    {
        "confirmed_missing" => "Confirmed broadcast",
        "probable_missing" => "Strong research lead",
        _ => "Broadcast lead"
    };
    public string BroadcastDateDisplay => BroadcastDate?.ToString("dd MMM yyyy") ?? "Date unknown";
    public string PartDisplay => TotalParts is > 1
        ? $"Part {PartNumber} of {TotalParts}"
        : PartNumber > 1 ? $"Part {PartNumber}" : string.Empty;
    public string IdentityDisplay
    {
        get
        {
            var bits = new List<string> { BroadcastDateDisplay };
            if (!string.IsNullOrWhiteSpace(Slot)) bits.Add(Slot);
            if (!string.IsNullOrWhiteSpace(PartDisplay)) bits.Add(PartDisplay);
            return string.Join(" · ", bits);
        }
    }
    public string HeadlineDisplay => string.IsNullOrWhiteSpace(Headline) ? "Untitled researched broadcast" : Headline;
    public string SummaryDisplay => string.IsNullOrWhiteSpace(Summary) ? "No summary has been added yet." : Summary;
    public string StatusDisplay
    {
        get
        {
            if (ConflictCount > 0) return "Metadata conflict";
            if (PendingDecisionCount > 0) return "Needs match decision";
            if (NeedsReview) return "Attention flag";
            if (EpisodeId.HasValue) return "In library";
            return ExistenceStatus switch
            {
                "confirmed_missing" => "Confirmed missing",
                "probable_missing" => "Probable missing",
                _ => "Unknown gap"
            };
        }
    }
    public string ConfidenceDisplay => Confidence > 0 ? $"{Confidence}% confidence" : "Confidence not rated";
    public string CoverageDisplay => $"{SourceCount} source{(SourceCount == 1 ? "" : "s")} · {PeopleCount} people · {TopicCount} topics · {MomentCount} moments";
    public string UpdatedDisplay => $"Updated {UpdatedAt.ToLocalTime():dd MMM yyyy HH:mm}";
}


public sealed class ResearchSourceDetailRecord
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string SourceType { get; set; } = "other";
    public int Confidence { get; set; }
    public string Accessed { get; set; } = string.Empty;
    public IReadOnlyList<string> Supports { get; set; } = Array.Empty<string>();
    public string Notes { get; set; } = string.Empty;
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title : !string.IsNullOrWhiteSpace(Publisher) ? Publisher : Url;
}

public sealed class ResearchLibraryRecordDetails
{
    public ResearchLibraryBrowseRecord Record { get; set; } = new();
    public TrvPackBroadcast Broadcast { get; set; } = new();
    public IReadOnlyList<string> Hosts { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Guests { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Callers { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> MentionedPeople { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Topics { get; set; } = Array.Empty<string>();
    public IReadOnlyList<TrvPackSource> Sources { get; set; } = Array.Empty<TrvPackSource>();
    public IReadOnlyList<ResearchSourceDetailRecord> SourceDetails { get; set; } = Array.Empty<ResearchSourceDetailRecord>();
    public IReadOnlyList<TrvPackMoment> Moments { get; set; } = Array.Empty<TrvPackMoment>();
    public IReadOnlyList<ResearchConflictRecord> Conflicts { get; set; } = Array.Empty<ResearchConflictRecord>();
}

public sealed class ResearchShowHealthRecord
{
    public int CollectionId { get; set; }
    public string Show { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Attached { get; set; }
    public int Missing { get; set; }
    public int NeedsReview { get; set; }
    public int Conflicts { get; set; }
    public int WithSummaries { get; set; }
    public int WithPeople { get; set; }
    public int WithTopics { get; set; }
    public int WithSources { get; set; }
    public int CoveragePercent => Total == 0 ? 0 : (int)Math.Round(100d * (WithSummaries + WithPeople + WithTopics + WithSources) / (Total * 4d));
    public int ArchivePercent => Total == 0 ? 0 : (int)Math.Round(100d * Attached / Total);
    public string PrimarySummary => $"{Attached:N0} in library · {Missing:N0} missing · {NeedsReview:N0} to review";
    public string CoverageSummary => $"Research coverage {CoveragePercent}% · Archive completion {ArchivePercent}%";
}

public sealed class ResearchImportRunRecord
{
    public long Id { get; set; }
    public string PackageName { get; set; } = string.Empty;
    public string PackageHash { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public string AppVersion { get; set; } = string.Empty;
    public DateTime ImportedAt { get; set; }
    public int ImportedCount { get; set; }
    public int MatchedCount { get; set; }
    public int MissingCount { get; set; }
    public int ConflictCount { get; set; }
    public int FieldsApplied { get; set; }
    public int FieldsMerged { get; set; }
    public int FieldsPreserved { get; set; }
    public int ManualFieldsProtected { get; set; }
    public int ChangeCount { get; set; }
    public string Status { get; set; } = "completed";
    public bool RollbackDataCaptured { get; set; }
    public int RestoredChangeCount { get; set; }
    public int BlockedRollbackCount { get; set; }
    public DateTime? LastRollbackAt { get; set; }
    public string DisplayName => string.IsNullOrWhiteSpace(PackageName) ? "Research pack import" : PackageName;
    public string ImportedDisplay => ImportedAt.ToLocalTime().ToString("dd MMM yyyy HH:mm");
    public string ResultDisplay => $"{ImportedCount:N0} records · {MatchedCount:N0} matched · {MissingCount:N0} missing · {ConflictCount:N0} conflicts";
    public string MergeDisplay => $"{FieldsApplied:N0} applied · {FieldsMerged:N0} merged · {FieldsPreserved:N0} preserved · {ManualFieldsProtected:N0} protected";
    public string StatusDisplay => Status switch
    {
        "rolled_back" => "Rolled back",
        "partially_rolled_back" => "Partially rolled back",
        "failed" => "Failed",
        "committing" => "Interrupted",
        _ => RollbackDataCaptured ? "Committed · rollback data captured" : "Committed"
    };
    public string TechnicalDisplay => $"Schema {SchemaVersion} · Radio Vault {AppVersion} · {ChangeCount:N0} field decisions";
    public string RollbackDisplay => RestoredChangeCount > 0
        ? $"{RestoredChangeCount:N0} decision{(RestoredChangeCount == 1 ? "" : "s")} restored" + (BlockedRollbackCount > 0 ? $" · {BlockedRollbackCount:N0} blocked" : string.Empty)
        : RollbackDataCaptured ? "Rollback available" : "No rollback data";
    public string HashDisplay => string.IsNullOrWhiteSpace(PackageHash) ? "No package fingerprint" : PackageHash;
    public bool CanRollback => RollbackDataCaptured && Status is not ("rolled_back" or "failed" or "committing");
}

public sealed class ResearchImportChangeRecord
{
    public long Id { get; set; }
    public long ImportRunId { get; set; }
    public long? ResearchBroadcastId { get; set; }
    public long? EpisodeId { get; set; }
    public string RecordIdentity { get; set; } = string.Empty;
    public string FieldName { get; set; } = string.Empty;
    public string BeforeValue { get; set; } = string.Empty;
    public string AfterValue { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string FieldDisplay => string.IsNullOrWhiteSpace(FieldName)
        ? "Record"
        : string.Join(" ", FieldName.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    public string DecisionDisplay => Decision switch
    {
        "applied" => "Applied",
        "merged" => "Merged",
        "preserved" => "Preserved",
        "protected" => "Protected",
        "retained_missing" => "Saved without audio",
        "ambiguous" => "Needs decision",
        "created" => "Created",
        _ => "Unchanged"
    };
    public string BeforeDisplay => FormatLedgerValue(BeforeValue, "Empty before import");
    public string AfterDisplay => FormatLedgerValue(AfterValue, "Empty after import");

    private static string FormatLedgerValue(string value, string emptyText)
    {
        if (string.IsNullOrWhiteSpace(value)) return emptyText;
        var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= 280 ? normalized : normalized[..277] + "…";
    }
}


public sealed class ResearchImportRollbackRunRecord
{
    public long Id { get; set; }
    public long ImportRunId { get; set; }
    public string Scope { get; set; } = "entire_import";
    public string RecordIdentity { get; set; } = string.Empty;
    public int RestoredCount { get; set; }
    public int BlockedCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public string ScopeDisplay => Scope == "record" && !string.IsNullOrWhiteSpace(RecordIdentity)
        ? RecordIdentity
        : "Entire import";
    public string CreatedDisplay => CreatedAt.ToLocalTime().ToString("dd MMM yyyy HH:mm");
    public string ResultDisplay => $"{RestoredCount:N0} restored · {BlockedCount:N0} blocked";
}

public sealed class ResearchImportRollbackPreview
{
    public long ImportRunId { get; set; }
    public string PackageName { get; set; } = string.Empty;
    public long? EpisodeId { get; set; }
    public long? ResearchBroadcastId { get; set; }
    public string TargetIdentity { get; set; } = string.Empty;
    public IReadOnlyList<ResearchImportRollbackItem> Items { get; set; } = Array.Empty<ResearchImportRollbackItem>();
    public int RestorableCount => Items.Count(x => x.Status == "safe");
    public int BlockedCount => Items.Count(x => x.Status == "blocked");
    public int AlreadyRestoredCount => Items.Count(x => x.Status == "already_reverted");
    public int PreservedCount => Items.Count(x => x.Status == "preserved");
    public bool CanApply => RestorableCount > 0;
    public bool IsSingleBroadcast => EpisodeId.HasValue || ResearchBroadcastId.HasValue;
    public string Heading => string.IsNullOrWhiteSpace(PackageName) ? "Research import" : PackageName;
    public string ScopeDisplay => IsSingleBroadcast && !string.IsNullOrWhiteSpace(TargetIdentity) ? TargetIdentity : "Entire import";
    public string SummaryDisplay => $"{RestorableCount:N0} restorable · {BlockedCount:N0} protected" +
                                    (AlreadyRestoredCount > 0 ? $" · {AlreadyRestoredCount:N0} already restored" : string.Empty) +
                                    (PreservedCount > 0 ? $" · {PreservedCount:N0} unchanged" : string.Empty);
}

public sealed class ResearchImportRollbackItem
{
    public long? ChangeId { get; set; }
    public long? ResearchBroadcastId { get; set; }
    public long? EpisodeId { get; set; }
    public string RecordIdentity { get; set; } = string.Empty;
    public string FieldName { get; set; } = string.Empty;
    public string BeforeValue { get; set; } = string.Empty;
    public string AfterValue { get; set; } = string.Empty;
    public string CurrentValue { get; set; } = string.Empty;
    public string Status { get; set; } = "blocked";
    public string Reason { get; set; } = string.Empty;
    public string FieldDisplay => string.IsNullOrWhiteSpace(FieldName)
        ? "Record"
        : string.Join(" ", FieldName.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    public string StatusDisplay => Status switch
    {
        "safe" => "Will restore",
        "already_reverted" => "Already restored",
        "preserved" => "No change",
        _ => "Blocked"
    };
    public string CurrentDisplay => Format(CurrentValue, "Currently empty");
    public string RestoreDisplay => Format(BeforeValue, "Restore to empty");

    private static string Format(string value, string empty)
    {
        if (string.IsNullOrWhiteSpace(value)) return empty;
        var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= 220 ? normalized : normalized[..217] + "…";
    }
}

public sealed class ResearchImportRollbackResult
{
    public long RollbackId { get; set; }
    public int Applied { get; set; }
    public int Blocked { get; set; }
    public int AlreadyRestored { get; set; }
    public int Preserved { get; set; }
    public bool Partial { get; set; }
    public string Summary => $"{Applied:N0} restored · {Blocked:N0} blocked" +
                             (AlreadyRestored > 0 ? $" · {AlreadyRestored:N0} already restored" : string.Empty) +
                             (Preserved > 0 ? $" · {Preserved:N0} preserved" : string.Empty);
}

public sealed class ResearchFieldProvenanceRecord
{
    public long Id { get; set; }
    public long? ResearchBroadcastId { get; set; }
    public long? EpisodeId { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string ValueText { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public string SourceLabel { get; set; } = string.Empty;
    public long? ImportRunId { get; set; }
    public int Confidence { get; set; }
    public int EvidenceCount { get; set; }
    public bool Protected { get; set; }
    public DateTime CreatedAt { get; set; }
    public string FieldDisplay => string.Join(" ", FieldName.Split('_', StringSplitOptions.RemoveEmptyEntries)
        .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    public string OriginDisplay => SourceKind switch
    {
        "manual" => "Manual edit",
        "rollback" => "Restored by rollback",
        "research_pack" => string.IsNullOrWhiteSpace(SourceLabel) ? "Research pack" : SourceLabel,
        _ => string.IsNullOrWhiteSpace(SourceLabel) ? "Existing archive metadata" : SourceLabel
    };
    public string EvidenceDisplay
    {
        get
        {
            var bits = new List<string>();
            if (Confidence > 0) bits.Add($"{Confidence}% confidence");
            if (EvidenceCount > 0) bits.Add($"{EvidenceCount} source{(EvidenceCount == 1 ? "" : "s")}");
            return bits.Count == 0 ? "No confidence or evidence count recorded" : string.Join(" · ", bits);
        }
    }
    public string ProtectionDisplay => Protected ? "Protected" : string.Empty;
    public string DateDisplay => CreatedAt.ToLocalTime().ToString("dd MMM yyyy HH:mm");
    public string DetailDisplay
    {
        get
        {
            var bits = new List<string> { DateDisplay };
            if (Confidence > 0) bits.Add($"{Confidence}% confidence");
            if (EvidenceCount > 0) bits.Add($"{EvidenceCount} source{(EvidenceCount == 1 ? "" : "s")}");
            if (Protected) bits.Add("protected");
            return string.Join(" · ", bits);
        }
    }
}

public sealed class ResearchSourceSummaryRecord
{
    public string Publisher { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public int SourceCount { get; set; }
    public int BroadcastCount { get; set; }
    public int AverageConfidence { get; set; }
    public DateTime? LatestAccessedAt { get; set; }
    public string DisplayName => !string.IsNullOrWhiteSpace(Publisher) ? Publisher : !string.IsNullOrWhiteSpace(Domain) ? Domain : "Unlabelled source";
    public string TypeDisplay => SourceType.Replace('_', ' ');
    public string UsageDisplay => $"{SourceCount:N0} source entries across {BroadcastCount:N0} broadcasts";
    public string ConfidenceDisplay => AverageConfidence > 0 ? $"Average confidence {AverageConfidence}%" : "Confidence not rated";
    public string LatestDisplay => LatestAccessedAt.HasValue ? $"Latest access {LatestAccessedAt.Value.ToLocalTime():dd MMM yyyy}" : "No access date recorded";
}

public sealed class ResearchReconciliationCandidateRecord
{
    public long Id { get; set; }
    public long ResearchBroadcastId { get; set; }
    public long EpisodeId { get; set; }
    public long? ExistingResearchEpisodeId { get; set; }
    public int Score { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string Show { get; set; } = string.Empty;
    public DateTime? BroadcastDate { get; set; }
    public string Slot { get; set; } = string.Empty;
    public int PartNumber { get; set; } = 1;
    public string ResearchHeadline { get; set; } = string.Empty;
    public string ResearchSummary { get; set; } = string.Empty;
    public string ResearchStation { get; set; } = string.Empty;
    public string ExistenceStatus { get; set; } = string.Empty;
    public int ResearchConfidence { get; set; }
    public string EpisodeHeadline { get; set; } = string.Empty;
    public string EpisodeSummary { get; set; } = string.Empty;
    public string EpisodeStation { get; set; } = string.Empty;
    public string OriginalFilename { get; set; } = string.Empty;
    public bool EpisodeUserModified { get; set; }
    public int PeopleCount { get; set; }
    public int TopicCount { get; set; }
    public int MomentCount { get; set; }
    public int SourceCount { get; set; }
    public bool CanUndo { get; set; }
    public bool ExistingEpisodeAvailable { get; set; }
    public bool RequiresReview { get; set; } = true;
    public string ReviewCategory { get; set; } = "ambiguous_match";
    public string RecommendedAction { get; set; } = string.Empty;
    public string DecisionSource { get; set; } = "manual";
    public string ResearchEdition { get; set; } = string.Empty;
    public string ResearchVariant { get; set; } = string.Empty;
    public string ResearchEra { get; set; } = string.Empty;
    public string ResearchEpisodeType { get; set; } = string.Empty;
    public string ResearchArchiveNotes { get; set; } = string.Empty;
    public string EpisodeSlot { get; set; } = string.Empty;
    public int EpisodePartNumber { get; set; } = 1;
    public int? EpisodeTotalParts { get; set; }
    public long EpisodeDurationMs { get; set; }
    public string ResearchSourceBroadcastId { get; set; } = string.Empty;
    public string EpisodeBroadcastUid { get; set; } = string.Empty;
    public DateTime? EpisodeBroadcastDate { get; set; }

    public bool IsAlternateCapture => ExistingResearchEpisodeId.HasValue
        && ExistingResearchEpisodeId.Value != EpisodeId
        && ExistingEpisodeAvailable;
    public bool IsPreviouslyMissing => !ExistingResearchEpisodeId.HasValue && ExistenceStatus is "confirmed_missing" or "probable_missing" or "unknown_gap";
    public string BroadcastDateDisplay => BroadcastDate?.ToString("dd MMM yyyy") ?? "Date unknown";
    public string IdentityDisplay => string.Join(" · ", new[]
    {
        BroadcastDateDisplay,
        string.IsNullOrWhiteSpace(Slot) ? null : Slot,
        PartNumber > 1 ? $"Part {PartNumber}" : null
    }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string EpisodeIdentityDisplay => string.Join(" · ", new[]
    {
        EpisodeBroadcastDate?.ToString("dd MMM yyyy") ?? BroadcastDateDisplay,
        string.IsNullOrWhiteSpace(EpisodeSlot) ? "Regular/unspecified slot" : EpisodeSlot,
        EpisodeTotalParts is > 1 ? $"Part {EpisodePartNumber} of {EpisodeTotalParts}" :
            EpisodePartNumber > 1 ? $"Part {EpisodePartNumber}" : null,
        EpisodeDurationMs > 0 ? TimeSpan.FromMilliseconds(EpisodeDurationMs).ToString(@"h\:mm\:ss") : null
    }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string ScoreDisplay => $"{Score}% match";
    public string MatchTypeDisplay => ReviewCategory switch
    {
        "research_already_attached" => "No action needed",
        "exact_identity" => "Exact broadcast identity",
        "choose_broadcast" => "Choose the correct broadcast",
        "slot_ambiguity" => "Broadcast slot is unclear",
        "multipart_timeline" => "Multipart timeline needs confirmation",
        "same_broadcast_family" => "Same logical broadcast",
        "identity_only" => "Identity-only record",
        "alternate_capture" => "Possible additional capture",
        "low_confidence" => "Possible match",
        _ => IsAlternateCapture ? "Possible alternate capture" : IsPreviouslyMissing ? "Previously missing broadcast" : "Saved research match"
    };
    public bool HasMeaningfulResearchPayload =>
        !string.IsNullOrWhiteSpace(ResearchHeadline)
        || !string.IsNullOrWhiteSpace(ResearchSummary)
        || !string.IsNullOrWhiteSpace(ResearchStation)
        || !string.IsNullOrWhiteSpace(ResearchEdition)
        || !string.IsNullOrWhiteSpace(ResearchVariant)
        || !string.IsNullOrWhiteSpace(ResearchEra)
        || !string.IsNullOrWhiteSpace(ResearchEpisodeType)
        || !string.IsNullOrWhiteSpace(ResearchArchiveNotes)
        || PeopleCount > 0 || TopicCount > 0 || MomentCount > 0;
    public bool HasTimedMoments => MomentCount > 0;
    public string ResearchHeadlineDisplay => string.IsNullOrWhiteSpace(ResearchHeadline)
        ? HasMeaningfulResearchPayload ? "Saved broadcast research" : "Archive identity record"
        : ResearchHeadline;
    public string EpisodeHeadlineDisplay => string.IsNullOrWhiteSpace(EpisodeHeadline) ? "No library headline" : EpisodeHeadline;
    public string CoverageDisplay => $"{SourceCount} sources · {PeopleCount} people · {TopicCount} topics · {MomentCount} moments";
    public string DetectedDisplay => $"Detected {UpdatedAt.ToLocalTime():dd MMM yyyy HH:mm}";
    public string DecisionDisplay => DecisionSource.Equals("automatic", StringComparison.OrdinalIgnoreCase) ? "Handled automatically" : Status switch
    {
        "approved" => "Approved manually",
        "rejected" => "Dismissed",
        _ => "Needs your decision"
    };
}

public sealed class ResearchReconciliationGroupRecord
{
    public long ResearchBroadcastId { get; set; }
    public IReadOnlyList<ResearchReconciliationCandidateRecord> Candidates { get; set; } = Array.Empty<ResearchReconciliationCandidateRecord>();
    public ResearchReconciliationCandidateRecord Primary => Candidates.First();
    public int CandidateCount => Candidates.Count;
    public string Show => Primary.Show;
    public DateTime? BroadcastDate => Primary.BroadcastDate;
    public string Slot => Primary.Slot;
    public int PartNumber => Primary.PartNumber;
    public string ResearchHeadlineDisplay => Primary.ResearchHeadlineDisplay;
    public string IdentityDisplay => Primary.IdentityDisplay;
    public string CoverageDisplay => Primary.CoverageDisplay;
    public string Status => Primary.Status;
    public string DecisionSource => Primary.DecisionSource;
    public string ReviewCategory => Primary.ReviewCategory;
    public string RecommendedAction => Primary.RecommendedAction;
    public bool RequiresReview => Candidates.Any(x => x.RequiresReview && x.Status == "pending");
    public int BestScore => Candidates.Max(x => x.Score);
    public string ScoreDisplay => CandidateCount == 1 ? $"{BestScore}% match" : $"{CandidateCount} possible broadcasts";
    public string ProblemDisplay => ReviewCategory switch
    {
        "choose_broadcast" => "Radio Vault found more than one plausible broadcast.",
        "slot_ambiguity" => "The research does not identify which same-day slot it belongs to.",
        "multipart_timeline" => "This research contains timed Moments and the archive has more than one multipart or alternate capture.",
        "same_broadcast_family" => "These files belong to the same logical broadcast and were handled as one family.",
        "identity_only" => "This record contains no research payload, so it does not need a manual decision.",
        "alternate_capture" => "Research is already linked, but another recording may represent a separate capture.",
        "low_confidence" => "The available evidence is not strong enough for an automatic decision.",
        "exact_identity" => "Radio Vault found one exact identity match.",
        "research_already_attached" => "The research is already linked to an available broadcast.",
        _ => Primary.Reason
    };
}

public sealed class ResearchReconciliationOverview
{
    public int NeedsDecision { get; set; }
    public int AutomaticDecisions { get; set; }
    public int ManualApprovals { get; set; }
    public int Dismissed { get; set; }
    public int PendingCandidateRows { get; set; }
    public int CompletedDecisions { get; set; }
    public DateTime? LatestAutomaticDecisionAt { get; set; }
    public string LatestAutomaticDisplay => LatestAutomaticDecisionAt.HasValue
        ? $"Latest automatic decision {LatestAutomaticDecisionAt.Value.ToLocalTime():dd MMM yyyy HH:mm}"
        : "No automatic decisions recorded yet";
}

public sealed class ResearchReconciliationTriageResult
{
    public int AutomaticallyApplied { get; set; }
    public int AutomaticallyDismissed { get; set; }
    public int GroupsNeedingDecision { get; set; }
    public int CandidateRowsNeedingDecision { get; set; }
    public string Summary => $"{AutomaticallyApplied:N0} applied automatically · {AutomaticallyDismissed:N0} unnecessary suggestions cleared · {GroupsNeedingDecision:N0} decision{(GroupsNeedingDecision == 1 ? "" : "s")} remain";
}

public sealed class ResearchReconciliationCandidateDetails
{
    public ResearchReconciliationCandidateRecord Candidate { get; set; } = new();
    public ResearchLibraryRecordDetails Research { get; set; } = new();
    public EpisodeMetadata Episode { get; set; } = new();
}

public sealed class ResearchReconciliationApplyOptions
{
    public bool ApplyHeadline { get; set; }
    public bool ApplySummary { get; set; }
    public bool ApplyBroadcastDetails { get; set; }
    public bool MergePeople { get; set; } = true;
    public bool MergeTopics { get; set; } = true;
    public bool CopyMoments { get; set; } = true;
    public bool CreateAlternateCapture { get; set; }
    public string DecisionSource { get; set; } = "manual";

    public string AppliedFieldsDisplay
    {
        get
        {
            var fields = new List<string>();
            if (ApplyHeadline) fields.Add("headline");
            if (ApplySummary) fields.Add("summary");
            if (ApplyBroadcastDetails) fields.Add("broadcast details");
            if (MergePeople) fields.Add("people");
            if (MergeTopics) fields.Add("topics");
            if (CopyMoments) fields.Add("Moments");
            return fields.Count == 0 ? "research link only" : string.Join(", ", fields);
        }
    }
}

public sealed class ResearchReconciliationApplyResult
{
    public bool Applied { get; set; }
    public bool CreatedAlternateCapture { get; set; }
    public int PeopleAdded { get; set; }
    public int TopicsAdded { get; set; }
    public int MomentsAdded { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public sealed class ResearchReconciliationUndoResult
{
    public bool Undone { get; set; }
    public bool Partial { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public enum ResearchQualitySeverity
{
    Info,
    Warning,
    Error
}

public sealed class ResearchQualityFinding
{
    public long ResearchBroadcastId { get; set; }
    public long? EpisodeId { get; set; }
    public string Show { get; set; } = string.Empty;
    public DateTime? BroadcastDate { get; set; }
    public string RuleId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public ResearchQualitySeverity Severity { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
    public string SuggestedAction { get; set; } = string.Empty;
    public bool SafeFixAvailable { get; set; }
    public string SafeFixKind { get; set; } = string.Empty;
    public string SafeFixValue { get; set; } = string.Empty;
    public string DirectDecisionKind { get; set; } = string.Empty;
    public string DirectDecisionSubject { get; set; } = string.Empty;
    public IReadOnlyList<string> DirectDecisionOptions { get; set; } = Array.Empty<string>();
    public string DirectDecisionFingerprint { get; set; } = string.Empty;

    public bool SupportsDirectDecision => !string.IsNullOrWhiteSpace(DirectDecisionKind);
    public string DecisionKey => $"{ResearchBroadcastId}|{RuleId}|{DirectDecisionFingerprint}";
    public string FixAvailabilityDisplay => SafeFixAvailable ? "Safe repair available" : SupportsDirectDecision ? "Quick decision available" : string.Empty;
    public string DateDisplay => BroadcastDate?.ToString("dd MMM yyyy") ?? "Date unknown";
    public string IdentityDisplay => $"{Show} · {DateDisplay}";
    public string SeverityDisplay => Severity.ToString();
    public bool IsDecisionActionable => EpisodeId.HasValue && !SafeFixAvailable && SupportsDirectDecision;
}

public sealed class ResearchQualityDecisionOption
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ShortcutDisplay { get; set; } = string.Empty;
    public bool IsRecommended { get; set; }
}

public sealed class ResearchQualityAuditResult
{
    public DateTime CompletedAt { get; set; } = DateTime.Now;
    public IReadOnlyList<ResearchQualityFinding> Findings { get; set; } = Array.Empty<ResearchQualityFinding>();
    public int ErrorCount => Findings.Count(x => x.Severity == ResearchQualitySeverity.Error);
    public int WarningCount => Findings.Count(x => x.Severity == ResearchQualitySeverity.Warning);
    public int InfoCount => Findings.Count(x => x.Severity == ResearchQualitySeverity.Info);
    public int AffectedBroadcasts => Findings.Select(x => x.ResearchBroadcastId).Distinct().Count();
    public int SafeFixCount => Findings.Count(x => x.SafeFixAvailable);
    public string SummaryDisplay => $"{Findings.Count:N0} findings across {AffectedBroadcasts:N0} broadcasts · {ErrorCount:N0} errors · {WarningCount:N0} warnings · {SafeFixCount:N0} safe repairs";
}

public sealed class ResearchQualityRepairPreview
{
    public bool CanApply { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Before { get; set; } = string.Empty;
    public string After { get; set; } = string.Empty;
    public string Warning { get; set; } = string.Empty;
}

public sealed class ResearchQualityRepairResult
{
    public bool Applied { get; set; }
    public long ActionId { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public sealed class ResearchQualityUndoResult
{
    public bool Undone { get; set; }
    public bool RefusedBecauseChanged { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public sealed class ResearchQualityActionRecord
{
    public long ActionId { get; set; }
    public long ResearchBroadcastId { get; set; }
    public long? EpisodeId { get; set; }
    public string RuleId { get; set; } = string.Empty;
    public string FixKind { get; set; } = string.Empty;
    public string Show { get; set; } = string.Empty;
    public DateTime? BroadcastDate { get; set; }
    public string Headline { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? UndoneAt { get; set; }
    public string StatusDisplay => UndoneAt.HasValue ? $"Undone {UndoneAt.Value:dd MMM HH:mm}" : "Applied";
    public string CreatedDisplay => CreatedAt.ToString("dd MMM yyyy · HH:mm");
    public string RepairDisplay => System.Text.RegularExpressions.Regex.Replace(FixKind.Replace('_', ' '), "(?<!^)([A-Z])", " $1");
    public string IdentityDisplay => string.IsNullOrWhiteSpace(Show)
        ? $"Research record {ResearchBroadcastId}"
        : $"{Show} · {(BroadcastDate?.ToString("dd MMM yyyy") ?? "Date unknown")}";
}
