using System.Globalization;
using Microsoft.Data.Sqlite;
using TheRadioVault.Core.Services;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

public sealed class MomentsService : IMomentsService
{
    private const string CanonicalIdentityExpression = "COALESCE((SELECT canonical_key FROM episode_canonical_map WHERE episode_id=e.id LIMIT 1),NULLIF(e.broadcast_uid,''),'EPISODE-' || e.id)";
    private const string RepresentativeEpisodeExpression = "COALESCE((SELECT survivor_episode_id FROM episode_canonical_map WHERE episode_id=e.id LIMIT 1),e.id)";

    private readonly SqliteDatabase _database;
    private readonly SemaphoreSlim _duplicateRepairGate = new(1, 1);
    private volatile bool _duplicateRepairCompleted;

    public MomentsService(SqliteDatabase database) => _database = database;

    public async Task<IReadOnlyList<MomentRecord>> GetForBroadcastAsync(long broadcastId, CancellationToken cancellationToken = default)
    {
        await EnsureDuplicateRepairAsync(cancellationToken);
        return await QueryAsync(
            $"""
            WHERE {CanonicalIdentityExpression}=(
                SELECT COALESCE((SELECT canonical_key FROM episode_canonical_map WHERE episode_id=target.id LIMIT 1),NULLIF(target.broadcast_uid,''),'EPISODE-' || target.id)
                  FROM episodes target
                 WHERE target.id=$episodeId
            )
            ORDER BY m.position_ms,m.created_at
            """,
            command => command.Parameters.AddWithValue("$episodeId", broadcastId),
            cancellationToken);
    }

    public async Task<IReadOnlyList<MomentRecord>> SearchAsync(string? searchText, int limit = 500, CancellationToken cancellationToken = default)
    {
        await EnsureDuplicateRepairAsync(cancellationToken);
        var hasSearch = !string.IsNullOrWhiteSpace(searchText);
        return await QueryAsync(
            hasSearch
                ? "WHERE m.title LIKE $search OR m.notes LIKE $search OR c.name LIKE $search OR e.title LIKE $search ORDER BY m.created_at DESC LIMIT $limit"
                : "ORDER BY m.created_at DESC LIMIT $limit",
            command =>
            {
                if (hasSearch) command.Parameters.AddWithValue("$search", $"%{searchText!.Trim()}%");
                command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
            }, cancellationToken);
    }

    public async Task<long> AddAsync(long broadcastId, long positionMs, string title, string? notes = null, CancellationToken cancellationToken = default)
    {
        if (broadcastId <= 0) throw new ArgumentOutOfRangeException(nameof(broadcastId));
        await EnsureDuplicateRepairAsync(cancellationToken);

        var safePosition = Math.Max(0, positionMs);
        var safeTitle = title?.Trim() ?? string.Empty;
        var safeNotes = notes?.Trim() ?? string.Empty;

        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var transactionBase = await connection.BeginTransactionAsync(cancellationToken);
        var transaction = (SqliteTransaction)transactionBase;

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = $"""
                WITH target AS (
                    SELECT COALESCE((SELECT canonical_key FROM episode_canonical_map WHERE episode_id=e.id LIMIT 1),NULLIF(e.broadcast_uid,''),'EPISODE-' || e.id) AS canonical_identity
                      FROM episodes e
                     WHERE e.id=$episodeId
                )
                SELECT m.id
                  FROM moments m
                  JOIN episodes e ON e.id=m.episode_id
                  JOIN target t ON {CanonicalIdentityExpression}=t.canonical_identity
                 WHERE m.position_ms BETWEEN $minimumPosition AND $maximumPosition
                   AND lower(trim(m.title))=lower(trim($title))
                   AND lower(trim(COALESCE(m.notes,'')))=lower(trim($notes))
                 ORDER BY m.created_at,m.id
                 LIMIT 1;
                """;
            existing.Parameters.AddWithValue("$episodeId", broadcastId);
            existing.Parameters.AddWithValue("$minimumPosition", Math.Max(0, safePosition - MomentDeduplicationPolicy.PositionToleranceMs));
            existing.Parameters.AddWithValue("$maximumPosition", safePosition + MomentDeduplicationPolicy.PositionToleranceMs);
            existing.Parameters.AddWithValue("$title", safeTitle);
            existing.Parameters.AddWithValue("$notes", safeNotes);
            var existingId = await existing.ExecuteScalarAsync(cancellationToken);
            if (existingId is not null && existingId is not DBNull)
            {
                await transaction.CommitAsync(cancellationToken);
                return Convert.ToInt64(existingId, CultureInfo.InvariantCulture);
            }
        }

