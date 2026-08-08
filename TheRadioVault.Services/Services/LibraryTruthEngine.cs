using System.Text.Json;
using Microsoft.Data.Sqlite;
using TheRadioVault.Core.LibraryTruth;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

/// <summary>
/// Builds a non-destructive shadow interpretation of the physical archive.
/// Alpha7 preserves the confirmed shadow model and adds a disposable transactional adoption rehearsal. Live episodes, playback, research, transcripts and files remain untouched.
/// </summary>
public sealed class LibraryTruthEngine
{
    private readonly SqliteDatabase _database;
    private readonly LibraryTruthParser _parser = new();
    private readonly LibraryTruthContextAnalyzer _contextAnalyzer = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public LibraryTruthEngine(SqliteDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public LibraryTruthRunResult BuildShadowIndex(
        IProgress<(double Percent, string Message)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var runId = CreateRun(startedAt);
        try
        {
            progress?.Report((1, "Reading the live file inventory…"));
            var sourceFiles = ReadSourceFiles(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report((5, $"Learning folder conventions from {sourceFiles.Count:N0} files…"));
            var contexts = _contextAnalyzer.Analyse(sourceFiles);
            var interpretations = new List<LibraryTruthInterpretation>(sourceFiles.Count);
            for (var index = 0; index < sourceFiles.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var input = sourceFiles[index];
                var key = LibraryTruthContextAnalyzer.ContextKey(input);
                var context = contexts.TryGetValue(key, out var found)
                    ? found
                    : new LibraryTruthFolderContext
                    {
                        ContextKey = key,
                        LibraryRoot = input.LibraryRoot,
                        AssignedCollectionName = input.AssignedCollectionName,
                        FileCount = 1
                    };
                interpretations.Add(_parser.Parse(input, context));
                if (index == 0 || index % 50 == 0 || index == sourceFiles.Count - 1)
                {
                    var percent = 5d + (sourceFiles.Count == 0 ? 70d : (index + 1d) / sourceFiles.Count * 70d);
                    progress?.Report((percent, $"Parser V3 analysed {index + 1:N0} of {sourceFiles.Count:N0} physical files…"));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((78, "Constructing canonical broadcasts, recordings and coverage relationships…"));
            var analysis = AnalyseGroups(interpretations);
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report((85, "Writing coverage evidence and the adoption preview without changing the live library…"));
            var summary = StoreRun(runId, startedAt, interpretations, analysis, cancellationToken);
            progress?.Report((100, summary.Message));
            return new LibraryTruthRunResult(summary);
        }
        catch (OperationCanceledException)
        {
            MarkRun(runId, "cancelled", "Shadow analysis cancelled. The live library was not changed.");
            throw;
        }
        catch (Exception ex)
        {
            MarkRun(runId, "failed", ex.Message);
            throw;
        }
    }

    public LibraryTruthRunSummary GetLatestSummary()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,status,parser_version,started_at,completed_at,source_file_count,current_broadcast_count,
                   proposed_broadcast_count,unchanged_files,changed_files,recovered_dates,unknown_dates,
                   needs_review,merge_groups,split_groups,exact_duplicate_groups,strong_duplicate_groups,
                   multipart_broadcasts,message
              FROM library_truth_runs
             ORDER BY CASE status WHEN 'completed' THEN 0 ELSE 1 END, id DESC
             LIMIT 1
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSummary(reader) : LibraryTruthRunSummary.Empty;
    }

    public IReadOnlyList<LibraryTruthFileView> GetFiles(string? filter = null, string? search = null, int limit = 10000)
    {
        var run = GetLatestCompletedRunId();
        if (run == 0) return Array.Empty<LibraryTruthFileView>();
        var result = new List<LibraryTruthFileView>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        var filterSql = FilterSql(filter);
        command.CommandText = $"""
            SELECT f.id,f.media_file_id,f.current_episode_id,f.original_filename,f.path,
                   f.current_collection,COALESCE(f.current_air_date,''),COALESCE(f.current_slot,''),f.current_part,
                   f.proposed_collection,COALESCE(f.proposed_air_date,''),COALESCE(f.proposed_slot,''),f.proposed_part,
                   COALESCE(f.proposed_total_parts,0),COALESCE(f.proposed_headline,''),f.disposition,f.change_summary,
                   f.confidence_score,f.confidence,f.canonical_broadcast_key,f.recording_key,f.evidence_json,f.warnings_json
              FROM library_truth_files f
              LEFT JOIN library_truth_broadcasts b
                ON b.run_id=f.run_id AND b.canonical_key=f.canonical_broadcast_key
             WHERE f.run_id=$run
               {filterSql}
               AND ($search='' OR lower(f.original_filename) LIKE $like OR lower(f.path) LIKE $like
                    OR lower(f.proposed_collection) LIKE $like OR lower(COALESCE(f.proposed_air_date,'')) LIKE $like)
             ORDER BY CASE f.disposition
                        WHEN 'Needs attention' THEN 0
                        WHEN 'Recovered date' THEN 1
                        WHEN 'Broadcast split' THEN 2
                        WHEN 'Broadcast merge' THEN 3
                        WHEN 'Multipart correction' THEN 4
                        WHEN 'Proposed correction' THEN 5
                        ELSE 9 END,
                      f.proposed_air_date,f.proposed_collection,f.original_filename
             LIMIT $limit
            """;
        command.Parameters.AddWithValue("$run", run);
        var normalizedSearch = (search ?? string.Empty).Trim().ToLowerInvariant();
        command.Parameters.AddWithValue("$search", normalizedSearch);
        command.Parameters.AddWithValue("$like", $"%{normalizedSearch}%");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50000));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var total = reader.GetInt32(13);
            result.Add(new LibraryTruthFileView(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), EmptyAsUnknown(reader.GetString(6)), EmptyAsStandard(reader.GetString(7)), PartDisplay(reader.GetInt32(8), null),
                reader.GetString(9), EmptyAsUnknown(reader.GetString(10)), EmptyAsStandard(reader.GetString(11)), PartDisplay(reader.GetInt32(12), total > 0 ? total : null),
                reader.GetString(14), reader.GetString(15), reader.GetString(16), reader.GetInt32(17), reader.GetString(18),
                reader.GetString(19), reader.GetString(20), FormatEvidence(reader.GetString(21)), FormatWarnings(reader.GetString(22))));
        }
        return result;
    }

    public IReadOnlyList<LibraryTruthBroadcastView> GetBroadcasts(string? filter = null, int limit = 10000)
    {
        var run = GetLatestCompletedRunId();
        if (run == 0) return Array.Empty<LibraryTruthBroadcastView>();
        var result = new List<LibraryTruthBroadcastView>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        var where = BroadcastFilterSql(filter);
        command.CommandText = $"""
            SELECT id,canonical_key,collection_name,COALESCE(air_date,''),COALESCE(broadcast_slot,''),
                   file_count,segment_count,recording_count,exact_duplicate_count,strong_duplicate_count,
                   current_episode_count,status,confidence_score,evidence_json,
                   COALESCE(adoption_state,'Not assessed'),COALESCE(adoption_reason,''),
                   COALESCE(preferred_recording_key,''),COALESCE(suspicious_merge,0),
                   COALESCE(duration_spread_ratio,1.0),COALESCE(cross_identity_conflict_count,0)
              FROM library_truth_broadcasts
             WHERE run_id=$run {where}
             ORDER BY CASE adoption_state WHEN 'Blocked' THEN 0 WHEN 'Review recommended' THEN 1 WHEN 'Ready with recording choice' THEN 2 ELSE 3 END,
                      CASE status WHEN 'Needs attention' THEN 0 WHEN 'Proposed changes' THEN 1 ELSE 2 END,
                      air_date,collection_name,broadcast_slot
             LIMIT $limit
            """;
        command.Parameters.AddWithValue("$run", run);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50000));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new LibraryTruthBroadcastView(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), EmptyAsUnknown(reader.GetString(3)),
                EmptyAsStandard(reader.GetString(4)), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
                reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetString(11), reader.GetInt32(12),
                reader.GetString(13), reader.GetString(14), reader.GetString(15), reader.GetString(16),
                reader.GetInt64(17) != 0, reader.GetDouble(18), reader.GetInt32(19)));
        }
        return result;
    }

    public IReadOnlyList<LibraryTruthRecordingView> GetRecordings(
        string? canonicalBroadcastKey = null,
        string? search = null,
        int limit = 50000,
        string? filter = null)
    {
        var run = GetLatestCompletedRunId();
        if (run == 0) return Array.Empty<LibraryTruthRecordingView>();
        var result = new List<LibraryTruthRecordingView>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        var filterSql = BroadcastFilterSql(filter, "b");
        command.CommandText = $"""
            SELECT r.id,r.canonical_broadcast_key,r.recording_key,r.label,r.file_count,r.segment_count,r.duration_ms,
                   r.relationship,r.confidence_score,r.evidence_json,COALESCE(r.role,'Unknown'),
                   COALESCE(r.completeness_score,0),COALESCE(r.preferred_score,0),COALESCE(r.duration_ratio,0),
                   COALESCE(r.is_preferred_candidate,0),COALESCE(r.review_reason,'')
              FROM library_truth_recordings r
              JOIN library_truth_broadcasts b
                ON b.run_id=r.run_id AND b.canonical_key=r.canonical_broadcast_key
             WHERE r.run_id=$run
               {filterSql}
               AND ($key='' OR r.canonical_broadcast_key=$key)
               AND ($search='' OR lower(r.canonical_broadcast_key) LIKE $like OR lower(r.label) LIKE $like
                    OR lower(r.relationship) LIKE $like OR lower(COALESCE(r.role,'')) LIKE $like)
             ORDER BY r.canonical_broadcast_key,
                      COALESCE(r.is_preferred_candidate,0) DESC,COALESCE(r.preferred_score,0) DESC,
                      r.duration_ms DESC,r.recording_key
             LIMIT $limit
            """;
        command.Parameters.AddWithValue("$run", run);
        command.Parameters.AddWithValue("$key", (canonicalBroadcastKey ?? string.Empty).Trim());
        var normalizedSearch = (search ?? string.Empty).Trim().ToLowerInvariant();
        command.Parameters.AddWithValue("$search", normalizedSearch);
        command.Parameters.AddWithValue("$like", $"%{normalizedSearch}%");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50000));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new LibraryTruthRecordingView(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetInt64(6), reader.GetString(7),
                reader.GetInt32(8), reader.GetString(9), reader.GetString(10), reader.GetInt32(11),
                reader.GetInt32(12), reader.GetDouble(13), reader.GetInt64(14) != 0, reader.GetString(15)));
        }
        return result;
    }

    public LibraryTruthAdoptionSummary GetAdoptionSummary()
    {
        var run = GetLatestCompletedRunId();
        if (run == 0) return LibraryTruthAdoptionSummary.Empty;
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN adoption_state='Ready' THEN 1 ELSE 0 END),
                SUM(CASE WHEN adoption_state='Ready with recording choice' THEN 1 ELSE 0 END),
                SUM(CASE WHEN adoption_state='Review recommended' THEN 1 ELSE 0 END),
                SUM(CASE WHEN adoption_state='Blocked' THEN 1 ELSE 0 END),
                (SELECT COUNT(*) FROM library_truth_recordings WHERE run_id=$run AND is_preferred_candidate=1),
                (SELECT COUNT(*) FROM library_truth_recordings WHERE run_id=$run AND role IN ('Partial recording','Partial multipart recording','Incomplete multipart recording','Multipart recording with unknown extent')),
                (SELECT COUNT(*) FROM library_truth_recordings WHERE run_id=$run AND role='Short fragment or clip'),
                (SELECT COUNT(*) FROM library_truth_recordings WHERE run_id=$run AND role='Likely truncated or damaged'),
                SUM(CASE WHEN suspicious_merge=1 THEN 1 ELSE 0 END),
                (SELECT COUNT(*) FROM library_truth_conflicts WHERE run_id=$run)
              FROM library_truth_broadcasts
             WHERE run_id=$run
            """;
        command.Parameters.AddWithValue("$run", run);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return LibraryTruthAdoptionSummary.Empty;
        return new LibraryTruthAdoptionSummary(
            SafeInt(reader, 0), SafeInt(reader, 1), SafeInt(reader, 2), SafeInt(reader, 3), SafeInt(reader, 4),
            SafeInt(reader, 5), SafeInt(reader, 6), SafeInt(reader, 7), SafeInt(reader, 8), SafeInt(reader, 9));
    }

    public IReadOnlyList<LibraryTruthYearView> GetYears()
    {
        var run = GetLatestCompletedRunId();
        if (run == 0) return Array.Empty<LibraryTruthYearView>();
        var result = new List<LibraryTruthYearView>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT year_label,physical_file_count,current_broadcast_count,proposed_broadcast_count,
                   merge_groups,split_groups,ready_broadcasts,review_recommended_broadcasts,blocked_broadcasts
              FROM library_truth_years
             WHERE run_id=$run
             ORDER BY CASE year_label WHEN 'Unknown' THEN 1 ELSE 0 END,year_label
            """;
        command.Parameters.AddWithValue("$run", run);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new LibraryTruthYearView(reader.GetString(0),reader.GetInt32(1),reader.GetInt32(2),reader.GetInt32(3),
                reader.GetInt32(4),reader.GetInt32(5),reader.GetInt32(6),reader.GetInt32(7),reader.GetInt32(8)));
        return result;
    }

    public IReadOnlyList<LibraryTruthConflictView> GetConflicts()
    {
        var run = GetLatestCompletedRunId();
        if (run == 0) return Array.Empty<LibraryTruthConflictView>();
        var result = new List<LibraryTruthConflictView>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,conflict_type,evidence_strength,file_count,identity_count,identities,evidence
              FROM library_truth_conflicts
             WHERE run_id=$run
             ORDER BY evidence_strength DESC,id
            """;
        command.Parameters.AddWithValue("$run", run);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new LibraryTruthConflictView(reader.GetInt64(0),reader.GetString(1),reader.GetInt32(2),
                reader.GetInt32(3),reader.GetInt32(4),reader.GetString(5),reader.GetString(6)));
        return result;
    }

    public IReadOnlyList<LibraryTruthCoverageView> GetCoverages(
        string? search = null,
        string? filter = null,
        bool reviewOnly = false,
        int limit = 50000)
    {
        var run = GetLatestCompletedRunId();
        if (run == 0) return Array.Empty<LibraryTruthCoverageView>();
        var result = new List<LibraryTruthCoverageView>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        var filterSql = BroadcastFilterSql(filter, "b");
        command.CommandText = $"""
            SELECT c.id,c.source_broadcast_key,c.recording_key,c.segment_number,c.segment_total,c.target_broadcast_key,
                   c.coverage_kind,c.start_offset_ms,c.end_offset_ms,c.confidence_score,c.requires_review,
                   c.media_file_ids_json,c.evidence
              FROM library_truth_coverages c
              JOIN library_truth_broadcasts b
                ON b.run_id=c.run_id AND b.canonical_key=c.source_broadcast_key
             WHERE c.run_id=$run
               {filterSql}
               AND ($reviewOnly=0 OR c.requires_review=1)
               AND ($search='' OR lower(c.source_broadcast_key) LIKE $like OR lower(c.target_broadcast_key) LIKE $like
                    OR lower(c.recording_key) LIKE $like OR lower(c.coverage_kind) LIKE $like OR lower(c.evidence) LIKE $like)
             ORDER BY c.requires_review DESC,c.source_broadcast_key,c.recording_key,c.segment_number,c.target_broadcast_key
             LIMIT $limit
            """;
        command.Parameters.AddWithValue("$run", run);
        command.Parameters.AddWithValue("$reviewOnly", reviewOnly ? 1 : 0);
        var normalizedSearch = (search ?? string.Empty).Trim().ToLowerInvariant();
        command.Parameters.AddWithValue("$search", normalizedSearch);
        command.Parameters.AddWithValue("$like", $"%{normalizedSearch}%");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50000));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new LibraryTruthCoverageView(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.GetString(5), reader.GetString(6),
                reader.GetInt64(7), reader.GetInt64(8), reader.GetInt32(9), reader.GetInt64(10) != 0,
                FormatMediaIds(reader.GetString(11)), reader.GetString(12)));
        }
        return result;
    }

    public IReadOnlyList<LibraryTruthAdoptionPreviewView> GetAdoptionPreviews(
        string? filter = null,
        string? search = null,
        int limit = 50000)
    {
        var run = GetLatestCompletedRunId();
        if (run == 0) return Array.Empty<LibraryTruthAdoptionPreviewView>();
        var result = new List<LibraryTruthAdoptionPreviewView>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        var filterSql = BroadcastFilterSql(filter, "b");
        command.CommandText = $"""
            SELECT p.id,p.canonical_key,p.adoption_state,p.planned_action,p.provisional_episode_id,p.current_episode_count,
                   p.current_episode_ids_json,p.media_file_count,p.recording_count,p.coverage_count,p.retire_episode_count,
                   p.reassign_file_count,p.planned_write_count,p.eligible_for_guarded_adoption,p.guard_reason,p.evidence
              FROM library_truth_adoption_previews p
              JOIN library_truth_broadcasts b
                ON b.run_id=p.run_id AND b.canonical_key=p.canonical_key
             WHERE p.run_id=$run
               {filterSql}
               AND ($search='' OR lower(p.canonical_key) LIKE $like OR lower(p.planned_action) LIKE $like
                    OR lower(p.guard_reason) LIKE $like OR lower(p.evidence) LIKE $like)
             ORDER BY p.eligible_for_guarded_adoption ASC,
                      CASE p.adoption_state WHEN 'Blocked' THEN 0 WHEN 'Review recommended' THEN 1 ELSE 2 END,
                      p.canonical_key
             LIMIT $limit
            """;
        command.Parameters.AddWithValue("$run", run);
        var normalizedSearch = (search ?? string.Empty).Trim().ToLowerInvariant();
        command.Parameters.AddWithValue("$search", normalizedSearch);
        command.Parameters.AddWithValue("$like", $"%{normalizedSearch}%");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50000));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new LibraryTruthAdoptionPreviewView(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetInt32(5), FormatMediaIds(reader.GetString(6)),
                reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11),
                reader.GetInt32(12), reader.GetInt64(13) != 0, reader.GetString(14), reader.GetString(15)));
        }
        return result;
    }

    public LibraryTruthAdoptionPlanSummary GetAdoptionPlanSummary()
    {
        var run = GetLatestCompletedRunId();
        if (run == 0) return LibraryTruthAdoptionPlanSummary.Empty;
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                   SUM(CASE WHEN eligible_for_guarded_adoption=1 THEN 1 ELSE 0 END),
                   SUM(CASE WHEN adoption_state='Review recommended' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN adoption_state='Blocked' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN eligible_for_guarded_adoption=1 THEN 1 ELSE 0 END),
                   SUM(CASE WHEN eligible_for_guarded_adoption=1 THEN recording_count ELSE 0 END),
                   SUM(CASE WHEN eligible_for_guarded_adoption=1 THEN coverage_count ELSE 0 END),
                   SUM(CASE WHEN eligible_for_guarded_adoption=1 THEN media_file_count ELSE 0 END),
                   SUM(CASE WHEN eligible_for_guarded_adoption=1 THEN retire_episode_count ELSE 0 END),
                   SUM(CASE WHEN eligible_for_guarded_adoption=1 AND provisional_episode_id IS NOT NULL THEN 1 ELSE 0 END)
              FROM library_truth_adoption_previews
             WHERE run_id=$run
            """;
        command.Parameters.AddWithValue("$run", run);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return LibraryTruthAdoptionPlanSummary.Empty;
        return new LibraryTruthAdoptionPlanSummary(
            SafeInt(reader, 0), SafeInt(reader, 1), SafeInt(reader, 2), SafeInt(reader, 3), SafeInt(reader, 4),
            SafeInt(reader, 5), SafeInt(reader, 6), SafeInt(reader, 7), SafeInt(reader, 8), SafeInt(reader, 9));
    }

    public void ExportLatest(string path, string appVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var rehearsal = new LibraryTruthAdoptionRehearsalService(_database);
        var report = new LibraryTruthExportReport
        {
            AppVersion = appVersion,
            ExportedAt = DateTimeOffset.UtcNow,
            Summary = GetLatestSummary(),
            Adoption = GetAdoptionSummary(),
            Files = GetFiles(limit: 50000),
            Broadcasts = GetBroadcasts(limit: 50000),
            Recordings = GetRecordings(limit: 50000),
            Years = GetYears(),
            Conflicts = GetConflicts(),
            AdoptionPlan = GetAdoptionPlanSummary(),
            Coverages = GetCoverages(limit: 50000),
            AdoptionPreviews = GetAdoptionPreviews(limit: 50000),
            Rehearsal = rehearsal.GetLatestSummary(),
            RehearsalItems = rehearsal.GetLatestItems(limit: 50000),
            ConflictForensics = rehearsal.GetLatestConflictForensics(limit: 50000)
        };
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private long CreateRun(DateTimeOffset startedAt)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO library_truth_runs(started_at,status,parser_version,message)
            VALUES($started,'running',$parser,'Reading the live file inventory…');
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$started", startedAt.ToString("O"));
        command.Parameters.AddWithValue("$parser", LibraryTruthParser.CurrentVersion);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private void MarkRun(long runId, string status, string message)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE library_truth_runs SET status=$status,completed_at=$completed,message=$message WHERE id=$id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$id", runId);
        command.ExecuteNonQuery();
    }

    private IReadOnlyList<LibraryTruthFileInput> ReadSourceFiles(CancellationToken cancellationToken)
    {
        var folders = ReadLibraryRoots();
        var result = new List<LibraryTruthFileInput>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mf.id,mf.episode_id,mf.path,mf.original_filename,mf.file_size,COALESCE(mf.duration_ms,0),
                   COALESCE(mf.partial_hash,''),COALESCE(mf.full_hash,''),COALESCE(mf.storage_state,''),
                   COALESCE(mf.is_preferred,0),c.name,e.air_date,COALESCE(e.broadcast_slot,''),
                   COALESCE(e.part_number,1),e.total_parts,COALESCE(e.title,''),COALESCE(e.broadcast_uid,'')
              FROM media_files mf
              JOIN episodes e ON e.id=mf.episode_id
              JOIN collections c ON c.id=e.collection_id
             WHERE COALESCE(mf.is_missing,0)=0 AND COALESCE(e.hidden,0)=0
             ORDER BY mf.path
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = reader.GetString(2);
            var folder = MatchRoot(path, folders);
            result.Add(new LibraryTruthFileInput
            {
                MediaFileId = reader.GetInt64(0),
                CurrentEpisodeId = reader.GetInt64(1),
                Path = path,
                OriginalFilename = reader.GetString(3),
                FileSize = reader.GetInt64(4),
                DurationMs = reader.GetInt64(5),
                PartialHash = reader.GetString(6),
                FullHash = reader.GetString(7),
                StorageState = reader.GetString(8),
                IsPreferred = reader.GetInt64(9) != 0,
                CurrentCollectionName = reader.GetString(10),
                CurrentAirDate = reader.IsDBNull(11) ? null : ParseDate(reader.GetString(11)),
                CurrentBroadcastSlot = reader.GetString(12),
                CurrentPartNumber = reader.GetInt32(13),
                CurrentTotalParts = reader.IsDBNull(14) ? null : reader.GetInt32(14),
                CurrentTitle = reader.GetString(15),
                CurrentBroadcastUid = reader.GetString(16),
                LibraryRoot = folder?.Path ?? string.Empty,
                AssignedCollectionName = folder?.AssignedCollectionName ?? string.Empty
            });
        }
        return result;
    }

    private IReadOnlyList<LibraryRoot> ReadLibraryRoots()
    {
        var result = new List<LibraryRoot>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT lf.path,COALESCE(c.name,'')
              FROM library_folders lf
              LEFT JOIN collections c ON c.id=lf.assigned_collection_id
             WHERE lf.enabled=1
             ORDER BY length(lf.path) DESC
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new LibraryRoot(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private static LibraryRoot? MatchRoot(string path, IReadOnlyList<LibraryRoot> roots)
    {
        foreach (var root in roots)
        {
            var normalized = root.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (path.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(normalized + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return root;
        }
        return null;
    }

    private static GroupAnalysis AnalyseGroups(IReadOnlyList<LibraryTruthInterpretation> interpretations)
    {
        var conflicts = BuildCrossIdentityConflicts(interpretations);
        var conflictMediaIds = conflicts.SelectMany(x => x.MediaFileIds).ToHashSet();
        var splitEpisodes = interpretations
            .GroupBy(x => x.Input.CurrentEpisodeId)
            .Where(x => x.Select(y => y.CanonicalBroadcastKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(x => x.Key)
            .ToHashSet();

        var groups = new List<BroadcastAnalysis>();
        var effectivePartAssignments = new Dictionary<long, EffectivePartAssignment>();
        foreach (var group in interpretations.GroupBy(x => x.CanonicalBroadcastKey, StringComparer.OrdinalIgnoreCase))
        {
            var files = group.ToArray();
            var exactGroups = files.Where(x => !string.IsNullOrWhiteSpace(x.Input.FullHash))
                .GroupBy(x => x.Input.FullHash, StringComparer.OrdinalIgnoreCase)
                .Where(x => x.Count() > 1).ToArray();
            var strongGroups = files.Where(x => !string.IsNullOrWhiteSpace(x.Input.PartialHash) && x.Input.DurationMs > 0)
                .GroupBy(StrongAudioIdentityKey, StringComparer.OrdinalIgnoreCase)
                .Where(x => x.Count() > 1 &&
                    x.Any(y => string.IsNullOrWhiteSpace(y.Input.FullHash)) &&
                    x.Select(y => y.Input.FullHash)
                        .Where(y => !string.IsNullOrWhiteSpace(y))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count() <= 1)
                .ToArray();
            var recordings = BuildRecordingFamilies(group.Key, files, effectivePartAssignments);
            var segmentCount = recordings.Where(x => x.IsMultipart).Select(x => x.SegmentCount).DefaultIfEmpty(1).Max();
            var currentEpisodeCount = files.Select(x => x.Input.CurrentEpisodeId).Distinct().Count();
            var exactDuplicateCount = exactGroups.Sum(x => x.Count() - 1);
            var strongDuplicateCount = strongGroups.Sum(x => x.Count() - 1);
            var broadcastConflicts = conflicts.Where(x => x.CanonicalKeys.Contains(group.Key)).ToArray();
            var hasSplit = files.Any(x => splitEpisodes.Contains(x.Input.CurrentEpisodeId));
            var parserNeedsReview = files.Any(x => x.NeedsReview);
            var unknownDate = files.Any(x => !x.AirDate.HasValue);
            var suspiciousAssessment = DetectSuspiciousMerge(recordings);
            var suspiciousMerge = suspiciousAssessment.IsSuspicious;
            var incompleteMultipartWithoutCompleteAlternative = recordings.Any(x => x.Role is "Incomplete multipart recording" or "Multipart recording with unknown extent") &&
                !recordings.Any(x => x.Role is "Complete recording" or "Complete alternate recording" or "Complete multipart recording");
            var durationSpread = DurationSpread(recordings);
            var preferred = recordings.OrderByDescending(x => x.IsPreferredCandidate).ThenByDescending(x => x.PreferredScore).FirstOrDefault();

            string adoptionState;
            string adoptionReason;
            if (parserNeedsReview || unknownDate)
            {
                adoptionState = "Blocked";
                adoptionReason = unknownDate
                    ? "A trustworthy broadcast date is still missing."
                    : "Parser V3 retained evidence that requires a human identity decision.";
            }
            else if (broadcastConflicts.Length > 0)
            {
                adoptionState = "Blocked";
                adoptionReason = "The same audio evidence appears under a different broadcast identity.";
            }
            else if (hasSplit)
            {
                adoptionState = "Blocked";
                adoptionReason = "One live episode currently contains files that Parser V3 assigns to different broadcasts.";
            }
            else if (suspiciousMerge)
            {
                adoptionState = "Review recommended";
                adoptionReason = string.IsNullOrWhiteSpace(suspiciousAssessment.Reason)
                    ? "The proposed broadcast contains more than one substantial recording family."
                    : suspiciousAssessment.Reason;
            }
            else if (incompleteMultipartWithoutCompleteAlternative)
            {
                adoptionState = "Review recommended";
                adoptionReason = "The multipart structure is incomplete or has an unknown extent and no complete standalone capture is available.";
            }
            else if (recordings.Count > 1)
            {
                adoptionState = "Ready with recording choice";
                adoptionReason = "The broadcast identity is safe; Radio Vault has ranked several preserved recording variants.";
            }
            else
            {
                adoptionState = "Ready";
                adoptionReason = "The broadcast identity and recording structure are internally consistent.";
            }

            var status = adoptionState == "Blocked" ? "Needs attention"
                : currentEpisodeCount > 1 || files.Any(x => x.HasMeaningfulChange) || adoptionState == "Review recommended"
                    ? "Proposed changes"
                    : "Stable";
            var confidence = files.Length == 0 ? 0 : (int)Math.Round(files.Average(x => x.ConfidenceScore));
            var evidence = BuildBroadcastEvidence(currentEpisodeCount, files, recordings, exactDuplicateCount,
                strongDuplicateCount, preferred, suspiciousAssessment, broadcastConflicts.Length, durationSpread);
            groups.Add(new BroadcastAnalysis(group.Key, files, segmentCount, recordings, exactDuplicateCount,
                strongDuplicateCount, currentEpisodeCount, adoptionState is "Blocked" or "Review recommended", status,
                confidence, evidence, adoptionState, adoptionReason, preferred?.RecordingKey ?? string.Empty,
                suspiciousMerge, durationSpread, broadcastConflicts.Length));
        }

        var coverageAudit = ApplySameDateCoverageAudit(groups);
        var directCoverages = BuildDirectCoverageEvidence(coverageAudit.Broadcasts, effectivePartAssignments);
        var coverages = directCoverages.Concat(coverageAudit.InferredCoverages)
            .OrderBy(x => x.SourceBroadcastKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.RecordingKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SegmentNumber)
            .ThenBy(x => x.TargetBroadcastKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var adoptionPreviews = BuildAdoptionPreviews(coverageAudit.Broadcasts, coverages);
        var years = BuildYearAnalyses(interpretations, coverageAudit.Broadcasts, splitEpisodes);
        return new GroupAnalysis(coverageAudit.Broadcasts, splitEpisodes, conflictMediaIds, conflicts, years,
            effectivePartAssignments, coverages, adoptionPreviews);
    }

    private static IReadOnlyList<ConflictAnalysis> BuildCrossIdentityConflicts(
        IReadOnlyList<LibraryTruthInterpretation> interpretations)
    {
        var result = new List<ConflictAnalysis>();
        var exactMedia = new HashSet<long>();
        foreach (var group in interpretations
                     .Where(x => !string.IsNullOrWhiteSpace(x.Input.FullHash))
                     .GroupBy(x => x.Input.FullHash, StringComparer.OrdinalIgnoreCase))
        {
            var identities = group.Select(x => x.CanonicalBroadcastKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (identities.Length <= 1) continue;
            var files = group.ToArray();
            foreach (var file in files) exactMedia.Add(file.Input.MediaFileId);
            result.Add(new ConflictAnalysis(
                "Exact audio, conflicting broadcast identities", 100, files.Length, identities.Length,
                identities, "A complete SHA-256 file hash is identical even though the filenames or folders claim different broadcasts.",
                files.Select(x => x.Input.MediaFileId).ToHashSet(), identities.ToHashSet(StringComparer.OrdinalIgnoreCase)));
        }

        foreach (var group in interpretations
                     .Where(x => !string.IsNullOrWhiteSpace(x.Input.PartialHash) && x.Input.DurationMs > 0 && x.Input.FileSize > 0)
                     .GroupBy(StrongAudioIdentityKey, StringComparer.OrdinalIgnoreCase))
        {
            var files = group.Where(x => !exactMedia.Contains(x.Input.MediaFileId)).ToArray();
            var identities = files.Select(x => x.CanonicalBroadcastKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length < 2 || identities.Length <= 1) continue;
            result.Add(new ConflictAnalysis(
                "Strong audio match, conflicting broadcast identities", 95, files.Length, identities.Length,
                identities, "The partial audio fingerprint, file size and measured duration are identical across conflicting date/show/slot claims. A full hash or listening review should decide the correct relationship.",
                files.Select(x => x.Input.MediaFileId).ToHashSet(), identities.ToHashSet(StringComparer.OrdinalIgnoreCase)));
        }
        return result;
    }

    private static IReadOnlyList<RecordingAnalysis> BuildRecordingFamilies(
        string canonicalBroadcastKey,
        IReadOnlyList<LibraryTruthInterpretation> files,
        IDictionary<long, EffectivePartAssignment> effectivePartAssignments)
    {
        if (files.Count == 0) return Array.Empty<RecordingAnalysis>();

        var planned = files.Select(file =>
        {
            var explicitMultipart = IsExplicitMultipart(file);
            var structure = LibraryTruthRecordingStructure.Analyse(file.Input.OriginalFilename, explicitMultipart);
            return new PlannedFile(file, structure, explicitMultipart);
        }).ToArray();

        var promotableBareNumberFamilies = planned
            .Where(x => !x.ExplicitMultipart && x.Structure.AmbiguousTrailingNumber.HasValue)
            .GroupBy(x => x.Structure.FamilyKey, StringComparer.OrdinalIgnoreCase)
            .Where(group =>
            {
                var numbers = group.Select(x => x.Structure.AmbiguousTrailingNumber!.Value)
                    .Distinct().OrderBy(x => x).ToArray();
                return numbers.Length >= 2 && numbers[0] == 1 &&
                       numbers.SequenceEqual(Enumerable.Range(1, numbers[^1]));
            })
            .Select(x => x.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var seeds = new List<RecordingSeed>();
        var multipart = planned.Where(x => x.ExplicitMultipart ||
                                           (x.Structure.AmbiguousTrailingNumber.HasValue &&
                                            promotableBareNumberFamilies.Contains(x.Structure.FamilyKey)))
            .ToArray();
        var standalone = planned.Except(multipart).ToArray();

        foreach (var cluster in standalone.GroupBy(x => AudioIdentityKey(x.File), StringComparer.OrdinalIgnoreCase)
                     .Select(x => new PlannedAudioCluster(x.Key, OrderCluster(x.Select(y => y.File)),
                         x.SelectMany(y => y.Structure.ProgrammeTokens).ToHashSet(StringComparer.OrdinalIgnoreCase),
                         x.Select(y => y.Structure.FamilyKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                         x.Any(y => y.Structure.NumericTokenKind == LibraryTruthNumericTokenKind.AmbiguousTrailingNumber))))
        {
            var duration = cluster.Files.Max(x => Math.Max(0, x.Input.DurationMs));
            var evidence = cluster.HasAmbiguousTrailingNumber
                ? "One unnumbered audio identity is preserved as a standalone recording variant. A lone trailing number was retained as track/version ambiguity rather than being promoted to multipart structure."
                : "One unnumbered audio identity is preserved as a separate recording variant.";
            seeds.Add(new RecordingSeed(cluster.Files, 1, duration, false, true, 1,
                "Standalone capture", evidence, string.Join(" + ", cluster.FamilyKeys), cluster.ProgrammeTokens, false));
        }

        foreach (var family in multipart.GroupBy(x => x.Structure.FamilyKey, StringComparer.OrdinalIgnoreCase))
        {
            var familyFiles = family.ToArray();
            var promotedBareSequence = familyFiles.Any(x => !x.ExplicitMultipart && x.Structure.AmbiguousTrailingNumber.HasValue);
            var clustersByPart = familyFiles
                .GroupBy(EffectivePartNumber)
                .OrderBy(x => x.Key)
                .ToDictionary(
                    part => part.Key,
                    part => part.GroupBy(x => AudioIdentityKey(x.File), StringComparer.OrdinalIgnoreCase)
                        .Select(cluster => new PlannedAudioCluster(
                            cluster.Key,
                            OrderCluster(cluster.Select(x => x.File)),
                            cluster.SelectMany(x => x.Structure.ProgrammeTokens).ToHashSet(StringComparer.OrdinalIgnoreCase),
                            cluster.Select(x => x.Structure.FamilyKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                            cluster.Any(x => x.Structure.NumericTokenKind == LibraryTruthNumericTokenKind.AmbiguousTrailingNumber)))
                        .OrderByDescending(cluster => cluster.Files.Any(x => x.Input.IsPreferred))
                        .ThenByDescending(cluster => cluster.Files.Max(x => x.Input.DurationMs))
                        .ThenByDescending(cluster => cluster.Files.Max(x => x.Input.FileSize))
                        .ThenBy(cluster => cluster.AudioKey, StringComparer.OrdinalIgnoreCase)
                        .ToArray());

            var recordingCount = Math.Max(1, clustersByPart.Values.Max(x => x.Length));
            for (var rank = 0; rank < recordingCount; rank++)
            {
                var selected = clustersByPart
                    .Where(x => rank < x.Value.Length)
                    .Select(x => (Part: x.Key, Cluster: x.Value[rank]))
                    .ToArray();
                if (selected.Length == 0) continue;

                var selectedFiles = selected.SelectMany(x => x.Cluster.Files).ToArray();
                var declaredTotals = selectedFiles.Where(x => x.TotalParts.HasValue)
                    .Select(x => x.TotalParts!.Value).Where(x => x > 0).Distinct().ToArray();
                var availableParts = selected.Select(x => x.Part).Distinct().OrderBy(x => x).ToArray();
                int? expectedParts = declaredTotals.Length == 1
                    ? declaredTotals[0]
                    : availableParts.Length >= 2 || availableParts[0] > 1
                        ? availableParts[^1]
                        : null;
                var complete = expectedParts.HasValue &&
                               Enumerable.Range(1, expectedParts.Value).All(availableParts.Contains);
                var duration = selected.Sum(x => x.Cluster.Files.Max(file => Math.Max(0, file.Input.DurationMs)));
                var evidence = expectedParts switch
                {
                    null => "Multipart evidence identifies one numbered segment, but the expected set size is unknown.",
                    _ when complete => $"Multipart assembly contains every numbered segment from 1 through {expectedParts.Value}.",
                    _ => $"Multipart assembly contains {availableParts.Length:N0} of {expectedParts.Value:N0} expected numbered segments."
                };
                if (promotedBareSequence)
                {
                    evidence += " Bare trailing numbers were promoted only because sibling filenames formed a contiguous 1..N sequence within this exact filename family.";
                    foreach (var selectedPart in selected)
                    {
                        foreach (var selectedFile in selectedPart.Cluster.Files)
                        {
                            effectivePartAssignments[selectedFile.Input.MediaFileId] = new EffectivePartAssignment(
                                selectedPart.Part,
                                expectedParts,
                                true);
                        }
                    }
                }
                evidence += $" Filename-family lineage: {family.Key}.";

                seeds.Add(new RecordingSeed(
                    selectedFiles,
                    availableParts.Length,
                    duration,
                    true,
                    complete,
                    expectedParts,
                    promotedBareSequence ? "Context-confirmed multipart assembly" : "Multipart assembly",
                    evidence,
                    family.Key,
                    selected.SelectMany(x => x.Cluster.ProgrammeTokens).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    promotedBareSequence));
            }
        }

        var longestDuration = seeds.Where(x => x.DurationMs > 0).Select(x => x.DurationMs).DefaultIfEmpty(0).Max();
        var analyses = new List<RecordingAnalysis>(seeds.Count);
        foreach (var seed in seeds)
        {
            var ratio = longestDuration <= 0 || seed.DurationMs <= 0 ? 0d : seed.DurationMs / (double)longestDuration;
            var role = ClassifyRecordingRole(seed, ratio, longestDuration, seeds.Count);
            var completeness = CompletenessScore(role, seed.CompleteMultipart);
            var confidence = RecordingConfidence(seed.Files);
            var preferredScore = PreferredRecordingScore(seed, role, completeness, confidence);
            var reviewReason = RecordingReviewReason(role, seed, ratio);
            analyses.Add(new RecordingAnalysis(
                string.Empty, string.Empty, seed.Files.Length, seed.SegmentCount, seed.DurationMs,
                seed.Relationship, confidence, string.Empty, seed.Files.Select(x => x.Input.MediaFileId).ToHashSet(),
                role, completeness, preferredScore, ratio, false, reviewReason, seed.IsMultipart, seed.CompleteMultipart,
                seed.ExpectedParts, seed.Evidence, seed.FamilyKey, seed.ProgrammeTokens, seed.PromotedBareNumberSequence));
        }

        var preferredIndex = analyses.Count == 0 ? -1 : analyses
            .Select((recording, index) => (recording, index))
            .OrderByDescending(x => x.recording.PreferredScore)
            .ThenByDescending(x => x.recording.DurationMs)
            .ThenByDescending(x => x.recording.ConfidenceScore)
            .Select(x => x.index).First();
        var final = new List<RecordingAnalysis>(analyses.Count);
        for (var index = 0; index < analyses.Count; index++)
        {
            var recording = analyses[index];
            var preferred = index == preferredIndex;
            var key = $"{canonicalBroadcastKey}|R{index + 1}";
            var label = preferred ? $"Preferred · {recording.Role}" : recording.Role;
            var evidence = BuildRecordingEvidence(recording, preferred, analyses.Count, longestDuration);
            final.Add(recording with
            {
                RecordingKey = key,
                Label = label,
                IsPreferredCandidate = preferred,
                Evidence = evidence
            });
        }
        return final.OrderByDescending(x => x.IsPreferredCandidate).ThenByDescending(x => x.PreferredScore).ToArray();
    }

    private static int EffectivePartNumber(PlannedFile file)
        => file.ExplicitMultipart
            ? Math.Max(1, file.File.PartNumber)
            : Math.Max(1, file.Structure.AmbiguousTrailingNumber ?? 1);

    private static LibraryTruthInterpretation[] OrderCluster(IEnumerable<LibraryTruthInterpretation> cluster)
        => cluster.OrderByDescending(x => x.Input.IsPreferred)
            .ThenByDescending(x => x.Input.DurationMs)
            .ThenByDescending(x => x.Input.FileSize)
            .ThenBy(x => x.Input.MediaFileId)
            .ToArray();

    private static bool IsExplicitMultipart(LibraryTruthInterpretation file)
        => file.PartNumber > 1 || file.TotalParts.HasValue || !string.IsNullOrWhiteSpace(file.MultipartKind);

    private static string AudioIdentityKey(LibraryTruthInterpretation file)
    {
        if (!string.IsNullOrWhiteSpace(file.Input.FullHash))
            return "FULL:" + file.Input.FullHash;
        if (!string.IsNullOrWhiteSpace(file.Input.PartialHash) && file.Input.DurationMs > 0)
            return "PARTIAL:" + StrongAudioIdentityKey(file);
        return $"FILE:{file.Input.MediaFileId}";
    }

    private static string StrongAudioIdentityKey(LibraryTruthInterpretation file)
        => $"{file.Input.PartialHash}:{file.Input.FileSize}:{file.Input.DurationMs}";

    private static int RecordingConfidence(IReadOnlyList<LibraryTruthInterpretation> files)
    {
        if (files.Count == 0) return 0;
        if (files.All(x => !string.IsNullOrWhiteSpace(x.Input.FullHash))) return 96;
        if (files.All(x => !string.IsNullOrWhiteSpace(x.Input.PartialHash) && x.Input.DurationMs > 0)) return 82;
        if (files.All(x => x.Input.DurationMs > 0)) return 68;
        return 50;
    }

    private static string ClassifyRecordingRole(RecordingSeed seed, double ratio, long longestDuration, int recordingCount)
    {
        if (seed.DurationMs <= 0) return seed.IsMultipart ? "Incomplete multipart recording" : "Recording with unknown duration";
        if (longestDuration >= TimeSpan.FromMinutes(30).TotalMilliseconds && seed.DurationMs <= TimeSpan.FromMinutes(1).TotalMilliseconds)
            return "Likely truncated or damaged";
        if (longestDuration >= TimeSpan.FromMinutes(45).TotalMilliseconds && seed.DurationMs <= TimeSpan.FromMinutes(5).TotalMilliseconds)
            return "Short fragment or clip";
        if (seed.IsMultipart)
        {
            if (!seed.ExpectedParts.HasValue) return "Multipart recording with unknown extent";
            if (!seed.CompleteMultipart) return "Incomplete multipart recording";
            if (ratio >= 0.85 || longestDuration == seed.DurationMs) return "Complete multipart recording";
            if (ratio >= 0.70) return "Likely complete multipart recording";
            return "Partial multipart recording";
        }
        if (recordingCount == 1) return "Complete recording";
        if (ratio >= 0.92) return "Complete alternate recording";
        if (ratio >= 0.70) return "Likely complete recording";
        return "Partial recording";
    }

    private static int CompletenessScore(string role, bool completeMultipart) => role switch
    {
        "Complete recording" => 100,
        "Complete alternate recording" => 96,
        "Complete multipart recording" when completeMultipart => 98,
        "Likely complete multipart recording" => 84,
        "Likely complete recording" => 82,
        "Partial multipart recording" => 58,
        "Partial recording" => 55,
        "Incomplete multipart recording" => 45,
        "Multipart recording with unknown extent" => 40,
        "Short fragment or clip" => 20,
        "Likely truncated or damaged" => 5,
        _ => 35
    };

    private static int PreferredRecordingScore(RecordingSeed seed, string role, int completeness, int confidence)
    {
        var score = completeness * 0.68 + confidence * 0.22;
        if (seed.Files.Any(x => x.Input.IsPreferred)) score += 6;
        if (seed.Files.All(x => !string.IsNullOrWhiteSpace(x.Input.FullHash))) score += 4;
        if (role is "Likely truncated or damaged" or "Short fragment or clip") score -= 20;
        if (role == "Incomplete multipart recording") score -= 8;
        if (role == "Multipart recording with unknown extent") score -= 10;
        return Math.Clamp((int)Math.Round(score), 0, 100);
    }

    private static string RecordingReviewReason(string role, RecordingSeed seed, double ratio)
        => role switch
        {
            "Likely truncated or damaged" => "The measured duration is tiny compared with another recording of this broadcast; preserve it as evidence but do not prefer it for playback.",
            "Short fragment or clip" => "This appears to be a short excerpt rather than a complete show.",
            "Partial recording" => $"This recording covers about {ratio:P0} of the longest known capture.",
            "Partial multipart recording" => $"Every labelled segment is present, but the assembled set covers only about {ratio:P0} of the longest standalone capture.",
            "Likely complete multipart recording" => $"The numbered set is complete and covers about {ratio:P0} of the longest standalone capture.",
            "Incomplete multipart recording" => $"Only {seed.SegmentCount:N0} of {seed.ExpectedParts!.Value:N0} expected segments were found.",
            "Multipart recording with unknown extent" => "A numbered segment exists, but no sibling or x-of-y marker establishes how many segments should exist.",
            _ => string.Empty
        };

    private static string BuildRecordingEvidence(RecordingAnalysis recording, bool preferred, int recordingCount, long longestDuration)
    {
        var copies = recording.FileCount;
        var duration = recording.DurationMs <= 0 ? "unknown duration" : TimeSpan.FromMilliseconds(recording.DurationMs).ToString(@"h\:mm\:ss");
        var evidence = $"{recording.SeedEvidence} This family contains {copies:N0} physical file location(s) and runs {duration}.";
        if (longestDuration > 0 && recording.DurationMs > 0)
            evidence += $" It covers {recording.DurationRatio:P0} of the longest recording grouped beneath this broadcast.";
        if (preferred)
            evidence += $" Radio Vault ranks it first for later adoption with a preferred score of {recording.PreferredScore}%.";
        else if (recordingCount > 1)
            evidence += " It remains preserved as an alternate, partial or fragment recording rather than being discarded.";
        if (!string.IsNullOrWhiteSpace(recording.ReviewReason)) evidence += " " + recording.ReviewReason;
        return evidence;
    }

    private static SuspiciousMergeAssessment DetectSuspiciousMerge(
        IReadOnlyList<RecordingAnalysis> recordings)
    {
        var substantial = recordings
            .Where(x => !x.IsMultipart && x.DurationMs >= TimeSpan.FromMinutes(90).TotalMilliseconds)
            .OrderBy(x => x.DurationMs)
            .ToArray();
        if (substantial.Length < 2) return new SuspiciousMergeAssessment(false, string.Empty);

        var shortestToLongest = substantial[0].DurationMs / (double)Math.Max(1, substantial[^1].DurationMs);
        if (shortestToLongest <= 0.40)
        {
            return new SuspiciousMergeAssessment(true,
                "Two substantial standalone captures differ by more than 2.5× in duration and may represent separate programme occurrences.");
        }

        var clusters = BuildDurationClusters(substantial);
        if (clusters.Count < 2) return new SuspiciousMergeAssessment(false, string.Empty);

        var repeated = clusters.Where(x => x.Recordings.Count >= 2).ToArray();
        if (repeated.Length >= 2)
        {
            var shortest = repeated.Min(x => x.MedianDurationMs);
            var longest = repeated.Max(x => x.MedianDurationMs);
            var spread = longest / (double)Math.Max(1, shortest);
            if (spread >= 1.25)
            {
                var tokenCluster = repeated.FirstOrDefault(x => x.ProgrammeTokens.Count > 0);
                var tokenEvidence = tokenCluster is null
                    ? string.Empty
                    : $" The shorter/alternate family carries programme-specific filename evidence ({string.Join(", ", tokenCluster.ProgrammeTokens.OrderBy(x => x))}).";
                return new SuspiciousMergeAssessment(true,
                    $"Two repeated duration families differ by {spread:0.00}×, which is more consistent with separate programme material than alternate encodes.{tokenEvidence}");
            }
        }

        foreach (var cluster in clusters.Where(x => x.ProgrammeTokens.Count > 0))
        {
            var othersShareToken = clusters.Where(x => !ReferenceEquals(x, cluster))
                .Any(x => x.ProgrammeTokens.Overlaps(cluster.ProgrammeTokens));
            if (!othersShareToken && cluster.Recordings.Count >= 2)
            {
                return new SuspiciousMergeAssessment(true,
                    $"A distinct repeated duration family carries programme-specific filename evidence ({string.Join(", ", cluster.ProgrammeTokens.OrderBy(x => x))}) not shared by the other recordings.");
            }
        }

        return new SuspiciousMergeAssessment(false, string.Empty);
    }

    private static IReadOnlyList<RecordingDurationCluster> BuildDurationClusters(
        IReadOnlyList<RecordingAnalysis> recordings)
    {
        var clusters = new List<List<RecordingAnalysis>>();
        foreach (var recording in recordings.OrderBy(x => x.DurationMs))
        {
            var target = clusters.LastOrDefault();
            var median = target is null ? 0 : MedianDuration(target.Select(x => x.DurationMs));
            if (target is null || !DurationsApproximatelyEqual(median, recording.DurationMs, 90_000, 0.015))
            {
                target = new List<RecordingAnalysis>();
                clusters.Add(target);
            }
            target.Add(recording);
        }

        return clusters.Select(items => new RecordingDurationCluster(
            items,
            MedianDuration(items.Select(x => x.DurationMs)),
            items.SelectMany(x => x.ProgrammeTokens).ToHashSet(StringComparer.OrdinalIgnoreCase))).ToArray();
    }

    private static long MedianDuration(IEnumerable<long> values)
    {
        var ordered = values.OrderBy(x => x).ToArray();
        if (ordered.Length == 0) return 0;
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2;
    }

    private static bool DurationsApproximatelyEqual(long left, long right, long absoluteToleranceMs, double relativeTolerance)
    {
        if (left <= 0 || right <= 0) return false;
        var difference = Math.Abs(left - right);
        var relative = difference / (double)Math.Max(left, right);
        return difference <= absoluteToleranceMs || relative <= relativeTolerance;
    }

    private static double DurationSpread(IReadOnlyList<RecordingAnalysis> recordings)
    {
        var durations = recordings.Where(x => x.DurationMs > 0).Select(x => x.DurationMs).ToArray();
        if (durations.Length < 2) return 1d;
        return durations.Max() / (double)Math.Max(1, durations.Min());
    }

    private static string BuildBroadcastEvidence(
        int currentEpisodeCount,
        IReadOnlyList<LibraryTruthInterpretation> files,
        IReadOnlyList<RecordingAnalysis> recordings,
        int exactDuplicateCount,
        int strongDuplicateCount,
        RecordingAnalysis? preferred,
        SuspiciousMergeAssessment suspiciousMerge,
        int conflictCount,
        double durationSpread)
    {
        var roles = recordings.GroupBy(x => x.Role).OrderByDescending(x => x.Count())
            .Select(x => $"{x.Count():N0} {x.Key.ToLowerInvariant()}");
        var evidence = $"All {files.Count:N0} physical files resolve to the same canonical show/date/slot. " +
                       $"They currently occupy {currentEpisodeCount:N0} live episode record(s) and form {recordings.Count:N0} recording variant(s): {string.Join(", ", roles)}.";
        if (exactDuplicateCount > 0) evidence += $" {exactDuplicateCount:N0} redundant physical location(s) are confirmed by full hash.";
        if (strongDuplicateCount > 0) evidence += $" {strongDuplicateCount:N0} additional copy candidate(s) share partial fingerprint, size and duration.";
        if (preferred is not null) evidence += $" Preferred recording: {preferred.RecordingKey.Split('|').Last()} ({preferred.Role}, score {preferred.PreferredScore}%).";
        if (durationSpread > 1.01) evidence += $" Recording durations span {durationSpread:0.00}×; shorter variants are classified rather than silently treated as equivalent.";
        if (suspiciousMerge.IsSuspicious) evidence += $" Merge audit: {suspiciousMerge.Reason}";
        if (conflictCount > 0) evidence += $" {conflictCount:N0} cross-identity audio conflict(s) block automatic adoption.";
        return evidence;
    }

    private static IReadOnlyList<SegmentCoverageAnalysis> BuildDirectCoverageEvidence(
        IReadOnlyList<BroadcastAnalysis> broadcasts,
        IReadOnlyDictionary<long, EffectivePartAssignment> effectivePartAssignments)
    {
        var result = new List<SegmentCoverageAnalysis>();
        foreach (var broadcast in broadcasts)
        {
            var filesByMediaId = broadcast.Files.ToDictionary(x => x.Input.MediaFileId);
            foreach (var recording in broadcast.Recordings)
            {
                var recordingFiles = recording.MediaFileIds
                    .Where(filesByMediaId.ContainsKey)
                    .Select(mediaId => filesByMediaId[mediaId])
                    .ToArray();
                if (recordingFiles.Length == 0) continue;

                var segmentGroups = recordingFiles
                    .GroupBy(file =>
                    {
                        if (effectivePartAssignments.TryGetValue(file.Input.MediaFileId, out var effective))
                            return Math.Max(1, effective.PartNumber);
                        return recording.IsMultipart ? Math.Max(1, file.PartNumber) : 1;
                    })
                    .OrderBy(x => x.Key)
                    .ToArray();
                var segmentTotal = recording.IsMultipart
                    ? recording.ExpectedParts
                    : 1;
                var offset = 0L;
                foreach (var segment in segmentGroups)
                {
                    var mediaIds = segment.Select(x => x.Input.MediaFileId).ToHashSet();
                    var duration = segment.Max(x => Math.Max(0, x.Input.DurationMs));
                    var end = duration > 0 ? offset + duration : offset;
                    var kind = recording.IsMultipart ? "Direct multipart segment" : "Direct recording";
                    var totalText = segmentTotal.HasValue ? $" of {segmentTotal.Value}" : string.Empty;
                    var evidence = $"Recording {recording.RecordingKey.Split('|').Last()} directly belongs to {broadcast.CanonicalKey}. " +
                                   $"Segment {segment.Key}{totalText} is supported by {mediaIds.Count:N0} physical file location(s).";
                    result.Add(new SegmentCoverageAnalysis(
                        broadcast.CanonicalKey,
                        recording.RecordingKey,
                        segment.Key,
                        segmentTotal,
                        broadcast.CanonicalKey,
                        kind,
                        offset,
                        end,
                        recording.ConfidenceScore,
                        false,
                        mediaIds,
                        evidence));
                    offset = end;
                }
            }
        }
        return result;
    }

    private static CoverageAuditResult ApplySameDateCoverageAudit(
        IReadOnlyList<BroadcastAnalysis> broadcasts)
    {
        var updated = broadcasts.ToDictionary(x => x.CanonicalKey, StringComparer.OrdinalIgnoreCase);
        var inferredCoverages = new List<SegmentCoverageAnalysis>();
        var dated = broadcasts.Where(x => x.Files.FirstOrDefault()?.AirDate is not null)
            .GroupBy(x => new
            {
                Collection = x.Files[0].CollectionName.ToUpperInvariant(),
                Date = x.Files[0].AirDate!.Value
            });

        foreach (var dateGroup in dated)
        {
            var siblings = dateGroup.Where(x => !string.IsNullOrWhiteSpace(x.Files[0].CanonicalSlot)).ToArray();
            if (siblings.Length == 0) continue;

            foreach (var standard in dateGroup.Where(x => string.IsNullOrWhiteSpace(x.Files[0].CanonicalSlot)))
            {
                var siblingDurations = siblings.Select(sibling =>
                {
                    var preferred = sibling.Recordings.FirstOrDefault(x => x.IsPreferredCandidate)
                                    ?? sibling.Recordings.OrderByDescending(x => x.DurationMs).FirstOrDefault();
                    return (Broadcast: sibling, Recording: preferred, Duration: preferred?.DurationMs ?? 0L);
                }).Where(x => x.Duration > 0 && x.Recording is not null)
                  .OrderBy(x => SlotOrder(x.Broadcast.Files[0].CanonicalSlot))
                  .ThenBy(x => x.Broadcast.CanonicalKey, StringComparer.OrdinalIgnoreCase)
                  .ToArray();

                string? riskReason = null;
                List<SegmentCoverageAnalysis>? proposedCoverage = null;
                foreach (var recording in standard.Recordings.Where(x => x.DurationMs > TimeSpan.FromMinutes(30).TotalMilliseconds))
                {
                    var equivalent = siblingDurations.FirstOrDefault(x =>
                        DurationsApproximatelyEqual(recording.DurationMs, x.Duration, 120_000, 0.01));
                    if (equivalent.Broadcast is not null)
                    {
                        riskReason = $"A Standard recording ({TimeSpan.FromMilliseconds(recording.DurationMs):h\\:mm\\:ss}) matches the {DisplaySlot(equivalent.Broadcast)} broadcast on the same date and should be represented as an alternate encode or coverage link rather than a third canonical broadcast.";
                        proposedCoverage = new List<SegmentCoverageAnalysis>
                        {
                            new(
                                standard.CanonicalKey,
                                recording.RecordingKey,
                                1,
                                1,
                                equivalent.Broadcast.CanonicalKey,
                                "Same-date equivalent",
                                0,
                                recording.DurationMs,
                                92,
                                true,
                                recording.MediaFileIds,
                                $"The full Standard recording duration matches the preferred {DisplaySlot(equivalent.Broadcast)} recording within one percent and two minutes. Confirm the relationship before adoption.")
                        };
                        break;
                    }

                    for (var left = 0; left < siblingDurations.Length && riskReason is null; left++)
                    for (var right = left + 1; right < siblingDurations.Length; right++)
                    {
                        var combined = siblingDurations[left].Duration + siblingDurations[right].Duration;
                        if (!DurationsApproximatelyEqual(recording.DurationMs, combined, 120_000, 0.01)) continue;
                        riskReason = $"A Standard recording ({TimeSpan.FromMilliseconds(recording.DurationMs):h\\:mm\\:ss}) matches the combined {DisplaySlot(siblingDurations[left].Broadcast)} and {DisplaySlot(siblingDurations[right].Broadcast)} broadcasts. Model segment-to-broadcast coverage instead of preserving a third canonical Standard broadcast.";
                        proposedCoverage = new List<SegmentCoverageAnalysis>
                        {
                            new(
                                standard.CanonicalKey,
                                recording.RecordingKey,
                                1,
                                2,
                                siblingDurations[left].Broadcast.CanonicalKey,
                                "Composite slot coverage",
                                0,
                                siblingDurations[left].Duration,
                                88,
                                true,
                                recording.MediaFileIds,
                                $"The first inferred range matches the preferred {DisplaySlot(siblingDurations[left].Broadcast)} recording duration. The boundary is duration-derived and requires confirmation."),
                            new(
                                standard.CanonicalKey,
                                recording.RecordingKey,
                                2,
                                2,
                                siblingDurations[right].Broadcast.CanonicalKey,
                                "Composite slot coverage",
                                siblingDurations[left].Duration,
                                combined,
                                88,
                                true,
                                recording.MediaFileIds,
                                $"The second inferred range matches the preferred {DisplaySlot(siblingDurations[right].Broadcast)} recording duration. The boundary is duration-derived and requires confirmation.")
                        };
                        break;
                    }
                    if (riskReason is not null) break;
                }

                if (riskReason is null) continue;
                if (proposedCoverage is not null) inferredCoverages.AddRange(proposedCoverage);
                var state = standard.AdoptionState == "Blocked" ? "Blocked" : "Review recommended";
                var reason = AppendSentence(standard.AdoptionReason, riskReason);
                updated[standard.CanonicalKey] = standard with
                {
                    RequiresStructureReview = true,
                    Status = state == "Blocked" ? "Needs attention" : "Proposed changes",
                    AdoptionState = state,
                    AdoptionReason = reason,
                    Evidence = AppendSentence(standard.Evidence, $"Same-date coverage audit: {riskReason}")
                };
            }
        }

        return new CoverageAuditResult(
            broadcasts.Select(x => updated[x.CanonicalKey]).ToArray(),
            inferredCoverages);
    }

    private static IReadOnlyList<AdoptionPreviewAnalysis> BuildAdoptionPreviews(
        IReadOnlyList<BroadcastAnalysis> broadcasts,
        IReadOnlyList<SegmentCoverageAnalysis> coverages)
    {
        var coverageBySource = coverages
            .GroupBy(x => x.SourceBroadcastKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var result = new List<AdoptionPreviewAnalysis>(broadcasts.Count);
        foreach (var broadcast in broadcasts)
        {
            var episodeGroups = broadcast.Files
                .GroupBy(x => x.Input.CurrentEpisodeId)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key)
                .ToArray();
            long? provisionalEpisodeId = episodeGroups.Length == 0 ? null : episodeGroups[0].Key;
            var currentEpisodeIds = episodeGroups.Select(x => x.Key).OrderBy(x => x).ToArray();
            var reassignFiles = provisionalEpisodeId.HasValue
                ? broadcast.Files.Count(x => x.Input.CurrentEpisodeId != provisionalEpisodeId.Value)
                : broadcast.Files.Length;
            var retireEpisodes = Math.Max(0, currentEpisodeIds.Length - 1);
            var sourceCoverageCount = coverageBySource.TryGetValue(broadcast.CanonicalKey, out var sourceCoverage)
                ? sourceCoverage.Length
                : 0;
            var eligible = broadcast.AdoptionState is "Ready" or "Ready with recording choice";
            var plannedAction = !eligible
                ? broadcast.AdoptionState == "Blocked"
                    ? "Hold until blocking identity evidence is resolved"
                    : "Hold for focused recording or coverage review"
                : currentEpisodeIds.Length > 1
                    ? $"Consolidate {currentEpisodeIds.Length:N0} live episode rows beneath one canonical broadcast"
                    : broadcast.Files.Any(x => x.HasMeaningfulChange)
                        ? "Re-key the existing live episode and attach canonical recording structure"
                        : broadcast.Recordings.Count > 1 || sourceCoverageCount > 1
                            ? "Attach ranked recordings and explicit coverage to the existing broadcast"
                            : "Attach canonical recording structure to the existing broadcast";
            var plannedWriteCount = 1 + broadcast.Files.Length + broadcast.Recordings.Count + sourceCoverageCount + retireEpisodes;
            var guardReason = eligible
                ? "Prepared for guarded adoption only after a validated backup, state preservation, a rollback-verified sealed rehearsal, fresh fingerprints and exact signature checks all pass."
                : broadcast.AdoptionReason;
            var evidence = $"Preview selects {(provisionalEpisodeId.HasValue ? $"live episode {provisionalEpisodeId.Value:N0}" : "no live episode")} as the provisional survivor using the largest physical-file contribution, then the lowest ID as a deterministic tie-break. " +
                           $"It would preserve {broadcast.Files.Length:N0} media-file link(s), create {broadcast.Recordings.Count:N0} recording row(s), persist {sourceCoverageCount:N0} coverage row(s), and consolidate {retireEpisodes:N0} redundant live episode row(s). " +
                           "This is planning evidence only; no live rows are updated, hidden or deleted.";
            result.Add(new AdoptionPreviewAnalysis(
                broadcast.CanonicalKey,
                broadcast.AdoptionState,
                plannedAction,
                provisionalEpisodeId,
                currentEpisodeIds,
                broadcast.Files.Length,
                broadcast.Recordings.Count,
                sourceCoverageCount,
                retireEpisodes,
                reassignFiles,
                plannedWriteCount,
                eligible,
                guardReason,
                evidence));
        }
        return result;
    }

    private static int SlotOrder(string? canonicalSlot)
        => (canonicalSlot ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "MORNING" => 10,
            "MIDDAY" => 20,
            "AFTERNOON" => 30,
            "EVENING" => 40,
            "OPIERADIO" => 50,
            _ => 100
        };

    private static string DisplaySlot(BroadcastAnalysis broadcast)
    {
        var display = broadcast.Files.FirstOrDefault()?.BroadcastSlot;
        return string.IsNullOrWhiteSpace(display) ? "Standard" : display;
    }

    private static string AppendSentence(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first)) return second;
        if (string.IsNullOrWhiteSpace(second)) return first;
        return $"{first.TrimEnd()} {second.Trim()}";
    }

    private static IReadOnlyList<YearAnalysis> BuildYearAnalyses(
        IReadOnlyList<LibraryTruthInterpretation> interpretations,
        IReadOnlyList<BroadcastAnalysis> broadcasts,
        IReadOnlySet<long> splitEpisodeIds)
    {
        var labels = interpretations.Select(x => x.AirDate?.Year.ToString() ?? "Unknown")
            .Concat(interpretations.Select(x => x.Input.CurrentAirDate?.Year.ToString() ?? "Unknown"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var result = new List<YearAnalysis>();
        foreach (var label in labels)
        {
            var proposedFiles = interpretations.Where(x => (x.AirDate?.Year.ToString() ?? "Unknown") == label).ToArray();
            var currentFiles = interpretations.Where(x => (x.Input.CurrentAirDate?.Year.ToString() ?? "Unknown") == label).ToArray();
            var yearBroadcasts = broadcasts.Where(x => (x.Files.FirstOrDefault()?.AirDate?.Year.ToString() ?? "Unknown") == label).ToArray();
            result.Add(new YearAnalysis(label, proposedFiles.Length,
                currentFiles.Select(x => x.Input.CurrentEpisodeId).Distinct().Count(), yearBroadcasts.Length,
                yearBroadcasts.Count(x => x.CurrentEpisodeCount > 1),
                proposedFiles.Where(x => splitEpisodeIds.Contains(x.Input.CurrentEpisodeId)).Select(x => x.Input.CurrentEpisodeId).Distinct().Count(),
                yearBroadcasts.Count(x => x.AdoptionState is "Ready" or "Ready with recording choice"),
                yearBroadcasts.Count(x => x.AdoptionState == "Review recommended"),
                yearBroadcasts.Count(x => x.AdoptionState == "Blocked")));
        }
        return result.OrderBy(x => x.YearLabel == "Unknown").ThenBy(x => x.YearLabel).ToArray();
    }

    private LibraryTruthRunSummary StoreRun(
        long runId,
        DateTimeOffset startedAt,
        IReadOnlyList<LibraryTruthInterpretation> interpretations,
        GroupAnalysis analysis,
        CancellationToken cancellationToken)
    {
        var completedAt = DateTimeOffset.UtcNow;
        var groupByKey = analysis.Broadcasts.ToDictionary(x => x.CanonicalKey, StringComparer.OrdinalIgnoreCase);
        var recordingKeyByMediaFile = analysis.Broadcasts
            .SelectMany(x => x.Recordings)
            .SelectMany(recording => recording.MediaFileIds.Select(mediaId => (MediaFileId: mediaId, RecordingKey: recording.RecordingKey)))
            .GroupBy(x => x.MediaFileId)
            .ToDictionary(group => group.Key, group => group.First().RecordingKey);
        var changed = 0;
        var unchanged = 0;
        var recovered = 0;
        var unknown = 0;
        var needsReview = 0;

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var deleteFiles = connection.CreateCommand())
        {
            deleteFiles.Transaction = transaction;
            deleteFiles.CommandText = "DELETE FROM library_truth_coverages WHERE run_id=$run; DELETE FROM library_truth_adoption_previews WHERE run_id=$run; DELETE FROM library_truth_files WHERE run_id=$run; DELETE FROM library_truth_recordings WHERE run_id=$run; DELETE FROM library_truth_broadcasts WHERE run_id=$run; DELETE FROM library_truth_years WHERE run_id=$run; DELETE FROM library_truth_conflicts WHERE run_id=$run;";
            deleteFiles.Parameters.AddWithValue("$run", runId);
            deleteFiles.ExecuteNonQuery();
        }

        using var insertFile = connection.CreateCommand();
        insertFile.Transaction = transaction;
        insertFile.CommandText = """
            INSERT INTO library_truth_files(
                run_id,media_file_id,current_episode_id,path,original_filename,current_collection,current_air_date,
                current_slot,current_part,current_total_parts,proposed_collection,proposed_air_date,proposed_slot,
                proposed_part,proposed_total_parts,proposed_headline,canonical_broadcast_key,recording_key,confidence_score,
                confidence,disposition,change_summary,evidence_json,warnings_json)
            VALUES($run,$media,$episode,$path,$filename,$currentCollection,$currentDate,$currentSlot,$currentPart,
                   $currentTotal,$proposedCollection,$proposedDate,$proposedSlot,$proposedPart,$proposedTotal,
                   $headline,$key,$recording,$score,$confidence,$disposition,$change,$evidence,$warnings)
            """;
        foreach (var interpretation in interpretations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = groupByKey[interpretation.CanonicalBroadcastKey];
            var disposition = interpretation.Disposition;
            var changeSummary = interpretation.ChangeSummary;
            var warnings = interpretation.Warnings.ToList();
            var evidence = interpretation.Evidence.ToList();
            var proposedPart = interpretation.PartNumber;
            var proposedTotalParts = interpretation.TotalParts;
            if (analysis.EffectivePartAssignments.TryGetValue(interpretation.Input.MediaFileId, out var effectivePart))
            {
                proposedPart = effectivePart.PartNumber;
                proposedTotalParts = effectivePart.TotalParts;
                evidence.Add(new LibraryTruthEvidence(
                    "part",
                    proposedTotalParts.HasValue ? $"{proposedPart} of {proposedTotalParts.Value}" : proposedPart.ToString(),
                    90,
                    "filename-family",
                    "Filename-family sibling evidence formed a contiguous 1..N sequence, so the recording planner promoted the otherwise ambiguous trailing number to an effective multipart position."));

                if (Math.Max(1, interpretation.Input.CurrentPartNumber) != Math.Max(1, proposedPart) ||
                    interpretation.Input.CurrentTotalParts != proposedTotalParts)
                {
                    var segmentChange = $"segment: {PartDisplay(interpretation.Input.CurrentPartNumber, interpretation.Input.CurrentTotalParts)} → {PartDisplay(proposedPart, proposedTotalParts)}";
                    if (disposition == "Unchanged")
                    {
                        disposition = "Multipart correction";
                        changeSummary = segmentChange;
                    }
                    else
                    {
                        changeSummary = Append(changeSummary, segmentChange);
                    }
                }
            }
            var hasConflictingExactIdentity = analysis.ConflictingIdentityMediaIds.Contains(interpretation.Input.MediaFileId);
            if (hasConflictingExactIdentity)
            {
                disposition = "Needs attention";
                changeSummary = Append(changeSummary, "The same exact or strongly matching audio evidence appears under a different proposed broadcast identity.");
                warnings.Add(new LibraryTruthWarning("cross-audio-identity-conflict",
                    "Matching audio evidence carries conflicting show/date/slot claims. Radio Vault has kept the broadcast claims separate and blocked automatic adoption."));
            }
            if (analysis.SplitEpisodeIds.Contains(interpretation.Input.CurrentEpisodeId))
            {
                if (disposition != "Needs attention") disposition = "Broadcast split";
                changeSummary = Append(changeSummary, "One live episode currently contains files that Parser V3 assigns to different canonical broadcasts.");
            }
            else if (group.CurrentEpisodeCount > 1)
            {
                if (disposition != "Needs attention") disposition = "Broadcast merge";
                changeSummary = Append(changeSummary, $"{group.CurrentEpisodeCount} live episode records represent one canonical broadcast family.");
            }
            else if (group.SegmentCount > 1 && disposition == "Unchanged")
            {
                disposition = "Multipart correction";
                changeSummary = "Separate physical segments are assembled beneath one canonical broadcast.";
            }

            if (disposition == "Unchanged") unchanged++; else changed++;
            if (disposition == "Recovered date") recovered++;
            if (!interpretation.AirDate.HasValue) unknown++;
            if (interpretation.NeedsReview || hasConflictingExactIdentity) needsReview++;

            insertFile.Parameters.Clear();
            insertFile.Parameters.AddWithValue("$run", runId);
            insertFile.Parameters.AddWithValue("$media", interpretation.Input.MediaFileId);
            insertFile.Parameters.AddWithValue("$episode", interpretation.Input.CurrentEpisodeId);
            insertFile.Parameters.AddWithValue("$path", interpretation.Input.Path);
            insertFile.Parameters.AddWithValue("$filename", interpretation.Input.OriginalFilename);
            insertFile.Parameters.AddWithValue("$currentCollection", interpretation.Input.CurrentCollectionName);
            insertFile.Parameters.AddWithValue("$currentDate", interpretation.Input.CurrentAirDate?.ToString("yyyy-MM-dd") ?? (object)DBNull.Value);
            insertFile.Parameters.AddWithValue("$currentSlot", interpretation.Input.CurrentBroadcastSlot);
            insertFile.Parameters.AddWithValue("$currentPart", interpretation.Input.CurrentPartNumber);
            insertFile.Parameters.AddWithValue("$currentTotal", interpretation.Input.CurrentTotalParts ?? (object)DBNull.Value);
            insertFile.Parameters.AddWithValue("$proposedCollection", interpretation.CollectionName);
            insertFile.Parameters.AddWithValue("$proposedDate", interpretation.AirDate?.ToString("yyyy-MM-dd") ?? (object)DBNull.Value);
            insertFile.Parameters.AddWithValue("$proposedSlot", interpretation.BroadcastSlot);
            insertFile.Parameters.AddWithValue("$proposedPart", proposedPart);
            insertFile.Parameters.AddWithValue("$proposedTotal", proposedTotalParts ?? (object)DBNull.Value);
            insertFile.Parameters.AddWithValue("$headline", interpretation.Headline);
            insertFile.Parameters.AddWithValue("$key", interpretation.CanonicalBroadcastKey);
            insertFile.Parameters.AddWithValue("$recording", recordingKeyByMediaFile.TryGetValue(interpretation.Input.MediaFileId, out var recordingKey) ? recordingKey : string.Empty);
            insertFile.Parameters.AddWithValue("$score", interpretation.ConfidenceScore);
            insertFile.Parameters.AddWithValue("$confidence", interpretation.Confidence.ToString());
            insertFile.Parameters.AddWithValue("$disposition", disposition);
            insertFile.Parameters.AddWithValue("$change", changeSummary);
            insertFile.Parameters.AddWithValue("$evidence", JsonSerializer.Serialize(evidence, _json));
            insertFile.Parameters.AddWithValue("$warnings", JsonSerializer.Serialize(warnings, _json));
            insertFile.ExecuteNonQuery();
        }

        using var insertBroadcast = connection.CreateCommand();
        insertBroadcast.Transaction = transaction;
        insertBroadcast.CommandText = """
            INSERT INTO library_truth_broadcasts(
                run_id,canonical_key,collection_name,air_date,broadcast_slot,file_count,segment_count,
                recording_count,exact_duplicate_count,strong_duplicate_count,current_episode_count,status,
                confidence_score,evidence_json,adoption_state,adoption_reason,preferred_recording_key,
                suspicious_merge,duration_spread_ratio,cross_identity_conflict_count)
            VALUES($run,$key,$collection,$date,$slot,$files,$segments,$recordings,$exact,$strong,$current,
                   $status,$confidence,$evidence,$adoption,$adoptionReason,$preferred,$suspicious,$spread,$conflicts)
            """;
        foreach (var broadcast in analysis.Broadcasts)
        {
            var exemplar = broadcast.Files[0];
            insertBroadcast.Parameters.Clear();
            insertBroadcast.Parameters.AddWithValue("$run", runId);
            insertBroadcast.Parameters.AddWithValue("$key", broadcast.CanonicalKey);
            insertBroadcast.Parameters.AddWithValue("$collection", exemplar.CollectionName);
            insertBroadcast.Parameters.AddWithValue("$date", exemplar.AirDate?.ToString("yyyy-MM-dd") ?? (object)DBNull.Value);
            insertBroadcast.Parameters.AddWithValue("$slot", exemplar.BroadcastSlot);
            insertBroadcast.Parameters.AddWithValue("$files", broadcast.Files.Length);
            insertBroadcast.Parameters.AddWithValue("$segments", broadcast.SegmentCount);
            insertBroadcast.Parameters.AddWithValue("$recordings", broadcast.Recordings.Count);
            insertBroadcast.Parameters.AddWithValue("$exact", broadcast.ExactDuplicateCount);
            insertBroadcast.Parameters.AddWithValue("$strong", broadcast.StrongDuplicateCount);
            insertBroadcast.Parameters.AddWithValue("$current", broadcast.CurrentEpisodeCount);
            insertBroadcast.Parameters.AddWithValue("$status", broadcast.Status);
            insertBroadcast.Parameters.AddWithValue("$confidence", broadcast.ConfidenceScore);
            insertBroadcast.Parameters.AddWithValue("$evidence", broadcast.Evidence);
            insertBroadcast.Parameters.AddWithValue("$adoption", broadcast.AdoptionState);
            insertBroadcast.Parameters.AddWithValue("$adoptionReason", broadcast.AdoptionReason);
            insertBroadcast.Parameters.AddWithValue("$preferred", broadcast.PreferredRecordingKey);
            insertBroadcast.Parameters.AddWithValue("$suspicious", broadcast.SuspiciousMerge ? 1 : 0);
            insertBroadcast.Parameters.AddWithValue("$spread", broadcast.DurationSpreadRatio);
            insertBroadcast.Parameters.AddWithValue("$conflicts", broadcast.CrossIdentityConflictCount);
            insertBroadcast.ExecuteNonQuery();
        }

        using var insertRecording = connection.CreateCommand();
        insertRecording.Transaction = transaction;
        insertRecording.CommandText = """
            INSERT INTO library_truth_recordings(
                run_id,canonical_broadcast_key,recording_key,label,file_count,segment_count,duration_ms,
                relationship,confidence_score,evidence_json,role,completeness_score,preferred_score,
                duration_ratio,is_preferred_candidate,review_reason)
            VALUES($run,$broadcast,$key,$label,$files,$segments,$duration,$relationship,$confidence,$evidence,
                   $role,$completeness,$preferredScore,$durationRatio,$isPreferred,$reviewReason)
            """;
        foreach (var broadcast in analysis.Broadcasts)
        {
            foreach (var recording in broadcast.Recordings)
            {
                insertRecording.Parameters.Clear();
                insertRecording.Parameters.AddWithValue("$run", runId);
                insertRecording.Parameters.AddWithValue("$broadcast", broadcast.CanonicalKey);
                insertRecording.Parameters.AddWithValue("$key", recording.RecordingKey);
                insertRecording.Parameters.AddWithValue("$label", recording.Label);
                insertRecording.Parameters.AddWithValue("$files", recording.FileCount);
                insertRecording.Parameters.AddWithValue("$segments", recording.SegmentCount);
                insertRecording.Parameters.AddWithValue("$duration", recording.DurationMs);
                insertRecording.Parameters.AddWithValue("$relationship", recording.Relationship);
                insertRecording.Parameters.AddWithValue("$confidence", recording.ConfidenceScore);
                insertRecording.Parameters.AddWithValue("$evidence", recording.Evidence);
                insertRecording.Parameters.AddWithValue("$role", recording.Role);
                insertRecording.Parameters.AddWithValue("$completeness", recording.CompletenessScore);
                insertRecording.Parameters.AddWithValue("$preferredScore", recording.PreferredScore);
                insertRecording.Parameters.AddWithValue("$durationRatio", recording.DurationRatio);
                insertRecording.Parameters.AddWithValue("$isPreferred", recording.IsPreferredCandidate ? 1 : 0);
                insertRecording.Parameters.AddWithValue("$reviewReason", recording.ReviewReason);
                insertRecording.ExecuteNonQuery();
            }
        }

        using var insertCoverage = connection.CreateCommand();
        insertCoverage.Transaction = transaction;
        insertCoverage.CommandText = """
            INSERT INTO library_truth_coverages(
                run_id,source_broadcast_key,recording_key,segment_number,segment_total,target_broadcast_key,
                coverage_kind,start_offset_ms,end_offset_ms,confidence_score,requires_review,media_file_ids_json,evidence)
            VALUES($run,$source,$recording,$segment,$total,$target,$kind,$start,$end,$confidence,$review,$media,$evidence)
            """;
        foreach (var coverage in analysis.Coverages)
        {
            insertCoverage.Parameters.Clear();
            insertCoverage.Parameters.AddWithValue("$run", runId);
            insertCoverage.Parameters.AddWithValue("$source", coverage.SourceBroadcastKey);
            insertCoverage.Parameters.AddWithValue("$recording", coverage.RecordingKey);
            insertCoverage.Parameters.AddWithValue("$segment", coverage.SegmentNumber);
            insertCoverage.Parameters.AddWithValue("$total", coverage.SegmentTotal ?? (object)DBNull.Value);
            insertCoverage.Parameters.AddWithValue("$target", coverage.TargetBroadcastKey);
            insertCoverage.Parameters.AddWithValue("$kind", coverage.CoverageKind);
            insertCoverage.Parameters.AddWithValue("$start", coverage.StartOffsetMs);
            insertCoverage.Parameters.AddWithValue("$end", coverage.EndOffsetMs);
            insertCoverage.Parameters.AddWithValue("$confidence", coverage.ConfidenceScore);
            insertCoverage.Parameters.AddWithValue("$review", coverage.RequiresReview ? 1 : 0);
            insertCoverage.Parameters.AddWithValue("$media", JsonSerializer.Serialize(coverage.MediaFileIds.OrderBy(x => x), _json));
            insertCoverage.Parameters.AddWithValue("$evidence", coverage.Evidence);
            insertCoverage.ExecuteNonQuery();
        }

        using var insertPreview = connection.CreateCommand();
        insertPreview.Transaction = transaction;
        insertPreview.CommandText = """
            INSERT INTO library_truth_adoption_previews(
                run_id,canonical_key,adoption_state,planned_action,provisional_episode_id,current_episode_count,
                current_episode_ids_json,media_file_count,recording_count,coverage_count,retire_episode_count,
                reassign_file_count,planned_write_count,eligible_for_guarded_adoption,guard_reason,evidence)
            VALUES($run,$key,$state,$action,$survivor,$currentCount,$currentIds,$files,$recordings,$coverages,
                   $retire,$reassign,$writes,$eligible,$guard,$evidence)
            """;
        foreach (var preview in analysis.AdoptionPreviews)
        {
            insertPreview.Parameters.Clear();
            insertPreview.Parameters.AddWithValue("$run", runId);
            insertPreview.Parameters.AddWithValue("$key", preview.CanonicalKey);
            insertPreview.Parameters.AddWithValue("$state", preview.AdoptionState);
            insertPreview.Parameters.AddWithValue("$action", preview.PlannedAction);
            insertPreview.Parameters.AddWithValue("$survivor", preview.ProvisionalEpisodeId ?? (object)DBNull.Value);
            insertPreview.Parameters.AddWithValue("$currentCount", preview.CurrentEpisodeIds.Count);
            insertPreview.Parameters.AddWithValue("$currentIds", JsonSerializer.Serialize(preview.CurrentEpisodeIds, _json));
            insertPreview.Parameters.AddWithValue("$files", preview.MediaFileCount);
            insertPreview.Parameters.AddWithValue("$recordings", preview.RecordingCount);
            insertPreview.Parameters.AddWithValue("$coverages", preview.CoverageCount);
            insertPreview.Parameters.AddWithValue("$retire", preview.RetireEpisodeCount);
            insertPreview.Parameters.AddWithValue("$reassign", preview.ReassignFileCount);
            insertPreview.Parameters.AddWithValue("$writes", preview.PlannedWriteCount);
            insertPreview.Parameters.AddWithValue("$eligible", preview.EligibleForGuardedAdoption ? 1 : 0);
            insertPreview.Parameters.AddWithValue("$guard", preview.GuardReason);
            insertPreview.Parameters.AddWithValue("$evidence", preview.Evidence);
            insertPreview.ExecuteNonQuery();
        }

        using var insertConflict = connection.CreateCommand();
        insertConflict.Transaction = transaction;
        insertConflict.CommandText = """
            INSERT INTO library_truth_conflicts(run_id,conflict_type,evidence_strength,file_count,identity_count,identities,evidence,media_file_ids_json)
            VALUES($run,$type,$strength,$files,$identities,$identityText,$evidence,$media)
            """;
        foreach (var conflict in analysis.Conflicts)
        {
            insertConflict.Parameters.Clear();
            insertConflict.Parameters.AddWithValue("$run", runId);
            insertConflict.Parameters.AddWithValue("$type", conflict.ConflictType);
            insertConflict.Parameters.AddWithValue("$strength", conflict.EvidenceStrength);
            insertConflict.Parameters.AddWithValue("$files", conflict.FileCount);
            insertConflict.Parameters.AddWithValue("$identities", conflict.IdentityCount);
            insertConflict.Parameters.AddWithValue("$identityText", string.Join(Environment.NewLine, conflict.Identities));
            insertConflict.Parameters.AddWithValue("$evidence", conflict.Evidence);
            insertConflict.Parameters.AddWithValue("$media", JsonSerializer.Serialize(conflict.MediaFileIds.OrderBy(x => x), _json));
            insertConflict.ExecuteNonQuery();
        }

        using var insertYear = connection.CreateCommand();
        insertYear.Transaction = transaction;
        insertYear.CommandText = """
            INSERT INTO library_truth_years(run_id,year_label,physical_file_count,current_broadcast_count,proposed_broadcast_count,
                merge_groups,split_groups,ready_broadcasts,review_recommended_broadcasts,blocked_broadcasts)
            VALUES($run,$year,$files,$current,$proposed,$merges,$splits,$ready,$review,$blocked)
            """;
        foreach (var year in analysis.Years)
        {
            insertYear.Parameters.Clear();
            insertYear.Parameters.AddWithValue("$run", runId);
            insertYear.Parameters.AddWithValue("$year", year.YearLabel);
            insertYear.Parameters.AddWithValue("$files", year.PhysicalFileCount);
            insertYear.Parameters.AddWithValue("$current", year.CurrentBroadcastCount);
            insertYear.Parameters.AddWithValue("$proposed", year.ProposedBroadcastCount);
            insertYear.Parameters.AddWithValue("$merges", year.MergeGroups);
            insertYear.Parameters.AddWithValue("$splits", year.SplitGroups);
            insertYear.Parameters.AddWithValue("$ready", year.ReadyBroadcasts);
            insertYear.Parameters.AddWithValue("$review", year.ReviewRecommendedBroadcasts);
            insertYear.Parameters.AddWithValue("$blocked", year.BlockedBroadcasts);
            insertYear.ExecuteNonQuery();
        }

        var currentBroadcasts = interpretations.Select(x => x.Input.CurrentEpisodeId).Distinct().Count();
        var mergeGroups = analysis.Broadcasts.Count(x => x.CurrentEpisodeCount > 1);
        var splitGroups = analysis.SplitEpisodeIds.Count;
        var exactGroups = analysis.Broadcasts.Count(x => x.ExactDuplicateCount > 0);
        var strongGroups = analysis.Broadcasts.Count(x => x.StrongDuplicateCount > 0);
        var multipart = analysis.Broadcasts.Count(x => x.SegmentCount > 1);
        var ready = analysis.Broadcasts.Count(x => x.AdoptionState is "Ready" or "Ready with recording choice");
        var reviewRecommended = analysis.Broadcasts.Count(x => x.AdoptionState == "Review recommended");
        var blocked = analysis.Broadcasts.Count(x => x.AdoptionState == "Blocked");
        var inferredCoverage = analysis.Coverages.Count(x => x.RequiresReview);
        var message = $"Shadow library built from {interpretations.Count:N0} physical files: {analysis.Broadcasts.Count:N0} canonical broadcasts; {ready:N0} adoption-ready, {reviewRecommended:N0} review recommended and {blocked:N0} blocked. {analysis.Coverages.Count:N0} segment coverage rows and {analysis.AdoptionPreviews.Count:N0} non-destructive adoption previews were persisted; {inferredCoverage:N0} coverage rows require confirmation. {unknown:N0} dates remain unresolved. The live library was not changed.";

        using (var updateRun = connection.CreateCommand())
        {
            updateRun.Transaction = transaction;
            updateRun.CommandText = """
                UPDATE library_truth_runs SET
                    completed_at=$completed,status='completed',source_file_count=$files,current_broadcast_count=$current,
                    proposed_broadcast_count=$proposed,unchanged_files=$unchanged,changed_files=$changed,
                    recovered_dates=$recovered,unknown_dates=$unknown,needs_review=$review,merge_groups=$merges,
                    split_groups=$splits,exact_duplicate_groups=$exact,strong_duplicate_groups=$strong,
                    multipart_broadcasts=$multipart,message=$message
                 WHERE id=$id
                """;
            updateRun.Parameters.AddWithValue("$completed", completedAt.ToString("O"));
            updateRun.Parameters.AddWithValue("$files", interpretations.Count);
            updateRun.Parameters.AddWithValue("$current", currentBroadcasts);
            updateRun.Parameters.AddWithValue("$proposed", analysis.Broadcasts.Count);
            updateRun.Parameters.AddWithValue("$unchanged", unchanged);
            updateRun.Parameters.AddWithValue("$changed", changed);
            updateRun.Parameters.AddWithValue("$recovered", recovered);
            updateRun.Parameters.AddWithValue("$unknown", unknown);
            updateRun.Parameters.AddWithValue("$review", needsReview);
            updateRun.Parameters.AddWithValue("$merges", mergeGroups);
            updateRun.Parameters.AddWithValue("$splits", splitGroups);
            updateRun.Parameters.AddWithValue("$exact", exactGroups);
            updateRun.Parameters.AddWithValue("$strong", strongGroups);
            updateRun.Parameters.AddWithValue("$multipart", multipart);
            updateRun.Parameters.AddWithValue("$message", message);
            updateRun.Parameters.AddWithValue("$id", runId);
            updateRun.ExecuteNonQuery();
        }

        using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = """
                DELETE FROM library_truth_runs
                 WHERE id NOT IN (SELECT id FROM library_truth_runs ORDER BY id DESC LIMIT 5);
                """;
            cleanup.ExecuteNonQuery();
        }
        transaction.Commit();

        return new LibraryTruthRunSummary(runId, "completed", LibraryTruthParser.CurrentVersion, startedAt, completedAt,
            interpretations.Count, currentBroadcasts, analysis.Broadcasts.Count, unchanged, changed, recovered, unknown,
            needsReview, mergeGroups, splitGroups, exactGroups, strongGroups, multipart, message);
    }

    private long GetLatestCompletedRunId()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(id),0) FROM library_truth_runs WHERE status='completed'";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static LibraryTruthRunSummary ReadSummary(SqliteDataReader reader)
        => new(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3)),
            reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)), reader.GetInt32(5), reader.GetInt32(6),
            reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11),
            reader.GetInt32(12), reader.GetInt32(13), reader.GetInt32(14), reader.GetInt32(15), reader.GetInt32(16),
            reader.GetInt32(17), reader.GetString(18));

    private static string BroadcastFilterSql(string? filter, string? alias = null)
    {
        var prefix = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias + ".";
        return (filter ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "needs-attention" => $"AND ({prefix}status='Needs attention' OR {prefix}adoption_state='Blocked')",
            "multipart" => $"AND {prefix}segment_count>1",
            "duplicates" => $"AND ({prefix}exact_duplicate_count>0 OR {prefix}strong_duplicate_count>0)",
            "proposed" => $"AND {prefix}status<>'Stable'",
            "review-recommended" => $"AND {prefix}adoption_state='Review recommended'",
            "blocked" => $"AND {prefix}adoption_state='Blocked'",
            "ready" => $"AND {prefix}adoption_state IN ('Ready','Ready with recording choice')",
            "suspicious-merges" => $"AND {prefix}suspicious_merge=1",
            "unknown" => $"AND {prefix}air_date IS NULL",
            "unchanged" => $"AND {prefix}status='Stable'",
            _ => string.Empty
        };
    }

    private static string FilterSql(string? filter) => (filter ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "needs-attention" => "AND f.disposition IN ('Needs attention','Broadcast split')",
        "blocked" => "AND b.adoption_state='Blocked'",
        "review-recommended" => "AND b.adoption_state='Review recommended'",
        "ready" => "AND b.adoption_state IN ('Ready','Ready with recording choice')",
        "suspicious-merges" => "AND b.suspicious_merge=1",
        "proposed" => "AND f.disposition<>'Unchanged'",
        "recovered" => "AND f.disposition='Recovered date'",
        "multipart" => "AND (f.proposed_part>1 OR f.proposed_total_parts IS NOT NULL OR b.segment_count>1)",
        "duplicates" => "AND (b.exact_duplicate_count>0 OR b.strong_duplicate_count>0)",
        "unknown" => "AND f.proposed_air_date IS NULL",
        "unchanged" => "AND f.disposition='Unchanged'",
        _ => string.Empty
    };

    private static DateOnly? ParseDate(string value)
        => DateOnly.TryParse(value, out var parsed) ? parsed : null;

    private static string EmptyAsUnknown(string value) => string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
    private static string EmptyAsStandard(string value) => string.IsNullOrWhiteSpace(value) ? "Standard" : value;
    private static string PartDisplay(int part, int? total) => total.HasValue ? $"Part {Math.Max(1, part)} of {total}" : $"Part {Math.Max(1, part)}";

    private static string FormatEvidence(string json)
    {
        try
        {
            var evidence = JsonSerializer.Deserialize<List<LibraryTruthEvidence>>(json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true }) ?? new();
            return evidence.Count == 0 ? "No structured evidence recorded." : string.Join(Environment.NewLine,
                evidence.OrderByDescending(x => x.Weight).Select(x => $"• {x.Field}: {x.Value} ({x.Weight}%) — {x.Reasoning}"));
        }
        catch
        {
            return json;
        }
    }

    private static string FormatWarnings(string json)
    {
        try
        {
            var warnings = JsonSerializer.Deserialize<List<LibraryTruthWarning>>(json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true }) ?? new();
            return warnings.Count == 0 ? "No warnings." : string.Join(Environment.NewLine, warnings.Select(x => $"• {x.Message}"));
        }
        catch
        {
            return json;
        }
    }

    private static string FormatMediaIds(string json)
    {
        try
        {
            var ids = JsonSerializer.Deserialize<long[]>(json) ?? Array.Empty<long>();
            return ids.Length == 0 ? "None" : string.Join(", ", ids.Select(x => x.ToString("N0")));
        }
        catch
        {
            return json;
        }
    }

    private static int SafeInt(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));

    private static string Append(string first, string second)
        => string.IsNullOrWhiteSpace(first) ? second : $"{first} {second}";

    private sealed record LibraryRoot(string Path, string AssignedCollectionName);
    private sealed record PlannedFile(
        LibraryTruthInterpretation File,
        LibraryTruthRecordingFilenameStructure Structure,
        bool ExplicitMultipart);
    private sealed record EffectivePartAssignment(
        int PartNumber,
        int? TotalParts,
        bool PromotedFromAmbiguousNumber);
    private sealed record PlannedAudioCluster(
        string AudioKey,
        LibraryTruthInterpretation[] Files,
        IReadOnlySet<string> ProgrammeTokens,
        IReadOnlyList<string> FamilyKeys,
        bool HasAmbiguousTrailingNumber);
    private sealed record RecordingSeed(
        LibraryTruthInterpretation[] Files,
        int SegmentCount,
        long DurationMs,
        bool IsMultipart,
        bool CompleteMultipart,
        int? ExpectedParts,
        string Relationship,
        string Evidence,
        string FamilyKey,
        IReadOnlySet<string> ProgrammeTokens,
        bool PromotedBareNumberSequence);
    private sealed record RecordingAnalysis(
        string RecordingKey,
        string Label,
        int FileCount,
        int SegmentCount,
        long DurationMs,
        string Relationship,
        int ConfidenceScore,
        string Evidence,
        IReadOnlySet<long> MediaFileIds,
        string Role,
        int CompletenessScore,
        int PreferredScore,
        double DurationRatio,
        bool IsPreferredCandidate,
        string ReviewReason,
        bool IsMultipart,
        bool CompleteMultipart,
        int? ExpectedParts,
        string SeedEvidence,
        string FamilyKey,
        IReadOnlySet<string> ProgrammeTokens,
        bool PromotedBareNumberSequence);
    private sealed record RecordingDurationCluster(
        IReadOnlyList<RecordingAnalysis> Recordings,
        long MedianDurationMs,
        IReadOnlySet<string> ProgrammeTokens);
    private sealed record SuspiciousMergeAssessment(bool IsSuspicious, string Reason);
    private sealed record SegmentCoverageAnalysis(
        string SourceBroadcastKey,
        string RecordingKey,
        int SegmentNumber,
        int? SegmentTotal,
        string TargetBroadcastKey,
        string CoverageKind,
        long StartOffsetMs,
        long EndOffsetMs,
        int ConfidenceScore,
        bool RequiresReview,
        IReadOnlySet<long> MediaFileIds,
        string Evidence);
    private sealed record CoverageAuditResult(
        IReadOnlyList<BroadcastAnalysis> Broadcasts,
        IReadOnlyList<SegmentCoverageAnalysis> InferredCoverages);
    private sealed record AdoptionPreviewAnalysis(
        string CanonicalKey,
        string AdoptionState,
        string PlannedAction,
        long? ProvisionalEpisodeId,
        IReadOnlyList<long> CurrentEpisodeIds,
        int MediaFileCount,
        int RecordingCount,
        int CoverageCount,
        int RetireEpisodeCount,
        int ReassignFileCount,
        int PlannedWriteCount,
        bool EligibleForGuardedAdoption,
        string GuardReason,
        string Evidence);
    private sealed record BroadcastAnalysis(
        string CanonicalKey,
        LibraryTruthInterpretation[] Files,
        int SegmentCount,
        IReadOnlyList<RecordingAnalysis> Recordings,
        int ExactDuplicateCount,
        int StrongDuplicateCount,
        int CurrentEpisodeCount,
        bool RequiresStructureReview,
        string Status,
        int ConfidenceScore,
        string Evidence,
        string AdoptionState,
        string AdoptionReason,
        string PreferredRecordingKey,
        bool SuspiciousMerge,
        double DurationSpreadRatio,
        int CrossIdentityConflictCount);
    private sealed record ConflictAnalysis(
        string ConflictType,
        int EvidenceStrength,
        int FileCount,
        int IdentityCount,
        IReadOnlyList<string> Identities,
        string Evidence,
        IReadOnlySet<long> MediaFileIds,
        IReadOnlySet<string> CanonicalKeys);
    private sealed record YearAnalysis(
        string YearLabel,
        int PhysicalFileCount,
        int CurrentBroadcastCount,
        int ProposedBroadcastCount,
        int MergeGroups,
        int SplitGroups,
        int ReadyBroadcasts,
        int ReviewRecommendedBroadcasts,
        int BlockedBroadcasts);
    private sealed record GroupAnalysis(
        IReadOnlyList<BroadcastAnalysis> Broadcasts,
        IReadOnlySet<long> SplitEpisodeIds,
        IReadOnlySet<long> ConflictingIdentityMediaIds,
        IReadOnlyList<ConflictAnalysis> Conflicts,
        IReadOnlyList<YearAnalysis> Years,
        IReadOnlyDictionary<long, EffectivePartAssignment> EffectivePartAssignments,
        IReadOnlyList<SegmentCoverageAnalysis> Coverages,
        IReadOnlyList<AdoptionPreviewAnalysis> AdoptionPreviews);
}
