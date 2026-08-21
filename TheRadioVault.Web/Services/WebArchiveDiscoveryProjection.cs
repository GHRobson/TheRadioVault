using TheRadioVault.Web.Models;

namespace TheRadioVault.Web.Services;

internal sealed record WebArchiveDiscoverySnapshot(
    WebLibrarySummary Library,
    IReadOnlyList<WebShowSummary> Shows,
    IReadOnlyList<int> Years,
    IReadOnlyList<WebEpisode> ContinueListening,
    IReadOnlyList<WebEpisode> Recent,
    IReadOnlyList<WebEpisode> Favourites,
    IReadOnlyList<WebEpisode> OnThisDay,
    IReadOnlyList<WebEpisode> Unheard);

/// <summary>
/// Canonical server projection for every dashboard/discovery consumer. The web
/// shell, paired desktop bootstrap and mobile cache therefore receive the same
/// definition of On This Day, recent, unheard and continuing broadcasts.
/// </summary>
internal static class WebArchiveDiscoveryProjection
{
    public static WebArchiveDiscoverySnapshot Build(
        IReadOnlyList<WebEpisode> episodes,
        int limit,
        DateTime today)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        limit = Math.Clamp(limit, 1, 50);
        var shows = WebEpisodeQuery.GetShows(episodes)
            .Select(value => new WebShowSummary(value.Show, value.Count))
            .ToArray();
        return new WebArchiveDiscoverySnapshot(
            BuildLibrarySummary(episodes, shows.Length),
            shows,
            WebEpisodeQuery.GetYears(episodes),
            WebEpisodeQuery.Apply(episodes, "continue", string.Empty, string.Empty, limit, today),
            WebEpisodeQuery.Apply(episodes, "recent", string.Empty, string.Empty, limit, today),
            WebEpisodeQuery.Apply(episodes, "favorites", string.Empty, string.Empty, limit, today),
            WebEpisodeQuery.Apply(episodes, "onthisday", string.Empty, string.Empty, limit, today),
            WebEpisodeQuery.Apply(
                episodes, "recent", string.Empty, string.Empty,
                null, null, null, "unplayed", limit, today));
    }

    public static WebLibrarySummary BuildLibrarySummary(
        IReadOnlyList<WebEpisode> episodes,
        int showCount)
        => new(
            episodes.Count,
            showCount,
            episodes.Count(value => value.Favourite),
            episodes.Count(value => value.PositionMs > 0 &&
                                    !value.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)),
            episodes.Count(value => value.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)),
            episodes.Select(value => value.AirDate).Max(),
            episodes.Select(value => value.LastPlayedAt).Max());
}
