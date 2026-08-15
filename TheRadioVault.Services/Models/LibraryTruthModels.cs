namespace TheRadioVault.Services.Models;

public sealed record LibraryTruthRunSummary(
    long RunId,
    string Status,
    string ParserVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int PhysicalFiles,
    int CurrentBroadcasts,
    int ProposedBroadcasts,
    int UnchangedFiles,
    int ChangedFiles,
    int RecoveredDates,
    int UnknownDates,
    int NeedsReview,
    int MergeGroups,
    int SplitGroups,
    int ExactDuplicateGroups,
    int StrongDuplicateGroups,
    int MultipartBroadcasts,
    string Message)
{
    public static LibraryTruthRunSummary Empty { get; } = new(
        0, "not-run", string.Empty, DateTimeOffset.MinValue, null,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        "No shadow-library analysis has been run yet.");

    public string CompletedDisplay => CompletedAt?.ToLocalTime().ToString("g") ?? "Not completed";
    public string ChangeDisplay => $"{ChangedFiles:N0} proposed file-level changes · {MergeGroups:N0} broadcast merge groups · {SplitGroups:N0} split groups";
}

public sealed record LibraryTruthRunResult(LibraryTruthRunSummary Summary);

public sealed record LibraryTruthDispositionSummary(
    int UnchangedFiles,
    int MetadataCorrectionFiles,
    int MultipartCorrectionFiles,
    int BroadcastSplitFiles,
    int BroadcastMergeFiles,
    int RecoveredDateFiles,
    int NeedsAttentionFiles,
    int OtherChangedFiles)
{
    public static LibraryTruthDispositionSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);
    public int InterpretedDifferentlyFiles => MetadataCorrectionFiles + MultipartCorrectionFiles +
                                              BroadcastSplitFiles + BroadcastMergeFiles + RecoveredDateFiles +
                                              NeedsAttentionFiles + OtherChangedFiles;
}

public sealed record LibraryTruthFileView(
    long Id,
    long MediaFileId,
    long CurrentEpisodeId,
    string Filename,
    string Path,
    string CurrentCollection,
    string CurrentDate,
    string CurrentSlot,
    string CurrentPart,
    string ProposedCollection,
    string ProposedDate,
    string ProposedSlot,
    string ProposedPart,
    string ProposedHeadline,
    string Disposition,
    string ChangeSummary,
    int ConfidenceScore,
    string Confidence,
    string CanonicalBroadcastKey,
    string RecordingKey,
    string Evidence,
    string Warnings)
{
    public string CurrentIdentityDisplay => $"{CurrentCollection} · {CurrentDate} · {CurrentSlot} · {CurrentPart}";
    public string ProposedIdentityDisplay => $"{ProposedCollection} · {ProposedDate} · {ProposedSlot} · {ProposedPart}";
    public string ConfidenceDisplay => $"{Confidence} · {ConfidenceScore}%";
    public string RecordingDisplay => string.IsNullOrWhiteSpace(RecordingKey) ? "Unassigned recording" : RecordingKey.Split('|').Last();
}

public sealed record LibraryTruthBroadcastView(
    long Id,
    string CanonicalKey,
    string CollectionName,
    string AirDate,
    string BroadcastSlot,
    int FileCount,
    int SegmentCount,
    int RecordingCount,
    int ExactDuplicateCount,
    int StrongDuplicateCount,
    int CurrentEpisodeCount,
    string Status,
    int ConfidenceScore,
    string Evidence,
    string AdoptionState,
    string AdoptionReason,
    string PreferredRecordingKey,
    bool SuspiciousMerge,
    double DurationSpreadRatio,
    int CrossIdentityConflictCount)
{
    public string IdentityDisplay => $"{CollectionName} · {AirDate} · {BroadcastSlot}";
    public string StructureDisplay => $"{FileCount:N0} files · {SegmentCount:N0} segments · {RecordingCount:N0} recording variants";
    public string AdoptionDisplay => string.IsNullOrWhiteSpace(AdoptionReason) ? AdoptionState : $"{AdoptionState} · {AdoptionReason}";
    public string DurationSpreadDisplay => DurationSpreadRatio <= 1.01 ? "Aligned durations" : $"{DurationSpreadRatio:0.00}× duration spread";
}

