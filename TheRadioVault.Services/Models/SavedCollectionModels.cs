namespace TheRadioVault.Services.Models;

public enum SavedCollectionKind
{
    Manual,
    Smart
}

public sealed record SavedCollectionRule(
    string? SearchText = null,
    int? CollectionId = null,
    LibraryListeningFilter Filter = LibraryListeningFilter.All,
    int? Year = null,
    int? Month = null,
    LibrarySearchScope SearchScope = LibrarySearchScope.All,
    bool HasTranscript = false,
    bool HideCompleted = false,
    bool NewestFirst = true,
    int Limit = 250)
{
    public LibraryBrowseRequest ToBrowseRequest() => new(
        SearchText,
        CollectionId,
        Filter,
        Year,
        Month,
        Math.Clamp(Limit, 1, 1000),
        Offset: 0,
        NewestFirst,
        SearchScope,
        HasTranscript,
        HideCompleted);
}

public sealed record SavedCollectionSummary(
    long Id,
    string Name,
    SavedCollectionKind Kind,
    int? ItemCount,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsSmart => Kind == SavedCollectionKind.Smart;
    public string KindText => IsSmart ? "Smart collection" : "Playlist";
    public string CountText => ItemCount.HasValue
        ? $"{ItemCount.Value:N0} broadcast{(ItemCount.Value == 1 ? string.Empty : "s")}"
        : "Updates automatically";
}

public sealed record SavedCollectionDetails(
    SavedCollectionSummary Summary,
    SavedCollectionRule? Rule,
    IReadOnlyList<LibraryBroadcastSummary> Broadcasts);

public sealed class SavedCollectionConflictException : InvalidOperationException
{
    public SavedCollectionConflictException(long collectionId, long expectedRevision, long actualRevision)
        : base($"Saved collection {collectionId} changed on another device. Expected revision {expectedRevision}, current revision {actualRevision}.")
    {
        CollectionId = collectionId;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public long CollectionId { get; }
    public long ExpectedRevision { get; }
    public long ActualRevision { get; }
}
