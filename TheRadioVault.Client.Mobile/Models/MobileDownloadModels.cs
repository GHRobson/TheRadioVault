using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile.Models;

public sealed record MobileDownloadPart(
    int PartNumber,
    int? PartTotal,
    long LogicalStartMs,
    long LogicalEndMs,
    long MediaFileId,
    long SizeBytes,
    string FileName,
    string MediaType);

public sealed record MobileDownloadRecord(
    WebClientLibraryBroadcastSummary Summary,
    string CanonicalKey,
    string RecordingKey,
    long DurationMs,
    DateTimeOffset DownloadedAt,
    string StorageDirectory,
    IReadOnlyList<MobileDownloadPart> Parts)
{
    public long EpisodeId => Summary.RepresentativeEpisodeId;
    public long SizeBytes => Parts.Sum(part => Math.Max(0, part.SizeBytes));
}

public sealed class MobileDownloadIndex
{
    public List<MobileDownloadRecord> Downloads { get; init; } = [];
}

public sealed record MobileDownloadProgress(
    long EpisodeId,
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

public sealed record MobileDownloadStorage(
    int DownloadCount,
    long CompletedBytes,
    long PendingBytes)
{
    public long TotalBytes => Math.Max(0, CompletedBytes) + Math.Max(0, PendingBytes);
}
