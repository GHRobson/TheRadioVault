using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TheRadioVault.Core.LibraryTruth;
using TheRadioVault.Core.Services;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

/// <summary>
/// Adds trustworthy files discovered after the guarded Library Truth adoption
/// to the live canonical model. The sealed adoption run remains immutable; new
/// broadcasts are appended as canonical rows and same-broadcast additions are
/// attached as extra recordings.
/// </summary>
public sealed class CanonicalScanPromotionService
{
    private readonly SqliteDatabase _database;

    public CanonicalScanPromotionService(SqliteDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public CanonicalScanPromotionResult PromoteUnmappedEpisodes()
    {
        using var connection = _database.OpenConnection();
        var truthRunId = LatestVerifiedTruthRunId(connection);
        if (truthRunId <= 0 || !TableExists(connection, "canonical_broadcasts") ||
            !TableExists(connection, "episode_canonical_map"))
            return CanonicalScanPromotionResult.Empty;

        var candidates = ReadUnmappedCandidates(connection);
        if (candidates.Count == 0) return CanonicalScanPromotionResult.Empty;

        var broadcastsAdded = 0;
        var recordingsAdded = 0;
        var episodesMapped = 0;
        var needsReview = 0;

        foreach (var group in candidates
                     .GroupBy(BuildCanonicalKey, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var episodes = group.OrderBy(x => x.PartNumber).ThenBy(x => x.EpisodeId).ToArray();
            if (!IsSafeForAutomaticPromotion(episodes))
            {
                needsReview += episodes.Length;
                continue;
            }

            using var transaction = connection.BeginTransaction();
            if (TruthBroadcastExists(connection, transaction, truthRunId, group.Key) &&
                !CanonicalBroadcastExists(connection, transaction, group.Key))
            {
                // A held/review baseline identity must never be silently adopted by
                // an ordinary scan. It remains visible through the sealed truth view.
                needsReview += episodes.Length;
                transaction.Rollback();
                continue;
            }

            var existingSurvivor = ReadExistingSurvivor(connection, transaction, group.Key);
            var representative = existingSurvivor ?? ChooseRepresentative(episodes).EpisodeId;
            var broadcastExists = CanonicalBroadcastExists(connection, transaction, group.Key);
            var recordingKey = CreateRecordingKey(connection, transaction, group.Key, episodes.Min(x => x.EpisodeId));
            var segments = BuildSegments(episodes);
            if (segments.Count == 0)
            {
                needsReview += episodes.Length;
                transaction.Rollback();
                continue;
            }

            var adoptedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var preferred = !broadcastExists || CanonicalPreferredRecordingIsEmpty(connection, transaction, group.Key);
            if (!broadcastExists)
            {
                var representativeEpisode = episodes.First(x => x.EpisodeId == representative);
                InsertCanonicalBroadcast(connection, transaction, group.Key, representativeEpisode,
                    recordingKey, truthRunId, adoptedAt);
                broadcastsAdded++;
            }

            InsertRecording(connection, transaction, recordingKey, group.Key, segments, preferred,
                truthRunId, adoptedAt);
            InsertSegmentsAndCoverages(connection, transaction, recordingKey, group.Key, segments,
                truthRunId, adoptedAt);
            recordingsAdded++;

            if (preferred && broadcastExists)
                SetPreferredRecording(connection, transaction, group.Key, recordingKey);

            foreach (var episode in episodes)
            {
                using var map = connection.CreateCommand();
                map.Transaction = transaction;
                map.CommandText = """
                    INSERT INTO episode_canonical_map(
                        episode_id,canonical_key,survivor_episode_id,is_survivor,source_truth_run_id,adopted_at)
                    VALUES($episode,$key,$survivor,$isSurvivor,$run,$adopted)
                    ON CONFLICT(episode_id) DO NOTHING
                    """;
                map.Parameters.AddWithValue("$episode", episode.EpisodeId);
                map.Parameters.AddWithValue("$key", group.Key);
                map.Parameters.AddWithValue("$survivor", representative);
                map.Parameters.AddWithValue("$isSurvivor", episode.EpisodeId == representative ? 1 : 0);
                map.Parameters.AddWithValue("$run", truthRunId);
                map.Parameters.AddWithValue("$adopted", adoptedAt);
                episodesMapped += map.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return new CanonicalScanPromotionResult(
            broadcastsAdded,
            recordingsAdded,
            episodesMapped,
            needsReview);
    }

    private static IReadOnlyList<ScannedEpisodeCandidate> ReadUnmappedCandidates(SqliteConnection connection)
    {
        var builders = new Dictionary<long, CandidateBuilder>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.id,c.name,e.air_date,COALESCE(e.date_confidence,'Unknown'),
                   COALESCE(e.broadcast_slot,''),COALESCE(e.part_number,1),e.total_parts,
                   COALESCE(e.title,''),COALESCE(e.broadcast_uid,''),
                   COALESCE(e.metadata_confidence,0),COALESCE(e.metadata_confidence_reason,''),
                   e.date_added,
                   mf.id,mf.path,COALESCE(mf.original_filename,''),COALESCE(mf.duration_ms,0),
                   COALESCE(ps.duration_ms,0),COALESCE(mf.storage_state,'Missing'),
                   COALESCE(mf.is_missing,0),COALESCE(mf.is_preferred,0),COALESCE(mf.full_hash,'')
              FROM episodes e
              JOIN collections c ON c.id=e.collection_id
              JOIN media_files mf ON mf.episode_id=e.id AND COALESCE(mf.is_missing,0)=0
              LEFT JOIN playback_state ps ON ps.episode_id=e.id
             WHERE COALESCE(e.hidden,0)=0
               AND NOT EXISTS(
                   SELECT 1 FROM episode_canonical_map map WHERE map.episode_id=e.id
               )
             ORDER BY e.id,COALESCE(mf.is_preferred,0) DESC,mf.id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var episodeId = reader.GetInt64(0);
            if (!builders.TryGetValue(episodeId, out var builder))
            {
                builder = new CandidateBuilder(
                    episodeId,
                    reader.GetString(1),
                    ReadDateOnly(reader, 2),
                    reader.GetString(3),
                    reader.GetString(4),
                    Math.Max(1, reader.GetInt32(5)),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetInt32(9),
                    reader.GetString(10),
                    ReadDateTimeOffset(reader, 11) ?? DateTimeOffset.UtcNow);
                builders.Add(episodeId, builder);
            }

            builder.Media.Add(new ScannedMediaCandidate(
                reader.GetInt64(12),
                reader.GetString(13),
                reader.GetString(14),
                Math.Max(reader.GetInt64(15), reader.GetInt64(16)),
                reader.GetString(17),
                reader.GetInt32(18) != 0,
                reader.GetInt32(19) != 0,
                reader.GetString(20)));
        }

        return builders.Values.Select(x => x.Build()).ToArray();
    }

    private static string BuildCanonicalKey(ScannedEpisodeCandidate episode)
    {
        var identityMedia = episode.Media
            .OrderBy(x => x.IsMissing)
            .ThenByDescending(x => x.IsPreferred)
            .ThenBy(x => x.MediaFileId)
            .First();
        return LibraryTruthIdentity.Build(
            episode.CollectionName,
            episode.AirDate,
            BroadcastSlotNormalizer.Canonicalize(episode.BroadcastSlot),
            identityMedia.MediaFileId,
            identityMedia.FullHash);
    }

    private static bool IsSafeForAutomaticPromotion(IReadOnlyList<ScannedEpisodeCandidate> episodes)
    {
        if (episodes.Count == 0 ||
            episodes.Any(x => x.CollectionName.Equals(KnownShowCatalog.Unsorted, StringComparison.OrdinalIgnoreCase)) ||
            episodes.Any(x => x.Media.All(media => media.IsMissing)))
            return false;

        var datedBroadcast = episodes.All(x =>
            x.AirDate.HasValue &&
            (x.DateConfidence.Equals("High", StringComparison.OrdinalIgnoreCase) ||
             x.DateConfidence.Equals("Manual", StringComparison.OrdinalIgnoreCase)));
        if (datedBroadcast) return true;

        // Interview and segment collections are often distributed as a guest or
        // topic catalogue rather than dated daily broadcasts. Once a file belongs
        // to one of those explicit show collections it is safe to expose even when
        // its filename yields no useful title: the canonical identity remains tied
        // to that physical file/hash, so unrelated interviews cannot be merged.
        // Research export preserves the original filename for later enrichment.
        return episodes.All(x =>
            !x.AirDate.HasValue &&
            KnownShowCatalog.SupportsUndatedCatalogueItems(x.CollectionName));
    }

    private static ScannedEpisodeCandidate ChooseRepresentative(IReadOnlyList<ScannedEpisodeCandidate> episodes)
        => episodes
            .OrderBy(x => x.PartNumber == 1 ? 0 : 1)
            .ThenByDescending(x => x.Media.Any(media => media.IsPreferred && !media.IsMissing))
            .ThenBy(x => x.EpisodeId)
            .First();

    private static IReadOnlyList<PromotionSegment> BuildSegments(IReadOnlyList<ScannedEpisodeCandidate> episodes)
    {
        var grouped = episodes
            .GroupBy(x => Math.Max(1, x.PartNumber))
            .OrderBy(x => x.Key)
            .ToArray();
        if (grouped.Length == 0) return Array.Empty<PromotionSegment>();

        var declaredTotal = episodes.Select(x => x.TotalParts).Where(x => x.HasValue && x.GetValueOrDefault() > 0)
            .Select(x => x.GetValueOrDefault()).DefaultIfEmpty(grouped.Max(x => x.Key)).Max();
        var offset = 0L;
        var result = new List<PromotionSegment>(grouped.Length);
        foreach (var part in grouped)
        {
            var media = part.SelectMany(x => x.Media)
                .Where(x => !x.IsMissing)
                .GroupBy(x => x.MediaFileId)
                .Select(x => x.First())
                .OrderByDescending(x => x.IsPreferred)
                .ThenBy(x => x.MediaFileId)
                .ToArray();
            if (media.Length == 0) continue;
            var duration = Math.Max(1L, media.Max(x => x.DurationMs));
            result.Add(new PromotionSegment(
                part.Key,
                Math.Max(declaredTotal, part.Key),
                offset,
                checked(offset + duration),
                media));
            offset = checked(offset + duration);
        }
        return result;
    }

    private static void InsertCanonicalBroadcast(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string canonicalKey,
        ScannedEpisodeCandidate representative,
        string preferredRecordingKey,
        long truthRunId,
        string adoptedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO canonical_broadcasts(
                canonical_key,collection_name,air_date,broadcast_slot,preferred_recording_key,
                confidence_score,source_truth_run_id,adopted_at)
            VALUES($key,$collection,$date,$slot,$recording,$confidence,$run,$adopted)
            """;
        command.Parameters.AddWithValue("$key", canonicalKey);
        command.Parameters.AddWithValue("$collection", representative.CollectionName);
        command.Parameters.AddWithValue("$date", representative.AirDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$slot", BroadcastSlotNormalizer.Canonicalize(representative.BroadcastSlot));
        command.Parameters.AddWithValue("$recording", preferredRecordingKey);
        command.Parameters.AddWithValue("$confidence", Math.Clamp(representative.MetadataConfidence, 0, 100));
        command.Parameters.AddWithValue("$run", truthRunId);
        command.Parameters.AddWithValue("$adopted", adoptedAt);
        command.ExecuteNonQuery();
    }

    private static void InsertRecording(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string recordingKey,
        string canonicalKey,
        IReadOnlyList<PromotionSegment> segments,
        bool preferred,
        long truthRunId,
        string adoptedAt)
    {
        var complete = IsComplete(segments);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO recordings(
                recording_key,canonical_key,label,duration_ms,role,completeness_score,
                preferred_score,is_preferred,source_truth_run_id,adopted_at)
            VALUES($recording,$canonical,$label,$duration,$role,$complete,$preferredScore,$preferred,$run,$adopted)
            """;
        command.Parameters.AddWithValue("$recording", recordingKey);
        command.Parameters.AddWithValue("$canonical", canonicalKey);
        command.Parameters.AddWithValue("$label", segments.Count > 1 ? "Scanned multipart recording" : "Scanned recording");
        command.Parameters.AddWithValue("$duration", segments.Max(x => x.EndOffsetMs));
        command.Parameters.AddWithValue("$role", segments.Count > 1
            ? (complete ? "Multipart assembly" : "Incomplete multipart recording")
            : "Full capture");
        command.Parameters.AddWithValue("$complete", complete ? 100 : 50);
        command.Parameters.AddWithValue("$preferredScore", preferred ? 100 : 60);
        command.Parameters.AddWithValue("$preferred", preferred ? 1 : 0);
        command.Parameters.AddWithValue("$run", truthRunId);
        command.Parameters.AddWithValue("$adopted", adoptedAt);
        command.ExecuteNonQuery();
    }

    private static void InsertSegmentsAndCoverages(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string recordingKey,
        string canonicalKey,
        IReadOnlyList<PromotionSegment> segments,
        long truthRunId,
        string adoptedAt)
    {
        foreach (var segment in segments)
        {
            var mediaJson = JsonSerializer.Serialize(segment.Media.Select(x => x.MediaFileId).ToArray());
            using (var insertSegment = connection.CreateCommand())
            {
                insertSegment.Transaction = transaction;
                insertSegment.CommandText = """
                    INSERT INTO recording_segments(
                        recording_key,segment_number,segment_total,start_offset_ms,end_offset_ms,
                        media_file_ids_json,source_truth_run_id,adopted_at)
                    VALUES($recording,$number,$total,$start,$end,$media,$run,$adopted)
                    """;
                insertSegment.Parameters.AddWithValue("$recording", recordingKey);
                insertSegment.Parameters.AddWithValue("$number", segment.Number);
                insertSegment.Parameters.AddWithValue("$total", segment.Total);
                insertSegment.Parameters.AddWithValue("$start", segment.StartOffsetMs);
                insertSegment.Parameters.AddWithValue("$end", segment.EndOffsetMs);
                insertSegment.Parameters.AddWithValue("$media", mediaJson);
                insertSegment.Parameters.AddWithValue("$run", truthRunId);
                insertSegment.Parameters.AddWithValue("$adopted", adoptedAt);
                insertSegment.ExecuteNonQuery();
            }

            using var coverage = connection.CreateCommand();
            coverage.Transaction = transaction;
            coverage.CommandText = """
                INSERT INTO recording_coverages(
                    recording_key,segment_number,target_canonical_key,coverage_kind,
                    start_offset_ms,end_offset_ms,confidence_score,requires_review,evidence,
                    source_truth_run_id,adopted_at)
                VALUES($recording,$number,$canonical,'Direct segment',$start,$end,90,0,$evidence,$run,$adopted)
                """;
            coverage.Parameters.AddWithValue("$recording", recordingKey);
            coverage.Parameters.AddWithValue("$number", segment.Number);
            coverage.Parameters.AddWithValue("$canonical", canonicalKey);
            coverage.Parameters.AddWithValue("$start", segment.StartOffsetMs);
            coverage.Parameters.AddWithValue("$end", segment.EndOffsetMs);
            coverage.Parameters.AddWithValue("$evidence", "Added by the post-cutover library scan from the indexed physical file set.");
            coverage.Parameters.AddWithValue("$run", truthRunId);
            coverage.Parameters.AddWithValue("$adopted", adoptedAt);
            coverage.ExecuteNonQuery();
        }
    }

    private static bool IsComplete(IReadOnlyList<PromotionSegment> segments)
    {
        if (segments.Count == 0) return false;
        var expected = segments.Max(x => x.Total);
        if (expected != segments.Count) return false;
        for (var index = 0; index < segments.Count; index++)
            if (segments[index].Number != index + 1 || segments[index].EndOffsetMs <= segments[index].StartOffsetMs)
                return false;
        return true;
    }

    private static string CreateRecordingKey(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string canonicalKey,
        long seedEpisodeId)
    {
        var root = $"{canonicalKey}|SCAN-E{seedEpisodeId}";
        var candidate = root;
        var suffix = 2;
        while (RecordingExists(connection, transaction, candidate))
            candidate = $"{root}-{suffix++}";
        return candidate;
    }

    private static long LatestVerifiedTruthRunId(SqliteConnection connection)
        => ScalarLong(connection, null, """
            SELECT COALESCE((
                SELECT truth_run_id
                  FROM library_truth_adoption_runs
                 WHERE status='completed' AND commit_verified=1
                   AND foreign_key_violations=0 AND lower(integrity_check)='ok'
                 ORDER BY id DESC LIMIT 1
            ),0)
            """);

    private static bool TruthBroadcastExists(SqliteConnection connection, SqliteTransaction transaction, long runId, string key)
        => ScalarLong(connection, transaction,
            "SELECT COUNT(*) FROM library_truth_broadcasts WHERE run_id=$run AND canonical_key=$key",
            ("$run", runId), ("$key", key)) > 0;

    private static bool CanonicalBroadcastExists(SqliteConnection connection, SqliteTransaction transaction, string key)
        => ScalarLong(connection, transaction,
            "SELECT COUNT(*) FROM canonical_broadcasts WHERE canonical_key=$key", ("$key", key)) > 0;

    private static bool RecordingExists(SqliteConnection connection, SqliteTransaction transaction, string key)
        => ScalarLong(connection, transaction,
            "SELECT COUNT(*) FROM recordings WHERE recording_key=$key", ("$key", key)) > 0;

    private static long? ReadExistingSurvivor(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT survivor_episode_id
              FROM episode_canonical_map
             WHERE canonical_key=$key
             ORDER BY is_survivor DESC,episode_id
             LIMIT 1
            """;
        command.Parameters.AddWithValue("$key", key);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static bool CanonicalPreferredRecordingIsEmpty(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(preferred_recording_key,'') FROM canonical_broadcasts WHERE canonical_key=$key";
        command.Parameters.AddWithValue("$key", key);
        return string.IsNullOrWhiteSpace(Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    private static void SetPreferredRecording(SqliteConnection connection, SqliteTransaction transaction, string key, string recordingKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE canonical_broadcasts SET preferred_recording_key=$recording WHERE canonical_key=$key";
        command.Parameters.AddWithValue("$recording", recordingKey);
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string name)
        => ScalarLong(connection, null,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name", ("$name", name)) > 0;

    private static long ScalarLong(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static DateOnly? ReadDateOnly(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        return DateOnly.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var value) ? value : null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        return DateTimeOffset.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var value) ? value : null;
    }

    private sealed class CandidateBuilder
    {
        public CandidateBuilder(long episodeId, string collectionName, DateOnly? airDate, string dateConfidence,
            string broadcastSlot, int partNumber, int? totalParts, string title, string broadcastUid,
            int metadataConfidence, string metadataConfidenceReason, DateTimeOffset dateAdded)
        {
            EpisodeId = episodeId;
            CollectionName = collectionName;
            AirDate = airDate;
            DateConfidence = dateConfidence;
            BroadcastSlot = broadcastSlot;
            PartNumber = partNumber;
            TotalParts = totalParts;
            Title = title;
            BroadcastUid = broadcastUid;
            MetadataConfidence = metadataConfidence;
            MetadataConfidenceReason = metadataConfidenceReason;
            DateAdded = dateAdded;
        }

        public long EpisodeId { get; }
        public string CollectionName { get; }
        public DateOnly? AirDate { get; }
        public string DateConfidence { get; }
        public string BroadcastSlot { get; }
        public int PartNumber { get; }
        public int? TotalParts { get; }
        public string Title { get; }
        public string BroadcastUid { get; }
        public int MetadataConfidence { get; }
        public string MetadataConfidenceReason { get; }
        public DateTimeOffset DateAdded { get; }
        public List<ScannedMediaCandidate> Media { get; } = new();

        public ScannedEpisodeCandidate Build() => new(EpisodeId, CollectionName, AirDate, DateConfidence,
            BroadcastSlot, PartNumber, TotalParts, Title, BroadcastUid, MetadataConfidence,
            MetadataConfidenceReason, DateAdded, Media.ToArray());
    }

    private sealed record ScannedEpisodeCandidate(
        long EpisodeId,
        string CollectionName,
        DateOnly? AirDate,
        string DateConfidence,
        string BroadcastSlot,
        int PartNumber,
        int? TotalParts,
        string Title,
        string BroadcastUid,
        int MetadataConfidence,
        string MetadataConfidenceReason,
        DateTimeOffset DateAdded,
        IReadOnlyList<ScannedMediaCandidate> Media);

    private sealed record ScannedMediaCandidate(
        long MediaFileId,
        string Path,
        string OriginalFilename,
        long DurationMs,
        string StorageState,
        bool IsMissing,
        bool IsPreferred,
        string FullHash);

    private sealed record PromotionSegment(
        int Number,
        int Total,
        long StartOffsetMs,
        long EndOffsetMs,
        IReadOnlyList<ScannedMediaCandidate> Media);
}
