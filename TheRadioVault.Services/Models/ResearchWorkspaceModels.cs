using System.Globalization;

namespace TheRadioVault.Services.Models;

public sealed record ResearchWorkspaceOverview(
    int TotalRecords,
    int InLibraryRecords,
    int MissingRecords,
    int NeedsReviewRecords,
    int ConflictRecords,
    int UnsourcedRecords,
    int WithSummaries,
    int WithPeople,
    int WithTopics,
    int WithSources,
    DateTimeOffset? LastImportAt,
    DateOnly? EarliestDate = null,
    DateOnly? LatestDate = null)
{
    public int CoveragePercent => TotalRecords <= 0
        ? 0
        : (int)Math.Round(100d * (WithSummaries + WithPeople + WithTopics + WithSources) / (TotalRecords * 4d));
    public string TotalText => TotalRecords.ToString("N0", CultureInfo.CurrentCulture);
    public string InLibraryText => InLibraryRecords.ToString("N0", CultureInfo.CurrentCulture);
    public string MissingText => MissingRecords.ToString("N0", CultureInfo.CurrentCulture);
    public string ReviewText => NeedsReviewRecords.ToString("N0", CultureInfo.CurrentCulture);
    public string CoverageText => $"{CoveragePercent}%";
    public string LastImportText => LastImportAt.HasValue
        ? $"Last import {LastImportAt.Value.ToLocalTime():dd MMM yyyy HH:mm}"
        : "No research imports recorded";
    public string DateRangeText => EarliestDate.HasValue && LatestDate.HasValue
        ? EarliestDate.Value.Year == LatestDate.Value.Year
            ? EarliestDate.Value.Year.ToString(CultureInfo.CurrentCulture)
            : $"{EarliestDate.Value.Year}–{LatestDate.Value.Year}"
        : "Dates still being established";
    public string KnowledgeStateText => TotalRecords <= 0
        ? "The knowledge database is ready for its first records."
        : $"{TotalRecords:N0} records across {DateRangeText}, with {CoveragePercent}% core knowledge coverage.";
}

public sealed record ResearchCollectionOption(int? CollectionId, string Name, int RecordCount)
{
    public string DisplayName => CollectionId.HasValue ? $"{Name} ({RecordCount:N0})" : Name;
}

public sealed record ResearchStatusOption(string Key, string Name);

public sealed record ResearchBrowseQuery(
    string? SearchText = null,
    int? CollectionId = null,
    string Status = "all",
    bool NeedsReviewOnly = false,
    int Limit = 1000);

public sealed record ResearchBrowseItem(
    long ResearchId,
    long? EpisodeId,
    int CollectionId,
    string ShowName,
    DateOnly? AirDate,
    string Slot,
    int PartNumber,
    int? TotalParts,
    string Headline,
    string Summary,
    string ResearchState,
    string ExistenceStatus,
    int Confidence,
    bool NeedsReview,
    int ConflictCount,
    int PendingDecisionCount,
    int SourceCount,
    int PeopleCount,
    int TopicCount,
    DateTimeOffset UpdatedAt)
{
    public bool HasAudio => EpisodeId.HasValue;
    public bool HasHeadline => !string.IsNullOrWhiteSpace(Headline);
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    public string HeadlineText => Headline.Trim();
    public string DateText => AirDate?.ToString("dd MMM yyyy", CultureInfo.CurrentCulture) ?? "Date unknown";
    public string IdentityText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Slot)) parts.Add(Slot.Trim());
            if (TotalParts is > 1) parts.Add($"Part {PartNumber} of {TotalParts}");
            else if (PartNumber > 1) parts.Add($"Part {PartNumber}");
            return string.Join(" · ", parts);
        }
    }
    public string StatusText
    {
        get
        {
            if (ConflictCount > 0) return "Metadata conflict";
            if (PendingDecisionCount > 0) return "Match decision";
            if (NeedsReview) return "Needs review";
            if (EpisodeId.HasValue) return "In library";
            return ExistenceStatus switch
            {
                "confirmed_missing" => "Confirmed missing",
                "probable_missing" => "Probable missing",
                _ => "Broadcast lead"
            };
        }
    }
    public string EvidenceText => $"{SourceCount:N0} sources · {PeopleCount:N0} people · {TopicCount:N0} topics";
    public string ConfidenceText => Confidence > 0 ? $"{Confidence}%" : "—";
}



