using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TheRadioVault.Data.Database;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Services;

public sealed class SqliteTranscriptRepository : ITranscriptRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteDatabase _database;

    public SqliteTranscriptRepository(SqliteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        MarkAbandonedJobsInterrupted();
    }

    private void MarkAbandonedJobsInterrupted()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE transcription_jobs
               SET state='Interrupted',
                   message='Interrupted when Radio Vault last closed',
                   finished_at=COALESCE(finished_at,$finished)
             WHERE state IN ('Queued','Running')
            """;
        command.Parameters.AddWithValue("$finished", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public Task<TranscriptDocument?> GetForEpisodeAsync(long episodeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        return Task.FromResult(ReadDocument(connection, episodeId));
    }

    public Task<TranscriptSummary?> GetSummaryForEpisodeAsync(long episodeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SummarySql + " WHERE t.episode_id=$episode LIMIT 1";
        command.Parameters.AddWithValue("$episode", episodeId);
        using var reader = command.ExecuteReader();
        return Task.FromResult(reader.Read() ? ReadSummary(reader) : null);
    }

    public Task<IReadOnlyList<TranscriptSummary>> GetSummariesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<TranscriptSummary>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SummarySql + " ORDER BY t.updated_at DESC, t.id DESC";
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(ReadSummary(reader));
        return Task.FromResult<IReadOnlyList<TranscriptSummary>>(result);
    }

    public Task<TranscriptEpisodeIdentity?> GetEpisodeIdentityAsync(long episodeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.id,COALESCE(e.broadcast_uid,''),c.name,e.air_date,COALESCE(e.part_number,1),e.title
              FROM episodes e
              JOIN collections c ON c.id=e.collection_id
             WHERE e.id=$episode
             LIMIT 1
            """;
        command.Parameters.AddWithValue("$episode", episodeId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return Task.FromResult<TranscriptEpisodeIdentity?>(null);
        return Task.FromResult<TranscriptEpisodeIdentity?>(new TranscriptEpisodeIdentity(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
            reader.GetInt32(4),
            reader.GetString(5)));
    }


    public Task<TranscriptionContext> GetTranscriptionContextAsync(long episodeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        var show = "";
        var title = "";
        var people = new List<string>();
        var topics = new List<string>();
        var terms = new List<string>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT c.name,COALESCE(e.title,''),COALESCE(e.description,''),COALESCE(e.hosts,''),
                       COALESCE(e.callers,''),COALESCE(e.mentioned_people,''),COALESCE(e.edition,'')
                  FROM episodes e
                  JOIN collections c ON c.id=e.collection_id
                 WHERE e.id=$episode LIMIT 1
                """;
            command.Parameters.AddWithValue("$episode", episodeId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                show = reader.GetString(0);
                title = reader.GetString(1);
                AddContextTerms(terms, show);
                AddContextTerms(terms, title);
                AddContextTerms(topics, reader.GetString(2));
                AddContextTerms(people, reader.GetString(3));
                AddContextTerms(people, reader.GetString(4));
                AddContextTerms(people, reader.GetString(5));
                AddContextTerms(terms, reader.GetString(6));
            }
        }

        using (var guests = connection.CreateCommand())
        {
            guests.CommandText = """
                SELECT g.name FROM episode_guests eg
                JOIN guests g ON g.id=eg.guest_id
                WHERE eg.episode_id=$episode
                ORDER BY g.name COLLATE NOCASE
                """;
            guests.Parameters.AddWithValue("$episode", episodeId);
            using var reader = guests.ExecuteReader();
            while (reader.Read()) people.Add(reader.GetString(0));
        }

        using (var research = connection.CreateCommand())
        {
            research.CommandText = """
                SELECT value FROM (
                    SELECT rp.name AS value FROM research_people rp
                    JOIN research_broadcasts rb ON rb.id=rp.research_broadcast_id
                    WHERE rb.episode_id=$episode
                    UNION ALL
                    SELECT rt.topic AS value FROM research_topics rt
                    JOIN research_broadcasts rb ON rb.id=rt.research_broadcast_id
                    WHERE rb.episode_id=$episode
                ) WHERE value<>''
                """;
            research.Parameters.AddWithValue("$episode", episodeId);
            using var reader = research.ExecuteReader();
            while (reader.Read()) terms.Add(reader.GetString(0));
        }

        terms.AddRange(people);
        terms.AddRange(topics);
        return Task.FromResult(new TranscriptionContext(
            show,
            title,
            people.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            topics.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            terms.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
    }

    public Task<string?> GetPreferredMediaPathAsync(long episodeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path
              FROM media_files
             WHERE episode_id=$episode AND is_missing=0
             ORDER BY is_preferred DESC,id
             LIMIT 1
            """;
        command.Parameters.AddWithValue("$episode", episodeId);
        return Task.FromResult(command.ExecuteScalar() as string);
    }

    public Task<long> GetEpisodeDurationMsAsync(long episodeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(MAX(m.duration_ms),0)
              FROM media_files m
             WHERE m.episode_id=$episode AND m.is_missing=0
            """;
        command.Parameters.AddWithValue("$episode", episodeId);
        return Task.FromResult(Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture));
    }

    public Task<TranscriptDocument> SaveAsync(TranscriptDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.EpisodeId <= 0) throw new ArgumentOutOfRangeException(nameof(document), "A valid episode ID is required.");
        cancellationToken.ThrowIfCancellationRequested();

        var segments = NormalizeSegments(document.Segments);
        var fullText = string.IsNullOrWhiteSpace(document.FullText)
            ? string.Join(Environment.NewLine, segments.Select(x => x.Text).Where(x => !string.IsNullOrWhiteSpace(x)))
            : document.FullText.Trim();
        var wordCount = document.WordCount > 0 ? document.WordCount : CountWords(fullText);
        var now = DateTimeOffset.UtcNow;

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        long transcriptId;
        int revision;
        DateTimeOffset createdAt;
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT id,revision,created_at FROM transcripts WHERE episode_id=$episode LIMIT 1";
            existing.Parameters.AddWithValue("$episode", document.EpisodeId);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                transcriptId = reader.GetInt64(0);
                revision = reader.GetInt32(1) + 1;
                createdAt = ParseTimestamp(reader.GetString(2));
            }
            else
            {
                transcriptId = 0;
                revision = 1;
                createdAt = document.CreatedAt == default ? now : document.CreatedAt;
            }
        }

        if (transcriptId == 0)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO transcripts(
                    episode_id,status,language,engine_id,engine_version,model_id,source,full_text,
                    word_count,duration_ms,has_word_timings,has_speaker_diarization,created_at,updated_at,completed_at,revision,metadata_json)
                VALUES($episode,$status,$language,$engine,$engineVersion,$model,$source,$text,
                    $words,$duration,$wordTimings,$speakerDiarization,$created,$updated,$completed,$revision,$metadata);
                SELECT last_insert_rowid();
                """;
            BindTranscript(insert, document, fullText, wordCount, createdAt, now, revision);
            transcriptId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        else
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE transcripts
                   SET status=$status,language=$language,engine_id=$engine,engine_version=$engineVersion,
                       model_id=$model,source=$source,full_text=$text,word_count=$words,duration_ms=$duration,
                       has_word_timings=$wordTimings,has_speaker_diarization=$speakerDiarization,updated_at=$updated,completed_at=$completed,
                       revision=$revision,metadata_json=$metadata
                 WHERE id=$id
                """;
            BindTranscript(update, document, fullText, wordCount, createdAt, now, revision);
            update.Parameters.AddWithValue("$id", transcriptId);
            update.ExecuteNonQuery();

            using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM transcript_segments WHERE transcript_id=$id";
            clear.Parameters.AddWithValue("$id", transcriptId);
            clear.ExecuteNonQuery();
        }

        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var insertSegment = connection.CreateCommand();
            insertSegment.Transaction = transaction;
            insertSegment.CommandText = """
                INSERT INTO transcript_segments(
                    transcript_id,segment_index,start_ms,end_ms,speaker,speaker_key,text,confidence,words_json,content_kind,is_reviewed)
                VALUES($transcript,$index,$start,$end,$speaker,$speakerKey,$text,$confidence,$words,$contentKind,$reviewed)
                """;
            insertSegment.Parameters.AddWithValue("$transcript", transcriptId);
            insertSegment.Parameters.AddWithValue("$index", segment.Index);
            insertSegment.Parameters.AddWithValue("$start", segment.StartMs);
            insertSegment.Parameters.AddWithValue("$end", segment.EndMs);
            insertSegment.Parameters.AddWithValue("$speaker", segment.Speaker ?? "");
            insertSegment.Parameters.AddWithValue("$speakerKey", segment.SpeakerKey ?? "");
            insertSegment.Parameters.AddWithValue("$text", segment.Text ?? "");
            insertSegment.Parameters.AddWithValue("$confidence", segment.Confidence.HasValue ? segment.Confidence.Value : DBNull.Value);
            insertSegment.Parameters.AddWithValue("$words", JsonSerializer.Serialize(segment.Words ?? Array.Empty<TranscriptWord>(), JsonOptions));
            insertSegment.Parameters.AddWithValue("$contentKind", segment.ContentKind.ToString());
            insertSegment.Parameters.AddWithValue("$reviewed", segment.IsReviewed ? 1 : 0);
            insertSegment.ExecuteNonQuery();
        }

        UpsertSpeakerClusters(connection, transaction, transcriptId, document.Speakers, segments, now, cancellationToken);

        transaction.Commit();
        var savedSpeakers = ReadSpeakers(connection, transcriptId);
        var savedSegments = ApplySpeakerAssignments(segments, savedSpeakers);
        return Task.FromResult(new TranscriptDocument
        {
            Id = transcriptId,
            EpisodeId = document.EpisodeId,
            Status = document.Status,
            Language = document.Language,
            EngineId = document.EngineId,
            EngineVersion = document.EngineVersion,
            ModelId = document.ModelId,
            Source = NormalizeTranscriptSource(document.Source),
            FullText = fullText,
            WordCount = wordCount,
            DurationMs = document.DurationMs,
            HasWordTimings = document.HasWordTimings,
            HasSpeakerDiarization = document.HasSpeakerDiarization || savedSpeakers.Count > 0,
            CreatedAt = createdAt,
            UpdatedAt = now,
            CompletedAt = document.CompletedAt ?? (document.Status == TranscriptStatus.Complete ? now : null),
            Revision = revision,
            MetadataJson = string.IsNullOrWhiteSpace(document.MetadataJson) ? "{}" : document.MetadataJson,
            Segments = savedSegments,
            Speakers = savedSpeakers
        });
    }

    public Task DeleteAsync(long episodeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM transcripts WHERE episode_id=$episode";
        command.Parameters.AddWithValue("$episode", episodeId);
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task CreateJobAsync(TranscriptionJobRecord job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transcription_jobs(
                job_id,episode_id,state,engine_id,model_id,progress_percent,message,error,
                requested_at,started_at,finished_at,background_job_id,language,start_ms,duration_ms,
                enable_speaker_diarization,use_vad,replace_existing,is_paused)
            VALUES($id,$episode,$state,$engine,$model,$progress,$message,$error,
                $requested,$started,$finished,$background,$language,$start,$duration,
                $diarization,$vad,$replace,$paused)
            """;
        BindJob(command, job);
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task UpdateJobAsync(TranscriptionJobRecord job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE transcription_jobs
               SET state=$state,engine_id=$engine,model_id=$model,progress_percent=$progress,
                   message=$message,error=$error,started_at=$started,finished_at=$finished,
                   background_job_id=$background,language=$language,start_ms=$start,duration_ms=$duration,
                   enable_speaker_diarization=$diarization,use_vad=$vad,replace_existing=$replace,is_paused=$paused
             WHERE job_id=$id
            """;
        BindJob(command, job);
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<TranscriptionJobRecord?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = JobSql + " WHERE job_id=$id LIMIT 1";
        command.Parameters.AddWithValue("$id", jobId.ToString("D"));
        using var reader = command.ExecuteReader();
        return Task.FromResult(reader.Read() ? ReadJob(reader) : null);
    }

    public Task<IReadOnlyList<TranscriptionJobRecord>> GetJobsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<TranscriptionJobRecord>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = JobSql + " ORDER BY requested_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(ReadJob(reader));
        return Task.FromResult<IReadOnlyList<TranscriptionJobRecord>>(result);
    }

    public Task RecordImportAsync(
        long episodeId,
        string packageId,
        string sourcePath,
        string checksum,
        int replacedRevision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transcript_imports(episode_id,package_id,source_path,checksum,imported_at,replaced_revision)
            VALUES($episode,$package,$path,$checksum,$at,$revision)
            ON CONFLICT(episode_id,package_id) DO UPDATE SET
                source_path=excluded.source_path,
                checksum=excluded.checksum,
                imported_at=excluded.imported_at,
                replaced_revision=excluded.replaced_revision
            """;
        command.Parameters.AddWithValue("$episode", episodeId);
        command.Parameters.AddWithValue("$package", packageId ?? "");
        command.Parameters.AddWithValue("$path", sourcePath ?? "");
        command.Parameters.AddWithValue("$checksum", checksum ?? "");
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$revision", replacedRevision);
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    private static TranscriptDocument? ReadDocument(SqliteConnection connection, long episodeId)
    {
        long id;
        TranscriptDocument shell;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id,episode_id,status,language,engine_id,engine_version,model_id,source,full_text,
                       word_count,duration_ms,has_word_timings,has_speaker_diarization,created_at,updated_at,completed_at,revision,metadata_json
                  FROM transcripts
                 WHERE episode_id=$episode
                 LIMIT 1
                """;
            command.Parameters.AddWithValue("$episode", episodeId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            id = reader.GetInt64(0);
            shell = new TranscriptDocument
            {
                Id = id,
                EpisodeId = reader.GetInt64(1),
                Status = ParseEnum(reader.GetString(2), TranscriptStatus.Complete),
                Language = reader.GetString(3),
                EngineId = reader.GetString(4),
                EngineVersion = reader.GetString(5),
                ModelId = reader.GetString(6),
                Source = reader.GetString(7),
                FullText = reader.GetString(8),
                WordCount = reader.GetInt32(9),
                DurationMs = reader.GetInt64(10),
                HasWordTimings = reader.GetInt32(11) == 1,
                HasSpeakerDiarization = reader.GetInt32(12) == 1,
                CreatedAt = ParseTimestamp(reader.GetString(13)),
                UpdatedAt = ParseTimestamp(reader.GetString(14)),
                CompletedAt = reader.IsDBNull(15) ? null : ParseTimestamp(reader.GetString(15)),
                Revision = reader.GetInt32(16),
                MetadataJson = reader.GetString(17)
            };
        }

        var segments = new List<TranscriptSegment>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT segment_index,start_ms,end_ms,text,speaker,confidence,words_json,speaker_key,content_kind,is_reviewed
                  FROM transcript_segments
                 WHERE transcript_id=$transcript
                 ORDER BY segment_index
                """;
            command.Parameters.AddWithValue("$transcript", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                IReadOnlyList<TranscriptWord> words;
                try
                {
                    words = JsonSerializer.Deserialize<List<TranscriptWord>>(reader.GetString(6), JsonOptions)
                        ?? new List<TranscriptWord>();
                }
                catch
                {
                    words = Array.Empty<TranscriptWord>();
                }
                segments.Add(new TranscriptSegment(
                    reader.GetInt32(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    words,
                    reader.GetString(7),
                    ContentKind: ParseEnum(reader.GetString(8), TranscriptContentKind.Speech),
                    IsReviewed: reader.GetInt32(9) == 1));
            }
        }

        var speakers = ReadSpeakers(connection, id);
        var assignedSegments = ApplySpeakerAssignments(segments, speakers);

        return new TranscriptDocument
        {
            Id = shell.Id,
            EpisodeId = shell.EpisodeId,
            Status = shell.Status,
            Language = shell.Language,
            EngineId = shell.EngineId,
            EngineVersion = shell.EngineVersion,
            ModelId = shell.ModelId,
            Source = shell.Source,
            FullText = shell.FullText,
            WordCount = shell.WordCount,
            DurationMs = shell.DurationMs,
            HasWordTimings = shell.HasWordTimings,
            HasSpeakerDiarization = shell.HasSpeakerDiarization || speakers.Count > 0,
            CreatedAt = shell.CreatedAt,
            UpdatedAt = shell.UpdatedAt,
            CompletedAt = shell.CompletedAt,
            Revision = shell.Revision,
            MetadataJson = shell.MetadataJson,
            Segments = assignedSegments,
            Speakers = speakers
        };
    }

    private static void UpsertSpeakerClusters(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long transcriptId,
        IReadOnlyList<TranscriptSpeakerCluster>? explicitSpeakers,
        IReadOnlyList<TranscriptSegment> segments,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var explicitByKey = (explicitSpeakers ?? Array.Empty<TranscriptSpeakerCluster>())
            .Where(x => !string.IsNullOrWhiteSpace(x.SpeakerKey))
            .GroupBy(x => x.SpeakerKey.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);

        var aggregates = segments
            .Where(x => !string.IsNullOrWhiteSpace(x.SpeakerKey))
            .GroupBy(x => x.SpeakerKey.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Key = group.Key,
                Label = group.Select(x => x.Speaker).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? group.Key,
                SegmentCount = group.Count(),
                SpeakingDurationMs = group.Sum(x => Math.Max(0, x.EndMs - x.StartMs))
            })
            .ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var explicitSpeaker in explicitByKey.Values)
        {
            if (!aggregates.ContainsKey(explicitSpeaker.SpeakerKey))
            {
                aggregates[explicitSpeaker.SpeakerKey] = new
                {
                    Key = explicitSpeaker.SpeakerKey,
                    Label = string.IsNullOrWhiteSpace(explicitSpeaker.Label) ? explicitSpeaker.SpeakerKey : explicitSpeaker.Label,
                    SegmentCount = explicitSpeaker.SegmentCount,
                    SpeakingDurationMs = explicitSpeaker.SpeakingDurationMs
                };
            }
        }

        foreach (var aggregate in aggregates.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            explicitByKey.TryGetValue(aggregate.Key, out var explicitSpeaker);
            long? voicePersonId = null;
            if (!string.IsNullOrWhiteSpace(explicitSpeaker?.PersonName))
                voicePersonId = GetOrCreateVoicePersonId(connection, transaction, explicitSpeaker.PersonName, now);

            var state = explicitSpeaker?.AssignmentState ?? SpeakerAssignmentState.Unassigned;
            using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO transcript_speakers(
                    transcript_id,speaker_key,label,segment_count,speaking_duration_ms,voice_person_id,
                    assignment_state,assignment_confidence,assignment_source,train_voice,created_at,updated_at)
                VALUES($transcript,$key,$label,$segments,$duration,$person,$state,$confidence,$source,$train,$created,$updated)
                ON CONFLICT(transcript_id,speaker_key) DO UPDATE SET
                    label=excluded.label,
                    segment_count=excluded.segment_count,
                    speaking_duration_ms=excluded.speaking_duration_ms,
                    voice_person_id=CASE WHEN excluded.voice_person_id IS NULL THEN transcript_speakers.voice_person_id ELSE excluded.voice_person_id END,
                    assignment_state=CASE WHEN excluded.assignment_state='Unassigned' THEN transcript_speakers.assignment_state ELSE excluded.assignment_state END,
                    assignment_confidence=CASE WHEN excluded.assignment_state='Unassigned' THEN transcript_speakers.assignment_confidence ELSE excluded.assignment_confidence END,
                    assignment_source=CASE WHEN excluded.assignment_state='Unassigned' THEN transcript_speakers.assignment_source ELSE excluded.assignment_source END,
                    train_voice=CASE WHEN excluded.assignment_state='Unassigned' THEN transcript_speakers.train_voice ELSE excluded.train_voice END,
                    updated_at=excluded.updated_at
                """;
            upsert.Parameters.AddWithValue("$transcript", transcriptId);
            upsert.Parameters.AddWithValue("$key", aggregate.Key);
            upsert.Parameters.AddWithValue("$label", string.IsNullOrWhiteSpace(explicitSpeaker?.Label) ? aggregate.Label : explicitSpeaker.Label);
            upsert.Parameters.AddWithValue("$segments", aggregate.SegmentCount);
            upsert.Parameters.AddWithValue("$duration", aggregate.SpeakingDurationMs);
            upsert.Parameters.AddWithValue("$person", voicePersonId.HasValue ? voicePersonId.Value : DBNull.Value);
            upsert.Parameters.AddWithValue("$state", state.ToString());
            upsert.Parameters.AddWithValue("$confidence", explicitSpeaker?.AssignmentConfidence is double confidence ? confidence : DBNull.Value);
            upsert.Parameters.AddWithValue("$source", explicitSpeaker?.AssignmentSource ?? "");
            upsert.Parameters.AddWithValue("$train", explicitSpeaker?.TrainVoice == false ? 0 : 1);
            upsert.Parameters.AddWithValue("$created", now.ToString("O"));
            upsert.Parameters.AddWithValue("$updated", now.ToString("O"));
            upsert.ExecuteNonQuery();
        }
    }

    private static long GetOrCreateVoicePersonId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string personName,
        DateTimeOffset now)
    {
        var canonical = personName.Trim();
        var normalized = NormalizePersonName(canonical);
        if (normalized.Length == 0) throw new InvalidDataException("A speaker assignment contains an invalid person name.");

        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO voice_people(canonical_name,normalized_name,aliases_json,created_at,updated_at)
                VALUES($name,$normalized,'[]',$now,$now)
                ON CONFLICT(normalized_name) DO UPDATE SET
                    canonical_name=CASE WHEN length(excluded.canonical_name)>length(voice_people.canonical_name) THEN excluded.canonical_name ELSE voice_people.canonical_name END,
                    updated_at=excluded.updated_at
                """;
            upsert.Parameters.AddWithValue("$name", canonical);
            upsert.Parameters.AddWithValue("$normalized", normalized);
            upsert.Parameters.AddWithValue("$now", now.ToString("O"));
            upsert.ExecuteNonQuery();
        }

        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT id FROM voice_people WHERE normalized_name=$normalized LIMIT 1";
        select.Parameters.AddWithValue("$normalized", normalized);
        return Convert.ToInt64(select.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<TranscriptSpeakerCluster> ReadSpeakers(SqliteConnection connection, long transcriptId)
    {
        var result = new List<TranscriptSpeakerCluster>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ts.id,ts.transcript_id,ts.speaker_key,ts.label,ts.segment_count,ts.speaking_duration_ms,
                   ts.voice_person_id,COALESCE(vp.canonical_name,''),ts.assignment_state,
                   ts.assignment_confidence,ts.assignment_source,ts.train_voice,ts.created_at,ts.updated_at
              FROM transcript_speakers ts
              LEFT JOIN voice_people vp ON vp.id=ts.voice_person_id
             WHERE ts.transcript_id=$transcript
             ORDER BY ts.speaker_key COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$transcript", transcriptId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new TranscriptSpeakerCluster
            {
                Id = reader.GetInt64(0),
                TranscriptId = reader.GetInt64(1),
                SpeakerKey = reader.GetString(2),
                Label = reader.GetString(3),
                SegmentCount = reader.GetInt32(4),
                SpeakingDurationMs = reader.GetInt64(5),
                VoicePersonId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                PersonName = reader.GetString(7),
                AssignmentState = ParseEnum(reader.GetString(8), SpeakerAssignmentState.Unassigned),
                AssignmentConfidence = reader.IsDBNull(9) ? null : reader.GetDouble(9),
                AssignmentSource = reader.GetString(10),
                TrainVoice = reader.GetInt32(11) == 1,
                CreatedAt = ParseTimestamp(reader.GetString(12)),
                UpdatedAt = ParseTimestamp(reader.GetString(13))
            });
        }
        return result;
    }

    private static IReadOnlyList<TranscriptSegment> ApplySpeakerAssignments(
        IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyList<TranscriptSpeakerCluster> speakers)
    {
        if (speakers.Count == 0) return segments;
        var byKey = speakers.ToDictionary(x => x.SpeakerKey, StringComparer.OrdinalIgnoreCase);
        return segments.Select(segment =>
        {
            if (string.IsNullOrWhiteSpace(segment.SpeakerKey) || !byKey.TryGetValue(segment.SpeakerKey, out var speaker))
                return segment;
            return segment with
            {
                AssignedPersonName = speaker.AssignmentState is SpeakerAssignmentState.Confirmed or SpeakerAssignmentState.Suggested
                    ? speaker.PersonName
                    : "",
                SpeakerConfidence = speaker.AssignmentConfidence,
                AssignmentState = speaker.AssignmentState
            };
        }).ToList();
    }

    private static string NormalizeSpeakerKey(string? speakerKey, string? speakerLabel, int index)
    {
        var source = !string.IsNullOrWhiteSpace(speakerKey) ? speakerKey : speakerLabel;
        if (string.IsNullOrWhiteSpace(source)) return "";
        var chars = source.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var normalized = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length == 0 ? $"speaker-{index + 1}" : normalized;
    }

    private static string NormalizePersonName(string value)
    {
        var compact = string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return new string(compact.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    private static void BindTranscript(
        SqliteCommand command,
        TranscriptDocument document,
        string fullText,
        int wordCount,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        int revision)
    {
        command.Parameters.AddWithValue("$episode", document.EpisodeId);
        command.Parameters.AddWithValue("$status", document.Status.ToString());
        command.Parameters.AddWithValue("$language", document.Language ?? "");
        command.Parameters.AddWithValue("$engine", document.EngineId ?? "");
        command.Parameters.AddWithValue("$engineVersion", document.EngineVersion ?? "");
        command.Parameters.AddWithValue("$model", document.ModelId ?? "");
        command.Parameters.AddWithValue("$source", NormalizeTranscriptSource(document.Source));
        command.Parameters.AddWithValue("$text", fullText);
        command.Parameters.AddWithValue("$words", wordCount);
        command.Parameters.AddWithValue("$duration", Math.Max(0, document.DurationMs));
        command.Parameters.AddWithValue("$wordTimings", document.HasWordTimings ? 1 : 0);
        command.Parameters.AddWithValue("$speakerDiarization", document.HasSpeakerDiarization || document.Segments.Any(x => !string.IsNullOrWhiteSpace(x.SpeakerKey) || !string.IsNullOrWhiteSpace(x.Speaker)) ? 1 : 0);
        command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", updatedAt.ToString("O"));
        command.Parameters.AddWithValue("$completed", document.CompletedAt.HasValue
            ? document.CompletedAt.Value.ToString("O")
            : document.Status == TranscriptStatus.Complete ? updatedAt.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$metadata", string.IsNullOrWhiteSpace(document.MetadataJson) ? "{}" : document.MetadataJson);
    }

    private static void BindJob(SqliteCommand command, TranscriptionJobRecord job)
    {
        command.Parameters.AddWithValue("$id", job.JobId.ToString("D"));
        command.Parameters.AddWithValue("$episode", job.EpisodeId);
        command.Parameters.AddWithValue("$state", job.State.ToString());
        command.Parameters.AddWithValue("$engine", job.EngineId ?? "");
        command.Parameters.AddWithValue("$model", job.ModelId ?? "");
        command.Parameters.AddWithValue("$progress", job.ProgressPercent.HasValue ? job.ProgressPercent.Value : DBNull.Value);
        command.Parameters.AddWithValue("$message", job.Message ?? "");
        command.Parameters.AddWithValue("$error", job.Error ?? "");
        command.Parameters.AddWithValue("$requested", job.RequestedAt.ToString("O"));
        command.Parameters.AddWithValue("$started", job.StartedAt.HasValue ? job.StartedAt.Value.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$finished", job.FinishedAt.HasValue ? job.FinishedAt.Value.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$background", job.BackgroundJobId.HasValue ? job.BackgroundJobId.Value.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$language", job.Language ?? "");
        command.Parameters.AddWithValue("$start", job.StartMs);
        command.Parameters.AddWithValue("$duration", job.DurationMs.HasValue ? job.DurationMs.Value : DBNull.Value);
        command.Parameters.AddWithValue("$diarization", job.EnableSpeakerDiarization ? 1 : 0);
        command.Parameters.AddWithValue("$vad", job.UseVoiceActivityDetection ? 1 : 0);
        command.Parameters.AddWithValue("$replace", job.ReplaceExistingTranscript ? 1 : 0);
        command.Parameters.AddWithValue("$paused", job.IsPaused ? 1 : 0);
    }

    private static string NormalizeTranscriptSource(string? source)
    {
        var normalized = source?.Trim().ToLowerInvariant();
        return normalized is "local" or "import" or "manual" or "shared"
            ? normalized
            : "local";
    }

    private static TranscriptSummary ReadSummary(SqliteDataReader reader) => new()
    {
        TranscriptId = reader.GetInt64(0),
        EpisodeId = reader.GetInt64(1),
        Show = reader.GetString(2),
        AirDate = reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
        EpisodeTitle = reader.GetString(4),
        Status = ParseEnum(reader.GetString(5), TranscriptStatus.Complete),
        Language = reader.GetString(6),
        EngineId = reader.GetString(7),
        ModelId = reader.GetString(8),
        Source = reader.GetString(9),
        WordCount = reader.GetInt32(10),
        SegmentCount = reader.GetInt32(11),
        SpeakerCount = reader.GetInt32(12),
        IdentifiedSpeakerCount = reader.GetInt32(13),
        DurationMs = reader.GetInt64(14),
        UpdatedAt = ParseTimestamp(reader.GetString(15))
    };

    private static TranscriptionJobRecord ReadJob(SqliteDataReader reader) => new()
    {
        JobId = Guid.Parse(reader.GetString(0)),
        EpisodeId = reader.GetInt64(1),
        State = ParseEnum(reader.GetString(2), TranscriptionJobState.Interrupted),
        EngineId = reader.GetString(3),
        ModelId = reader.GetString(4),
        ProgressPercent = reader.IsDBNull(5) ? null : reader.GetDouble(5),
        Message = reader.GetString(6),
        Error = reader.GetString(7),
        RequestedAt = ParseTimestamp(reader.GetString(8)),
        StartedAt = reader.IsDBNull(9) ? null : ParseTimestamp(reader.GetString(9)),
        FinishedAt = reader.IsDBNull(10) ? null : ParseTimestamp(reader.GetString(10)),
        BackgroundJobId = reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11)),
        Language = reader.GetString(12),
        StartMs = reader.GetInt64(13),
        DurationMs = reader.IsDBNull(14) ? null : reader.GetInt64(14),
        EnableSpeakerDiarization = reader.GetInt32(15) == 1,
        UseVoiceActivityDetection = reader.GetInt32(16) == 1,
        ReplaceExistingTranscript = reader.GetInt32(17) == 1,
        IsPaused = reader.GetInt32(18) == 1
    };

    private static IReadOnlyList<TranscriptSegment> NormalizeSegments(IReadOnlyList<TranscriptSegment>? input)
    {
        var ordered = (input ?? Array.Empty<TranscriptSegment>())
            .OrderBy(x => x.Index)
            .ThenBy(x => x.StartMs)
            .ToList();
        var result = new List<TranscriptSegment>(ordered.Count);
        long previousEnd = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            var segment = ordered[i];
            var start = Math.Max(0, segment.StartMs);
            var end = Math.Max(start, segment.EndMs);
            if (start < previousEnd) start = previousEnd;
            if (end < start) end = start;
            result.Add(segment with
            {
                Index = i,
                StartMs = start,
                EndMs = end,
                Text = (segment.Text ?? "").Trim(),
                Speaker = (segment.Speaker ?? "").Trim(),
                SpeakerKey = NormalizeSpeakerKey(segment.SpeakerKey, segment.Speaker, i),
                AssignedPersonName = (segment.AssignedPersonName ?? "").Trim(),
                Words = segment.Words ?? Array.Empty<TranscriptWord>(),
                ContentKind = Enum.IsDefined(segment.ContentKind) ? segment.ContentKind : TranscriptContentKind.Unknown
            });
            previousEnd = end;
        }
        return result;
    }


    private static void AddContextTerms(ICollection<string> target, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        foreach (var item in value.Split(new[] { '|', ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (item.Length >= 2 && item.Length <= 100) target.Add(item);
        }
    }

    private static int CountWords(string value)
        => string.IsNullOrWhiteSpace(value)
            ? 0
            : value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static T ParseEnum<T>(string value, T fallback) where T : struct, Enum
        => Enum.TryParse<T>(value, true, out var parsed) ? parsed : fallback;

    private const string SummarySql = """
        SELECT t.id,t.episode_id,c.name,e.air_date,e.title,t.status,t.language,t.engine_id,t.model_id,t.source,
               t.word_count,(SELECT COUNT(*) FROM transcript_segments s WHERE s.transcript_id=t.id),
               (SELECT COUNT(*) FROM transcript_speakers ts WHERE ts.transcript_id=t.id),
               (SELECT COUNT(*) FROM transcript_speakers ts WHERE ts.transcript_id=t.id AND ts.assignment_state='Confirmed' AND ts.voice_person_id IS NOT NULL),
               t.duration_ms,t.updated_at
          FROM transcripts t
          JOIN episodes e ON e.id=t.episode_id
          JOIN collections c ON c.id=e.collection_id
        """;

    private const string JobSql = """
        SELECT job_id,episode_id,state,engine_id,model_id,progress_percent,message,error,
               requested_at,started_at,finished_at,background_job_id,language,start_ms,duration_ms,
               enable_speaker_diarization,use_vad,replace_existing,is_paused
          FROM transcription_jobs
        """;
}
