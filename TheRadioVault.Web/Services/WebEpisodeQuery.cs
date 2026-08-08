using TheRadioVault.Web.Models;

namespace TheRadioVault.Web.Services;

public sealed record WebEpisodePage(
    IReadOnlyList<WebEpisode> Episodes,
    int Total,
    int Offset,
    int Limit)
{
    public bool HasMore => Offset + Episodes.Count < Total;
}

public static class WebEpisodeQuery
{
    public static IReadOnlyList<WebEpisode> Apply(
        IEnumerable<WebEpisode> source,
        string view,
        string search,
        string show,
        int limit,
        DateTime today)
        => Apply(source, view, search, show, null, null, null, string.Empty, limit, today);

    /// <summary>
    /// Applies the bounded, server-side canonical-library filters used by the
    /// Anywhere web app. Keeping these filters on the server
    /// avoids downloading the full archive merely to navigate by date/status.
    /// </summary>
    public static IReadOnlyList<WebEpisode> Apply(
        IEnumerable<WebEpisode> source,
        string view,
        string search,
        string show,
        int? year,
        int? month,
        DateTime? exactDate,
        string status,
        int limit,
        DateTime today)
        => BuildQuery(source, view, search, show, year, month, exactDate, status, today)
            .Take(Math.Clamp(limit, 1, 500))
            .ToArray();

    /// <summary>
    /// Returns one deterministic page plus the full filtered count. The web
    /// client can therefore render a small first page instead of constructing
    /// hundreds of broadcast cards on a phone in one frame.
    /// </summary>
    public static WebEpisodePage ApplyPage(
        IEnumerable<WebEpisode> source,
        string view,
        string search,
        string show,
        int? year,
        int? month,
        DateTime? exactDate,
        string status,
        int offset,
        int limit,
        DateTime today)
    {
        var filtered = BuildQuery(source, view, search, show, year, month, exactDate, status, today).ToArray();
        var safeOffset = Math.Clamp(offset, 0, filtered.Length);
        var safeLimit = Math.Clamp(limit, 1, 150);
        return new WebEpisodePage(
            filtered.Skip(safeOffset).Take(safeLimit).ToArray(),
            filtered.Length,
            safeOffset,
            safeLimit);
    }

    private static IEnumerable<WebEpisode> BuildQuery(
        IEnumerable<WebEpisode> source,
        string view,
        string search,
        string show,
        int? year,
        int? month,
        DateTime? exactDate,
        string status,
        DateTime today)
    {
        ArgumentNullException.ThrowIfNull(source);
        IEnumerable<WebEpisode> episodes = view.Trim().ToLowerInvariant() switch
        {
            "library" => source.OrderByDescending(x => x.AirDate ?? DateTime.MinValue)
                .ThenByDescending(x => x.DateAdded),
            "continue" => source.Where(x => x.PositionMs > 0 && !x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.LastPlayedAt ?? DateTime.MinValue),
            "favorites" => source.Where(x => x.Favourite).OrderByDescending(x => x.AirDate),
            "onthisday" => source.Where(x => x.AirDate.HasValue && x.AirDate.Value.Month == today.Month && x.AirDate.Value.Day == today.Day)
                .OrderByDescending(x => x.AirDate),
            _ => source.OrderByDescending(x => x.DateAdded).ThenByDescending(x => x.AirDate)
        };

        if (!string.IsNullOrWhiteSpace(show))
            episodes = episodes.Where(x => x.Show.Equals(show.Trim(), StringComparison.OrdinalIgnoreCase));

        if (year is >= 1900 and <= 9999)
            episodes = episodes.Where(x => x.AirDate?.Year == year.Value);

        if (month is >= 1 and <= 12)
            episodes = episodes.Where(x => x.AirDate?.Month == month.Value);

        if (exactDate.HasValue)
            episodes = episodes.Where(x => x.AirDate?.Date == exactDate.Value.Date);

        episodes = status.Trim().ToLowerInvariant() switch
        {
            "unplayed" => episodes.Where(x => x.PositionMs <= 0 && !x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)),
            "inprogress" => episodes.Where(x => x.PositionMs > 0 && !x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)),
            "completed" => episodes.Where(x => x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)),
            _ => episodes
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            episodes = episodes.Where(x => x.Show.Contains(term, StringComparison.OrdinalIgnoreCase)
                || x.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                || x.Summary.Contains(term, StringComparison.OrdinalIgnoreCase)
                || x.PeopleSearchText.Contains(term, StringComparison.OrdinalIgnoreCase)
                || x.TopicSearchText.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (x.AirDate?.ToString("yyyy-MM-dd") ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        return episodes;
    }

    public static IReadOnlyList<(string Show, int Count)> GetShows(IEnumerable<WebEpisode> source)
        => source.Where(x => !string.IsNullOrWhiteSpace(x.Show))
            .GroupBy(x => x.Show.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(x => (Show: x.First().Show.Trim(), Count: x.Count()))
            .OrderBy(x => x.Show, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<int> GetYears(IEnumerable<WebEpisode> source)
        => source.Where(x => x.AirDate.HasValue)
            .Select(x => x.AirDate!.Value.Year)
            .Distinct()
            .OrderByDescending(x => x)
            .ToArray();
}
