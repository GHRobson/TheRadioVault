using System.Globalization;
using Microsoft.Data.Sqlite;
using TheRadioVault.Core.Playback;
using TheRadioVault.Data.Database;
using TheRadioVault.Models;

namespace TheRadioVault.Services;

/// <summary>
/// Owns the transactional persistence boundary for listener-controlled state.
/// Canonical episode expansion remains a library-query concern; every resolved
/// member is read and written in one transaction here.
/// </summary>
internal sealed class PersonalStateRepository
{
    private readonly SqliteDatabase _database;

    public PersonalStateRepository(SqliteDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public PlaybackState GetPlaybackState(long episodeId, IReadOnlyList<long> stateEpisodeIds)
    {
        if (stateEpisodeIds.Count == 0) return EmptyState(episodeId);

        using var connection = _database.OpenConnection();
        return ReadPlaybackState(connection, transaction: null, episodeId, stateEpisodeIds);
    }

    public void SavePlaybackState(
        long episodeId,
        IReadOnlyList<long> stateEpisodeIds,
        long positionMs,
        long durationMs,
        bool completed,
        double playbackSpeed,
        bool incrementPlayCount,
        bool incrementCompletionCount,
        bool allowPositionReset,
        DateTimeOffset? playedAt)
    {
        if (stateEpisodeIds.Count == 0) return;

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var existing = ReadPlaybackState(connection, transaction, episodeId, stateEpisodeIds);
        WritePlaybackState(
            connection,
            transaction,
            stateEpisodeIds,
            existing,
            positionMs,
            durationMs,
            completed,
            playbackSpeed,
            incrementPlayCount,
            incrementCompletionCount,
            allowPositionReset,
            playedAt);
        transaction.Commit();
    }

    public void MarkCompleted(
        long episodeId,
        IReadOnlyList<long> stateEpisodeIds,
        bool completed)
    {
        if (stateEpisodeIds.Count == 0) return;

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var existing = ReadPlaybackState(connection, transaction, episodeId, stateEpisodeIds);
        var position = completed && existing.DurationMs > 0
            ? existing.DurationMs
            : completed ? existing.PositionMs : 0;
        WritePlaybackState(
            connection,
            transaction,
            stateEpisodeIds,
            existing,
            position,
            existing.DurationMs,
            completed,
            existing.PlaybackSpeed,
            incrementPlayCount: false,
            incrementCompletionCount: false,
            allowPositionReset: !completed,
            playedAt: null);
        transaction.Commit();
    }

    public void SetFavourite(IReadOnlyList<long> stateEpisodeIds, bool favourite)
    {
        if (stateEpisodeIds.Count == 0) return;

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var changedAt = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        foreach (var stateEpisodeId in stateEpisodeIds)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE episodes SET favourite=$value,updated_at=$now WHERE id=$id";
            command.Parameters.AddWithValue("$value", favourite ? 1 : 0);
            command.Parameters.AddWithValue("$now", changedAt);
            command.Parameters.AddWithValue("$id", stateEpisodeId);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static PlaybackState ReadPlaybackState(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long episodeId,
        IReadOnlyList<long> stateEpisodeIds)
    {
        if (stateEpisodeIds.Count == 0) return EmptyState(episodeId);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameters = stateEpisodeIds.Select((_, index) => $"$id{index}").ToArray();
        command.CommandText = $"""
            SELECT position_ms,completed,duration_ms,playback_speed,first_played_at,last_played_at,play_count,completion_count
              FROM playback_state
             WHERE episode_id IN ({string.Join(',', parameters)})
             ORDER BY CASE WHEN last_played_at IS NULL THEN 1 ELSE 0 END,last_played_at DESC,position_ms DESC
            """;
        for (var index = 0; index < stateEpisodeIds.Count; index++)
            command.Parameters.AddWithValue(parameters[index], stateEpisodeIds[index]);

        var result = EmptyState(episodeId);
        var newestSpeedRead = false;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.PositionMs = Math.Max(result.PositionMs, reader.GetInt64(0));
            result.Completed |= reader.GetInt32(1) == 1;
            result.DurationMs = Math.Max(result.DurationMs, reader.GetInt64(2));
            if (!newestSpeedRead)
            {
                result.PlaybackSpeed = reader.GetDouble(3);
                newestSpeedRead = true;
            }

            var firstPlayed = ReadTimestamp(reader, 4);
            var lastPlayed = ReadTimestamp(reader, 5);
            if (firstPlayed.HasValue && (!result.FirstPlayedAt.HasValue || firstPlayed.Value < result.FirstPlayedAt.Value))
                result.FirstPlayedAt = firstPlayed;
            if (lastPlayed.HasValue && (!result.LastPlayedAt.HasValue || lastPlayed.Value > result.LastPlayedAt.Value))
                result.LastPlayedAt = lastPlayed;
            result.PlayCount = Math.Max(result.PlayCount, reader.IsDBNull(6) ? 0 : reader.GetInt32(6));
            result.CompletionCount = Math.Max(result.CompletionCount, reader.IsDBNull(7) ? 0 : reader.GetInt32(7));
        }

        return result;
    }

    private static void WritePlaybackState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<long> stateEpisodeIds,
        PlaybackState existing,
        long positionMs,
        long durationMs,
        bool completed,
        double playbackSpeed,
        bool incrementPlayCount,
        bool incrementCompletionCount,
        bool allowPositionReset,
        DateTimeOffset? playedAt)
    {
        var requestedPosition = Math.Max(0, positionMs);
        var effectivePosition = PlaybackPersistencePolicy.ResolvePosition(
            requestedPosition,
            existing.PositionMs,
            allowPositionReset);
        var preserveExistingProgress = effectivePosition != requestedPosition;
        var effectiveDuration = Math.Max(Math.Max(0, durationMs), existing.DurationMs);
        var effectiveCompleted = preserveExistingProgress ? existing.Completed : completed;
        var effectiveSpeed = playbackSpeed > 0
            ? playbackSpeed
            : existing.PlaybackSpeed > 0 ? existing.PlaybackSpeed : 1d;
        var receivedAt = DateTimeOffset.UtcNow;
        var playedAtValue = (playedAt ?? receivedAt).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var receivedAtValue = receivedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        foreach (var stateEpisodeId in stateEpisodeIds)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO playback_state(
                  episode_id,position_ms,completed,last_played_at,play_count,duration_ms,playback_speed,
                  completed_at,first_played_at,completion_count)
                VALUES(
                  $id,$position,$completed,$now,$playCount,$duration,$speed,
                  CASE WHEN $completed=1 THEN COALESCE($completedAt,$now) ELSE $completedAt END,
                  CASE WHEN $increment=1 THEN COALESCE($firstPlayedAt,$now) ELSE $firstPlayedAt END,
                  $completionCount)
                ON CONFLICT(episode_id) DO UPDATE SET
                  position_ms=excluded.position_ms,
                  completed=excluded.completed,
                  last_played_at=excluded.last_played_at,
                  play_count=MAX(playback_state.play_count,$basePlayCount)+$increment,
                  duration_ms=MAX(playback_state.duration_ms,excluded.duration_ms),
                  playback_speed=excluded.playback_speed,
                  completed_at=CASE WHEN excluded.completed=1 THEN COALESCE(playback_state.completed_at,excluded.completed_at) ELSE playback_state.completed_at END,
                  first_played_at=COALESCE(playback_state.first_played_at,excluded.first_played_at),
                  completion_count=MAX(playback_state.completion_count,$baseCompletionCount)+$completionIncrement
                """;
            command.Parameters.AddWithValue("$id", stateEpisodeId);
            command.Parameters.AddWithValue("$position", effectivePosition);
            command.Parameters.AddWithValue("$duration", effectiveDuration);
            command.Parameters.AddWithValue("$completed", effectiveCompleted ? 1 : 0);
            command.Parameters.AddWithValue("$speed", effectiveSpeed);
            command.Parameters.AddWithValue("$increment", incrementPlayCount ? 1 : 0);
            command.Parameters.AddWithValue("$completionIncrement", incrementCompletionCount ? 1 : 0);
            command.Parameters.AddWithValue("$basePlayCount", existing.PlayCount);
            command.Parameters.AddWithValue("$baseCompletionCount", existing.CompletionCount);
            command.Parameters.AddWithValue("$playCount", existing.PlayCount + (incrementPlayCount ? 1 : 0));
            command.Parameters.AddWithValue("$completionCount", existing.CompletionCount + (incrementCompletionCount ? 1 : 0));
            command.Parameters.AddWithValue(
                "$firstPlayedAt",
                existing.FirstPlayedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
            command.Parameters.AddWithValue(
                "$completedAt",
                effectiveCompleted && existing.LastPlayedAt.HasValue
                    ? existing.LastPlayedAt.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                    : (object)DBNull.Value);
            command.Parameters.AddWithValue("$now", playedAtValue);
            command.ExecuteNonQuery();

            using var status = connection.CreateCommand();
            status.Transaction = transaction;
            status.CommandText = "UPDATE episodes SET status=$status,updated_at=$now WHERE id=$id";
            status.Parameters.AddWithValue(
                "$status",
                effectiveCompleted ? "Completed" : effectivePosition > 0 ? "In Progress" : "Unplayed");
            status.Parameters.AddWithValue("$now", receivedAtValue);
            status.Parameters.AddWithValue("$id", stateEpisodeId);
            status.ExecuteNonQuery();
        }
    }

    private static PlaybackState EmptyState(long episodeId) => new() { EpisodeId = episodeId };

    private static DateTime? ReadTimestamp(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : DateTime.Parse(
                reader.GetString(ordinal),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
}
