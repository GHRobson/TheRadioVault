namespace TheRadioVault.Services.Models;

/// <summary>
/// One locally persisted part of a canonical server recording. RelativePath is
/// rooted by the native download service and is never accepted as an absolute
/// path.
/// </summary>
public sealed record NativeDownloadPart(
    int PartNumber,
    int? PartTotal,
    long LogicalStartMs,
    long LogicalEndMs,
    long MediaFileId,
    long SizeBytes,
    string RelativePath,
    string MediaType);

/// <summary>
/// Durable metadata for one explicit, device-local native download. The record
/// is committed only after every canonical media part has reached local storage.
/// </summary>
public sealed record NativeDownloadRecord(
    long RepresentativeEpisodeId,
    string CanonicalKey,
    string RecordingKey,
    string BroadcastId,
    string Title,
    string CollectionName,
    DateOnly? AirDate,
    string? ArtworkPath,
    long ResumePositionMs,
    long DurationMs,
    double PlaybackSpeed,
    bool Completed,
    bool Favourite,
    DateTimeOffset DownloadedAt,
    long SizeBytes,
    IReadOnlyList<NativeDownloadPart> Parts,
    string RepairState = "",
    DateTimeOffset? LastAccessedAt = null)
{
    public bool IsMultipart => Parts.Count > 1;
    public bool NeedsRepair => !string.IsNullOrWhiteSpace(RepairState);
}

/// <summary>
/// Streaming progress for a foreground native download. TotalBytes can be zero
/// when an older server cannot provide a trustworthy size before transfer.
/// </summary>
public sealed record NativeDownloadProgress(
    long RepresentativeEpisodeId,
    string Title,
    int PartNumber,
    int PartCount,
    long BytesReceived,
    long TotalBytes)
{
    public int Percent => TotalBytes > 0
        ? Math.Clamp((int)Math.Round(BytesReceived * 100d / TotalBytes), 0, 100)
        : 0;
}

public sealed record NativeDownloadAuditResult(
    int Checked,
    int Healthy,
    int NeedsRepair,
    long StoredBytes);

public sealed record NativeDownloadMaintenancePolicy(
    bool DeleteCompleted,
    int ExpiryDays,
    long StorageLimitBytes,
    DateTimeOffset Now);

public sealed record NativeDownloadMaintenanceResult(
    int RemovedCompleted,
    int RemovedExpired,
    int RemovedForStorage,
    long BytesFreed)
{
    public int Removed => RemovedCompleted + RemovedExpired + RemovedForStorage;
}

public sealed class NativeDownloadPreferences
{
    public bool AutomaticDownloadsEnabled { get; set; }
    public DateTimeOffset AutomaticDownloadSince { get; set; } = DateTimeOffset.MinValue;
    public long AutomaticDownloadWatermarkEpisodeId { get; set; }
    public bool DeleteCompletedDownloads { get; set; }
    public int DownloadExpiryDays { get; set; }
    public long StorageLimitBytes { get; set; }
}
