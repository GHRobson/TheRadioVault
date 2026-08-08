using System.Text;
using TheRadioVault.Models;

namespace TheRadioVault.Services;

public sealed class MetadataCleanupService
{
    private static readonly string[] GenericSummaryFragments =
    {
        "No sufficiently reliable public segment index was recovered",
        "the entry is intentionally conservative",
        "The surviving research index does not provide a reliable segment-by-segment rundown",
        "guests and individual topics remain unassigned"
    };

    private static readonly Dictionary<string, string> StationNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sirius XM satellite radio"] = "SiriusXM",
        ["SiriusXM satellite radio"] = "SiriusXM",
        ["XM Satellite Radio"] = "XM Satellite Radio",
        ["SiriusXM Raw Dog Comedy Hits 99"] = "SiriusXM Raw Dog Comedy Hits 99",
        ["WNEW-FM New York"] = "WNEW-FM",
        ["WJFK evening show"] = "WJFK-FM"
    };

    public MetadataCleanupResult Clean(MetadataPackage source)
    {
        var package = Clone(source);
        var report = new MetadataCleanupReport { TotalEpisodes = package.Episodes.Count };

        foreach (var episode in package.Episodes)
            CleanEpisode(episode, report);

        report.ChangedEpisodes = report.Changes.Select(x => x.EpisodeKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new MetadataCleanupResult(package, report);
    }

    private static void CleanEpisode(MetadataPackageEpisode episode, MetadataCleanupReport report)
    {
        var key = EpisodeKey(episode);
        episode.Broadcast ??= new MetadataBroadcastFields();
        episode.People ??= new MetadataPeopleFields();
        episode.Research ??= new MetadataResearchFields();
        episode.Archive ??= new MetadataArchiveFields();
        episode.Moments ??= new List<MetadataPackageMoment>();

        episode.People.Hosts = NormalizeArray(episode.People.Hosts, key, "Hosts", report);
        episode.People.Guests = NormalizeGuests(episode.People.Guests, episode.Collection, key, report);
        episode.People.Callers = NormalizeArray(episode.People.Callers, key, "Callers", report);
        episode.People.MentionedPeople = NormalizeArray(episode.People.MentionedPeople, key, "Mentioned people", report);
        episode.Topics = NormalizeArray(episode.Topics, key, "Topics", report);
        episode.Research.Sources = NormalizeArray(episode.Research.Sources, key, "Research sources", report);

        MoveOverloadedStationValue(episode, key, report);
        NormalizeStation(episode, key, report);
        RemoveTechnicalSummaryLines(episode, key, report);
        RemoveGenericSummary(episode, key, report);
        NormalizeHeadline(episode, key, report);
        RemoveKnownGenericHeadline(episode, key, report);
        InferEpisodeType(episode, key, report);
        InferEra(episode, key, report);
    }

    private static void MoveOverloadedStationValue(MetadataPackageEpisode episode, string key, MetadataCleanupReport report)
    {
        var station = episode.Broadcast.Station.Trim();
        if (station.Length == 0) return;

        if (station.Equals("Weekday broadcast", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(episode.Broadcast.EpisodeType))
            {
                episode.Broadcast.EpisodeType = "Regular broadcast";
                report.Add(key, "Episode type", "", "Regular broadcast", $"Moved overloaded value '{station}' out of Broadcast station.");
            }
            episode.Broadcast.Station = "";
        }
        else if (station.Equals("Friday-era broadcast", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(episode.Broadcast.Era))
            {
                episode.Broadcast.Era = "Friday-only era";
                report.Add(key, "Era", "", "Friday-only era", $"Moved overloaded value '{station}' out of Broadcast station.");
            }
            episode.Broadcast.Station = "";
        }
        else if (station.Equals("Daily-series premiere", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(episode.Broadcast.EpisodeType))
            {
                episode.Broadcast.EpisodeType = "Series premiere";
                report.Add(key, "Episode type", "", "Series premiere", $"Moved overloaded value '{station}' out of Broadcast station.");
            }
            episode.Broadcast.Station = "";
        }
        else if (station.Equals("OpieRadio Edition", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(episode.Broadcast.Slot))
            {
                episode.Broadcast.Slot = "OpieRadio Edition";
                report.Add(key, "Broadcast slot", "", "OpieRadio Edition", $"Moved overloaded value '{station}' out of Broadcast station as a separate same-day programme.");
            }
            episode.Broadcast.Station = "";
        }
        else if (station.Contains("evening show", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(episode.Broadcast.Slot))
            {
                episode.Broadcast.Slot = "Evening";
                report.Add(key, "Broadcast slot", "", "Evening", "Moved a scheduling description out of Station.");
            }
        }
    }

    private static void NormalizeStation(MetadataPackageEpisode episode, string key, MetadataCleanupReport report)
    {
        var old = episode.Broadcast.Station.Trim();
        if (old.Length == 0) return;
        if (!StationNames.TryGetValue(old, out var normalized) || old == normalized) return;
        episode.Broadcast.Station = normalized;
        report.Add(key, "Station", old, normalized, "Standardised station/network naming.");
    }

    private static void RemoveTechnicalSummaryLines(MetadataPackageEpisode episode, string key, MetadataCleanupReport report)
    {
        if (string.IsNullOrWhiteSpace(episode.Summary)) return;
        var lines = episode.Summary.Replace("\r\n", "\n").Split('\n');
        var kept = lines.Where(line =>
            !line.TrimStart().StartsWith("Broadcast ID:", StringComparison.OrdinalIgnoreCase) &&
            !line.TrimStart().StartsWith("Original filename:", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("Managed by The Radio Vault", StringComparison.OrdinalIgnoreCase)).ToArray();
        var cleaned = string.Join(Environment.NewLine, kept).Trim();
        if (cleaned == episode.Summary.Trim()) return;
        var old = episode.Summary;
        episode.Summary = cleaned;
        report.Add(key, "Summary", old, cleaned, "Removed internal archive/technical lines from the listener-facing summary.");
    }

    private static void RemoveGenericSummary(MetadataPackageEpisode episode, string key, MetadataCleanupReport report)
    {
        if (string.IsNullOrWhiteSpace(episode.Summary)) return;
        var hits = GenericSummaryFragments.Count(fragment => episode.Summary.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        if (hits == 0) return;

        var old = episode.Summary;
        episode.Summary = "";
        if (string.IsNullOrWhiteSpace(episode.Research.ConfidenceReason))
            episode.Research.ConfidenceReason = "No reliable episode-specific rundown was available when this record was researched.";
        report.Add(key, "Summary", old, "", "Removed repeated research boilerplate that did not describe this specific episode.");
        report.GenericSummariesRemoved++;
    }

    private static void NormalizeHeadline(MetadataPackageEpisode episode, string key, MetadataCleanupReport report)
    {
        if (string.IsNullOrWhiteSpace(episode.Headline)) return;
        var expectedPrefix = episode.Collection + " — ";
        if (!episode.Headline.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)) return;
        if (episode.AirDate is null || !DateTime.TryParse(episode.AirDate, out var date)) return;
        var generic = expectedPrefix + date.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        if (!episode.Headline.Equals(generic, StringComparison.OrdinalIgnoreCase)) return;
        var old = episode.Headline;
        episode.Headline = "";
        report.Add(key, "Headline", old, "", "Removed a title that only repeated the show name and date.");
        report.GenericHeadlinesRemoved++;
    }


    private static void RemoveKnownGenericHeadline(MetadataPackageEpisode episode, string key, MetadataCleanupReport report)
    {
        if (string.IsNullOrWhiteSpace(episode.Headline)) return;

        var headline = episode.Headline.Trim();
        if (!headline.Equals("Faction Talk archive broadcast", StringComparison.OrdinalIgnoreCase)) return;

        episode.Headline = "";
        report.Add(key, "Headline", headline, "", "Removed a generic archive label that did not describe this specific broadcast.");
        report.GenericHeadlinesRemoved++;
    }

    private static string[] NormalizeGuests(string[]? values, string collection, string key, MetadataCleanupReport report)
    {
        values ??= Array.Empty<string>();
        var normalized = values.Select(x => (x ?? "").Trim())
            .Where(x => x.Length > 0)
            .Where(x => !IsProgrammeNameUsedAsGuest(x, collection))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (values.SequenceEqual(normalized, StringComparer.Ordinal)) return normalized;

        var removedProgrammeName = values.Any(x => IsProgrammeNameUsedAsGuest((x ?? "").Trim(), collection));
        report.Add(
            key,
            "Guests",
            string.Join("; ", values),
            string.Join("; ", normalized),
            removedProgrammeName
                ? "Removed the programme name from the guest list, then trimmed and deduplicated the remaining guests."
                : "Trimmed, deduplicated and sorted values.");
        report.ArraysNormalized++;
        return normalized;
    }

    private static bool IsProgrammeNameUsedAsGuest(string value, string collection)
    {
        if (value.Length == 0) return false;
        if (value.Equals(collection?.Trim(), StringComparison.OrdinalIgnoreCase)) return true;

        return string.Equals(collection, "Bennington", StringComparison.OrdinalIgnoreCase)
            && value.Equals("Bennington", StringComparison.OrdinalIgnoreCase);
    }

    private static void InferEpisodeType(MetadataPackageEpisode episode, string key, MetadataCleanupReport report)
    {
        if (!string.IsNullOrWhiteSpace(episode.Broadcast.EpisodeType)) return;
        var text = $"{episode.Headline} {episode.OriginalFilename}";
        string? value = null;
        if (text.Contains("best of", StringComparison.OrdinalIgnoreCase)) value = "Best of";
        else if (text.Contains("special", StringComparison.OrdinalIgnoreCase)) value = "Special";
        else if (text.Contains("premiere", StringComparison.OrdinalIgnoreCase) || text.Contains("first daily", StringComparison.OrdinalIgnoreCase)) value = "Series premiere";
        else if (text.Contains("second archive recording", StringComparison.OrdinalIgnoreCase) || episode.PartNumber > 1) value = "Alternate recording";
        if (value is null) return;
        episode.Broadcast.EpisodeType = value;
        report.Add(key, "Episode type", "", value, "Inferred from the existing headline or filename.");
    }

    private static void InferEra(MetadataPackageEpisode episode, string key, MetadataCleanupReport report)
    {
        if (!string.IsNullOrWhiteSpace(episode.Broadcast.Era)) return;
        if (!episode.Collection.Equals("Bennington", StringComparison.OrdinalIgnoreCase) || episode.AirDate is null || !DateTime.TryParse(episode.AirDate, out var date)) return;
        var value = date < new DateTime(2015, 4, 20) ? "Friday-only era" : "Daily Bennington era";
        episode.Broadcast.Era = value;
        report.Add(key, "Era", "", value, "Inferred from the programme and broadcast date.");
    }

    private static string[] NormalizeArray(string[]? values, string key, string field, MetadataCleanupReport report)
    {
        values ??= Array.Empty<string>();
        var normalized = values.Select(x => (x ?? "").Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (values.SequenceEqual(normalized, StringComparer.Ordinal)) return normalized;
        report.Add(key, field, string.Join("; ", values), string.Join("; ", normalized), "Trimmed, deduplicated and sorted values.");
        report.ArraysNormalized++;
        return normalized;
    }

    private static string EpisodeKey(MetadataPackageEpisode e)
        => !string.IsNullOrWhiteSpace(e.BroadcastUid) ? e.BroadcastUid : $"{e.Collection} · {e.AirDate ?? "Unknown date"} · {e.OriginalFilename}";

    private static MetadataPackage Clone(MetadataPackage source)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(source);
        return System.Text.Json.JsonSerializer.Deserialize<MetadataPackage>(json) ?? new MetadataPackage();
    }
}

public sealed record MetadataCleanupResult(MetadataPackage Package, MetadataCleanupReport Report);

public sealed class MetadataCleanupReport
{
    public int TotalEpisodes { get; set; }
    public int ChangedEpisodes { get; set; }
    public int GenericSummariesRemoved { get; set; }
    public int GenericHeadlinesRemoved { get; set; }
    public int ArraysNormalized { get; set; }
    public List<MetadataCleanupChange> Changes { get; } = new();

    public void Add(string episodeKey, string field, string before, string after, string reason)
        => Changes.Add(new MetadataCleanupChange(episodeKey, field, before, after, reason));

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Radio Vault metadata cleanup audit");
        sb.AppendLine($"Generated: {DateTime.Now:F}");
        sb.AppendLine();
        sb.AppendLine($"Episodes examined: {TotalEpisodes:N0}");
        sb.AppendLine($"Episodes changed: {ChangedEpisodes:N0}");
        sb.AppendLine($"Field changes: {Changes.Count:N0}");
        sb.AppendLine($"Generic summaries removed: {GenericSummariesRemoved:N0}");
        sb.AppendLine($"Generic date-only headlines removed: {GenericHeadlinesRemoved:N0}");
        sb.AppendLine($"People/topic/source arrays normalised: {ArraysNormalized:N0}");
        sb.AppendLine();
        foreach (var change in Changes)
        {
            sb.AppendLine(change.EpisodeKey);
            sb.AppendLine($"  {change.Field}: {change.Reason}");
            sb.AppendLine($"  Before: {Compact(change.Before)}");
            sb.AppendLine($"  After:  {Compact(change.After)}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Compact(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(empty)";
        var compact = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return compact.Length <= 240 ? compact : compact[..237] + "...";
    }
}

public sealed record MetadataCleanupChange(string EpisodeKey, string Field, string Before, string After, string Reason);
