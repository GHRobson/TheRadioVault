using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Contracts;

/// <summary>
/// Toolkit-neutral read model used by the redesigned desktop shells. It exposes
/// canonical broadcasts rather than physical-file or legacy episode identity.
/// </summary>
public interface ILibraryBrowseService
{
    Task<LibraryOverview> GetOverviewAsync(CancellationToken cancellationToken = default);
    Task<LibraryBroadcastSummary?> GetBroadcastAsync(
        long representativeEpisodeId,
        CancellationToken cancellationToken = default);
    Task<LibraryBrowseResult> BrowseAsync(LibraryBrowseRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LibraryArchivePeriodSummary>> GetArchivePeriodsAsync(
        int? collectionId,
        int? year,
        bool hideCompleted = false,
        CancellationToken cancellationToken = default);
    Task<LibrarySearchFacets> GetSearchFacetsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LibrarySearchSuggestion>> GetSearchSuggestionsAsync(
        string prefix,
        int limit = 10,
        CancellationToken cancellationToken = default);
}
