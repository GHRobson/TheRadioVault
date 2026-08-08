using System.Globalization;
using Microsoft.Data.Sqlite;
using TheRadioVault.Core.Domain;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

public sealed class ArchiveService : IArchiveService
{
    private readonly SqliteDatabase _database;

    public ArchiveService(SqliteDatabase database) => _database = database;

    public async Task<IReadOnlyList<BroadcastSummary>> SearchAsync(ArchiveSearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var results = new List<BroadcastSummary>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var where = new List<string>();
        if (request.CollectionId.HasValue)
        {
            where.Add("e.collection_id = $collectionId");
            command.Parameters.AddWithValue("$collectionId", request.CollectionId.Value);
        }
        if (request.Year.HasValue)
        {
            where.Add("substr(e.air_date,1,4) = $year");
            command.Parameters.AddWithValue("$year", request.Year.Value.ToString("0000", CultureInfo.InvariantCulture));
        }
        if (request.Month.HasValue)
        {
            where.Add("substr(e.air_date,6,2) = $month");
            command.Parameters.AddWithValue("$month", request.Month.Value.ToString("00", CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(request.SearchText))
        {
            where.Add("(e.title LIKE $search OR e.description LIKE $search OR c.name LIKE $search OR e.air_date LIKE $search)");
            command.Parameters.AddWithValue("$search", $"%{request.SearchText.Trim()}%");
        }
        if (request.Favourite.HasValue)
        {
            where.Add("e.favourite = $favourite");
            command.Parameters.AddWithValue("$favourite", request.Favourite.Value ? 1 : 0);
        }
        if (!request.IncludeHidden) where.Add("COALESCE(e.hidden,0)=0");

        command.CommandText = $"""
            SELECT e.id, COALESCE(e.broadcast_uid,'BROADCAST-' || e.id), e.collection_id, c.name,
                   e.air_date, COALESCE(e.part_number,1), e.title, e.description,
                   COALESCE(e.favourite,0), COALESCE(e.hidden,0),
                   COALESCE(p.position_ms,0), COALESCE(NULLIF(p.duration_ms,0), mf.duration_ms,0),
                   COALESCE(p.completed,0), p.last_played_at, e.artwork_path
            FROM episodes e
            JOIN collections c ON c.id=e.collection_id
            LEFT JOIN playback_state p ON p.episode_id=e.id
            LEFT JOIN media_files mf ON mf.episode_id=e.id AND COALESCE(mf.is_preferred,1)=1
            {(where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where))}
            GROUP BY e.id
            ORDER BY e.air_date {(request.NewestFirst ? "DESC" : "ASC")}, COALESCE(e.part_number,1), e.id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(request.Limit, 1, 5000));
        command.Parameters.AddWithValue("$offset", Math.Max(0, request.Offset));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(ReadBroadcast(reader));
        return results;
    }

    public async Task<BroadcastSummary?> GetByIdAsync(long broadcastId, CancellationToken cancellationToken = default)
    {
        var items = await SearchByIdAsync(broadcastId, cancellationToken);
        return items.Count == 0 ? null : items[0];
    }

    private async Task<IReadOnlyList<BroadcastSummary>> SearchByIdAsync(long broadcastId, CancellationToken cancellationToken)
    {
        var results = new List<BroadcastSummary>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.id, COALESCE(e.broadcast_uid,'BROADCAST-' || e.id), e.collection_id, c.name,
                   e.air_date, COALESCE(e.part_number,1), e.title, e.description,
                   COALESCE(e.favourite,0), COALESCE(e.hidden,0),
                   COALESCE(p.position_ms,0), COALESCE(NULLIF(p.duration_ms,0), mf.duration_ms,0),
                   COALESCE(p.completed,0), p.last_played_at, e.artwork_path
            FROM episodes e
            JOIN collections c ON c.id=e.collection_id
            LEFT JOIN playback_state p ON p.episode_id=e.id
            LEFT JOIN media_files mf ON mf.episode_id=e.id AND COALESCE(mf.is_preferred,1)=1
            WHERE e.id=$id
            GROUP BY e.id;
            """;
        command.Parameters.AddWithValue("$id", broadcastId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(ReadBroadcast(reader));
        return results;
    }

    public Task<IReadOnlyList<ArchivePeriodSummary>> GetYearsAsync(int collectionId, CancellationToken cancellationToken = default)
        => GetPeriodsAsync(collectionId, null, cancellationToken);

    public Task<IReadOnlyList<ArchivePeriodSummary>> GetMonthsAsync(int collectionId, int year, CancellationToken cancellationToken = default)
        => GetPeriodsAsync(collectionId, year, cancellationToken);

    private async Task<IReadOnlyList<ArchivePeriodSummary>> GetPeriodsAsync(int collectionId, int? year, CancellationToken cancellationToken)
    {
        var results = new List<ArchivePeriodSummary>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var isYear = year is null;
        command.CommandText = $"""
            SELECT CAST(substr(e.air_date,{(isYear ? 1 : 6)},{(isYear ? 4 : 2)}) AS INTEGER) period_value,
                   COUNT(*) episode_count,
                   SUM(CASE WHEN COALESCE(p.completed,0)=1 THEN 1 ELSE 0 END) completed_count,
                   SUM(CASE WHEN COALESCE(e.favourite,0)=1 THEN 1 ELSE 0 END) favourite_count,
                   MAX(e.artwork_path) artwork_path
            FROM episodes e
            LEFT JOIN playback_state p ON p.episode_id=e.id
            WHERE e.collection_id=$collectionId AND e.air_date IS NOT NULL
                  {(isYear ? string.Empty : "AND substr(e.air_date,1,4)=$year")}
                  AND COALESCE(e.hidden,0)=0
            GROUP BY period_value
            ORDER BY period_value;
            """;
        command.Parameters.AddWithValue("$collectionId", collectionId);
        if (year.HasValue) command.Parameters.AddWithValue("$year", year.Value.ToString("0000", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = reader.GetInt32(0);
            var count = reader.GetInt32(1);
            var completed = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            var favourites = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            var title = isYear ? value.ToString(CultureInfo.InvariantCulture) : CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(value);
            results.Add(new ArchivePeriodSummary(value, title, count, completed, favourites,
                count == 0 ? 0 : (double)completed / count * 100d,
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return results;
    }

    public Task SetFavouriteAsync(IEnumerable<long> broadcastIds, bool favourite, CancellationToken cancellationToken = default)
        => ExecuteBulkAsync(broadcastIds, "UPDATE episodes SET favourite=$value, updated_at=$now WHERE id=$id", favourite ? 1 : 0, cancellationToken);

    public async Task SetPlayedAsync(IEnumerable<long> broadcastIds, bool played, CancellationToken cancellationToken = default)
    {
        var requestedIds = NormalizeIds(broadcastIds);
        if (requestedIds.Count == 0) return;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Playback state is a canonical-broadcast property. Expand the visible
        // representative ID before applying an explicit played/unplayed action
        // so an older retained member cannot make progress reappear later.
        var ids = new HashSet<long>();
        foreach (var requestedId in requestedIds)
        {
            foreach (var stateId in await ExpandPlaybackEpisodeIdsAsync(
                         connection,
                         (SqliteTransaction)transaction,
                         requestedId,
                         cancellationToken))
            {
                ids.Add(stateId);
            }
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var id in ids)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO playback_state(episode_id,position_ms,completed,last_played_at,play_count,duration_ms,playback_speed,completed_at)
                VALUES($id,CASE WHEN $played=1 THEN COALESCE((SELECT duration_ms FROM playback_state WHERE episode_id=$id),0) ELSE 0 END,$played,$now,0,COALESCE((SELECT duration_ms FROM playback_state WHERE episode_id=$id),0),1.0,CASE WHEN $played=1 THEN $now ELSE NULL END)
                ON CONFLICT(episode_id) DO UPDATE SET
                    position_ms=CASE WHEN $played=1 THEN duration_ms ELSE 0 END,
                    completed=$played,
                    last_played_at=$now,
                    completed_at=CASE WHEN $played=1 THEN $now ELSE NULL END;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$played", played ? 1 : 0);
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<long>> ExpandPlaybackEpisodeIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long episodeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH latest AS (
                SELECT truth_run_id AS run_id
                  FROM library_truth_adoption_runs
                 WHERE status='completed' AND commit_verified=1
                   AND foreign_key_violations=0 AND lower(integrity_check)='ok'
                 ORDER BY id DESC
                 LIMIT 1
            ),
            adopted_key AS (
                SELECT canonical_key
                  FROM episode_canonical_map
                 WHERE episode_id=$id
                 LIMIT 1
            ),
            held_key AS (
                SELECT f.canonical_broadcast_key
                  FROM library_truth_files f
                  JOIN latest l ON l.run_id=f.run_id
                 WHERE f.current_episode_id=$id
                 LIMIT 1
            )
            SELECT episode_id
              FROM episode_canonical_map
             WHERE canonical_key=(SELECT canonical_key FROM adopted_key)
            UNION
            SELECT f.current_episode_id
              FROM library_truth_files f
              JOIN latest l ON l.run_id=f.run_id
             WHERE f.canonical_broadcast_key=(SELECT canonical_broadcast_key FROM held_key)
            UNION SELECT $id
            ORDER BY 1;
            """;
        command.Parameters.AddWithValue("$id", episodeId);
        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private async Task ExecuteBulkAsync(IEnumerable<long> broadcastIds, string sql, int value, CancellationToken cancellationToken)
    {
        var ids = NormalizeIds(broadcastIds);
        if (ids.Count == 0) return;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var id in ids)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static List<long> NormalizeIds(IEnumerable<long> ids)
        => ids.Where(id => id > 0).Distinct().ToList();

    private static BroadcastSummary ReadBroadcast(SqliteDataReader reader)
    {
        DateOnly? airDate = null;
        if (!reader.IsDBNull(4) && DateOnly.TryParse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)) airDate = parsedDate;
        DateTimeOffset? lastPlayed = null;
        if (!reader.IsDBNull(13) && DateTimeOffset.TryParse(reader.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedLastPlayed)) lastPlayed = parsedLastPlayed;
        return new BroadcastSummary(
            reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), airDate,
            reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt32(8) != 0, reader.GetInt32(9) != 0, reader.GetInt64(10), reader.GetInt64(11), reader.GetInt32(12) != 0,
            lastPlayed, reader.IsDBNull(14) ? null : reader.GetString(14));
    }
}
