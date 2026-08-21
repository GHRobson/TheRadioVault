namespace TheRadioVault.Services.Models;

public sealed record BroadcastSummary(
    long Id,
    string BroadcastId,
    int CollectionId,
    string CollectionName,
    DateOnly? AirDate,
    int PartNumber,
    string? Title,
    string? Description,
    bool Favourite,
    bool Hidden,
    long PositionMs,
    long DurationMs,
    bool Completed,
    DateTimeOffset? LastPlayedAt,
    string? ArtworkPath);

public sealed record MomentRecord(
    long Id,
    long BroadcastId,
    string CollectionName,
    string? BroadcastTitle,
    DateOnly? AirDate,
    long PositionMs,
    string Title,
    string Notes,
    DateTimeOffset CreatedAt);

public sealed record LibraryFolderRecord(
    long Id,
    string Path,
    int? AssignedCollectionId,
    string? AssignedCollectionName,
    bool Recursive,
    bool Enabled,
    DateTimeOffset? LastScanAt,
    bool IsManagedArchive = false)
{
    public string AssignmentDisplayName => string.IsNullOrWhiteSpace(AssignedCollectionName)
        ? IsManagedArchive ? "Managed archive · auto-detect shows" : "Auto-detect / mixed-show folder"
        : AssignedCollectionName;
}

public sealed record LibraryFolderCollectionOption(
    int CollectionId,
    string Name);

public sealed record QueueRecord(
    long Id,
    long BroadcastId,
    int Position,
    DateTimeOffset AddedAt,
    string CollectionName,
    string? BroadcastTitle,
    string OriginalFilename,
    string Path,
    DateOnly? AirDate);

public sealed record ArchiveSearchRequest(
    int? CollectionId = null,
    int? Year = null,
    int? Month = null,
    string? SearchText = null,
    bool? Favourite = null,
    bool IncludeHidden = false,
    int Limit = 500,
    int Offset = 0,
    bool NewestFirst = true);
