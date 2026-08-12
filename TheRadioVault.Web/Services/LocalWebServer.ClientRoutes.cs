using TheRadioVault.Web.Contracts;

namespace TheRadioVault.Web.Services;

public sealed partial class LocalWebServer
{
    private async Task<bool> TryHandleClientRouteAsync(
        Stream stream,
        string path,
        IReadOnlyDictionary<string, string> query,
        HttpRequest request,
        bool isHead,
        CancellationToken cancellationToken)
    {
        if (path.Equals(WebApiRoutes.Bootstrap, StringComparison.OrdinalIgnoreCase))
        {
            await HandleBootstrapApiAsync(stream, query, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (TryMatchClientOperation(path, WebApiRoutes.ClientResearch, out var researchOperation))
        {
            await HandleClientOperationAsync(stream, request, researchOperation, _archive.ExecuteClientResearchAsync, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (TryMatchClientOperation(path, WebApiRoutes.ClientTranscripts, out var transcriptOperation))
        {
            await HandleClientOperationAsync(stream, request, transcriptOperation, _archive.ExecuteClientTranscriptAsync, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (TryMatchClientOperation(path, WebApiRoutes.ClientSpeakers, out var speakerOperation))
        {
            await HandleClientOperationAsync(stream, request, speakerOperation, _archive.ExecuteClientSpeakerAsync, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (TryMatchClientOperation(path, WebApiRoutes.ClientTranscription, out var transcriptionOperation))
        {
            await HandleClientOperationAsync(stream, request, transcriptionOperation, _archive.ExecuteClientTranscriptionAsync, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (TryMatchClientOperation(path, WebApiRoutes.ClientWiki, out var wikiOperation))
        {
            await HandleClientOperationAsync(stream, request, wikiOperation, _archive.ExecuteClientWikiAsync, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.ClientLibraryOverview, StringComparison.OrdinalIgnoreCase))
        {
            await HandleClientLibraryOverviewAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.ClientLibraryBrowse, StringComparison.OrdinalIgnoreCase))
        {
            await HandleClientLibraryBrowseAsync(stream, query, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.ClientLibraryArchivePeriods, StringComparison.OrdinalIgnoreCase))
        {
            await HandleClientLibraryArchivePeriodsAsync(stream, query, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.ClientLibrarySearchFacets, StringComparison.OrdinalIgnoreCase))
        {
            await HandleClientLibrarySearchFacetsAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.ClientLibrarySearchSuggestions, StringComparison.OrdinalIgnoreCase))
        {
            await HandleClientLibrarySearchSuggestionsAsync(stream, query, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (TryMatchClientLibraryBroadcast(path, out var clientLibraryEpisodeId))
        {
            await HandleClientLibraryBroadcastAsync(stream, clientLibraryEpisodeId, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (TryMatchClientBroadcast(path, out var clientDetailsEpisodeId))
        {
            await HandleClientBroadcastDetailsAsync(stream, clientDetailsEpisodeId, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
