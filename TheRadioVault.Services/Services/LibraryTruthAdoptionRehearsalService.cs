using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

/// <summary>
/// Owns the Library Truth rehearsal and guarded-adoption pipeline. Rehearsal
/// always runs inside a disposable SQLite clone and proves backup, integrity
/// and rollback safety. Alpha10 seals the exact shadow plan and both forensic
/// ledgers so the separately guarded live API can reproduce them inside one
/// transaction without accepting a stale or edited rehearsal.
/// </summary>
public sealed partial class LibraryTruthAdoptionRehearsalService
{
    private readonly SqliteDatabase _database;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public LibraryTruthAdoptionRehearsalService(SqliteDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public LibraryTruthRehearsalSummary GetLatestSummary()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,truth_run_id,started_at,completed_at,status,backup_path,source_fingerprint,rollback_fingerprint,
                   truth_run_signature,item_signature,conflict_signature,
                   eligible_broadcasts,canonical_writes,recording_writes,segment_writes,coverage_writes,file_reassignments,
                   alias_rows_retired,state_rows_migrated,metadata_conflicts,auto_resolved_conflicts,unresolved_conflicts,preserved_alternates,
                   transcript_conflicts,foreign_key_violations,integrity_check,backup_restore_check,rollback_verified,message
              FROM library_truth_rehearsal_runs
             ORDER BY id DESC LIMIT 1
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSummary(reader) : LibraryTruthRehearsalSummary.Empty;
    }

