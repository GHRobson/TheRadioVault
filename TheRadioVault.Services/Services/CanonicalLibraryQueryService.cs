using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

/// <summary>
/// Presents the adopted Library Truth hierarchy as one stable, user-facing
/// broadcast read model. Physical files and legacy episode rows remain available
/// as implementation detail, but are never used as top-level library identity.
/// </summary>
public sealed class CanonicalLibraryQueryService
{
    private readonly SqliteDatabase _database;

    public CanonicalLibraryQueryService(SqliteDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public CanonicalLibrarySummary GetSummary()
    {
        using var connection = _database.OpenConnection();
        if (!TableExists(connection, "canonical_broadcasts") ||
            !TableExists(connection, "library_truth_broadcasts") ||
            !TableExists(connection, "library_truth_adoption_runs"))
            return CanonicalLibrarySummary.Unavailable;

        var latestTruthRunId = ScalarLong(connection, """
            SELECT COALESCE((
                SELECT truth_run_id
                  FROM library_truth_adoption_runs
                 WHERE status='completed'
                   AND commit_verified=1
                   AND foreign_key_violations=0
                   AND lower(integrity_check)='ok'
                 ORDER BY id DESC
                 LIMIT 1
            ),0)
            """);
        if (latestTruthRunId == 0) return CanonicalLibrarySummary.Unavailable;

        var adopted = ScalarInt(connection, "SELECT COUNT(*) FROM canonical_broadcasts");
        var truthBroadcasts = ScalarInt(connection,
            "SELECT COUNT(*) FROM library_truth_broadcasts WHERE run_id=$run",
            ("$run", latestTruthRunId));
        var incrementalBroadcasts = ScalarInt(connection, """
            SELECT COUNT(*)
              FROM canonical_broadcasts cb
             WHERE NOT EXISTS(
                 SELECT 1 FROM library_truth_broadcasts b
                  WHERE b.run_id=$run AND b.canonical_key=cb.canonical_key
             )
            """, ("$run", latestTruthRunId));
        var broadcasts = checked(truthBroadcasts + incrementalBroadcasts);
        var review = ScalarInt(connection, """
            SELECT COUNT(*)
              FROM library_truth_broadcasts
             WHERE run_id=$run AND adoption_state='Review recommended'
            """, ("$run", latestTruthRunId));
        var blocked = ScalarInt(connection, """
            SELECT COUNT(*)
              FROM library_truth_broadcasts
             WHERE run_id=$run AND adoption_state='Blocked'
            """, ("$run", latestTruthRunId));
        var recordings = checked(ScalarInt(connection,
            "SELECT COUNT(*) FROM library_truth_recordings WHERE run_id=$run",
            ("$run", latestTruthRunId)) + ScalarInt(connection, """
                SELECT COUNT(*)
                  FROM recordings r
                 WHERE NOT EXISTS(
                     SELECT 1 FROM library_truth_recordings tr
                      WHERE tr.run_id=$run AND tr.recording_key=r.recording_key
                 )
                """, ("$run", latestTruthRunId)));
        var coverages = checked(ScalarInt(connection,
            "SELECT COUNT(*) FROM library_truth_coverages WHERE run_id=$run",
            ("$run", latestTruthRunId)) + ScalarInt(connection, """
                SELECT COUNT(*)
                  FROM recording_coverages c
                  JOIN recordings r ON r.recording_key=c.recording_key
                 WHERE NOT EXISTS(
                     SELECT 1 FROM library_truth_recordings tr
                      WHERE tr.run_id=$run AND tr.recording_key=r.recording_key
                 )
                """, ("$run", latestTruthRunId)));
        var files = checked(ScalarInt(connection,
            "SELECT COUNT(*) FROM library_truth_files WHERE run_id=$run",
            ("$run", latestTruthRunId)) + ScalarInt(connection, """
                SELECT COUNT(DISTINCT mf.id)
                  FROM canonical_broadcasts cb
                  JOIN episode_canonical_map map ON map.canonical_key=cb.canonical_key
                  JOIN media_files mf ON mf.episode_id=map.episode_id
                 WHERE NOT EXISTS(
                     SELECT 1 FROM library_truth_files tf
                      WHERE tf.run_id=$run AND tf.media_file_id=mf.id
                 )
                """, ("$run", latestTruthRunId)));
        var adoptionVerified = ScalarInt(connection, """
            SELECT COUNT(*)
              FROM library_truth_adoption_runs
             WHERE truth_run_id=$run
               AND status='completed'
               AND commit_verified=1
               AND foreign_key_violations=0
               AND lower(integrity_check)='ok'
            """, ("$run", latestTruthRunId)) > 0;

        return new CanonicalLibrarySummary(
            latestTruthRunId,
            broadcasts,
            adopted,
            checked(review + blocked),
            review,
            blocked,
            recordings,
            coverages,
            files,
            adoptionVerified);
    }

    public IReadOnlyList<CanonicalLibraryEntry> GetBroadcasts()
        => GetBroadcasts(null);

    public CanonicalLibraryEntry? GetBroadcast(long episodeId)
    {
        var resolution = ResolveEpisode(episodeId);
        if (resolution is null) return null;
        return GetBroadcasts(resolution.CanonicalKey).FirstOrDefault();
    }

    private IReadOnlyList<CanonicalLibraryEntry> GetBroadcasts(string? canonicalKey)
    {
        var summary = GetSummary();
        if (!summary.IsCutoverReady) return Array.Empty<CanonicalLibraryEntry>();

        var result = new List<CanonicalLibraryEntry>(canonicalKey is null ? summary.Broadcasts : 1);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH
            truth_broadcasts AS (
                SELECT *
                  FROM library_truth_broadcasts
                 WHERE run_id=$run
                   AND ($key IS NULL OR canonical_key=$key)
            ),
            members AS (
                SELECT DISTINCT canonical_broadcast_key AS canonical_key,
                       current_episode_id AS episode_id
                  FROM library_truth_files
                 WHERE run_id=$run
                UNION
                SELECT map.canonical_key,map.episode_id
                  FROM episode_canonical_map map
                  JOIN truth_broadcasts b ON b.canonical_key=map.canonical_key
            ),
            adopted_survivors AS (
                SELECT canonical_key,MAX(survivor_episode_id) AS representative_episode_id
                  FROM episode_canonical_map
                 WHERE is_survivor=1
                 GROUP BY canonical_key
            ),
            ranked_files AS (
                SELECT f.canonical_broadcast_key AS canonical_key,
                       f.current_episode_id,
                       f.media_file_id,
                       ROW_NUMBER() OVER (
                           PARTITION BY f.canonical_broadcast_key
                           ORDER BY CASE WHEN f.recording_key=b.preferred_recording_key THEN 0 ELSE 1 END,
                                    CASE WHEN f.proposed_part=1 THEN 0 ELSE 1 END,
                                    CASE WHEN COALESCE(fm.is_missing,0)=0 THEN 0 ELSE 1 END,
                                    CASE WHEN COALESCE(fm.is_preferred,0)=1 THEN 0 ELSE 1 END,
                                    f.media_file_id
                       ) AS preference_rank
                  FROM library_truth_files f
                  JOIN truth_broadcasts b ON b.canonical_key=f.canonical_broadcast_key
                  JOIN media_files fm ON fm.id=f.media_file_id
                 WHERE f.run_id=$run
            ),
            representatives AS (
                SELECT b.*,
                       COALESCE(a.representative_episode_id,rf.current_episode_id)
                           AS representative_episode_id
                  FROM truth_broadcasts b
                  LEFT JOIN adopted_survivors a ON a.canonical_key=b.canonical_key
                  LEFT JOIN ranked_files rf
                    ON rf.canonical_key=b.canonical_key AND rf.preference_rank=1
            ),
            preferred_files AS (
                SELECT canonical_key,media_file_id
                  FROM ranked_files
                 WHERE preference_rank=1
            ),
            state_aggregate AS (
                SELECT m.canonical_key,
                       MAX(COALESCE(e.favourite,0)) AS favourite,
                       MAX(COALESCE(ps.completed,0)) AS completed,
                       MAX(COALESCE(ps.position_ms,0)) AS position_ms,
                       MAX(ps.last_played_at) AS last_played_at,
                       MAX(e.date_added) AS date_added
                  FROM members m
                  JOIN episodes e ON e.id=m.episode_id
                  LEFT JOIN playback_state ps ON ps.episode_id=m.episode_id
                 GROUP BY m.canonical_key
            ),
            guest_names AS (
                SELECT canonical_key,group_concat(name, ', ') AS names
                  FROM (
                    SELECT DISTINCT m.canonical_key,g.name
                      FROM members m
                      JOIN episode_guests eg ON eg.episode_id=m.episode_id
                      JOIN guests g ON g.id=eg.guest_id
                     ORDER BY m.canonical_key,g.name
                  )
                 GROUP BY canonical_key
            ),
            tag_names AS (
                SELECT canonical_key,group_concat(name, ', ') AS names
                  FROM (
                    SELECT DISTINCT m.canonical_key,t.name
                      FROM members m
                      JOIN episode_tags et ON et.episode_id=m.episode_id
                      JOIN tags t ON t.id=et.tag_id
                     ORDER BY m.canonical_key,t.name
                  )
                 GROUP BY canonical_key
            ),
            preferred_recordings AS (
                SELECT b.canonical_key,
                       COALESCE(
                           (
                               SELECT r.duration_ms
                                 FROM library_truth_recordings r
                                WHERE r.run_id=$run
                                  AND r.recording_key=b.preferred_recording_key
                                LIMIT 1
                           ),
                           (
                               SELECT MAX(r.duration_ms)
                                 FROM library_truth_recordings r
                                WHERE r.run_id=$run
                                  AND r.canonical_broadcast_key=b.canonical_key
                           ),
                           0
                       ) AS duration_ms
                  FROM truth_broadcasts b
            )
            SELECT b.canonical_key,
                   b.representative_episode_id,
                   COALESCE(NULLIF(e.broadcast_uid,''),b.canonical_key),
                   COALESCE(canonical_collection.id,e.collection_id),
                   b.collection_name,
                   b.air_date,
                   COALESCE(b.broadcast_slot,''),
                   COALESCE(e.title,''),
                   COALESCE(e.description,''),
                   COALESCE(mf.original_filename,''),
                   COALESCE(mf.path,''),
                   COALESCE(mf.storage_state,'Missing'),
                   COALESCE(sa.favourite,0),
                   CASE WHEN COALESCE(sa.completed,0)=1 THEN 'Completed'
                        WHEN COALESCE(sa.position_ms,0)>0 THEN 'In Progress'
                        ELSE 'Unplayed' END,
                   COALESCE(sa.position_ms,0),
                   MAX(COALESCE(pr.duration_ms,0),COALESCE(ps.duration_ms,0),COALESCE(mf.duration_ms,0)),
                   sa.last_played_at,
                   COALESCE(sa.date_added,e.date_added),
                   COALESCE(gn.names,''),
                   COALESCE(tn.names,''),
                   NULLIF(COALESCE(e.artwork_path,''),''),
                   COALESCE(e.edition,''),
                   b.confidence_score,
                   COALESCE(e.metadata_confidence_reason,''),
                   MAX(b.recording_count,COALESCE((
                       SELECT COUNT(*) FROM recordings ar WHERE ar.canonical_key=b.canonical_key
                   ),0)),
                   MAX(b.segment_count,COALESCE((
                       SELECT COUNT(*) FROM recording_segments aseg
                        WHERE aseg.recording_key=COALESCE(NULLIF(cb.preferred_recording_key,''),b.preferred_recording_key)
                   ),0)),
                   MAX(b.file_count,COALESCE((
                       SELECT COUNT(DISTINCT amf.id)
                         FROM episode_canonical_map amap
                         JOIN media_files amf ON amf.episode_id=amap.episode_id
                        WHERE amap.canonical_key=b.canonical_key
                   ),0)),
                   CASE WHEN b.adoption_state IN ('Review recommended','Blocked') THEN 1 ELSE 0 END,
                   COALESCE(b.adoption_state,''),
                   COALESCE(b.adoption_reason,''),
                   CASE WHEN cb.canonical_key IS NULL THEN 0 ELSE 1 END
              FROM representatives b
              JOIN episodes e ON e.id=b.representative_episode_id
              LEFT JOIN collections canonical_collection ON lower(canonical_collection.name)=lower(b.collection_name)
              LEFT JOIN preferred_files pf ON pf.canonical_key=b.canonical_key
              LEFT JOIN media_files mf ON mf.id=pf.media_file_id
              LEFT JOIN playback_state ps ON ps.episode_id=b.representative_episode_id
              LEFT JOIN state_aggregate sa ON sa.canonical_key=b.canonical_key
              LEFT JOIN guest_names gn ON gn.canonical_key=b.canonical_key
              LEFT JOIN tag_names tn ON tn.canonical_key=b.canonical_key
              LEFT JOIN preferred_recordings pr ON pr.canonical_key=b.canonical_key
              LEFT JOIN canonical_broadcasts cb ON cb.canonical_key=b.canonical_key
             ORDER BY b.collection_name,b.air_date,b.broadcast_slot,b.canonical_key
            """;
        command.Parameters.AddWithValue("$run", summary.LatestTruthRunId);
        command.Parameters.AddWithValue("$key", (object?)canonicalKey ?? DBNull.Value);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var airDate = ReadDateOnly(reader, 5);
            var lastPlayed = ReadDateTimeOffset(reader, 16);
            var dateAdded = ReadDateTimeOffset(reader, 17) ?? DateTimeOffset.MinValue;
            result.Add(new CanonicalLibraryEntry(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                airDate,
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetInt32(12) != 0,
                reader.GetString(13),
                reader.GetInt64(14),
                reader.GetInt64(15),
                lastPlayed,
                dateAdded,
                reader.GetString(18),
                reader.GetString(19),
                reader.IsDBNull(20) ? null : reader.GetString(20),
                reader.GetString(21),
                reader.GetInt32(22),
                reader.GetString(23),
                reader.GetInt32(24),
                reader.GetInt32(25),
                reader.GetInt32(26),
                reader.GetInt32(27) != 0,
                reader.GetString(28),
                reader.GetString(29),
                reader.GetInt32(30) != 0));
        }
        reader.Close();

        result.AddRange(ReadIncrementalBroadcasts(connection, summary.LatestTruthRunId, canonicalKey));
        return result
            .OrderBy(x => x.CollectionName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.AirDate)
            .ThenBy(x => x.BroadcastSlot, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.CanonicalKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<CanonicalLibraryEntry> ReadIncrementalBroadcasts(
        SqliteConnection connection,
        long truthRunId,
        string? canonicalKey = null)
    {
        var result = new List<CanonicalLibraryEntry>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH incremental AS (
                SELECT cb.canonical_key,cb.collection_name,cb.air_date,cb.broadcast_slot,
                       cb.preferred_recording_key,cb.confidence_score,
                       map.survivor_episode_id AS representative_episode_id
                 FROM canonical_broadcasts cb
                  JOIN episode_canonical_map map
                    ON map.canonical_key=cb.canonical_key AND map.is_survivor=1
                 WHERE ($key IS NULL OR cb.canonical_key=$key)
                   AND NOT EXISTS(
                     SELECT 1 FROM library_truth_broadcasts b
                      WHERE b.run_id=$run AND b.canonical_key=cb.canonical_key
                 )
            ),
            members AS (
                SELECT map.canonical_key,map.episode_id
                  FROM episode_canonical_map map
                  JOIN incremental i ON i.canonical_key=map.canonical_key
            ),
            state_aggregate AS (
                SELECT m.canonical_key,MAX(COALESCE(e.favourite,0)) AS favourite,
                       MAX(COALESCE(ps.completed,0)) AS completed,
                       MAX(COALESCE(ps.position_ms,0)) AS position_ms,
                       MAX(ps.last_played_at) AS last_played_at,
                       MAX(e.date_added) AS date_added
                  FROM members m
                  JOIN episodes e ON e.id=m.episode_id
                  LEFT JOIN playback_state ps ON ps.episode_id=m.episode_id
                 GROUP BY m.canonical_key
            ),
            guest_names AS (
                SELECT canonical_key,group_concat(name, ', ') AS names
                  FROM (
                    SELECT DISTINCT m.canonical_key,g.name
                      FROM members m
                      JOIN episode_guests eg ON eg.episode_id=m.episode_id
                      JOIN guests g ON g.id=eg.guest_id
                     ORDER BY m.canonical_key,g.name
                  )
                 GROUP BY canonical_key
            ),
            tag_names AS (
                SELECT canonical_key,group_concat(name, ', ') AS names
                  FROM (
                    SELECT DISTINCT m.canonical_key,t.name
                      FROM members m
                      JOIN episode_tags et ON et.episode_id=m.episode_id
                      JOIN tags t ON t.id=et.tag_id
                     ORDER BY m.canonical_key,t.name
                  )
                 GROUP BY canonical_key
            )
            SELECT i.canonical_key,i.representative_episode_id,
                   COALESCE(NULLIF(e.broadcast_uid,''),i.canonical_key),
                   COALESCE(canonical_collection.id,e.collection_id),i.collection_name,i.air_date,
                   COALESCE(i.broadcast_slot,''),COALESCE(e.title,''),COALESCE(e.description,''),
                   COALESCE(mf.original_filename,''),COALESCE(mf.path,''),COALESCE(mf.storage_state,'Missing'),
                   COALESCE(sa.favourite,0),
                   CASE WHEN COALESCE(sa.completed,0)=1 THEN 'Completed'
                        WHEN COALESCE(sa.position_ms,0)>0 THEN 'In Progress' ELSE 'Unplayed' END,
                   COALESCE(sa.position_ms,0),
                   MAX(COALESCE(r.duration_ms,0),COALESCE(ps.duration_ms,0),COALESCE(mf.duration_ms,0)),
                   sa.last_played_at,COALESCE(sa.date_added,e.date_added),
                   COALESCE(gn.names,''),COALESCE(tn.names,''),NULLIF(COALESCE(e.artwork_path,''),''),
                   COALESCE(e.edition,''),i.confidence_score,COALESCE(e.metadata_confidence_reason,''),
                   (SELECT COUNT(*) FROM recordings allr WHERE allr.canonical_key=i.canonical_key),
                   (SELECT COUNT(*) FROM recording_segments seg WHERE seg.recording_key=i.preferred_recording_key),
                   (SELECT COUNT(DISTINCT allmf.id)
                      FROM episode_canonical_map allmap
                      JOIN media_files allmf ON allmf.episode_id=allmap.episode_id
                     WHERE allmap.canonical_key=i.canonical_key),
                   0,'Added by scan','Automatically appended after the guarded canonical cutover.',1
              FROM incremental i
              JOIN episodes e ON e.id=i.representative_episode_id
              LEFT JOIN collections canonical_collection ON lower(canonical_collection.name)=lower(i.collection_name)
              LEFT JOIN media_files mf ON mf.id=(
                   SELECT pick.id FROM media_files pick
                    WHERE pick.episode_id=e.id AND COALESCE(pick.is_missing,0)=0
                    ORDER BY COALESCE(pick.is_preferred,0) DESC,pick.id LIMIT 1
              )
              LEFT JOIN playback_state ps ON ps.episode_id=e.id
              LEFT JOIN recordings r ON r.recording_key=i.preferred_recording_key
              LEFT JOIN state_aggregate sa ON sa.canonical_key=i.canonical_key
              LEFT JOIN guest_names gn ON gn.canonical_key=i.canonical_key
              LEFT JOIN tag_names tn ON tn.canonical_key=i.canonical_key
             ORDER BY i.collection_name,i.air_date,i.broadcast_slot,i.canonical_key
            """;
        command.Parameters.AddWithValue("$run", truthRunId);
        command.Parameters.AddWithValue("$key", (object?)canonicalKey ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new CanonicalLibraryEntry(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                ReadDateOnly(reader, 5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetInt32(12) != 0,
                reader.GetString(13),
                reader.GetInt64(14),
                reader.GetInt64(15),
                ReadDateTimeOffset(reader, 16),
                ReadDateTimeOffset(reader, 17) ?? DateTimeOffset.MinValue,
                reader.GetString(18),
                reader.GetString(19),
                reader.IsDBNull(20) ? null : reader.GetString(20),
                reader.GetString(21),
                reader.GetInt32(22),
                reader.GetString(23),
                reader.GetInt32(24),
                reader.GetInt32(25),
                reader.GetInt32(26),
                reader.GetInt32(27) != 0,
                reader.GetString(28),
                reader.GetString(29),
                reader.GetInt32(30) != 0));
        }
        return result;
    }

    public IReadOnlyList<CanonicalCollectionSummary> GetCollectionSummaries()
        => GetBroadcasts()
            .GroupBy(x => new { x.CollectionId, x.CollectionName })
            .Select(x => new CanonicalCollectionSummary(x.Key.CollectionId, x.Key.CollectionName, x.Count()))
            .OrderBy(x => x.CollectionName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private long GetLatestVerifiedTruthRunId()
    {
        using var connection = _database.OpenConnection();
        if (!TableExists(connection, "library_truth_adoption_runs")) return 0;
        return ScalarLong(connection, """
            SELECT COALESCE((
                SELECT truth_run_id
                  FROM library_truth_adoption_runs
                 WHERE status='completed'
                   AND commit_verified=1
                   AND foreign_key_violations=0
                   AND lower(integrity_check)='ok'
                 ORDER BY id DESC
                 LIMIT 1
            ),0)
            """);
    }

    public CanonicalEpisodeResolution? ResolveEpisode(long episodeId)
    {
        if (episodeId <= 0) return null;
        var latestTruthRunId = GetLatestVerifiedTruthRunId();
        if (latestTruthRunId <= 0) return null;

        using var connection = _database.OpenConnection();
        using (var adopted = connection.CreateCommand())
        {
            adopted.CommandText = """
                SELECT canonical_key,survivor_episode_id,is_survivor
                  FROM episode_canonical_map
                 WHERE episode_id=$episode
                """;
            adopted.Parameters.AddWithValue("$episode", episodeId);
            using var reader = adopted.ExecuteReader();
            if (reader.Read())
            {
                var representative = reader.GetInt64(1);
                return new CanonicalEpisodeResolution(
                    episodeId,
                    reader.GetString(0),
                    representative,
                    true,
                    episodeId == representative || reader.GetInt32(2) != 0);
            }
        }

        using var held = connection.CreateCommand();
        held.CommandText = """
            WITH matching AS (
                SELECT canonical_broadcast_key AS canonical_key
                  FROM library_truth_files
                 WHERE run_id=$run AND current_episode_id=$episode
                 ORDER BY canonical_broadcast_key
                 LIMIT 1
            )
            SELECT m.canonical_key,
                   (
                       SELECT f.current_episode_id
                         FROM library_truth_files f
                         JOIN library_truth_broadcasts b
                           ON b.run_id=f.run_id AND b.canonical_key=f.canonical_broadcast_key
                         JOIN media_files mf ON mf.id=f.media_file_id
                        WHERE f.run_id=$run AND f.canonical_broadcast_key=m.canonical_key
                        ORDER BY CASE WHEN f.recording_key=b.preferred_recording_key THEN 0 ELSE 1 END,
                                 CASE WHEN f.proposed_part=1 THEN 0 ELSE 1 END,
                                 CASE WHEN COALESCE(mf.is_missing,0)=0 THEN 0 ELSE 1 END,
                                 f.media_file_id
                        LIMIT 1
                   ) AS representative_episode_id
              FROM matching m
            """;
        held.Parameters.AddWithValue("$run", latestTruthRunId);
        held.Parameters.AddWithValue("$episode", episodeId);
        using (var reader = held.ExecuteReader())
        {
            if (!reader.Read() || reader.IsDBNull(1)) return null;
            var representative = reader.GetInt64(1);
            return new CanonicalEpisodeResolution(
                episodeId,
                reader.GetString(0),
                representative,
                false,
                representative == episodeId);
        }
    }

    public IReadOnlyList<long> ExpandStateEpisodeIds(long episodeId)
    {
        var resolution = ResolveEpisode(episodeId);
        if (resolution is null) return episodeId > 0 ? new[] { episodeId } : Array.Empty<long>();

        var ids = new HashSet<long> { episodeId, resolution.RepresentativeEpisodeId };
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        if (resolution.Adopted)
        {
            command.CommandText = """
                SELECT episode_id
                  FROM episode_canonical_map
                 WHERE canonical_key=$key
                 ORDER BY episode_id
                """;
            command.Parameters.AddWithValue("$key", resolution.CanonicalKey);
        }
        else
        {
            var summary = GetSummary();
            command.CommandText = """
                SELECT DISTINCT current_episode_id
                  FROM library_truth_files
                 WHERE run_id=$run AND canonical_broadcast_key=$key
                 ORDER BY current_episode_id
                """;
            command.Parameters.AddWithValue("$run", summary.LatestTruthRunId);
            command.Parameters.AddWithValue("$key", resolution.CanonicalKey);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids.OrderBy(x => x).ToArray();
    }


    public IReadOnlyList<CanonicalRecordingOption> GetRecordingOptions(string canonicalKey)
    {
        if (string.IsNullOrWhiteSpace(canonicalKey)) return Array.Empty<CanonicalRecordingOption>();
        var summary = GetSummary();
        if (!summary.IsCutoverReady) return Array.Empty<CanonicalRecordingOption>();
        using var connection = _database.OpenConnection();
        var adoptedOptions = ReadAdoptedRecordingOptions(connection, canonicalKey);
        if (adoptedOptions.Count > 0) return adoptedOptions;
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.recording_key,r.label,r.role,r.duration_ms,
                   COUNT(DISTINCT c.segment_number),
                   COUNT(DISTINCT f.media_file_id),
                   CASE WHEN r.recording_key=b.preferred_recording_key THEN 1 ELSE 0 END,
                   CASE WHEN SUM(CASE WHEN c.requires_review=1 THEN 1 ELSE 0 END)=0 AND COUNT(c.id)>0 THEN 1 ELSE 0 END,
                   CASE WHEN SUM(CASE WHEN c.requires_review=1 THEN 1 ELSE 0 END)>0 THEN 1 ELSE 0 END
              FROM library_truth_recordings r
              JOIN library_truth_broadcasts b ON b.run_id=r.run_id AND b.canonical_key=r.canonical_broadcast_key
              LEFT JOIN library_truth_coverages c ON c.run_id=r.run_id AND c.recording_key=r.recording_key AND c.target_broadcast_key=b.canonical_key
              LEFT JOIN library_truth_files f ON f.run_id=r.run_id AND f.recording_key=r.recording_key
             WHERE r.run_id=$run AND r.canonical_broadcast_key=$key
             GROUP BY r.recording_key,r.label,r.role,r.duration_ms,b.preferred_recording_key
             ORDER BY CASE WHEN r.recording_key=b.preferred_recording_key THEN 0 ELSE 1 END,
                      CASE WHEN r.role='Full capture' THEN 0 WHEN r.role='Multipart assembly' THEN 1 ELSE 2 END,
                      r.duration_ms DESC,r.recording_key
            """;
        command.Parameters.AddWithValue("$run", summary.LatestTruthRunId);
        command.Parameters.AddWithValue("$key", canonicalKey);
        var result = new List<CanonicalRecordingOption>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new CanonicalRecordingOption(canonicalKey,reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetInt64(3),reader.GetInt32(4),reader.GetInt32(5),reader.GetInt32(6)!=0,reader.GetInt32(7)!=0,reader.GetInt32(8)!=0));
        return result;
    }

    private static IReadOnlyList<CanonicalRecordingOption> ReadAdoptedRecordingOptions(
        SqliteConnection connection,
        string canonicalKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.recording_key,r.label,r.role,r.duration_ms,
                   COUNT(DISTINCT s.segment_number) AS segment_count,
                   COUNT(DISTINCT s.id) AS file_count,
                   COALESCE(r.is_preferred,0),
                   CASE WHEN COUNT(s.id)>0
                              AND MIN(s.segment_number)=1
                              AND MAX(s.segment_number)=COUNT(DISTINCT s.segment_number)
                              AND COALESCE(MAX(s.segment_total),COUNT(DISTINCT s.segment_number))=COUNT(DISTINCT s.segment_number)
                        THEN 1 ELSE 0 END AS is_complete,
                   CASE WHEN COUNT(s.id)>0
                              AND MIN(s.segment_number)=1
                              AND MAX(s.segment_number)=COUNT(DISTINCT s.segment_number)
                              AND COALESCE(MAX(s.segment_total),COUNT(DISTINCT s.segment_number))=COUNT(DISTINCT s.segment_number)
                        THEN 0 ELSE 1 END AS requires_review
              FROM recordings r
              LEFT JOIN recording_segments s ON s.recording_key=r.recording_key
             WHERE r.canonical_key=$key
             GROUP BY r.recording_key,r.label,r.role,r.duration_ms,r.is_preferred,r.preferred_score
             ORDER BY r.is_preferred DESC,r.preferred_score DESC,r.duration_ms DESC,r.recording_key
            """;
        command.Parameters.AddWithValue("$key", canonicalKey);
        var result = new List<CanonicalRecordingOption>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new CanonicalRecordingOption(
                canonicalKey,reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetInt64(3),
                reader.GetInt32(4),reader.GetInt32(5),reader.GetInt32(6)!=0,reader.GetInt32(7)!=0,reader.GetInt32(8)!=0));
        return result;
    }

    public CanonicalPlaybackPlan? GetPlaybackPlan(string canonicalKey, string? recordingKey)
    {
        if (string.IsNullOrWhiteSpace(recordingKey)) return GetPreferredPlaybackPlan(canonicalKey);
        var summary = GetSummary();
        if (!summary.IsCutoverReady) return null;
        using var connection = _database.OpenConnection();
        var adopted = ReadAdoptedPlaybackPlan(connection, canonicalKey, recordingKey);
        if (IsPlaybackPlanComplete(adopted)) return adopted;
        var plan = ReadTruthPlaybackPlan(connection, summary.LatestTruthRunId, canonicalKey, recordingKey);
        return IsPlaybackPlanComplete(plan) ? plan : null;
    }

    public CanonicalRecordingSelectionReason? ExplainRecordingSelection(string canonicalKey)
    {
        if (string.IsNullOrWhiteSpace(canonicalKey)) return null;
        var summary = GetSummary();
        if (!summary.IsCutoverReady) return null;

        using var connection = _database.OpenConnection();
        var adopted = ReadAdoptedPlaybackPlan(connection, canonicalKey);
        if (IsPlaybackPlanComplete(adopted))
            return BuildSelectionReason(adopted!, "Adopted canonical recording", true, false,
                "The guarded Library Truth adoption selected this recording and every ordered segment resolves to at least one physical source.");

        var fallback = ReadTruthPlaybackPlan(connection, summary.LatestTruthRunId, canonicalKey);
        if (!IsPlaybackPlanComplete(fallback)) return null;
        return BuildSelectionReason(fallback!, "Held-group compatibility boundary", false, true,
            "This broadcast remains held for review. Playback uses the deterministic preferred Library Truth recording without adopting or rewriting the group.");
    }

    public CanonicalLibraryAuditSnapshot GetAuditSnapshot()
    {
        var summary = GetSummary();
        if (!summary.IsCutoverReady)
            return new CanonicalLibraryAuditSnapshot(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,DateTimeOffset.UtcNow);

        var duplicateIdentities = GetBroadcasts()
            .GroupBy(value => value.RepresentativeEpisodeId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Count())
            .ToArray();

        using var connection = _database.OpenConnection();
        var run = summary.LatestTruthRunId;
        var multipart = ScalarInt(connection, "SELECT COUNT(*) FROM library_truth_recordings WHERE run_id=$run AND segment_count>1", ("$run", run));
        var reviewCoverage = ScalarInt(connection, "SELECT COUNT(*) FROM library_truth_coverages WHERE run_id=$run AND requires_review=1", ("$run", run));
        var missing = ScalarInt(connection, "SELECT COUNT(*) FROM library_truth_files f JOIN media_files m ON m.id=f.media_file_id WHERE f.run_id=$run AND COALESCE(m.is_missing,0)=1", ("$run", run));
        var cloudOnly = ScalarInt(connection, "SELECT COUNT(*) FROM library_truth_files f JOIN media_files m ON m.id=f.media_file_id WHERE f.run_id=$run AND lower(COALESCE(m.storage_state,''))='cloudonly'", ("$run", run));
        var incomplete = ScalarInt(connection, """
            SELECT COUNT(*) FROM library_truth_recordings r
             WHERE r.run_id=$run AND (
               r.segment_count<=0 OR EXISTS (
                 SELECT 1 FROM library_truth_coverages c
                  WHERE c.run_id=r.run_id AND c.recording_key=r.recording_key AND c.requires_review=1
               ) OR r.segment_count<>(
                 SELECT COUNT(DISTINCT c.segment_number) FROM library_truth_coverages c
                  WHERE c.run_id=r.run_id AND c.recording_key=r.recording_key AND c.target_broadcast_key=r.canonical_broadcast_key
               )
             )
            """, ("$run", run));
        var invalidPreferred = ScalarInt(connection, """
            SELECT COUNT(*) FROM library_truth_broadcasts b
             WHERE b.run_id=$run AND NOT EXISTS (
               SELECT 1 FROM library_truth_recordings r
                WHERE r.run_id=b.run_id AND r.recording_key=b.preferred_recording_key
             )
            """, ("$run", run));
        return new CanonicalLibraryAuditSnapshot(
            run, summary.Broadcasts, summary.AdoptedBroadcasts, summary.NeedsAttentionBroadcasts,
            summary.ReviewRecommendedBroadcasts, summary.BlockedBroadcasts, summary.Recordings,
            multipart, incomplete, reviewCoverage, missing, cloudOnly, summary.NeedsAttentionBroadcasts,
            invalidPreferred, duplicateIdentities.Length,
            duplicateIdentities.Sum(count => count - 1), DateTimeOffset.UtcNow);
    }

    public CanonicalDownloadManifest? GetDownloadManifest(string canonicalKey, string? recordingKey = null)
    {
        var plan = GetPlaybackPlan(canonicalKey, recordingKey);
        if (plan is null) return null;
        using var connection = _database.OpenConnection();
        var parts = new List<CanonicalDownloadPart>();
        foreach (var segment in plan.Segments)
        {
            var source = segment.Sources.Where(x => !x.IsMissing).OrderByDescending(x => x.IsPreferred).ThenBy(x => x.MediaFileId).FirstOrDefault();
            if (source is null) return null;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(file_size,0) FROM media_files WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", source.MediaFileId);
            var size = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
            parts.Add(new CanonicalDownloadPart(segment.SegmentNumber,segment.SegmentTotal,segment.LogicalStartMs,segment.LogicalEndMs,source.MediaFileId,source.Path,size,source.StorageState));
        }
        return new CanonicalDownloadManifest(plan.CanonicalKey,plan.RecordingKey,plan.Label,plan.DurationMs,parts);
    }

    public CanonicalTimelineLocation? ResolveTimelineLocation(long episodeId, string? recordingKey = null)
    {
        var resolution = ResolveEpisode(episodeId);
        if (resolution is null) return null;
        var summary = GetSummary();
        if (!summary.IsCutoverReady) return null;

        long mediaFileId = 0;
        string? sourceRecordingKey = null;
        using (var connection = _database.OpenConnection())
        {
            using (var truth = connection.CreateCommand())
            {
                truth.CommandText = """
                    SELECT media_file_id,recording_key
                      FROM library_truth_files
                     WHERE run_id=$run AND current_episode_id=$episode
                       AND canonical_broadcast_key=$key
                     ORDER BY CASE WHEN recording_key=$recording THEN 0 ELSE 1 END,
                              proposed_part,media_file_id
                     LIMIT 1
                    """;
                truth.Parameters.AddWithValue("$run", summary.LatestTruthRunId);
                truth.Parameters.AddWithValue("$episode", episodeId);
                truth.Parameters.AddWithValue("$key", resolution.CanonicalKey);
                truth.Parameters.AddWithValue("$recording", (object?)recordingKey ?? DBNull.Value);
                using var reader = truth.ExecuteReader();
                if (reader.Read())
                {
                    mediaFileId = reader.GetInt64(0);
                    sourceRecordingKey = reader.GetString(1);
                }
            }

            if (mediaFileId <= 0)
            {
                using var incremental = connection.CreateCommand();
                incremental.CommandText = """
                    SELECT mf.id,COALESCE($recording,cb.preferred_recording_key)
                      FROM media_files mf
                      JOIN episode_canonical_map map ON map.episode_id=mf.episode_id
                      JOIN canonical_broadcasts cb ON cb.canonical_key=map.canonical_key
                     WHERE mf.episode_id=$episode AND map.canonical_key=$key
                       AND COALESCE(mf.is_missing,0)=0
                     ORDER BY COALESCE(mf.is_preferred,0) DESC,mf.id
                     LIMIT 1
                    """;
                incremental.Parameters.AddWithValue("$episode", episodeId);
                incremental.Parameters.AddWithValue("$key", resolution.CanonicalKey);
                incremental.Parameters.AddWithValue("$recording", (object?)recordingKey ?? DBNull.Value);
                using var reader = incremental.ExecuteReader();
                if (!reader.Read()) return null;
                mediaFileId = reader.GetInt64(0);
                sourceRecordingKey = reader.GetString(1);
            }
        }

        // Transcript timings belong to their source physical file. Resolve the
        // source recording's offsets even when the listener has chosen another
        // alternate recording for ordinary playback.
        var plan = GetPlaybackPlan(resolution.CanonicalKey, sourceRecordingKey);
        if (plan is null) return null;
        var segment = plan.Segments.FirstOrDefault(x => x.MediaFileIds.Contains(mediaFileId));
        if (segment is null) return null;
        return new CanonicalTimelineLocation(
            resolution.CanonicalKey,
            plan.RecordingKey,
            resolution.RepresentativeEpisodeId,
            episodeId,
            mediaFileId,
            segment.SegmentNumber,
            segment.LogicalStartMs,
            segment.LogicalEndMs);
    }

    public CanonicalPlaybackPlan? GetPreferredPlaybackPlan(string canonicalKey)
    {
        if (string.IsNullOrWhiteSpace(canonicalKey)) return null;
        var latestTruthRunId = GetLatestVerifiedTruthRunId();
        if (latestTruthRunId <= 0) return null;

        using var connection = _database.OpenConnection();
        var adopted = ReadAdoptedPlaybackPlan(connection, canonicalKey);
        if (IsPlaybackPlanComplete(adopted)) return adopted;
        var fallback = ReadTruthPlaybackPlan(connection, latestTruthRunId, canonicalKey);
        return IsPlaybackPlanComplete(fallback) ? fallback : null;
    }

    private static CanonicalPlaybackPlan? ReadAdoptedPlaybackPlan(
        SqliteConnection connection,
        string canonicalKey,
        string? requestedRecordingKey = null)
    {
        using var recording = connection.CreateCommand();
        recording.CommandText = """
            SELECT r.recording_key,r.label,r.duration_ms,r.role
              FROM canonical_broadcasts b
              JOIN recordings r ON r.recording_key=COALESCE($recording,b.preferred_recording_key)
             WHERE b.canonical_key=$key AND r.canonical_key=b.canonical_key
            """;
        recording.Parameters.AddWithValue("$key", canonicalKey);
        recording.Parameters.AddWithValue("$recording", (object?)requestedRecordingKey ?? DBNull.Value);

        string recordingKey;
        string label;
        long durationMs;
        string role;
        using (var reader = recording.ExecuteReader())
        {
            if (!reader.Read()) return null;
            recordingKey = reader.GetString(0);
            label = reader.GetString(1);
            durationMs = reader.GetInt64(2);
            role = reader.GetString(3);
        }

        using var segments = connection.CreateCommand();
        segments.CommandText = """
            SELECT segment_number,segment_total,start_offset_ms,end_offset_ms,media_file_ids_json
              FROM recording_segments
             WHERE recording_key=$recording
             ORDER BY segment_number
            """;
        segments.Parameters.AddWithValue("$recording", recordingKey);
        var planSegments = ReadSegments(connection, segments);
        return planSegments.Count == 0
            ? null
            : new CanonicalPlaybackPlan(
                canonicalKey,
                recordingKey,
                label,
                Math.Max(durationMs, planSegments.Max(x => x.LogicalEndMs)),
                role,
                planSegments);
    }

    private static CanonicalPlaybackPlan? ReadTruthPlaybackPlan(
        SqliteConnection connection,
        long truthRunId,
        string canonicalKey,
        string? requestedRecordingKey = null)
    {
        using var recording = connection.CreateCommand();
        recording.CommandText = """
            SELECT r.recording_key,r.label,r.duration_ms,r.role
              FROM library_truth_broadcasts b
              JOIN library_truth_recordings r
                ON r.run_id=b.run_id
               AND r.recording_key=COALESCE($recording,b.preferred_recording_key)
             WHERE b.run_id=$run AND b.canonical_key=$key
            """;
        recording.Parameters.AddWithValue("$run", truthRunId);
        recording.Parameters.AddWithValue("$key", canonicalKey);
        recording.Parameters.AddWithValue("$recording", (object?)requestedRecordingKey ?? DBNull.Value);

        string recordingKey;
        string label;
        long durationMs;
        string role;
        using (var reader = recording.ExecuteReader())
        {
            if (!reader.Read()) return null;
            recordingKey = reader.GetString(0);
            label = reader.GetString(1);
            durationMs = reader.GetInt64(2);
            role = reader.GetString(3);
        }

        using var segments = connection.CreateCommand();
        segments.CommandText = """
            SELECT segment_number,segment_total,start_offset_ms,end_offset_ms,media_file_ids_json
              FROM library_truth_coverages
             WHERE run_id=$run
               AND source_broadcast_key=$key
               AND recording_key=$recording
               AND requires_review=0
               AND target_broadcast_key=$key
             ORDER BY segment_number
            """;
        segments.Parameters.AddWithValue("$run", truthRunId);
        segments.Parameters.AddWithValue("$key", canonicalKey);
        segments.Parameters.AddWithValue("$recording", recordingKey);
        var planSegments = ReadSegments(connection, segments);
        return planSegments.Count == 0
            ? null
            : new CanonicalPlaybackPlan(
                canonicalKey,
                recordingKey,
                label,
                Math.Max(durationMs, planSegments.Max(x => x.LogicalEndMs)),
                role,
                planSegments);
    }

    private static IReadOnlyList<CanonicalRecordingSegment> ReadSegments(
        SqliteConnection connection,
        SqliteCommand segmentCommand)
    {
        var raw = new List<(int Number, int? Total, long Start, long End, long[] MediaIds)>();
        using (var reader = segmentCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                raw.Add((
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    ParseMediaFileIds(reader.GetString(4))));
            }
        }

        var result = new List<CanonicalRecordingSegment>(raw.Count);
        foreach (var item in raw)
        {
            var sources = ReadMediaSources(connection, item.MediaIds);
            if (sources.Count == 0) continue;
            result.Add(new CanonicalRecordingSegment(
                item.Number,
                item.Total,
                item.Start,
                item.End,
                sources));
        }
        return result;
    }

    private static IReadOnlyList<CanonicalMediaSource> ReadMediaSources(
        SqliteConnection connection,
        IReadOnlyList<long> mediaFileIds)
    {
        if (mediaFileIds.Count == 0) return Array.Empty<CanonicalMediaSource>();

        using var command = connection.CreateCommand();
        var parameterNames = new string[mediaFileIds.Count];
        for (var index = 0; index < mediaFileIds.Count; index++)
        {
            parameterNames[index] = $"$id{index}";
            command.Parameters.AddWithValue(parameterNames[index], mediaFileIds[index]);
        }
        command.CommandText = $"""
            SELECT id,path,COALESCE(duration_ms,0),COALESCE(storage_state,'Missing'),
                   COALESCE(is_missing,0),COALESCE(is_preferred,0)
              FROM media_files
             WHERE id IN ({string.Join(",", parameterNames)})
             ORDER BY COALESCE(is_missing,0),COALESCE(is_preferred,0) DESC,id
            """;

        var result = new List<CanonicalMediaSource>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new CanonicalMediaSource(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetInt32(4) != 0,
                reader.GetInt32(5) != 0));
        }
        return result;
    }


    private static CanonicalRecordingSelectionReason BuildSelectionReason(
        CanonicalPlaybackPlan plan,
        string resolutionPath,
        bool isAdopted,
        bool isHeldFallback,
        string explanation)
        => new(
            plan.CanonicalKey,
            plan.RecordingKey,
            plan.Label,
            plan.Role,
            resolutionPath,
            isAdopted,
            isHeldFallback,
            IsPlaybackPlanComplete(plan),
            plan.Segments.Count,
            plan.Segments.SelectMany(x => x.Sources).Select(x => x.MediaFileId).Distinct().Count(),
            plan.DurationMs,
            explanation);

    private static bool IsPlaybackPlanComplete(CanonicalPlaybackPlan? plan)
    {
        if (plan is null || plan.Segments.Count == 0) return false;
        var ordered = plan.Segments.OrderBy(x => x.SegmentNumber).ToArray();
        var expectedTotal = ordered.Select(x => x.SegmentTotal).Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty(ordered.Length).Max();
        if (expectedTotal != ordered.Length) return false;
        for (var index = 0; index < ordered.Length; index++)
        {
            var segment = ordered[index];
            if (segment.SegmentNumber != index + 1) return false;
            if (segment.LogicalEndMs <= segment.LogicalStartMs) return false;
            if (index > 0 && segment.LogicalStartMs < ordered[index - 1].LogicalEndMs) return false;
            if (!segment.Sources.Any(x => !x.IsMissing)) return false;
        }
        return true;
    }

    private static long[] ParseMediaFileIds(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<long>();
        try
        {
            return JsonSerializer.Deserialize<long[]>(json) ?? Array.Empty<long>();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "A canonical recording segment contains invalid physical-file identity JSON.", ex);
        }
    }

    private static bool TableExists(SqliteConnection connection, string table)
        => ScalarInt(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name",
            ("$name", table)) > 0;

    private static DateOnly? ReadDateOnly(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        return DateOnly.TryParse(
            reader.GetString(ordinal),
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var value)
            ? value
            : null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        return DateTimeOffset.TryParse(
            reader.GetString(ordinal),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var value)
            ? value
            : null;
    }

    private static int ScalarInt(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
        => checked((int)ScalarLong(connection, sql, parameters));

    private static long ScalarLong(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
