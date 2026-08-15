using System.Text.Json;
using Microsoft.Data.Sqlite;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

/// <summary>
/// Exports only the evidence that can explain an unresolved reconciliation date.
/// Main database tables are never changed; the report intentionally excludes
/// transcripts, playback state, credentials and private RSS configuration.
/// </summary>
public sealed class ResearchDateAuthorityEvidenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SqliteDatabase _database;

    public ResearchDateAuthorityEvidenceService(SqliteDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public void ExportLatest(string path, string appVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var connection = _database.OpenConnection();
        var runId = LatestCompletedRunId(connection);
        if (runId <= 0)
            throw new InvalidOperationException("Run archive reconciliation before exporting unresolved-date evidence.");

        var unresolved = ReadUnresolvedBroadcasts(connection, runId);
        var episodeIds = unresolved
            .SelectMany(item => item.Episodes)
            .Select(item => item.EpisodeId)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var canonicalCollections = unresolved
            .Select(item => CollectionIdentityResolver.Canonicalize(item.CollectionName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var familyCollectionIds = CollectionIdentityResolver.LoadFamilies(connection)
            .Where(family => canonicalCollections.Contains(family.CanonicalName))
            .SelectMany(family => family.CollectionIds)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        var research = ReadResearchRecords(connection, episodeIds, familyCollectionIds);
        var researchIds = research.Select(item => item.ResearchId).Distinct().OrderBy(id => id).ToArray();
        var provenance = ReadProvenance(connection, episodeIds, researchIds);
        var importHistory = ReadImportHistory(connection, episodeIds, researchIds);
        var missing = ReadLegacyMissingResearch(connection, episodeIds, canonicalCollections);
        var revisions = ReadLegacyRevisions(connection, missing.Select(item => item.Id).ToArray());

        var report = new ResearchDateAuthorityEvidenceReport
        {
            AppVersion = appVersion?.Trim() ?? string.Empty,
            ExportedAt = DateTimeOffset.UtcNow,
            ReconciliationRunId = runId,
            UnresolvedBroadcasts = unresolved,
            ResearchRecords = research,
            Provenance = provenance,
            ImportHistory = importHistory,
            LegacyMissingResearch = missing,
            LegacyMissingResearchRevisions = revisions,
            Summary = new ResearchDateAuthorityEvidenceSummary(
                unresolved.Count,
                episodeIds.Length,
                unresolved.Sum(item => item.Episodes.Sum(episode => episode.Paths.Count)),
                research.Count(item => item.EpisodeId.HasValue),
                research.Count(item => !item.EpisodeId.HasValue),
                research.Count(item => !string.IsNullOrWhiteSpace(item.StructuredAirDate)),
                provenance.Count,
                importHistory.Count,
                missing.Count,
                revisions.Count)
        };

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
    }

    private static long LatestCompletedRunId(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE((SELECT id FROM library_truth_runs WHERE status='completed' ORDER BY id DESC LIMIT 1),0)";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static IReadOnlyList<UnresolvedDateBroadcastEvidence> ReadUnresolvedBroadcasts(
        SqliteConnection connection,
        long runId)
    {
        var broadcasts = new Dictionary<string, BroadcastBuilder>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.canonical_key,b.collection_name,COALESCE(b.broadcast_slot,''),COALESCE(b.adoption_reason,''),
                   e.id,e.collection_id,c.name,COALESCE(e.broadcast_uid,''),COALESCE(e.air_date,''),
                   COALESCE(e.date_confidence,'Unknown'),COALESCE(e.title,''),COALESCE(e.part_number,1),e.total_parts,
                   f.original_filename,f.path
              FROM library_truth_broadcasts b
              JOIN library_truth_files f
                ON f.run_id=b.run_id AND f.canonical_broadcast_key=b.canonical_key
              JOIN episodes e ON e.id=f.current_episode_id
              JOIN collections c ON c.id=e.collection_id
             WHERE b.run_id=$run AND b.air_date IS NULL
             ORDER BY b.collection_name,b.canonical_key,e.id,f.original_filename;
            """;
        command.Parameters.AddWithValue("$run", runId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            if (!broadcasts.TryGetValue(key, out var broadcast))
            {
                broadcast = new BroadcastBuilder(reader.GetString(1), reader.GetString(2), reader.GetString(3));
                broadcasts.Add(key, broadcast);
            }
            var episodeId = reader.GetInt64(4);
            if (!broadcast.Episodes.TryGetValue(episodeId, out var episode))
            {
                episode = new EpisodeBuilder(
                    reader.GetInt32(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                    reader.GetString(9), reader.GetString(10), reader.GetInt32(11),
                    reader.IsDBNull(12) ? null : reader.GetInt32(12));
                broadcast.Episodes.Add(episodeId, episode);
            }
            episode.Filenames.Add(reader.GetString(13));
            episode.Paths.Add(reader.GetString(14));
        }

        return broadcasts.Select(pair => new UnresolvedDateBroadcastEvidence(
                pair.Key,
                pair.Value.CollectionName,
                pair.Value.BroadcastSlot,
                pair.Value.AdoptionReason,
                pair.Value.Episodes.Select(episode => new UnresolvedDateEpisodeEvidence(
                        episode.Key,
                        episode.Value.CollectionId,
                        episode.Value.CollectionName,
                        episode.Value.BroadcastUid,
                        episode.Value.CurrentAirDate,
                        episode.Value.DateConfidence,
                        episode.Value.Title,
                        episode.Value.PartNumber,
                        episode.Value.TotalParts,
                        episode.Value.Filenames.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                        episode.Value.Paths.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()))
                    .OrderBy(episode => episode.EpisodeId)
                    .ToArray()))
            .OrderBy(item => item.CollectionName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.CanonicalKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<ResearchDateRecordEvidence> ReadResearchRecords(
        SqliteConnection connection,
        IReadOnlyList<long> episodeIds,
        IReadOnlyList<int> familyCollectionIds)
    {
        if (episodeIds.Count == 0) return Array.Empty<ResearchDateRecordEvidence>();
        using var command = connection.CreateCommand();
        var episodePredicate = AddLongParameters(command, "rb.episode_id", "episode", episodeIds);
        var familyPredicate = CollectionIdentityResolver.AddIdPredicate(command, "rb.collection_id", "family", familyCollectionIds);
        command.CommandText = $"""
            SELECT rb.id,rb.episode_id,rb.collection_id,c.name,rb.legacy_missing_research_id,
                   COALESCE(rb.identity_key,''),COALESCE(rb.source_broadcast_id,''),COALESCE(rb.air_date,''),
                   COALESCE(rb.slot,''),COALESCE(rb.part_number,1),rb.total_parts,COALESCE(rb.headline,''),
                   COALESCE(rb.confidence,0),COALESCE(rb.needs_review,0),COALESCE(rb.research_state,''),
                   COALESCE(rb.existence_status,''),COALESCE(rb.research_json,char(123)||char(125))
              FROM research_broadcasts rb
              JOIN collections c ON c.id=rb.collection_id
             WHERE {episodePredicate}
                OR (rb.episode_id IS NULL AND {familyPredicate})
             ORDER BY c.name,rb.air_date,rb.headline,rb.id;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<ResearchDateRecordEvidence>();
        while (reader.Read())
        {
            result.Add(new ResearchDateRecordEvidence(
                reader.GetInt64(0), NullableInt64(reader, 1), reader.GetInt32(2), reader.GetString(3),
                NullableInt64(reader, 4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), reader.GetInt32(9), NullableInt32(reader, 10), reader.GetString(11),
                reader.GetInt32(12), reader.GetInt64(13) != 0, reader.GetString(14), reader.GetString(15),
                reader.GetString(16)));
        }
        return result;
    }

    private static IReadOnlyList<ResearchDateProvenanceEvidence> ReadProvenance(
        SqliteConnection connection,
        IReadOnlyList<long> episodeIds,
        IReadOnlyList<long> researchIds)
    {
        var result = new Dictionary<long, ResearchDateProvenanceEvidence>();
        foreach (var (column, prefix, ids) in new[]
                 {
                     ("episode_id", "provenanceEpisode", episodeIds),
                     ("research_broadcast_id", "provenanceResearch", researchIds)
                 })
        {
            foreach (var chunk in ids.Chunk(400))
            {
                using var command = connection.CreateCommand();
                var predicate = AddLongParameters(command, column, prefix, chunk);
                command.CommandText = $"""
                    SELECT id,research_broadcast_id,episode_id,field_name,COALESCE(value_text,''),
                           COALESCE(source_kind,''),COALESCE(source_label,''),import_run_id,
                           COALESCE(confidence,0),COALESCE(protected,0),COALESCE(active,0),
                           COALESCE(created_at,''),COALESCE(superseded_at,'')
                      FROM research_field_provenance
                     WHERE {predicate}
                       AND (lower(field_name) LIKE '%date%' OR lower(field_name) IN ('research_json','air_date'));
                    """;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var item = new ResearchDateProvenanceEvidence(
                        reader.GetInt64(0), NullableInt64(reader, 1), NullableInt64(reader, 2), reader.GetString(3),
                        reader.GetString(4), reader.GetString(5), reader.GetString(6), NullableInt64(reader, 7),
                        reader.GetInt32(8), reader.GetInt64(9) != 0, reader.GetInt64(10) != 0,
                        reader.GetString(11), reader.GetString(12));
                    result[item.Id] = item;
                }
            }
        }
        return result.Values.OrderBy(item => item.Id).ToArray();
    }

    private static IReadOnlyList<ResearchDateImportChangeEvidence> ReadImportHistory(
        SqliteConnection connection,
        IReadOnlyList<long> episodeIds,
        IReadOnlyList<long> researchIds)
    {
        var result = new Dictionary<long, ResearchDateImportChangeEvidence>();
        foreach (var (column, prefix, ids) in new[]
                 {
                     ("ric.episode_id", "historyEpisode", episodeIds),
                     ("ric.research_broadcast_id", "historyResearch", researchIds)
                 })
        {
            foreach (var chunk in ids.Chunk(400))
            {
                using var command = connection.CreateCommand();
                var predicate = AddLongParameters(command, column, prefix, chunk);
                command.CommandText = $"""
                    SELECT ric.id,ric.import_run_id,ric.research_broadcast_id,ric.episode_id,
                           COALESCE(rir.package_name,''),COALESCE(rir.imported_at,''),
                           COALESCE(ric.record_identity,''),COALESCE(ric.field_name,''),
                           COALESCE(ric.before_value,''),COALESCE(ric.after_value,''),
                           COALESCE(ric.decision,''),COALESCE(ric.reason,''),COALESCE(ric.created_at,'')
                      FROM research_import_changes ric
                      JOIN research_import_runs rir ON rir.id=ric.import_run_id
                     WHERE {predicate}
                       AND (lower(ric.field_name) LIKE '%date%'
                            OR lower(ric.field_name) IN ('research_json','air_date'));
                    """;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var item = new ResearchDateImportChangeEvidence(
                        reader.GetInt64(0), reader.GetInt64(1), NullableInt64(reader, 2), NullableInt64(reader, 3),
                        reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                        reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
                        reader.GetString(12));
                    result[item.Id] = item;
                }
            }
        }
        return result.Values.OrderBy(item => item.Id).ToArray();
    }

    private static IReadOnlyList<LegacyMissingDateEvidence> ReadLegacyMissingResearch(
        SqliteConnection connection,
        IReadOnlyCollection<long> episodeIds,
        IReadOnlySet<string> canonicalCollections)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,COALESCE(stable_key,''),COALESCE(broadcast_uid,''),COALESCE(show_name,''),
                   COALESCE(broadcast_date,''),COALESCE(slot,''),COALESCE(part_number,1),total_parts,
                   COALESCE(headline,''),COALESCE(confidence,0),COALESCE(status,''),matched_episode_id,
                   COALESCE(match_notes,''),COALESCE(research_json,'{}'),COALESCE(updated_at,'')
              FROM missing_broadcast_research
             ORDER BY show_name,broadcast_date,headline,id;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<LegacyMissingDateEvidence>();
        while (reader.Read())
        {
            var matchedEpisodeId = NullableInt64(reader, 11);
            var show = CollectionIdentityResolver.Canonicalize(reader.GetString(3));
            if ((!matchedEpisodeId.HasValue || !episodeIds.Contains(matchedEpisodeId.Value))
                && !canonicalCollections.Contains(show)) continue;
            result.Add(new LegacyMissingDateEvidence(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetInt32(6), NullableInt32(reader, 7),
                reader.GetString(8), reader.GetInt32(9), reader.GetString(10), matchedEpisodeId,
                reader.GetString(12), reader.GetString(13), reader.GetString(14)));
        }
        return result;
    }

    private static IReadOnlyList<LegacyMissingDateRevisionEvidence> ReadLegacyRevisions(
        SqliteConnection connection,
        IReadOnlyList<long> missingIds)
    {
        var result = new List<LegacyMissingDateRevisionEvidence>();
        foreach (var chunk in missingIds.Chunk(400))
        {
            using var command = connection.CreateCommand();
            var predicate = AddLongParameters(command, "missing_research_id", "revision", chunk);
            command.CommandText = $"""
                SELECT id,missing_research_id,COALESCE(status,''),matched_episode_id,
                       COALESCE(match_notes,''),COALESCE(research_json,char(123)||char(125)),COALESCE(saved_at,'')
                  FROM missing_broadcast_research_revisions
                 WHERE {predicate}
                 ORDER BY missing_research_id,id;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new LegacyMissingDateRevisionEvidence(
                    reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), NullableInt64(reader, 3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6)));
            }
        }
        return result;
    }

    private static string AddLongParameters(
        SqliteCommand command,
        string column,
        string prefix,
        IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return "1=0";
        var names = new string[ids.Count];
        for (var index = 0; index < ids.Count; index++)
        {
            names[index] = $"${prefix}{index}";
            command.Parameters.AddWithValue(names[index], ids[index]);
        }
        return $"{column} IN ({string.Join(",", names)})";
    }

    private static long? NullableInt64(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static int? NullableInt32(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private sealed class BroadcastBuilder(string collectionName, string broadcastSlot, string adoptionReason)
    {
        public string CollectionName { get; } = collectionName;
        public string BroadcastSlot { get; } = broadcastSlot;
        public string AdoptionReason { get; } = adoptionReason;
        public Dictionary<long, EpisodeBuilder> Episodes { get; } = new();
    }

    private sealed class EpisodeBuilder(
        int collectionId,
        string collectionName,
        string broadcastUid,
        string currentAirDate,
        string dateConfidence,
        string title,
        int partNumber,
        int? totalParts)
    {
        public int CollectionId { get; } = collectionId;
        public string CollectionName { get; } = collectionName;
        public string BroadcastUid { get; } = broadcastUid;
        public string CurrentAirDate { get; } = currentAirDate;
        public string DateConfidence { get; } = dateConfidence;
        public string Title { get; } = title;
        public int PartNumber { get; } = partNumber;
        public int? TotalParts { get; } = totalParts;
        public HashSet<string> Filenames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
