using System.Globalization;
using Microsoft.Data.Sqlite;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

/// <summary>
/// Local canonical playback boundary used by the Avalonia shell. It resolves
/// the preferred Library Truth recording, maps multipart segments to physical
/// files and writes progress to every member of the canonical broadcast.
/// </summary>
public sealed class LocalPlaybackLibraryService : ILocalPlaybackLibraryService
{
    private readonly SqliteDatabase _database;
    private readonly CanonicalLibraryQueryService _canonical;

    public LocalPlaybackLibraryService(SqliteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _canonical = new CanonicalLibraryQueryService(database);
    }

    public async Task<LocalPlaybackDescriptor> PrepareAsync(
        long representativeEpisodeId,
        CancellationToken cancellationToken = default)
    {
        if (representativeEpisodeId <= 0)
            throw new ArgumentOutOfRangeException(nameof(representativeEpisodeId));

        // Canonical identity lookup uses SQLite and can take a noticeable amount of
        // time on a large archive. Keep it off the UI thread so the loading state is
        // painted immediately and the shell never appears frozen while Play starts.
        var resolution = await Task.Run(
            () => _canonical.ResolveEpisode(representativeEpisodeId),
            cancellationToken).ConfigureAwait(false);

        return await PrepareAsync(
            resolution?.CanonicalKey ?? string.Empty,
            resolution?.RepresentativeEpisodeId ?? representativeEpisodeId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalPlaybackDescriptor> PrepareAsync(
        string canonicalKey,
        long representativeEpisodeId,
        CancellationToken cancellationToken = default)
    {
        if (representativeEpisodeId <= 0)
            throw new ArgumentOutOfRangeException(nameof(representativeEpisodeId));

        cancellationToken.ThrowIfCancellationRequested();
        var metadata = await ReadMetadataAsync(representativeEpisodeId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected broadcast no longer exists in the local library.");

        IReadOnlyList<LocalPlaybackSegment> segments;
        long plannedDurationMs = 0;
        if (!string.IsNullOrWhiteSpace(canonicalKey))
        {
            var plan = await Task.Run(() => _canonical.GetPreferredPlaybackPlan(canonicalKey), cancellationToken)
                .ConfigureAwait(false);
            if (plan is not null)
            {
                segments = plan.Segments
                    .OrderBy(x => x.SegmentNumber)
                    .Select(segment =>
                    {
                        var source = segment.Sources
                            .Where(x => !x.IsMissing && !string.IsNullOrWhiteSpace(x.Path))
                            .OrderByDescending(x => x.IsPreferred)
                            .ThenBy(x => x.MediaFileId)
                            .FirstOrDefault(x => File.Exists(x.Path));
                        if (source is null)
                            throw new FileNotFoundException($"Part {segment.SegmentNumber} of this broadcast is not available offline.");
                        return new LocalPlaybackSegment(
                            segment.SegmentNumber,
                            segment.SegmentTotal,
                            Math.Max(0, segment.LogicalStartMs),
                            Math.Max(segment.LogicalStartMs, segment.LogicalEndMs),
                            Path.GetFullPath(source.Path),
                            Math.Max(0, source.DurationMs));
                    })
                    .ToArray();
                plannedDurationMs = Math.Max(0, plan.DurationMs);
            }
            else
            {
                segments = await ReadLegacySegmentsAsync(representativeEpisodeId, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            segments = await ReadLegacySegmentsAsync(representativeEpisodeId, cancellationToken).ConfigureAwait(false);
        }

        if (segments.Count == 0)
            throw new FileNotFoundException("No playable local media file could be resolved for this broadcast.");

        var durationMs = Math.Max(plannedDurationMs, segments.Max(x => x.LogicalEndMs));

        var state = await ReadCanonicalStateAsync(representativeEpisodeId, cancellationToken).ConfigureAwait(false);
        durationMs = Math.Max(durationMs, state.DurationMs);
        var resume = state.Completed ? 0 : Math.Clamp(state.PositionMs, 0, Math.Max(0, durationMs));

        return new LocalPlaybackDescriptor(
            canonicalKey,
            representativeEpisodeId,
            metadata.BroadcastId,
            metadata.Title,
            metadata.CollectionName,
            metadata.AirDate,
            metadata.ArtworkPath,
            resume,
            durationMs,
            state.Speed <= 0 ? 1d : Math.Clamp(state.Speed, 0.5d, 3d),
            state.Completed,
            metadata.Favourite,
            segments);
    }

    public async Task SaveAsync(LocalPlaybackSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RepresentativeEpisodeId <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.RepresentativeEpisodeId));

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var ids = await ExpandPlaybackEpisodeIdsAsync(
            connection,
            (SqliteTransaction)transaction,
            request.RepresentativeEpisodeId,
            cancellationToken).ConfigureAwait(false);

        var position = Math.Max(0, request.PositionMs);
        var duration = Math.Max(0, request.DurationMs);
        if (duration > 0) position = Math.Min(position, duration);
        if (request.Completed && duration > 0) position = duration;
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        foreach (var id in ids)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO playback_state(
                    episode_id,position_ms,completed,last_played_at,play_count,duration_ms,
                    playback_speed,completed_at,first_played_at,completion_count)
                VALUES(
                    $id,$position,$completed,$now,$playIncrement,$duration,$speed,
                    CASE WHEN $completed=1 THEN $now ELSE NULL END,
                    CASE WHEN $playIncrement=1 THEN $now ELSE NULL END,
                    CASE WHEN $completed=1 THEN 1 ELSE 0 END)
                ON CONFLICT(episode_id) DO UPDATE SET
                    position_ms=CASE
                        WHEN $allowCompletionReset=1 THEN excluded.position_ms
                        WHEN playback_state.completed=1 THEN MAX(playback_state.position_ms,excluded.position_ms)
                        ELSE excluded.position_ms
                    END,
                    completed=CASE
                        WHEN $allowCompletionReset=1 THEN excluded.completed
                        ELSE MAX(playback_state.completed,excluded.completed)
                    END,
                    last_played_at=excluded.last_played_at,
                    play_count=playback_state.play_count+$playIncrement,
                    duration_ms=MAX(playback_state.duration_ms,excluded.duration_ms),
                    playback_speed=excluded.playback_speed,
                    first_played_at=COALESCE(playback_state.first_played_at,
                        CASE WHEN $playIncrement=1 THEN $now ELSE NULL END),
                    completion_count=playback_state.completion_count+
                        CASE WHEN excluded.completed=1 AND playback_state.completed=0 THEN 1 ELSE 0 END,
                    completed_at=CASE
                        WHEN excluded.completed=1 THEN COALESCE(playback_state.completed_at,$now)
                        ELSE playback_state.completed_at
                    END;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$position", position);
            command.Parameters.AddWithValue("$completed", request.Completed ? 1 : 0);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$playIncrement", request.IncrementPlayCount ? 1 : 0);
            command.Parameters.AddWithValue("$duration", duration);
            command.Parameters.AddWithValue("$speed", Math.Clamp(request.PlaybackSpeed, 0.5d, 3d));
            command.Parameters.AddWithValue("$allowCompletionReset", request.AllowCompletionReset ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Read the canonical row back after commit. This makes shutdown persistence
        // deterministic and surfaces a failed or overwritten write instead of allowing
        // the window to close on the assumption that progress was stored.
        var saved = await ReadCanonicalStateAsync(request.RepresentativeEpisodeId, cancellationToken).ConfigureAwait(false);
        if (request.Completed && !saved.Completed)
            throw new IOException("Radio Vault could not verify the completed listening state after saving it.");
        if (!request.Completed && request.AllowCompletionReset && saved.Completed)
            throw new IOException("Radio Vault could not verify that the completed listening state was reset.");
        if (!saved.Completed && Math.Abs(saved.PositionMs - position) > 1_500)
            throw new IOException($"Radio Vault saved {saved.PositionMs} ms instead of the requested {position} ms listening position.");
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private async Task<IReadOnlyList<LocalPlaybackSegment>> ReadLegacySegmentsAsync(
        long episodeId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path,COALESCE(duration_ms,0)
              FROM media_files
             WHERE episode_id=$id AND COALESCE(is_missing,0)=0
             ORDER BY COALESCE(is_preferred,0) DESC,id
            """;
        command.Parameters.AddWithValue("$id", episodeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var path = reader.GetString(0);
            if (!File.Exists(path)) continue;
            var duration = Math.Max(0, reader.GetInt64(1));
            return new[] { new LocalPlaybackSegment(1, 1, 0, duration, Path.GetFullPath(path), duration) };
        }
        return Array.Empty<LocalPlaybackSegment>();
    }

    private async Task<PlaybackMetadata?> ReadMetadataAsync(long episodeId, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(e.broadcast_uid,'BROADCAST-' || e.id),
                   CASE WHEN trim(COALESCE(e.title,''))='' THEN COALESCE(e.air_date,'Untitled broadcast') ELSE e.title END,
                   c.name,e.air_date,e.artwork_path,COALESCE(e.favourite,0)
              FROM episodes e
              JOIN collections c ON c.id=e.collection_id
             WHERE e.id=$id
            """;
        command.Parameters.AddWithValue("$id", episodeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        DateOnly? airDate = null;
        if (!reader.IsDBNull(3) && DateOnly.TryParse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            airDate = parsed;
        return new PlaybackMetadata(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            airDate,
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt32(5) != 0);
    }

    private async Task<PlaybackState> ReadCanonicalStateAsync(long representativeEpisodeId, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH latest AS (
                SELECT truth_run_id AS run_id
                  FROM library_truth_adoption_runs
                 WHERE status='completed' AND commit_verified=1
                   AND foreign_key_violations=0 AND lower(integrity_check)='ok'
                 ORDER BY id DESC LIMIT 1
            ), ids AS (
                SELECT episode_id FROM episode_canonical_map
                 WHERE canonical_key=(SELECT canonical_key FROM episode_canonical_map WHERE episode_id=$id LIMIT 1)
                UNION
                SELECT f.current_episode_id FROM library_truth_files f JOIN latest l ON l.run_id=f.run_id
                 WHERE f.canonical_broadcast_key=(
                     SELECT canonical_broadcast_key FROM library_truth_files f2 JOIN latest l2 ON l2.run_id=f2.run_id
                      WHERE f2.current_episode_id=$id LIMIT 1)
                UNION SELECT $id
            )
            SELECT COALESCE(MAX(position_ms),0),COALESCE(MAX(duration_ms),0),COALESCE(MAX(completed),0),
                   COALESCE((SELECT playback_speed FROM playback_state p2 JOIN ids i2 ON i2.episode_id=p2.episode_id
                              ORDER BY COALESCE(last_played_at,'') DESC LIMIT 1),1.0)
              FROM playback_state p JOIN ids i ON i.episode_id=p.episode_id;
            """;
        command.Parameters.AddWithValue("$id", representativeEpisodeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return new PlaybackState(0, 0, false, 1d);
        return new PlaybackState(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2) != 0, reader.GetDouble(3));
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
                 ORDER BY id DESC LIMIT 1
            ), adopted_key AS (
                SELECT canonical_key FROM episode_canonical_map WHERE episode_id=$id LIMIT 1
            ), held_key AS (
                SELECT f.canonical_broadcast_key FROM library_truth_files f JOIN latest l ON l.run_id=f.run_id
                 WHERE f.current_episode_id=$id LIMIT 1
            )
            SELECT episode_id FROM episode_canonical_map WHERE canonical_key=(SELECT canonical_key FROM adopted_key)
            UNION
            SELECT f.current_episode_id FROM library_truth_files f JOIN latest l ON l.run_id=f.run_id
             WHERE f.canonical_broadcast_key=(SELECT canonical_broadcast_key FROM held_key)
            UNION SELECT $id
            ORDER BY 1;
            """;
        command.Parameters.AddWithValue("$id", episodeId);
        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(reader.GetInt64(0));
        return ids.Count == 0 ? new[] { episodeId } : ids;
    }

    private sealed record PlaybackMetadata(
        string BroadcastId,
        string Title,
        string CollectionName,
        DateOnly? AirDate,
        string? ArtworkPath,
        bool Favourite);
    private sealed record PlaybackState(long PositionMs, long DurationMs, bool Completed, double Speed);
}
