using Microsoft.Data.Sqlite;
using TheRadioVault.Models;

namespace TheRadioVault.Services;

public sealed partial class DatabaseService
{
    public ExploreSnapshot GetExploreSnapshot(int limitPerSection = 12)
    {
        var limit = Math.Clamp(limitPerSection, 4, 40);
        using var connection = OpenConnection();
        return new ExploreSnapshot
        {
            Shows = ReadShowFacets(connection, limit),
            Years = ReadYearFacets(connection, limit),
            People = ReadPeopleFacets(connection, limit),
            Topics = ReadTopicFacets(connection, limit),
            Stations = ReadStationFacets(connection, limit),
            Sources = ReadSourceFacets(connection, limit)
        };
    }

    private static IReadOnlyList<ExploreFacetItem> ReadShowFacets(SqliteConnection connection, int limit)
    {
        var result = new List<ExploreFacetItem>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.name,
                   (SELECT COUNT(*) FROM episodes e WHERE e.collection_id=c.id AND COALESCE(e.hidden,0)=0) AS audio_count,
                   (SELECT COUNT(*) FROM research_broadcasts rb WHERE rb.collection_id=c.id AND rb.episode_id IS NULL) AS missing_count,
                   (SELECT COUNT(*) FROM research_broadcasts rb WHERE rb.collection_id=c.id) AS research_count
            FROM collections c
            WHERE EXISTS(SELECT 1 FROM episodes e WHERE e.collection_id=c.id AND COALESCE(e.hidden,0)=0)
               OR EXISTS(SELECT 1 FROM research_broadcasts rb WHERE rb.collection_id=c.id)
            ORDER BY audio_count + missing_count DESC,c.sort_name,c.name
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var audio = Convert.ToInt32(reader.GetInt64(1));
            var missing = Convert.ToInt32(reader.GetInt64(2));
            var research = Convert.ToInt32(reader.GetInt64(3));
            result.Add(new ExploreFacetItem
            {
                Kind = ExploreFacetKind.Show,
                Value = reader.GetString(0),
                SearchText = reader.GetString(0),
                Count = audio + missing,
                Detail = $"{audio:N0} in library · {missing:N0} missing · {research:N0} research records"
            });
        }
        return result;
    }

    private static IReadOnlyList<ExploreFacetItem> ReadYearFacets(SqliteConnection connection, int limit)
    {
        var result = new List<ExploreFacetItem>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH year_counts AS (
                SELECT substr(e.air_date,1,4) AS value,COUNT(*) AS audio_count,0 AS missing_count
                FROM episodes e
                WHERE COALESCE(e.hidden,0)=0 AND length(e.air_date)>=4
                GROUP BY substr(e.air_date,1,4)
                UNION ALL
                SELECT substr(rb.air_date,1,4),0,COUNT(*)
                FROM research_broadcasts rb
                WHERE rb.episode_id IS NULL AND length(rb.air_date)>=4
                GROUP BY substr(rb.air_date,1,4)
            )
            SELECT value,SUM(audio_count),SUM(missing_count)
            FROM year_counts
            WHERE value GLOB '[12][0-9][0-9][0-9]'
            GROUP BY value
            ORDER BY value DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var audio = Convert.ToInt32(reader.GetInt64(1));
            var missing = Convert.ToInt32(reader.GetInt64(2));
            result.Add(new ExploreFacetItem
            {
                Kind = ExploreFacetKind.Year,
                Value = reader.GetString(0),
                SearchText = reader.GetString(0),
                Count = audio + missing,
                Detail = missing > 0 ? $"{audio:N0} recordings · {missing:N0} researched gaps" : $"{audio:N0} recordings"
            });
        }
        return result;
    }

    private static IReadOnlyList<ExploreFacetItem> ReadPeopleFacets(SqliteConnection connection, int limit)
    {
        var counts = new Dictionary<string, FacetAccumulator>(StringComparer.OrdinalIgnoreCase);

        using (var guests = connection.CreateCommand())
        {
            guests.CommandText = """
                SELECT g.name,COUNT(DISTINCT eg.episode_id)
                FROM guests g JOIN episode_guests eg ON eg.guest_id=g.id
                JOIN episodes e ON e.id=eg.episode_id
                WHERE COALESCE(e.hidden,0)=0 AND trim(g.name)<>''
                GROUP BY g.name;
                """;
            AddCountRows(guests, counts);
        }

        using (var episodePeople = connection.CreateCommand())
        {
            episodePeople.CommandText = "SELECT hosts,callers,mentioned_people FROM episodes WHERE COALESCE(hidden,0)=0";
            using var reader = episodePeople.ExecuteReader();
            while (reader.Read())
            {
                var perBroadcast = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < 3; i++)
                {
                    if (reader.IsDBNull(i)) continue;
                    foreach (var parsedName in SplitFacetValues(reader.GetString(i))) perBroadcast.Add(parsedName);
                }
                foreach (var personName in perBroadcast) AddCount(counts, personName, 1);
            }
        }

        using (var researchPeople = connection.CreateCommand())
        {
            researchPeople.CommandText = """
                SELECT rp.name,COUNT(DISTINCT rp.research_broadcast_id)
                FROM research_people rp
                JOIN research_broadcasts rb ON rb.id=rp.research_broadcast_id
                WHERE rb.episode_id IS NULL AND trim(rp.name)<>''
                GROUP BY rp.name;
                """;
            AddCountRows(researchPeople, counts);
        }

        return ToFacetItems(counts, ExploreFacetKind.Person, limit, count => $"{count:N0} appearances or references");
    }

    private static IReadOnlyList<ExploreFacetItem> ReadTopicFacets(SqliteConnection connection, int limit)
    {
        var counts = new Dictionary<string, FacetAccumulator>(StringComparer.OrdinalIgnoreCase);
        using (var episodeTopics = connection.CreateCommand())
        {
            episodeTopics.CommandText = """
                SELECT t.name,COUNT(DISTINCT et.episode_id)
                FROM tags t JOIN episode_tags et ON et.tag_id=t.id
                JOIN episodes e ON e.id=et.episode_id
                WHERE COALESCE(e.hidden,0)=0 AND trim(t.name)<>''
                GROUP BY t.name;
                """;
            AddCountRows(episodeTopics, counts);
        }
        using (var researchTopics = connection.CreateCommand())
        {
            researchTopics.CommandText = """
                SELECT rt.topic,COUNT(DISTINCT rt.research_broadcast_id)
                FROM research_topics rt
                JOIN research_broadcasts rb ON rb.id=rt.research_broadcast_id
                WHERE rb.episode_id IS NULL AND trim(rt.topic)<>''
                GROUP BY rt.topic;
                """;
            AddCountRows(researchTopics, counts);
        }
        return ToFacetItems(counts, ExploreFacetKind.Topic, limit, count => $"{count:N0} tagged broadcasts");
    }

    private static IReadOnlyList<ExploreFacetItem> ReadStationFacets(SqliteConnection connection, int limit)
    {
        var counts = new Dictionary<string, FacetAccumulator>(StringComparer.OrdinalIgnoreCase);
        using (var episodeStations = connection.CreateCommand())
        {
            episodeStations.CommandText = """
                SELECT trim(edition),COUNT(*)
                FROM episodes
                WHERE COALESCE(hidden,0)=0 AND trim(COALESCE(edition,''))<>''
                GROUP BY trim(edition);
                """;
            AddCountRows(episodeStations, counts);
        }
        using (var researchStations = connection.CreateCommand())
        {
            researchStations.CommandText = """
                SELECT trim(station),COUNT(*)
                FROM research_broadcasts
                WHERE episode_id IS NULL AND trim(station)<>''
                GROUP BY trim(station);
                """;
            AddCountRows(researchStations, counts);
        }
        return ToFacetItems(counts, ExploreFacetKind.Station, limit, count => $"{count:N0} broadcasts");
    }

    private static IReadOnlyList<ExploreFacetItem> ReadSourceFacets(SqliteConnection connection, int limit)
    {
        var result = new List<ExploreFacetItem>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE
                       WHEN trim(publisher)<>'' THEN trim(publisher)
                       WHEN instr(replace(replace(url,'https://',''),'http://',''),'/')>0
                         THEN substr(replace(replace(url,'https://',''),'http://',''),1,instr(replace(replace(url,'https://',''),'http://',''),'/')-1)
                       ELSE replace(replace(url,'https://',''),'http://','')
                   END AS source_name,
                   COUNT(DISTINCT research_broadcast_id),COUNT(*)
            FROM research_sources
            WHERE trim(publisher)<>'' OR trim(url)<>''
            GROUP BY source_name
            ORDER BY COUNT(DISTINCT research_broadcast_id) DESC,COUNT(*) DESC,source_name COLLATE NOCASE
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0).Trim();
            if (name.Length == 0) continue;
            var broadcasts = Convert.ToInt32(reader.GetInt64(1));
            var entries = Convert.ToInt32(reader.GetInt64(2));
            result.Add(new ExploreFacetItem
            {
                Kind = ExploreFacetKind.Source,
                Value = name,
                SearchText = name,
                Count = broadcasts,
                Detail = $"{entries:N0} source entries across {broadcasts:N0} broadcasts"
            });
        }
        return result;
    }

    private static void AddCountRows(SqliteCommand command, IDictionary<string, FacetAccumulator> counts)
    {
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;
            var value = reader.GetString(0).Trim();
            if (value.Length == 0) continue;
            AddCount(counts, value, Convert.ToInt32(reader.GetInt64(1)));
        }
    }

    private static void AddCount(IDictionary<string, FacetAccumulator> counts, string value, int amount)
    {
        var clean = value.Trim();
        if (clean.Length == 0) return;
        if (counts.TryGetValue(clean, out var existing))
            existing.Count += amount;
        else
            counts[clean] = new FacetAccumulator(clean, amount);
    }

    private static IReadOnlyList<ExploreFacetItem> ToFacetItems(
        IDictionary<string, FacetAccumulator> counts,
        ExploreFacetKind kind,
        int limit,
        Func<int, string> detail)
        => counts.Values
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(x => new ExploreFacetItem
            {
                Kind = kind,
                Value = x.Value,
                SearchText = x.Value,
                Count = x.Count,
                Detail = detail(x.Count)
            })
            .ToList();

    private static IEnumerable<string> SplitFacetValues(string value)
        => value.Split(new[] { '|', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0);

    private sealed class FacetAccumulator
    {
        public FacetAccumulator(string value, int count)
        {
            Value = value;
            Count = count;
        }

        public string Value { get; }
        public int Count { get; set; }
    }
}
