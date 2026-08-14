namespace TheRadioVault.Services.Models;

public static class MediaConsolidationDisposition
{
    public const string ManagedCopy = "ManagedCopy";
    public const string RejectedDuplicate = "RejectedDuplicate";
    public const string RejectedAlternate = "RejectedAlternate";
}

public sealed record MediaConsolidationPlanItem(
    string ItemId,
    long MediaFileId,
    long EpisodeId,
    string CanonicalBroadcastKey,
    string RecordingKey,
    int SegmentNumber,
    int? SegmentTotal,
    string ShowName,
    DateOnly AirDate,
    string BroadcastSlot,
    string Title,
    string SourcePath,
    long SourceBytes,
    long DurationMs,
    long EstimatedBitrate,
    string FullSha256,
    string Disposition,
    string ManagedPath,
    string QuarantinePath,
    string SelectionReason)
{
    public bool IsManagedCopy => Disposition == MediaConsolidationDisposition.ManagedCopy;
    public bool IsRejected => !IsManagedCopy;
}

public sealed record MediaConsolidationPlan(
    string PlanId,
    DateTimeOffset CreatedAt,
    long LibraryTruthRunId,
    string ManagedRoot,
    string QuarantineRoot,
    string PlanSignature,
    IReadOnlyList<MediaConsolidationPlanItem> Items,
    int EligibleBroadcasts,
    int HeldBroadcasts,
    long ManagedBytes,
    long QuarantinedOriginalBytes,
    IReadOnlyList<string> Warnings)
{
    public int ManagedFiles => Items.Count(item => item.IsManagedCopy);
    public int RejectedFiles => Items.Count(item => item.IsRejected);
    public string ConfirmationText => $"CONSOLIDATE {PlanId}";
}

public sealed record MediaConsolidationRehearsalResult(
    string PlanId,
    string PlanSignature,
    bool CanCommit,
    int VerifiedSourceFiles,
    long VerifiedSourceBytes,
    long RequiredManagedBytes,
    long RequiredQuarantineBytes,
    long ManagedFreeBytes,
    long QuarantineFreeBytes,
    string ManifestPath,
    string Message,
    IReadOnlyList<string> Problems);

public sealed record MediaConsolidationCommitResult(
    string PlanId,
    bool Completed,
    int ManagedFiles,
    int QuarantinedFiles,
    int DatabaseRowsUpdated,
    string ManagedRoot,
    string QuarantineDirectory,
    string DatabaseBackupPath,
    string JournalPath,
    string Message);

public sealed record MediaConsolidationProgress(
    string Stage,
    int Completed,
    int Total,
    string CurrentFile,
    string Message)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Completed * 100d / Total, 0, 100);
}