    public IReadOnlyList<LibraryTruthRehearsalItem> GetLatestItems(int limit = 50000)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.id,i.rehearsal_run_id,i.canonical_key,i.survivor_episode_id,i.alias_episode_ids_json,
                   i.files_reassigned,i.state_rows_migrated,i.metadata_conflicts,i.auto_resolved_conflicts,i.unresolved_conflicts,
                   i.preserved_alternates,i.transcript_conflicts,i.outcome,i.evidence
              FROM library_truth_rehearsal_items i
             WHERE i.rehearsal_run_id=(SELECT COALESCE(MAX(id),0) FROM library_truth_rehearsal_runs)
             ORDER BY CASE i.outcome WHEN 'Needs review' THEN 0 ELSE 1 END,i.canonical_key
             LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50000));
        var result = new List<LibraryTruthRehearsalItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new LibraryTruthRehearsalItem(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3),
                FormatIds(reader.GetString(4)), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
                reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11),
                reader.GetString(12), reader.GetString(13)));
        }
        return result;
    }

    public LibraryTruthRehearsalSummary Run(
        string backupDirectory,
        IProgress<LibraryTruthRehearsalProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupDirectory))
            throw new ArgumentException("A backup directory is required.", nameof(backupDirectory));

        Directory.CreateDirectory(backupDirectory);
        var truthRunId = LatestTruthRunId();
        if (truthRunId == 0)
            throw new InvalidOperationException("Run and complete Library Truth before starting an adoption rehearsal.");

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(backupDirectory, $"RadioVault-before-adoption-rehearsal-{stamp}.db");
        var workingPath = Path.Combine(backupDirectory, $"RadioVault-adoption-rehearsal-{stamp}.working.db");
        long reportId = 0;
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            progress?.Report(new("Backup", 0, 1, "Creating a consistent pre-rehearsal SQLite backup…"));
            CreateOnlineBackup(_database.DatabasePath, backupPath, cancellationToken);
            var backupRestoreCheck = ValidateDatabaseFile(backupPath);
            if (!string.Equals(backupRestoreCheck, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The pre-rehearsal backup could not be validated: {backupRestoreCheck}");

            // The retained backup must be a true pre-rehearsal snapshot. Only
            // after it has reopened cleanly may the live shadow ledger record
            // that a disposable rehearsal is in progress.
            reportId = BeginReport(truthRunId, startedAt, backupPath);
            File.Copy(backupPath, workingPath, overwrite: true);
            progress?.Report(new("Backup", 1, 1, "Backup opened successfully; disposable working copy created."));

            var result = ExecuteOnClone(workingPath, truthRunId, progress, cancellationToken);
            var completedAt = DateTimeOffset.UtcNow;
            CompleteReport(reportId, truthRunId, completedAt, backupRestoreCheck, result);
            return GetSummary(reportId);
        }
        catch (OperationCanceledException)
        {
            if (reportId != 0) FailReport(reportId, "cancelled", "Adoption rehearsal was cancelled. The live library was not changed.");
            throw;
        }
        catch (Exception ex)
        {
            if (reportId != 0) FailReport(reportId, "failed", $"Adoption rehearsal failed safely: {ex.Message}. The live library was not changed.");
            throw;
        }
        finally
        {
            TryDelete(workingPath);
            TryDelete(workingPath + "-wal");
            TryDelete(workingPath + "-shm");
        }
    }

    private RehearsalExecution ExecuteOnClone(
        string workingPath,
        long truthRunId,
        IProgress<LibraryTruthRehearsalProgress>? progress,
        CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = workingPath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 10
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        Execute(connection, null, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;");

        var sourceFingerprint = ComputeLogicalFingerprint(connection, cancellationToken);
        var expectedEligible = ScalarInt(connection, null,
            $"SELECT COUNT(*) FROM library_truth_adoption_previews WHERE run_id={truthRunId} AND eligible_for_guarded_adoption=1");
        var previews = LoadEligiblePreviews(connection, truthRunId);
        if (previews.Count == 0)
            throw new InvalidOperationException("The latest Library Truth run contains no guarded adoption previews.");
        if (previews.Count != expectedEligible)
            throw new InvalidOperationException($"The adoption preview is incomplete: expected {expectedEligible:N0} eligible broadcasts but loaded {previews.Count:N0} valid plans.");

        var items = new List<RehearsalItemResult>(previews.Count);
        var canonicalWrites = 0;
        var recordingWrites = 0;
        var segmentWrites = 0;
        var coverageWrites = 0;
        var fileReassignments = 0;
        var aliasRowsRetired = 0;
        var stateRowsMigrated = 0;
        var metadataConflicts = 0;
        var autoResolvedConflicts = 0;
        var unresolvedConflicts = 0;
        var preservedAlternates = 0;
        var transcriptConflicts = 0;
        var conflictForensics = new List<ConflictForensicResult>();
        var foreignKeyViolations = 0;
        var integrityCheck = string.Empty;
        var rollbackFingerprint = string.Empty;
        var rolledBack = false;

        using (var transaction = connection.BeginTransaction())
        {
            CreateRehearsalSchema(connection, transaction);
            for (var index = 0; index < previews.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var preview = previews[index];
                if (index == 0 || index % 25 == 0 || index == previews.Count - 1)
                    progress?.Report(new("Rehearsal", index, previews.Count, $"Rehearsing {preview.CanonicalKey}…"));

                var item = RehearseBroadcast(connection, transaction, truthRunId, preview);
                items.Add(item);
                canonicalWrites += item.CanonicalWrites;
                recordingWrites += item.RecordingWrites;
                segmentWrites += item.SegmentWrites;
                coverageWrites += item.CoverageWrites;
                fileReassignments += item.FilesReassigned;
                aliasRowsRetired += item.AliasesRetired;
                stateRowsMigrated += item.StateRowsMigrated;
                metadataConflicts += item.MetadataConflicts;
                autoResolvedConflicts += item.AutoResolvedConflicts;
                unresolvedConflicts += item.UnresolvedConflicts;
                preservedAlternates += item.PreservedAlternates;
                transcriptConflicts += item.TranscriptConflicts;
                conflictForensics.AddRange(item.Conflicts);
            }

            foreignKeyViolations = ScalarInt(connection, transaction, "SELECT COUNT(*) FROM pragma_foreign_key_check");
            integrityCheck = Convert.ToString(Scalar(connection, transaction, "PRAGMA integrity_check"), CultureInfo.InvariantCulture) ?? "unknown";
            if (foreignKeyViolations != 0 || !string.Equals(integrityCheck, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Disposable migration integrity failed: {foreignKeyViolations} foreign-key violation(s), integrity={integrityCheck}.");

            transaction.Rollback();
            rolledBack = true;
        }

        progress?.Report(new("Rollback", 0, 1, "Verifying that rollback restored every non-shadow table…"));
        rollbackFingerprint = ComputeLogicalFingerprint(connection, cancellationToken);
        var rollbackVerified = rolledBack && string.Equals(sourceFingerprint, rollbackFingerprint, StringComparison.OrdinalIgnoreCase);
        if (!rollbackVerified)
            throw new InvalidOperationException("The disposable database did not return to its exact pre-rehearsal logical fingerprint after rollback.");
        progress?.Report(new("Rollback", 1, 1, "Rollback fingerprint verified. The working copy is unchanged."));

        return new RehearsalExecution(
            previews.Count, canonicalWrites, recordingWrites, segmentWrites, coverageWrites, fileReassignments,
            aliasRowsRetired, stateRowsMigrated, metadataConflicts, autoResolvedConflicts, unresolvedConflicts,
            preservedAlternates, transcriptConflicts, foreignKeyViolations, integrityCheck, sourceFingerprint,
            rollbackFingerprint, rollbackVerified, items, conflictForensics);
    }

    private RehearsalItemResult RehearseBroadcast(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long truthRunId,
        PreviewPlan preview)
    {
        var ids = preview.CurrentEpisodeIds;
        var survivor = preview.SurvivorEpisodeId;
        var aliases = ids.Where(x => x != survivor).OrderBy(x => x).ToArray();
        var idSql = string.Join(",", ids);
        var aliasSql = aliases.Length == 0 ? "NULL" : string.Join(",", aliases);

        var canonicalWrites = Execute(connection, transaction, """
            INSERT INTO rehearsal_canonical_broadcasts(canonical_key,collection_name,air_date,broadcast_slot,preferred_recording_key,confidence_score,source_truth_run_id)
            SELECT canonical_key,collection_name,air_date,broadcast_slot,preferred_recording_key,confidence_score,run_id
              FROM library_truth_broadcasts
             WHERE run_id=$run AND canonical_key=$key
            """, ("$run", truthRunId), ("$key", preview.CanonicalKey));

        var recordingWrites = Execute(connection, transaction, """
            INSERT INTO rehearsal_recordings(recording_key,canonical_key,label,duration_ms,role,completeness_score,preferred_score,is_preferred)
            SELECT recording_key,canonical_broadcast_key,label,duration_ms,role,completeness_score,preferred_score,is_preferred_candidate
              FROM library_truth_recordings
             WHERE run_id=$run AND canonical_broadcast_key=$key
            """, ("$run", truthRunId), ("$key", preview.CanonicalKey));

        var segmentWrites = Execute(connection, transaction, """
            INSERT INTO rehearsal_recording_segments(recording_key,segment_number,segment_total,start_offset_ms,end_offset_ms,media_file_ids_json)
            SELECT recording_key,segment_number,segment_total,start_offset_ms,end_offset_ms,media_file_ids_json
              FROM library_truth_coverages
             WHERE run_id=$run AND source_broadcast_key=$key AND requires_review=0
            """, ("$run", truthRunId), ("$key", preview.CanonicalKey));

        var coverageWrites = Execute(connection, transaction, """
            INSERT INTO rehearsal_recording_coverages(recording_key,segment_number,target_canonical_key,coverage_kind,start_offset_ms,end_offset_ms,confidence_score,requires_review,evidence)
            SELECT recording_key,segment_number,target_broadcast_key,coverage_kind,start_offset_ms,end_offset_ms,confidence_score,requires_review,evidence
              FROM library_truth_coverages
             WHERE run_id=$run AND source_broadcast_key=$key
            """, ("$run", truthRunId), ("$key", preview.CanonicalKey));

        foreach (var episodeId in ids)
        {
            Execute(connection, transaction, """
                INSERT INTO rehearsal_episode_canonical_map(episode_id,canonical_key,survivor_episode_id,is_survivor)
                VALUES($episode,$key,$survivor,$isSurvivor)
                """, ("$episode", episodeId), ("$key", preview.CanonicalKey), ("$survivor", survivor), ("$isSurvivor", episodeId == survivor ? 1 : 0));
        }

        var filesReassigned = Execute(connection, transaction, """
            UPDATE media_files
               SET episode_id=$survivor
             WHERE episode_id<>$survivor
               AND id IN (SELECT media_file_id FROM library_truth_files WHERE run_id=$run AND canonical_broadcast_key=$key)
            """, ("$survivor", survivor), ("$run", truthRunId), ("$key", preview.CanonicalKey));
        var linkedFileCount = Convert.ToInt32(Scalar(connection, transaction, """
            SELECT COUNT(*)
              FROM media_files
             WHERE episode_id=$survivor
               AND id IN (SELECT media_file_id FROM library_truth_files WHERE run_id=$run AND canonical_broadcast_key=$key)
            """, ("$survivor", survivor), ("$run", truthRunId), ("$key", preview.CanonicalKey)), CultureInfo.InvariantCulture);

        var conflictForensics = AnalyzeMetadataForensics(connection, transaction, truthRunId, preview.CanonicalKey, ids, survivor);
        var transcriptCount = ScalarInt(connection, transaction, $"SELECT COUNT(*) FROM transcripts WHERE episode_id IN ({idSql})");
        var transcriptConflictCount = transcriptCount > 1 ? transcriptCount - 1 : 0;
        var stateRows = 0;

        // Aggregate the state that has a clear lossless rule.
        stateRows += MergePlaybackState(connection, transaction, ids, survivor);
        stateRows += Execute(connection, transaction, $"UPDATE moments SET episode_id=$survivor WHERE episode_id IN ({aliasSql})", ("$survivor", survivor));
        stateRows += Execute(connection, transaction, $"UPDATE playback_queue SET episode_id=$survivor WHERE episode_id IN ({aliasSql})", ("$survivor", survivor));
        stateRows += MergeLinkTable(connection, transaction, "episode_guests", "guest_id", ids, survivor);
        stateRows += MergeLinkTable(connection, transaction, "episode_tags", "tag_id", ids, survivor);
        stateRows += Execute(connection, transaction, $"UPDATE research_import_changes SET episode_id=$survivor WHERE episode_id IN ({aliasSql})", ("$survivor", survivor));
        stateRows += Execute(connection, transaction, $"UPDATE research_field_provenance SET episode_id=$survivor WHERE episode_id IN ({aliasSql})", ("$survivor", survivor));
        stateRows += Execute(connection, transaction, $"UPDATE research_broadcasts SET episode_id=$survivor WHERE episode_id IN ({aliasSql})", ("$survivor", survivor));
        stateRows += Execute(connection, transaction, $"UPDATE research_conflicts SET episode_id=$survivor WHERE episode_id IN ({aliasSql})", ("$survivor", survivor));
        stateRows += Execute(connection, transaction, $"UPDATE research_quality_actions SET episode_id=$survivor WHERE episode_id IN ({aliasSql})", ("$survivor", survivor));
        stateRows += Execute(connection, transaction, $"UPDATE research_reconciliation_actions SET episode_id=$survivor WHERE episode_id IN ({aliasSql})", ("$survivor", survivor));
        stateRows += Execute(connection, transaction, $"UPDATE OR IGNORE research_reconciliation_candidates SET episode_id=$survivor WHERE episode_id IN ({aliasSql})", ("$survivor", survivor));
        stateRows += Execute(connection, transaction, $"UPDATE missing_broadcast_research SET matched_episode_id=$survivor WHERE matched_episode_id IN ({aliasSql})", ("$survivor", survivor));
        stateRows += Execute(connection, transaction, $"UPDATE missing_broadcast_research_revisions SET matched_episode_id=$survivor WHERE matched_episode_id IN ({aliasSql})", ("$survivor", survivor));
        stateRows += Execute(connection, transaction, $"UPDATE transcription_jobs SET episode_id=$survivor WHERE episode_id IN ({aliasSql})", ("$survivor", survivor));

        var headlineAnalysis = AnalyzeHeadlineReviewForensics(connection, transaction, preview.CanonicalKey, ids, survivor);
        stateRows += headlineAnalysis.StateRowsMigrated;
        conflictForensics.AddRange(headlineAnalysis.Forensics);

        // A single transcript can move losslessly. Multiple transcripts remain on mapped aliases and are surfaced as policy conflicts.
        if (transcriptCount == 1)
        {
            stateRows += Execute(connection, transaction, $"UPDATE transcripts SET episode_id=$survivor WHERE episode_id IN ({idSql}) AND episode_id<>$survivor", ("$survivor", survivor));
            stateRows += Execute(connection, transaction, $"UPDATE OR IGNORE transcript_imports SET episode_id=$survivor WHERE episode_id IN ({aliasSql})", ("$survivor", survivor));
            stateRows += Execute(connection, transaction, $"UPDATE OR IGNORE voice_samples SET episode_id=$survivor WHERE episode_id IN ({aliasSql})", ("$survivor", survivor));
        }

        var aliasAnalysis = AnalyzeRemainingAliasReferences(connection, transaction, preview.CanonicalKey, aliases, survivor);
        stateRows += aliasAnalysis.StateRowsMigrated;
        conflictForensics.AddRange(aliasAnalysis.Forensics);

        var metadataConflictCount = conflictForensics.Count;
        var autoResolvedConflictCount = conflictForensics.Count(x => x.AutoResolved && !x.RequiresReview);
        var unresolvedConflictCount = conflictForensics.Count(x => x.RequiresReview);
        var preservedAlternateCount = conflictForensics.Sum(x => x.PreservedAlternateCount);

        // Preserve favourite and the strongest listening status on the survivor.
        Execute(connection, transaction, $"""
            UPDATE episodes
               SET favourite=(SELECT MAX(favourite) FROM episodes WHERE id IN ({idSql})),
                   status=CASE
                       WHEN EXISTS(SELECT 1 FROM episodes WHERE id IN ({idSql}) AND status='Completed') THEN 'Completed'
                       WHEN EXISTS(SELECT 1 FROM episodes WHERE id IN ({idSql}) AND status='In Progress') THEN 'In Progress'
                       ELSE 'Unplayed' END,
                   updated_at=$now
             WHERE id=$survivor
            """, ("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)), ("$survivor", survivor));

        var aliasesRetired = aliases.Length == 0 ? 0 : Execute(connection, transaction,
            $"UPDATE episodes SET hidden=1,updated_at=$now WHERE id IN ({aliasSql})",
            ("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));

        if (canonicalWrites != 1 ||
            recordingWrites != preview.RecordingCount ||
            segmentWrites != preview.CoverageCount ||
            coverageWrites != preview.CoverageCount ||
            linkedFileCount != preview.MediaFileCount ||
            filesReassigned != preview.ReassignFileCount ||
            aliasesRetired != preview.RetireEpisodeCount)
        {
            throw new InvalidOperationException(
                $"The disposable operations for {preview.CanonicalKey} did not match its persisted adoption preview. " +
                $"Canonical {canonicalWrites}/1, recordings {recordingWrites}/{preview.RecordingCount}, " +
                $"segments {segmentWrites}/{preview.CoverageCount}, coverage {coverageWrites}/{preview.CoverageCount}, " +
                $"linked files {linkedFileCount}/{preview.MediaFileCount}, file reassignments {filesReassigned}/{preview.ReassignFileCount}, " +
                $"aliases {aliasesRetired}/{preview.RetireEpisodeCount}.");
        }

        var outcome = unresolvedConflictCount > 0 || transcriptConflictCount > 0 ? "Needs review" : "Policy resolved";
        var evidence = $"Disposable transaction created {canonicalWrites:N0} canonical row, {recordingWrites:N0} recording rows, {segmentWrites:N0} segment rows and {coverageWrites:N0} coverage rows. " +
                       $"It reassigned {filesReassigned:N0} media-file links, migrated {stateRows:N0} losslessly mergeable state rows and retired {aliasesRetired:N0} redundant live rows as mapped hidden aliases. " +
                       $"Policy analysis classified {metadataConflictCount:N0} field/reference difference(s): {autoResolvedConflictCount:N0} were resolved deterministically, {unresolvedConflictCount:N0} still require review, and {preservedAlternateCount:N0} alternate values were preserved. " +
                       $"{transcriptConflictCount:N0} additional transcript(s) require policy. Every change is rolled back.";

        return new RehearsalItemResult(preview.CanonicalKey, survivor, aliases, filesReassigned, stateRows,
            metadataConflictCount, autoResolvedConflictCount, unresolvedConflictCount, preservedAlternateCount,
            transcriptConflictCount, outcome, evidence, canonicalWrites, recordingWrites, segmentWrites,
            coverageWrites, aliasesRetired, conflictForensics);
    }

    private static int MergePlaybackState(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<long> ids, long survivor)
    {
        var idSql = string.Join(",", ids);
        var count = ScalarInt(connection, transaction, $"SELECT COUNT(*) FROM playback_state WHERE episode_id IN ({idSql})");
        if (count == 0) return 0;
        Execute(connection, transaction, $"""
            INSERT INTO playback_state(episode_id,position_ms,completed,last_played_at,play_count,duration_ms,playback_speed,completed_at,first_played_at,completion_count)
            SELECT $survivor,MAX(position_ms),MAX(completed),MAX(last_played_at),SUM(play_count),MAX(duration_ms),
                   COALESCE((SELECT playback_speed FROM playback_state WHERE episode_id IN ({idSql}) ORDER BY last_played_at DESC LIMIT 1),1.0),
                   MAX(completed_at),MIN(first_played_at),SUM(completion_count)
              FROM playback_state WHERE episode_id IN ({idSql})
            ON CONFLICT(episode_id) DO UPDATE SET
                position_ms=excluded.position_ms,completed=excluded.completed,last_played_at=excluded.last_played_at,
                play_count=excluded.play_count,duration_ms=excluded.duration_ms,playback_speed=excluded.playback_speed,
                completed_at=excluded.completed_at,first_played_at=excluded.first_played_at,completion_count=excluded.completion_count
            """, ("$survivor", survivor));
        Execute(connection, transaction, $"DELETE FROM playback_state WHERE episode_id IN ({idSql}) AND episode_id<>$survivor", ("$survivor", survivor));
        return count;
    }

    private static int MergeLinkTable(SqliteConnection connection, SqliteTransaction transaction, string table, string valueColumn, IReadOnlyList<long> ids, long survivor)
    {
        var idSql = string.Join(",", ids);
        var aliases = ids.Where(x => x != survivor).ToArray();
        if (aliases.Length == 0) return 0;
        var aliasSql = string.Join(",", aliases);
        var before = ScalarInt(connection, transaction, $"SELECT COUNT(*) FROM {table} WHERE episode_id IN ({aliasSql})");
        Execute(connection, transaction, $"INSERT OR IGNORE INTO {table}(episode_id,{valueColumn}) SELECT $survivor,{valueColumn} FROM {table} WHERE episode_id IN ({idSql})", ("$survivor", survivor));
        Execute(connection, transaction, $"DELETE FROM {table} WHERE episode_id IN ({aliasSql})");
        return before;
    }

    private static void CreateRehearsalSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE rehearsal_canonical_broadcasts(
                canonical_key TEXT PRIMARY KEY,collection_name TEXT NOT NULL,air_date TEXT NULL,broadcast_slot TEXT NOT NULL,
                preferred_recording_key TEXT NOT NULL,confidence_score INTEGER NOT NULL,source_truth_run_id INTEGER NOT NULL);
            CREATE TABLE rehearsal_recordings(
                recording_key TEXT PRIMARY KEY,canonical_key TEXT NOT NULL REFERENCES rehearsal_canonical_broadcasts(canonical_key),
                label TEXT NOT NULL,duration_ms INTEGER NOT NULL,role TEXT NOT NULL,completeness_score INTEGER NOT NULL,
                preferred_score INTEGER NOT NULL,is_preferred INTEGER NOT NULL);
            CREATE TABLE rehearsal_recording_segments(
                id INTEGER PRIMARY KEY AUTOINCREMENT,recording_key TEXT NOT NULL REFERENCES rehearsal_recordings(recording_key),
                segment_number INTEGER NOT NULL,segment_total INTEGER NULL,start_offset_ms INTEGER NOT NULL,end_offset_ms INTEGER NOT NULL,
                media_file_ids_json TEXT NOT NULL,UNIQUE(recording_key,segment_number));
            CREATE TABLE rehearsal_recording_coverages(
                id INTEGER PRIMARY KEY AUTOINCREMENT,recording_key TEXT NOT NULL REFERENCES rehearsal_recordings(recording_key),
                segment_number INTEGER NOT NULL,target_canonical_key TEXT NOT NULL,coverage_kind TEXT NOT NULL,start_offset_ms INTEGER NOT NULL,
                end_offset_ms INTEGER NOT NULL,confidence_score INTEGER NOT NULL,requires_review INTEGER NOT NULL,evidence TEXT NOT NULL);
            CREATE TABLE rehearsal_episode_canonical_map(
                episode_id INTEGER PRIMARY KEY REFERENCES episodes(id),canonical_key TEXT NOT NULL REFERENCES rehearsal_canonical_broadcasts(canonical_key),
                survivor_episode_id INTEGER NOT NULL REFERENCES episodes(id),is_survivor INTEGER NOT NULL);
            """);
    }

    private List<PreviewPlan> LoadEligiblePreviews(
        SqliteConnection connection,
        long truthRunId,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT canonical_key,provisional_episode_id,current_episode_ids_json,current_episode_count,
                   media_file_count,recording_count,coverage_count,retire_episode_count,reassign_file_count
              FROM library_truth_adoption_previews
             WHERE run_id=$run AND eligible_for_guarded_adoption=1
             ORDER BY canonical_key
            """;
        command.Parameters.AddWithValue("$run", truthRunId);
        var result = new List<PreviewPlan>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var canonicalKey = reader.GetString(0);
            if (reader.IsDBNull(1))
                throw new InvalidOperationException($"Eligible adoption preview {canonicalKey} has no provisional survivor.");
            var survivor = reader.GetInt64(1);
            var ids = (JsonSerializer.Deserialize<long[]>(reader.GetString(2), _json) ?? Array.Empty<long>())
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
            var expectedEpisodeCount = reader.GetInt32(3);
            if (ids.Length == 0 || ids.Length != expectedEpisodeCount || !ids.Contains(survivor))
                throw new InvalidOperationException($"Eligible adoption preview {canonicalKey} has inconsistent episode membership.");
            result.Add(new PreviewPlan(
                canonicalKey,
                survivor,
                ids,
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8)));
        }
        return result;
    }

    private long LatestTruthRunId()
    {
        using var connection = _database.OpenConnection();
        return Convert.ToInt64(Scalar(connection, null, "SELECT COALESCE(MAX(id),0) FROM library_truth_runs WHERE status='completed'"), CultureInfo.InvariantCulture);
    }

    private static void CreateOnlineBackup(string sourcePath, string backupPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryDelete(backupPath);
        var sourceBuilder = new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false };
        var destinationBuilder = new SqliteConnectionStringBuilder { DataSource = backupPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, Pooling = false };
        using var source = new SqliteConnection(sourceBuilder.ToString());
        using var destination = new SqliteConnection(destinationBuilder.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static string ValidateDatabaseFile(string path)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            Execute(connection, null, "PRAGMA foreign_keys=ON;");
            var foreignKeyViolations = ScalarInt(connection, null, "SELECT COUNT(*) FROM pragma_foreign_key_check");
            var integrity = Convert.ToString(Scalar(connection, null, "PRAGMA integrity_check"), CultureInfo.InvariantCulture) ?? "unknown";
            return foreignKeyViolations == 0 && string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase)
                ? "ok"
                : $"foreign keys={foreignKeyViolations}, integrity={integrity}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string ComputeLogicalFingerprint(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null,
        bool includeAdoptionTables = false)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = includeAdoptionTables
                ? """
                    SELECT name FROM sqlite_master
                     WHERE type='table' AND name NOT LIKE 'sqlite_%'
                       AND name NOT LIKE 'library_truth_%'
                       AND name NOT LIKE 'rehearsal_%'
                     ORDER BY name
                    """
                : """
                    SELECT name FROM sqlite_master
                     WHERE type='table' AND name NOT LIKE 'sqlite_%'
                       AND name NOT LIKE 'library_truth_%'
                       AND name NOT LIKE 'rehearsal_%'
                       AND name NOT IN ('canonical_broadcasts','recordings','recording_segments','recording_coverages','episode_canonical_map')
                     ORDER BY name
                    """;
            using var reader = command.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Append(hash, $"TABLE:{table}\n");
            var escapedTable = table.Replace("\"", "\"\"", StringComparison.Ordinal);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT * FROM \"{escapedTable}\" ORDER BY rowid";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (reader.IsDBNull(i)) Append(hash, "N;");
                    else if (reader.GetValue(i) is byte[] bytes)
                    {
                        Append(hash, "B:");
                        hash.AppendData(bytes);
                        Append(hash, ";");
                    }
                    else Append(hash, $"{reader.GetFieldType(i).Name}:{Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)};");
                }
                Append(hash, "\n");
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
        => hash.AppendData(Encoding.UTF8.GetBytes(value));

    private long BeginReport(long truthRunId, DateTimeOffset startedAt, string backupPath)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO library_truth_rehearsal_runs(truth_run_id,started_at,status,backup_path,message)
            VALUES($truth,$started,'running',$backup,'Preparing disposable sealed-plan rehearsal.');
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$truth", truthRunId);
        command.Parameters.AddWithValue("$started", startedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$backup", backupPath);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void CompleteReport(
        long reportId,
        long truthRunId,
        DateTimeOffset completedAt,
        string backupRestoreCheck,
        RehearsalExecution result)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        // Take SQLite's write reservation before sealing the plan. The report is
        // not marked completed until the exact shadow rows and both persisted
        // forensic ledgers have deterministic SHA-256 signatures.
        Execute(connection, transaction,
            "UPDATE library_truth_rehearsal_runs SET status='sealing',message='Sealing the exact truth plan and forensic ledgers.' WHERE id=$id",
            ("$id", reportId));

        var truthRunSignature = ComputeTruthRunSignature(connection, transaction, truthRunId, CancellationToken.None);
        PersistItems(connection, transaction, reportId, result.Items);
        PersistConflicts(connection, transaction, reportId, result.Conflicts);
        var itemSignature = ComputePersistedRehearsalItemSignature(connection, transaction, reportId);
        var conflictSignature = ComputePersistedRehearsalConflictSignature(connection, transaction, reportId);

        var itemCount = Convert.ToInt32(Scalar(connection, transaction,
            "SELECT COUNT(*) FROM library_truth_rehearsal_items WHERE rehearsal_run_id=$run", ("$run", reportId)), CultureInfo.InvariantCulture);
        var conflictCount = Convert.ToInt32(Scalar(connection, transaction,
            "SELECT COUNT(*) FROM library_truth_rehearsal_conflicts WHERE rehearsal_run_id=$run", ("$run", reportId)), CultureInfo.InvariantCulture);
        if (itemCount != result.Items.Count || conflictCount != result.Conflicts.Count)
            throw new InvalidOperationException(
                $"The sealed rehearsal ledger is incomplete: items {itemCount:N0}/{result.Items.Count:N0}, conflicts {conflictCount:N0}/{result.Conflicts.Count:N0}.");

        Execute(connection, transaction, """
            UPDATE library_truth_rehearsal_runs SET
                completed_at=$completed,status='completed',source_fingerprint=$source,rollback_fingerprint=$rollback,
                truth_run_signature=$truthSignature,item_signature=$itemSignature,conflict_signature=$conflictSignature,
                eligible_broadcasts=$eligible,canonical_writes=$canonical,recording_writes=$recordings,segment_writes=$segments,
                coverage_writes=$coverages,file_reassignments=$files,alias_rows_retired=$aliases,state_rows_migrated=$state,
                metadata_conflicts=$metadata,auto_resolved_conflicts=$autoResolved,unresolved_conflicts=$unresolved,
                preserved_alternates=$alternates,transcript_conflicts=$transcripts,foreign_key_violations=$fk,
                integrity_check=$integrity,backup_restore_check=$backupCheck,rollback_verified=$verified,
                message=$message
             WHERE id=$id
            """,
            ("$completed", completedAt.ToString("O", CultureInfo.InvariantCulture)),
            ("$source", result.SourceFingerprint),
            ("$rollback", result.RollbackFingerprint),
            ("$truthSignature", truthRunSignature),
            ("$itemSignature", itemSignature),
            ("$conflictSignature", conflictSignature),
            ("$eligible", result.EligibleBroadcasts),
            ("$canonical", result.CanonicalWrites),
            ("$recordings", result.RecordingWrites),
            ("$segments", result.SegmentWrites),
            ("$coverages", result.CoverageWrites),
            ("$files", result.FileReassignments),
            ("$aliases", result.AliasRowsRetired),
            ("$state", result.StateRowsMigrated),
            ("$metadata", result.MetadataConflicts),
            ("$autoResolved", result.AutoResolvedConflicts),
            ("$unresolved", result.UnresolvedConflicts),
            ("$alternates", result.PreservedAlternates),
            ("$transcripts", result.TranscriptConflicts),
            ("$fk", result.ForeignKeyViolations),
            ("$integrity", result.IntegrityCheck),
            ("$backupCheck", backupRestoreCheck),
            ("$verified", result.RollbackVerified ? 1 : 0),
            ("$message", $"Disposable adoption transaction completed for {result.EligibleBroadcasts:N0} broadcasts. The exact truth plan and both forensic ledgers were sealed. Policy analysis classified {result.MetadataConflicts:N0} field/reference differences, resolved {result.AutoResolvedConflicts:N0} deterministically, preserved {result.PreservedAlternates:N0} alternates and left {result.UnresolvedConflicts:N0} for review. SQLite integrity and rollback fingerprint checks passed. The live library was not changed."),
            ("$id", reportId));

        transaction.Commit();
    }

    private void PersistItems(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long reportId,
        IReadOnlyList<RehearsalItemResult> items)
    {
        foreach (var item in items)
        {
            Execute(connection, transaction, """
                INSERT INTO library_truth_rehearsal_items(
                    rehearsal_run_id,canonical_key,survivor_episode_id,alias_episode_ids_json,files_reassigned,state_rows_migrated,
                    metadata_conflicts,auto_resolved_conflicts,unresolved_conflicts,preserved_alternates,
                    transcript_conflicts,outcome,evidence)
                VALUES($run,$key,$survivor,$aliases,$files,$state,$metadata,$autoResolved,$unresolved,$alternates,$transcripts,$outcome,$evidence)
                """,
                ("$run", reportId),
                ("$key", item.CanonicalKey),
                ("$survivor", item.SurvivorEpisodeId),
                ("$aliases", JsonSerializer.Serialize(item.AliasEpisodeIds, _json)),
                ("$files", item.FilesReassigned),
                ("$state", item.StateRowsMigrated),
                ("$metadata", item.MetadataConflicts),
                ("$autoResolved", item.AutoResolvedConflicts),
                ("$unresolved", item.UnresolvedConflicts),
                ("$alternates", item.PreservedAlternates),
                ("$transcripts", item.TranscriptConflicts),
                ("$outcome", item.Outcome),
                ("$evidence", item.Evidence));
        }
    }

    private static void PersistConflicts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long reportId,
        IReadOnlyList<ConflictForensicResult> conflicts)
    {
        foreach (var conflict in conflicts)
        {
            Execute(connection, transaction, """
                INSERT INTO library_truth_rehearsal_conflicts(
                    rehearsal_run_id,canonical_key,field_name,conflict_kind,classification,selected_episode_id,
                    selected_value,candidate_values_json,provenance_json,resolution,auto_resolved,requires_review,
                    confidence_score,preserved_alternate_count,evidence)
                VALUES($run,$key,$field,$kind,$classification,$selectedEpisode,$selectedValue,$candidates,$provenance,
                       $resolution,$autoResolved,$requiresReview,$confidence,$alternates,$evidence)
                """,
                ("$run", reportId),
                ("$key", conflict.CanonicalKey),
                ("$field", conflict.FieldName),
                ("$kind", conflict.ConflictKind),
                ("$classification", conflict.Classification),
                ("$selectedEpisode", conflict.SelectedEpisodeId),
                ("$selectedValue", conflict.SelectedValue),
                ("$candidates", conflict.CandidateValuesJson),
                ("$provenance", conflict.ProvenanceJson),
                ("$resolution", conflict.Resolution),
                ("$autoResolved", conflict.AutoResolved ? 1 : 0),
                ("$requiresReview", conflict.RequiresReview ? 1 : 0),
                ("$confidence", conflict.ConfidenceScore),
                ("$alternates", conflict.PreservedAlternateCount),
                ("$evidence", conflict.Evidence));
        }
    }

    private void FailReport(long reportId, string status, string message)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE library_truth_rehearsal_runs SET completed_at=$completed,status=$status,message=$message WHERE id=$id";
        command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$id", reportId);
        command.ExecuteNonQuery();
    }

    private LibraryTruthRehearsalSummary GetSummary(long id)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,truth_run_id,started_at,completed_at,status,backup_path,source_fingerprint,rollback_fingerprint,
                   truth_run_signature,item_signature,conflict_signature,
                   eligible_broadcasts,canonical_writes,recording_writes,segment_writes,coverage_writes,file_reassignments,
                   alias_rows_retired,state_rows_migrated,metadata_conflicts,auto_resolved_conflicts,unresolved_conflicts,preserved_alternates,
                   transcript_conflicts,foreign_key_violations,integrity_check,backup_restore_check,rollback_verified,message
              FROM library_truth_rehearsal_runs WHERE id=$id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSummary(reader) : LibraryTruthRehearsalSummary.Empty;
    }

    private static LibraryTruthRehearsalSummary ReadSummary(SqliteDataReader reader)
        => new(
            reader.GetInt64(0), reader.GetInt64(1), DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
            reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetInt32(11),
            reader.GetInt32(12), reader.GetInt32(13), reader.GetInt32(14), reader.GetInt32(15), reader.GetInt32(16),
            reader.GetInt32(17), reader.GetInt32(18), reader.GetInt32(19), reader.GetInt32(20), reader.GetInt32(21),
            reader.GetInt32(22), reader.GetInt32(23), reader.GetInt32(24), reader.GetString(25), reader.GetString(26),
            reader.GetInt64(27) != 0, reader.GetString(28));

    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command.ExecuteScalar();
    }

    private static int ScalarInt(SqliteConnection connection, SqliteTransaction? transaction, string sql)
        => Convert.ToInt32(Scalar(connection, transaction, sql), CultureInfo.InvariantCulture);

    private static string FormatIds(string json)
    {
        try
        {
            var ids = JsonSerializer.Deserialize<long[]>(json) ?? Array.Empty<long>();
            return string.Join(", ", ids.Select(x => x.ToString("N0", CultureInfo.CurrentCulture)));
        }
        catch { return json; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record PreviewPlan(
        string CanonicalKey,
        long SurvivorEpisodeId,
        IReadOnlyList<long> CurrentEpisodeIds,
        int MediaFileCount,
        int RecordingCount,
        int CoverageCount,
        int RetireEpisodeCount,
        int ReassignFileCount);
    private sealed record RehearsalItemResult(
        string CanonicalKey,long SurvivorEpisodeId,IReadOnlyList<long> AliasEpisodeIds,int FilesReassigned,int StateRowsMigrated,
        int MetadataConflicts,int AutoResolvedConflicts,int UnresolvedConflicts,int PreservedAlternates,int TranscriptConflicts,
        string Outcome,string Evidence,int CanonicalWrites,int RecordingWrites,int SegmentWrites,int CoverageWrites,
        int AliasesRetired,IReadOnlyList<ConflictForensicResult> Conflicts);
    private sealed record RehearsalExecution(
        int EligibleBroadcasts,int CanonicalWrites,int RecordingWrites,int SegmentWrites,int CoverageWrites,int FileReassignments,
        int AliasRowsRetired,int StateRowsMigrated,int MetadataConflicts,int AutoResolvedConflicts,int UnresolvedConflicts,
        int PreservedAlternates,int TranscriptConflicts,int ForeignKeyViolations,string IntegrityCheck,string SourceFingerprint,
        string RollbackFingerprint,bool RollbackVerified,IReadOnlyList<RehearsalItemResult> Items,
        IReadOnlyList<ConflictForensicResult> Conflicts);
}
