using System.Globalization;
using Microsoft.Data.Sqlite;
using TheRadioVault.Core.Services;
using TheRadioVault.Data.Database;

var tests = new (string Name, Action Run)[]
{
    ("Database seeds every first-class show", DatabaseSeedsEveryFirstClassShow),
    ("Latest schema exposes the canonical topic identity", LatestSchemaExposesCanonicalTopicIdentity),
    ("Latest schema contains required persistence boundaries", LatestSchemaContainsRequiredPersistenceBoundaries),
    ("Schema 43 upgrades safely to the latest schema", Schema43UpgradesSafely)
};

var selectedTests = args.Length == 0
    ? tests
    : tests.Where(test => args.Any(filter => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
if (selectedTests.Length == 0)
{
    Console.Error.WriteLine("No data tests matched the supplied filters.");
    return 2;
}

var failures = new List<string>();
foreach (var test in selectedTests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL  {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{selectedTests.Length - failures.Count}/{selectedTests.Length} data tests passed.");
return failures.Count == 0 ? 0 : 1;

static void DatabaseSeedsEveryFirstClassShow()
{
    WithDatabase("show-seed", database =>
    {
        using var connection = database.OpenConnection();
        foreach (var show in new[]
                 {
                     KnownShowCatalog.RonRon,
                     KnownShowCatalog.Unmasked,
                     KnownShowCatalog.RonBenningtonInterviews
                 })
        {
            Equal(1L, ScalarLong(
                connection,
                "SELECT COUNT(*) FROM collections WHERE name=$name",
                ("$name", show)), $"seeded collection {show}");
        }

        Equal(
            KnownShowCatalog.RonBenningtonInterviews,
            ScalarString(
                connection,
                """
                SELECT c.name
                  FROM collections c
                  JOIN collection_aliases a ON a.collection_id=c.id
                 WHERE a.alias=$alias COLLATE NOCASE;
                """,
                ("$alias", "ron bennington interview")),
            "seeded collection alias");
    });
}

static void LatestSchemaExposesCanonicalTopicIdentity()
{
    WithDatabase("wiki-schema", database =>
    {
        using var connection = database.OpenConnection();
        Equal(47L, ScalarLong(connection, "PRAGMA user_version"), "schema version");
        AssertObjectsExist(connection, "table",
        [
            "wiki_pages", "wiki_page_aliases", "wiki_page_revisions", "wiki_relationships",
            "wiki_sources", "wiki_citations", "wiki_images", "wiki_page_images",
            "wiki_timeline_events", "wiki_timeline_event_sources", "wiki_timeline_event_images",
            "wiki_timeline_event_broadcasts", "wiki_import_runs", "canonical_topics",
            "canonical_topic_aliases", "topic_merge_history", "wiki_page_redirects"
        ]);
        AssertObjectsExist(connection, "index",
        [
            "ix_wiki_pages_type_status", "ix_wiki_citations_page", "ix_wiki_timeline_events_page_date"
        ]);
    });
}

static void LatestSchemaContainsRequiredPersistenceBoundaries()
{
    WithDatabase("latest-schema", database =>
    {
        using var connection = database.OpenConnection();
        Equal(47L, ScalarLong(connection, "PRAGMA user_version"), "schema version");

        AssertObjectsExist(connection, "table",
        [
            "research_quality_actions", "research_import_rollbacks", "research_import_rollback_changes",
            "research_field_provenance", "transcripts", "transcript_segments", "transcription_jobs",
            "transcript_imports", "voice_people", "transcript_speakers", "voice_profiles", "voice_samples",
            "speaker_match_suggestions", "preservation_scan_runs", "library_truth_runs", "library_truth_files",
            "library_truth_recordings", "library_truth_broadcasts", "library_truth_years",
            "library_truth_conflicts", "library_truth_coverages", "library_truth_adoption_previews",
            "library_truth_rehearsal_runs", "library_truth_rehearsal_items",
            "library_truth_rehearsal_conflicts", "canonical_broadcasts", "recordings", "recording_segments",
            "recording_coverages", "episode_canonical_map", "library_truth_adoption_runs",
            "library_truth_adoption_items", "library_truth_adoption_conflicts"
        ]);
        AssertObjectsExist(connection, "index",
        [
            "ix_transcripts_status_updated", "ix_transcript_segments_time",
            "ix_transcription_jobs_state_requested", "ux_library_truth_adoption_completed_truth"
        ]);

        AssertColumnsExist(connection, "transcript_segments", ["speaker_key", "content_kind", "is_reviewed"]);
        AssertColumnsExist(connection, "transcription_jobs",
            ["language", "start_ms", "duration_ms", "enable_speaker_diarization", "use_vad", "replace_existing"]);
        AssertColumnsExist(connection, "research_reconciliation_candidates",
            ["requires_review", "review_category", "recommended_action", "decision_source"]);
        AssertColumnsExist(connection, "research_reconciliation_actions", ["decision_source"]);
        AssertColumnsExist(connection, "media_files",
            ["fingerprinted_at", "full_hashed_at", "inspection_error", "inspection_error_at"]);
        AssertColumnsExist(connection, "library_truth_files", ["recording_key"]);
        AssertColumnsExist(connection, "library_truth_coverages",
            ["recording_key", "segment_number", "target_broadcast_key", "coverage_kind", "start_offset_ms", "end_offset_ms", "requires_review", "media_file_ids_json"]);
        AssertColumnsExist(connection, "library_truth_adoption_previews",
            ["canonical_key", "planned_action", "provisional_episode_id", "current_episode_ids_json", "coverage_count", "planned_write_count", "eligible_for_guarded_adoption", "guard_reason"]);
        AssertColumnsExist(connection, "library_truth_rehearsal_runs",
            ["backup_path", "source_fingerprint", "rollback_fingerprint", "truth_run_signature", "item_signature", "conflict_signature", "file_reassignments", "state_rows_migrated", "auto_resolved_conflicts", "unresolved_conflicts", "preserved_alternates", "foreign_key_violations", "rollback_verified", "message"]);
        AssertColumnsExist(connection, "library_truth_rehearsal_conflicts",
            ["canonical_key", "field_name", "classification", "selected_value", "candidate_values_json", "provenance_json", "resolution", "auto_resolved", "requires_review", "preserved_alternate_count"]);
        AssertColumnsExist(connection, "library_truth_recordings",
            ["role", "completeness_score", "preferred_score", "duration_ratio", "is_preferred_candidate", "review_reason"]);
        AssertColumnsExist(connection, "library_truth_broadcasts",
            ["adoption_state", "adoption_reason", "preferred_recording_key", "suspicious_merge", "duration_spread_ratio", "cross_identity_conflict_count"]);
        AssertColumnsExist(connection, "library_truth_adoption_runs",
            ["truth_run_id", "rehearsal_run_id", "backup_path", "source_fingerprint", "staged_fingerprint", "post_commit_fingerprint", "rehearsal_truth_signature", "commit_truth_signature", "rehearsal_item_signature", "commit_item_signature", "rehearsal_conflict_signature", "commit_conflict_signature", "commit_verified"]);
    });
}

static void Schema43UpgradesSafely()
{
    var directory = CreateTemporaryDirectory("schema43-upgrade");
    var path = Path.Combine(directory, "schema43.sqlite");
    try
    {
        using (var legacy = new SqliteConnection($"Data Source={path}"))
        {
            legacy.Open();
            Execute(legacy, """
                PRAGMA user_version=43;
                CREATE TABLE library_truth_runs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    started_at TEXT NOT NULL,
                    completed_at TEXT NULL,
                    status TEXT NOT NULL DEFAULT 'running',
                    parser_version TEXT NOT NULL DEFAULT '',
                    source_file_count INTEGER NOT NULL DEFAULT 0,
                    current_broadcast_count INTEGER NOT NULL DEFAULT 0,
                    proposed_broadcast_count INTEGER NOT NULL DEFAULT 0,
                    unchanged_files INTEGER NOT NULL DEFAULT 0,
                    changed_files INTEGER NOT NULL DEFAULT 0,
                    recovered_dates INTEGER NOT NULL DEFAULT 0,
                    unknown_dates INTEGER NOT NULL DEFAULT 0,
                    needs_review INTEGER NOT NULL DEFAULT 0,
                    merge_groups INTEGER NOT NULL DEFAULT 0,
                    split_groups INTEGER NOT NULL DEFAULT 0,
                    exact_duplicate_groups INTEGER NOT NULL DEFAULT 0,
                    strong_duplicate_groups INTEGER NOT NULL DEFAULT 0,
                    multipart_broadcasts INTEGER NOT NULL DEFAULT 0,
                    message TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE library_truth_broadcasts (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_id INTEGER NOT NULL,
                    canonical_key TEXT NOT NULL,
                    collection_name TEXT NOT NULL DEFAULT '',
                    air_date TEXT NULL,
                    broadcast_slot TEXT NOT NULL DEFAULT '',
                    file_count INTEGER NOT NULL DEFAULT 0,
                    segment_count INTEGER NOT NULL DEFAULT 1,
                    recording_count INTEGER NOT NULL DEFAULT 1,
                    exact_duplicate_count INTEGER NOT NULL DEFAULT 0,
                    strong_duplicate_count INTEGER NOT NULL DEFAULT 0,
                    current_episode_count INTEGER NOT NULL DEFAULT 0,
                    status TEXT NOT NULL DEFAULT 'Stable',
                    confidence_score INTEGER NOT NULL DEFAULT 0,
                    evidence_json TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE library_truth_recordings (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_id INTEGER NOT NULL,
                    canonical_broadcast_key TEXT NOT NULL,
                    recording_key TEXT NOT NULL,
                    label TEXT NOT NULL DEFAULT '',
                    file_count INTEGER NOT NULL DEFAULT 0,
                    segment_count INTEGER NOT NULL DEFAULT 1,
                    duration_ms INTEGER NOT NULL DEFAULT 0,
                    relationship TEXT NOT NULL DEFAULT 'Single recording',
                    confidence_score INTEGER NOT NULL DEFAULT 0,
                    evidence_json TEXT NOT NULL DEFAULT ''
                );
                """);
        }

        var database = new SqliteDatabase(path);
        database.Initialize();
        using var connection = database.OpenConnection();
        Equal(47L, ScalarLong(connection, "PRAGMA user_version"), "upgraded schema version");
        AssertColumnsExist(connection, "library_truth_recordings",
            ["role", "completeness_score", "preferred_score", "duration_ratio", "is_preferred_candidate", "review_reason"]);
        AssertObjectsExist(connection, "index", ["ix_library_truth_recordings_role"]);
        AssertObjectsExist(connection, "table",
        [
            "library_truth_coverages", "library_truth_adoption_previews", "library_truth_rehearsal_runs",
            "library_truth_rehearsal_items", "library_truth_rehearsal_conflicts", "canonical_broadcasts", "recordings",
            "recording_segments", "recording_coverages", "episode_canonical_map", "library_truth_adoption_runs",
            "library_truth_adoption_items", "library_truth_adoption_conflicts"
        ]);
    }
    finally
    {
        DeleteTemporaryDirectory(directory);
    }
}

static void WithDatabase(string name, Action<SqliteDatabase> test)
{
    var directory = CreateTemporaryDirectory(name);
    try
    {
        var database = new SqliteDatabase(Path.Combine(directory, $"{name}.sqlite"));
        database.Initialize();
        test(database);
    }
    finally
    {
        DeleteTemporaryDirectory(directory);
    }
}

static string CreateTemporaryDirectory(string name)
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultDataTests", $"{name}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    return directory;
}

static void DeleteTemporaryDirectory(string directory)
{
    SqliteConnection.ClearAllPools();
    try { Directory.Delete(directory, recursive: true); }
    catch (DirectoryNotFoundException) { }
}

static void AssertObjectsExist(SqliteConnection connection, string type, IReadOnlyCollection<string> names)
{
    var placeholders = names.Select((_, index) => $"$name{index}").ToArray();
    using var command = connection.CreateCommand();
    command.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type=$type AND name IN ({string.Join(',', placeholders)})";
    command.Parameters.AddWithValue("$type", type);
    var index = 0;
    foreach (var name in names)
        command.Parameters.AddWithValue(placeholders[index++], name);
    Equal((long)names.Count, Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture), $"{type} set");
}

static void AssertColumnsExist(SqliteConnection connection, string table, IReadOnlyCollection<string> names)
{
    var placeholders = names.Select((_, index) => $"$name{index}").ToArray();
    using var command = connection.CreateCommand();
    command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info($table) WHERE name IN ({string.Join(',', placeholders)})";
    command.Parameters.AddWithValue("$table", table);
    var index = 0;
    foreach (var name in names)
        command.Parameters.AddWithValue(placeholders[index++], name);
    Equal((long)names.Count, Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture), $"{table} column set");
}

static long ScalarLong(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters) =>
    Convert.ToInt64(Scalar(connection, sql, parameters), CultureInfo.InvariantCulture);

static string ScalarString(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters) =>
    Convert.ToString(Scalar(connection, sql, parameters), CultureInfo.InvariantCulture) ?? string.Empty;

static object? Scalar(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    foreach (var parameter in parameters)
        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
    return command.ExecuteScalar();
}

static void Execute(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

static void Equal<T>(T expected, T actual, string context)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {context} to be {expected}, got {actual}.");
}
