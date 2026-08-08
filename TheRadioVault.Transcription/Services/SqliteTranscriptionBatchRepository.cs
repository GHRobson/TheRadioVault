using System.Globalization;
using Microsoft.Data.Sqlite;
using TheRadioVault.Data.Database;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Services;

public sealed class SqliteTranscriptionBatchRepository : ITranscriptionBatchRepository
{
    private readonly SqliteDatabase _database;

    public SqliteTranscriptionBatchRepository(SqliteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        MarkAbandonedBatchesInterrupted();
    }

    public Task<TranscriptionBatchRecord> CreateAsync(TranscriptionBatchCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Candidates.Count == 0) throw new InvalidOperationException("The batch selection contains no broadcasts.");
        cancellationToken.ThrowIfCancellationRequested();
        var batchId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO transcription_batches(
                    batch_id,name,selection_label,state,language,model_id,enable_speaker_diarization,use_vad,
                    created_at,updated_at)
                VALUES($id,$name,$selection,'Queued',$language,$model,$diarization,$vad,$now,$now)
                """;
            insert.Parameters.AddWithValue("$id", batchId.ToString("D"));
            insert.Parameters.AddWithValue("$name", string.IsNullOrWhiteSpace(request.Name) ? "Transcription batch" : request.Name.Trim());
            insert.Parameters.AddWithValue("$selection", request.SelectionLabel?.Trim() ?? "");
            insert.Parameters.AddWithValue("$language", request.Options.Language ?? "");
            insert.Parameters.AddWithValue("$model", request.Options.ModelId ?? "");
            insert.Parameters.AddWithValue("$diarization", request.Options.EnableSpeakerDiarization ? 1 : 0);
            insert.Parameters.AddWithValue("$vad", request.Options.UseVoiceActivityDetection ? 1 : 0);
            insert.Parameters.AddWithValue("$now", now.ToString("O"));
            insert.ExecuteNonQuery();
        }

        var candidates = request.Candidates
            .GroupBy(x => x.EpisodeId)
            .Select(x => x.First())
            .OrderBy(x => x.AirDate ?? DateOnly.MaxValue)
            .ThenBy(x => x.Show, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        for (var index = 0; index < candidates.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var item = connection.CreateCommand();
            item.Transaction = transaction;
            item.CommandText = """
                INSERT INTO transcription_batch_items(
                    batch_id,episode_id,position,state,error,created_at,updated_at)
                VALUES($batch,$episode,$position,$state,$error,$now,$now)
                """;
            item.Parameters.AddWithValue("$batch", batchId.ToString("D"));
            item.Parameters.AddWithValue("$episode", candidates[index].EpisodeId);
            item.Parameters.AddWithValue("$position", index);
            item.Parameters.AddWithValue("$state", candidates[index].HasTranscript ? TranscriptionBatchItemState.Skipped.ToString() : TranscriptionBatchItemState.Pending.ToString());
            item.Parameters.AddWithValue("$error", candidates[index].HasTranscript ? "Transcript already exists" : "");
            item.Parameters.AddWithValue("$now", now.ToString("O"));
            item.ExecuteNonQuery();
        }
        transaction.Commit();
        return GetRequiredAsync(batchId, cancellationToken);
    }

    public Task<TranscriptionBatchRecord?> GetAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = BatchSql + " WHERE b.batch_id=$id GROUP BY b.batch_id LIMIT 1";
        command.Parameters.AddWithValue("$id", batchId.ToString("D"));
        using var reader = command.ExecuteReader();
        return Task.FromResult(reader.Read() ? ReadBatch(reader) : null);
    }

    public Task<IReadOnlyList<TranscriptionBatchRecord>> GetBatchesAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<TranscriptionBatchRecord>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = BatchSql + " GROUP BY b.batch_id ORDER BY b.created_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(ReadBatch(reader));
        return Task.FromResult<IReadOnlyList<TranscriptionBatchRecord>>(result);
    }

    public Task<IReadOnlyList<TranscriptionBatchItemRecord>> GetItemsAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<TranscriptionBatchItemRecord>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT bi.id,bi.batch_id,bi.episode_id,bi.position,bi.state,bi.transcription_job_id,bi.error,
                   c.name,e.air_date,e.title,COALESCE((SELECT MAX(m.duration_ms) FROM media_files m WHERE m.episode_id=e.id AND m.is_missing=0),0),
                   tj.progress_percent,COALESCE(tj.message,'')
              FROM transcription_batch_items bi
              JOIN episodes e ON e.id=bi.episode_id
              JOIN collections c ON c.id=e.collection_id
              LEFT JOIN transcription_jobs tj ON tj.job_id=bi.transcription_job_id
             WHERE bi.batch_id=$batch
             ORDER BY bi.position
            """;
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new TranscriptionBatchItemRecord
            {
                Id = reader.GetInt64(0),
                BatchId = Guid.Parse(reader.GetString(1)),
                EpisodeId = reader.GetInt64(2),
                Position = reader.GetInt32(3),
                State = ParseEnum(reader.GetString(4), TranscriptionBatchItemState.Pending),
                TranscriptionJobId = reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                Error = reader.GetString(6),
                Show = reader.GetString(7),
                AirDate = reader.IsDBNull(8) ? null : DateOnly.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                Title = reader.GetString(9),
                DurationMs = reader.GetInt64(10),
                ProgressPercent = reader.IsDBNull(11) ? null : reader.GetDouble(11),
                JobMessage = reader.GetString(12)
            });
        }
        return Task.FromResult<IReadOnlyList<TranscriptionBatchItemRecord>>(result);
    }

    public Task SetBatchStateAsync(Guid batchId, TranscriptionBatchState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE transcription_batches
               SET state=$state,updated_at=$now,
                   started_at=CASE WHEN $state='Running' THEN COALESCE(started_at,$now) ELSE started_at END,
                   finished_at=CASE WHEN $terminal=1 THEN $now WHEN $state='Running' THEN NULL ELSE finished_at END
             WHERE batch_id=$id
            """;
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$terminal", state is TranscriptionBatchState.Completed or TranscriptionBatchState.CompletedWithErrors or TranscriptionBatchState.Cancelled ? 1 : 0);
        command.Parameters.AddWithValue("$id", batchId.ToString("D"));
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task SetItemStateAsync(
        long itemId,
        TranscriptionBatchItemState state,
        Guid? transcriptionJobId = null,
        string error = "",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE transcription_batch_items
               SET state=$state,
                   transcription_job_id=CASE WHEN $job='' THEN transcription_job_id ELSE $job END,
                   error=$error,updated_at=$now
             WHERE id=$id
            """;
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$job", transcriptionJobId?.ToString("D") ?? "");
        command.Parameters.AddWithValue("$error", error ?? "");
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", itemId);
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task ResetFailedItemsAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE transcription_batch_items
               SET state='Pending',transcription_job_id=NULL,error='',updated_at=$now
             WHERE batch_id=$batch AND state='Failed'
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task CancelPendingItemsAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE transcription_batch_items
               SET state='Cancelled',error='Batch cancelled',updated_at=$now
             WHERE batch_id=$batch AND state='Pending'
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<bool> MoveItemAsync(Guid batchId, long itemId, int direction, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        direction = Math.Sign(direction);
        if (direction == 0) return Task.FromResult(false);
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        int currentPosition;
        using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT position FROM transcription_batch_items WHERE id=$id AND batch_id=$batch AND state='Pending'";
            current.Parameters.AddWithValue("$id", itemId);
            current.Parameters.AddWithValue("$batch", batchId.ToString("D"));
            var value = current.ExecuteScalar();
            if (value is null) { transaction.Commit(); return Task.FromResult(false); }
            currentPosition = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        long otherId;
        int otherPosition;
        using (var other = connection.CreateCommand())
        {
            other.Transaction = transaction;
            other.CommandText = direction < 0
                ? "SELECT id,position FROM transcription_batch_items WHERE batch_id=$batch AND state='Pending' AND position<$position ORDER BY position DESC LIMIT 1"
                : "SELECT id,position FROM transcription_batch_items WHERE batch_id=$batch AND state='Pending' AND position>$position ORDER BY position LIMIT 1";
            other.Parameters.AddWithValue("$batch", batchId.ToString("D"));
            other.Parameters.AddWithValue("$position", currentPosition);
            using var reader = other.ExecuteReader();
            if (!reader.Read()) { transaction.Commit(); return Task.FromResult(false); }
            otherId = reader.GetInt64(0);
            otherPosition = reader.GetInt32(1);
        }

        using (var park = connection.CreateCommand())
        {
            park.Transaction = transaction;
            park.CommandText = "UPDATE transcription_batch_items SET position=$temporary WHERE id=$id";
            park.Parameters.AddWithValue("$temporary", currentPosition + 1_000_000);
            park.Parameters.AddWithValue("$id", itemId);
            park.ExecuteNonQuery();
        }
        using (var moveOther = connection.CreateCommand())
        {
            moveOther.Transaction = transaction;
            moveOther.CommandText = "UPDATE transcription_batch_items SET position=$position WHERE id=$id";
            moveOther.Parameters.AddWithValue("$position", currentPosition);
            moveOther.Parameters.AddWithValue("$id", otherId);
            moveOther.ExecuteNonQuery();
        }
        using (var moveCurrent = connection.CreateCommand())
        {
            moveCurrent.Transaction = transaction;
            moveCurrent.CommandText = "UPDATE transcription_batch_items SET position=$position WHERE id=$id";
            moveCurrent.Parameters.AddWithValue("$position", otherPosition);
            moveCurrent.Parameters.AddWithValue("$id", itemId);
            moveCurrent.ExecuteNonQuery();
        }
        transaction.Commit();
        return Task.FromResult(true);
    }

    private void MarkAbandonedBatchesInterrupted()
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        using (var batches = connection.CreateCommand())
        {
            batches.Transaction = transaction;
            batches.CommandText = "UPDATE transcription_batches SET state='Interrupted',updated_at=$now WHERE state IN ('Queued','Running')";
            batches.Parameters.AddWithValue("$now", now);
            batches.ExecuteNonQuery();
        }
        using (var items = connection.CreateCommand())
        {
            items.Transaction = transaction;
            items.CommandText = "UPDATE transcription_batch_items SET state='Pending',transcription_job_id=NULL,error='Recovered after Radio Vault restarted',updated_at=$now WHERE state='Running'";
            items.Parameters.AddWithValue("$now", now);
            items.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private async Task<TranscriptionBatchRecord> GetRequiredAsync(Guid batchId, CancellationToken cancellationToken)
        => await GetAsync(batchId, cancellationToken).ConfigureAwait(false)
           ?? throw new InvalidOperationException("The transcription batch could not be read after it was created.");

    private static TranscriptionBatchRecord ReadBatch(SqliteDataReader reader)
        => new()
        {
            BatchId = Guid.Parse(reader.GetString(0)),
            Name = reader.GetString(1),
            SelectionLabel = reader.GetString(2),
            State = ParseEnum(reader.GetString(3), TranscriptionBatchState.Interrupted),
            Language = reader.GetString(4),
            ModelId = reader.GetString(5),
            EnableSpeakerDiarization = reader.GetInt32(6) == 1,
            UseVoiceActivityDetection = reader.GetInt32(7) == 1,
            CreatedAt = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
            StartedAt = reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
            FinishedAt = reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
            TotalCount = reader.GetInt32(12),
            PendingCount = reader.GetInt32(13),
            RunningCount = reader.GetInt32(14),
            CompletedCount = reader.GetInt32(15),
            FailedCount = reader.GetInt32(16),
            SkippedCount = reader.GetInt32(17),
            CancelledCount = reader.GetInt32(18),
            CurrentJobPercent = reader.IsDBNull(19) ? 0 : reader.GetDouble(19)
        };

    private static T ParseEnum<T>(string value, T fallback) where T : struct, Enum
        => Enum.TryParse<T>(value, true, out var parsed) ? parsed : fallback;

    private const string BatchSql = """
        SELECT b.batch_id,b.name,b.selection_label,b.state,b.language,b.model_id,
               b.enable_speaker_diarization,b.use_vad,b.created_at,b.updated_at,b.started_at,b.finished_at,
               COUNT(bi.id),
               SUM(CASE WHEN bi.state='Pending' THEN 1 ELSE 0 END),
               SUM(CASE WHEN bi.state='Running' THEN 1 ELSE 0 END),
               SUM(CASE WHEN bi.state='Completed' THEN 1 ELSE 0 END),
               SUM(CASE WHEN bi.state='Failed' THEN 1 ELSE 0 END),
               SUM(CASE WHEN bi.state='Skipped' THEN 1 ELSE 0 END),
               SUM(CASE WHEN bi.state='Cancelled' THEN 1 ELSE 0 END),
               MAX(CASE WHEN bi.state='Running' THEN COALESCE(tj.progress_percent,0) ELSE 0 END)
          FROM transcription_batches b
          LEFT JOIN transcription_batch_items bi ON bi.batch_id=b.batch_id
          LEFT JOIN transcription_jobs tj ON tj.job_id=bi.transcription_job_id
        """;
}
