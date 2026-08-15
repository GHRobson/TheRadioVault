using TheRadioVault.Core.Services;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Services;

public sealed class LoopbackResearchWorkspaceService : IResearchWorkspaceService
{
    private readonly LoopbackServerClient _connection;

    public LoopbackResearchWorkspaceService(LoopbackServerClient connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public Task<ResearchWorkspaceOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        => CallAsync<ResearchWorkspaceOverview>("overview", new { }, cancellationToken);

    public Task<IReadOnlyList<ResearchCollectionOption>> GetCollectionsAsync(CancellationToken cancellationToken = default)
        => CallListAsync<ResearchCollectionOption>("collections", new { }, cancellationToken);

    public Task<IReadOnlyList<ResearchBrowseItem>> BrowseAsync(ResearchBrowseQuery query, CancellationToken cancellationToken = default)
        => CallListAsync<ResearchBrowseItem>("browse", query, cancellationToken);

    public Task<ResearchRecordDetails?> GetDetailsAsync(long researchId, CancellationToken cancellationToken = default)
        => CallAsync<ResearchRecordDetails?>("details", new { id = researchId }, cancellationToken);

    public async Task SaveMetadataAsync(ResearchMetadataUpdate update, CancellationToken cancellationToken = default)
        => _ = await CallAsync<bool>("save-metadata", update, cancellationToken).ConfigureAwait(false);

    public async Task SetNeedsReviewAsync(long researchId, bool needsReview, CancellationToken cancellationToken = default)
        => _ = await CallAsync<bool>("set-review", new { researchId, needsReview }, cancellationToken).ConfigureAwait(false);

    public Task<IReadOnlyList<ResearchSourceDiagnostic>> GetSourceDiagnosticsAsync(CancellationToken cancellationToken = default)
        => CallListAsync<ResearchSourceDiagnostic>("source-diagnostics", new { }, cancellationToken);

    public Task<IReadOnlyList<ResearchImportRunSummary>> GetImportHistoryAsync(int limit = 100, CancellationToken cancellationToken = default)
        => CallListAsync<ResearchImportRunSummary>("import-history", new { limit }, cancellationToken);

    public Task<IReadOnlyList<UndatedBroadcastItem>> GetUndatedBroadcastsAsync(int? collectionId = null, CancellationToken cancellationToken = default)
        => CallListAsync<UndatedBroadcastItem>("undated", new { collectionId }, cancellationToken);

    public Task<IReadOnlyList<CatalogueDateReviewItem>> GetCatalogueDateReviewsAsync(int? collectionId = null, bool includeResolved = false, CancellationToken cancellationToken = default)
        => CallListAsync<CatalogueDateReviewItem>("date-reviews", new { collectionId, includeResolved }, cancellationToken);

    public async Task ResolveCatalogueDateReviewAsync(long researchId, CatalogueDateReviewAction action, DateOnly? selectedDate = null, CancellationToken cancellationToken = default)
        => _ = await CallAsync<bool>("resolve-date-review", new { researchId, action, selectedDate }, cancellationToken).ConfigureAwait(false);

    public async Task AssignBroadcastDateAsync(long episodeId, DateOnly airDate, CancellationToken cancellationToken = default)
        => _ = await CallAsync<bool>("assign-date", new { episodeId, airDate }, cancellationToken).ConfigureAwait(false);

    public Task<ResearchCoverageShow?> GetCoverageAsync(int collectionId, CancellationToken cancellationToken = default)
        => CallAsync<ResearchCoverageShow?>("coverage", new { collectionId }, cancellationToken);

    private async Task<T> CallAsync<T>(string operation, object body, CancellationToken cancellationToken)
    {
        var envelope = await _connection.SendJsonAsync<ValueEnvelope<T>>(
            HttpMethod.Post,
            WebApiRoutes.ClientResearchOperation(operation),
            body,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return envelope.Value;
    }

    private async Task<IReadOnlyList<T>> CallListAsync<T>(string operation, object body, CancellationToken cancellationToken)
        => await CallAsync<List<T>>(operation, body, cancellationToken).ConfigureAwait(false);

    private sealed record ValueEnvelope<T>(T Value);
}

public sealed class LoopbackResearchPackTransferService : IResearchPackTransferService
{
    private readonly LoopbackServerClient _connection;
    private Guid? _pendingSessionId;

    public LoopbackResearchPackTransferService(LoopbackServerClient connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public bool IsAvailable => _connection.IsAvailable;
    public bool IsRemoteOwned => true;

    public async Task<ResearchPackPreviewSummary> PreviewImportAsync(
        string filePath,
        IProgress<ResearchPackTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(filePath);
        if (!file.Exists)
            throw new FileNotFoundException("The selected Archive Knowledge Database no longer exists.", filePath);
        if (file.Length == 0)
            throw new InvalidDataException("The selected Archive Knowledge Database is empty.");
        if (file.Length > WebResearchPackLimits.MaximumPackageBytes)
            throw new InvalidDataException($"This Archive Knowledge Database is larger than the supported {WebResearchPackLimits.MaximumPackageBytes / 1024 / 1024} MB limit.");

        progress?.Report(new ResearchPackTransferProgress(2, "Reading the Knowledge Database…", Phase: "Preview"));
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        progress?.Report(new ResearchPackTransferProgress(12, "Uploading and comparing the Knowledge Database with the server archive…", Phase: "Preview"));
        var envelope = await _connection.PostBytesForJsonAsync<PreviewEnvelope>(
            WebApiRoutes.FederationResearchImportPreview,
            bytes,
            "application/vnd.radiovault.knowledge+sqlite3",
            new Dictionary<string, string> { ["X-Radio-Vault-File-Name"] = Uri.EscapeDataString(Path.GetFileName(filePath)) },
            cancellationToken).ConfigureAwait(false);
        _pendingSessionId = envelope.Result.SessionId;
        var value = envelope.Result.Preview;
        progress?.Report(new ResearchPackTransferProgress(100, "Knowledge Database analysis complete.", value.TotalRecords, value.TotalRecords, "Preview"));
        return new ResearchPackPreviewSummary(
            value.PackageName,
            value.Show,
            value.TotalRecords,
            value.TranscriptCount,
            value.ExactMatches,
            value.MissingRecords,
            value.AmbiguousMatches,
            value.AuthoritativeAudit,
            $"{value.ExactMatches:N0} matched, {value.MissingRecords:N0} missing, {value.AmbiguousMatches:N0} need review, {value.TranscriptCount:N0} transcripts, {value.WikiPageCount:N0} Wiki pages.",
            value.WikiPageCount,
            value.WikiImageCount,
            value.WikiTimelineEventCount);
    }

    public async Task<ResearchPackApplySummary> ApplyImportAsync(
        IProgress<ResearchPackTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = _pendingSessionId ?? throw new InvalidOperationException("Analyse an Archive Knowledge Database before importing it.");
        var envelope = await _connection.SendJsonAsync<ImportJobEnvelope>(
            HttpMethod.Post,
            WebApiRoutes.FederationResearchImportApply,
            new WebResearchPackApplyRequest(sessionId),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var job = envelope.Result;
        while (job.State is "Queued" or "Running" or "Pending")
        {
            progress?.Report(new ResearchPackTransferProgress(
                job.Percent, job.Message, job.Current, job.Total, "Import"));
            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
            job = (await _connection.SendJsonAsync<ImportJobEnvelope>(
                HttpMethod.Post,
                WebApiRoutes.FederationResearchImportStatus,
                new WebResearchPackApplyRequest(sessionId),
                cancellationToken: cancellationToken).ConfigureAwait(false)).Result;
        }

        _pendingSessionId = null;
        if (job.State == "Cancelled")
            throw new OperationCanceledException("The Knowledge Database import was cancelled. No partial changes were kept.");
        if (job.State == "Failed")
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(job.Error)
                ? "The server could not import the Knowledge Database. No partial changes were kept."
                : $"{job.Error} No partial changes were kept.");
        var value = job.Result ?? throw new InvalidOperationException(
            "The server completed the Knowledge Database job without returning an import result.");
        progress?.Report(new ResearchPackTransferProgress(100, "Knowledge Database import complete.", value.Total, value.Total, "Import"));
        return new ResearchPackApplySummary(
            value.ResearchRecordsStored,
            value.Matched,
            value.RetainedMissing,
            value.ConflictsCreated,
            $"Imported {value.ResearchRecordsStored:N0} research records and {value.WikiPagesChanged:N0} Wiki pages; {value.Matched:N0} broadcasts matched and {value.ConflictsCreated + value.WikiConflicts:N0} conflicts recorded.",
            value.WikiPagesChanged,
            value.WikiConflicts);
    }

    public async Task CancelImportAsync(CancellationToken cancellationToken = default)
    {
        if (!_pendingSessionId.HasValue) return;
        await _connection.SendJsonAsync<CancelEnvelope>(
            HttpMethod.Post,
            WebApiRoutes.FederationResearchImportCancel,
            new WebResearchPackCancelRequest(_pendingSessionId.Value),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _pendingSessionId = null;
    }

    public async Task<ResearchPackExportSummary> ExportAsync(
        KnowledgeExportScope scope = KnowledgeExportScope.Complete,
        CancellationToken cancellationToken = default)
    {
        var response = await _connection.PostJsonForBytesAsync(
            WebApiRoutes.FederationResearchExport,
            new WebResearchPackExportRequest(scope.ToWireValue()),
            cancellationToken).ConfigureAwait(false);
        if (scope != KnowledgeExportScope.Complete &&
            !string.Equals(response.ExportScope, scope.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The connected Radio Vault Server does not support focused Knowledge exports yet. Update the server before exporting this research queue.");
        return new ResearchPackExportSummary(
            response.Bytes,
            response.FileName,
            response.BroadcastCount,
            response.MissingCount,
            response.TranscriptCount,
            response.WikiPageCount);
    }

    private sealed record PreviewEnvelope(WebResearchPackPreviewResponse Result);
    private sealed record ImportJobEnvelope(WebResearchPackImportJob Result);
    private sealed record CancelEnvelope(bool Cancelled);
    private sealed record FacetsEnvelope(WebClientLibrarySearchFacets Facets);
}
