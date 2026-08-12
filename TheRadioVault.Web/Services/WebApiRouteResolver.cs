using TheRadioVault.Web.Contracts;

namespace TheRadioVault.Web.Services;

internal enum WebRequestMethod
{
    Get,
    Head,
    Post
}

[Flags]
internal enum WebRequestMethodSet
{
    None = 0,
    Get = 1,
    Head = 2,
    Post = 4,
    Read = Get | Head,
    All = Get | Head | Post
}

internal enum WebApiRouteKind
{
    Unknown,
    ServerInfo,
    Episodes,
    Shows,
    Search,
    Favourites,
    Events,
    Jobs,
    JobCancel,
    OfflineProgress,
    FavouriteMutation,
    ListeningStatusMutation,
    MetadataMutation,
    Transcripts,
    Transcript,
    MomentCreate,
    MomentDelete,
    MomentUpdate,
    BroadcastDetails,
    Research,
    ArchiveHealth,
    Moments
}

internal readonly record struct WebApiRouteMatch(
    WebApiRouteKind Kind,
    WebRequestMethodSet AllowedMethods = WebRequestMethodSet.All,
    string MethodNotAllowedMessage = "",
    long PrimaryId = 0,
    long SecondaryId = 0,
    Guid JobId = default)
{
    public bool Allows(WebRequestMethod method)
        => (AllowedMethods & (method switch
        {
            WebRequestMethod.Get => WebRequestMethodSet.Get,
            WebRequestMethod.Head => WebRequestMethodSet.Head,
            WebRequestMethod.Post => WebRequestMethodSet.Post,
            _ => WebRequestMethodSet.None
        })) != 0;
}

internal static class WebApiRouteResolver
{
    private static readonly IReadOnlyDictionary<string, WebApiRouteMatch> ExactRoutes =
        new Dictionary<string, WebApiRouteMatch>(StringComparer.OrdinalIgnoreCase)
        {
            [WebApiRoutes.ServerInfo] = new(WebApiRouteKind.ServerInfo),
            ["/api/episodes"] = new(WebApiRouteKind.Episodes),
            [WebApiRoutes.Broadcasts] = new(WebApiRouteKind.Episodes),
            ["/api/shows"] = new(WebApiRouteKind.Shows),
            [WebApiRoutes.Shows] = new(WebApiRouteKind.Shows),
            [WebApiRoutes.Search] = new(WebApiRouteKind.Search),
            [WebApiRoutes.Favourites] = new(WebApiRouteKind.Favourites),
            [WebApiRoutes.Events] = new(WebApiRouteKind.Events),
            [WebApiRoutes.Jobs] = new(WebApiRouteKind.Jobs),
            [WebApiRoutes.Transcripts] = ReadOnly(
                WebApiRouteKind.Transcripts,
                "Transcript browsing supports GET and HEAD only."),
            [WebApiRoutes.ArchiveHealth] = new(WebApiRouteKind.ArchiveHealth),
            [WebApiRoutes.MomentsAll] = ReadOnly(
                WebApiRouteKind.Moments,
                "Moment browsing supports GET and HEAD only.")
        };

    public static bool TryParseMethod(string value, out WebRequestMethod method)
    {
        if (value.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            method = WebRequestMethod.Get;
            return true;
        }
        if (value.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            method = WebRequestMethod.Head;
            return true;
        }
        if (value.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            method = WebRequestMethod.Post;
            return true;
        }

        method = default;
        return false;
    }

    public static bool TryResolve(string path, out WebApiRouteMatch match)
    {
        if (ExactRoutes.TryGetValue(path, out match)) return true;
        if (TryResolveJob(path, out match)) return true;
        if (TryResolveMomentUpdate(path, out match)) return true;
        if (TryResolveBroadcast(path, out match)) return true;
        if (TryResolveResearch(path, out match)) return true;

        match = default;
        return false;
    }

