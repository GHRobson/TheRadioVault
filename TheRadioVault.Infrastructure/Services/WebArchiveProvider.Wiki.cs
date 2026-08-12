using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheRadioVault.Services.Models;
using TheRadioVault.Services.Services;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Services;

internal sealed partial class WebArchiveProvider
{
    public async Task<object?> ExecuteClientWikiAsync(
        string operation,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        var service = new WikiService(_database.PlatformDatabase);
        switch (operation)
        {
            case "overview":
                return await service.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
            case "browse":
                return await service.BrowseAsync(Read<WikiBrowseQuery>(payload), cancellationToken).ConfigureAwait(false);
            case "page":
                return await service.GetPageAsync(Read<WikiPageRequest>(payload).PageId, cancellationToken).ConfigureAwait(false);
            case "image":
                return await service.GetImageAsync(Read<WikiImageRequest>(payload).ImageId, cancellationToken).ConfigureAwait(false);
            case "revisions":
                return await service.GetRevisionsAsync(Read<WikiPageRequest>(payload).PageId, cancellationToken).ConfigureAwait(false);
            case "restore-revision":
            {
                var result = await service.RestoreRevisionAsync(Read<WikiRevisionRestoreRequest>(payload), cancellationToken).ConfigureAwait(false);
                AddChange("wiki", null, $"revision-restored:{result.PageId:D}:{result.Revision}", DateTimeOffset.UtcNow);
                return result;
            }
            case "citation-audit":
                return await service.AuditCitationsAsync(cancellationToken).ConfigureAwait(false);
            case "navigation":
                return await service.GetNavigationContextAsync(Read<WikiPageRequest>(payload).PageId, cancellationToken).ConfigureAwait(false);
            case "dashboard-highlights":
            {
                var request = Read<WikiDashboardRequest>(payload);
                return await service.GetDashboardHighlightsAsync(request.Month, request.Day, cancellationToken).ConfigureAwait(false);
            }
            case "timeline-shows":
                return await service.GetTimelineShowsAsync(cancellationToken).ConfigureAwait(false);
            case "quality-audit":
                return await service.AuditQualityAsync(cancellationToken).ConfigureAwait(false);
            case "topic-audit":
                return await service.AuditTopicsAsync(cancellationToken).ConfigureAwait(false);
            case "topic-auto-cleanup":
            {
                var result = await service.RunAutomaticTopicCleanupAsync(cancellationToken).ConfigureAwait(false);
                if (result.GroupsMerged > 0) AddChange("wiki", null, $"topics-auto-merged:{result.GroupsMerged}", DateTimeOffset.UtcNow);
                return result;
            }
            case "topic-merge":
            {
                var result = await service.MergeTopicsAsync(Read<TopicMergeRequest>(payload), cancellationToken).ConfigureAwait(false);
                AddChange("wiki", null, $"topics-merged:{result.MergeId:D}", DateTimeOffset.UtcNow);
                return result;
            }
            case "save":
            {
                var result = await service.SavePageAsync(Read<WikiPageDraft>(payload), cancellationToken).ConfigureAwait(false);
                AddChange("wiki", null, $"page-saved:{result.PageId:D}:{result.Revision}", DateTimeOffset.UtcNow);
                return result;
            }
            case "starter-preview":
                return await service.PreviewStarterPagesAsync(cancellationToken).ConfigureAwait(false);
            case "starter-generate":
            {
                var result = await service.GenerateStarterPagesAsync(cancellationToken).ConfigureAwait(false);
                AddChange("wiki", null, $"starter-pages-generated:{result.CreatedPages}", DateTimeOffset.UtcNow);
                return result;
            }
            case "archive-links":
                return await service.BrowseArchiveLinksAsync(Read<WikiArchiveBrowseQuery>(payload), cancellationToken).ConfigureAwait(false);
            default:
                throw new InvalidOperationException($"Unknown wiki client operation '{operation}'.");
        }
    }

