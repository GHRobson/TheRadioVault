using TheRadioVault.Web.Contracts;

namespace TheRadioVault.Web.Services;

public sealed partial class LocalWebServer
{
    private async Task<bool> TryHandleFederationAdministrationRouteAsync(
        Stream stream,
        string path,
        IReadOnlyDictionary<string, string> query,
        HttpRequest request,
        bool isGet,
        bool isHead,
        bool isPost,
        CancellationToken cancellationToken)
    {
        if (path.Equals(WebApiRoutes.FederationStatus, StringComparison.OrdinalIgnoreCase))
        {
            await HandleFederationStatusAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.FederationBootstrap, StringComparison.OrdinalIgnoreCase))
        {
            await HandleFederationBootstrapAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.FederationLibrarySync, StringComparison.OrdinalIgnoreCase))
        {
            if (!isGet && !isHead)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Library synchronization supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleFederationLibrarySyncAsync(stream, query, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.FederationLibraryScan, StringComparison.OrdinalIgnoreCase))
        {
            if (!isGet && !isHead && !isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Library scanning supports GET, HEAD and POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleFederationLibraryScanAsync(stream, request, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.FederationParity, StringComparison.OrdinalIgnoreCase))
        {
            if (!isGet && !isHead)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Remote-client parity supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleFederationParityAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.FederationSettings, StringComparison.OrdinalIgnoreCase))
        {
            if (!isGet && !isHead)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Server settings support GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleFederationSettingsAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.FederationPlaybackPreferences, StringComparison.OrdinalIgnoreCase))
        {
            if (!isGet && !isHead && !isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Playback preferences support GET, HEAD and POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleFederationPlaybackPreferencesAsync(stream, request, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.FederationResearchWorkspace, StringComparison.OrdinalIgnoreCase))
        {
            if (!isGet && !isHead)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Research workspace browsing supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleFederationResearchWorkspaceAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.FederationResearchUndated, StringComparison.OrdinalIgnoreCase))
        {
            if (!isGet && !isHead)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Undated Research browsing supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleFederationResearchUndatedAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (TryMatchFederationResearchCoverageByShow(path, out var researchCoverageShow))
        {
            if (!isGet && !isHead)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Research coverage supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleFederationResearchCoverageByShowAsync(stream, researchCoverageShow, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (TryMatchFederationResearchCoverage(path, out var researchCoverageCollectionId))
        {
            if (!isGet && !isHead)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Research coverage supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleFederationResearchCoverageAsync(stream, researchCoverageCollectionId, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (TryMatchFederationResearchUndatedDate(path, out var undatedEpisodeId))
        {
            if (!isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Manual broadcast dating requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleFederationResearchUndatedDateAsync(stream, request, undatedEpisodeId, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (TryMatchFederationResearchWorkspaceRecord(path, out var researchWorkspaceRecordId))
        {
            if (!isGet && !isHead)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Research record browsing supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleFederationResearchWorkspaceRecordAsync(stream, researchWorkspaceRecordId, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.FederationResearchImportPreview, StringComparison.OrdinalIgnoreCase))
        {
            if (!isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Research pack analysis requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleFederationResearchImportPreviewAsync(stream, request, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.FederationResearchImportApply, StringComparison.OrdinalIgnoreCase))
        {
            if (!isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Research pack import requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleFederationResearchImportApplyAsync(stream, request, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.FederationResearchImportStatus, StringComparison.OrdinalIgnoreCase))
        {
            if (!isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Research import status requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleFederationResearchImportStatusAsync(stream, request, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.FederationResearchImportCancel, StringComparison.OrdinalIgnoreCase))
        {
            if (!isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Research pack cancellation requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleFederationResearchImportCancelAsync(stream, request, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.FederationResearchExport, StringComparison.OrdinalIgnoreCase))
        {
            if (!isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Research pack export requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleFederationResearchExportAsync(stream, request, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.FederationWikiImportPreview, StringComparison.OrdinalIgnoreCase))
        {
            if (!isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Wiki pack analysis requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleFederationWikiImportPreviewAsync(stream, request, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.FederationWikiImportApply, StringComparison.OrdinalIgnoreCase))
        {
            if (!isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Wiki pack import requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleFederationWikiImportApplyAsync(stream, request, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.FederationWikiExport, StringComparison.OrdinalIgnoreCase))
        {
            if (!isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Wiki pack export requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleFederationWikiExportAsync(stream, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
