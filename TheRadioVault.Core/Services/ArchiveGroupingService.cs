using TheRadioVault.Core.Domain;

namespace TheRadioVault.Core.Services;

public static class ArchiveGroupingService
{
    public static IReadOnlyList<ArchivePeriodSummary> GroupByYear(IEnumerable<ArchiveEpisode> episodes)
        => episodes
            .Where(e => e.AirDate.HasValue)
            .GroupBy(e => e.AirDate!.Value.Year)
            .OrderByDescending(g => g.Key)
            .Select(g => Build(g.Key, g.Key.ToString(), g))
            .ToList();

    public static IReadOnlyList<ArchivePeriodSummary> GroupByMonth(IEnumerable<ArchiveEpisode> episodes, int year)
        => episodes
            .Where(e => e.AirDate?.Year == year)
            .GroupBy(e => e.AirDate!.Value.Month)
            .OrderBy(g => g.Key)
            .Select(g => Build(g.Key, new DateTime(year, g.Key, 1).ToString("MMMM"), g))
            .ToList();

    private static ArchivePeriodSummary Build(int value, string title, IEnumerable<ArchiveEpisode> source)
    {
        var items = source.ToList();
        var completed = items.Count(e => e.Completed || PlaybackProgressService.GetStatus(e.PositionMs, e.DurationMs) == ListeningStatus.Completed);
        var percent = items.Count == 0 ? 0 : (double)completed / items.Count * 100d;
        var artwork = items.Select(e => e.ArtworkPath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        return new ArchivePeriodSummary(value, title, items.Count, completed, items.Count(e => e.Favourite), percent, artwork);
    }
}