public enum CatalogueDateReviewAction
{
    ApproveLibraryDate,
    KeepExisting,
    Ignore,
    KeepAsRecordingDate,
    KeepAsReleaseDate,
    LeaveUndated,
    Reopen
}

public sealed record CatalogueDateReviewItem(
    long ResearchId,
    long EpisodeId,
    int CollectionId,
    string ShowName,
    string Title,
    string OriginalFilename,
    string CandidateText,
    DateOnly? ProposedDate,
    string CandidateKind,
    string ReleaseDateText,
    string RecordingDateText,
    string Basis,
    string Provenance,
    int Confidence,
    int SourceCount,
    bool HasSameDayCollision,
    string DecisionStatus,
    DateOnly? CurrentLibraryDate,
    DateTimeOffset UpdatedAt)
{
    public bool IsPending => string.IsNullOrWhiteSpace(DecisionStatus)
        || DecisionStatus.Equals("pending", StringComparison.OrdinalIgnoreCase)
        || DecisionStatus.Equals("reopened", StringComparison.OrdinalIgnoreCase);
    public bool IsResolved => !IsPending;
    public bool IsIgnored => DecisionStatus.Equals("ignored", StringComparison.OrdinalIgnoreCase);
    public bool IsCompleted => IsResolved && !IsIgnored;
    public bool HasProposedDate => ProposedDate.HasValue;
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? OriginalFilename : Title.Trim();
    public string ProposedDateText => ProposedDate?.ToString("dd MMM yyyy", CultureInfo.CurrentCulture)
        ?? (!string.IsNullOrWhiteSpace(CandidateText) ? CandidateText : "No exact date");
    public string ConfidenceText => Confidence > 0 ? $"{Confidence}% confidence" : "Confidence not rated";
    public string EvidenceText => $"{CandidateKind} · {ConfidenceText} · {SourceCount:N0} source{(SourceCount == 1 ? string.Empty : "s")}";
    public string CollisionText => HasSameDayCollision ? "Another item in this show already uses this date" : string.Empty;
    public bool HasCurrentLibraryDate => CurrentLibraryDate.HasValue;
    public string CurrentLibraryDateText => CurrentLibraryDate?.ToString("dd MMM yyyy", CultureInfo.CurrentCulture) ?? "Undated";
    public bool WouldReplaceCurrentLibraryDate => CurrentLibraryDate.HasValue
        && ProposedDate.HasValue
        && CurrentLibraryDate.Value != ProposedDate.Value;
    public string CurrentDateWarningText => WouldReplaceCurrentLibraryDate
        ? $"Approving this candidate will replace the current Library date ({CurrentLibraryDateText})."
        : string.Empty;
    public string DecisionText => DecisionStatus switch
    {
        "approved_library_date" => "Approved as Library date",
        "kept_existing" => "Kept existing Library date",
        "ignored" => "Ignored",
        "recording_date_only" => "Kept as recording date",
        "release_date_only" => "Kept as release/archive date",
        "left_undated" => "Left undated",
        _ => "Needs your decision"
    };
}

public sealed record UndatedBroadcastItem(
    long EpisodeId,
    int CollectionId,
    string ShowName,
    string Title,
    string DateConfidence,
    string PreferredFilename,
    string PreferredPath,
    int FileCount,
    DateOnly? ProposedDate,
    string ParserEvidence,
    string ParserWarnings,
    DateTimeOffset UpdatedAt)
{
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title.Trim() : PreferredFilename;
    public string FileText => FileCount == 1 ? PreferredFilename : $"{PreferredFilename} · {FileCount:N0} files";
    public string ParserText => ProposedDate.HasValue
        ? $"Parser candidate: {ProposedDate.Value:dd MMM yyyy}"
        : "No reliable date candidate";
    public bool HasParserClues => ProposedDate.HasValue || !string.IsNullOrWhiteSpace(ParserEvidence) || !string.IsNullOrWhiteSpace(ParserWarnings);
    public string UpdatedText => UpdatedAt == DateTimeOffset.MinValue ? string.Empty : $"Updated {UpdatedAt.ToLocalTime():dd MMM yyyy HH:mm}";
}

