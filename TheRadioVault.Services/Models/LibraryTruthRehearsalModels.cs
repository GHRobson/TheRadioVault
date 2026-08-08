namespace TheRadioVault.Services.Models;

public sealed record LibraryTruthRehearsalProgress(string Stage, int Completed, int Total, string Message)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Completed * 100d / Total, 0, 100);
}

public sealed record LibraryTruthRehearsalSummary(
    long Id,
    long TruthRunId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Status,
    string BackupPath,
    string SourceFingerprint,
    string RollbackFingerprint,
    string TruthRunSignature,
    string ItemSignature,
    string ConflictSignature,
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
    bool RollbackVerified,
    string Message)
{
    public static LibraryTruthRehearsalSummary Empty { get; } = new(
        0, 0, DateTimeOffset.MinValue, null, "not-run", string.Empty, string.Empty, string.Empty,
        string.Empty, string.Empty, string.Empty,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, string.Empty, string.Empty, false,
        "No adoption rehearsal has been run yet.");

    public string CompletedDisplay => CompletedAt?.ToLocalTime().ToString("g") ?? "Not completed";
    public string RollbackDisplay => RollbackVerified ? "Verified" : "Not verified";
    public string SummaryDisplay => $"{EligibleBroadcasts:N0} rehearsed · {FileReassignments:N0} file links · {AliasRowsRetired:N0} aliases · {AutoResolvedConflicts:N0} policy-resolved · {UnresolvedConflicts:N0} need review · rollback {RollbackDisplay.ToLowerInvariant()}";
}

public sealed record LibraryTruthRehearsalItem(
    long Id,
    long RehearsalRunId,
    string CanonicalKey,
    long SurvivorEpisodeId,
    string AliasEpisodeIds,
    int FilesReassigned,
    int StateRowsMigrated,
    int MetadataConflicts,
    int AutoResolvedConflicts,
    int UnresolvedConflicts,
    int PreservedAlternates,
    int TranscriptConflicts,
    string Outcome,
    string Evidence)
{
    public string BroadcastDisplay => CanonicalKey.Replace("|", " · ", StringComparison.Ordinal);
    public string SurvivorDisplay => $"Episode {SurvivorEpisodeId:N0}";
    public string OperationsDisplay => $"{FilesReassigned:N0} files · {StateRowsMigrated:N0} state rows";
    public string ConflictDisplay => $"{AutoResolvedConflicts:N0} auto · {UnresolvedConflicts:N0} review · {TranscriptConflicts:N0} transcript";
}

public sealed record LibraryTruthConflictForensic(
    long Id,
    long RehearsalRunId,
    string CanonicalKey,
    string FieldName,
    string ConflictKind,
    string Classification,
    long? SelectedEpisodeId,
    string SelectedValue,
    string CandidateValues,
    string Provenance,
    string Resolution,
    bool AutoResolved,
    bool RequiresReview,
    int ConfidenceScore,
    int PreservedAlternateCount,
    string Evidence)
{
    public string BroadcastDisplay => CanonicalKey.Replace("|", " · ", StringComparison.Ordinal);
    public string PolicyDisplay => RequiresReview ? "Needs review" : AutoResolved ? "Auto-resolved" : "Informational";
    public string SelectedEpisodeDisplay => SelectedEpisodeId.HasValue ? $"Episode {SelectedEpisodeId.Value:N0}" : "Canonical value";
    public string ConfidenceDisplay => $"{ConfidenceScore}%";
}
