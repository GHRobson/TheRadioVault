namespace TheRadioVault.Services.Models;

public sealed class LibraryTruthAdoptionCommitBoundaryException : InvalidOperationException
{
    public LibraryTruthAdoptionCommitBoundaryException(
        string message,
        string backupPath,
        Exception? innerException = null,
        bool restoreRequired = true)
        : base(message, innerException)
    {
        BackupPath = backupPath ?? string.Empty;
        RestoreRequired = restoreRequired;
    }

    public string BackupPath { get; }
    public bool RestoreRequired { get; }
}

public sealed record LibraryTruthAdoptionEligibility(
    bool CanAdopt,
    string Reason,
    long TruthRunId,
    long RehearsalRunId,
    int EligibleBroadcasts,
    int CanonicalWrites,
    int RecordingWrites,
    int SegmentWrites,
    int CoverageWrites,
    int FileReassignments,
    int AliasRowsRetired,
    int StateRowsMigrated,
    int MetadataConflicts,
    int AutoResolvedConflicts,
    int UnresolvedConflicts,
    int PreservedAlternates,
    int TranscriptConflicts,
    string ExpectedSourceFingerprint,
    string CurrentSourceFingerprint,
    string ExpectedTruthRunSignature,
    string ExpectedItemSignature,
    string ExpectedConflictSignature,
    string RehearsalCompletedDisplay,
    string ExistingAdoptionStatus)
{
    public static LibraryTruthAdoptionEligibility Blocked(string reason) => new(
        false, reason, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

    public string PlanDisplay =>
        $"{EligibleBroadcasts:N0} broadcasts · {RecordingWrites:N0} recordings · {CoverageWrites:N0} coverage rows · " +
        $"{FileReassignments:N0} file reassignments · {AliasRowsRetired:N0} aliases · {StateRowsMigrated:N0} state rows";

    public string ConflictDisplay =>
        $"{AutoResolvedConflicts:N0} deterministic resolutions · {UnresolvedConflicts:N0} retained for review · {PreservedAlternates:N0} preserved alternates";
}

public sealed record LibraryTruthAdoptionRunSummary(
    long Id,
    long TruthRunId,
    long RehearsalRunId,
    string AppVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Status,
    string BackupPath,
    string SourceFingerprint,
    string StagedFingerprint,
    string PostCommitFingerprint,
    string RehearsalTruthSignature,
    string CommitTruthSignature,
    string RehearsalItemSignature,
    string CommitItemSignature,
    string RehearsalConflictSignature,
    string CommitConflictSignature,
    int EligibleBroadcasts,
    int CanonicalWrites,
    int RecordingWrites,
    int SegmentWrites,
    int CoverageWrites,
    int FileReassignments,
    int AliasRowsRetired,
    int StateRowsMigrated,
    int MetadataConflicts,
    int AutoResolvedConflicts,
    int UnresolvedConflicts,
    int PreservedAlternates,
    int TranscriptConflicts,
    int ForeignKeyViolations,
    string IntegrityCheck,
    string BackupRestoreCheck,
    bool CommitVerified,
    string Message)
{
    public static LibraryTruthAdoptionRunSummary Empty { get; } = new(
        0, 0, 0, string.Empty, DateTimeOffset.MinValue, null, "not-run", string.Empty,
        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, string.Empty, string.Empty, false,
        "No live Library Truth adoption has been run.");

    public string CompletedDisplay => CompletedAt?.ToLocalTime().ToString("g") ?? "Not completed";
    public string VerificationDisplay => CommitVerified ? "Verified" : "Not verified";
    public string SummaryDisplay =>
        $"{EligibleBroadcasts:N0} committed · {FileReassignments:N0} file links · {AliasRowsRetired:N0} aliases · " +
        $"{AutoResolvedConflicts:N0} policy-resolved · {UnresolvedConflicts:N0} retained for review · commit {VerificationDisplay.ToLowerInvariant()}";
}