public sealed record ResearchCoverageDay(
    DateOnly Date,
    bool IsWeekend,
    bool HasAudio,
    bool HasResearch,
    bool IsKnownMissing,
    int BroadcastCount,
    int MetadataScore,
    string MissingFields,
    long? RepresentativeEpisodeId,
    long? ResearchId)
{
    public bool IsGap => !IsWeekend && !HasAudio && !HasResearch && !IsKnownMissing;
    public bool IsEmptyWeekend => IsWeekend && !HasAudio && !HasResearch && !IsKnownMissing;
    public bool IsCritical => (HasAudio || HasResearch) && MetadataScore < 25;
    public bool IsSparse => (HasAudio || HasResearch) && MetadataScore is >= 25 and < 50;
    public bool IsPartial => (HasAudio || HasResearch) && MetadataScore is >= 50 and < 80;
    public bool IsComplete => (HasAudio || HasResearch) && MetadataScore >= 80;
    public string DateText => Date.ToString("ddd, dd MMM yyyy", CultureInfo.CurrentCulture);
    public string ToolTipText
    {
        get
        {
            if (IsKnownMissing) return $"{DateText}\nRecording known to be missing";
            if (IsGap) return $"{DateText}\nNo broadcast metadata";
            if (IsEmptyWeekend) return $"{DateText}\nNo scheduled metadata";
            var coverage = $"{MetadataScore}% metadata coverage";
            var missing = string.IsNullOrWhiteSpace(MissingFields) ? "Complete core metadata" : $"Missing: {MissingFields}";
            var audio = HasAudio ? "Audio in library" : "No linked audio";
            return $"{DateText}\n{coverage}\n{missing}\n{audio}";
        }
    }
}

public sealed record ResearchCoverageShow(
    int CollectionId,
    string ShowName,
    DateOnly FirstDate,
    DateOnly LastDate,
    IReadOnlyList<ResearchCoverageDay> Days)
{
    public int DatedBroadcastDays => Days.Count(x => x.HasAudio || x.HasResearch || x.IsKnownMissing);
    public int GapDays => Days.Count(x => x.IsGap);
    public int CriticalDays => Days.Count(x => x.IsCritical);
    public int CompleteDays => Days.Count(x => x.IsComplete);
    public int AverageMetadataScore
    {
        get
        {
            var covered = Days.Where(x => x.HasAudio || x.HasResearch).ToArray();
            return covered.Length == 0 ? 0 : (int)Math.Round(covered.Average(x => x.MetadataScore));
        }
    }
    public string RunText => $"{FirstDate:dd MMM yyyy} – {LastDate:dd MMM yyyy}";
    public string SummaryText => $"{DatedBroadcastDays:N0} dated broadcast days · {AverageMetadataScore}% average metadata · {GapDays:N0} weekday gaps";
}

public sealed record ResearchSourceItem(
    long Id,
    string Url,
    string Title,
    string Publisher,
    string SourceType,
    int Confidence,
    string Supports,
    string Notes,
    DateTimeOffset? AccessedAt)
{
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title)
        ? Title
        : !string.IsNullOrWhiteSpace(Publisher) ? Publisher : Url;
    public string SourceTypeText => string.Join(" ", SourceType.Split('_', StringSplitOptions.RemoveEmptyEntries)) switch
    {
        var value when value.Length == 0 => "Other",
        var value => char.ToUpperInvariant(value[0]) + value[1..]
    };
    public string DetailText
    {
        get
        {
            var parts = new List<string> { SourceTypeText };
            if (!string.IsNullOrWhiteSpace(Publisher)) parts.Add(Publisher);
            if (Confidence > 0) parts.Add($"{Confidence}% confidence");
            return string.Join(" · ", parts);
        }
    }
}

