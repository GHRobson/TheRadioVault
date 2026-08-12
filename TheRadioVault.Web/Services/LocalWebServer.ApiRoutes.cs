namespace TheRadioVault.Web.Services;

public sealed partial class LocalWebServer
{
    private async Task<bool> TryHandleAuthorizedRouteAsync(
        Stream stream,
        string path,
        IReadOnlyDictionary<string, string> query,
        HttpRequest request,
        WebRequestMethod method,
        CancellationToken cancellationToken)
    {
        var isGet = method == WebRequestMethod.Get;
        var isHead = method == WebRequestMethod.Head;
        var isPost = method == WebRequestMethod.Post;
        var hasGeneralRoute = WebApiRouteResolver.TryResolve(path, out var generalRoute);

        // Preserve the existing priority of the server-info route over
        // the larger authenticated route groups.
        if (hasGeneralRoute && generalRoute.Kind == WebApiRouteKind.ServerInfo)
        {
            await DispatchGeneralApiRouteAsync(
                    stream, query, request, method, generalRoute, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        if (await TryHandleFederationAdministrationRouteAsync(
                stream,
                path,
                query,
                request,
                isGet,
                isHead,
                isPost,
                cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (await TryHandleClientRouteAsync(
                stream, path, query, request, isHead, cancellationToken)
            .ConfigureAwait(false))
        {
            return true;
        }

        if (hasGeneralRoute)
        {
            await DispatchGeneralApiRouteAsync(
                    stream, query, request, method, generalRoute, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        if (await TryHandleCanonicalMediaRouteAsync(
                stream, path, query, request, isHead, cancellationToken)
            .ConfigureAwait(false))
        {
            return true;
        }

        if (await TryHandlePlaybackQueueRouteAsync(
                stream, path, request, isHead, isPost, cancellationToken)
            .ConfigureAwait(false))
        {
            return true;
        }

        return await TryHandleArtworkAudioRouteAsync(
                stream, path, request, isHead, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DispatchGeneralApiRouteAsync(
        Stream stream,
        IReadOnlyDictionary<string, string> query,
        HttpRequest request,
        WebRequestMethod method,
        WebApiRouteMatch route,
        CancellationToken cancellationToken)
    {
        if (!route.Allows(method))
        {
            await WriteTextResponseAsync(
                    stream,
                    405,
                    "Method Not Allowed",
                    route.MethodNotAllowedMessage,
                    "text/plain; charset=utf-8",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var headOnly = method == WebRequestMethod.Head;
        switch (route.Kind)
        {
            case WebApiRouteKind.ServerInfo:
                await HandleServerInfoApiAsync(stream, headOnly, cancellationToken).ConfigureAwait(false);
                break;
            case WebApiRouteKind.Episodes:
                await HandleEpisodesApiAsync(stream, query, headOnly, cancellationToken).ConfigureAwait(false);
                break;
            case WebApiRouteKind.Shows:
                await HandleShowsApiAsync(stream, headOnly, cancellationToken).ConfigureAwait(false);
                break;
            case WebApiRouteKind.Search:
                await HandleEpisodesApiAsync(
                        stream, query, headOnly, cancellationToken, forceSearchView: true)
                    .ConfigureAwait(false);
                break;
            case WebApiRouteKind.Favourites:
                await HandleEpisodesApiAsync(
                        stream, query, headOnly, cancellationToken, forcedView: "favorites")
                    .ConfigureAwait(false);
                break;
            case WebApiRouteKind.Events:
                await HandleEventsApiAsync(stream, query, headOnly, cancellationToken).ConfigureAwait(false);
                break;
            case WebApiRouteKind.Jobs:
                await HandleJobsApiAsync(stream, headOnly, cancellationToken).ConfigureAwait(false);
                break;
            case WebApiRouteKind.JobCancel:
                await HandleJobCancellationAsync(stream, route.JobId, cancellationToken).ConfigureAwait(false);
                break;
            case WebApiRouteKind.OfflineProgress:
                await HandleOfflineProgressAsync(
                        stream, request, route.PrimaryId, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case WebApiRouteKind.FavouriteMutation:
                await HandleFavouriteMutationAsync(
                        stream, route.PrimaryId, request, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case WebApiRouteKind.ListeningStatusMutation:
                await HandleListeningStatusMutationAsync(
                        stream, route.PrimaryId, request, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case WebApiRouteKind.MetadataMutation:
                await HandleBroadcastMetadataMutationAsync(
                        stream, route.PrimaryId, request, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case WebApiRouteKind.Transcripts:
                await HandleTranscriptsApiAsync(stream, headOnly, cancellationToken).ConfigureAwait(false);
                break;
            case WebApiRouteKind.Transcript:
                await HandleTranscriptApiAsync(
                        stream, route.PrimaryId, headOnly, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case WebApiRouteKind.MomentCreate:
                await HandleMomentCreateAsync(
                        stream, route.PrimaryId, request, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case WebApiRouteKind.MomentDelete:
                await HandleMomentDeleteAsync(
                        stream, route.PrimaryId, route.SecondaryId, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case WebApiRouteKind.MomentUpdate:
                await HandleMomentUpdateAsync(
                        stream, route.PrimaryId, request, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case WebApiRouteKind.BroadcastDetails:
                await HandleBroadcastDetailsApiAsync(
                        stream, route.PrimaryId, headOnly, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case WebApiRouteKind.Research:
                await HandleResearchApiAsync(
                        stream, route.PrimaryId, headOnly, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case WebApiRouteKind.ArchiveHealth:
                await HandleArchiveHealthApiAsync(stream, headOnly, cancellationToken).ConfigureAwait(false);
                break;
            case WebApiRouteKind.Moments:
                await HandleMomentsApiAsync(stream, headOnly, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unsupported Web API route kind: {route.Kind}.");
        }
    }
}
