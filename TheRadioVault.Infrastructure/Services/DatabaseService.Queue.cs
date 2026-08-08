using Microsoft.Data.Sqlite;

namespace TheRadioVault.Services;

public sealed partial class DatabaseService
{
    public IReadOnlyList<QueueItem> GetQueue()
    {
        var visible = GetEpisodes()
            .Where(x => !string.IsNullOrWhiteSpace(x.CanonicalKey))
            .GroupBy(x => x.CanonicalKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<QueueItem>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT q.id,q.queue_position,e.id,c.name,e.title,
                   m.original_filename,m.path,e.air_date
              FROM playback_queue q
              JOIN episodes e ON e.id=q.episode_id
              JOIN collections c ON c.id=e.collection_id
              LEFT JOIN media_files m ON m.id=(
                  SELECT MIN(m2.id)
                    FROM media_files m2
                   WHERE m2.episode_id=e.id AND m2.is_missing=0)
             ORDER BY q.queue_position,q.id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var queuedEpisodeId = reader.GetInt64(2);
            var resolution = visible.Count == 0 ? null : ResolveCanonicalEpisode(queuedEpisodeId);
            if (resolution is not null && visible.TryGetValue(resolution.CanonicalKey, out var broadcast))
            {
                result.Add(new QueueItem
                {
                    QueueId = reader.GetInt64(0),
                    Position = reader.GetInt32(1),
                    EpisodeId = broadcast.Id,
                    CollectionName = broadcast.CollectionName,
                    DisplayTitle = broadcast.DisplayTitle,
                    OriginalFilename = broadcast.OriginalFilename,
                    Path = broadcast.Path,
                    AirDate = broadcast.AirDate,
                    BroadcastSlot = broadcast.BroadcastSlot
                });
                continue;
            }

            result.Add(new QueueItem
            {
                QueueId = reader.GetInt64(0),
                Position = reader.GetInt32(1),
                EpisodeId = queuedEpisodeId,
                CollectionName = reader.GetString(3),
                DisplayTitle = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                OriginalFilename = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                Path = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                AirDate = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7))
            });
        }

        return result;
    }

    public void AddToQueue(long episodeId, bool playNext)
    {
        var resolution = ResolveCanonicalEpisode(episodeId);
        if (resolution is not null) episodeId = resolution.RepresentativeEpisodeId;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        if (playNext)
        {
            using var shift = connection.CreateCommand();
            shift.Transaction = transaction;
            shift.CommandText = "UPDATE playback_queue SET queue_position=queue_position+1";
            shift.ExecuteNonQuery();
        }

        var position = 0;
        if (!playNext)
        {
            using var max = connection.CreateCommand();
            max.Transaction = transaction;
            max.CommandText = "SELECT COALESCE(MAX(queue_position),-1)+1 FROM playback_queue";
            position = Convert.ToInt32(max.ExecuteScalar());
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO playback_queue(episode_id,queue_position,added_at)
            VALUES($episode,$position,$added)
            """;
        command.Parameters.AddWithValue("$episode", episodeId);
        command.Parameters.AddWithValue("$position", position);
        command.Parameters.AddWithValue("$added", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void RemoveQueueItem(long queueId)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM playback_queue WHERE id=$id";
            command.Parameters.AddWithValue("$id", queueId);
            command.ExecuteNonQuery();
        }

        NormalizeQueue(connection, transaction);
        transaction.Commit();
    }

    public void ClearQueue()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM playback_queue";
        command.ExecuteNonQuery();
    }

    public void MoveQueueItem(long queueId, int direction)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        int current;
        using (var get = connection.CreateCommand())
        {
            get.Transaction = transaction;
            get.CommandText = "SELECT queue_position FROM playback_queue WHERE id=$id";
            get.Parameters.AddWithValue("$id", queueId);
            var value = get.ExecuteScalar();
            if (value is null) return;
            current = Convert.ToInt32(value);
        }

        var target = current + direction;
        if (target < 0) return;

        long? otherId = null;
        using (var other = connection.CreateCommand())
        {
            other.Transaction = transaction;
            other.CommandText = "SELECT id FROM playback_queue WHERE queue_position=$position ORDER BY id LIMIT 1";
            other.Parameters.AddWithValue("$position", target);
            var value = other.ExecuteScalar();
            if (value is not null) otherId = Convert.ToInt64(value);
        }

        if (!otherId.HasValue) return;

        using (var first = connection.CreateCommand())
        {
            first.Transaction = transaction;
            first.CommandText = "UPDATE playback_queue SET queue_position=$position WHERE id=$id";
            first.Parameters.AddWithValue("$position", target);
            first.Parameters.AddWithValue("$id", queueId);
            first.ExecuteNonQuery();
        }

        using (var second = connection.CreateCommand())
        {
            second.Transaction = transaction;
            second.CommandText = "UPDATE playback_queue SET queue_position=$position WHERE id=$id";
            second.Parameters.AddWithValue("$position", current);
            second.Parameters.AddWithValue("$id", otherId.Value);
            second.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void NormalizeQueue(SqliteConnection connection, SqliteTransaction transaction)
    {
        var ids = new List<long>();
        using (var get = connection.CreateCommand())
        {
            get.Transaction = transaction;
            get.CommandText = "SELECT id FROM playback_queue ORDER BY queue_position,id";
            using var reader = get.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
        }

        for (var index = 0; index < ids.Count; index++)
        {
            using var set = connection.CreateCommand();
            set.Transaction = transaction;
            set.CommandText = "UPDATE playback_queue SET queue_position=$position WHERE id=$id";
            set.Parameters.AddWithValue("$position", index);
            set.Parameters.AddWithValue("$id", ids[index]);
            set.ExecuteNonQuery();
        }
    }
}