public sealed record ResearchRecordDetails(
    ResearchBrowseItem Record,
    string Station,
    string Edition,
    string BroadcastVariant,
    string BroadcastEra,
    string EpisodeType,
    string ArchiveNotes,
    string ConfidenceReason,
    string Hosts,
    string Guests,
    string Callers,
    string MentionedPeople,
    string Topics,
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
    string? ArtworkPath,
    string LibraryTitle,
    string LibraryDescription,
    IReadOnlyList<ResearchSourceItem> Sources,
    bool ArtworkEditingAllowed = true,
    bool AdvancedMetadataEditingAllowed = true,
    bool ReviewEditingAllowed = true)
{
    public bool HasArtwork => !string.IsNullOrWhiteSpace(ArtworkPath) && File.Exists(ArtworkPath);
    public bool CanEditArtwork => Record.EpisodeId.HasValue && ArtworkEditingAllowed;
    public bool CanEditAdvancedMetadata => AdvancedMetadataEditingAllowed;
    public bool CanEditReviewState => ReviewEditingAllowed;
}

public sealed record ResearchMetadataUpdate(
    long ResearchId,
    string Headline,
    string Summary,
    string Station,
    string Edition,
    string BroadcastVariant,
    string BroadcastEra,
    string EpisodeType,
    string ArchiveNotes,
    int Confidence,
    string ConfidenceReason,
    bool NeedsReview,
    string Hosts,
    string Guests,
    string Callers,
    string MentionedPeople,
    string Topics,
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
    string? ArtworkPath,
    bool IsRemoteOwned = false);

public sealed record ResearchSourceDiagnostic(
    string SourceType,
    int SourceCount,
    int RecordCount,
    int AverageConfidence,
    int MissingUrlCount,
    int StaleAccessCount)
{
    public string SourceTypeText => string.Join(" ", SourceType.Split('_', StringSplitOptions.RemoveEmptyEntries)) switch
    {
        var value when value.Length == 0 => "Other",
        var value => char.ToUpperInvariant(value[0]) + value[1..]
    };
    public string CountText => $"{SourceCount:N0} sources across {RecordCount:N0} records";
    public string QualityText => $"{AverageConfidence}% average confidence · {MissingUrlCount:N0} without URL · {StaleAccessCount:N0} not checked recently";
}

public sealed record ResearchImportRunSummary(
    long Id,
    string PackageName,
    string PackageHash,
    int SchemaVersion,
    string AppVersion,
    DateTimeOffset ImportedAt,
    int ImportedCount,
    int MatchedCount,
    int MissingCount,
    int ConflictCount,
    int AppliedCount,
    int MergedCount,
    int PreservedCount,
    int ProtectedCount,
    string Status,
    int RestoredChangeCount,
    int BlockedRollbackCount)
{
    public string DisplayName => string.IsNullOrWhiteSpace(PackageName) ? "Research pack import" : PackageName;
    public string ImportedText => ImportedAt.ToLocalTime().ToString("dd MMM yyyy HH:mm", CultureInfo.CurrentCulture);
    public string ResultText => $"{ImportedCount:N0} records · {MatchedCount:N0} matched · {MissingCount:N0} missing · {ConflictCount:N0} conflicts";
    public string DecisionText => $"{AppliedCount:N0} applied · {MergedCount:N0} merged · {PreservedCount:N0} preserved · {ProtectedCount:N0} protected";
    public string StatusText => Status switch
    {
        "rolled_back" => "Rolled back",
        "partially_rolled_back" => "Partially rolled back",
        "failed" => "Failed",
        "committing" => "Interrupted",
        _ => "Committed"
    };
    public string TechnicalText => $"Schema {SchemaVersion} · Radio Vault {AppVersion}";
}