public sealed record LibraryTruthRecordingView(
    long Id,
    string CanonicalBroadcastKey,
    string RecordingKey,
    string Label,
    int FileCount,
    int SegmentCount,
    long DurationMs,
    string Relationship,
    int ConfidenceScore,
    string Evidence,
    string Role,
    int CompletenessScore,
    int PreferredScore,
    double DurationRatio,
    bool IsPreferredCandidate,
    string ReviewReason)
{
    public string DurationDisplay => DurationMs <= 0 ? "Unknown duration" : TimeSpan.FromMilliseconds(DurationMs).ToString(@"h\:mm\:ss");
    public string BroadcastIdentityDisplay => CanonicalBroadcastKey.Replace("|", " · ", StringComparison.Ordinal);
    public string StructureDisplay => $"{SegmentCount:N0} segment(s) · {FileCount:N0} physical file(s)";
    public string CompletenessDisplay => $"{Role} · {CompletenessScore}%";
    public string PreferenceDisplay => IsPreferredCandidate ? $"Preferred · {PreferredScore}%" : $"Score {PreferredScore}%";
    public string DurationRatioDisplay => DurationRatio <= 0 ? "Unknown coverage" : $"{DurationRatio:P0} of longest recording";
}



public sealed record LibraryTruthCoverageView(
    long Id,
    string SourceBroadcastKey,
    string RecordingKey,
    int SegmentNumber,
    int? SegmentTotal,
    string TargetBroadcastKey,
    string CoverageKind,
    long StartOffsetMs,
    long EndOffsetMs,
    int ConfidenceScore,
    bool RequiresReview,
    string MediaFileIds,
    string Evidence)
{
    public string SegmentDisplay => SegmentTotal.HasValue
        ? $"Segment {Math.Max(1, SegmentNumber)} of {SegmentTotal.Value}"
        : $"Segment {Math.Max(1, SegmentNumber)}";
    public string DurationDisplay => EndOffsetMs <= StartOffsetMs
        ? "Unknown duration"
        : TimeSpan.FromMilliseconds(EndOffsetMs - StartOffsetMs).ToString(@"h\:mm\:ss");
    public string SourceDisplay => SourceBroadcastKey.Replace("|", " · ", StringComparison.Ordinal);
    public string TargetDisplay => TargetBroadcastKey.Replace("|", " · ", StringComparison.Ordinal);
    public string ReviewDisplay => RequiresReview ? "Review before adoption" : "Direct evidence";
}

public sealed record LibraryTruthAdoptionPreviewView(
    long Id,
    string CanonicalKey,
    string AdoptionState,
    string PlannedAction,
    long? ProvisionalEpisodeId,
    int CurrentEpisodeCount,
    string CurrentEpisodeIds,
    int MediaFileCount,
    int RecordingCount,
    int CoverageCount,
    int RetireEpisodeCount,
    int ReassignFileCount,
    int PlannedWriteCount,
    bool EligibleForGuardedAdoption,
    string GuardReason,
    string Evidence)
{
    public string BroadcastDisplay => CanonicalKey.Replace("|", " · ", StringComparison.Ordinal);
    public string SurvivorDisplay => ProvisionalEpisodeId.HasValue
        ? $"Episode {ProvisionalEpisodeId.Value:N0} (preview only)"
        : "No provisional survivor";
    public string StructureDisplay => $"{MediaFileCount:N0} files · {RecordingCount:N0} recordings · {CoverageCount:N0} coverage rows";
    public string OperationsDisplay => $"{PlannedWriteCount:N0} planned writes · {RetireEpisodeCount:N0} live rows would consolidate";
    public string EligibilityDisplay => EligibleForGuardedAdoption ? "Prepared" : "Held";
}

public sealed record LibraryTruthAdoptionPlanSummary(
    int PreviewBroadcasts,
    int EligibleBroadcasts,
    int ReviewBroadcasts,
    int BlockedBroadcasts,
    int CanonicalBroadcastWrites,
    int RecordingWrites,
    int CoverageWrites,
    int FileLinkWrites,
    int LiveEpisodeRowsToConsolidate,
    int ProvisionalSurvivorSelections)
{
    public static LibraryTruthAdoptionPlanSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    public string SummaryDisplay => $"{EligibleBroadcasts:N0} prepared · {ReviewBroadcasts:N0} review held · {BlockedBroadcasts:N0} blocked";
}