    private static bool TryResolveJob(string path, out WebApiRouteMatch match)
    {
        var prefix = WebApiRoutes.Jobs + "/";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var segments = path[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 2 &&
                Guid.TryParse(segments[0], out var jobId) &&
                segments[1].Equals("cancel", StringComparison.OrdinalIgnoreCase))
            {
                match = PostOnly(WebApiRouteKind.JobCancel, "Use POST for this action.") with
                {
                    JobId = jobId
                };
                return true;
            }
        }

        match = default;
        return false;
    }

    private static bool TryResolveMomentUpdate(string path, out WebApiRouteMatch match)
    {
        var prefix = WebApiRoutes.MomentsAll + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            match = default;
            return false;
        }

        var segments = path[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 2 &&
            long.TryParse(segments[0], out var momentId) &&
            segments[1].Equals("update", StringComparison.OrdinalIgnoreCase))
        {
            match = PostOnly(WebApiRouteKind.MomentUpdate, "Moment editing requires POST.") with
            {
                PrimaryId = momentId
            };
            return true;
        }

        match = default;
        return false;
    }

    private static bool TryResolveBroadcast(string path, out WebApiRouteMatch match)
    {
        var prefix = WebApiRoutes.Broadcasts + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            match = default;
            return false;
        }

        var tail = path[prefix.Length..];
        if (!tail.Contains('/') && long.TryParse(tail, out var episodeId))
        {
            match = new WebApiRouteMatch(WebApiRouteKind.BroadcastDetails, PrimaryId: episodeId);
            return true;
        }

        var segments = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 2 && long.TryParse(segments[0], out episodeId))
        {
            if (segments[1].Equals("offline-progress", StringComparison.OrdinalIgnoreCase))
            {
                match = PostOnly(
                        WebApiRouteKind.OfflineProgress,
                        "Offline progress synchronisation requires POST.") with
                    { PrimaryId = episodeId };
                return true;
            }
            if (segments[1].Equals("favourite", StringComparison.OrdinalIgnoreCase))
            {
                match = PostOnly(WebApiRouteKind.FavouriteMutation, "Use POST for this action.") with
                    { PrimaryId = episodeId };
                return true;
            }
            if (segments[1].Equals("listening-status", StringComparison.OrdinalIgnoreCase))
            {
                match = PostOnly(WebApiRouteKind.ListeningStatusMutation, "Use POST for this action.") with
                    { PrimaryId = episodeId };
                return true;
            }
            if (segments[1].Equals("metadata", StringComparison.OrdinalIgnoreCase))
            {
                match = PostOnly(WebApiRouteKind.MetadataMutation, "Metadata updates require POST.") with
                    { PrimaryId = episodeId };
                return true;
            }
            if (segments[1].Equals("transcript", StringComparison.OrdinalIgnoreCase))
            {
                match = ReadOnly(
                        WebApiRouteKind.Transcript,
                        "Transcript access supports GET and HEAD only.") with
                    { PrimaryId = episodeId };
                return true;
            }
            if (segments[1].Equals("moments", StringComparison.OrdinalIgnoreCase))
            {
                match = PostOnly(WebApiRouteKind.MomentCreate, "Moment creation requires POST.") with
                    { PrimaryId = episodeId };
                return true;
            }
        }

        if (segments.Length == 3 &&
            long.TryParse(segments[0], out episodeId) &&
            segments[1].Equals("moments", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(segments[2], out var momentId))
        {
            match = PostOnly(WebApiRouteKind.MomentDelete, "Moment deletion requires POST.") with
            {
                PrimaryId = episodeId,
                SecondaryId = momentId
            };
            return true;
        }

        match = default;
        return false;
    }

    private static bool TryResolveResearch(string path, out WebApiRouteMatch match)
    {
        var prefix = WebApiRoutes.Root + "/research/";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(path[prefix.Length..], out var episodeId))
        {
            match = new WebApiRouteMatch(WebApiRouteKind.Research, PrimaryId: episodeId);
            return true;
        }

        match = default;
        return false;
    }

    private static WebApiRouteMatch ReadOnly(WebApiRouteKind kind, string message)
        => new(kind, WebRequestMethodSet.Read, message);

    private static WebApiRouteMatch PostOnly(WebApiRouteKind kind, string message)
        => new(kind, WebRequestMethodSet.Post, message);

}
