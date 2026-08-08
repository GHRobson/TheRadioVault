using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Contracts;

public interface IResearchWorkspaceService
{
    Task<ResearchWorkspaceOverview> GetOverviewAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ResearchCollectionOption>> GetCollectionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ResearchBrowseItem>> BrowseAsync(ResearchBrowseQuery query, CancellationToken cancellationToken = default);
    Task<ResearchRecordDetails?> GetDetailsAsync(long researchId, CancellationToken cancellationToken = default);
    Task SaveMetadataAsync(ResearchMetadataUpdate update, CancellationToken cancellationToken = default);
    Task SetNeedsReviewAsync(long researchId, bool needsReview, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ResearchSourceDiagnostic>> GetSourceDiagnosticsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ResearchImportRunSummary>> GetImportHistoryAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UndatedBroadcastItem>> GetUndatedBroadcastsAsync(int? collectionId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CatalogueDateReviewItem>> GetCatalogueDateReviewsAsync(int? collectionId = null, bool includeResolved = false, CancellationToken cancellationToken = default);
    Task ResolveCatalogueDateReviewAsync(long researchId, CatalogueDateReviewAction action, DateOnly? selectedDate = null, CancellationToken cancellationToken = default);
    Task AssignBroadcastDateAsync(long episodeId, DateOnly airDate, CancellationToken cancellationToken = default);
    Task<ResearchCoverageShow?> GetCoverageAsync(int collectionId, CancellationToken cancellationToken = default);
}