    public async Task<WebWikiPackPreview> PreviewWikiPackAsync(
        Stream packageStream,
        string sourceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageStream);
        var transfer = new WikiAuthoringPackService();
        var hash = await HashSeekablePackageAsync(packageStream, cancellationToken).ConfigureAwait(false);
        var snapshot = transfer.Import(packageStream);
        var preview = await new WikiService(_database.PlatformDatabase)
            .PreviewImportAsync(snapshot, SafePackageName(sourceName), hash, cancellationToken)
            .ConfigureAwait(false);
        return ToWeb(preview);
    }

    public async Task<WebWikiPackImportResult> ApplyWikiPackAsync(
        Stream packageStream,
        string sourceName,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageStream);
        var hash = await HashSeekablePackageAsync(packageStream, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(expectedSha256) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(hash),
                Encoding.ASCII.GetBytes(expectedSha256.Trim().ToLowerInvariant())))
            throw new InvalidDataException("The wiki pack changed after it was previewed. Preview it again before importing.");

        var snapshot = new WikiAuthoringPackService().Import(packageStream);
        var result = await new WikiService(_database.PlatformDatabase)
            .ApplyImportAsync(snapshot, SafePackageName(sourceName), hash, cancellationToken)
            .ConfigureAwait(false);
        AddChange("wiki", null, $"pack-imported:{result.ImportRunId}", DateTimeOffset.UtcNow);
        return ToWeb(result);
    }

    public async Task<WebWikiPackExportPayload> ExportWikiPackAsync(CancellationToken cancellationToken = default)
    {
        var databaseName = Path.GetFileName(_database.PlatformDatabase.DatabasePath);
        var identityBytes = SHA256.HashData(Encoding.UTF8.GetBytes(databaseName));
        var databaseIdentity = Convert.ToHexString(identityBytes)[..16].ToLowerInvariant();
        var snapshot = await new WikiService(_database.PlatformDatabase)
            .GetAuthoringSnapshotAsync(AppVersionService.Version, databaseIdentity, cancellationToken)
            .ConfigureAwait(false);
        var bytes = new WikiAuthoringPackService().Export(snapshot);
        var fileName = $"RadioVault-Wiki-{DateTime.UtcNow:yyyyMMdd-HHmmss}.rvwiki";
        return new WebWikiPackExportPayload(bytes, fileName, snapshot.Pages.Count, snapshot.Images.Count);
    }

    private static WebWikiPackPreview ToWeb(WikiPackPreview value) => new(
        value.PackageName, value.PackageSha256, value.TotalPages, value.NewPages, value.ChangedPages,
        value.UnchangedPages, value.ConflictingPages, value.SourceCount, value.CitationCount, value.ImageCount,
        value.TimelineEventCount, value.ConflictTitles, value.CanApply, value.Summary,
        (value.PageChanges ?? Array.Empty<WikiPackPageChangePreview>()).Select(x => new WebWikiPackPageChangePreview(
            x.PageId, x.Title, x.PageType, x.ChangeKind, x.IncomingBaseRevision, x.CurrentRevision, x.Detail)).ToArray());

    private static WebWikiPackImportResult ToWeb(WikiPackImportResult value) => new(
        value.CreatedPages, value.UpdatedPages, value.UnchangedPages, value.SkippedConflicts,
        value.SourcesStored, value.CitationsStored, value.ImagesStored, value.TimelineEventsStored,
        value.ImportRunId, value.Summary);

    private static string SafePackageName(string? sourceName)
    {
        var name = Path.GetFileName(sourceName ?? string.Empty);
        return string.IsNullOrWhiteSpace(name) ? "RadioVault-Wiki.rvwiki" : name;
    }

    private static async Task<string> HashSeekablePackageAsync(
        Stream packageStream,
        CancellationToken cancellationToken)
    {
        if (!packageStream.CanRead || !packageStream.CanSeek)
            throw new InvalidDataException("The staged wiki authoring pack is not seekable.");
        packageStream.Position = 0;
        var hash = Convert.ToHexString(
            await SHA256.HashDataAsync(packageStream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        packageStream.Position = 0;
        return hash;
    }

    private sealed record WikiPageRequest(Guid PageId);
    private sealed record WikiImageRequest(Guid ImageId);
    private sealed record WikiDashboardRequest(int Month, int Day);
}
