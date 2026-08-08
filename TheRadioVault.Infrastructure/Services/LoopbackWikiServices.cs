using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;
using TheRadioVault.Services.Services;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Services;

public sealed class LoopbackWikiService : IWikiService
{
    private readonly LoopbackServerClient _connection;

    public LoopbackWikiService(LoopbackServerClient connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public Task<WikiOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        => CallAsync<WikiOverview>("overview", new { }, cancellationToken);

    public async Task<IReadOnlyList<WikiPageSummary>> BrowseAsync(WikiBrowseQuery query, CancellationToken cancellationToken = default)
        => await CallAsync<List<WikiPageSummary>>("browse", query, cancellationToken).ConfigureAwait(false);

    public Task<WikiPageDocument?> GetPageAsync(Guid pageId, CancellationToken cancellationToken = default)
        => CallAsync<WikiPageDocument?>("page", new { pageId }, cancellationToken);

    public Task<WikiImageContent?> GetImageAsync(Guid imageId, CancellationToken cancellationToken = default)
        => CallAsync<WikiImageContent?>("image", new { imageId }, cancellationToken);

    public async Task<IReadOnlyList<WikiRevisionRecord>> GetRevisionsAsync(Guid pageId, CancellationToken cancellationToken = default)
        => await CallAsync<List<WikiRevisionRecord>>("revisions", new { pageId }, cancellationToken).ConfigureAwait(false);

    public Task<WikiPageSaveResult> RestoreRevisionAsync(WikiRevisionRestoreRequest request, CancellationToken cancellationToken = default)
        => CallAsync<WikiPageSaveResult>("restore-revision", request, cancellationToken);

    public Task<WikiCitationAuditReport> AuditCitationsAsync(CancellationToken cancellationToken = default)
        => CallAsync<WikiCitationAuditReport>("citation-audit", new { }, cancellationToken);

    public Task<WikiNavigationContext> GetNavigationContextAsync(Guid pageId, CancellationToken cancellationToken = default)
        => CallAsync<WikiNavigationContext>("navigation", new { pageId }, cancellationToken);

    public Task<WikiDashboardHighlights> GetDashboardHighlightsAsync(int month, int day, CancellationToken cancellationToken = default)
        => CallAsync<WikiDashboardHighlights>("dashboard-highlights", new { month, day }, cancellationToken);

    public async Task<IReadOnlyList<WikiTimelineShowSummary>> GetTimelineShowsAsync(CancellationToken cancellationToken = default)
        => await CallAsync<List<WikiTimelineShowSummary>>("timeline-shows", new { }, cancellationToken).ConfigureAwait(false);

    public Task<WikiQualityAuditReport> AuditQualityAsync(CancellationToken cancellationToken = default)
        => CallAsync<WikiQualityAuditReport>("quality-audit", new { }, cancellationToken);

    public Task<TopicCleanupReport> AuditTopicsAsync(CancellationToken cancellationToken = default)
        => CallAsync<TopicCleanupReport>("topic-audit", new { }, cancellationToken);

    public Task<TopicAutomaticCleanupResult> RunAutomaticTopicCleanupAsync(CancellationToken cancellationToken = default)
        => CallAsync<TopicAutomaticCleanupResult>("topic-auto-cleanup", new { }, cancellationToken);

    public Task<TopicMergeResult> MergeTopicsAsync(TopicMergeRequest request, CancellationToken cancellationToken = default)
        => CallAsync<TopicMergeResult>("topic-merge", request, cancellationToken);

    public Task<WikiPageSaveResult> SavePageAsync(WikiPageDraft draft, CancellationToken cancellationToken = default)
        => CallAsync<WikiPageSaveResult>("save", draft, cancellationToken);

    public Task<WikiStarterPagePreview> PreviewStarterPagesAsync(CancellationToken cancellationToken = default)
        => CallAsync<WikiStarterPagePreview>("starter-preview", new { }, cancellationToken);

    public Task<WikiStarterGenerationResult> GenerateStarterPagesAsync(CancellationToken cancellationToken = default)
        => CallAsync<WikiStarterGenerationResult>("starter-generate", new { }, cancellationToken);

    public Task<WikiArchiveLinkResults> BrowseArchiveLinksAsync(WikiArchiveBrowseQuery query, CancellationToken cancellationToken = default)
        => CallAsync<WikiArchiveLinkResults>("archive-links", query, cancellationToken);

    private async Task<T> CallAsync<T>(string operation, object body, CancellationToken cancellationToken)
    {
        var envelope = await _connection.SendJsonAsync<ValueEnvelope<T>>(
            HttpMethod.Post,
            WebApiRoutes.ClientWikiOperation(operation),
            body,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return envelope.Value;
    }

    private sealed record ValueEnvelope<T>(T Value);
}

public sealed class LoopbackWikiPackTransferService : IWikiPackTransferService
{
    private readonly LoopbackServerClient _connection;
    private string? _pendingPath;
    private string? _pendingSha256;

    public LoopbackWikiPackTransferService(LoopbackServerClient connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public bool IsAvailable => _connection.IsAvailable;

    public async Task<WikiPackExportPayload> ExportAsync(CancellationToken cancellationToken = default)
    {
        var response = await _connection.PostJsonForFileAsync(
            WebApiRoutes.FederationWikiExport, new { }, cancellationToken).ConfigureAwait(false);
        return new WikiPackExportPayload(response.Bytes, response.FileName, response.PageCount, response.ImageCount);
    }

    public async Task<WikiPackPreview> PreviewImportAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(filePath);
        if (!file.Exists) throw new FileNotFoundException("The selected wiki authoring pack no longer exists.", filePath);
        if (file.Length == 0) throw new InvalidDataException("The selected wiki authoring pack is empty.");
        if (file.Length > WikiAuthoringPackService.MaximumPackageBytes)
            throw new InvalidDataException($"Wiki authoring packs are limited to {WikiAuthoringPackService.MaximumPackageBytes / 1024 / 1024} MB.");

        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        var envelope = await _connection.PostBytesForJsonAsync<PreviewEnvelope>(
            WebApiRoutes.FederationWikiImportPreview,
            bytes,
            "application/vnd.radiovault.wiki+zip",
            new Dictionary<string, string> { ["X-Radio-Vault-File-Name"] = Uri.EscapeDataString(file.Name) },
            cancellationToken).ConfigureAwait(false);
        _pendingPath = file.FullName;
        _pendingSha256 = envelope.Result.PackageSha256;
        return FromWeb(envelope.Result);
    }

    public async Task<WikiPackImportResult> ApplyImportAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_pendingPath) || string.IsNullOrWhiteSpace(_pendingSha256))
            throw new InvalidOperationException("Choose and preview a wiki authoring pack before importing it.");
        if (!File.Exists(_pendingPath)) throw new FileNotFoundException("The previewed wiki authoring pack no longer exists.", _pendingPath);

