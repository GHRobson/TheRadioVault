namespace TheRadioVault.Services.Models;

public enum LibraryListeningFilter
{
    All,
    ContinueListening,
    Favourites,
    Completed,
    Unplayed,
    NeedsAttention,
    RecentlyAdded,
    OnThisDay
}

public enum LibrarySearchScope
{
    All,
    TitlesAndSummaries,
    People,
    Topics,
    Research,
    Transcripts
}

public sealed record LibraryBrowseRequest(
    string? SearchText = null,
    int? CollectionId = null,
    LibraryListeningFilter Filter = LibraryListeningFilter.All,
    int? Year = null,
    int? Month = null,
    int Limit = 250,
    int Offset = 0,
    bool NewestFirst = true,
    LibrarySearchScope SearchScope = LibrarySearchScope.All,
    bool HasTranscript = false,
    bool HideCompleted = false);

public sealed record LibrarySearchFacets(
    IReadOnlyList<int> Years,
    int TranscriptCount);

public sealed record LibrarySearchSuggestion(
    string Value,
    string Kind,
    int MatchCount);

public sealed record LibraryBroadcastSummary(
    string CanonicalKey,
    long RepresentativeEpisodeId,
    string BroadcastId,
    int CollectionId,
    string CollectionName,
    DateOnly? AirDate,
    DateTimeOffset DateAdded,
    string BroadcastSlot,
    string? Title,
    string? Description,
    bool Favourite,
    bool Completed,
    bool InProgress,
    long PositionMs,
    long DurationMs,
    DateTimeOffset? LastPlayedAt,
    string? ArtworkPath,
    int RecordingCount,
    int SegmentCount,
    int PhysicalFileCount,
    bool NeedsAttention,
    string AttentionReason)
{
    public string SearchContext { get; init; } = "";
    public int SearchScore { get; init; }
    public bool HasSearchContext => !string.IsNullOrWhiteSpace(SearchContext);

    public int ProgressPercent => Completed
        ? 100
        : DurationMs <= 0
        ? 0
        : (int)Math.Clamp(Math.Round(PositionMs * 100d / DurationMs), 0d, 99d);
}

public sealed record LibraryCollectionSummary(
    int CollectionId,
    string CollectionName,
    int BroadcastCount);

public sealed record LibraryArchivePeriodSummary(
    int Value,
    string Title,
    int BroadcastCount,
    int CompletedCount,
    int FavouriteCount,
    int ProgressPercent,
    string ProgressText,
    string ShowsText,
    string? ArtworkPath);

public sealed record LibraryOverview(
    int TotalBroadcasts,
    int CompletedBroadcasts,
    int InProgressBroadcasts,
    int FavouriteBroadcasts,
    int NeedsAttentionBroadcasts,
    bool UsesCanonicalLibrary,
    IReadOnlyList<LibraryCollectionSummary> Collections,
    IReadOnlyList<LibraryBroadcastSummary> ContinueListening,
    IReadOnlyList<LibraryBroadcastSummary> RecentBroadcasts,
    IReadOnlyList<LibraryBroadcastSummary> OnThisDay);

public sealed record LibraryBrowseResult(
    IReadOnlyList<LibraryBroadcastSummary> Broadcasts,
    int TotalMatching,
    bool UsesCanonicalLibrary);
