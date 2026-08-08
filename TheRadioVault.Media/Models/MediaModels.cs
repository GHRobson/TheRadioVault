namespace TheRadioVault.Media.Models;

public sealed record AudioMetadata(
    string? Title,
    string? Description,
    IReadOnlyList<string> Performers,
    IReadOnlyList<string> Genres,
    long DurationMs,
    byte[]? ArtworkBytes,
    string? ArtworkMimeType,
    uint Year,
    string? Album,
    IReadOnlyList<string> AlbumArtists);

public sealed record MediaWriteRequest(
    string Path,
    string Title,
    string Album,
    IReadOnlyList<string> AlbumArtists,
    IReadOnlyList<string> Performers,
    IReadOnlyList<string> Genres,
    uint? Year,
    string Comment,
    string? ArtworkPath);

public sealed record RenamePlan(string CurrentPath, string ProposedPath, bool WillRename);

public sealed record MediaFingerprint(long FileSize, string PartialSha256, string? FullSha256 = null);

/// <summary>Portable result of inspecting a locally available media file.</summary>
public sealed record MediaInspection(
    AudioMetadata Metadata,
    MediaFingerprint Fingerprint,
    string? CachedArtworkPath);

public sealed record FileSynchronizationItem(
    long BroadcastId,
    string BroadcastUid,
    string CurrentPath,
    string ProposedPath,
    string CollectionName,
    DateTime? AirDate,
    string? Title,
    int PartNumber,
    string? ArtworkPath)
{
    public bool WillRename => !string.Equals(CurrentPath, ProposedPath, StringComparison.OrdinalIgnoreCase);
}

public sealed record FileSynchronizationOptions(
    bool RenameFiles = true,
    bool WriteTags = true,
    bool EmbedArtwork = false,
    bool CreateUndoManifest = true);

public sealed record FileSynchronizationProgress(
    int Completed,
    int Total,
    string CurrentPath,
    string Message)
{
    public double Percent => Total <= 0 ? 100 : Math.Clamp((double)Completed / Total * 100, 0, 100);
}

public sealed record FileSynchronizationResult(
    int Processed,
    int Renamed,
    int TagsWritten,
    int Failed,
    string? UndoManifestPath,
    IReadOnlyList<string> Errors);
