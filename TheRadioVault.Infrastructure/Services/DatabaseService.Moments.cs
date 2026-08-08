using Microsoft.Data.Sqlite;
using TheRadioVault.Core.Services;

namespace TheRadioVault.Services;

public sealed partial class DatabaseService
{
    public long AddMoment(long episodeId, long positionMs, string title, string notes)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var id = AddMomentIdempotent(connection, transaction, episodeId, positionMs, title, notes, DateTime.UtcNow.ToString("O"));
        transaction.Commit();
        return id;
    }

    public IReadOnlyList<MomentItem> GetMoments(long? episodeId = null)
    {
        var result = new List<MomentItem>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.id,
                   COALESCE((SELECT survivor_episode_id FROM episode_canonical_map WHERE episode_id=e.id LIMIT 1),e.id),
                   c.name,e.title,e.air_date,m.position_ms,m.title,m.notes,m.created_at
              FROM moments m
              JOIN episodes e ON e.id=m.episode_id
              JOIN collections c ON c.id=e.collection_id
             WHERE ($episode IS NULL OR
                    COALESCE((SELECT canonical_key FROM episode_canonical_map WHERE episode_id=e.id LIMIT 1),NULLIF(e.broadcast_uid,''),'EPISODE-' || e.id)=
                    (SELECT COALESCE((SELECT canonical_key FROM episode_canonical_map WHERE episode_id=target.id LIMIT 1),NULLIF(target.broadcast_uid,''),'EPISODE-' || target.id)
                       FROM episodes target WHERE target.id=$episode))
             ORDER BY m.created_at DESC
            """;
        command.Parameters.AddWithValue("$episode", episodeId.HasValue ? episodeId.Value : DBNull.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var airDate = reader.IsDBNull(4) ? (DateTime?)null : DateTime.Parse(reader.GetString(4));
            result.Add(new MomentItem
            {
                Id = reader.GetInt64(0),
                EpisodeId = reader.GetInt64(1),
                CollectionName = reader.GetString(2),
                EpisodeTitle = reader.GetString(3),
                AirDateDisplay = airDate?.ToString("dd MMM yyyy") ?? "Unknown",
                PositionMs = reader.GetInt64(5),
                Title = reader.GetString(6),
                Notes = reader.GetString(7),
                CreatedAt = DateTime.Parse(reader.GetString(8))
            });
        }

        return result;
    }


    /// <summary>
    /// Performs the conservative legacy Moment repair as an explicit maintenance operation.
    /// Reads stay read-only; this is invoked once during startup and by diagnostic tooling.
    /// </summary>
    public int RepairDuplicateMomentsForMaintenance()
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var repaired = RepairDuplicateMoments(connection, transaction);
        transaction.Commit();
        return repaired;
    }

    public void DeleteMoment(long momentId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM moments WHERE id=$id";
        command.Parameters.AddWithValue("$id", momentId);
        command.ExecuteNonQuery();
    }

    public bool UpdateMoment(long momentId, string title, string notes)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE moments SET title=$title,notes=$notes WHERE id=$id";
        command.Parameters.AddWithValue("$id", momentId);
        command.Parameters.AddWithValue("$title", title?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$notes", notes?.Trim() ?? string.Empty);
        return command.ExecuteNonQuery() > 0;
    }

    private static long AddMomentIdempotent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long episodeId,
        long positionMs,
        string? title,
        string? notes,
        string createdAt)
    {
        var safePosition = Math.Max(0, positionMs);
        var safeTitle = title?.Trim() ?? string.Empty;
        var safeNotes = notes?.Trim() ?? string.Empty;
        var equivalent = FindEquivalentMomentId(connection, transaction, episodeId, safePosition, safeTitle, safeNotes);
        if (equivalent.HasValue) return equivalent.Value;

        long targetEpisodeId;
        using (var target = connection.CreateCommand())
        {
            target.Transaction = transaction;
            target.CommandText = """
                SELECT COALESCE((SELECT survivor_episode_id FROM episode_canonical_map WHERE episode_id=e.id LIMIT 1),e.id)
                  FROM episodes e
                 WHERE e.id=$episode;
                """;
            target.Parameters.AddWithValue("$episode", episodeId);
            var value = target.ExecuteScalar();
            if (value is null || value is DBNull) throw new InvalidOperationException($"Episode {episodeId} no longer exists.");
            targetEpisodeId = Convert.ToInt64(value);
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO moments(episode_id,position_ms,title,notes,created_at)
            VALUES($episode,$position,$title,$notes,$created);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$episode", targetEpisodeId);
        insert.Parameters.AddWithValue("$position", safePosition);
        insert.Parameters.AddWithValue("$title", safeTitle);
        insert.Parameters.AddWithValue("$notes", safeNotes);
        insert.Parameters.AddWithValue("$created", createdAt);
        return Convert.ToInt64(insert.ExecuteScalar());
    }

    private static long? FindEquivalentMomentId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long episodeId,
        long positionMs,
        string title,
        string notes)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH target AS (
                SELECT COALESCE((SELECT canonical_key FROM episode_canonical_map WHERE episode_id=e.id LIMIT 1),NULLIF(e.broadcast_uid,''),'EPISODE-' || e.id) AS canonical_identity
                  FROM episodes e
                 WHERE e.id=$episode
            )
            SELECT m.id
              FROM moments m
              JOIN episodes e ON e.id=m.episode_id
              JOIN target t ON COALESCE((SELECT canonical_key FROM episode_canonical_map WHERE episode_id=e.id LIMIT 1),NULLIF(e.broadcast_uid,''),'EPISODE-' || e.id)=t.canonical_identity
             WHERE m.position_ms BETWEEN $minimum AND $maximum
               AND lower(trim(m.title))=lower(trim($title))
               AND lower(trim(COALESCE(m.notes,'')))=lower(trim($notes))
             ORDER BY m.created_at,m.id
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("$episode", episodeId);
        command.Parameters.AddWithValue("$minimum", Math.Max(0, positionMs - MomentDeduplicationPolicy.PositionToleranceMs));
        command.Parameters.AddWithValue("$maximum", positionMs + MomentDeduplicationPolicy.PositionToleranceMs);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$notes", notes);
        var value = command.ExecuteScalar();
        return value is null || value is DBNull ? null : Convert.ToInt64(value);
    }

    private static int RepairDuplicateMoments(SqliteConnection connection, SqliteTransaction transaction)
    {
        var candidates = new List<LegacyMomentCandidate>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT m.id,
                       COALESCE((SELECT canonical_key FROM episode_canonical_map WHERE episode_id=e.id LIMIT 1),NULLIF(e.broadcast_uid,''),'EPISODE-' || e.id),
                       m.position_ms,m.title,COALESCE(m.notes,''),m.created_at
                  FROM moments m
                  JOIN episodes e ON e.id=m.episode_id
                 ORDER BY 2,lower(trim(m.title)),lower(trim(COALESCE(m.notes,''))),m.position_ms,m.created_at,m.id;
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                DateTimeOffset.TryParse(reader.GetString(5), out var createdAt);
                candidates.Add(new LegacyMomentCandidate(
                    reader.GetInt64(0), reader.GetString(1), Math.Max(0, reader.GetInt64(2)),
                    reader.GetString(3), reader.GetString(4), createdAt));
            }
        }

        var deleteIds = new List<long>();
        foreach (var group in candidates.GroupBy(candidate => new
                 {
                     candidate.CanonicalIdentity,
                     Title = MomentDeduplicationPolicy.NormalizeText(candidate.Title),
                     Notes = MomentDeduplicationPolicy.NormalizeText(candidate.Notes)
                 }))
        {
            var ordered = group.OrderBy(candidate => candidate.PositionMs).ThenBy(candidate => candidate.CreatedAt).ThenBy(candidate => candidate.Id).ToArray();
            var cluster = new List<LegacyMomentCandidate>();
            foreach (var candidate in ordered)
            {
                if (cluster.Count == 0 || candidate.PositionMs - cluster[0].PositionMs <= MomentDeduplicationPolicy.PositionToleranceMs)
                {
                    cluster.Add(candidate);
                    continue;
                }
                CollectLegacyMomentDuplicates(cluster, deleteIds);
                cluster.Clear();
                cluster.Add(candidate);
            }
            CollectLegacyMomentDuplicates(cluster, deleteIds);
        }

        foreach (var id in deleteIds.Distinct())
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM moments WHERE id=$id";
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }
        return deleteIds.Distinct().Count();
    }

    private static void CollectLegacyMomentDuplicates(IReadOnlyList<LegacyMomentCandidate> cluster, ICollection<long> deleteIds)
    {
        if (cluster.Count < 2) return;
        var survivor = cluster
            .OrderBy(candidate => candidate.CreatedAt == default ? DateTimeOffset.MaxValue : candidate.CreatedAt)
            .ThenBy(candidate => candidate.Id)
            .First();
        foreach (var candidate in cluster)
            if (candidate.Id != survivor.Id) deleteIds.Add(candidate.Id);
    }

    private sealed record LegacyMomentCandidate(
        long Id,
        string CanonicalIdentity,
        long PositionMs,
        string Title,
        string Notes,
        DateTimeOffset CreatedAt);
}
