using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TheRadioVault.Data.Database;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Services;

public sealed class SqliteSpeakerIdentityRepository : ISpeakerIdentityRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteDatabase _database;

    public SqliteSpeakerIdentityRepository(SqliteDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task<IReadOnlyList<TranscriptSpeakerCluster>> GetClustersForEpisodeAsync(
        long episodeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        EnsureClustersForEpisode(connection, episodeId, cancellationToken);
        return Task.FromResult<IReadOnlyList<TranscriptSpeakerCluster>>(ReadClusters(connection, episodeId));
    }

    public Task<IReadOnlyList<TranscriptPersonCandidate>> GetEpisodePeopleAsync(
        long episodeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = new List<TranscriptPersonCandidate>();
        using var connection = _database.OpenConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COALESCE(e.hosts,''),
                       COALESCE((SELECT group_concat(g.name,'|') FROM episode_guests eg JOIN guests g ON g.id=eg.guest_id WHERE eg.episode_id=e.id),''),
                       COALESCE(e.callers,''),COALESCE(e.mentioned_people,'')
                  FROM episodes e
                 WHERE e.id=$episode
                 LIMIT 1
                """;
            command.Parameters.AddWithValue("$episode", episodeId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                AddCandidates(candidates, reader.GetString(0), "Host");
                AddCandidates(candidates, reader.GetString(1), "Guest");
                AddCandidates(candidates, reader.GetString(2), "Caller");
                AddCandidates(candidates, reader.GetString(3), "Mentioned");
            }
        }

        using (var researched = connection.CreateCommand())
        {
            researched.CommandText = """
                SELECT rp.name,rp.role
                  FROM research_people rp
                  JOIN research_broadcasts rb ON rb.id=rp.research_broadcast_id
                 WHERE rb.episode_id=$episode
                 ORDER BY rp.role,rp.name COLLATE NOCASE
                """;
            researched.Parameters.AddWithValue("$episode", episodeId);
            using var reader = researched.ExecuteReader();
            while (reader.Read())
            {
                var role = reader.GetString(1).ToLowerInvariant() switch
                {
                    "host" => "Host",
                    "guest" => "Guest",
                    "caller" => "Caller",
                    _ => "Mentioned"
                };
                AddCandidate(candidates, reader.GetString(0), role);
            }
        }

        using (var known = connection.CreateCommand())
        {
            known.CommandText = "SELECT canonical_name FROM voice_people ORDER BY canonical_name COLLATE NOCASE";
            using var reader = known.ExecuteReader();
            while (reader.Read()) AddCandidate(candidates, reader.GetString(0), "Known voice");
        }

        return Task.FromResult<IReadOnlyList<TranscriptPersonCandidate>>(candidates
            .GroupBy(x => NormalizePersonName(x.Name), StringComparer.Ordinal)
            .Where(x => x.Key.Length > 0)
            .Select(group => group.OrderBy(x => RolePriority(x.Role)).First())
            .OrderBy(x => RolePriority(x.Role))
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    public Task<VoicePersonRecord> GetOrCreateVoicePersonAsync(
        string personName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var id = GetOrCreateVoicePersonId(connection, transaction, personName, DateTimeOffset.UtcNow);
        transaction.Commit();
        return Task.FromResult(ReadVoicePerson(connection, id));
    }

    public Task<SpeakerAssignmentResult> AssignClusterAsync(
        long episodeId,
        string speakerKey,
        string personName,
        bool trainVoice,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        using var connection = _database.OpenConnection();
        EnsureClustersForEpisode(connection, episodeId, cancellationToken);
        using var transaction = connection.BeginTransaction();

        var transcriptId = GetTranscriptId(connection, transaction, episodeId)
            ?? throw new InvalidOperationException("This broadcast does not have a transcript.");
        var clusterId = GetClusterId(connection, transaction, transcriptId, speakerKey)
            ?? throw new InvalidOperationException("The selected speaker cluster could not be found.");
        var voicePersonId = GetOrCreateVoicePersonId(connection, transaction, personName, now);

        long? previousVoicePersonId = null;
        var previousTrainVoice = false;
        using (var previous = connection.CreateCommand())
        {
            previous.Transaction = transaction;
            previous.CommandText = "SELECT voice_person_id,train_voice FROM transcript_speakers WHERE id=$cluster LIMIT 1";
            previous.Parameters.AddWithValue("$cluster", clusterId);
            using var reader = previous.ExecuteReader();
            if (reader.Read())
            {
                previousVoicePersonId = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                previousTrainVoice = reader.GetInt32(1) == 1;
            }
        }

        var identityChanged = previousVoicePersonId.HasValue && previousVoicePersonId.Value != voicePersonId;
        var learningDisabled = previousTrainVoice && !trainVoice;
        if (identityChanged || learningDisabled)
        {
            RejectClusterSamples(connection, transaction, transcriptId, speakerKey,
                identityChanged ? "Speaker identity corrected by the user" : "Voice learning disabled for this speaker",
                now);
            if (previousVoicePersonId.HasValue)
                RebuildAllProfilesForPerson(connection, transaction, previousVoicePersonId.Value, cancellationToken);
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE transcript_speakers
                   SET voice_person_id=$person,
                       assignment_state='Confirmed',
                       assignment_confidence=1.0,
                       assignment_source='manual',
                       train_voice=$train,
                       updated_at=$now
                 WHERE id=$cluster
                """;
            update.Parameters.AddWithValue("$person", voicePersonId);
            update.Parameters.AddWithValue("$train", trainVoice ? 1 : 0);
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$cluster", clusterId);
            update.ExecuteNonQuery();
        }

        using (var expire = connection.CreateCommand())
        {
            expire.Transaction = transaction;
            expire.CommandText = "UPDATE speaker_match_suggestions SET status='expired' WHERE transcript_speaker_id=$cluster AND status='pending'";
            expire.Parameters.AddWithValue("$cluster", clusterId);
            expire.ExecuteNonQuery();
        }

        var samplesCreated = trainVoice
            ? CreatePendingSamples(connection, transaction, voicePersonId, episodeId, transcriptId, speakerKey, now, cancellationToken)
            : 0;

        transaction.Commit();
        var profile = ReadProfileSummary(connection, voicePersonId)
            ?? throw new InvalidOperationException("The voice profile could not be read after assignment.");

        return Task.FromResult(new SpeakerAssignmentResult(
            episodeId,
            transcriptId,
            speakerKey,
            profile.PersonName,
            SpeakerAssignmentState.Confirmed,
            samplesCreated,
            profile));
    }

    public Task ClearAssignmentAsync(long episodeId, string speakerKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        speakerKey = speakerKey?.Trim() ?? string.Empty;
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var transcriptId = GetTranscriptId(connection, transaction, episodeId);
        if (!transcriptId.HasValue)
        {
            transaction.Commit();
            return Task.CompletedTask;
        }

        long? previousVoicePersonId = null;
        using (var previous = connection.CreateCommand())
        {
            previous.Transaction = transaction;
            previous.CommandText = "SELECT voice_person_id FROM transcript_speakers WHERE transcript_id=$transcript AND speaker_key=$key LIMIT 1";
            previous.Parameters.AddWithValue("$transcript", transcriptId.Value);
            previous.Parameters.AddWithValue("$key", speakerKey);
            var value = previous.ExecuteScalar();
            if (value is not null && value is not DBNull)
                previousVoicePersonId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        RejectClusterSamples(
            connection,
            transaction,
            transcriptId.Value,
            speakerKey,
            "Speaker assignment cleared by the user",
            DateTimeOffset.UtcNow);

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE transcript_speakers
                   SET voice_person_id=NULL,assignment_state='Unassigned',assignment_confidence=NULL,
                       assignment_source='',updated_at=$now
                 WHERE transcript_id=$transcript AND speaker_key=$key
                """;
            update.Parameters.AddWithValue("$transcript", transcriptId.Value);
            update.Parameters.AddWithValue("$key", speakerKey);
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            update.ExecuteNonQuery();
        }
        if (previousVoicePersonId.HasValue)
            RebuildAllProfilesForPerson(connection, transaction, previousVoicePersonId.Value, cancellationToken);
        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VoiceProfileSummary>> GetVoiceProfilesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<VoiceProfileSummary>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = ProfileSummarySql + " ORDER BY vp.canonical_name COLLATE NOCASE";
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(ReadProfileSummary(reader));
        return Task.FromResult<IReadOnlyList<VoiceProfileSummary>>(result);
    }

    public Task<VoiceProfileSummary?> GetVoiceProfileAsync(long voicePersonId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        return Task.FromResult(ReadProfileSummary(connection, voicePersonId));
    }

    public Task<IReadOnlyList<VoiceSampleRecord>> GetPendingVoiceSamplesAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<VoiceSampleRecord>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = VoiceSampleSql + " WHERE vs.state='Pending' ORDER BY vs.created_at,vs.id LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(ReadVoiceSample(reader));
        return Task.FromResult<IReadOnlyList<VoiceSampleRecord>>(result);
    }

    public Task SaveVoiceEmbeddingAsync(
        long sampleId,
        VoiceEmbeddingResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Values is null || result.Values.Count == 0)
            throw new ArgumentException("The voice embedding contains no values.", nameof(result));
        if (result.Values.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
            throw new ArgumentException("The voice embedding contains an invalid value.", nameof(result));

        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        long voicePersonId;
        using (var sample = connection.CreateCommand())
        {
            sample.Transaction = transaction;
            sample.CommandText = "SELECT voice_person_id FROM voice_samples WHERE id=$id LIMIT 1";
            sample.Parameters.AddWithValue("$id", sampleId);
            var value = sample.ExecuteScalar();
            if (value is null) throw new InvalidOperationException("The pending voice sample no longer exists.");
            voicePersonId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        var modelId = result.ModelId?.Trim() ?? string.Empty;
        var modelVersion = result.ModelVersion?.Trim() ?? string.Empty;
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE voice_samples
                   SET state='Ready',embedding_model_id=$model,embedding_model_version=$version,
                       embedding_json=$embedding,quality_score=$quality,error='',processed_at=$now
                 WHERE id=$id
                """;
            update.Parameters.AddWithValue("$model", modelId);
            update.Parameters.AddWithValue("$version", modelVersion);
            update.Parameters.AddWithValue("$embedding", JsonSerializer.Serialize(result.Values, JsonOptions));
            update.Parameters.AddWithValue("$quality", result.QualityScore.HasValue ? result.QualityScore.Value : DBNull.Value);
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$id", sampleId);
            update.ExecuteNonQuery();
        }

        RebuildProfile(connection, transaction, voicePersonId, modelId, modelVersion, cancellationToken);
        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task MarkVoiceSampleFailedAsync(long sampleId, string error, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE voice_samples
               SET state='Failed',error=$error,processed_at=$now
             WHERE id=$id
            """;
        command.Parameters.AddWithValue("$error", error ?? "Voice embedding failed.");
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", sampleId);
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SpeakerMatchSuggestion>> MatchClusterAsync(
        long episodeId,
        string speakerKey,
        VoiceEmbeddingResult embedding,
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        if (embedding.Values is null || embedding.Values.Count == 0)
            throw new ArgumentException("The speaker embedding contains no values.", nameof(embedding));
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        EnsureClustersForEpisode(connection, episodeId, cancellationToken);
        using var transaction = connection.BeginTransaction();
        var transcriptId = GetTranscriptId(connection, transaction, episodeId)
            ?? throw new InvalidOperationException("This broadcast does not have a transcript.");
        var clusterId = GetClusterId(connection, transaction, transcriptId, speakerKey)
            ?? throw new InvalidOperationException("The selected speaker cluster could not be found.");

        var candidates = new List<(long PersonId, string PersonName, int Revision, double Confidence, double Distance)>();
        using (var profiles = connection.CreateCommand())
        {
            profiles.Transaction = transaction;
            profiles.CommandText = """
                SELECT p.voice_person_id,vp.canonical_name,p.centroid_json,p.profile_revision
                  FROM voice_profiles p
                  JOIN voice_people vp ON vp.id=p.voice_person_id
                 WHERE p.active=1 AND p.sample_count>=2 AND p.embedding_model_id=$model AND p.embedding_model_version=$version
                """;
            profiles.Parameters.AddWithValue("$model", embedding.ModelId ?? "");
            profiles.Parameters.AddWithValue("$version", embedding.ModelVersion ?? "");
            using var reader = profiles.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var centroid = JsonSerializer.Deserialize<double[]>(reader.GetString(2), JsonOptions) ?? Array.Empty<double>();
                    if (centroid.Length != embedding.Values.Count || centroid.Length == 0) continue;
                    var similarity = CosineSimilarity(embedding.Values, centroid);
                    if (double.IsNaN(similarity)) continue;
                    var confidence = Math.Clamp(similarity, 0, 1);
                    candidates.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(3), confidence, 1 - confidence));
                }
                catch
                {
                    // A malformed historic profile cannot participate in matching.
                }
            }
        }

        var top = candidates
            .OrderByDescending(x => x.Confidence)
            .ThenBy(x => x.PersonName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 20))
            .ToList();
        var now = DateTimeOffset.UtcNow;
        using (var expire = connection.CreateCommand())
        {
            expire.Transaction = transaction;
            expire.CommandText = "UPDATE speaker_match_suggestions SET status='expired' WHERE transcript_speaker_id=$cluster AND status='pending'";
            expire.Parameters.AddWithValue("$cluster", clusterId);
            expire.ExecuteNonQuery();
        }

        foreach (var candidate in top)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO speaker_match_suggestions(
                    transcript_speaker_id,voice_person_id,confidence,distance,embedding_model_id,
                    profile_revision,status,created_at)
                VALUES($cluster,$person,$confidence,$distance,$model,$revision,'pending',$now)
                ON CONFLICT(transcript_speaker_id,voice_person_id,embedding_model_id,profile_revision) DO UPDATE SET
                    confidence=excluded.confidence,distance=excluded.distance,status='pending',created_at=excluded.created_at
                """;
            insert.Parameters.AddWithValue("$cluster", clusterId);
            insert.Parameters.AddWithValue("$person", candidate.PersonId);
            insert.Parameters.AddWithValue("$confidence", candidate.Confidence);
            insert.Parameters.AddWithValue("$distance", candidate.Distance);
            insert.Parameters.AddWithValue("$model", embedding.ModelId ?? "");
            insert.Parameters.AddWithValue("$revision", candidate.Revision);
            insert.Parameters.AddWithValue("$now", now.ToString("O"));
            insert.ExecuteNonQuery();
        }

        var best = top.FirstOrDefault();
        var secondBestConfidence = top.Count > 1 ? top[1].Confidence : 0d;
        var hasClearLead = top.Count <= 1 || best.Confidence - secondBestConfidence >= 0.04;
        if (best.PersonId > 0 && best.Confidence >= 0.84 && hasClearLead)
        {
            using var suggest = connection.CreateCommand();
            suggest.Transaction = transaction;
            suggest.CommandText = """
                UPDATE transcript_speakers
                   SET voice_person_id=$person,assignment_state='Suggested',assignment_confidence=$confidence,
                       assignment_source='voice-match',updated_at=$now
                 WHERE id=$cluster AND assignment_state='Unassigned'
                """;
            suggest.Parameters.AddWithValue("$person", best.PersonId);
            suggest.Parameters.AddWithValue("$confidence", best.Confidence);
            suggest.Parameters.AddWithValue("$now", now.ToString("O"));
            suggest.Parameters.AddWithValue("$cluster", clusterId);
            suggest.ExecuteNonQuery();
        }

        transaction.Commit();
        return GetSuggestionsAsync(clusterId, cancellationToken);
    }

    public Task<IReadOnlyList<SpeakerMatchSuggestion>> GetSuggestionsAsync(
        long transcriptSpeakerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<SpeakerMatchSuggestion>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sms.transcript_speaker_id,sms.voice_person_id,vp.canonical_name,sms.confidence,
                   sms.distance,sms.embedding_model_id,sms.profile_revision,sms.created_at
              FROM speaker_match_suggestions sms
              JOIN voice_people vp ON vp.id=sms.voice_person_id
             WHERE sms.transcript_speaker_id=$speaker AND sms.status='pending'
             ORDER BY sms.confidence DESC,vp.canonical_name COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$speaker", transcriptSpeakerId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new SpeakerMatchSuggestion
            {
                TranscriptSpeakerId = reader.GetInt64(0),
                VoicePersonId = reader.GetInt64(1),
                PersonName = reader.GetString(2),
                Confidence = reader.GetDouble(3),
                Distance = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                EmbeddingModelId = reader.GetString(5),
                ProfileRevision = reader.GetInt32(6),
                CreatedAt = ParseTimestamp(reader.GetString(7))
            });
        }
        return Task.FromResult<IReadOnlyList<SpeakerMatchSuggestion>>(result);
    }

    private static void EnsureClustersForEpisode(SqliteConnection connection, long episodeId, CancellationToken cancellationToken)
    {
        long transcriptId;
        using (var transcript = connection.CreateCommand())
        {
            transcript.CommandText = "SELECT id FROM transcripts WHERE episode_id=$episode LIMIT 1";
            transcript.Parameters.AddWithValue("$episode", episodeId);
            var value = transcript.ExecuteScalar();
            if (value is null) return;
            transcriptId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        using var transaction = connection.BeginTransaction();
        var rows = new List<(long Id, string Speaker, string Key, long StartMs, long EndMs)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id,speaker,speaker_key,start_ms,end_ms FROM transcript_segments WHERE transcript_id=$transcript ORDER BY segment_index";
            command.Parameters.AddWithValue("$transcript", transcriptId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4)));
            }
        }

        for (var index = 0; index < rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[index];
            var key = NormalizeSpeakerKey(row.Key, row.Speaker, index);
            if (key.Length == 0) continue;
            if (!string.Equals(key, row.Key, StringComparison.Ordinal))
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE transcript_segments SET speaker_key=$key WHERE id=$id";
                update.Parameters.AddWithValue("$key", key);
                update.Parameters.AddWithValue("$id", row.Id);
                update.ExecuteNonQuery();
                rows[index] = (row.Id, row.Speaker, key, row.StartMs, row.EndMs);
            }
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var group in rows.Where(x => !string.IsNullOrWhiteSpace(x.Key)).GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var label = group.Select(x => x.Speaker).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? group.Key;
            using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO transcript_speakers(
                    transcript_id,speaker_key,label,segment_count,speaking_duration_ms,assignment_state,
                    assignment_source,train_voice,created_at,updated_at)
                VALUES($transcript,$key,$label,$count,$duration,'Unassigned','',1,$now,$now)
                ON CONFLICT(transcript_id,speaker_key) DO UPDATE SET
                    label=excluded.label,segment_count=excluded.segment_count,
                    speaking_duration_ms=excluded.speaking_duration_ms,updated_at=excluded.updated_at
                """;
            upsert.Parameters.AddWithValue("$transcript", transcriptId);
            upsert.Parameters.AddWithValue("$key", group.Key);
            upsert.Parameters.AddWithValue("$label", label);
            upsert.Parameters.AddWithValue("$count", group.Count());
            upsert.Parameters.AddWithValue("$duration", group.Sum(x => Math.Max(0, x.EndMs - x.StartMs)));
            upsert.Parameters.AddWithValue("$now", now);
            upsert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static IReadOnlyList<TranscriptSpeakerCluster> ReadClusters(SqliteConnection connection, long episodeId)
    {
        var result = new List<TranscriptSpeakerCluster>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ts.id,ts.transcript_id,ts.speaker_key,ts.label,ts.segment_count,ts.speaking_duration_ms,
                   ts.voice_person_id,COALESCE(vp.canonical_name,''),ts.assignment_state,
                   ts.assignment_confidence,ts.assignment_source,ts.train_voice,ts.created_at,ts.updated_at
              FROM transcript_speakers ts
              JOIN transcripts t ON t.id=ts.transcript_id
              LEFT JOIN voice_people vp ON vp.id=ts.voice_person_id
             WHERE t.episode_id=$episode
             ORDER BY ts.speaker_key COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$episode", episodeId);
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

    private static int CreatePendingSamples(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long voicePersonId,
        long episodeId,
        long transcriptId,
        string speakerKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var ranges = new List<(long StartMs, long EndMs)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT start_ms,end_ms
                  FROM transcript_segments
                 WHERE transcript_id=$transcript AND speaker_key=$key AND end_ms-start_ms>=1500
                 ORDER BY (end_ms-start_ms) DESC,start_ms
                 LIMIT 8
                """;
            select.Parameters.AddWithValue("$transcript", transcriptId);
            select.Parameters.AddWithValue("$key", speakerKey);
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var start = reader.GetInt64(0);
                var end = Math.Min(reader.GetInt64(1), start + 30_000);
                if (end - start >= 1500) ranges.Add((start, end));
            }
        }

        var created = 0;
        foreach (var range in ranges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO voice_samples(
                    voice_person_id,episode_id,transcript_id,speaker_key,start_ms,end_ms,state,
                    confirmed_by_user,created_at)
                VALUES($person,$episode,$transcript,$key,$start,$end,'Pending',1,$now)
                ON CONFLICT(voice_person_id,episode_id,transcript_id,speaker_key,start_ms,end_ms) DO UPDATE SET
                    state='Pending',embedding_model_id='',embedding_model_version='',embedding_json='',
                    quality_score=NULL,confirmed_by_user=1,error='',created_at=excluded.created_at,processed_at=NULL
                WHERE voice_samples.state IN ('Rejected','Failed')
                """;
            insert.Parameters.AddWithValue("$person", voicePersonId);
            insert.Parameters.AddWithValue("$episode", episodeId);
            insert.Parameters.AddWithValue("$transcript", transcriptId);
            insert.Parameters.AddWithValue("$key", speakerKey);
            insert.Parameters.AddWithValue("$start", range.StartMs);
            insert.Parameters.AddWithValue("$end", range.EndMs);
            insert.Parameters.AddWithValue("$now", now.ToString("O"));
            created += insert.ExecuteNonQuery();
        }
        return created;
    }

    private static void RejectClusterSamples(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long transcriptId,
        string speakerKey,
        string reason,
        DateTimeOffset now)
    {
        using var reject = connection.CreateCommand();
        reject.Transaction = transaction;
        reject.CommandText = """
            UPDATE voice_samples
               SET state='Rejected',error=$reason,processed_at=$now
             WHERE transcript_id=$transcript AND speaker_key=$key AND state IN ('Pending','Ready','Failed')
            """;
        reject.Parameters.AddWithValue("$transcript", transcriptId);
        reject.Parameters.AddWithValue("$key", speakerKey ?? "");
        reject.Parameters.AddWithValue("$reason", reason ?? "Speaker evidence rejected by the user");
        reject.Parameters.AddWithValue("$now", now.ToString("O"));
        reject.ExecuteNonQuery();
    }

    private static void RebuildAllProfilesForPerson(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long voicePersonId,
        CancellationToken cancellationToken)
    {
        var models = new List<(string Id, string Version)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT embedding_model_id,embedding_model_version FROM voice_profiles WHERE voice_person_id=$person";
            command.Parameters.AddWithValue("$person", voicePersonId);
            using var reader = command.ExecuteReader();
            while (reader.Read()) models.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var model in models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RebuildProfile(connection, transaction, voicePersonId, model.Id, model.Version, cancellationToken);
        }
    }

    private static void RebuildProfile(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long voicePersonId,
        string modelId,
        string modelVersion,
        CancellationToken cancellationToken)
    {
        var embeddings = new List<(double[] Values, double? Quality)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT embedding_json,quality_score
                  FROM voice_samples
                 WHERE voice_person_id=$person AND state='Ready'
                   AND embedding_model_id=$model AND embedding_model_version=$version
                 ORDER BY id
                """;
            command.Parameters.AddWithValue("$person", voicePersonId);
            command.Parameters.AddWithValue("$model", modelId ?? "");
            command.Parameters.AddWithValue("$version", modelVersion ?? "");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var values = JsonSerializer.Deserialize<double[]>(reader.GetString(0), JsonOptions) ?? Array.Empty<double>();
                    if (values.Length > 0 && values.All(value => !double.IsNaN(value) && !double.IsInfinity(value)))
                        embeddings.Add((values, reader.IsDBNull(1) ? null : reader.GetDouble(1)));
                }
                catch
                {
                    // A malformed historic sample is ignored rather than poisoning the profile.
                }
            }
        }
        if (embeddings.Count == 0)
        {
            using var deactivate = connection.CreateCommand();
            deactivate.Transaction = transaction;
            deactivate.CommandText = """
                UPDATE voice_profiles
                   SET active=0,sample_count=0,centroid_json='[]',average_quality=NULL,
                       profile_revision=profile_revision+1,updated_at=$now
                 WHERE voice_person_id=$person AND embedding_model_id=$model AND embedding_model_version=$version
                """;
            deactivate.Parameters.AddWithValue("$person", voicePersonId);
            deactivate.Parameters.AddWithValue("$model", modelId ?? "");
            deactivate.Parameters.AddWithValue("$version", modelVersion ?? "");
            deactivate.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            deactivate.ExecuteNonQuery();
            return;
        }

        var dimensions = embeddings.GroupBy(x => x.Values.Length).OrderByDescending(x => x.Count()).First().Key;
        var compatible = embeddings.Where(x => x.Values.Length == dimensions).ToList();
        var centroid = new double[dimensions];
        foreach (var embedding in compatible)
        {
            for (var index = 0; index < dimensions; index++) centroid[index] += embedding.Values[index];
        }
        for (var index = 0; index < dimensions; index++) centroid[index] /= compatible.Count;
        var qualities = compatible.Where(x => x.Quality.HasValue).Select(x => x.Quality!.Value).ToList();
        var averageQuality = qualities.Count == 0 ? (double?)null : qualities.Average();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO voice_profiles(
                voice_person_id,embedding_model_id,embedding_model_version,embedding_dimensions,
                centroid_json,sample_count,average_quality,profile_revision,active,created_at,updated_at)
            VALUES($person,$model,$version,$dimensions,$centroid,$samples,$quality,1,1,$now,$now)
            ON CONFLICT(voice_person_id,embedding_model_id,embedding_model_version) DO UPDATE SET
                embedding_dimensions=excluded.embedding_dimensions,centroid_json=excluded.centroid_json,
                sample_count=excluded.sample_count,average_quality=excluded.average_quality,
                profile_revision=voice_profiles.profile_revision+1,active=1,updated_at=excluded.updated_at
            """;
        upsert.Parameters.AddWithValue("$person", voicePersonId);
        upsert.Parameters.AddWithValue("$model", modelId ?? "");
        upsert.Parameters.AddWithValue("$version", modelVersion ?? "");
        upsert.Parameters.AddWithValue("$dimensions", dimensions);
        upsert.Parameters.AddWithValue("$centroid", JsonSerializer.Serialize(centroid, JsonOptions));
        upsert.Parameters.AddWithValue("$samples", compatible.Count);
        upsert.Parameters.AddWithValue("$quality", averageQuality.HasValue ? averageQuality.Value : DBNull.Value);
        upsert.Parameters.AddWithValue("$now", now);
        upsert.ExecuteNonQuery();
    }

    private static VoicePersonRecord ReadVoicePerson(SqliteConnection connection, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,canonical_name,normalized_name,aliases_json,created_at,updated_at FROM voice_people WHERE id=$id LIMIT 1";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("The voice person could not be found.");
        return new VoicePersonRecord
        {
            Id = reader.GetInt64(0),
            CanonicalName = reader.GetString(1),
            NormalizedName = reader.GetString(2),
            AliasesJson = reader.GetString(3),
            CreatedAt = ParseTimestamp(reader.GetString(4)),
            UpdatedAt = ParseTimestamp(reader.GetString(5))
        };
    }

    private static VoiceProfileSummary? ReadProfileSummary(SqliteConnection connection, long voicePersonId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = ProfileSummarySql + " WHERE vp.id=$person LIMIT 1";
        command.Parameters.AddWithValue("$person", voicePersonId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProfileSummary(reader) : null;
    }

    private static VoiceProfileSummary ReadProfileSummary(SqliteDataReader reader) => new()
    {
        VoicePersonId = reader.GetInt64(0),
        PersonName = reader.GetString(1),
        ConfirmedClusterCount = reader.GetInt32(2),
        PendingSampleCount = reader.GetInt32(3),
        ReadySampleCount = reader.GetInt32(4),
        BroadcastCount = reader.GetInt32(5),
        EmbeddingModelId = reader.GetString(6),
        ProfileRevision = reader.GetInt32(7),
        LastUpdatedAt = reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8))
    };

    private static VoiceSampleRecord ReadVoiceSample(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        VoicePersonId = reader.GetInt64(1),
        PersonName = reader.GetString(2),
        EpisodeId = reader.GetInt64(3),
        TranscriptId = reader.GetInt64(4),
        SpeakerKey = reader.GetString(5),
        StartMs = reader.GetInt64(6),
        EndMs = reader.GetInt64(7),
        State = ParseEnum(reader.GetString(8), VoiceSampleState.Pending),
        EmbeddingModelId = reader.GetString(9),
        EmbeddingModelVersion = reader.GetString(10),
        EmbeddingJson = reader.GetString(11),
        QualityScore = reader.IsDBNull(12) ? null : reader.GetDouble(12),
        ConfirmedByUser = reader.GetInt32(13) == 1,
        Error = reader.GetString(14),
        CreatedAt = ParseTimestamp(reader.GetString(15)),
        ProcessedAt = reader.IsDBNull(16) ? null : ParseTimestamp(reader.GetString(16))
    };

    private static long GetOrCreateVoicePersonId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string personName,
        DateTimeOffset now)
    {
        var canonical = string.Join(' ', (personName ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var normalized = NormalizePersonName(canonical);
        if (normalized.Length == 0) throw new ArgumentException("A valid person name is required.", nameof(personName));
        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO voice_people(canonical_name,normalized_name,aliases_json,created_at,updated_at)
                VALUES($name,$normalized,'[]',$now,$now)
                ON CONFLICT(normalized_name) DO UPDATE SET
                    canonical_name=excluded.canonical_name,updated_at=excluded.updated_at
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

    private static long? GetTranscriptId(SqliteConnection connection, SqliteTransaction transaction, long episodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM transcripts WHERE episode_id=$episode LIMIT 1";
        command.Parameters.AddWithValue("$episode", episodeId);
        var value = command.ExecuteScalar();
        return value is null ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static long? GetClusterId(SqliteConnection connection, SqliteTransaction transaction, long transcriptId, string speakerKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM transcript_speakers WHERE transcript_id=$transcript AND speaker_key=$key LIMIT 1";
        command.Parameters.AddWithValue("$transcript", transcriptId);
        command.Parameters.AddWithValue("$key", speakerKey ?? "");
        var value = command.ExecuteScalar();
        return value is null ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void AddCandidates(List<TranscriptPersonCandidate> destination, string value, string role)
    {
        foreach (var name in SplitNames(value)) AddCandidate(destination, name, role);
    }

    private static void AddCandidate(List<TranscriptPersonCandidate> destination, string name, string role)
    {
        var compact = string.Join(' ', (name ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (compact.Length > 0) destination.Add(new TranscriptPersonCandidate(compact, role));
    }

    private static IEnumerable<string> SplitNames(string value)
        => (value ?? "").Split(new[] { '|', ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int RolePriority(string role) => role switch
    {
        "Host" => 0,
        "Guest" => 1,
        "Caller" => 2,
        "Mentioned" => 3,
        _ => 4
    };

    private static string NormalizeSpeakerKey(string? speakerKey, string? speakerLabel, int index)
    {
        var source = !string.IsNullOrWhiteSpace(speakerKey) ? speakerKey : speakerLabel;
        if (string.IsNullOrWhiteSpace(source)) return "";
        var chars = source.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var normalized = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length == 0 ? $"speaker-{index + 1}" : normalized;
    }

    private static string NormalizePersonName(string value)
        => new string((value ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static double CosineSimilarity(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        if (left.Count == 0 || left.Count != right.Count) return double.NaN;
        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }
        if (leftMagnitude <= 0 || rightMagnitude <= 0) return double.NaN;
        return dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static T ParseEnum<T>(string value, T fallback) where T : struct, Enum
        => Enum.TryParse<T>(value, true, out var parsed) ? parsed : fallback;

    private const string ProfileSummarySql = """
        SELECT vp.id,vp.canonical_name,
               (SELECT COUNT(*) FROM transcript_speakers ts WHERE ts.voice_person_id=vp.id AND ts.assignment_state='Confirmed'),
               (SELECT COUNT(*) FROM voice_samples vs WHERE vs.voice_person_id=vp.id AND vs.state='Pending'),
               (SELECT COUNT(*) FROM voice_samples vs WHERE vs.voice_person_id=vp.id AND vs.state='Ready'),
               (SELECT COUNT(DISTINCT t.episode_id) FROM transcript_speakers ts JOIN transcripts t ON t.id=ts.transcript_id WHERE ts.voice_person_id=vp.id AND ts.assignment_state='Confirmed'),
               COALESCE((SELECT embedding_model_id FROM voice_profiles p WHERE p.voice_person_id=vp.id AND p.active=1 ORDER BY p.updated_at DESC LIMIT 1),''),
               COALESCE((SELECT profile_revision FROM voice_profiles p WHERE p.voice_person_id=vp.id AND p.active=1 ORDER BY p.updated_at DESC LIMIT 1),0),
               (SELECT MAX(updated_at) FROM voice_profiles p WHERE p.voice_person_id=vp.id AND p.active=1)
          FROM voice_people vp
        """;

    private const string VoiceSampleSql = """
        SELECT vs.id,vs.voice_person_id,vp.canonical_name,vs.episode_id,vs.transcript_id,vs.speaker_key,
               vs.start_ms,vs.end_ms,vs.state,vs.embedding_model_id,vs.embedding_model_version,
               vs.embedding_json,vs.quality_score,vs.confirmed_by_user,vs.error,vs.created_at,vs.processed_at
          FROM voice_samples vs
          JOIN voice_people vp ON vp.id=vs.voice_person_id
        """;
}
