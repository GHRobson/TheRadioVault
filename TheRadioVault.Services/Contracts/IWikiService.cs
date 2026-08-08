using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Contracts;

public interface IWikiService
{
    Task<WikiOverview> GetOverviewAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WikiPageSummary>> BrowseAsync(WikiBrowseQuery query, CancellationToken cancellationToken = default);
    Task<WikiPageDocument?> GetPageAsync(Guid pageId, CancellationToken cancellationToken = default);
    Task<WikiImageContent?> GetImageAsync(Guid imageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WikiRevisionRecord>> GetRevisionsAsync(Guid pageId, CancellationToken cancellationToken = default);
    Task<WikiPageSaveResult> RestoreRevisionAsync(WikiRevisionRestoreRequest request, CancellationToken cancellationToken = default);
    Task<WikiCitationAuditReport> AuditCitationsAsync(CancellationToken cancellationToken = default);
    Task<WikiNavigationContext> GetNavigationContextAsync(Guid pageId, CancellationToken cancellationToken = default);
    Task<WikiDashboardHighlights> GetDashboardHighlightsAsync(int month, int day, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WikiTimelineShowSummary>> GetTimelineShowsAsync(CancellationToken cancellationToken = default);
    Task<WikiQualityAuditReport> AuditQualityAsync(CancellationToken cancellationToken = default);
    Task<TopicCleanupReport> AuditTopicsAsync(CancellationToken cancellationToken = default);
    Task<TopicAutomaticCleanupResult> RunAutomaticTopicCleanupAsync(CancellationToken cancellationToken = default);
    Task<TopicMergeResult> MergeTopicsAsync(TopicMergeRequest request, CancellationToken cancellationToken = default);
    Task<WikiPageSaveResult> SavePageAsync(WikiPageDraft draft, CancellationToken cancellationToken = default);
    Task<WikiStarterPagePreview> PreviewStarterPagesAsync(CancellationToken cancellationToken = default);
    Task<WikiStarterGenerationResult> GenerateStarterPagesAsync(CancellationToken cancellationToken = default);
    Task<WikiArchiveLinkResults> BrowseArchiveLinksAsync(WikiArchiveBrowseQuery query, CancellationToken cancellationToken = default);
}

public interface IWikiPackTransferService
{
    bool IsAvailable { get; }
    Task<WikiPackExportPayload> ExportAsync(CancellationToken cancellationToken = default);
    Task<WikiPackPreview> PreviewImportAsync(string filePath, CancellationToken cancellationToken = default);
    Task<WikiPackImportResult> ApplyImportAsync(CancellationToken cancellationToken = default);
    Task CancelImportAsync(CancellationToken cancellationToken = default);
}
