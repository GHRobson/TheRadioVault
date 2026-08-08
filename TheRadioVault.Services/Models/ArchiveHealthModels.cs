namespace TheRadioVault.Services.Models;

public enum ArchiveHealthArea
{
    Collection,
    Metadata,
    Research,
    Preservation
}

public enum ArchiveHealthSeverity
{
    Information,
    Suggestion,
    Warning,
    Error
}

public sealed record ArchiveHealthIssue(
    ArchiveHealthArea Area,
    ArchiveHealthSeverity Severity,
    long? BroadcastId,
    long? MediaFileId,
    string CollectionName,
    DateOnly? AirDate,
    string DisplayTitle,
    string Detail,
    string SuggestedAction,
    string? Path = null,
    long? ResearchBroadcastId = null)
{
    public string AirDateDisplay => AirDate?.ToString("d MMM yyyy") ?? "Unknown date";
    public string SeverityDisplay => Severity switch
    {
        ArchiveHealthSeverity.Error => "Critical",
        ArchiveHealthSeverity.Warning => "Warning",
        ArchiveHealthSeverity.Suggestion => "Suggestion",
        _ => "Information"
    };
}

public sealed record ArchiveHealthReport(
    int HealthScore,
    int CollectionScore,
    int MetadataScore,
    int ResearchScore,
    int PreservationScore,
    int TotalBroadcasts,
    int RegisteredFolders,
    int MissingFiles,
    int CloudOnlyFiles,
    int DuplicateCandidates,
    int NeedsReview,
    int MissingArtwork,
    int GenericTitles,
    int UnfingerprintedFiles,
    int NeverScannedFolders,
    int TotalResearchRecords,
    int ConfirmedMissingBroadcasts,
    int ProbableMissingBroadcasts,
    int UnknownResearchGaps,
    int ResearchNeedsReview,
    int ResearchConflicts,
    int UnsourcedResearchRecords,
    int LowConfidenceResearchRecords,
    int PendingReconciliationCandidates,
    DateTime? LastCompletedScanAt,
    IReadOnlyList<ArchiveHealthIssue> Issues)
{
    /// <summary>
    /// Remote clients receive the server's compact health summary rather than
    /// every diagnostic row. This preserves the authoritative actionable count
    /// without fabricating thousands of placeholder issues in the client cache.
    /// </summary>
    public int? ActionableIssueOverride { get; init; }

    public int CollectionIssues => Actionable(ArchiveHealthArea.Collection);
    public int MetadataIssues => Actionable(ArchiveHealthArea.Metadata);
    public int ResearchIssues => Actionable(ArchiveHealthArea.Research);
    public int PreservationIssues => Actionable(ArchiveHealthArea.Preservation);

    // Compatibility aliases retained for diagnostic readers created before the health refactor.
    public int CollectionQualityScore => MetadataScore;
    public int StorageIssues => CollectionIssues;
    public int BroadcastIssues => PreservationIssues;
    public int SynchronisationIssues => PreservationIssues;

    public int CriticalIssues => Issues.Count(x => x.Severity == ArchiveHealthSeverity.Error);
    public int WarningIssues => Issues.Count(x => x.Severity == ArchiveHealthSeverity.Warning);
    public int SuggestionIssues => Issues.Count(x => x.Severity == ArchiveHealthSeverity.Suggestion);
    public int InformationIssues => Issues.Count(x => x.Severity == ArchiveHealthSeverity.Information);
    public int ActionableIssues => ActionableIssueOverride ?? (CriticalIssues + WarningIssues + SuggestionIssues);

    public string HealthLabel => ScoreLabel(HealthScore);
    public string CollectionLabel => ScoreLabel(CollectionScore);
    public string MetadataLabel => ScoreLabel(MetadataScore);
    public string ResearchLabel => TotalResearchRecords == 0 ? "Not assessed" : ScoreLabel(ResearchScore);
    public string PreservationLabel => ScoreLabel(PreservationScore);

    public string CollectionQualityLabel => MetadataLabel;

    public string ScoreBreakdown =>
        $"Collection {CollectionScore}% · Metadata {MetadataScore}% · Research {(TotalResearchRecords == 0 ? "not assessed" : ResearchScore + "%")} · Preservation {PreservationScore}%";

    public string LastCompletedScanDisplay => LastCompletedScanAt.HasValue
        ? $"Last complete scan {LastCompletedScanAt.Value.ToLocalTime():dd MMM yyyy HH:mm}"
        : "No completed scan recorded";

    private int Actionable(ArchiveHealthArea area)
        => Issues.Count(x => x.Area == area && x.Severity != ArchiveHealthSeverity.Information);

    private static string ScoreLabel(int score) => score switch
    {
        >= 95 => "Excellent",
        >= 85 => "Healthy",
        >= 70 => "Needs attention",
        _ => "Action recommended"
    };
}
