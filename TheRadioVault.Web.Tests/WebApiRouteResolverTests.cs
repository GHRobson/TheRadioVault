using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Services;
using static TheRadioVault.Web.Tests.TestAssert;

namespace TheRadioVault.Web.Tests;

internal static class WebApiRouteResolverTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases { get; } =
    [
        ("Web API resolver recognises supported methods", ResolverRecognisesSupportedMethods),
        ("Web API resolver preserves legacy list aliases", ResolverPreservesLegacyListAliases),
        ("Web API resolver matches exact routes case-insensitively", ResolverMatchesExactRoutesCaseInsensitively),
        ("Web API resolver centralises read-only method policy", ResolverCentralisesReadOnlyMethodPolicy),
        ("Web API resolver captures broadcast mutations", ResolverCapturesBroadcastMutations),
        ("Web API resolver captures moment and job identifiers", ResolverCapturesMomentAndJobIdentifiers),
        ("Web API resolver captures broadcast and research details", ResolverCapturesDetailIdentifiers),
        ("Web API resolver leaves specialised and malformed routes alone", ResolverLeavesSpecialisedRoutesAlone)
    ];

    private static void ResolverRecognisesSupportedMethods()
    {
        True(WebApiRouteResolver.TryParseMethod("get", out var get));
        Equal(WebRequestMethod.Get, get);
        True(WebApiRouteResolver.TryParseMethod("HEAD", out var head));
        Equal(WebRequestMethod.Head, head);
        True(WebApiRouteResolver.TryParseMethod("Post", out var post));
        Equal(WebRequestMethod.Post, post);
        True(!WebApiRouteResolver.TryParseMethod("PUT", out _));
    }

    private static void ResolverPreservesLegacyListAliases()
    {
        Equal(WebApiRouteKind.Episodes, Resolve("/api/episodes").Kind);
        Equal(WebApiRouteKind.Episodes, Resolve(WebApiRoutes.Broadcasts).Kind);
        Equal(WebApiRouteKind.Shows, Resolve("/api/shows").Kind);
        Equal(WebApiRouteKind.Shows, Resolve(WebApiRoutes.Shows).Kind);
    }

    private static void ResolverMatchesExactRoutesCaseInsensitively()
    {
        Equal(WebApiRouteKind.ServerInfo, Resolve("/API/V1/SERVER-INFO").Kind);
        Equal(WebApiRouteKind.ArchiveHealth, Resolve("/API/V1/ARCHIVE-HEALTH").Kind);
        Equal(WebApiRouteKind.Moments, Resolve("/API/V1/MOMENTS").Kind);
    }

    private static void ResolverCentralisesReadOnlyMethodPolicy()
    {
        var transcripts = Resolve(WebApiRoutes.Transcripts);
        True(transcripts.Allows(WebRequestMethod.Get));
        True(transcripts.Allows(WebRequestMethod.Head));
        True(!transcripts.Allows(WebRequestMethod.Post));
        Equal("Transcript browsing supports GET and HEAD only.", transcripts.MethodNotAllowedMessage);

        var moments = Resolve(WebApiRoutes.MomentsAll);
        True(moments.Allows(WebRequestMethod.Get));
        True(moments.Allows(WebRequestMethod.Head));
        True(!moments.Allows(WebRequestMethod.Post));
    }

    private static void ResolverCapturesBroadcastMutations()
    {
        var offline = Resolve(WebApiRoutes.OfflineProgress(42));
        Equal(WebApiRouteKind.OfflineProgress, offline.Kind);
        Equal(42L, offline.PrimaryId);
        True(offline.Allows(WebRequestMethod.Post));
        True(!offline.Allows(WebRequestMethod.Get));

        var favourite = Resolve(WebApiRoutes.Favourite(84).ToUpperInvariant());
        Equal(WebApiRouteKind.FavouriteMutation, favourite.Kind);
        Equal(84L, favourite.PrimaryId);

        var transcript = Resolve(WebApiRoutes.Transcript(126));
        Equal(WebApiRouteKind.Transcript, transcript.Kind);
        True(transcript.Allows(WebRequestMethod.Head));
        True(!transcript.Allows(WebRequestMethod.Post));
    }

    private static void ResolverCapturesMomentAndJobIdentifiers()
    {
        var delete = Resolve(WebApiRoutes.Moment(7, 19));
        Equal(WebApiRouteKind.MomentDelete, delete.Kind);
        Equal(7L, delete.PrimaryId);
        Equal(19L, delete.SecondaryId);

        var update = Resolve(WebApiRoutes.MomentUpdate(23));
        Equal(WebApiRouteKind.MomentUpdate, update.Kind);
        Equal(23L, update.PrimaryId);

        var jobId = Guid.NewGuid();
        var cancel = Resolve(WebApiRoutes.JobCancel(jobId));
        Equal(WebApiRouteKind.JobCancel, cancel.Kind);
        Equal(jobId, cancel.JobId);
        True(cancel.Allows(WebRequestMethod.Post));
        True(!cancel.Allows(WebRequestMethod.Head));
    }

    private static void ResolverCapturesDetailIdentifiers()
    {
        var broadcast = Resolve(WebApiRoutes.Broadcast(71));
        Equal(WebApiRouteKind.BroadcastDetails, broadcast.Kind);
        Equal(71L, broadcast.PrimaryId);

        var research = Resolve(WebApiRoutes.Research(72));
        Equal(WebApiRouteKind.Research, research.Kind);
        Equal(72L, research.PrimaryId);
    }

    private static void ResolverLeavesSpecialisedRoutesAlone()
    {
        NotResolved(WebApiRoutes.MediaManifest(9));
        NotResolved(WebApiRoutes.MediaPart(9, 3));
        NotResolved(WebApiRoutes.PlayerTransferBegin);
        NotResolved(WebApiRoutes.ClientLibraryOverview);
        NotResolved(WebApiRoutes.Broadcasts + "/9/not-an-action");
        NotResolved(WebApiRoutes.Research(9) + "/extra");
        NotResolved(WebApiRoutes.Broadcast(9) + "/");
    }

    private static WebApiRouteMatch Resolve(string path)
    {
        True(WebApiRouteResolver.TryResolve(path, out var route), $"Expected route to resolve: {path}");
        return route;
    }

    private static void NotResolved(string path)
        => True(!WebApiRouteResolver.TryResolve(path, out _), $"Expected route not to resolve: {path}");
}
