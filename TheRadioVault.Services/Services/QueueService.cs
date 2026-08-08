using System.Globalization;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

public sealed class QueueService : IQueueService
{
    private readonly SqliteDatabase _database;
    private readonly CanonicalLibraryQueryService _canonicalLibrary;

    public QueueService(SqliteDatabase database)
    {
        _database = database;
        _canonicalLibrary = new CanonicalLibraryQueryService(database);
    }

    public async Task<IReadOnlyList<QueueRecord>> GetAsync(CancellationToken cancellationToken = default)
    {
        var canonical = _canonicalLibrary.GetBroadcasts()
            .ToDictionary(x => x.CanonicalKey, StringComparer.OrdinalIgnoreCase);
        var result = new List<QueueRecord>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT q.id,q.episode_id,q.queue_position,q.added_at,c.name,e.title,
                   m.original_filename,m.path,e.air_date
            FROM playback_queue q
            JOIN episodes e ON e.id=q.episode_id
            JOIN collections c ON c.id=e.collection_id
            LEFT JOIN media_files m ON m.id=(
                SELECT MIN(m2.id)
                  FROM media_files m2
                 WHERE m2.episode_id=e.id AND m2.is_missing=0)
            ORDER BY q.queue_position,q.id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            DateTimeOffset.TryParse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var addedAt);
            var queuedEpisodeId = reader.GetInt64(1);
            var resolution = canonical.Count == 0 ? null : _canonicalLibrary.ResolveEpisode(queuedEpisodeId);
            if (resolution is not null && canonical.TryGetValue(resolution.CanonicalKey, out var broadcast))
            {
                result.Add(new QueueRecord(
                    reader.GetInt64(0),
                    broadcast.RepresentativeEpisodeId,
                    reader.GetInt32(2),
                    addedAt,
                    broadcast.CollectionName,
                    broadcast.Headline,
                    broadcast.OriginalFilename,
                    broadcast.Path,
                    broadcast.AirDate));
                continue;
            }

            DateOnly? airDate = null;
            if (!reader.IsDBNull(8) && DateOnly.TryParse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                airDate = parsedDate;
            result.Add(new QueueRecord(
                reader.GetInt64(0),
                queuedEpisodeId,
                reader.GetInt32(2),
                addedAt,
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                airDate));
        }
        return result;
    }

    public async Task<long> AddAsync(long broadcastId, bool playNext = false, CancellationToken cancellationToken = default)
    {
        if (broadcastId <= 0) throw new ArgumentOutOfRangeException(nameof(broadcastId));
        var resolution = _canonicalLibrary.ResolveEpisode(broadcastId);
        if (resolution is not null) broadcastId = resolution.RepresentativeEpisodeId;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (playNext)
        {
            await using var shift = connection.CreateCommand();
            shift.Transaction = transaction;
            shift.CommandText = "UPDATE playback_queue SET queue_position=queue_position+1";
            await shift.ExecuteNonQueryAsync(cancellationToken);
        }

        int position;
        if (playNext) position = 0;
        else
        {
            await using var max = connection.CreateCommand();
            max.Transaction = transaction;
            max.CommandText = "SELECT COALESCE(MAX(queue_position),-1)+1 FROM playback_queue";
            position = Convert.ToInt32(await max.ExecuteScalarAsync(cancellationToken));
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO playback_queue(episode_id,queue_position,added_at) VALUES($episode,$position,$added); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$episode", broadcastId);
        command.Parameters.AddWithValue("$position", position);
        command.Parameters.AddWithValue("$added", DateTimeOffset.UtcNow.ToString("O"));
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task RemoveAsync(long queueItemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM playback_queue WHERE id=$id";
            command.Parameters.AddWithValue("$id", queueItemId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await NormalizeAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM playback_queue";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MoveAsync(long queueItemId, int direction, CancellationToken cancellationToken = default)
    {
        if (direction == 0) return;
        direction = direction < 0 ? -1 : 1;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        int? current = null;
        await using (var get = connection.CreateCommand())
        {
            get.Transaction = transaction;
            get.CommandText = "SELECT queue_position FROM playback_queue WHERE id=$id";
            get.Parameters.AddWithValue("$id", queueItemId);
            var value = await get.ExecuteScalarAsync(cancellationToken);
            if (value is not null && value != DBNull.Value) current = Convert.ToInt32(value);
        }
        if (!current.HasValue || current.Value + direction < 0) return;
        var target = current.Value + direction;

        long? otherId = null;
        await using (var other = connection.CreateCommand())
        {
            other.Transaction = transaction;
            other.CommandText = "SELECT id FROM playback_queue WHERE queue_position=$position ORDER BY id LIMIT 1";
            other.Parameters.AddWithValue("$position", target);
            var value = await other.ExecuteScalarAsync(cancellationToken);
            if (value is not null && value != DBNull.Value) otherId = Convert.ToInt64(value);
        }
        if (!otherId.HasValue) return;

        await using (var first = connection.CreateCommand())
        {
            first.Transaction = transaction;
            first.CommandText = "UPDATE playback_queue SET queue_position=$position WHERE id=$id";
            first.Parameters.AddWithValue("$position", target);
            first.Parameters.AddWithValue("$id", queueItemId);
            await first.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var second = connection.CreateCommand())
        {
            second.Transaction = transaction;
            second.CommandText = "UPDATE playback_queue SET queue_position=$position WHERE id=$id";
            second.Parameters.AddWithValue("$position", current.Value);
            second.Parameters.AddWithValue("$id", otherId.Value);
            await second.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task NormalizeAsync(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var ids = new List<long>();
        await using (var get = connection.CreateCommand())
        {
            get.Transaction = transaction;
            get.CommandText = "SELECT id FROM playback_queue ORDER BY queue_position,id";
            await using var reader = await get.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt64(0));
        }
        for (var i = 0; i < ids.Count; i++)
        {
            await using var set = connection.CreateCommand();
            set.Transaction = transaction;
            set.CommandText = "UPDATE playback_queue SET queue_position=$position WHERE id=$id";
            set.Parameters.AddWithValue("$position", i);
            set.Parameters.AddWithValue("$id", ids[i]);
            await set.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
