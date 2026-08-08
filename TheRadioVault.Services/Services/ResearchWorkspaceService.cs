using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TheRadioVault.Core.Services;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

/// <summary>
/// Local-authoritative Research workspace boundary shared by desktop shells.
/// The service never writes when a request is marked as remote-owned; remote
/// Research editing must go through the server API rather than a client cache.
/// </summary>
public sealed class ResearchWorkspaceService : IResearchWorkspaceService
{
    private readonly SqliteDatabase _database;

    public ResearchWorkspaceService(SqliteDatabase database) => _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<ResearchWorkspaceOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await MarkPendingCatalogueDateReviewsAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*),
                SUM(CASE WHEN episode_id IS NOT NULL THEN 1 ELSE 0 END),
                SUM(CASE WHEN episode_id IS NULL THEN 1 ELSE 0 END),
                SUM(CASE WHEN needs_review=1 OR EXISTS(
                    SELECT 1 FROM research_reconciliation_candidates rrc
                    WHERE rrc.research_broadcast_id=research_broadcasts.id
                      AND rrc.status='pending' AND rrc.requires_review=1) THEN 1 ELSE 0 END),
                SUM(CASE WHEN EXISTS(
                    SELECT 1 FROM research_conflicts rc
                    WHERE rc.research_broadcast_id=research_broadcasts.id
                      AND rc.resolution='unresolved') THEN 1 ELSE 0 END),
                SUM(CASE WHEN NOT EXISTS(
                    SELECT 1 FROM research_sources rs
                    WHERE rs.research_broadcast_id=research_broadcasts.id) THEN 1 ELSE 0 END),
                SUM(CASE WHEN trim(summary)<>'' THEN 1 ELSE 0 END),
                SUM(CASE WHEN EXISTS(SELECT 1 FROM research_people rp WHERE rp.research_broadcast_id=research_broadcasts.id) THEN 1 ELSE 0 END),
                SUM(CASE WHEN EXISTS(SELECT 1 FROM research_topics rt WHERE rt.research_broadcast_id=research_broadcasts.id) THEN 1 ELSE 0 END),
                SUM(CASE WHEN EXISTS(SELECT 1 FROM research_sources rs WHERE rs.research_broadcast_id=research_broadcasts.id) THEN 1 ELSE 0 END),
                (SELECT MAX(imported_at) FROM research_import_runs),
                MIN(NULLIF(air_date,'')),
                MAX(NULLIF(air_date,''))
            FROM research_broadcasts;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return new ResearchWorkspaceOverview(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null);

        return new ResearchWorkspaceOverview(
            ReadInt(reader, 0), ReadInt(reader, 1), ReadInt(reader, 2), ReadInt(reader, 3), ReadInt(reader, 4),
            ReadInt(reader, 5), ReadInt(reader, 6), ReadInt(reader, 7), ReadInt(reader, 8), ReadInt(reader, 9),
            ParseTimestamp(reader, 10), ParseDate(reader, 11), ParseDate(reader, 12));
    }

    private static async Task MarkPendingCatalogueDateReviewsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var pendingIds = new List<long>();
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT rb.id,c.name,COALESCE(rb.air_date,''),COALESCE(rb.research_json,'{}'),
                       COALESCE(e.air_date,''),COALESCE(e.date_confidence,'Unknown'),
                       COALESCE((SELECT mf.original_filename FROM media_files mf
                                 WHERE mf.episode_id=e.id AND mf.is_missing=0
                                 ORDER BY mf.is_preferred DESC,mf.id LIMIT 1),''),
                       COALESCE(NULLIF(trim(rb.headline),''),NULLIF(trim(e.title),''),''),
                       COALESCE(rb.needs_review,0)
                  FROM research_broadcasts rb
                  JOIN collections c ON c.id=rb.collection_id
                  JOIN episodes e ON e.id=rb.episode_id
                 WHERE rb.episode_id IS NOT NULL;
                """;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var show = CollectionIdentityResolver.Canonicalize(ReadString(reader, 1));
                if (!KnownShowCatalog.SupportsDateReview(show)) continue;

                JsonObject? root = null;
                try { root = JsonNode.Parse(ReadString(reader, 3)) as JsonObject; } catch { }
                var catalogue = root?["research"]?["catalogue"] as JsonObject;
                var status = ReadJsonText(catalogue, "date_review_status");
                if (!IsPendingDateReviewStatus(status)) continue;

                var explicitHint = CatalogueDateService.Resolve(ReadJsonText(catalogue, "date_review_date"));
                var broadcastHint = CatalogueDateService.Resolve(
                    ReadJsonText(root, "broadcast_date"),
                    ReadString(reader, 2),
                    ReadString(reader, 4));
                var releaseHint = CatalogueDateService.Resolve(ReadJsonText(catalogue, "original_release_date"));
                var recordingHint = CatalogueDateService.Resolve(ReadJsonText(catalogue, "recording_date"));
                var filenameHint = CatalogueDateService.Resolve(ReadString(reader, 6), ReadString(reader, 7));

                var currentDate = ParseDate(ReadString(reader, 4));
                var currentConfidence = ReadString(reader, 5);
                var selected = SelectDateReviewCandidate(
                    show, currentDate, explicitHint, broadcastHint, releaseHint, recordingHint, filenameHint);
                var hasExplicitReviewState = !string.IsNullOrWhiteSpace(status);
                var autoResearchDate = IsResearchAdoptedDateConfidence(currentConfidence);
                var uncertainCurrentDate = !currentDate.HasValue || IsUncertainDateConfidence(currentConfidence);
                var conflictsWithCurrentDate = DateHintConflictsWithCurrentDate(selected.Hint, currentDate);
                var hasRoleConflict = DateHintConflictsWithCurrentDate(releaseHint, currentDate)
                    || DateHintConflictsWithCurrentDate(recordingHint, currentDate);

                // Every first-class show uses the same guarded date workflow.
                // Already-settled High/Confirmed/Manual dates stay quiet unless
                // imported evidence conflicts, a previous Research build adopted
                // the date automatically, or the pack explicitly requests review.
                var shouldReview = hasExplicitReviewState
                    || uncertainCurrentDate
                    || autoResearchDate
                    || conflictsWithCurrentDate
                    || hasRoleConflict;
                if (!shouldReview || ReadInt(reader, 8) == 1) continue;

                pendingIds.Add(reader.GetInt64(0));
            }
        }

        if (pendingIds.Count == 0) return;

        // SQLite commonly limits a statement to 999 bound parameters. All-show
        // review can legitimately contain thousands of legacy records, so update
        // them in bounded batches rather than constructing one enormous IN list.
        foreach (var batch in pendingIds.Chunk(400))
        {
            await using var update = connection.CreateCommand();
            var placeholders = string.Join(",", batch.Select((_, index) => $"$dateReview{index}"));
            update.CommandText = $"""
                UPDATE research_broadcasts
                   SET needs_review=1,
                       research_state=CASE WHEN episode_id IS NOT NULL THEN 'conflicting_information' ELSE research_state END
                 WHERE id IN ({placeholders});
                """;
            for (var i = 0; i < batch.Length; i++)
                update.Parameters.AddWithValue($"$dateReview{i}", batch[i]);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<ResearchCollectionOption>> GetCollectionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var families = CollectionIdentityResolver.LoadFamilies(connection);
        var researchCounts = new Dictionary<int, int>();
        var libraryCounts = new Dictionary<int, int>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT c.id,
                       (SELECT COUNT(*) FROM research_broadcasts rb WHERE rb.collection_id=c.id),
                       (SELECT COUNT(*)
                          FROM episodes e
                         WHERE e.collection_id=c.id
                           AND COALESCE(e.hidden,0)=0
                           AND EXISTS(SELECT 1 FROM media_files mf WHERE mf.episode_id=e.id AND COALESCE(mf.is_missing,0)=0)
                           AND (NOT EXISTS(SELECT 1 FROM episode_canonical_map map WHERE map.episode_id=e.id)
                                OR EXISTS(SELECT 1 FROM episode_canonical_map map WHERE map.episode_id=e.id AND map.is_survivor=1)))
                  FROM collections c;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                researchCounts[reader.GetInt32(0)] = ReadInt(reader, 1);
                libraryCounts[reader.GetInt32(0)] = ReadInt(reader, 2);
            }
        }

        var result = new List<ResearchCollectionOption> { new(null, "All shows", 0) };
        var total = 0;
        foreach (var family in families)
        {
            var researchCount = family.CollectionIds.Sum(id => researchCounts.TryGetValue(id, out var count) ? count : 0);
            var libraryCount = family.CollectionIds.Sum(id => libraryCounts.TryGetValue(id, out var count) ? count : 0);
            var availableCount = Math.Max(researchCount, libraryCount);
            if (availableCount <= 0) continue;
            result.Add(new ResearchCollectionOption(
                family.PreferredCollectionId,
                family.CanonicalName,
                availableCount));
            total += availableCount;
        }
        result[0] = result[0] with { RecordCount = total };
        return result;
    }

    public async Task<IReadOnlyList<ResearchBrowseItem>> BrowseAsync(ResearchBrowseQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var result = new List<ResearchBrowseItem>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var family = query.CollectionId.HasValue
            ? CollectionIdentityResolver.ResolveFamily(connection, query.CollectionId.Value)
            : null;
        var collectionPredicate = query.CollectionId.HasValue
            ? CollectionIdentityResolver.AddIdPredicate(command, "rb.collection_id", "browseCollection", family?.CollectionIds ?? Array.Empty<int>())
            : "1=1";
        command.CommandText = $"""
            SELECT rb.id,rb.episode_id,c.id,c.name,rb.air_date,rb.slot,rb.part_number,rb.total_parts,
                   rb.headline,rb.summary,rb.research_state,rb.existence_status,rb.confidence,rb.needs_review,
                   (SELECT COUNT(*) FROM research_conflicts rc
                    WHERE rc.research_broadcast_id=rb.id AND rc.resolution='unresolved'),
                   (SELECT COUNT(*) FROM research_reconciliation_candidates rrc
                    WHERE rrc.research_broadcast_id=rb.id AND rrc.status='pending' AND rrc.requires_review=1),
                   (SELECT COUNT(*) FROM research_sources rs WHERE rs.research_broadcast_id=rb.id),
                   (SELECT COUNT(*) FROM research_people rp WHERE rp.research_broadcast_id=rb.id),
                   (SELECT COUNT(*) FROM research_topics rt WHERE rt.research_broadcast_id=rb.id),
                   rb.updated_at
            FROM research_broadcasts rb
            JOIN collections c ON c.id=rb.collection_id
            WHERE ({collectionPredicate})
              AND ($search='' OR c.name LIKE $like OR rb.headline LIKE $like OR rb.summary LIKE $like
                   OR rb.air_date LIKE $like OR rb.slot LIKE $like
                   OR EXISTS(SELECT 1 FROM research_people rp WHERE rp.research_broadcast_id=rb.id AND rp.name LIKE $like)
                   OR EXISTS(SELECT 1 FROM research_topics rt WHERE rt.research_broadcast_id=rb.id AND rt.topic LIKE $like))
              AND ($reviewOnly=0 OR rb.needs_review=1
                   OR EXISTS(SELECT 1 FROM research_conflicts rc WHERE rc.research_broadcast_id=rb.id AND rc.resolution='unresolved')
                   OR EXISTS(SELECT 1 FROM research_reconciliation_candidates rrc WHERE rrc.research_broadcast_id=rb.id AND rrc.status='pending' AND rrc.requires_review=1))
              AND (
                    $status='all'
                 OR ($status='attention' AND (rb.needs_review=1
                     OR EXISTS(SELECT 1 FROM research_conflicts rc WHERE rc.research_broadcast_id=rb.id AND rc.resolution='unresolved')
                     OR EXISTS(SELECT 1 FROM research_reconciliation_candidates rrc WHERE rrc.research_broadcast_id=rb.id AND rrc.status='pending' AND rrc.requires_review=1)))
                 OR ($status='in_library' AND rb.episode_id IS NOT NULL)
                 OR ($status='missing' AND rb.episode_id IS NULL)
                 OR ($status='review' AND (rb.needs_review=1
                     OR EXISTS(SELECT 1 FROM research_reconciliation_candidates rrc WHERE rrc.research_broadcast_id=rb.id AND rrc.status='pending' AND rrc.requires_review=1)))
                 OR ($status='conflicts' AND EXISTS(SELECT 1 FROM research_conflicts rc WHERE rc.research_broadcast_id=rb.id AND rc.resolution='unresolved'))
                 OR ($status='unsourced' AND NOT EXISTS(SELECT 1 FROM research_sources rs WHERE rs.research_broadcast_id=rb.id))
              )
            ORDER BY COALESCE(rb.air_date,'9999-12-31') DESC,c.sort_name,rb.slot,rb.part_number,rb.id DESC
            LIMIT $limit;
            """;
        var search = query.SearchText?.Trim() ?? string.Empty;
        command.Parameters.AddWithValue("$search", search);
        command.Parameters.AddWithValue("$like", $"%{search}%");
        command.Parameters.AddWithValue("$reviewOnly", query.NeedsReviewOnly ? 1 : 0);
        command.Parameters.AddWithValue("$status", string.IsNullOrWhiteSpace(query.Status) ? "attention" : query.Status);
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 5000));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ResearchBrowseItem(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetInt32(2),
                CollectionIdentityResolver.Canonicalize(reader.GetString(3)),
                ParseDate(reader, 4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetInt32(12),
                reader.GetInt32(13) == 1,
                ReadInt(reader, 14), ReadInt(reader, 15), ReadInt(reader, 16), ReadInt(reader, 17), ReadInt(reader, 18),
                ParseTimestamp(reader, 19) ?? DateTimeOffset.MinValue));
        }
        return result;
    }

    public async Task<IReadOnlyList<UndatedBroadcastItem>> GetUndatedBroadcastsAsync(
        int? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        var result = new List<UndatedBroadcastItem>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var family = collectionId.HasValue
            ? CollectionIdentityResolver.ResolveFamily(connection, collectionId.Value)
            : null;
        var collectionPredicate = collectionId.HasValue
            ? CollectionIdentityResolver.AddIdPredicate(command, "e.collection_id", "undatedCollection", family?.CollectionIds ?? Array.Empty<int>())
            : "1=1";
        command.CommandText = $"""
            SELECT e.id,c.id,c.name,COALESCE(e.title,''),COALESCE(e.date_confidence,'Unknown'),
                   COALESCE((SELECT mf.original_filename FROM media_files mf
                             WHERE mf.episode_id=e.id AND mf.is_missing=0
                             ORDER BY mf.is_preferred DESC,mf.id LIMIT 1),''),
                   COALESCE((SELECT mf.path FROM media_files mf
                             WHERE mf.episode_id=e.id AND mf.is_missing=0
                             ORDER BY mf.is_preferred DESC,mf.id LIMIT 1),''),
                   (SELECT COUNT(*) FROM media_files mf WHERE mf.episode_id=e.id AND mf.is_missing=0),
                   (SELECT ltf.proposed_air_date
                      FROM library_truth_files ltf
                     WHERE ltf.run_id=(SELECT COALESCE(MAX(id),0) FROM library_truth_runs WHERE status='completed')
                       AND ltf.current_episode_id=e.id
                       AND ltf.proposed_air_date IS NOT NULL
                     ORDER BY ltf.confidence_score DESC,ltf.id LIMIT 1),
                   COALESCE((SELECT ltf.evidence_json
                      FROM library_truth_files ltf
                     WHERE ltf.run_id=(SELECT COALESCE(MAX(id),0) FROM library_truth_runs WHERE status='completed')
                       AND ltf.current_episode_id=e.id
                     ORDER BY ltf.confidence_score DESC,ltf.id LIMIT 1),'[]'),
                   COALESCE((SELECT ltf.warnings_json
                      FROM library_truth_files ltf
                     WHERE ltf.run_id=(SELECT COALESCE(MAX(id),0) FROM library_truth_runs WHERE status='completed')
                       AND ltf.current_episode_id=e.id
                     ORDER BY ltf.confidence_score DESC,ltf.id LIMIT 1),'[]'),
                   e.updated_at
              FROM episodes e
              JOIN collections c ON c.id=e.collection_id
             WHERE e.hidden=0
               AND ({collectionPredicate})
               AND (e.air_date IS NULL OR lower(COALESCE(e.date_confidence,'unknown')) IN ('unknown','ambiguous'))
               AND EXISTS(SELECT 1 FROM media_files mf WHERE mf.episode_id=e.id AND mf.is_missing=0)
               AND (NOT EXISTS(SELECT 1 FROM episode_canonical_map ecm WHERE ecm.episode_id=e.id)
                    OR EXISTS(SELECT 1 FROM episode_canonical_map ecm WHERE ecm.episode_id=e.id AND ecm.is_survivor=1))
             ORDER BY c.sort_name,c.name,
                      COALESCE((SELECT mf.original_filename FROM media_files mf WHERE mf.episode_id=e.id ORDER BY mf.is_preferred DESC,mf.id LIMIT 1),''),
                      e.id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new UndatedBroadcastItem(
                reader.GetInt64(0),
                reader.GetInt32(1),
                CollectionIdentityResolver.Canonicalize(ReadString(reader, 2)),
                ReadString(reader, 3),
                ReadString(reader, 4),
                ReadString(reader, 5),
                ReadString(reader, 6),
                ReadInt(reader, 7),
                ParseDate(reader, 8),
                ReadString(reader, 9),
                ReadString(reader, 10),
                ParseTimestamp(reader, 11) ?? DateTimeOffset.MinValue));
        }
        return result;
    }

    public async Task<IReadOnlyList<CatalogueDateReviewItem>> GetCatalogueDateReviewsAsync(
        int? collectionId = null,
        bool includeResolved = false,
        CancellationToken cancellationToken = default)
    {
        var result = new List<CatalogueDateReviewItem>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await MarkPendingCatalogueDateReviewsAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var family = collectionId.HasValue
            ? CollectionIdentityResolver.ResolveFamily(connection, collectionId.Value)
            : null;
        var collectionPredicate = collectionId.HasValue
            ? CollectionIdentityResolver.AddIdPredicate(command, "rb.collection_id", "dateReviewCollection", family?.CollectionIds ?? Array.Empty<int>())
            : "1=1";
        command.CommandText = """
            SELECT rb.id,e.id,c.id,c.name,
                   COALESCE(NULLIF(trim(rb.headline),''),NULLIF(trim(e.title),''),''),
                   COALESCE((SELECT mf.original_filename FROM media_files mf
                             WHERE mf.episode_id=e.id AND mf.is_missing=0
                             ORDER BY mf.is_preferred DESC,mf.id LIMIT 1),''),
                   COALESCE(rb.air_date,''),COALESCE(rb.confidence,0),
                   COALESCE(rb.confidence_reason,''),COALESCE(rb.archive_notes,''),
                   COALESCE(rb.research_json,'{}'),
                   (SELECT COUNT(*) FROM research_sources rs WHERE rs.research_broadcast_id=rb.id),
                   e.air_date,COALESCE(e.date_confidence,'Unknown'),rb.updated_at
              FROM research_broadcasts rb
              JOIN episodes e ON e.id=rb.episode_id
              JOIN collections c ON c.id=rb.collection_id
             WHERE (__COLLECTION_PREDICATE__)
               AND e.hidden=0
               AND EXISTS(SELECT 1 FROM media_files mf WHERE mf.episode_id=e.id AND mf.is_missing=0)
               AND (NOT EXISTS(SELECT 1 FROM episode_canonical_map ecm WHERE ecm.episode_id=e.id)
                    OR EXISTS(SELECT 1 FROM episode_canonical_map ecm WHERE ecm.episode_id=e.id AND ecm.is_survivor=1))
             ORDER BY c.sort_name,c.name,COALESCE(rb.air_date,'9999-12-31'),rb.headline,e.id;
            """;
        command.CommandText = command.CommandText.Replace(
            "__COLLECTION_PREDICATE__",
            collectionPredicate,
            StringComparison.Ordinal);

        var rows = new List<(long ResearchId,long EpisodeId,int CollectionId,string ShowName,string Title,string Filename,
            string ResearchAirDate,int Confidence,string ConfidenceReason,string ArchiveNotes,string ResearchJson,
            int SourceCount,string EpisodeAirDate,string EpisodeDateConfidence,DateTimeOffset UpdatedAt)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((reader.GetInt64(0),reader.GetInt64(1),reader.GetInt32(2),
                    CollectionIdentityResolver.Canonicalize(ReadString(reader,3)),ReadString(reader,4),ReadString(reader,5),
                    ReadString(reader,6),ReadInt(reader,7),ReadString(reader,8),ReadString(reader,9),ReadString(reader,10),
                    ReadInt(reader,11),ReadString(reader,12),ReadString(reader,13),
                    ParseTimestamp(reader,14) ?? DateTimeOffset.MinValue));
            }
        }

        var dateCounts = new Dictionary<(int CollectionId, string Date), int>();
        await using (var collisionSummary = connection.CreateCommand())
        {
            collisionSummary.CommandText = """
                SELECT collection_id,air_date,COUNT(*)
                  FROM episodes
                 WHERE hidden=0 AND air_date IS NOT NULL AND trim(air_date)<>''
                 GROUP BY collection_id,air_date;
                """;
            await using var reader = await collisionSummary.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                dateCounts[(reader.GetInt32(0), ReadString(reader, 1))] = ReadInt(reader, 2);
        }

        foreach (var row in rows)
        {
            if (!KnownShowCatalog.SupportsDateReview(row.ShowName)) continue;

            JsonObject? root = null;
            try { root = JsonNode.Parse(row.ResearchJson) as JsonObject; } catch { }
            var research = root?["research"] as JsonObject;
            var catalogue = research?["catalogue"] as JsonObject;

            var decision = ReadJsonText(catalogue, "date_review_status");
            var pending = IsPendingDateReviewStatus(decision);
            if (!includeResolved && !pending) continue;

            var explicitHint = CatalogueDateService.Resolve(ReadJsonText(catalogue, "date_review_date"));
            var broadcastHint = CatalogueDateService.Resolve(
                FirstValue(row.ResearchAirDate, ReadJsonText(root, "broadcast_date"), row.EpisodeAirDate));
            var releaseDate = ReadJsonText(catalogue, "original_release_date");
            var recordingDate = ReadJsonText(catalogue, "recording_date");
            var releaseHint = CatalogueDateService.Resolve(releaseDate);
            var recordingHint = CatalogueDateService.Resolve(recordingDate);
            var filenameHint = CatalogueDateService.Resolve(row.Filename, row.Title);

            var currentEpisodeDate = ParseDate(row.EpisodeAirDate);
            var selected = SelectDateReviewCandidate(
                row.ShowName, currentEpisodeDate, explicitHint, broadcastHint, releaseHint, recordingHint, filenameHint);
            var autoResearchDate = IsResearchAdoptedDateConfidence(row.EpisodeDateConfidence);
            var uncertainCurrentDate = !currentEpisodeDate.HasValue || IsUncertainDateConfidence(row.EpisodeDateConfidence);
            var conflictsWithCurrentDate = DateHintConflictsWithCurrentDate(selected.Hint, currentEpisodeDate);
            var hasRoleConflict = DateHintConflictsWithCurrentDate(releaseHint, currentEpisodeDate)
                || DateHintConflictsWithCurrentDate(recordingHint, currentEpisodeDate);
            var hasExplicitReviewState = !string.IsNullOrWhiteSpace(decision);
            var needsDecision = hasExplicitReviewState
                || uncertainCurrentDate
                || autoResearchDate
                || conflictsWithCurrentDate
                || hasRoleConflict;

            // Blank status is an implicit candidate, not a reason to clutter the
            // queue. A settled trusted date with matching evidence stays hidden.
            if (pending && !needsDecision) continue;

            var hasCollision = false;
            if (selected.Hint.ExactDate.HasValue)
            {
                var candidateDateText = selected.Hint.ExactDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                dateCounts.TryGetValue((row.CollectionId, candidateDateText), out var sameDayCount);
                var currentItemAlreadyUsesCandidate = currentEpisodeDate.HasValue
                    && currentEpisodeDate.Value == selected.Hint.ExactDate.Value;
                hasCollision = sameDayCount > (currentItemAlreadyUsesCandidate ? 1 : 0);
            }

            var currentDateBasis = uncertainCurrentDate
                ? $"Current Library date confidence: {row.EpisodeDateConfidence}. Confirm or replace it before treating the chronology as settled."
                : string.Empty;
            var basis = FirstValue(
                ReadJsonText(catalogue, "date_review_basis"),
                currentDateBasis,
                row.ConfidenceReason,
                ReadJsonText(catalogue, "research_notes"),
                row.ArchiveNotes,
                filenameHint.HasValue ? "Date clue found in the original filename or title." : string.Empty);
            var provenance = FirstValue(
                ReadJsonText(catalogue, "provenance"),
                ReadJsonText(catalogue, "date_review_notes"));

            result.Add(new CatalogueDateReviewItem(
                row.ResearchId,row.EpisodeId,row.CollectionId,row.ShowName,row.Title,row.Filename,
                selected.Hint.DisplayText,selected.Hint.ExactDate,selected.Kind,releaseDate,recordingDate,basis,provenance,
                Math.Clamp(row.Confidence,0,100),row.SourceCount,hasCollision,
                string.IsNullOrWhiteSpace(decision)?"pending":decision,currentEpisodeDate,row.UpdatedAt));
        }
        return result;
    }

    public async Task ResolveCatalogueDateReviewAsync(
        long researchId,
        CatalogueDateReviewAction action,
        DateOnly? selectedDate = null,
        CancellationToken cancellationToken = default)
    {
        if (researchId<=0) throw new ArgumentOutOfRangeException(nameof(researchId));
        long episodeId;
        string researchJson;
        string currentAirDate;
        string currentDateConfidence;
        string title;
        string originalFilename;
        await using (var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT rb.episode_id,COALESCE(rb.research_json,'{}'),COALESCE(e.air_date,''),COALESCE(e.date_confidence,'Unknown'),
                       COALESCE(NULLIF(trim(rb.headline),''),NULLIF(trim(e.title),''),''),
                       COALESCE((SELECT mf.original_filename FROM media_files mf
                                 WHERE mf.episode_id=e.id AND mf.is_missing=0
                                 ORDER BY mf.is_preferred DESC,mf.id LIMIT 1),'')
                  FROM research_broadcasts rb
                  JOIN episodes e ON e.id=rb.episode_id
                 WHERE rb.id=$id;
                """;
            command.Parameters.AddWithValue("$id",researchId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The date-review item no longer exists.");
            episodeId=reader.GetInt64(0);
            researchJson=ReadString(reader,1);
            currentAirDate=ReadString(reader,2);
            currentDateConfidence=ReadString(reader,3);
            title=ReadString(reader,4);
            originalFilename=ReadString(reader,5);
        }

        JsonObject root;
        try { root=JsonNode.Parse(researchJson) as JsonObject ?? new JsonObject(); }
        catch { root=new JsonObject(); }
        var research=root["research"] as JsonObject ?? new JsonObject();
        root["research"]=research;
        var catalogue=research["catalogue"] as JsonObject ?? new JsonObject();
        research["catalogue"]=catalogue;
        var candidateHint=CatalogueDateService.Resolve(
            selectedDate?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
            ReadJsonText(catalogue,"date_review_date"),
            ReadJsonText(root,"broadcast_date"),
            ReadJsonText(catalogue,"original_release_date"),
            ReadJsonText(catalogue,"recording_date"),
            originalFilename,
            title);
        var candidate = selectedDate ?? candidateHint.ExactDate;
        var candidateText = selectedDate?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)
            ?? candidateHint.DisplayText;

        var autoAdoptedCatalogueDate = !string.IsNullOrWhiteSpace(currentAirDate)
            && IsResearchAdoptedDateConfidence(currentDateConfidence);
        var previousAirDateForDecision = currentAirDate;
        var previousConfidenceForDecision = currentDateConfidence;
        DateOnly? restoredDate = null;

        if (action==CatalogueDateReviewAction.ApproveLibraryDate)
        {
            if (!candidate.HasValue) throw new InvalidOperationException("Choose an exact date before approving it for the Library.");
            await AssignBroadcastDateAsync(episodeId,candidate.Value,cancellationToken).ConfigureAwait(false);
        }
        else if (action==CatalogueDateReviewAction.Reopen)
        {
            var previousDateText=ReadJsonText(catalogue,"date_review_previous_air_date");
            var previousConfidence=ReadJsonText(catalogue,"date_review_previous_confidence");
            var previousDate=DateOnly.TryParse(previousDateText,CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsedPrevious)
                ? parsedPrevious : (DateOnly?)null;
            restoredDate = previousDate;
            await RestoreBroadcastDateAsync(episodeId,previousDate,previousConfidence,cancellationToken).ConfigureAwait(false);
        }

        else if (action==CatalogueDateReviewAction.LeaveUndated)
        {
            await RestoreBroadcastDateAsync(episodeId,null,"Unknown",cancellationToken).ConfigureAwait(false);
        }
        else if (autoAdoptedCatalogueDate
            && action is (CatalogueDateReviewAction.KeepAsRecordingDate
                or CatalogueDateReviewAction.KeepAsReleaseDate))
        {
            await RestoreBroadcastDateAsync(episodeId,null,"Unknown",cancellationToken).ConfigureAwait(false);
        }

        var now=DateTimeOffset.UtcNow.ToString("O",CultureInfo.InvariantCulture);
        if (action==CatalogueDateReviewAction.Reopen)
        {
            catalogue["date_review_status"]="pending";
            catalogue["date_reviewed_at"]=null;
        }
        else
        {
            catalogue["date_review_previous_air_date"]=previousAirDateForDecision;
            catalogue["date_review_previous_confidence"]=previousConfidenceForDecision;
            catalogue["date_review_date"]=!string.IsNullOrWhiteSpace(candidateText)
                ? candidateText
                : ReadJsonText(catalogue,"date_review_date");
            catalogue["date_review_status"]=action switch
            {
                CatalogueDateReviewAction.ApproveLibraryDate=>"approved_library_date",
                CatalogueDateReviewAction.KeepExisting=>"kept_existing",
                CatalogueDateReviewAction.Ignore=>"ignored",
                CatalogueDateReviewAction.KeepAsRecordingDate=>"recording_date_only",
                CatalogueDateReviewAction.KeepAsReleaseDate=>"release_date_only",
                _=>"left_undated"
            };
            catalogue["date_reviewed_at"]=now;
        }

        if (!string.IsNullOrWhiteSpace(candidateText) && action==CatalogueDateReviewAction.KeepAsRecordingDate)
        {
            catalogue["recording_date"]=candidateText;
            var releaseText=ReadJsonText(catalogue,"original_release_date");
            if (string.Equals(releaseText,candidateText,StringComparison.OrdinalIgnoreCase)
                || (candidate.HasValue && CatalogueDateService.ResolveExactDate(releaseText)==candidate))
                catalogue["original_release_date"]=string.Empty;
            root["broadcast_date"]=null;
        }
        if (!string.IsNullOrWhiteSpace(candidateText) && action==CatalogueDateReviewAction.KeepAsReleaseDate)
        {
            catalogue["original_release_date"]=candidateText;
            var recordingText=ReadJsonText(catalogue,"recording_date");
            if (string.Equals(recordingText,candidateText,StringComparison.OrdinalIgnoreCase)
                || (candidate.HasValue && CatalogueDateService.ResolveExactDate(recordingText)==candidate))
                catalogue["recording_date"]=string.Empty;
            root["broadcast_date"]=null;
        }
        if (action==CatalogueDateReviewAction.ApproveLibraryDate && candidate.HasValue)
            root["broadcast_date"] = candidate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        else if (action==CatalogueDateReviewAction.Reopen)
            root["broadcast_date"] = restoredDate.HasValue
                ? restoredDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null;
        else if (action==CatalogueDateReviewAction.LeaveUndated)
            root["broadcast_date"] = null;

        await using var updateConnection=await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var update=updateConnection.CreateCommand();
        update.CommandText="""
            UPDATE research_broadcasts
               SET research_json=$json,
                   air_date=$airDate,
                   needs_review=CASE WHEN $review=1
                                          OR EXISTS(SELECT 1 FROM research_reconciliation_candidates rrc
                                                     WHERE rrc.research_broadcast_id=research_broadcasts.id
                                                       AND rrc.status='pending' AND rrc.requires_review=1)
                                          OR EXISTS(SELECT 1 FROM research_conflicts rc
                                                     WHERE rc.research_broadcast_id=research_broadcasts.id
                                                       AND rc.resolution='unresolved')
                                     THEN 1 ELSE 0 END,
                   research_state=CASE WHEN $review=1
                                            OR EXISTS(SELECT 1 FROM research_reconciliation_candidates rrc
                                                       WHERE rrc.research_broadcast_id=research_broadcasts.id
                                                         AND rrc.status='pending' AND rrc.requires_review=1)
                                            OR EXISTS(SELECT 1 FROM research_conflicts rc
                                                       WHERE rc.research_broadcast_id=research_broadcasts.id
                                                         AND rc.resolution='unresolved')
                                       THEN 'conflicting_information'
                                       WHEN episode_id IS NOT NULL THEN 'in_library'
                                       ELSE research_state END,
                   user_modified=1,updated_at=$now
             WHERE id=$id;
            """;
        update.Parameters.AddWithValue("$json",root.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
        var priorTrustedDate = ParseDate(previousAirDateForDecision);
        var researchAirDate = action==CatalogueDateReviewAction.ApproveLibraryDate && candidate.HasValue
            ? candidate
            : action==CatalogueDateReviewAction.Reopen
                ? restoredDate
                : action is (CatalogueDateReviewAction.KeepExisting
                    or CatalogueDateReviewAction.Ignore
                    or CatalogueDateReviewAction.KeepAsRecordingDate
                    or CatalogueDateReviewAction.KeepAsReleaseDate)
                    ? priorTrustedDate
                    : null;
        update.Parameters.AddWithValue("$airDate",researchAirDate.HasValue
            ? researchAirDate.Value.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)
            : DBNull.Value);
        update.Parameters.AddWithValue("$review",action==CatalogueDateReviewAction.Reopen?1:0);
        update.Parameters.AddWithValue("$now",now);
        update.Parameters.AddWithValue("$id",researchId);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AssignBroadcastDateAsync(long episodeId, DateOnly airDate, CancellationToken cancellationToken = default)
    {
        if (episodeId <= 0) throw new ArgumentOutOfRangeException(nameof(episodeId));
        var earliest = new DateOnly(1920, 1, 1);
        var latest = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
        if (airDate < earliest || airDate > latest)
            throw new ArgumentOutOfRangeException(nameof(airDate), $"Broadcast dates must be between {earliest:dd MMM yyyy} and {latest:dd MMM yyyy}.");

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionBase = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = (SqliteTransaction)transactionBase;

        string? canonicalKey = null;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT canonical_key FROM episode_canonical_map WHERE episode_id=$episodeId";
            lookup.Parameters.AddWithValue("$episodeId", episodeId);
            canonicalKey = Convert.ToString(await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        var episodeIds = new List<long>();
        await using (var members = connection.CreateCommand())
        {
            members.Transaction = transaction;
            members.CommandText = string.IsNullOrWhiteSpace(canonicalKey)
                ? "SELECT id FROM episodes WHERE id=$episodeId"
                : "SELECT episode_id FROM episode_canonical_map WHERE canonical_key=$canonicalKey ORDER BY is_survivor DESC,episode_id";
            if (string.IsNullOrWhiteSpace(canonicalKey)) members.Parameters.AddWithValue("$episodeId", episodeId);
            else members.Parameters.AddWithValue("$canonicalKey", canonicalKey);
            await using var reader = await members.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) episodeIds.Add(reader.GetInt64(0));
        }
        if (episodeIds.Count == 0) throw new InvalidOperationException("The undated broadcast no longer exists.");

        var dateText = airDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var placeholders = string.Join(",", episodeIds.Select((_, index) => $"$episode{index}"));
        var writableTruthRunIds = await GetWritableTruthRunIdsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var truthRunPlaceholders = string.Join(",", writableTruthRunIds.Select((_, index) => $"$truthRun{index}"));

        await using (var updateEpisodes = connection.CreateCommand())
        {
            updateEpisodes.Transaction = transaction;
            updateEpisodes.CommandText = $"""
                UPDATE episodes
                   SET air_date=$airDate,
                       date_confidence='Manual',
                       user_modified=1,
                       metadata_confidence=CASE WHEN metadata_confidence<80 THEN 80 ELSE metadata_confidence END,
                       metadata_confidence_reason=CASE
                           WHEN trim(COALESCE(metadata_confidence_reason,''))='' THEN 'Broadcast date assigned manually in Research'
                           ELSE metadata_confidence_reason END,
                       updated_at=$now
                 WHERE id IN ({placeholders});
                """;
            updateEpisodes.Parameters.AddWithValue("$airDate", dateText);
            updateEpisodes.Parameters.AddWithValue("$now", now);
            AddEpisodeParameters(updateEpisodes, episodeIds);
            await updateEpisodes.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var linkedResearch = new List<(long ResearchId, long EpisodeId)>();
        await using (var readResearch = connection.CreateCommand())
        {
            readResearch.Transaction = transaction;
            readResearch.CommandText = $"SELECT id,episode_id FROM research_broadcasts WHERE episode_id IN ({placeholders})";
            AddEpisodeParameters(readResearch, episodeIds);
            await using var reader = await readResearch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                linkedResearch.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }

        await using (var updateResearch = connection.CreateCommand())
        {
            updateResearch.Transaction = transaction;
            updateResearch.CommandText = $"""
                UPDATE research_broadcasts
                   SET air_date=$airDate,user_modified=1,updated_at=$now
                 WHERE episode_id IN ({placeholders});
                """;
            updateResearch.Parameters.AddWithValue("$airDate", dateText);
            updateResearch.Parameters.AddWithValue("$now", now);
            AddEpisodeParameters(updateResearch, episodeIds);
            await updateResearch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var item in linkedResearch)
            await RecordManualProvenanceAsync(connection, transaction, item.ResearchId, item.EpisodeId,
                "air_date", dateText, 100, now, cancellationToken, "Research undated broadcasts").ConfigureAwait(false);
        var researchEpisodeIds = linkedResearch.Select(x => x.EpisodeId).ToHashSet();
        foreach (var id in episodeIds.Where(id => !researchEpisodeIds.Contains(id)))
            await RecordManualEpisodeProvenanceAsync(connection, transaction, id, "air_date", dateText, 100,
                now, cancellationToken, "Research undated broadcasts").ConfigureAwait(false);

        var canonicalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(canonicalKey)) canonicalKeys.Add(canonicalKey);
        if (writableTruthRunIds.Count > 0)
        await using (var truthKeys = connection.CreateCommand())
        {
            truthKeys.Transaction = transaction;
            truthKeys.CommandText = $"""
                SELECT DISTINCT canonical_broadcast_key
                  FROM library_truth_files
                 WHERE run_id IN ({truthRunPlaceholders})
                   AND current_episode_id IN ({placeholders})
                   AND trim(canonical_broadcast_key)<>'';
                """;
            AddTruthRunParameters(truthKeys, writableTruthRunIds);
            AddEpisodeParameters(truthKeys, episodeIds);
            await using var reader = await truthKeys.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) canonicalKeys.Add(reader.GetString(0));
        }

        if (writableTruthRunIds.Count > 0)
        await using (var updateTruthFiles = connection.CreateCommand())
        {
            updateTruthFiles.Transaction = transaction;
            updateTruthFiles.CommandText = $"""
                UPDATE library_truth_files
                   SET current_air_date=$airDate,proposed_air_date=$airDate,
                       confidence_score=100,confidence='Manual',
                       change_summary='Broadcast date assigned manually in Research'
                 WHERE run_id IN ({truthRunPlaceholders})
                   AND current_episode_id IN ({placeholders});
                """;
            updateTruthFiles.Parameters.AddWithValue("$airDate", dateText);
            AddTruthRunParameters(updateTruthFiles, writableTruthRunIds);
            AddEpisodeParameters(updateTruthFiles, episodeIds);
            await updateTruthFiles.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var key in canonicalKeys)
        {
            await using (var updateCanonical = connection.CreateCommand())
            {
                updateCanonical.Transaction = transaction;
                updateCanonical.CommandText = "UPDATE canonical_broadcasts SET air_date=$airDate,confidence_score=100 WHERE canonical_key=$key";
                updateCanonical.Parameters.AddWithValue("$airDate", dateText);
                updateCanonical.Parameters.AddWithValue("$key", key);
                await updateCanonical.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            if (writableTruthRunIds.Count > 0)
            {
                await using var updateTruthBroadcast = connection.CreateCommand();
                updateTruthBroadcast.Transaction = transaction;
                updateTruthBroadcast.CommandText = $"""
                    UPDATE library_truth_broadcasts
                       SET air_date=$airDate,confidence_score=100,
                           adoption_reason=CASE WHEN trim(COALESCE(adoption_reason,''))='' THEN 'Broadcast date assigned manually in Research' ELSE adoption_reason END
                     WHERE run_id IN ({truthRunPlaceholders})
                       AND canonical_key=$key;
                    """;
                updateTruthBroadcast.Parameters.AddWithValue("$airDate", dateText);
                updateTruthBroadcast.Parameters.AddWithValue("$key", key);
                AddTruthRunParameters(updateTruthBroadcast, writableTruthRunIds);
                await updateTruthBroadcast.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // A manually confirmed date is authoritative enough for the guarded
        // post-cutover promoter. This makes a previously unmapped recording
        // appear in its dated Library location immediately, without waiting
        // for another filesystem scan.
        _ = new CanonicalScanPromotionService(_database).PromoteUnmappedEpisodes();
    }

    private async Task RestoreBroadcastDateAsync(
        long episodeId,
        DateOnly? previousDate,
        string previousConfidence,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionBase = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = (SqliteTransaction)transactionBase;

        string? canonicalKey = null;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT canonical_key FROM episode_canonical_map WHERE episode_id=$episode";
            lookup.Parameters.AddWithValue("$episode",episodeId);
            canonicalKey = Convert.ToString(await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),CultureInfo.InvariantCulture);
        }
        var episodeIds = new List<long>();
        await using (var members = connection.CreateCommand())
        {
            members.Transaction = transaction;
            members.CommandText = string.IsNullOrWhiteSpace(canonicalKey)
                ? "SELECT id FROM episodes WHERE id=$episode"
                : "SELECT episode_id FROM episode_canonical_map WHERE canonical_key=$key";
            if (string.IsNullOrWhiteSpace(canonicalKey)) members.Parameters.AddWithValue("$episode",episodeId);
            else members.Parameters.AddWithValue("$key",canonicalKey);
            await using var reader = await members.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) episodeIds.Add(reader.GetInt64(0));
        }
        if (episodeIds.Count==0) return;

        var dateValue = previousDate.HasValue
            ? previousDate.Value.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)
            : (object)DBNull.Value;
        var confidence = string.IsNullOrWhiteSpace(previousConfidence) ? "Unknown" : previousConfidence.Trim();
        var now = DateTimeOffset.UtcNow.ToString("O",CultureInfo.InvariantCulture);
        var placeholders=string.Join(",",episodeIds.Select((_,index)=>$"$restoreEpisode{index}"));
        var writableTruthRunIds = await GetWritableTruthRunIdsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var truthRunPlaceholders = string.Join(",", writableTruthRunIds.Select((_, index) => $"$restoreTruthRun{index}"));

        await using (var episodes = connection.CreateCommand())
        {
            episodes.Transaction=transaction;
            episodes.CommandText=$"UPDATE episodes SET air_date=$date,date_confidence=$confidence,user_modified=1,updated_at=$now WHERE id IN ({placeholders})";
            episodes.Parameters.AddWithValue("$date",dateValue);
            episodes.Parameters.AddWithValue("$confidence",confidence);
            episodes.Parameters.AddWithValue("$now",now);
            for (var i=0;i<episodeIds.Count;i++) episodes.Parameters.AddWithValue($"$restoreEpisode{i}",episodeIds[i]);
            await episodes.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var research = connection.CreateCommand())
        {
            research.Transaction=transaction;
            research.CommandText=$"UPDATE research_broadcasts SET air_date=$date,updated_at=$now WHERE episode_id IN ({placeholders})";
            research.Parameters.AddWithValue("$date",dateValue);
            research.Parameters.AddWithValue("$now",now);
            for (var i=0;i<episodeIds.Count;i++) research.Parameters.AddWithValue($"$restoreEpisode{i}",episodeIds[i]);
            await research.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (!string.IsNullOrWhiteSpace(canonicalKey))
        {
            await using var canonical=connection.CreateCommand();
            canonical.Transaction=transaction;
            canonical.CommandText="UPDATE canonical_broadcasts SET air_date=$date WHERE canonical_key=$key;";
            canonical.Parameters.AddWithValue("$date",dateValue);
            canonical.Parameters.AddWithValue("$key",canonicalKey);
            await canonical.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (writableTruthRunIds.Count > 0)
            {
                await using var truthBroadcasts=connection.CreateCommand();
                truthBroadcasts.Transaction=transaction;
                truthBroadcasts.CommandText=$"""
                    UPDATE library_truth_broadcasts SET air_date=$date
                     WHERE run_id IN ({truthRunPlaceholders}) AND canonical_key=$key;
                    """;
                truthBroadcasts.Parameters.AddWithValue("$date",dateValue);
                truthBroadcasts.Parameters.AddWithValue("$key",canonicalKey);
                AddTruthRunParameters(truthBroadcasts,writableTruthRunIds,"$restoreTruthRun");
                await truthBroadcasts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        if (writableTruthRunIds.Count > 0)
        await using (var truthFiles=connection.CreateCommand())
        {
            truthFiles.Transaction=transaction;
            truthFiles.CommandText=$"""
                UPDATE library_truth_files SET current_air_date=$date,proposed_air_date=$date
                 WHERE run_id IN ({truthRunPlaceholders})
                   AND current_episode_id IN ({placeholders});
                """;
            truthFiles.Parameters.AddWithValue("$date",dateValue);
            AddTruthRunParameters(truthFiles,writableTruthRunIds,"$restoreTruthRun");
            for (var i=0;i<episodeIds.Count;i++) truthFiles.Parameters.AddWithValue($"$restoreEpisode{i}",episodeIds[i]);
            await truthFiles.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ResearchCoverageShow?> GetCoverageAsync(int collectionId, CancellationToken cancellationToken = default)
    {
        if (collectionId <= 0) return null;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var family = CollectionIdentityResolver.ResolveFamily(connection, collectionId);
        if (family is null) return null;
        var showName = family.CanonicalName;

        DateOnly? firstDate = null;
        DateOnly? lastDate = null;
        await using (var range = connection.CreateCommand())
        {
            var episodePredicate = CollectionIdentityResolver.AddIdPredicate(
                range, "collection_id", "coverageRangeEpisode", family.CollectionIds);
            var researchPredicate = CollectionIdentityResolver.AddIdPredicate(
                range, "collection_id", "coverageRangeResearch", family.CollectionIds);
            range.CommandText = $"""
                WITH dated(value) AS (
                    SELECT air_date FROM episodes WHERE ({episodePredicate}) AND hidden=0 AND air_date IS NOT NULL
                    UNION ALL
                    SELECT air_date FROM research_broadcasts WHERE ({researchPredicate}) AND air_date IS NOT NULL
                )
                SELECT MIN(value),MAX(value) FROM dated;
                """;
            await using var reader = await range.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                firstDate = ParseDate(reader, 0);
                lastDate = ParseDate(reader, 1);
            }
        }
        if (!firstDate.HasValue || !lastDate.HasValue) return null;

        var coverage = new Dictionary<DateOnly, CoverageAccumulator>();
        await using (var research = connection.CreateCommand())
        {
            var researchPredicate = CollectionIdentityResolver.AddIdPredicate(
                research, "rb.collection_id", "coverageResearch", family.CollectionIds);
            research.CommandText = $"""
                SELECT rb.air_date,
                       COUNT(*),
                       MAX(CASE WHEN rb.episode_id IS NOT NULL THEN 1 ELSE 0 END),
                       MAX(CASE WHEN rb.episode_id IS NULL AND rb.existence_status IN ('confirmed_missing','probable_missing') THEN 1 ELSE 0 END),
                       MAX(CASE WHEN trim(COALESCE(rb.headline,''))<>'' THEN 1 ELSE 0 END),
                       MAX(CASE WHEN trim(COALESCE(rb.summary,''))<>'' THEN 1 ELSE 0 END),
                       MAX(CASE WHEN EXISTS(SELECT 1 FROM research_people rp WHERE rp.research_broadcast_id=rb.id) THEN 1 ELSE 0 END),
                       MAX(CASE WHEN EXISTS(SELECT 1 FROM research_topics rt WHERE rt.research_broadcast_id=rb.id) THEN 1 ELSE 0 END),
                       MAX(CASE WHEN EXISTS(SELECT 1 FROM research_sources rs WHERE rs.research_broadcast_id=rb.id) THEN 1 ELSE 0 END),
                       MAX(CASE WHEN rb.episode_id IS NOT NULL AND EXISTS(SELECT 1 FROM transcripts t WHERE t.episode_id=rb.episode_id AND t.status='Complete') THEN 1 ELSE 0 END),
                       MAX(CASE WHEN rb.episode_id IS NOT NULL AND EXISTS(SELECT 1 FROM episodes e WHERE e.id=rb.episode_id AND trim(COALESCE(e.artwork_path,''))<>'') THEN 1 ELSE 0 END),
                       MAX(rb.episode_id),MIN(rb.id)
                  FROM research_broadcasts rb
                 WHERE ({researchPredicate}) AND rb.air_date IS NOT NULL
                 GROUP BY rb.air_date;
                """;
            await using var reader = await research.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var date = ParseDate(reader, 0);
                if (!date.HasValue) continue;
                var item = GetCoverageAccumulator(coverage, date.Value);
                item.BroadcastCount = Math.Max(item.BroadcastCount, ReadInt(reader, 1));
                item.HasAudio |= ReadInt(reader, 2) == 1;
                item.IsKnownMissing |= ReadInt(reader, 3) == 1;
                item.HasResearch = true;
                item.Headline |= ReadInt(reader, 4) == 1;
                item.Summary |= ReadInt(reader, 5) == 1;
                item.People |= ReadInt(reader, 6) == 1;
                item.Topics |= ReadInt(reader, 7) == 1;
                item.Sources |= ReadInt(reader, 8) == 1;
                item.Transcript |= ReadInt(reader, 9) == 1;
                item.Artwork |= ReadInt(reader, 10) == 1;
                item.RepresentativeEpisodeId ??= reader.IsDBNull(11) ? null : reader.GetInt64(11);
                item.ResearchId ??= reader.IsDBNull(12) ? null : reader.GetInt64(12);
            }
        }

        await using (var audio = connection.CreateCommand())
        {
            var audioPredicate = CollectionIdentityResolver.AddIdPredicate(
                audio, "e.collection_id", "coverageAudio", family.CollectionIds);
            audio.CommandText = $"""
                SELECT e.air_date,COUNT(*),
                       MAX(CASE WHEN trim(COALESCE(e.title,''))<>'' THEN 1 ELSE 0 END),
                       MAX(CASE WHEN trim(COALESCE(e.description,''))<>'' THEN 1 ELSE 0 END),
                       MAX(CASE WHEN trim(COALESCE(e.hosts,''))<>'' OR trim(COALESCE(e.callers,''))<>'' OR trim(COALESCE(e.mentioned_people,''))<>''
                                OR EXISTS(SELECT 1 FROM episode_guests eg WHERE eg.episode_id=e.id) THEN 1 ELSE 0 END),
                       MAX(CASE WHEN EXISTS(SELECT 1 FROM episode_tags et WHERE et.episode_id=e.id) THEN 1 ELSE 0 END),
                       MAX(CASE WHEN trim(COALESCE(e.research_sources,''))<>'' THEN 1 ELSE 0 END),
                       MAX(CASE WHEN EXISTS(SELECT 1 FROM transcripts t WHERE t.episode_id=e.id AND t.status='Complete') THEN 1 ELSE 0 END),
                       MAX(CASE WHEN trim(COALESCE(e.artwork_path,''))<>'' THEN 1 ELSE 0 END),
                       MAX(e.id)
                  FROM episodes e
                 WHERE ({audioPredicate}) AND e.hidden=0 AND e.air_date IS NOT NULL
                   AND (NOT EXISTS(SELECT 1 FROM episode_canonical_map ecm WHERE ecm.episode_id=e.id)
                        OR EXISTS(SELECT 1 FROM episode_canonical_map ecm WHERE ecm.episode_id=e.id AND ecm.is_survivor=1))
                 GROUP BY e.air_date;
                """;
            await using var reader = await audio.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var date = ParseDate(reader, 0);
                if (!date.HasValue) continue;
                var item = GetCoverageAccumulator(coverage, date.Value);
                item.BroadcastCount = Math.Max(item.BroadcastCount, ReadInt(reader, 1));
                item.HasAudio = true;
                item.Headline |= ReadInt(reader, 2) == 1;
                item.Summary |= ReadInt(reader, 3) == 1;
                item.People |= ReadInt(reader, 4) == 1;
                item.Topics |= ReadInt(reader, 5) == 1;
                item.Sources |= ReadInt(reader, 6) == 1;
                item.Transcript |= ReadInt(reader, 7) == 1;
                item.Artwork |= ReadInt(reader, 8) == 1;
                item.RepresentativeEpisodeId ??= reader.IsDBNull(9) ? null : reader.GetInt64(9);
            }
        }

        var days = new List<ResearchCoverageDay>();
        for (var date = firstDate.Value; date <= lastDate.Value; date = date.AddDays(1))
        {
            coverage.TryGetValue(date, out var item);
            item ??= new CoverageAccumulator();
            var fields = new[]
            {
                ("headline", item.Headline), ("summary", item.Summary), ("people", item.People),
                ("topics", item.Topics), ("sources", item.Sources), ("transcript", item.Transcript), ("artwork", item.Artwork)
            };
            var present = fields.Count(x => x.Item2);
            var score = item.HasAudio || item.HasResearch ? (int)Math.Round(present * 100d / fields.Length) : 0;
            days.Add(new ResearchCoverageDay(
                date,
                date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                item.HasAudio,
                item.HasResearch,
                item.IsKnownMissing,
                item.BroadcastCount,
                score,
                string.Join(", ", fields.Where(x => !x.Item2).Select(x => x.Item1)),
                item.RepresentativeEpisodeId,
                item.ResearchId));
        }
        return new ResearchCoverageShow(collectionId, showName, firstDate.Value, lastDate.Value, days);
    }

    public async Task<ResearchRecordDetails?> GetDetailsAsync(long researchId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        ResearchBrowseItem? record = null;
        string station = string.Empty;
        string edition = string.Empty;
        string variant = string.Empty;
        string era = string.Empty;
        string episodeType = string.Empty;
        string archiveNotes = string.Empty;
        string confidenceReason = string.Empty;
        string? artworkPath = null;
        string libraryTitle = string.Empty;
        string libraryDescription = string.Empty;
        string researchJson = string.Empty;
        string originalFilename = string.Empty;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT rb.id,rb.episode_id,c.id,c.name,rb.air_date,rb.slot,rb.part_number,rb.total_parts,
                       rb.headline,rb.summary,rb.research_state,rb.existence_status,rb.confidence,rb.needs_review,
                       (SELECT COUNT(*) FROM research_conflicts rc WHERE rc.research_broadcast_id=rb.id AND rc.resolution='unresolved'),
                       (SELECT COUNT(*) FROM research_reconciliation_candidates rrc WHERE rrc.research_broadcast_id=rb.id AND rrc.status='pending' AND rrc.requires_review=1),
                       (SELECT COUNT(*) FROM research_sources rs WHERE rs.research_broadcast_id=rb.id),
                       (SELECT COUNT(*) FROM research_people rp WHERE rp.research_broadcast_id=rb.id),
                       (SELECT COUNT(*) FROM research_topics rt WHERE rt.research_broadcast_id=rb.id),
                       rb.updated_at,rb.station,rb.edition,rb.broadcast_variant,rb.broadcast_era,rb.episode_type,
                       rb.archive_notes,rb.confidence_reason,e.artwork_path,e.title,e.description,rb.research_json,
                       COALESCE((SELECT mf.original_filename FROM media_files mf
                                 WHERE mf.episode_id=rb.episode_id AND COALESCE(mf.is_missing,0)=0
                                 ORDER BY COALESCE(mf.is_preferred,0) DESC,mf.id LIMIT 1),'')
                FROM research_broadcasts rb
                JOIN collections c ON c.id=rb.collection_id
                LEFT JOIN episodes e ON e.id=rb.episode_id
                WHERE rb.id=$id;
                """;
            command.Parameters.AddWithValue("$id", researchId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            record = new ResearchBrowseItem(
                reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3),
                ParseDate(reader, 4), reader.IsDBNull(5) ? string.Empty : reader.GetString(5), reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                reader.IsDBNull(9) ? string.Empty : reader.GetString(9), reader.GetString(10), reader.GetString(11),
                reader.GetInt32(12), reader.GetInt32(13) == 1, ReadInt(reader, 14), ReadInt(reader, 15), ReadInt(reader, 16),
                ReadInt(reader, 17), ReadInt(reader, 18), ParseTimestamp(reader, 19) ?? DateTimeOffset.MinValue);
            station = ReadString(reader, 20);
            edition = ReadString(reader, 21);
            variant = ReadString(reader, 22);
            era = ReadString(reader, 23);
            episodeType = ReadString(reader, 24);
            archiveNotes = ReadString(reader, 25);
            confidenceReason = ReadString(reader, 26);
            artworkPath = reader.IsDBNull(27) ? null : reader.GetString(27);
            libraryTitle = ReadString(reader, 28);
            libraryDescription = ReadString(reader, 29);
            researchJson = ReadString(reader, 30);
            originalFilename = ReadString(reader, 31);
        }

        var people = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = new(), ["guest"] = new(), ["caller"] = new(), ["mentioned"] = new()
        };
        await using (var peopleCommand = connection.CreateCommand())
        {
            peopleCommand.CommandText = "SELECT name,role FROM research_people WHERE research_broadcast_id=$id ORDER BY role,name COLLATE NOCASE";
            peopleCommand.Parameters.AddWithValue("$id", researchId);
            await using var reader = await peopleCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var role = reader.GetString(1);
                if (people.TryGetValue(role, out var list)) list.Add(reader.GetString(0));
            }
        }

        var topics = new List<string>();
        await using (var topicCommand = connection.CreateCommand())
        {
            topicCommand.CommandText = "SELECT topic FROM research_topics WHERE research_broadcast_id=$id ORDER BY topic COLLATE NOCASE";
            topicCommand.Parameters.AddWithValue("$id", researchId);
            await using var reader = await topicCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) topics.Add(reader.GetString(0));
        }

        var sources = new List<ResearchSourceItem>();
        await using (var sourceCommand = connection.CreateCommand())
        {
            sourceCommand.CommandText = """
                SELECT id,url,title,publisher,source_type,confidence,supports,notes,accessed_at
                FROM research_sources WHERE research_broadcast_id=$id ORDER BY confidence DESC,id;
                """;
            sourceCommand.Parameters.AddWithValue("$id", researchId);
            await using var reader = await sourceCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sources.Add(new ResearchSourceItem(reader.GetInt64(0), ReadString(reader, 1), ReadString(reader, 2), ReadString(reader, 3),
                    ReadString(reader, 4), reader.GetInt32(5), ReadString(reader, 6), ReadString(reader, 7), ParseTimestamp(reader, 8)));
            }
        }

        var catalogue = ReadCatalogueMetadata(researchJson);
        if (KnownShowCatalog.SupportsUndatedCatalogueItems(record.ShowName))
        {
            catalogue = catalogue with
            {
                Series = FirstValue(catalogue.Series, record.ShowName),
                Programme = FirstValue(catalogue.Programme, edition),
                Format = FirstValue(catalogue.Format, episodeType),
                OriginalReleaseDate = FirstValue(
                    catalogue.OriginalReleaseDate,
                    record.AirDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    CatalogueDateService.ResolveDisplayText(originalFilename, record.Headline)),
                Network = FirstValue(catalogue.Network, station),
                OriginalFilename = FirstValue(catalogue.OriginalFilename, originalFilename)
            };
        }
        return new ResearchRecordDetails(record, station, edition, variant, era, episodeType, archiveNotes, confidenceReason,
            JoinList(people["host"]), JoinList(people["guest"]), JoinList(people["caller"]), JoinList(people["mentioned"]),
            JoinList(topics), catalogue.Series, catalogue.Programme, catalogue.Format, catalogue.OriginalReleaseDate,
            catalogue.RecordingDate, catalogue.Venue, catalogue.Event, catalogue.Network, catalogue.CatalogueNumber,
            catalogue.OriginalFilename, catalogue.Provenance, catalogue.ResearchNotes,
            artworkPath, libraryTitle, libraryDescription, sources);
    }

    public async Task SaveMetadataAsync(ResearchMetadataUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.IsRemoteOwned)
            throw new InvalidOperationException("This Research record is owned by the connected server. Edit it through the server workspace rather than the local cache.");
        if (!string.IsNullOrWhiteSpace(update.ArtworkPath) && !File.Exists(update.ArtworkPath))
            throw new FileNotFoundException("The selected artwork file no longer exists.", update.ArtworkPath);

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionBase = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = (SqliteTransaction)transactionBase;

        long? episodeId;
        string collectionName;
        string existingResearchJson;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT rb.episode_id,rb.research_json,c.name
                  FROM research_broadcasts rb
                  JOIN collections c ON c.id=rb.collection_id
                 WHERE rb.id=$id
                """;
            read.Parameters.AddWithValue("$id", update.ResearchId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The Research record no longer exists.");
            episodeId = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            existingResearchJson = ReadString(reader, 1);
            collectionName = ReadString(reader, 2);
        }

        var updatedResearchJson = UpdateResearchJson(existingResearchJson, update);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE research_broadcasts SET
                    headline=$headline,summary=$summary,station=$station,edition=$edition,
                    broadcast_variant=$variant,broadcast_era=$era,episode_type=$episodeType,
                    archive_notes=$archiveNotes,confidence=$confidence,confidence_reason=$confidenceReason,
                    research_json=$researchJson,user_modified=1,needs_review=$needsReview,
                    research_state=CASE WHEN $needsReview=1 THEN 'conflicting_information'
                                        WHEN episode_id IS NOT NULL THEN 'in_library'
                                        ELSE 'partially_researched' END,
                    updated_at=$now
                WHERE id=$id;
                """;
            AddMetadataParameters(command, update, updatedResearchJson, now);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The Research record could not be updated.");
        }

        await ReplacePeopleAsync(connection, transaction, update.ResearchId, "host", ParseList(update.Hosts), now, cancellationToken).ConfigureAwait(false);
        await ReplacePeopleAsync(connection, transaction, update.ResearchId, "guest", ParseList(update.Guests), now, cancellationToken).ConfigureAwait(false);
        await ReplacePeopleAsync(connection, transaction, update.ResearchId, "caller", ParseList(update.Callers), now, cancellationToken).ConfigureAwait(false);
        await ReplacePeopleAsync(connection, transaction, update.ResearchId, "mentioned", ParseList(update.MentionedPeople), now, cancellationToken).ConfigureAwait(false);
        await ReplaceTopicsAsync(connection, transaction, update.ResearchId, ParseList(update.Topics), now, cancellationToken).ConfigureAwait(false);

        if (episodeId.HasValue)
        {
            await using var episode = connection.CreateCommand();
            episode.Transaction = transaction;
            episode.CommandText = """
                UPDATE episodes SET
                    title=CASE WHEN trim($headline)<>'' THEN $headline ELSE title END,
                    description=$summary,edition=$edition,broadcast_variant=$variant,broadcast_era=$era,
                    episode_type=$episodeType,archive_notes=$archiveNotes,hosts=$hosts,callers=$callers,
                    mentioned_people=$mentioned,artwork_path=$artwork,user_modified=1,updated_at=$now
                WHERE id=$episodeId;
                """;
            episode.Parameters.AddWithValue("$headline", Clean(update.Headline));
            episode.Parameters.AddWithValue("$summary", Clean(update.Summary));
            episode.Parameters.AddWithValue("$edition", Clean(update.Edition));
            episode.Parameters.AddWithValue("$variant", Clean(update.BroadcastVariant));
            episode.Parameters.AddWithValue("$era", Clean(update.BroadcastEra));
            episode.Parameters.AddWithValue("$episodeType", Clean(update.EpisodeType));
            episode.Parameters.AddWithValue("$archiveNotes", Clean(update.ArchiveNotes));
            episode.Parameters.AddWithValue("$hosts", JoinList(ParseList(update.Hosts)));
            episode.Parameters.AddWithValue("$callers", JoinList(ParseList(update.Callers)));
            episode.Parameters.AddWithValue("$mentioned", JoinList(ParseList(update.MentionedPeople)));
            episode.Parameters.AddWithValue("$artwork", string.IsNullOrWhiteSpace(update.ArtworkPath) ? DBNull.Value : update.ArtworkPath.Trim());
            episode.Parameters.AddWithValue("$now", now);
            episode.Parameters.AddWithValue("$episodeId", episodeId.Value);
            await episode.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await ApplyManualCatalogueDateAsync(
                connection,
                transaction,
                episodeId.Value,
                collectionName,
                update.OriginalReleaseDate,
                update.RecordingDate,
                now,
                cancellationToken).ConfigureAwait(false);
            await ReplaceEpisodeGuestsAsync(connection, transaction, episodeId.Value, ParseList(update.Guests), cancellationToken).ConfigureAwait(false);
            await ReplaceEpisodeTagsAsync(connection, transaction, episodeId.Value, ParseList(update.Topics), cancellationToken).ConfigureAwait(false);
        }

        var provenance = new Dictionary<string, string>
        {
            ["headline"] = Clean(update.Headline), ["summary"] = Clean(update.Summary), ["station"] = Clean(update.Station),
            ["edition"] = Clean(update.Edition), ["broadcast_variant"] = Clean(update.BroadcastVariant),
            ["broadcast_era"] = Clean(update.BroadcastEra), ["episode_type"] = Clean(update.EpisodeType),
            ["archive_notes"] = Clean(update.ArchiveNotes), ["hosts"] = JoinList(ParseList(update.Hosts)),
            ["guests"] = JoinList(ParseList(update.Guests)), ["callers"] = JoinList(ParseList(update.Callers)),
            ["mentioned_people"] = JoinList(ParseList(update.MentionedPeople)), ["topics"] = JoinList(ParseList(update.Topics)),
            ["catalogue_series"] = Clean(update.CatalogueSeries), ["catalogue_programme"] = Clean(update.CatalogueProgramme),
            ["catalogue_format"] = Clean(update.CatalogueFormat), ["original_release_date"] = Clean(update.OriginalReleaseDate),
            ["recording_date"] = Clean(update.RecordingDate), ["venue"] = Clean(update.Venue), ["event"] = Clean(update.Event),
            ["network"] = Clean(update.Network), ["catalogue_number"] = Clean(update.CatalogueNumber),
            ["original_filename"] = Clean(update.OriginalFilename), ["provenance"] = Clean(update.Provenance),
            ["research_notes"] = Clean(update.ResearchNotes)
        };
        foreach (var field in provenance)
            await RecordManualProvenanceAsync(connection, transaction, update.ResearchId, episodeId, field.Key, field.Value, update.Confidence, now, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyManualCatalogueDateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long episodeId,
        string collectionName,
        string originalReleaseDate,
        string recordingDate,
        string now,
        CancellationToken cancellationToken)
    {
        if (!KnownShowCatalog.SupportsDateReview(collectionName)) return;
        var exactDate = CatalogueDateService.ResolveExactDate(originalReleaseDate, recordingDate);
        if (!exactDate.HasValue) return;
        var dateText = exactDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var rows = new List<(long ResearchId,string Json)>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id,COALESCE(research_json,'{}') FROM research_broadcasts WHERE episode_id=$id";
            read.Parameters.AddWithValue("$id", episodeId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add((reader.GetInt64(0),ReadString(reader,1)));
        }

        foreach (var row in rows)
        {
            JsonObject root;
            try { root = JsonNode.Parse(row.Json) as JsonObject ?? new JsonObject(); }
            catch { root = new JsonObject(); }
            var research = root["research"] as JsonObject ?? new JsonObject();
            root["research"] = research;
            var catalogue = research["catalogue"] as JsonObject ?? new JsonObject();
            research["catalogue"] = catalogue;
            var existingDecision = ReadJsonText(catalogue,"date_review_status");
            if (!existingDecision.Equals("approved_library_date",StringComparison.OrdinalIgnoreCase))
            {
                catalogue["date_review_status"] = "pending";
                catalogue["date_review_date"] = dateText;
                catalogue["date_review_basis"] = "Exact catalogue date entered in Metadata Studio; confirm how it should be used.";
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE research_broadcasts
                   SET research_json=$json,needs_review=1,research_state='conflicting_information',updated_at=$now
                 WHERE id=$id;
                """;
            update.Parameters.AddWithValue("$json",root.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
            update.Parameters.AddWithValue("$now",now);
            update.Parameters.AddWithValue("$id",row.ResearchId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SetNeedsReviewAsync(long researchId, bool needsReview, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE research_broadcasts SET needs_review=$review,
                research_state=CASE WHEN $review=1 THEN 'conflicting_information'
                                    WHEN episode_id IS NOT NULL THEN 'in_library'
                                    ELSE 'partially_researched' END,
                updated_at=$now
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$review", needsReview ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", researchId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ResearchSourceDiagnostic>> GetSourceDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<ResearchSourceDiagnostic>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_type,COUNT(*),COUNT(DISTINCT research_broadcast_id),CAST(ROUND(AVG(confidence)) AS INTEGER),
                   SUM(CASE WHEN trim(url)='' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN accessed_at IS NULL OR accessed_at='' OR datetime(accessed_at)<datetime('now','-18 months') THEN 1 ELSE 0 END)
            FROM research_sources
            GROUP BY source_type
            ORDER BY COUNT(*) DESC,source_type;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new ResearchSourceDiagnostic(reader.GetString(0), ReadInt(reader, 1), ReadInt(reader, 2), ReadInt(reader, 3), ReadInt(reader, 4), ReadInt(reader, 5)));

        await using var unsourced = connection.CreateCommand();
        unsourced.CommandText = "SELECT COUNT(*) FROM research_broadcasts rb WHERE NOT EXISTS(SELECT 1 FROM research_sources rs WHERE rs.research_broadcast_id=rb.id)";
        var unsourcedCount = Convert.ToInt32(await unsourced.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (unsourcedCount > 0) result.Insert(0, new ResearchSourceDiagnostic("unsourced_records", 0, unsourcedCount, 0, unsourcedCount, unsourcedCount));
        return result;
    }

    public async Task<IReadOnlyList<ResearchImportRunSummary>> GetImportHistoryAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        var result = new List<ResearchImportRunSummary>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rir.id,rir.package_name,rir.package_sha256,rir.schema_version,rir.app_version,rir.imported_at,
                   rir.imported_count,rir.matched_count,rir.missing_count,rir.conflict_count,
                   (SELECT COUNT(*) FROM research_import_changes ric WHERE ric.import_run_id=rir.id AND ric.decision IN ('applied','created')),
                   (SELECT COUNT(*) FROM research_import_changes ric WHERE ric.import_run_id=rir.id AND ric.decision='merged'),
                   (SELECT COUNT(*) FROM research_import_changes ric WHERE ric.import_run_id=rir.id AND ric.decision IN ('preserved','retained_missing')),
                   (SELECT COUNT(*) FROM research_import_changes ric WHERE ric.import_run_id=rir.id AND ric.decision='protected'),
                   rir.status,rir.restored_change_count,rir.blocked_rollback_count
            FROM research_import_runs rir
            ORDER BY rir.imported_at DESC,rir.id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ResearchImportRunSummary(reader.GetInt64(0), ReadString(reader, 1), ReadString(reader, 2), reader.GetInt32(3),
                ReadString(reader, 4), ParseTimestamp(reader, 5) ?? DateTimeOffset.MinValue, ReadInt(reader, 6), ReadInt(reader, 7),
                ReadInt(reader, 8), ReadInt(reader, 9), ReadInt(reader, 10), ReadInt(reader, 11), ReadInt(reader, 12), ReadInt(reader, 13),
                ReadString(reader, 14), ReadInt(reader, 15), ReadInt(reader, 16)));
        }
        return result;
    }

    private static async Task ReplacePeopleAsync(SqliteConnection connection, SqliteTransaction transaction, long researchId, string role,
        IReadOnlyList<string> names, string now, CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM research_people WHERE research_broadcast_id=$id AND role=$role";
            delete.Parameters.AddWithValue("$id", researchId);
            delete.Parameters.AddWithValue("$role", role);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var name in names)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO research_people(research_broadcast_id,name,role,confidence,source_id,notes,created_at) VALUES($id,$name,$role,100,NULL,'',$now)";
            insert.Parameters.AddWithValue("$id", researchId);
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$role", role);
            insert.Parameters.AddWithValue("$now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReplaceTopicsAsync(SqliteConnection connection, SqliteTransaction transaction, long researchId,
        IReadOnlyList<string> topics, string now, CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM research_topics WHERE research_broadcast_id=$id";
            delete.Parameters.AddWithValue("$id", researchId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var topic in topics)
        {
            var canonicalTopic = await ResolveCanonicalTopicAsync(connection, transaction, topic, cancellationToken).ConfigureAwait(false);
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO research_topics(research_broadcast_id,topic,confidence,source_id,notes,created_at) VALUES($id,$topic,100,NULL,'',$now)";
            insert.Parameters.AddWithValue("$id", researchId);
            insert.Parameters.AddWithValue("$topic", canonicalTopic);
            insert.Parameters.AddWithValue("$now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReplaceEpisodeGuestsAsync(SqliteConnection connection, SqliteTransaction transaction, long episodeId,
        IReadOnlyList<string> guests, CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM episode_guests WHERE episode_id=$episodeId";
            delete.Parameters.AddWithValue("$episodeId", episodeId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var guest in guests)
        {
            await using (var ensure = connection.CreateCommand())
            {
                ensure.Transaction = transaction;
                ensure.CommandText = "INSERT OR IGNORE INTO guests(name) VALUES($name)";
                ensure.Parameters.AddWithValue("$name", guest);
                await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = "INSERT OR IGNORE INTO episode_guests(episode_id,guest_id) SELECT $episodeId,id FROM guests WHERE name=$name";
            link.Parameters.AddWithValue("$episodeId", episodeId);
            link.Parameters.AddWithValue("$name", guest);
            await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReplaceEpisodeTagsAsync(SqliteConnection connection, SqliteTransaction transaction, long episodeId,
        IReadOnlyList<string> topics, CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM episode_tags WHERE episode_id=$episodeId";
            delete.Parameters.AddWithValue("$episodeId", episodeId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var topic in topics)
        {
            var canonicalTopic = await ResolveCanonicalTopicAsync(connection, transaction, topic, cancellationToken).ConfigureAwait(false);
            await using (var ensure = connection.CreateCommand())
            {
                ensure.Transaction = transaction;
                ensure.CommandText = "INSERT OR IGNORE INTO tags(name) VALUES($name)";
                ensure.Parameters.AddWithValue("$name", canonicalTopic);
                await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = "INSERT OR IGNORE INTO episode_tags(episode_id,tag_id) SELECT $episodeId,id FROM tags WHERE name=$name";
            link.Parameters.AddWithValue("$episodeId", episodeId);
            link.Parameters.AddWithValue("$name", canonicalTopic);
            await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> ResolveCanonicalTopicAsync(SqliteConnection connection, SqliteTransaction transaction,
        string topic, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.canonical_name FROM canonical_topic_aliases a
            JOIN canonical_topics c ON c.id=a.topic_id
            WHERE a.alias=$topic COLLATE NOCASE LIMIT 1;
            """;
        command.Parameters.AddWithValue("$topic", topic);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string ?? topic;
    }

    private static async Task RecordManualProvenanceAsync(SqliteConnection connection, SqliteTransaction transaction, long researchId,
        long? episodeId, string fieldName, string value, int confidence, string now, CancellationToken cancellationToken,
        string sourceLabel = "Avalonia Metadata Studio")
    {
        await using (var deactivate = connection.CreateCommand())
        {
            deactivate.Transaction = transaction;
            deactivate.CommandText = "UPDATE research_field_provenance SET active=0,superseded_at=$now WHERE research_broadcast_id=$researchId AND field_name=$field AND active=1";
            deactivate.Parameters.AddWithValue("$now", now);
            deactivate.Parameters.AddWithValue("$researchId", researchId);
            deactivate.Parameters.AddWithValue("$field", fieldName);
            await deactivate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO research_field_provenance(research_broadcast_id,episode_id,field_name,value_text,source_kind,source_label,
                import_run_id,confidence,evidence_count,protected,active,created_at,superseded_at)
            VALUES($researchId,$episodeId,$field,$value,'manual',$sourceLabel,NULL,$confidence,0,1,1,$now,NULL);
            """;
        insert.Parameters.AddWithValue("$researchId", researchId);
        insert.Parameters.AddWithValue("$episodeId", episodeId.HasValue ? episodeId.Value : DBNull.Value);
        insert.Parameters.AddWithValue("$field", fieldName);
        insert.Parameters.AddWithValue("$value", value);
        insert.Parameters.AddWithValue("$sourceLabel", sourceLabel);
        insert.Parameters.AddWithValue("$confidence", Math.Clamp(confidence, 0, 100));
        insert.Parameters.AddWithValue("$now", now);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }


    private static async Task RecordManualEpisodeProvenanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long episodeId,
        string fieldName,
        string value,
        int confidence,
        string now,
        CancellationToken cancellationToken,
        string sourceLabel)
    {
        await using (var deactivate = connection.CreateCommand())
        {
            deactivate.Transaction = transaction;
            deactivate.CommandText = "UPDATE research_field_provenance SET active=0,superseded_at=$now WHERE episode_id=$episodeId AND field_name=$field AND active=1";
            deactivate.Parameters.AddWithValue("$now", now);
            deactivate.Parameters.AddWithValue("$episodeId", episodeId);
            deactivate.Parameters.AddWithValue("$field", fieldName);
            await deactivate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO research_field_provenance(research_broadcast_id,episode_id,field_name,value_text,source_kind,source_label,
                import_run_id,confidence,evidence_count,protected,active,created_at,superseded_at)
            VALUES(NULL,$episodeId,$field,$value,'manual',$sourceLabel,NULL,$confidence,0,1,1,$now,NULL);
            """;
        insert.Parameters.AddWithValue("$episodeId", episodeId);
        insert.Parameters.AddWithValue("$field", fieldName);
        insert.Parameters.AddWithValue("$value", value);
        insert.Parameters.AddWithValue("$sourceLabel", sourceLabel);
        insert.Parameters.AddWithValue("$confidence", Math.Clamp(confidence, 0, 100));
        insert.Parameters.AddWithValue("$now", now);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddEpisodeParameters(SqliteCommand command, IReadOnlyList<long> episodeIds)
    {
        for (var index = 0; index < episodeIds.Count; index++)
            command.Parameters.AddWithValue($"$episode{index}", episodeIds[index]);
    }

    private static void AddTruthRunParameters(
        SqliteCommand command,
        IReadOnlyList<long> truthRunIds,
        string prefix = "$truthRun")
    {
        for (var index = 0; index < truthRunIds.Count; index++)
            command.Parameters.AddWithValue($"{prefix}{index}", truthRunIds[index]);
    }

    private static async Task<IReadOnlyList<long>> GetWritableTruthRunIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var result = new List<long>(2);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT truth_run_id
              FROM library_truth_adoption_runs
             WHERE status='completed'
               AND commit_verified=1
               AND foreign_key_violations=0
               AND lower(integrity_check)='ok'
             ORDER BY id DESC
             LIMIT 1;
            """;
        var adoptedValue = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (adoptedValue is not null && adoptedValue is not DBNull)
            result.Add(Convert.ToInt64(adoptedValue, CultureInfo.InvariantCulture));

        command.CommandText = "SELECT COALESCE(MAX(id),0) FROM library_truth_runs WHERE status='completed';";
        var latestValue = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var latestRunId = Convert.ToInt64(latestValue ?? 0, CultureInfo.InvariantCulture);
        if (latestRunId > 0 && !result.Contains(latestRunId)) result.Add(latestRunId);
        return result;
    }

    private static CoverageAccumulator GetCoverageAccumulator(
        IDictionary<DateOnly, CoverageAccumulator> coverage,
        DateOnly date)
    {
        if (coverage.TryGetValue(date, out var existing)) return existing;
        var created = new CoverageAccumulator();
        coverage[date] = created;
        return created;
    }

    private sealed class CoverageAccumulator
    {
        public bool HasAudio { get; set; }
        public bool HasResearch { get; set; }
        public bool IsKnownMissing { get; set; }
        public int BroadcastCount { get; set; }
        public bool Headline { get; set; }
        public bool Summary { get; set; }
        public bool People { get; set; }
        public bool Topics { get; set; }
        public bool Sources { get; set; }
        public bool Transcript { get; set; }
        public bool Artwork { get; set; }
        public long? RepresentativeEpisodeId { get; set; }
        public long? ResearchId { get; set; }
    }

    private static void AddMetadataParameters(SqliteCommand command, ResearchMetadataUpdate update, string researchJson, string now)
    {
        command.Parameters.AddWithValue("$headline", Clean(update.Headline));
        command.Parameters.AddWithValue("$summary", Clean(update.Summary));
        command.Parameters.AddWithValue("$station", Clean(update.Station));
        command.Parameters.AddWithValue("$edition", Clean(update.Edition));
        command.Parameters.AddWithValue("$variant", Clean(update.BroadcastVariant));
        command.Parameters.AddWithValue("$era", Clean(update.BroadcastEra));
        command.Parameters.AddWithValue("$episodeType", Clean(update.EpisodeType));
        command.Parameters.AddWithValue("$archiveNotes", Clean(update.ArchiveNotes));
        command.Parameters.AddWithValue("$confidence", Math.Clamp(update.Confidence, 0, 100));
        command.Parameters.AddWithValue("$confidenceReason", Clean(update.ConfidenceReason));
        command.Parameters.AddWithValue("$researchJson", researchJson);
        command.Parameters.AddWithValue("$needsReview", update.NeedsReview ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", update.ResearchId);
    }


    private static string UpdateResearchJson(string existingJson, ResearchMetadataUpdate update)
    {
        JsonObject root;
        try { root = JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject(); }
        catch { root = new JsonObject(); }

        var research = root["research"] as JsonObject;
        if (research is null)
        {
            research = new JsonObject();
            root["research"] = research;
        }
        research["headline"] = Clean(update.Headline);
        research["summary"] = Clean(update.Summary);
        research["edition"] = Clean(update.Edition);
        research["archive_notes"] = Clean(update.ArchiveNotes);
        research["guests"] = ToJsonArray(ParseList(update.Guests));
        research["topics"] = ToJsonArray(ParseList(update.Topics));

        var broadcast = research["broadcast"] as JsonObject;
        if (broadcast is null)
        {
            broadcast = new JsonObject();
            research["broadcast"] = broadcast;
        }
        broadcast["station"] = Clean(update.Station);
        broadcast["variant"] = Clean(update.BroadcastVariant);
        broadcast["era"] = Clean(update.BroadcastEra);
        broadcast["episode_type"] = Clean(update.EpisodeType);

        var catalogue = research["catalogue"] as JsonObject;
        if (catalogue is null)
        {
            catalogue = new JsonObject();
            research["catalogue"] = catalogue;
        }
        catalogue["series"] = Clean(update.CatalogueSeries);
        catalogue["programme"] = Clean(update.CatalogueProgramme);
        catalogue["format"] = Clean(update.CatalogueFormat);
        catalogue["original_release_date"] = Clean(update.OriginalReleaseDate);
        catalogue["recording_date"] = Clean(update.RecordingDate);
        catalogue["venue"] = Clean(update.Venue);
        catalogue["event"] = Clean(update.Event);
        catalogue["network"] = Clean(update.Network);
        catalogue["catalogue_number"] = Clean(update.CatalogueNumber);
        catalogue["original_filename"] = Clean(update.OriginalFilename);
        catalogue["provenance"] = Clean(update.Provenance);
        catalogue["research_notes"] = Clean(update.ResearchNotes);

        var people = research["people"] as JsonObject;
        if (people is null)
        {
            people = new JsonObject();
            research["people"] = people;
        }
        people["hosts"] = ToJsonArray(ParseList(update.Hosts));
        people["guests"] = ToJsonArray(ParseList(update.Guests));
        people["callers"] = ToJsonArray(ParseList(update.Callers));
        people["mentioned_people"] = ToJsonArray(ParseList(update.MentionedPeople));

        var quality = research["quality"] as JsonObject;
        if (quality is null)
        {
            quality = new JsonObject();
            research["quality"] = quality;
        }
        quality["confidence"] = Math.Clamp(update.Confidence, 0, 100);
        quality["confidence_reason"] = Clean(update.ConfidenceReason);

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static CatalogueMetadataValues ReadCatalogueMetadata(string existingJson)
    {
        try
        {
            var root = JsonNode.Parse(existingJson) as JsonObject;
            var research = root?["research"] as JsonObject;
            var catalogue = research?["catalogue"] as JsonObject;
            if (catalogue is null) return CatalogueMetadataValues.Empty;
            return new CatalogueMetadataValues(
                ReadJsonText(catalogue, "series"), ReadJsonText(catalogue, "programme"),
                ReadJsonText(catalogue, "format"), ReadJsonText(catalogue, "original_release_date"),
                ReadJsonText(catalogue, "recording_date"), ReadJsonText(catalogue, "venue"),
                ReadJsonText(catalogue, "event"), ReadJsonText(catalogue, "network"),
                ReadJsonText(catalogue, "catalogue_number"), ReadJsonText(catalogue, "original_filename"),
                ReadJsonText(catalogue, "provenance"), ReadJsonText(catalogue, "research_notes"));
        }
        catch { return CatalogueMetadataValues.Empty; }
    }

    private static string ReadJsonText(JsonObject? source, string name)
    {
        if (source?[name] is not JsonNode node) return string.Empty;
        try { return node.GetValue<string>()?.Trim() ?? string.Empty; }
        catch { return node.ToJsonString().Trim('"').Trim(); }
    }

    private static bool IsPendingDateReviewStatus(string? value)
    {
        var status = value?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(status)
            || status.Equals("pending", StringComparison.OrdinalIgnoreCase)
            || status.Equals("reopened", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUncertainDateConfidence(string? value)
    {
        var confidence = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(confidence)) return true;
        if (IsResearchAdoptedDateConfidence(confidence)) return false;
        return !confidence.Equals("High", StringComparison.OrdinalIgnoreCase)
            && !confidence.Equals("Confirmed", StringComparison.OrdinalIgnoreCase)
            && !confidence.Equals("Manual", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DateHintConflictsWithCurrentDate(CatalogueDateHint hint, DateOnly? currentDate)
    {
        if (!hint.HasValue || !currentDate.HasValue) return false;
        if (hint.ExactDate.HasValue) return hint.ExactDate.Value != currentDate.Value;

        if (hint.Precision == CatalogueDatePrecision.Year
            && int.TryParse(hint.DisplayText, NumberStyles.None, CultureInfo.InvariantCulture, out var year))
            return currentDate.Value.Year != year;

        if (hint.Precision == CatalogueDatePrecision.Month
            && DateTime.TryParseExact(hint.DisplayText, "MMMM yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var month))
            return currentDate.Value.Year != month.Year || currentDate.Value.Month != month.Month;

        return false;
    }

    private static (CatalogueDateHint Hint, string Kind) SelectDateReviewCandidate(
        string showName,
        DateOnly? currentDate,
        CatalogueDateHint explicitHint,
        CatalogueDateHint broadcastHint,
        CatalogueDateHint releaseHint,
        CatalogueDateHint recordingHint,
        CatalogueDateHint filenameHint)
    {
        var candidates = new (CatalogueDateHint Hint, string Kind)[]
        {
            (explicitHint, "Proposed date from Research"),
            (broadcastHint, "Proposed broadcast date"),
            (releaseHint, "Release / archive date"),
            (recordingHint, "Recording / event date"),
            (filenameHint, "Filename / title clue")
        };

        if (currentDate.HasValue)
        {
            foreach (var candidate in candidates)
            {
                if (DateHintConflictsWithCurrentDate(candidate.Hint, currentDate))
                    return candidate;
            }
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Hint.HasValue) return candidate;
        }

        return (
            CatalogueDateHint.None,
            KnownShowCatalog.SupportsUndatedCatalogueItems(showName)
                ? "Missing or uncertain programme date"
                : "Missing or uncertain broadcast date");
    }

    private static bool IsResearchAdoptedDateConfidence(string? value)
    {
        var confidence = value?.Trim() ?? string.Empty;
        return confidence.StartsWith("Research exact date",StringComparison.OrdinalIgnoreCase)
            || confidence.StartsWith("Research authoritative",StringComparison.OrdinalIgnoreCase)
            || confidence.StartsWith("Research manual",StringComparison.OrdinalIgnoreCase)
            || confidence.StartsWith("Research date approved",StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstValue(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private sealed record CatalogueMetadataValues(
        string Series, string Programme, string Format, string OriginalReleaseDate,
        string RecordingDate, string Venue, string Event, string Network,
        string CatalogueNumber, string OriginalFilename, string Provenance, string ResearchNotes)
    {
        public static CatalogueMetadataValues Empty { get; } = new(
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    private static IReadOnlyList<string> ParseList(string? value) => (value ?? string.Empty)
        .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string JoinList(IEnumerable<string> values) => string.Join(", ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
    private static int ReadInt(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    private static string ReadString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);

    private static DateOnly? ParseDate(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        return DateOnly.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;
    }

    private static DateOnly? ParseDate(string? value)
        => DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static DateTimeOffset? ParseTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        return DateTimeOffset.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : null;
    }
}