        var bytes = await File.ReadAllBytesAsync(_pendingPath, cancellationToken).ConfigureAwait(false);
        var envelope = await _connection.PostBytesForJsonAsync<ApplyEnvelope>(
            WebApiRoutes.FederationWikiImportApply,
            bytes,
            "application/vnd.radiovault.wiki+zip",
            new Dictionary<string, string>
            {
                ["X-Radio-Vault-File-Name"] = Uri.EscapeDataString(Path.GetFileName(_pendingPath)),
                ["X-Radio-Vault-Package-Sha256"] = _pendingSha256
            },
            cancellationToken).ConfigureAwait(false);
        _pendingPath = null;
        _pendingSha256 = null;
        return FromWeb(envelope.Result);
    }

    public Task CancelImportAsync(CancellationToken cancellationToken = default)
    {
        _pendingPath = null;
        _pendingSha256 = null;
        return Task.CompletedTask;
    }

    private static WikiPackPreview FromWeb(WebWikiPackPreview value) => new(
        value.PackageName, value.PackageSha256, value.TotalPages, value.NewPages, value.ChangedPages,
        value.UnchangedPages, value.ConflictingPages, value.SourceCount, value.CitationCount, value.ImageCount,
        value.TimelineEventCount, value.ConflictTitles, value.CanApply, value.Summary,
        value.PageChanges.Select(x => new WikiPackPageChangePreview(x.PageId, x.Title, x.PageType, x.ChangeKind,
            x.IncomingBaseRevision, x.CurrentRevision, x.Detail)).ToArray());

    private static WikiPackImportResult FromWeb(WebWikiPackImportResult value) => new(
        value.CreatedPages, value.UpdatedPages, value.UnchangedPages, value.SkippedConflicts,
        value.SourcesStored, value.CitationsStored, value.ImagesStored, value.TimelineEventsStored,
        value.ImportRunId, value.Summary);

    private sealed record PreviewEnvelope(WebWikiPackPreview Result);
    private sealed record ApplyEnvelope(WebWikiPackImportResult Result);
}
