namespace TheRadioVault.Services.Models;

public sealed record LocalPlaybackSegment(
    int SegmentNumber,
    int? SegmentTotal,
    long LogicalStartMs,
    long LogicalEndMs,
    string MediaPath,
    long SourceDurationMs)
{
    public long LogicalDurationMs => Math.Max(0, LogicalEndMs - LogicalStartMs);
}

public sealed record LocalPlaybackDescriptor(
    string CanonicalKey,
    long RepresentativeEpisodeId,
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
    IReadOnlyList<LocalPlaybackSegment> Segments)
{
    public bool IsMultipart => Segments.Count > 1;
}

public sealed record LocalPlaybackSaveRequest(
    string CanonicalKey,
    long RepresentativeEpisodeId,
    long PositionMs,
    long DurationMs,
    bool Completed,
    double PlaybackSpeed,
    bool IncrementPlayCount,
    bool AllowCompletionReset = false,
    long ExpectedPlaybackGeneration = 0,
    bool ExplicitSeek = false);
