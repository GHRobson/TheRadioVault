namespace TheRadioVault.Services.Models;

public sealed record CanonicalLibrarySummary(
    long LatestTruthRunId,
    int Broadcasts,
    int AdoptedBroadcasts,
    int NeedsAttentionBroadcasts,
    int ReviewRecommendedBroadcasts,
    int BlockedBroadcasts,
    int Recordings,
    int CoverageRows,
    int PhysicalFiles,
    bool AdoptionVerified)
{
    public bool IsCutoverReady =>
        AdoptionVerified &&
        LatestTruthRunId > 0 &&
        Broadcasts > 0 &&
        Broadcasts == AdoptedBroadcasts + NeedsAttentionBroadcasts;

    public static CanonicalLibrarySummary Unavailable { get; } =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, false);
}

public sealed record CanonicalLibraryEntry(
    string CanonicalKey,
    long RepresentativeEpisodeId,
    string BroadcastUid,
    int CollectionId,
    string CollectionName,
    DateOnly? AirDate,
    string BroadcastSlot,
    string Headline,
    string Description,
    string OriginalFilename,
    string Path,
    string StorageState,
    bool Favourite,
    string ListeningStatus,
    long PositionMs,
    long DurationMs,
    DateTimeOffset? LastPlayedAt,
    DateTimeOffset DateAdded,
    string Guests,
    string Tags,
    string? ArtworkPath,
    string Edition,
    int MetadataConfidence,
    string MetadataConfidenceReason,
    int RecordingCount,
    int SegmentCount,
    int PhysicalFileCount,
    bool NeedsAttention,
    string AttentionState,
    string AttentionReason,
    bool Adopted);

public sealed record CanonicalCollectionSummary(
    int CollectionId,
    string CollectionName,
    int BroadcastCount);

public sealed record CanonicalEpisodeResolution(
    long RequestedEpisodeId,
    string CanonicalKey,
    long RepresentativeEpisodeId,
    bool Adopted,
    bool IsRepresentative);

public sealed record CanonicalMediaSource(
    long MediaFileId,
    string Path,
    long DurationMs,
    string StorageState,
    bool IsMissing,
    bool IsPreferred);

public sealed record CanonicalRecordingSegment(
    int SegmentNumber,
    int? SegmentTotal,
    long LogicalStartMs,
    long LogicalEndMs,
    IReadOnlyList<CanonicalMediaSource> Sources)
{
    public IReadOnlyList<long> MediaFileIds => Sources.Select(x => x.MediaFileId).ToArray();
    public long LogicalDurationMs => Math.Max(0, LogicalEndMs - LogicalStartMs);
    public CanonicalMediaSource? PreferredSource => Sources
        .OrderBy(x => x.IsMissing)
        .ThenByDescending(x => x.IsPreferred)
        .ThenBy(x => x.MediaFileId)
        .FirstOrDefault();
}

public sealed record CanonicalPlaybackPlan(
    string CanonicalKey,
    string RecordingKey,
    string Label,
    long DurationMs,
    string Role,
    IReadOnlyList<CanonicalRecordingSegment> Segments)
{
    public bool IsMultipart => Segments.Count > 1;
}


public sealed record CanonicalRecordingOption(
    string CanonicalKey,
    string RecordingKey,
    string Label,
    string Role,
    long DurationMs,
    int SegmentCount,
    int PhysicalFileCount,
    bool IsPreferred,
    bool IsComplete,
    bool NeedsReview);

public sealed record CanonicalDownloadPart(
    int PartNumber,
    int? PartTotal,
    long LogicalStartMs,
    long LogicalEndMs,
    long MediaFileId,
    string Path,
    long SizeBytes,
    string StorageState);

public sealed record CanonicalDownloadManifest(
    string CanonicalKey,
    string RecordingKey,
    string Label,
    long DurationMs,
    IReadOnlyList<CanonicalDownloadPart> Parts)
{
    public bool IsMultipart => Parts.Count > 1;
    public long TotalSizeBytes => Parts.Sum(x => Math.Max(0, x.SizeBytes));
}




public sealed record CanonicalRecordingSelectionReason(
    string CanonicalKey,
    string RecordingKey,
    string Label,
    string Role,
    string ResolutionPath,
    bool IsAdopted,
    bool IsHeldFallback,
    bool IsComplete,
    int SegmentCount,
    int PhysicalFileCount,
    long DurationMs,
    string Explanation);

public sealed record CanonicalLibraryAuditSnapshot(
    long TruthRunId,
    int Broadcasts,
    int AdoptedBroadcasts,
    int HeldBroadcasts,
    int ReviewRecommendedBroadcasts,
    int BlockedBroadcasts,
    int Recordings,
    int MultipartRecordings,
    int IncompleteRecordings,
    int ReviewRequiredCoverageRows,
    int MissingPhysicalFiles,
    int CloudOnlyPhysicalFiles,
    int LegacyFallbackBroadcasts,
    int InvalidPreferredRecordingBroadcasts,
    int DuplicatePlayableIdentityGroups,
    int DuplicateCanonicalAliases,
    DateTimeOffset GeneratedAt)
{
    public bool IsClean =>
        InvalidPreferredRecordingBroadcasts == 0 &&
        DuplicatePlayableIdentityGroups == 0 &&
        DuplicateCanonicalAliases == 0 &&
        IncompleteRecordings == 0 &&
        ReviewRequiredCoverageRows == 0;
}

public sealed record CanonicalTimelineLocation(
    string CanonicalKey,
    string RecordingKey,
    long RepresentativeEpisodeId,
    long SourceEpisodeId,
    long MediaFileId,
    int SegmentNumber,
    long LogicalStartMs,
    long LogicalEndMs)
{
    public long ToLogicalPosition(long sourcePositionMs)
        => Math.Clamp(LogicalStartMs + Math.Max(0, sourcePositionMs), LogicalStartMs, Math.Max(LogicalStartMs, LogicalEndMs));
}
