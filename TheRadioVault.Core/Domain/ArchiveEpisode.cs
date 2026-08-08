namespace TheRadioVault.Core.Domain;

public sealed record ArchiveEpisode(
    long Id,
    string CollectionName,
    DateOnly? AirDate,
    long PositionMs,
    long DurationMs,
    bool Completed,
    bool Favourite,
    string? ArtworkPath = null);

public sealed record ArchivePeriodSummary(
    int Value,
    string Title,
    int EpisodeCount,
    int CompletedCount,
    int FavouriteCount,
    double CompletionPercent,
    string? ArtworkPath);
