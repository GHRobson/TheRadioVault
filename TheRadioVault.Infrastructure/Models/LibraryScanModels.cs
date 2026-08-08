namespace TheRadioVault.Models;

/// <summary>
/// Lightweight database state used to avoid re-reading tags, artwork and
/// fingerprints when an archive file has not changed since the previous scan.
/// </summary>
public sealed class LibraryScanFileSnapshot
{
    public long EpisodeId { get; init; }
    public long FileSize { get; init; }
    public DateTime ModifiedUtc { get; init; }
    public int CollectionId { get; init; }
    public EpisodeStorageState StorageState { get; init; }
    public bool WasMissing { get; init; }
}

/// <summary>
/// Result of a scan upsert. Returning the episode identity avoids an extra
/// database lookup for every file in large archive scans.
/// </summary>
public readonly struct ScannedFileUpsertResult
{
    public ScannedFileUpsertResult(bool added, long episodeId)
    {
        Added = added;
        EpisodeId = episodeId;
    }

    public bool Added { get; }
    public long EpisodeId { get; }
}