        long targetEpisodeId;
        await using (var target = connection.CreateCommand())
        {
            target.Transaction = transaction;
            target.CommandText = """
                SELECT COALESCE((SELECT survivor_episode_id FROM episode_canonical_map WHERE episode_id=e.id LIMIT 1),e.id)
                  FROM episodes e
                 WHERE e.id=$episodeId;
                """;
            target.Parameters.AddWithValue("$episodeId", broadcastId);
            var scalar = await target.ExecuteScalarAsync(cancellationToken);
            if (scalar is null || scalar is DBNull)
                throw new InvalidOperationException($"Broadcast {broadcastId} no longer exists.");
            targetEpisodeId = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }

        long insertedId;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO moments(episode_id,position_ms,title,notes,created_at) VALUES($episodeId,$position,$title,$notes,$created); SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$episodeId", targetEpisodeId);
            insert.Parameters.AddWithValue("$position", safePosition);
            insert.Parameters.AddWithValue("$title", safeTitle);
            insert.Parameters.AddWithValue("$notes", safeNotes);
            insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            insertedId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        await transaction.CommitAsync(cancellationToken);
        return insertedId;
    }

    public async Task UpdateAsync(long momentId, string title, string? notes, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE moments SET title=$title, notes=$notes WHERE id=$id";
        command.Parameters.AddWithValue("$id", momentId);
        command.Parameters.AddWithValue("$title", title?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$notes", notes?.Trim() ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken);
        _duplicateRepairCompleted = false;
    }

    public async Task DeleteAsync(long momentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM moments WHERE id=$id";
        command.Parameters.AddWithValue("$id", momentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureDuplicateRepairAsync(CancellationToken cancellationToken)
    {
        if (_duplicateRepairCompleted) return;
        await _duplicateRepairGate.WaitAsync(cancellationToken);
        try
        {
            if (_duplicateRepairCompleted) return;
            await RepairDuplicateMomentsAsync(cancellationToken);
            _duplicateRepairCompleted = true;
        }
        finally
        {
            _duplicateRepairGate.Release();
        }
    }

    private async Task<int> RepairDuplicateMomentsAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<MomentCandidate>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = $"""
                SELECT m.id,m.episode_id,{CanonicalIdentityExpression},m.position_ms,m.title,COALESCE(m.notes,''),m.created_at
                  FROM moments m
                  JOIN episodes e ON e.id=m.episode_id
                 ORDER BY 3,lower(trim(m.title)),lower(trim(COALESCE(m.notes,''))),m.position_ms,m.created_at,m.id;
                """;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                DateTimeOffset.TryParse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt);
                candidates.Add(new MomentCandidate(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    Math.Max(0, reader.GetInt64(3)),
                    reader.GetString(4),
                    reader.GetString(5),
                    createdAt));
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
            var cluster = new List<MomentCandidate>();
            foreach (var candidate in ordered)
            {
                if (cluster.Count == 0 || candidate.PositionMs - cluster[0].PositionMs <= MomentDeduplicationPolicy.PositionToleranceMs)
                {
                    cluster.Add(candidate);
                    continue;
                }

                CollectDuplicateIds(cluster, deleteIds);
                cluster.Clear();
                cluster.Add(candidate);
            }
            CollectDuplicateIds(cluster, deleteIds);
        }

        if (deleteIds.Count == 0) return 0;

        await using var transactionBase = await connection.BeginTransactionAsync(cancellationToken);
        var transaction = (SqliteTransaction)transactionBase;
        foreach (var id in deleteIds.Distinct())
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM moments WHERE id=$id";
            delete.Parameters.AddWithValue("$id", id);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return deleteIds.Distinct().Count();
    }

    private static void CollectDuplicateIds(IReadOnlyList<MomentCandidate> cluster, ICollection<long> deleteIds)
    {
        if (cluster.Count < 2) return;
        var survivor = cluster
            .OrderBy(candidate => candidate.CreatedAt == default ? DateTimeOffset.MaxValue : candidate.CreatedAt)
            .ThenBy(candidate => candidate.Id)
            .First();
        foreach (var candidate in cluster)
        {
            if (candidate.Id != survivor.Id) deleteIds.Add(candidate.Id);
        }
    }

    private async Task<IReadOnlyList<MomentRecord>> QueryAsync(string tail, Action<SqliteCommand> configure, CancellationToken cancellationToken)
    {
        var results = new List<MomentRecord>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT m.id,{RepresentativeEpisodeExpression},c.name,e.title,e.air_date,m.position_ms,m.title,m.notes,m.created_at
            FROM moments m
            JOIN episodes e ON e.id=m.episode_id
            JOIN collections c ON c.id=e.collection_id
            {tail}
            """;
        configure(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            DateOnly? airDate = null;
            if (!reader.IsDBNull(4) && DateOnly.TryParse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                airDate = parsedDate;
            DateTimeOffset.TryParse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created);
            results.Add(new MomentRecord(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), airDate,
                reader.GetInt64(5), reader.GetString(6), reader.GetString(7), created));
        }
        return results;
    }

    private sealed record MomentCandidate(
        long Id,
        long EpisodeId,
        string CanonicalIdentity,
        long PositionMs,
        string Title,
        string Notes,
        DateTimeOffset CreatedAt);
}