public sealed record LibraryTruthAdoptionSummary(
    int ReadyBroadcasts,
    int ReadyWithRecordingChoice,
    int ReviewRecommendedBroadcasts,
    int BlockedBroadcasts,
    int PreferredRecordingCandidates,
    int PartialRecordings,
    int FragmentRecordings,
    int TruncatedRecordings,
    int SuspiciousMergeGroups,
    int CrossIdentityConflicts)
{
    public static LibraryTruthAdoptionSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    public int AdoptionReadyTotal => ReadyBroadcasts + ReadyWithRecordingChoice;
    public string SummaryDisplay => $"{AdoptionReadyTotal:N0} ready · {ReviewRecommendedBroadcasts:N0} review recommended · {BlockedBroadcasts:N0} blocked";
}

public sealed record LibraryTruthYearView(
    string Year,
    int PhysicalFiles,
    int CurrentBroadcasts,
    int ProposedBroadcasts,
    int MergeGroups,
    int SplitGroups,
    int ReadyBroadcasts,
    int ReviewRecommendedBroadcasts,
    int BlockedBroadcasts)
{
    public string DifferenceDisplay => ProposedBroadcasts == CurrentBroadcasts
        ? "No top-level change"
        : ProposedBroadcasts < CurrentBroadcasts
            ? $"{CurrentBroadcasts - ProposedBroadcasts:N0} live entries consolidate"
            : $"{ProposedBroadcasts - CurrentBroadcasts:N0} additional broadcasts";
    public string ReadinessDisplay => $"{ReadyBroadcasts:N0} ready · {ReviewRecommendedBroadcasts:N0} review · {BlockedBroadcasts:N0} blocked";
}

public sealed record LibraryTruthConflictView(
    long Id,
    string ConflictType,
    int EvidenceStrength,
    int FileCount,
    int IdentityCount,
    string Identities,
    string Evidence)
{
    public string StrengthDisplay => $"{EvidenceStrength}% evidence";
    public string StructureDisplay => $"{FileCount:N0} files · {IdentityCount:N0} conflicting broadcast identities";
}

public sealed class LibraryTruthExportReport
{
    public int SchemaVersion { get; set; } = 6;
    public string AppVersion { get; set; } = string.Empty;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public LibraryTruthRunSummary Summary { get; set; } = LibraryTruthRunSummary.Empty;
    public LibraryTruthAdoptionSummary Adoption { get; set; } = LibraryTruthAdoptionSummary.Empty;
    public IReadOnlyList<LibraryTruthFileView> Files { get; set; } = Array.Empty<LibraryTruthFileView>();
    public IReadOnlyList<LibraryTruthBroadcastView> Broadcasts { get; set; } = Array.Empty<LibraryTruthBroadcastView>();
    public IReadOnlyList<LibraryTruthRecordingView> Recordings { get; set; } = Array.Empty<LibraryTruthRecordingView>();
    public IReadOnlyList<LibraryTruthYearView> Years { get; set; } = Array.Empty<LibraryTruthYearView>();
    public IReadOnlyList<LibraryTruthConflictView> Conflicts { get; set; } = Array.Empty<LibraryTruthConflictView>();
    public LibraryTruthAdoptionPlanSummary AdoptionPlan { get; set; } = LibraryTruthAdoptionPlanSummary.Empty;
    public IReadOnlyList<LibraryTruthCoverageView> Coverages { get; set; } = Array.Empty<LibraryTruthCoverageView>();
    public IReadOnlyList<LibraryTruthAdoptionPreviewView> AdoptionPreviews { get; set; } = Array.Empty<LibraryTruthAdoptionPreviewView>();
    public LibraryTruthRehearsalSummary Rehearsal { get; set; } = LibraryTruthRehearsalSummary.Empty;
    public IReadOnlyList<LibraryTruthRehearsalItem> RehearsalItems { get; set; } = Array.Empty<LibraryTruthRehearsalItem>();
    public IReadOnlyList<LibraryTruthConflictForensic> ConflictForensics { get; set; } = Array.Empty<LibraryTruthConflictForensic>();
}
