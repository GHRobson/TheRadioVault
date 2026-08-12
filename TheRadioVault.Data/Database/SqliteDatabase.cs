using Microsoft.Data.Sqlite;
using TheRadioVault.Core.Services;
using TheRadioVault.Data.Database.Migrations;

namespace TheRadioVault.Data.Database;

/// <summary>
/// Cross-platform SQLite database bootstrap and connection factory.
/// This project contains no WPF or Windows-specific dependencies and can be
/// reused by future Avalonia, iOS, Android, macOS, Linux, or command-line hosts.
/// </summary>
public sealed class SqliteDatabase
{
    public static int CurrentSchemaVersion => SqliteMigrationCatalog.Runner.CurrentVersion;

    private readonly object _initializationGate = new();
    private bool _initialized;

    public SqliteDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A database path is required.", nameof(databasePath));

        DatabasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath { get; }
    public string ConnectionString
    {
        get
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true,
                DefaultTimeout = 5
            };
            return builder.ToString();
        }
    }

    public SqliteConnection CreateConnection()
        => new(ConnectionString);

    /// <summary>
    /// Opens a consistently configured application connection. SQLite PRAGMAs
    /// such as foreign-key enforcement are connection-local, so every caller
    /// must go through this method rather than opening a raw connection.
    /// </summary>
    public SqliteConnection OpenConnection()
    {
        var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Initialize()
    {
        lock (_initializationGate)
        {
            if (_initialized) return;

            var directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            CreateUpgradeBackupIfNeeded();

            using var connection = OpenConnection();

            ConfigureDatabase(connection);
            SqliteMigrationCatalog.Runner.EnsureCompatible(connection);

            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                PRAGMA foreign_keys = ON;
                CREATE TABLE IF NOT EXISTS collections (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL UNIQUE,
                    sort_name TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS collection_aliases (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    collection_id INTEGER NOT NULL REFERENCES collections(id),
                    alias TEXT NOT NULL UNIQUE
                );
                CREATE TABLE IF NOT EXISTS library_folders (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    path TEXT NOT NULL UNIQUE,
                    assigned_collection_id INTEGER NULL REFERENCES collections(id),
                    recursive INTEGER NOT NULL DEFAULT 1,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    last_scan_at TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS episodes (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    collection_id INTEGER NOT NULL REFERENCES collections(id),
                    air_date TEXT NULL,
                    date_confidence TEXT NOT NULL DEFAULT 'Unknown',
                    title TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'Unplayed',
                    date_added TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS media_files (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    episode_id INTEGER NOT NULL REFERENCES episodes(id),
                    path TEXT NOT NULL UNIQUE,
                    original_filename TEXT NOT NULL,
                    file_size INTEGER NOT NULL,
                    modified_time TEXT NOT NULL,
                    is_missing INTEGER NOT NULL DEFAULT 0,
                    last_seen_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS playback_state (
                    episode_id INTEGER PRIMARY KEY REFERENCES episodes(id),
                    position_ms INTEGER NOT NULL DEFAULT 0,
                    completed INTEGER NOT NULL DEFAULT 0,
                    last_played_at TEXT NULL,
                    play_count INTEGER NOT NULL DEFAULT 0,
                    duration_ms INTEGER NOT NULL DEFAULT 0,
                    playback_speed REAL NOT NULL DEFAULT 1.0,
                    completed_at TEXT NULL
                );
                """;
                command.ExecuteNonQuery();
            }

            var columnCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            EnsureColumn(connection, columnCache, "playback_state", "duration_ms", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "playback_state", "playback_speed", "REAL NOT NULL DEFAULT 1.0");
            EnsureColumn(connection, columnCache, "playback_state", "completed_at", "TEXT NULL");
            EnsureColumn(connection, columnCache, "playback_state", "first_played_at", "TEXT NULL");
            EnsureColumn(connection, columnCache, "playback_state", "completion_count", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "episodes", "favourite", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "episodes", "description", "TEXT NULL");
            EnsureColumn(connection, columnCache, "episodes", "notes", "TEXT NULL");
            EnsureColumn(connection, columnCache, "episodes", "user_modified", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "episodes", "artwork_path", "TEXT NULL");
            EnsureColumn(connection, columnCache, "episodes", "broadcast_uid", "TEXT NULL");
            EnsureColumn(connection, columnCache, "episodes", "part_number", "INTEGER NOT NULL DEFAULT 1");
            EnsureColumn(connection, columnCache, "episodes", "total_parts", "INTEGER NULL");
            EnsureColumn(connection, columnCache, "episodes", "broadcast_slot", "TEXT NULL");
            EnsureColumn(connection, columnCache, "episodes", "edition", "TEXT NULL");
            EnsureColumn(connection, columnCache, "episodes", "metadata_confidence", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "episodes", "metadata_confidence_reason", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "episodes", "archive_notes", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "episodes", "broadcast_variant", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "episodes", "broadcast_era", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "episodes", "episode_type", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "episodes", "research_sources", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "episodes", "hosts", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "episodes", "callers", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "episodes", "mentioned_people", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "episodes", "hidden", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "media_files", "duration_ms", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "media_files", "storage_state", "TEXT NOT NULL DEFAULT 'AvailableOffline'");
            EnsureColumn(connection, columnCache, "media_files", "is_preferred", "INTEGER NOT NULL DEFAULT 1");
            EnsureColumn(connection, columnCache, "media_files", "partial_hash", "TEXT NULL");
            EnsureColumn(connection, columnCache, "media_files", "full_hash", "TEXT NULL");
            EnsureColumn(connection, columnCache, "media_files", "fingerprinted_at", "TEXT NULL");
            EnsureColumn(connection, columnCache, "media_files", "full_hashed_at", "TEXT NULL");
            EnsureColumn(connection, columnCache, "media_files", "inspection_error", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "media_files", "inspection_error_at", "TEXT NULL");

            using (var featureTables = connection.CreateCommand())
            {
                featureTables.CommandText = """
                CREATE TABLE IF NOT EXISTS moments (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    episode_id INTEGER NOT NULL REFERENCES episodes(id) ON DELETE CASCADE,
                    position_ms INTEGER NOT NULL,
                    title TEXT NOT NULL DEFAULT '',
                    notes TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_moments_episode ON moments(episode_id, position_ms);
                CREATE TABLE IF NOT EXISTS playback_queue (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    episode_id INTEGER NOT NULL REFERENCES episodes(id) ON DELETE CASCADE,
                    queue_position INTEGER NOT NULL,
                    added_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_playback_queue_position ON playback_queue(queue_position);
                CREATE TABLE IF NOT EXISTS guests (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE);
                CREATE TABLE IF NOT EXISTS episode_guests (episode_id INTEGER NOT NULL REFERENCES episodes(id) ON DELETE CASCADE, guest_id INTEGER NOT NULL REFERENCES guests(id) ON DELETE CASCADE, PRIMARY KEY(episode_id,guest_id));
                CREATE TABLE IF NOT EXISTS tags (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE);
                CREATE TABLE IF NOT EXISTS episode_tags (episode_id INTEGER NOT NULL REFERENCES episodes(id) ON DELETE CASCADE, tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE, PRIMARY KEY(episode_id,tag_id));
                CREATE TABLE IF NOT EXISTS scan_runs (id INTEGER PRIMARY KEY AUTOINCREMENT, started_at TEXT NOT NULL, completed_at TEXT NULL, scan_type TEXT NOT NULL, files_found INTEGER NOT NULL DEFAULT 0, files_added INTEGER NOT NULL DEFAULT 0, files_updated INTEGER NOT NULL DEFAULT 0, files_unchanged INTEGER NOT NULL DEFAULT 0, missing_files INTEGER NOT NULL DEFAULT 0, errors INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS headline_reviews (episode_id INTEGER PRIMARY KEY REFERENCES episodes(id) ON DELETE CASCADE, candidate TEXT NOT NULL DEFAULT '', confidence TEXT NOT NULL DEFAULT 'Probable', reasoning TEXT NOT NULL DEFAULT '', decision TEXT NOT NULL DEFAULT '', updated_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS preservation_scan_runs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    machine_id TEXT NOT NULL DEFAULT '',
                    started_at TEXT NOT NULL,
                    completed_at TEXT NULL,
                    status TEXT NOT NULL DEFAULT 'running',
                    options_json TEXT NOT NULL DEFAULT '{}',
                    total_files INTEGER NOT NULL DEFAULT 0,
                    processed_files INTEGER NOT NULL DEFAULT 0,
                    fingerprinted_files INTEGER NOT NULL DEFAULT 0,
                    full_hashed_files INTEGER NOT NULL DEFAULT 0,
                    errors INTEGER NOT NULL DEFAULT 0,
                    message TEXT NOT NULL DEFAULT ''
                );
                CREATE INDEX IF NOT EXISTS ix_preservation_scan_runs_started ON preservation_scan_runs(started_at DESC);
                CREATE TABLE IF NOT EXISTS missing_broadcast_research (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    stable_key TEXT NOT NULL UNIQUE,
                    broadcast_uid TEXT NOT NULL DEFAULT '',
                    show_name TEXT NOT NULL,
                    normalized_show_name TEXT NOT NULL,
                    broadcast_date TEXT NULL,
                    slot TEXT NOT NULL DEFAULT '',
                    normalized_slot TEXT NOT NULL DEFAULT '',
                    part_number INTEGER NOT NULL DEFAULT 1,
                    total_parts INTEGER NULL,
                    headline TEXT NOT NULL DEFAULT '',
                    summary TEXT NOT NULL DEFAULT '',
                    confidence INTEGER NOT NULL DEFAULT 0,
                    confidence_reason TEXT NOT NULL DEFAULT '',
                    research_json TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','ambiguous','resolved','ignored')),
                    matched_episode_id INTEGER NULL REFERENCES episodes(id) ON DELETE SET NULL,
                    match_notes TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    resolved_at TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_missing_broadcast_research_match
                    ON missing_broadcast_research(status,normalized_show_name,broadcast_date,normalized_slot,part_number);
                CREATE INDEX IF NOT EXISTS ix_missing_broadcast_research_uid
                    ON missing_broadcast_research(status,broadcast_uid);
                CREATE TABLE IF NOT EXISTS missing_broadcast_research_revisions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    missing_research_id INTEGER NOT NULL REFERENCES missing_broadcast_research(id) ON DELETE CASCADE,
                    research_json TEXT NOT NULL,
                    status TEXT NOT NULL,
                    matched_episode_id INTEGER NULL,
                    match_notes TEXT NOT NULL DEFAULT '',
                    saved_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_missing_broadcast_research_revisions_parent
                    ON missing_broadcast_research_revisions(missing_research_id,id);
                CREATE TRIGGER IF NOT EXISTS tr_missing_broadcast_research_revision
                BEFORE UPDATE ON missing_broadcast_research
                BEGIN
                    INSERT INTO missing_broadcast_research_revisions(
                        missing_research_id,research_json,status,matched_episode_id,match_notes,saved_at)
                    VALUES(OLD.id,OLD.research_json,OLD.status,OLD.matched_episode_id,OLD.match_notes,OLD.updated_at);
                END;
                """;
                featureTables.ExecuteNonQuery();
            }

            EnsureColumn(connection, columnCache, "headline_reviews", "parser_version", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "headline_reviews", "reviewed_headline", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "scan_runs", "files_unchanged", "INTEGER NOT NULL DEFAULT 0");

            using (var identityMigration = connection.CreateCommand())
            {
                identityMigration.CommandText = "UPDATE episodes SET broadcast_uid='BROADCAST-' || id WHERE broadcast_uid IS NULL OR broadcast_uid=''; UPDATE media_files SET is_preferred=1 WHERE is_preferred IS NULL;";
                identityMigration.ExecuteNonQuery();
            }

            foreach (var show in KnownShowCatalog.Collections)
                SeedCollection(connection, show.CanonicalName, show.SortName, show.Aliases);

            EnsureResearchLibrarySchema(connection);
            EnsureTranscriptionSchema(connection);
            EnsureColumn(connection, columnCache, "transcripts", "has_speaker_diarization", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "transcript_segments", "speaker_key", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "transcript_segments", "content_kind", "TEXT NOT NULL DEFAULT 'Speech'");
            EnsureColumn(connection, columnCache, "transcript_segments", "is_reviewed", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "transcription_jobs", "language", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "transcription_jobs", "start_ms", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "transcription_jobs", "duration_ms", "INTEGER NULL");
            EnsureColumn(connection, columnCache, "transcription_jobs", "enable_speaker_diarization", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "transcription_jobs", "use_vad", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "transcription_jobs", "replace_existing", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "transcription_jobs", "is_paused", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "research_import_runs", "status", "TEXT NOT NULL DEFAULT 'completed'");
            EnsureColumn(connection, columnCache, "research_import_runs", "restored_change_count", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "research_import_runs", "blocked_rollback_count", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "research_import_runs", "last_rollback_at", "TEXT NULL");
            EnsureColumn(connection, columnCache, "research_reconciliation_candidates", "requires_review", "INTEGER NOT NULL DEFAULT 1");
            EnsureColumn(connection, columnCache, "research_reconciliation_candidates", "review_category", "TEXT NOT NULL DEFAULT 'ambiguous_match'");
            EnsureColumn(connection, columnCache, "research_reconciliation_candidates", "recommended_action", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "research_reconciliation_candidates", "decision_source", "TEXT NOT NULL DEFAULT 'manual'");
            EnsureColumn(connection, columnCache, "research_reconciliation_actions", "decision_source", "TEXT NOT NULL DEFAULT 'manual'");
            using (var preservationEvidenceMigration = connection.CreateCommand())
            {
                preservationEvidenceMigration.CommandText = """
                    UPDATE media_files
                       SET fingerprinted_at=COALESCE(fingerprinted_at,last_seen_at)
                     WHERE COALESCE(partial_hash,'')<>'';
                    UPDATE media_files
                       SET full_hashed_at=COALESCE(full_hashed_at,last_seen_at)
                     WHERE COALESCE(full_hash,'')<>'';
                    """;
                preservationEvidenceMigration.ExecuteNonQuery();
            }
            EnsureLibraryTruthSchema(connection);
            EnsureLibraryTruthAdoptionSchema(connection);
            EnsureColumn(connection, columnCache, "library_truth_broadcasts", "adoption_state", "TEXT NOT NULL DEFAULT 'Not assessed'");
            EnsureColumn(connection, columnCache, "library_truth_broadcasts", "adoption_reason", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "library_truth_broadcasts", "preferred_recording_key", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "library_truth_broadcasts", "suspicious_merge", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "library_truth_broadcasts", "duration_spread_ratio", "REAL NOT NULL DEFAULT 1.0");
            EnsureColumn(connection, columnCache, "library_truth_broadcasts", "cross_identity_conflict_count", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "library_truth_recordings", "role", "TEXT NOT NULL DEFAULT 'Unknown'");
            EnsureColumn(connection, columnCache, "library_truth_recordings", "completeness_score", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "library_truth_recordings", "preferred_score", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "library_truth_recordings", "duration_ratio", "REAL NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "library_truth_recordings", "is_preferred_candidate", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "library_truth_recordings", "review_reason", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "library_truth_rehearsal_runs", "auto_resolved_conflicts", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "library_truth_rehearsal_runs", "unresolved_conflicts", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "library_truth_rehearsal_runs", "preserved_alternates", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "library_truth_rehearsal_runs", "truth_run_signature", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "library_truth_rehearsal_runs", "item_signature", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "library_truth_rehearsal_runs", "conflict_signature", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "library_truth_adoption_runs", "rehearsal_truth_signature", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "library_truth_adoption_runs", "commit_truth_signature", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, columnCache, "library_truth_rehearsal_items", "auto_resolved_conflicts", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "library_truth_rehearsal_items", "unresolved_conflicts", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columnCache, "library_truth_rehearsal_items", "preserved_alternates", "INTEGER NOT NULL DEFAULT 0");
            EnsureWikiSchema(connection);
            EnsurePerformanceIndexes(connection);

            if (SqliteMigrationRunner.ReadVersion(connection) < SqliteMigrationCatalog.LegacySchemaVersion)
            {
                using var schemaVersion = connection.CreateCommand();
                schemaVersion.CommandText = $"PRAGMA user_version = {SqliteMigrationCatalog.LegacySchemaVersion};";
                schemaVersion.ExecuteNonQuery();
            }

            SqliteMigrationCatalog.Runner.ApplyPending(connection);

            using (var optimize = connection.CreateCommand())
            {
                optimize.CommandText = "PRAGMA optimize;";
                optimize.ExecuteNonQuery();
            }

            _initialized = true;
        }
    }

    private static void ConfigureDatabase(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA temp_store=MEMORY;
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureWikiSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS wiki_pages (
                id TEXT PRIMARY KEY,
                slug TEXT NOT NULL UNIQUE COLLATE NOCASE,
                title TEXT NOT NULL,
                page_type TEXT NOT NULL DEFAULT 'Custom',
                summary TEXT NOT NULL DEFAULT '',
                body_markdown TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL DEFAULT 'Draft',
                revision INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                created_by TEXT NOT NULL DEFAULT 'Radio Vault user',
                last_editor TEXT NOT NULL DEFAULT 'Radio Vault user'
            );
            CREATE INDEX IF NOT EXISTS ix_wiki_pages_title ON wiki_pages(title COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS ix_wiki_pages_type_status ON wiki_pages(page_type,status,updated_at DESC);

            CREATE TABLE IF NOT EXISTS wiki_page_aliases (
                page_id TEXT NOT NULL REFERENCES wiki_pages(id) ON DELETE CASCADE,
                alias TEXT NOT NULL COLLATE NOCASE,
                sort_order INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(page_id,alias)
            );
            CREATE INDEX IF NOT EXISTS ix_wiki_page_aliases_alias ON wiki_page_aliases(alias COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS wiki_page_revisions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                page_id TEXT NOT NULL REFERENCES wiki_pages(id) ON DELETE CASCADE,
                revision INTEGER NOT NULL,
                snapshot_json TEXT NOT NULL,
                change_summary TEXT NOT NULL DEFAULT '',
                author TEXT NOT NULL DEFAULT '',
                import_run_id INTEGER NULL,
                created_at TEXT NOT NULL,
                UNIQUE(page_id,revision)
            );
            CREATE INDEX IF NOT EXISTS ix_wiki_page_revisions_page ON wiki_page_revisions(page_id,revision DESC);

            CREATE TABLE IF NOT EXISTS wiki_relationships (
                id TEXT PRIMARY KEY,
                from_page_id TEXT NOT NULL REFERENCES wiki_pages(id) ON DELETE CASCADE,
                to_page_id TEXT NOT NULL REFERENCES wiki_pages(id) ON DELETE CASCADE,
                relationship_type TEXT NOT NULL,
                valid_from TEXT NULL,
                valid_to TEXT NULL,
                date_precision TEXT NOT NULL DEFAULT 'Unknown',
                notes TEXT NOT NULL DEFAULT '',
                sort_order INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_wiki_relationships_from ON wiki_relationships(from_page_id,sort_order);
            CREATE INDEX IF NOT EXISTS ix_wiki_relationships_to ON wiki_relationships(to_page_id,relationship_type);

            CREATE TABLE IF NOT EXISTS wiki_sources (
                id TEXT PRIMARY KEY,
                source_type TEXT NOT NULL,
                title TEXT NOT NULL,
                author TEXT NOT NULL DEFAULT '',
                publisher TEXT NOT NULL DEFAULT '',
                url TEXT NOT NULL DEFAULT '',
                archived_url TEXT NOT NULL DEFAULT '',
                published_date TEXT NULL,
                date_precision TEXT NOT NULL DEFAULT 'Unknown',
                accessed_at TEXT NULL,
                episode_id INTEGER NULL REFERENCES episodes(id) ON DELETE SET NULL,
                broadcast_uid TEXT NOT NULL DEFAULT '',
                start_ms INTEGER NULL,
                end_ms INTEGER NULL,
                transcript_segment_id INTEGER NULL,
                moment_id INTEGER NULL REFERENCES moments(id) ON DELETE SET NULL,
                locator TEXT NOT NULL DEFAULT '',
                notes TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_wiki_sources_episode ON wiki_sources(episode_id,start_ms);
            CREATE INDEX IF NOT EXISTS ix_wiki_sources_url ON wiki_sources(url) WHERE url<>'';

            CREATE TABLE IF NOT EXISTS wiki_citations (
                id TEXT PRIMARY KEY,
                page_id TEXT NOT NULL REFERENCES wiki_pages(id) ON DELETE CASCADE,
                source_id TEXT NOT NULL REFERENCES wiki_sources(id) ON DELETE CASCADE,
                ordinal INTEGER NOT NULL DEFAULT 1,
                section_anchor TEXT NOT NULL DEFAULT '',
                quoted_text TEXT NOT NULL DEFAULT '',
                note TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_wiki_citations_page ON wiki_citations(page_id,ordinal);
            CREATE INDEX IF NOT EXISTS ix_wiki_citations_source ON wiki_citations(source_id,page_id);

            CREATE TABLE IF NOT EXISTS wiki_images (
                id TEXT PRIMARY KEY,
                original_file_name TEXT NOT NULL,
                media_type TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                byte_count INTEGER NOT NULL,
                content BLOB NOT NULL,
                caption TEXT NOT NULL DEFAULT '',
                alt_text TEXT NOT NULL DEFAULT '',
                creator TEXT NOT NULL DEFAULT '',
                copyright_holder TEXT NOT NULL DEFAULT '',
                licence TEXT NOT NULL DEFAULT '',
                source_id TEXT NULL REFERENCES wiki_sources(id) ON DELETE SET NULL,
                captured_date TEXT NULL,
                representative_from TEXT NULL,
                representative_to TEXT NULL,
                date_precision TEXT NOT NULL DEFAULT 'Unknown',
                date_notes TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_wiki_images_sha256 ON wiki_images(sha256);
            CREATE INDEX IF NOT EXISTS ix_wiki_images_dates ON wiki_images(captured_date,representative_from,representative_to);

            CREATE TABLE IF NOT EXISTS wiki_page_images (
                page_id TEXT NOT NULL REFERENCES wiki_pages(id) ON DELETE CASCADE,
                image_id TEXT NOT NULL REFERENCES wiki_images(id) ON DELETE CASCADE,
                role TEXT NOT NULL DEFAULT 'Gallery',
                sort_order INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(page_id,image_id)
            );
            CREATE INDEX IF NOT EXISTS ix_wiki_page_images_order ON wiki_page_images(page_id,role,sort_order);

            CREATE TABLE IF NOT EXISTS wiki_timeline_events (
                id TEXT PRIMARY KEY,
                page_id TEXT NOT NULL REFERENCES wiki_pages(id) ON DELETE CASCADE,
                title TEXT NOT NULL,
                summary TEXT NOT NULL DEFAULT '',
                category TEXT NOT NULL DEFAULT 'Milestone',
                start_date TEXT NULL,
                end_date TEXT NULL,
                date_precision TEXT NOT NULL DEFAULT 'Unknown',
                date_display TEXT NOT NULL DEFAULT '',
                significance INTEGER NOT NULL DEFAULT 50,
                sort_order INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_wiki_timeline_events_page_date ON wiki_timeline_events(page_id,start_date,sort_order);

            CREATE TABLE IF NOT EXISTS wiki_timeline_event_sources (
                event_id TEXT NOT NULL REFERENCES wiki_timeline_events(id) ON DELETE CASCADE,
                source_id TEXT NOT NULL REFERENCES wiki_sources(id) ON DELETE CASCADE,
                sort_order INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(event_id,source_id)
            );
            CREATE TABLE IF NOT EXISTS wiki_timeline_event_images (
                event_id TEXT NOT NULL REFERENCES wiki_timeline_events(id) ON DELETE CASCADE,
                image_id TEXT NOT NULL REFERENCES wiki_images(id) ON DELETE CASCADE,
                sort_order INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(event_id,image_id)
            );
            CREATE TABLE IF NOT EXISTS wiki_timeline_event_broadcasts (
                event_id TEXT NOT NULL REFERENCES wiki_timeline_events(id) ON DELETE CASCADE,
                episode_id INTEGER NOT NULL REFERENCES episodes(id) ON DELETE CASCADE,
                moment_id INTEGER NULL REFERENCES moments(id) ON DELETE SET NULL,
                start_ms INTEGER NULL,
                end_ms INTEGER NULL,
                label TEXT NOT NULL DEFAULT '',
                sort_order INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(event_id,episode_id,start_ms)
            );
            CREATE INDEX IF NOT EXISTS ix_wiki_timeline_broadcasts_episode ON wiki_timeline_event_broadcasts(episode_id,start_ms);

            CREATE TABLE IF NOT EXISTS wiki_import_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                package_name TEXT NOT NULL,
                package_sha256 TEXT NOT NULL,
                package_id TEXT NOT NULL,
                schema_version INTEGER NOT NULL,
                imported_at TEXT NOT NULL,
                created_pages INTEGER NOT NULL DEFAULT 0,
                updated_pages INTEGER NOT NULL DEFAULT 0,
                unchanged_pages INTEGER NOT NULL DEFAULT 0,
                skipped_conflicts INTEGER NOT NULL DEFAULT 0,
                sources_stored INTEGER NOT NULL DEFAULT 0,
                citations_stored INTEGER NOT NULL DEFAULT 0,
                images_stored INTEGER NOT NULL DEFAULT 0,
                timeline_events_stored INTEGER NOT NULL DEFAULT 0,
                summary_json TEXT NOT NULL DEFAULT '{}'
            );
            CREATE INDEX IF NOT EXISTS ix_wiki_import_runs_date ON wiki_import_runs(imported_at DESC);
            CREATE INDEX IF NOT EXISTS ix_wiki_import_runs_hash ON wiki_import_runs(package_sha256);

            CREATE TABLE IF NOT EXISTS canonical_topics (
                id TEXT PRIMARY KEY,
                canonical_name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                normalized_key TEXT NOT NULL UNIQUE,
                canonical_wiki_page_id TEXT NULL REFERENCES wiki_pages(id) ON DELETE SET NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS canonical_topic_aliases (
                alias TEXT PRIMARY KEY COLLATE NOCASE,
                normalized_key TEXT NOT NULL,
                topic_id TEXT NOT NULL REFERENCES canonical_topics(id) ON DELETE CASCADE,
                confidence INTEGER NOT NULL DEFAULT 100 CHECK(confidence BETWEEN 0 AND 100),
                merge_kind TEXT NOT NULL DEFAULT 'manual',
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_canonical_topic_aliases_topic ON canonical_topic_aliases(topic_id,alias);
            CREATE INDEX IF NOT EXISTS ix_canonical_topic_aliases_key ON canonical_topic_aliases(normalized_key);
            CREATE TABLE IF NOT EXISTS topic_merge_history (
                id TEXT PRIMARY KEY,
                topic_id TEXT NOT NULL REFERENCES canonical_topics(id) ON DELETE CASCADE,
                canonical_name TEXT NOT NULL,
                aliases_json TEXT NOT NULL,
                reason TEXT NOT NULL DEFAULT '',
                confidence INTEGER NOT NULL DEFAULT 100,
                automatic INTEGER NOT NULL DEFAULT 0,
                affected_research_rows INTEGER NOT NULL DEFAULT 0,
                affected_tag_links INTEGER NOT NULL DEFAULT 0,
                archived_wiki_pages INTEGER NOT NULL DEFAULT 0,
                snapshot_json TEXT NOT NULL DEFAULT '{}',
                created_at TEXT NOT NULL,
                created_by TEXT NOT NULL DEFAULT 'Radio Vault'
            );
            CREATE INDEX IF NOT EXISTS ix_topic_merge_history_date ON topic_merge_history(created_at DESC);
            CREATE TABLE IF NOT EXISTS wiki_page_redirects (
                from_page_id TEXT PRIMARY KEY REFERENCES wiki_pages(id) ON DELETE CASCADE,
                to_page_id TEXT NOT NULL REFERENCES wiki_pages(id) ON DELETE CASCADE,
                merge_history_id TEXT NULL REFERENCES topic_merge_history(id) ON DELETE SET NULL,
                created_at TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureLibraryTruthSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS library_truth_runs (
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
            CREATE INDEX IF NOT EXISTS ix_library_truth_runs_started
                ON library_truth_runs(started_at DESC);

            CREATE TABLE IF NOT EXISTS library_truth_files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL REFERENCES library_truth_runs(id) ON DELETE CASCADE,
                media_file_id INTEGER NOT NULL REFERENCES media_files(id) ON DELETE CASCADE,
                current_episode_id INTEGER NOT NULL REFERENCES episodes(id) ON DELETE CASCADE,
                path TEXT NOT NULL,
                original_filename TEXT NOT NULL,
                current_collection TEXT NOT NULL DEFAULT '',
                current_air_date TEXT NULL,
                current_slot TEXT NOT NULL DEFAULT '',
                current_part INTEGER NOT NULL DEFAULT 1,
                current_total_parts INTEGER NULL,
                proposed_collection TEXT NOT NULL DEFAULT '',
                proposed_air_date TEXT NULL,
                proposed_slot TEXT NOT NULL DEFAULT '',
                proposed_part INTEGER NOT NULL DEFAULT 1,
                proposed_total_parts INTEGER NULL,
                proposed_headline TEXT NOT NULL DEFAULT '',
                canonical_broadcast_key TEXT NOT NULL,
                recording_key TEXT NOT NULL DEFAULT '',
                confidence_score INTEGER NOT NULL DEFAULT 0,
                confidence TEXT NOT NULL DEFAULT 'Unknown',
                disposition TEXT NOT NULL DEFAULT 'Unchanged',
                change_summary TEXT NOT NULL DEFAULT '',
                evidence_json TEXT NOT NULL DEFAULT '[]',
                warnings_json TEXT NOT NULL DEFAULT '[]'
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_library_truth_files_run_media
                ON library_truth_files(run_id,media_file_id);
            CREATE INDEX IF NOT EXISTS ix_library_truth_files_run_disposition
                ON library_truth_files(run_id,disposition,proposed_air_date);
            CREATE INDEX IF NOT EXISTS ix_library_truth_files_run_key
                ON library_truth_files(run_id,canonical_broadcast_key);
            CREATE INDEX IF NOT EXISTS ix_library_truth_files_run_recording
                ON library_truth_files(run_id,recording_key);

            CREATE TABLE IF NOT EXISTS library_truth_broadcasts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL REFERENCES library_truth_runs(id) ON DELETE CASCADE,
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
                evidence_json TEXT NOT NULL DEFAULT '',
                adoption_state TEXT NOT NULL DEFAULT 'Not assessed',
                adoption_reason TEXT NOT NULL DEFAULT '',
                preferred_recording_key TEXT NOT NULL DEFAULT '',
                suspicious_merge INTEGER NOT NULL DEFAULT 0,
                duration_spread_ratio REAL NOT NULL DEFAULT 1.0,
                cross_identity_conflict_count INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_library_truth_broadcasts_run_key
                ON library_truth_broadcasts(run_id,canonical_key);
            CREATE INDEX IF NOT EXISTS ix_library_truth_broadcasts_run_status
                ON library_truth_broadcasts(run_id,status,air_date);

            CREATE TABLE IF NOT EXISTS library_truth_recordings (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL REFERENCES library_truth_runs(id) ON DELETE CASCADE,
                canonical_broadcast_key TEXT NOT NULL,
                recording_key TEXT NOT NULL,
                label TEXT NOT NULL DEFAULT '',
                file_count INTEGER NOT NULL DEFAULT 0,
                segment_count INTEGER NOT NULL DEFAULT 1,
                duration_ms INTEGER NOT NULL DEFAULT 0,
                relationship TEXT NOT NULL DEFAULT 'Single recording',
                confidence_score INTEGER NOT NULL DEFAULT 0,
                evidence_json TEXT NOT NULL DEFAULT '',
                role TEXT NOT NULL DEFAULT 'Unknown',
                completeness_score INTEGER NOT NULL DEFAULT 0,
                preferred_score INTEGER NOT NULL DEFAULT 0,
                duration_ratio REAL NOT NULL DEFAULT 0,
                is_preferred_candidate INTEGER NOT NULL DEFAULT 0,
                review_reason TEXT NOT NULL DEFAULT ''
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_library_truth_recordings_run_key
                ON library_truth_recordings(run_id,recording_key);
            CREATE INDEX IF NOT EXISTS ix_library_truth_recordings_broadcast
                ON library_truth_recordings(run_id,canonical_broadcast_key);
            CREATE TABLE IF NOT EXISTS library_truth_years (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL REFERENCES library_truth_runs(id) ON DELETE CASCADE,
                year_label TEXT NOT NULL,
                physical_file_count INTEGER NOT NULL DEFAULT 0,
                current_broadcast_count INTEGER NOT NULL DEFAULT 0,
                proposed_broadcast_count INTEGER NOT NULL DEFAULT 0,
                merge_groups INTEGER NOT NULL DEFAULT 0,
                split_groups INTEGER NOT NULL DEFAULT 0,
                ready_broadcasts INTEGER NOT NULL DEFAULT 0,
                review_recommended_broadcasts INTEGER NOT NULL DEFAULT 0,
                blocked_broadcasts INTEGER NOT NULL DEFAULT 0,
                UNIQUE(run_id,year_label)
            );

            CREATE TABLE IF NOT EXISTS library_truth_conflicts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL REFERENCES library_truth_runs(id) ON DELETE CASCADE,
                conflict_type TEXT NOT NULL,
                evidence_strength INTEGER NOT NULL DEFAULT 0,
                file_count INTEGER NOT NULL DEFAULT 0,
                identity_count INTEGER NOT NULL DEFAULT 0,
                identities TEXT NOT NULL DEFAULT '',
                evidence TEXT NOT NULL DEFAULT '',
                media_file_ids_json TEXT NOT NULL DEFAULT '[]'
            );
            CREATE INDEX IF NOT EXISTS ix_library_truth_conflicts_run
                ON library_truth_conflicts(run_id,evidence_strength DESC);

            CREATE TABLE IF NOT EXISTS library_truth_coverages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL REFERENCES library_truth_runs(id) ON DELETE CASCADE,
                source_broadcast_key TEXT NOT NULL,
                recording_key TEXT NOT NULL,
                segment_number INTEGER NOT NULL DEFAULT 1,
                segment_total INTEGER NULL,
                target_broadcast_key TEXT NOT NULL,
                coverage_kind TEXT NOT NULL DEFAULT 'Direct segment',
                start_offset_ms INTEGER NOT NULL DEFAULT 0,
                end_offset_ms INTEGER NOT NULL DEFAULT 0,
                confidence_score INTEGER NOT NULL DEFAULT 0,
                requires_review INTEGER NOT NULL DEFAULT 0,
                media_file_ids_json TEXT NOT NULL DEFAULT '[]',
                evidence TEXT NOT NULL DEFAULT '',
                UNIQUE(run_id,recording_key,segment_number,target_broadcast_key,coverage_kind)
            );
            CREATE INDEX IF NOT EXISTS ix_library_truth_coverages_recording
                ON library_truth_coverages(run_id,recording_key,segment_number);
            CREATE INDEX IF NOT EXISTS ix_library_truth_coverages_target
                ON library_truth_coverages(run_id,target_broadcast_key,requires_review);

            CREATE TABLE IF NOT EXISTS library_truth_adoption_previews (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL REFERENCES library_truth_runs(id) ON DELETE CASCADE,
                canonical_key TEXT NOT NULL,
                adoption_state TEXT NOT NULL,
                planned_action TEXT NOT NULL DEFAULT '',
                provisional_episode_id INTEGER NULL,
                current_episode_count INTEGER NOT NULL DEFAULT 0,
                current_episode_ids_json TEXT NOT NULL DEFAULT '[]',
                media_file_count INTEGER NOT NULL DEFAULT 0,
                recording_count INTEGER NOT NULL DEFAULT 0,
                coverage_count INTEGER NOT NULL DEFAULT 0,
                retire_episode_count INTEGER NOT NULL DEFAULT 0,
                reassign_file_count INTEGER NOT NULL DEFAULT 0,
                planned_write_count INTEGER NOT NULL DEFAULT 0,
                eligible_for_guarded_adoption INTEGER NOT NULL DEFAULT 0,
                guard_reason TEXT NOT NULL DEFAULT '',
                evidence TEXT NOT NULL DEFAULT '',
                UNIQUE(run_id,canonical_key)
            );
            CREATE INDEX IF NOT EXISTS ix_library_truth_adoption_previews_state
                ON library_truth_adoption_previews(run_id,eligible_for_guarded_adoption,adoption_state);

            CREATE TABLE IF NOT EXISTS library_truth_rehearsal_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                truth_run_id INTEGER NOT NULL REFERENCES library_truth_runs(id) ON DELETE CASCADE,
                started_at TEXT NOT NULL,
                completed_at TEXT NULL,
                status TEXT NOT NULL DEFAULT 'running',
                backup_path TEXT NOT NULL DEFAULT '',
                source_fingerprint TEXT NOT NULL DEFAULT '',
                rollback_fingerprint TEXT NOT NULL DEFAULT '',
                truth_run_signature TEXT NOT NULL DEFAULT '',
                item_signature TEXT NOT NULL DEFAULT '',
                conflict_signature TEXT NOT NULL DEFAULT '',
                eligible_broadcasts INTEGER NOT NULL DEFAULT 0,
                canonical_writes INTEGER NOT NULL DEFAULT 0,
                recording_writes INTEGER NOT NULL DEFAULT 0,
                segment_writes INTEGER NOT NULL DEFAULT 0,
                coverage_writes INTEGER NOT NULL DEFAULT 0,
                file_reassignments INTEGER NOT NULL DEFAULT 0,
                alias_rows_retired INTEGER NOT NULL DEFAULT 0,
                state_rows_migrated INTEGER NOT NULL DEFAULT 0,
                metadata_conflicts INTEGER NOT NULL DEFAULT 0,
                auto_resolved_conflicts INTEGER NOT NULL DEFAULT 0,
                unresolved_conflicts INTEGER NOT NULL DEFAULT 0,
                preserved_alternates INTEGER NOT NULL DEFAULT 0,
                transcript_conflicts INTEGER NOT NULL DEFAULT 0,
                foreign_key_violations INTEGER NOT NULL DEFAULT 0,
                integrity_check TEXT NOT NULL DEFAULT '',
                backup_restore_check TEXT NOT NULL DEFAULT '',
                rollback_verified INTEGER NOT NULL DEFAULT 0,
                message TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_library_truth_rehearsal_runs_started
                ON library_truth_rehearsal_runs(started_at DESC);

            CREATE TABLE IF NOT EXISTS library_truth_rehearsal_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                rehearsal_run_id INTEGER NOT NULL REFERENCES library_truth_rehearsal_runs(id) ON DELETE CASCADE,
                canonical_key TEXT NOT NULL,
                survivor_episode_id INTEGER NOT NULL,
                alias_episode_ids_json TEXT NOT NULL DEFAULT '[]',
                files_reassigned INTEGER NOT NULL DEFAULT 0,
                state_rows_migrated INTEGER NOT NULL DEFAULT 0,
                metadata_conflicts INTEGER NOT NULL DEFAULT 0,
                auto_resolved_conflicts INTEGER NOT NULL DEFAULT 0,
                unresolved_conflicts INTEGER NOT NULL DEFAULT 0,
                preserved_alternates INTEGER NOT NULL DEFAULT 0,
                transcript_conflicts INTEGER NOT NULL DEFAULT 0,
                outcome TEXT NOT NULL DEFAULT 'Passed',
                evidence TEXT NOT NULL DEFAULT '',
                UNIQUE(rehearsal_run_id,canonical_key)
            );
            CREATE INDEX IF NOT EXISTS ix_library_truth_rehearsal_items_outcome
                ON library_truth_rehearsal_items(rehearsal_run_id,outcome,canonical_key);

            CREATE TABLE IF NOT EXISTS library_truth_rehearsal_conflicts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                rehearsal_run_id INTEGER NOT NULL REFERENCES library_truth_rehearsal_runs(id) ON DELETE CASCADE,
                canonical_key TEXT NOT NULL,
                field_name TEXT NOT NULL,
                conflict_kind TEXT NOT NULL DEFAULT 'Episode metadata',
                classification TEXT NOT NULL DEFAULT '',
                selected_episode_id INTEGER NULL,
                selected_value TEXT NOT NULL DEFAULT '',
                candidate_values_json TEXT NOT NULL DEFAULT '[]',
                provenance_json TEXT NOT NULL DEFAULT '[]',
                resolution TEXT NOT NULL DEFAULT 'manual_review',
                auto_resolved INTEGER NOT NULL DEFAULT 0,
                requires_review INTEGER NOT NULL DEFAULT 1,
                confidence_score INTEGER NOT NULL DEFAULT 0,
                preserved_alternate_count INTEGER NOT NULL DEFAULT 0,
                evidence TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_library_truth_rehearsal_conflicts_review
                ON library_truth_rehearsal_conflicts(rehearsal_run_id,requires_review,canonical_key,field_name);
            CREATE INDEX IF NOT EXISTS ix_library_truth_rehearsal_conflicts_classification
                ON library_truth_rehearsal_conflicts(rehearsal_run_id,classification,field_name);
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureLibraryTruthAdoptionSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS canonical_broadcasts (
                canonical_key TEXT PRIMARY KEY,
                collection_name TEXT NOT NULL,
                air_date TEXT NULL,
                broadcast_slot TEXT NOT NULL DEFAULT '',
                preferred_recording_key TEXT NOT NULL DEFAULT '',
                confidence_score INTEGER NOT NULL DEFAULT 0,
                source_truth_run_id INTEGER NOT NULL,
                adopted_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_canonical_broadcasts_date
                ON canonical_broadcasts(collection_name,air_date,broadcast_slot);

            CREATE TABLE IF NOT EXISTS recordings (
                recording_key TEXT PRIMARY KEY,
                canonical_key TEXT NOT NULL REFERENCES canonical_broadcasts(canonical_key) ON DELETE CASCADE,
                label TEXT NOT NULL DEFAULT '',
                duration_ms INTEGER NOT NULL DEFAULT 0,
                role TEXT NOT NULL DEFAULT 'Unknown',
                completeness_score INTEGER NOT NULL DEFAULT 0,
                preferred_score INTEGER NOT NULL DEFAULT 0,
                is_preferred INTEGER NOT NULL DEFAULT 0,
                source_truth_run_id INTEGER NOT NULL,
                adopted_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_recordings_canonical
                ON recordings(canonical_key,is_preferred,preferred_score DESC);

            CREATE TABLE IF NOT EXISTS recording_segments (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                recording_key TEXT NOT NULL REFERENCES recordings(recording_key) ON DELETE CASCADE,
                segment_number INTEGER NOT NULL,
                segment_total INTEGER NULL,
                start_offset_ms INTEGER NOT NULL DEFAULT 0,
                end_offset_ms INTEGER NOT NULL DEFAULT 0,
                media_file_ids_json TEXT NOT NULL DEFAULT '[]',
                source_truth_run_id INTEGER NOT NULL,
                adopted_at TEXT NOT NULL,
                UNIQUE(recording_key,segment_number)
            );
            CREATE INDEX IF NOT EXISTS ix_recording_segments_recording
                ON recording_segments(recording_key,segment_number);

            CREATE TABLE IF NOT EXISTS recording_coverages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                recording_key TEXT NOT NULL REFERENCES recordings(recording_key) ON DELETE CASCADE,
                segment_number INTEGER NOT NULL,
                target_canonical_key TEXT NOT NULL,
                coverage_kind TEXT NOT NULL DEFAULT 'Direct segment',
                start_offset_ms INTEGER NOT NULL DEFAULT 0,
                end_offset_ms INTEGER NOT NULL DEFAULT 0,
                confidence_score INTEGER NOT NULL DEFAULT 0,
                requires_review INTEGER NOT NULL DEFAULT 0,
                evidence TEXT NOT NULL DEFAULT '',
                source_truth_run_id INTEGER NOT NULL,
                adopted_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_recording_coverages_recording
                ON recording_coverages(recording_key,segment_number);
            CREATE INDEX IF NOT EXISTS ix_recording_coverages_target
                ON recording_coverages(target_canonical_key,requires_review);

            CREATE TABLE IF NOT EXISTS episode_canonical_map (
                episode_id INTEGER PRIMARY KEY REFERENCES episodes(id) ON DELETE CASCADE,
                canonical_key TEXT NOT NULL REFERENCES canonical_broadcasts(canonical_key) ON DELETE CASCADE,
                survivor_episode_id INTEGER NOT NULL REFERENCES episodes(id) ON DELETE CASCADE,
                is_survivor INTEGER NOT NULL DEFAULT 0,
                source_truth_run_id INTEGER NOT NULL,
                adopted_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_episode_canonical_map_key
                ON episode_canonical_map(canonical_key,is_survivor,episode_id);
            CREATE INDEX IF NOT EXISTS ix_episode_canonical_map_survivor
                ON episode_canonical_map(survivor_episode_id,is_survivor);

            CREATE TABLE IF NOT EXISTS library_truth_adoption_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                truth_run_id INTEGER NOT NULL REFERENCES library_truth_runs(id) ON DELETE RESTRICT,
                rehearsal_run_id INTEGER NOT NULL REFERENCES library_truth_rehearsal_runs(id) ON DELETE RESTRICT,
                app_version TEXT NOT NULL DEFAULT '',
                started_at TEXT NOT NULL,
                completed_at TEXT NULL,
                status TEXT NOT NULL DEFAULT 'running',
                backup_path TEXT NOT NULL DEFAULT '',
                source_fingerprint TEXT NOT NULL DEFAULT '',
                staged_fingerprint TEXT NOT NULL DEFAULT '',
                post_commit_fingerprint TEXT NOT NULL DEFAULT '',
                rehearsal_truth_signature TEXT NOT NULL DEFAULT '',
                commit_truth_signature TEXT NOT NULL DEFAULT '',
                rehearsal_item_signature TEXT NOT NULL DEFAULT '',
                commit_item_signature TEXT NOT NULL DEFAULT '',
                rehearsal_conflict_signature TEXT NOT NULL DEFAULT '',
                commit_conflict_signature TEXT NOT NULL DEFAULT '',
                eligible_broadcasts INTEGER NOT NULL DEFAULT 0,
                canonical_writes INTEGER NOT NULL DEFAULT 0,
                recording_writes INTEGER NOT NULL DEFAULT 0,
                segment_writes INTEGER NOT NULL DEFAULT 0,
                coverage_writes INTEGER NOT NULL DEFAULT 0,
                file_reassignments INTEGER NOT NULL DEFAULT 0,
                alias_rows_retired INTEGER NOT NULL DEFAULT 0,
                state_rows_migrated INTEGER NOT NULL DEFAULT 0,
                metadata_conflicts INTEGER NOT NULL DEFAULT 0,
                auto_resolved_conflicts INTEGER NOT NULL DEFAULT 0,
                unresolved_conflicts INTEGER NOT NULL DEFAULT 0,
                preserved_alternates INTEGER NOT NULL DEFAULT 0,
                transcript_conflicts INTEGER NOT NULL DEFAULT 0,
                foreign_key_violations INTEGER NOT NULL DEFAULT 0,
                integrity_check TEXT NOT NULL DEFAULT '',
                backup_restore_check TEXT NOT NULL DEFAULT '',
                commit_verified INTEGER NOT NULL DEFAULT 0,
                message TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_library_truth_adoption_runs_started
                ON library_truth_adoption_runs(started_at DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_library_truth_adoption_completed_truth
                ON library_truth_adoption_runs(truth_run_id)
                WHERE status='completed';

            CREATE TABLE IF NOT EXISTS library_truth_adoption_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                adoption_run_id INTEGER NOT NULL REFERENCES library_truth_adoption_runs(id) ON DELETE CASCADE,
                canonical_key TEXT NOT NULL,
                survivor_episode_id INTEGER NOT NULL,
                alias_episode_ids_json TEXT NOT NULL DEFAULT '[]',
                files_reassigned INTEGER NOT NULL DEFAULT 0,
                state_rows_migrated INTEGER NOT NULL DEFAULT 0,
                metadata_conflicts INTEGER NOT NULL DEFAULT 0,
                auto_resolved_conflicts INTEGER NOT NULL DEFAULT 0,
                unresolved_conflicts INTEGER NOT NULL DEFAULT 0,
                preserved_alternates INTEGER NOT NULL DEFAULT 0,
                transcript_conflicts INTEGER NOT NULL DEFAULT 0,
                outcome TEXT NOT NULL DEFAULT 'Committed',
                evidence TEXT NOT NULL DEFAULT '',
                UNIQUE(adoption_run_id,canonical_key)
            );
            CREATE INDEX IF NOT EXISTS ix_library_truth_adoption_items_outcome
                ON library_truth_adoption_items(adoption_run_id,outcome,canonical_key);

            CREATE TABLE IF NOT EXISTS library_truth_adoption_conflicts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                adoption_run_id INTEGER NOT NULL REFERENCES library_truth_adoption_runs(id) ON DELETE CASCADE,
                canonical_key TEXT NOT NULL,
                field_name TEXT NOT NULL,
                conflict_kind TEXT NOT NULL DEFAULT 'Episode metadata',
                classification TEXT NOT NULL DEFAULT '',
                selected_episode_id INTEGER NULL,
                selected_value TEXT NOT NULL DEFAULT '',
                candidate_values_json TEXT NOT NULL DEFAULT '[]',
                provenance_json TEXT NOT NULL DEFAULT '[]',
                resolution TEXT NOT NULL DEFAULT 'manual_review',
                auto_resolved INTEGER NOT NULL DEFAULT 0,
                requires_review INTEGER NOT NULL DEFAULT 1,
                confidence_score INTEGER NOT NULL DEFAULT 0,
                preserved_alternate_count INTEGER NOT NULL DEFAULT 0,
                evidence TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_library_truth_adoption_conflicts_review
                ON library_truth_adoption_conflicts(adoption_run_id,requires_review,canonical_key,field_name);
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsurePerformanceIndexes(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_episodes_collection_date
                ON episodes(collection_id,air_date,part_number,broadcast_slot);
            CREATE INDEX IF NOT EXISTS ix_episodes_visible_collection_date
                ON episodes(hidden,collection_id,air_date);
            CREATE INDEX IF NOT EXISTS ix_episodes_favourite_date
                ON episodes(favourite,air_date DESC) WHERE favourite=1;
            CREATE INDEX IF NOT EXISTS ix_episodes_status_date
                ON episodes(status,air_date DESC);
            CREATE INDEX IF NOT EXISTS ix_episodes_broadcast_uid
                ON episodes(broadcast_uid) WHERE broadcast_uid IS NOT NULL AND broadcast_uid<>'';

            CREATE INDEX IF NOT EXISTS ix_media_files_episode_preferred
                ON media_files(episode_id,is_preferred,is_missing);
            CREATE INDEX IF NOT EXISTS ix_media_files_missing_path
                ON media_files(is_missing,path);
            CREATE INDEX IF NOT EXISTS ix_media_files_fingerprint
                ON media_files(file_size,partial_hash) WHERE partial_hash IS NOT NULL AND partial_hash<>'';
            CREATE INDEX IF NOT EXISTS ix_media_files_full_hash
                ON media_files(full_hash) WHERE full_hash IS NOT NULL AND full_hash<>'';
            CREATE INDEX IF NOT EXISTS ix_media_files_preservation_missing
                ON media_files(is_missing,storage_state,duration_ms,fingerprinted_at);
            CREATE INDEX IF NOT EXISTS ix_media_files_last_seen
                ON media_files(last_seen_at);

            CREATE INDEX IF NOT EXISTS ix_playback_state_continue
                ON playback_state(completed,last_played_at DESC);
            CREATE INDEX IF NOT EXISTS ix_library_folders_enabled
                ON library_folders(enabled,path);
            CREATE INDEX IF NOT EXISTS ix_scan_runs_started
                ON scan_runs(started_at DESC);
            CREATE INDEX IF NOT EXISTS ix_episode_guests_guest
                ON episode_guests(guest_id,episode_id);
            CREATE INDEX IF NOT EXISTS ix_episode_tags_tag
                ON episode_tags(tag_id,episode_id);
            CREATE INDEX IF NOT EXISTS ix_library_truth_recordings_role
                ON library_truth_recordings(run_id,role,is_preferred_candidate);
            CREATE INDEX IF NOT EXISTS ix_library_truth_coverages_recording
                ON library_truth_coverages(run_id,recording_key,segment_number);
            CREATE INDEX IF NOT EXISTS ix_library_truth_coverages_target
                ON library_truth_coverages(run_id,target_broadcast_key,requires_review);
            CREATE INDEX IF NOT EXISTS ix_library_truth_adoption_previews_state
                ON library_truth_adoption_previews(run_id,eligible_for_guarded_adoption,adoption_state);

            """;
        command.ExecuteNonQuery();
    }

    private void CreateUpgradeBackupIfNeeded()
    {
        if (!File.Exists(DatabasePath) || new FileInfo(DatabasePath).Length == 0) return;

        var schemaVersion = 0L;
        try
        {
            using var probe = CreateConnection();
            probe.Open();
            using var command = probe.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            schemaVersion = Convert.ToInt64(command.ExecuteScalar());
        }
        catch
        {
            // If the existing database cannot be opened, normal initialization will
            // surface the real error. Do not create a misleading partial backup.
            return;
        }

        if (schemaVersion >= CurrentSchemaVersion) return;

        var directory = Path.GetDirectoryName(DatabasePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(DatabasePath);
        var backupPath = Path.Combine(directory, $"{stem}.pre-schema-{CurrentSchemaVersion}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.sqlite");
        using var source = CreateConnection();
        using var destination = new SqliteConnection($"Data Source={backupPath}");
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static void EnsureResearchLibrarySchema(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
        CREATE TABLE IF NOT EXISTS research_import_runs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            package_name TEXT NOT NULL DEFAULT '',
            package_sha256 TEXT NOT NULL DEFAULT '',
            schema_version INTEGER NOT NULL DEFAULT 0,
            app_version TEXT NOT NULL DEFAULT '',
            imported_at TEXT NOT NULL,
            imported_count INTEGER NOT NULL DEFAULT 0,
            matched_count INTEGER NOT NULL DEFAULT 0,
            missing_count INTEGER NOT NULL DEFAULT 0,
            conflict_count INTEGER NOT NULL DEFAULT 0,
            summary_json TEXT NOT NULL DEFAULT '{}',
            rollback_json TEXT NOT NULL DEFAULT '{}',
            status TEXT NOT NULL DEFAULT 'completed',
            restored_change_count INTEGER NOT NULL DEFAULT 0,
            blocked_rollback_count INTEGER NOT NULL DEFAULT 0,
            last_rollback_at TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_research_import_runs_hash
            ON research_import_runs(package_sha256)
            WHERE package_sha256<>'';

        CREATE TABLE IF NOT EXISTS research_import_changes (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            import_run_id INTEGER NOT NULL REFERENCES research_import_runs(id) ON DELETE CASCADE,
            research_broadcast_id INTEGER NULL REFERENCES research_broadcasts(id) ON DELETE SET NULL,
            episode_id INTEGER NULL REFERENCES episodes(id) ON DELETE SET NULL,
            record_identity TEXT NOT NULL DEFAULT '',
            field_name TEXT NOT NULL,
            before_value TEXT NOT NULL DEFAULT '',
            after_value TEXT NOT NULL DEFAULT '',
            decision TEXT NOT NULL DEFAULT 'unchanged'
                CHECK(decision IN ('applied','merged','preserved','protected','unchanged','created','retained_missing','ambiguous')),
            reason TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_research_import_changes_run
            ON research_import_changes(import_run_id,id);
        CREATE INDEX IF NOT EXISTS ix_research_import_changes_episode
            ON research_import_changes(episode_id,import_run_id);

        CREATE TABLE IF NOT EXISTS research_import_rollbacks (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            import_run_id INTEGER NOT NULL REFERENCES research_import_runs(id) ON DELETE CASCADE,
            scope TEXT NOT NULL DEFAULT 'entire_import'
                CHECK(scope IN ('entire_import','record')),
            record_identity TEXT NOT NULL DEFAULT '',
            restored_count INTEGER NOT NULL DEFAULT 0,
            blocked_count INTEGER NOT NULL DEFAULT 0,
            summary_json TEXT NOT NULL DEFAULT '{}',
            created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_research_import_rollbacks_run
            ON research_import_rollbacks(import_run_id,created_at DESC);

        CREATE TABLE IF NOT EXISTS research_import_rollback_changes (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            rollback_id INTEGER NOT NULL REFERENCES research_import_rollbacks(id) ON DELETE CASCADE,
            import_change_id INTEGER NOT NULL REFERENCES research_import_changes(id) ON DELETE CASCADE,
            outcome TEXT NOT NULL CHECK(outcome IN ('restored','blocked')),
            reason TEXT NOT NULL DEFAULT '',
            restored_value TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_research_import_rollback_changes_rollback
            ON research_import_rollback_changes(rollback_id,id);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_research_import_change_restored_once
            ON research_import_rollback_changes(import_change_id)
            WHERE outcome='restored';

        CREATE TABLE IF NOT EXISTS research_field_provenance (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            research_broadcast_id INTEGER NULL REFERENCES research_broadcasts(id) ON DELETE CASCADE,
            episode_id INTEGER NULL REFERENCES episodes(id) ON DELETE CASCADE,
            field_name TEXT NOT NULL,
            value_text TEXT NOT NULL DEFAULT '',
            source_kind TEXT NOT NULL DEFAULT 'system'
                CHECK(source_kind IN ('system','manual','research_pack','rollback')),
            source_label TEXT NOT NULL DEFAULT '',
            import_run_id INTEGER NULL REFERENCES research_import_runs(id) ON DELETE SET NULL,
            confidence INTEGER NOT NULL DEFAULT 0 CHECK(confidence BETWEEN 0 AND 100),
            evidence_count INTEGER NOT NULL DEFAULT 0,
            protected INTEGER NOT NULL DEFAULT 0,
            active INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL,
            superseded_at TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_research_field_provenance_episode
            ON research_field_provenance(episode_id,active,field_name);
        CREATE INDEX IF NOT EXISTS ix_research_field_provenance_research
            ON research_field_provenance(research_broadcast_id,active,field_name);
        CREATE INDEX IF NOT EXISTS ix_research_field_provenance_import
            ON research_field_provenance(import_run_id);

        CREATE TABLE IF NOT EXISTS research_broadcasts (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            identity_key TEXT NOT NULL UNIQUE,
            collection_id INTEGER NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
            episode_id INTEGER NULL REFERENCES episodes(id) ON DELETE SET NULL,
            legacy_missing_research_id INTEGER NULL UNIQUE REFERENCES missing_broadcast_research(id) ON DELETE SET NULL,
            source_broadcast_id TEXT NOT NULL DEFAULT '',
            air_date TEXT NULL,
            slot TEXT NOT NULL DEFAULT '',
            part_number INTEGER NOT NULL DEFAULT 1 CHECK(part_number>=1),
            total_parts INTEGER NULL CHECK(total_parts IS NULL OR total_parts>=1),
            capture_key TEXT NOT NULL DEFAULT 'primary',
            headline TEXT NOT NULL DEFAULT '',
            summary TEXT NOT NULL DEFAULT '',
            station TEXT NOT NULL DEFAULT '',
            edition TEXT NOT NULL DEFAULT '',
            broadcast_variant TEXT NOT NULL DEFAULT '',
            broadcast_era TEXT NOT NULL DEFAULT '',
            episode_type TEXT NOT NULL DEFAULT '',
            archive_notes TEXT NOT NULL DEFAULT '',
            research_json TEXT NOT NULL DEFAULT '{}',
            research_state TEXT NOT NULL DEFAULT 'partially_researched'
                CHECK(research_state IN ('in_library','missing_recording','partially_researched','fully_researched','conflicting_information','alternate_capture','encore_or_replay','special_edition')),
            existence_status TEXT NOT NULL DEFAULT 'unknown_gap'
                CHECK(existence_status IN ('in_library','confirmed_missing','probable_missing','unknown_gap')),
            confidence INTEGER NOT NULL DEFAULT 0 CHECK(confidence BETWEEN 0 AND 100),
            confidence_reason TEXT NOT NULL DEFAULT '',
            user_modified INTEGER NOT NULL DEFAULT 0 CHECK(user_modified IN (0,1)),
            needs_review INTEGER NOT NULL DEFAULT 0 CHECK(needs_review IN (0,1)),
            import_run_id INTEGER NULL REFERENCES research_import_runs(id) ON DELETE SET NULL,
            attached_at TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_research_broadcasts_collection_date
            ON research_broadcasts(collection_id,air_date,slot,part_number);
        CREATE INDEX IF NOT EXISTS ix_research_broadcasts_episode
            ON research_broadcasts(episode_id);
        CREATE INDEX IF NOT EXISTS ix_research_broadcasts_missing
            ON research_broadcasts(existence_status,collection_id,air_date);
        CREATE INDEX IF NOT EXISTS ix_research_broadcasts_review
            ON research_broadcasts(needs_review,research_state);

        CREATE TABLE IF NOT EXISTS research_sources (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            research_broadcast_id INTEGER NOT NULL REFERENCES research_broadcasts(id) ON DELETE CASCADE,
            url TEXT NOT NULL DEFAULT '',
            title TEXT NOT NULL DEFAULT '',
            publisher TEXT NOT NULL DEFAULT '',
            source_type TEXT NOT NULL DEFAULT 'community'
                CHECK(source_type IN ('official','archive_index','community','listening_thread','media_file','user','inference','other')),
            accessed_at TEXT NULL,
            confidence INTEGER NOT NULL DEFAULT 0 CHECK(confidence BETWEEN 0 AND 100),
            supports TEXT NOT NULL DEFAULT '',
            notes TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL,
            UNIQUE(research_broadcast_id,url,title)
        );
        CREATE INDEX IF NOT EXISTS ix_research_sources_broadcast
            ON research_sources(research_broadcast_id);

        CREATE TABLE IF NOT EXISTS research_people (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            research_broadcast_id INTEGER NOT NULL REFERENCES research_broadcasts(id) ON DELETE CASCADE,
            name TEXT NOT NULL,
            role TEXT NOT NULL CHECK(role IN ('host','guest','caller','mentioned')),
            confidence INTEGER NOT NULL DEFAULT 0 CHECK(confidence BETWEEN 0 AND 100),
            source_id INTEGER NULL REFERENCES research_sources(id) ON DELETE SET NULL,
            notes TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL,
            UNIQUE(research_broadcast_id,name,role)
        );
        CREATE INDEX IF NOT EXISTS ix_research_people_name ON research_people(name COLLATE NOCASE);

        CREATE TABLE IF NOT EXISTS research_topics (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            research_broadcast_id INTEGER NOT NULL REFERENCES research_broadcasts(id) ON DELETE CASCADE,
            topic TEXT NOT NULL,
            confidence INTEGER NOT NULL DEFAULT 0 CHECK(confidence BETWEEN 0 AND 100),
            source_id INTEGER NULL REFERENCES research_sources(id) ON DELETE SET NULL,
            notes TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL,
            UNIQUE(research_broadcast_id,topic)
        );
        CREATE INDEX IF NOT EXISTS ix_research_topics_topic ON research_topics(topic COLLATE NOCASE);

        CREATE TABLE IF NOT EXISTS research_moments (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            research_broadcast_id INTEGER NOT NULL REFERENCES research_broadcasts(id) ON DELETE CASCADE,
            timestamp_seconds INTEGER NOT NULL DEFAULT 0 CHECK(timestamp_seconds>=0),
            title TEXT NOT NULL DEFAULT '',
            description TEXT NOT NULL DEFAULT '',
            tags TEXT NOT NULL DEFAULT '',
            confidence INTEGER NOT NULL DEFAULT 0 CHECK(confidence BETWEEN 0 AND 100),
            source_id INTEGER NULL REFERENCES research_sources(id) ON DELETE SET NULL,
            created_at TEXT NOT NULL,
            UNIQUE(research_broadcast_id,timestamp_seconds,title)
        );

        CREATE TABLE IF NOT EXISTS research_aliases (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            research_broadcast_id INTEGER NOT NULL REFERENCES research_broadcasts(id) ON DELETE CASCADE,
            alias_type TEXT NOT NULL DEFAULT 'filename'
                CHECK(alias_type IN ('filename','broadcast_id','date_label','capture','other')),
            alias_value TEXT NOT NULL,
            confidence INTEGER NOT NULL DEFAULT 0 CHECK(confidence BETWEEN 0 AND 100),
            UNIQUE(research_broadcast_id,alias_type,alias_value)
        );
        CREATE INDEX IF NOT EXISTS ix_research_aliases_value ON research_aliases(alias_value COLLATE NOCASE);

        CREATE TABLE IF NOT EXISTS research_conflicts (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            research_broadcast_id INTEGER NOT NULL REFERENCES research_broadcasts(id) ON DELETE CASCADE,
            episode_id INTEGER NULL REFERENCES episodes(id) ON DELETE SET NULL,
            field_name TEXT NOT NULL,
            existing_value TEXT NOT NULL DEFAULT '',
            incoming_value TEXT NOT NULL DEFAULT '',
            existing_source TEXT NOT NULL DEFAULT '',
            incoming_source TEXT NOT NULL DEFAULT '',
            resolution TEXT NOT NULL DEFAULT 'unresolved'
                CHECK(resolution IN ('unresolved','keep_existing','use_incoming','merge','ignored')),
            created_at TEXT NOT NULL,
            resolved_at TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_research_conflicts_unresolved
            ON research_conflicts(resolution,research_broadcast_id);

        CREATE TABLE IF NOT EXISTS research_reconciliation_candidates (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            research_broadcast_id INTEGER NOT NULL REFERENCES research_broadcasts(id) ON DELETE CASCADE,
            episode_id INTEGER NOT NULL REFERENCES episodes(id) ON DELETE CASCADE,
            score INTEGER NOT NULL CHECK(score BETWEEN 0 AND 100),
            reason TEXT NOT NULL DEFAULT '',
            status TEXT NOT NULL DEFAULT 'pending'
                CHECK(status IN ('pending','approved','rejected')),
            requires_review INTEGER NOT NULL DEFAULT 1,
            review_category TEXT NOT NULL DEFAULT 'ambiguous_match',
            recommended_action TEXT NOT NULL DEFAULT '',
            decision_source TEXT NOT NULL DEFAULT 'manual',
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(research_broadcast_id,episode_id)
        );
        CREATE INDEX IF NOT EXISTS ix_research_candidates_status
            ON research_reconciliation_candidates(status,score DESC);

        CREATE TABLE IF NOT EXISTS research_reconciliation_actions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            candidate_id INTEGER NOT NULL REFERENCES research_reconciliation_candidates(id) ON DELETE CASCADE,
            research_broadcast_id INTEGER NOT NULL REFERENCES research_broadcasts(id) ON DELETE CASCADE,
            episode_id INTEGER NOT NULL REFERENCES episodes(id) ON DELETE CASCADE,
            action TEXT NOT NULL CHECK(action IN ('approved','rejected')),
            options_json TEXT NOT NULL DEFAULT '{}',
            change_json TEXT NOT NULL DEFAULT '{}',
            decision_source TEXT NOT NULL DEFAULT 'manual',
            created_at TEXT NOT NULL,
            undone_at TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_research_reconciliation_actions_candidate
            ON research_reconciliation_actions(candidate_id,created_at DESC);
        CREATE INDEX IF NOT EXISTS ix_research_reconciliation_actions_active
            ON research_reconciliation_actions(candidate_id,action,undone_at,id DESC);

        CREATE TABLE IF NOT EXISTS research_quality_actions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            research_broadcast_id INTEGER NOT NULL REFERENCES research_broadcasts(id) ON DELETE CASCADE,
            episode_id INTEGER NULL REFERENCES episodes(id) ON DELETE SET NULL,
            rule_id TEXT NOT NULL,
            fix_kind TEXT NOT NULL,
            fix_value TEXT NOT NULL DEFAULT '',
            before_json TEXT NOT NULL,
            after_json TEXT NOT NULL,
            created_at TEXT NOT NULL,
            undone_at TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_research_quality_actions_active
            ON research_quality_actions(research_broadcast_id,undone_at,id DESC);

        CREATE TRIGGER IF NOT EXISTS trg_research_broadcasts_updated_at
        AFTER UPDATE ON research_broadcasts
        FOR EACH ROW
        WHEN NEW.updated_at=OLD.updated_at
        BEGIN
            UPDATE research_broadcasts
               SET updated_at=strftime('%Y-%m-%dT%H:%M:%fZ','now')
             WHERE id=NEW.id;
        END;

        CREATE VIEW IF NOT EXISTS v_research_library_overview AS
        SELECT
            rb.collection_id,
            COUNT(*) AS total_research_records,
            SUM(CASE WHEN rb.episode_id IS NOT NULL THEN 1 ELSE 0 END) AS attached_records,
            SUM(CASE WHEN rb.existence_status='confirmed_missing' THEN 1 ELSE 0 END) AS confirmed_missing,
            SUM(CASE WHEN rb.existence_status='probable_missing' THEN 1 ELSE 0 END) AS probable_missing,
            SUM(CASE WHEN rb.existence_status='unknown_gap' AND rb.episode_id IS NULL THEN 1 ELSE 0 END) AS unknown_gaps,
            SUM(CASE WHEN rb.needs_review=1 THEN 1 ELSE 0 END) AS needs_review,
            SUM(CASE WHEN EXISTS(
                SELECT 1 FROM research_conflicts rc
                WHERE rc.research_broadcast_id=rb.id AND rc.resolution='unresolved'
            ) THEN 1 ELSE 0 END) AS conflicted_records
        FROM research_broadcasts rb
        GROUP BY rb.collection_id;

        CREATE VIEW IF NOT EXISTS v_missing_broadcasts AS
        SELECT
            rb.id,rb.collection_id,rb.identity_key,rb.source_broadcast_id,rb.air_date,
            rb.slot,rb.part_number,rb.headline,rb.summary,rb.existence_status,
            rb.confidence,rb.confidence_reason,rb.needs_review,
            (SELECT COUNT(*) FROM research_sources rs WHERE rs.research_broadcast_id=rb.id) AS source_count,
            (SELECT COUNT(*) FROM research_topics rt WHERE rt.research_broadcast_id=rb.id) AS topic_count,
            (SELECT COUNT(*) FROM research_people rp WHERE rp.research_broadcast_id=rb.id) AS people_count
        FROM research_broadcasts rb
        WHERE rb.episode_id IS NULL AND rb.existence_status<>'in_library';
        """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void EnsureTranscriptionSchema(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
        CREATE TABLE IF NOT EXISTS transcripts (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            episode_id INTEGER NOT NULL UNIQUE REFERENCES episodes(id) ON DELETE CASCADE,
            status TEXT NOT NULL DEFAULT 'Complete'
                CHECK(status IN ('Draft','Complete','Failed')),
            language TEXT NOT NULL DEFAULT '',
            engine_id TEXT NOT NULL DEFAULT '',
            engine_version TEXT NOT NULL DEFAULT '',
            model_id TEXT NOT NULL DEFAULT '',
            source TEXT NOT NULL DEFAULT 'local'
                CHECK(source IN ('local','import','manual','shared')),
            full_text TEXT NOT NULL DEFAULT '',
            word_count INTEGER NOT NULL DEFAULT 0 CHECK(word_count>=0),
            duration_ms INTEGER NOT NULL DEFAULT 0 CHECK(duration_ms>=0),
            has_word_timings INTEGER NOT NULL DEFAULT 0 CHECK(has_word_timings IN (0,1)),
            has_speaker_diarization INTEGER NOT NULL DEFAULT 0 CHECK(has_speaker_diarization IN (0,1)),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            completed_at TEXT NULL,
            revision INTEGER NOT NULL DEFAULT 1 CHECK(revision>=1),
            metadata_json TEXT NOT NULL DEFAULT '{}'
        );
        CREATE INDEX IF NOT EXISTS ix_transcripts_status_updated
            ON transcripts(status,updated_at DESC);
        CREATE INDEX IF NOT EXISTS ix_transcripts_engine
            ON transcripts(engine_id,model_id);

        CREATE TABLE IF NOT EXISTS transcript_segments (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            transcript_id INTEGER NOT NULL REFERENCES transcripts(id) ON DELETE CASCADE,
            segment_index INTEGER NOT NULL CHECK(segment_index>=0),
            start_ms INTEGER NOT NULL CHECK(start_ms>=0),
            end_ms INTEGER NOT NULL CHECK(end_ms>=start_ms),
            speaker TEXT NOT NULL DEFAULT '',
            speaker_key TEXT NOT NULL DEFAULT '',
            text TEXT NOT NULL,
            confidence REAL NULL,
            words_json TEXT NOT NULL DEFAULT '[]',
            content_kind TEXT NOT NULL DEFAULT 'Speech',
            is_reviewed INTEGER NOT NULL DEFAULT 0 CHECK(is_reviewed IN (0,1)),
            UNIQUE(transcript_id,segment_index)
        );
        CREATE INDEX IF NOT EXISTS ix_transcript_segments_time
            ON transcript_segments(transcript_id,start_ms,end_ms);

        CREATE TABLE IF NOT EXISTS transcription_jobs (
            job_id TEXT PRIMARY KEY,
            episode_id INTEGER NOT NULL REFERENCES episodes(id) ON DELETE CASCADE,
            state TEXT NOT NULL
                CHECK(state IN ('Queued','Running','Completed','Failed','Cancelled','Interrupted')),
            engine_id TEXT NOT NULL DEFAULT '',
            model_id TEXT NOT NULL DEFAULT '',
            progress_percent REAL NULL,
            message TEXT NOT NULL DEFAULT '',
            error TEXT NOT NULL DEFAULT '',
            requested_at TEXT NOT NULL,
            started_at TEXT NULL,
            finished_at TEXT NULL,
            background_job_id TEXT NULL,
            language TEXT NOT NULL DEFAULT '',
            start_ms INTEGER NOT NULL DEFAULT 0 CHECK(start_ms>=0),
            duration_ms INTEGER NULL CHECK(duration_ms IS NULL OR duration_ms>0),
            enable_speaker_diarization INTEGER NOT NULL DEFAULT 0 CHECK(enable_speaker_diarization IN (0,1)),
            use_vad INTEGER NOT NULL DEFAULT 0 CHECK(use_vad IN (0,1)),
            replace_existing INTEGER NOT NULL DEFAULT 0 CHECK(replace_existing IN (0,1))
        );
        CREATE INDEX IF NOT EXISTS ix_transcription_jobs_state_requested
            ON transcription_jobs(state,requested_at DESC);
        CREATE INDEX IF NOT EXISTS ix_transcription_jobs_episode
            ON transcription_jobs(episode_id,requested_at DESC);

        CREATE TABLE IF NOT EXISTS transcription_batches (
            batch_id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            selection_label TEXT NOT NULL DEFAULT '',
            state TEXT NOT NULL CHECK(state IN ('Queued','Running','Paused','Completed','CompletedWithErrors','Cancelled','Interrupted')),
            language TEXT NOT NULL DEFAULT '',
            model_id TEXT NOT NULL DEFAULT '',
            enable_speaker_diarization INTEGER NOT NULL DEFAULT 0 CHECK(enable_speaker_diarization IN (0,1)),
            use_vad INTEGER NOT NULL DEFAULT 0 CHECK(use_vad IN (0,1)),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            started_at TEXT NULL,
            finished_at TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_transcription_batches_state_created
            ON transcription_batches(state,created_at DESC);

        CREATE TABLE IF NOT EXISTS transcription_batch_items (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            batch_id TEXT NOT NULL REFERENCES transcription_batches(batch_id) ON DELETE CASCADE,
            episode_id INTEGER NOT NULL REFERENCES episodes(id) ON DELETE CASCADE,
            position INTEGER NOT NULL CHECK(position>=0),
            state TEXT NOT NULL CHECK(state IN ('Pending','Running','Completed','Failed','Skipped','Cancelled')),
            transcription_job_id TEXT NULL REFERENCES transcription_jobs(job_id) ON DELETE SET NULL,
            error TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(batch_id,episode_id),
            UNIQUE(batch_id,position)
        );
        CREATE INDEX IF NOT EXISTS ix_transcription_batch_items_batch_state
            ON transcription_batch_items(batch_id,state,position);

        CREATE TABLE IF NOT EXISTS transcript_imports (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            episode_id INTEGER NOT NULL REFERENCES episodes(id) ON DELETE CASCADE,
            package_id TEXT NOT NULL,
            source_path TEXT NOT NULL DEFAULT '',
            checksum TEXT NOT NULL DEFAULT '',
            imported_at TEXT NOT NULL,
            replaced_revision INTEGER NOT NULL DEFAULT 0,
            UNIQUE(episode_id,package_id)
        );
        CREATE INDEX IF NOT EXISTS ix_transcript_imports_episode
            ON transcript_imports(episode_id,imported_at DESC);

        CREATE TABLE IF NOT EXISTS voice_people (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            canonical_name TEXT NOT NULL,
            normalized_name TEXT NOT NULL UNIQUE,
            aliases_json TEXT NOT NULL DEFAULT '[]',
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_voice_people_name
            ON voice_people(canonical_name COLLATE NOCASE);

        CREATE TABLE IF NOT EXISTS transcript_speakers (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            transcript_id INTEGER NOT NULL REFERENCES transcripts(id) ON DELETE CASCADE,
            speaker_key TEXT NOT NULL,
            label TEXT NOT NULL DEFAULT '',
            segment_count INTEGER NOT NULL DEFAULT 0 CHECK(segment_count>=0),
            speaking_duration_ms INTEGER NOT NULL DEFAULT 0 CHECK(speaking_duration_ms>=0),
            voice_person_id INTEGER NULL REFERENCES voice_people(id) ON DELETE SET NULL,
            assignment_state TEXT NOT NULL DEFAULT 'Unassigned'
                CHECK(assignment_state IN ('Unassigned','Suggested','Confirmed')),
            assignment_confidence REAL NULL,
            assignment_source TEXT NOT NULL DEFAULT '',
            train_voice INTEGER NOT NULL DEFAULT 1 CHECK(train_voice IN (0,1)),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(transcript_id,speaker_key)
        );
        CREATE INDEX IF NOT EXISTS ix_transcript_speakers_person
            ON transcript_speakers(voice_person_id,assignment_state);
        CREATE INDEX IF NOT EXISTS ix_transcript_speakers_transcript
            ON transcript_speakers(transcript_id,speaker_key);

        CREATE TABLE IF NOT EXISTS voice_profiles (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            voice_person_id INTEGER NOT NULL REFERENCES voice_people(id) ON DELETE CASCADE,
            embedding_model_id TEXT NOT NULL,
            embedding_model_version TEXT NOT NULL DEFAULT '',
            embedding_dimensions INTEGER NOT NULL DEFAULT 0 CHECK(embedding_dimensions>=0),
            centroid_json TEXT NOT NULL DEFAULT '[]',
            sample_count INTEGER NOT NULL DEFAULT 0 CHECK(sample_count>=0),
            average_quality REAL NULL,
            profile_revision INTEGER NOT NULL DEFAULT 1 CHECK(profile_revision>=1),
            active INTEGER NOT NULL DEFAULT 1 CHECK(active IN (0,1)),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(voice_person_id,embedding_model_id,embedding_model_version)
        );
        CREATE INDEX IF NOT EXISTS ix_voice_profiles_model
            ON voice_profiles(embedding_model_id,active,updated_at DESC);

        CREATE TABLE IF NOT EXISTS voice_samples (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            voice_person_id INTEGER NOT NULL REFERENCES voice_people(id) ON DELETE CASCADE,
            episode_id INTEGER NOT NULL REFERENCES episodes(id) ON DELETE CASCADE,
            transcript_id INTEGER NOT NULL REFERENCES transcripts(id) ON DELETE CASCADE,
            speaker_key TEXT NOT NULL,
            start_ms INTEGER NOT NULL CHECK(start_ms>=0),
            end_ms INTEGER NOT NULL CHECK(end_ms>=start_ms),
            state TEXT NOT NULL DEFAULT 'Pending'
                CHECK(state IN ('Pending','Ready','Rejected','Failed')),
            embedding_model_id TEXT NOT NULL DEFAULT '',
            embedding_model_version TEXT NOT NULL DEFAULT '',
            embedding_json TEXT NOT NULL DEFAULT '',
            quality_score REAL NULL,
            confirmed_by_user INTEGER NOT NULL DEFAULT 1 CHECK(confirmed_by_user IN (0,1)),
            error TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL,
            processed_at TEXT NULL,
            UNIQUE(voice_person_id,episode_id,transcript_id,speaker_key,start_ms,end_ms)
        );
        CREATE INDEX IF NOT EXISTS ix_voice_samples_pending
            ON voice_samples(state,created_at,id);
        CREATE INDEX IF NOT EXISTS ix_voice_samples_person
            ON voice_samples(voice_person_id,state,created_at DESC);

        CREATE TABLE IF NOT EXISTS speaker_match_suggestions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            transcript_speaker_id INTEGER NOT NULL REFERENCES transcript_speakers(id) ON DELETE CASCADE,
            voice_person_id INTEGER NOT NULL REFERENCES voice_people(id) ON DELETE CASCADE,
            confidence REAL NOT NULL CHECK(confidence>=0 AND confidence<=1),
            distance REAL NULL,
            embedding_model_id TEXT NOT NULL DEFAULT '',
            profile_revision INTEGER NOT NULL DEFAULT 0,
            status TEXT NOT NULL DEFAULT 'pending'
                CHECK(status IN ('pending','accepted','rejected','expired')),
            created_at TEXT NOT NULL,
            UNIQUE(transcript_speaker_id,voice_person_id,embedding_model_id,profile_revision)
        );
        CREATE INDEX IF NOT EXISTS ix_speaker_match_suggestions_cluster
            ON speaker_match_suggestions(transcript_speaker_id,status,confidence DESC);

        UPDATE transcription_jobs
           SET state='Interrupted',
               message='Radio Vault closed before this transcription job completed.',
               finished_at=COALESCE(finished_at,strftime('%Y-%m-%dT%H:%M:%fZ','now'))
         WHERE state IN ('Queued','Running');
        """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        IDictionary<string, HashSet<string>> columnCache,
        string table,
        string column,
        string definition)
    {
        if (!columnCache.TryGetValue(table, out var columns))
        {
            columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var check = connection.CreateCommand();
            check.CommandText = $"PRAGMA table_info({table})";
            using var reader = check.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
            columnCache[table] = columns;
        }

        if (columns.Contains(column)) return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
        columns.Add(column);
    }

    private static void SeedCollection(SqliteConnection connection, string name, string sortName, IEnumerable<string> aliases)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT OR IGNORE INTO collections(name, sort_name) VALUES ($name, $sort);";
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$sort", sortName);
        insert.ExecuteNonQuery();

        using var idCommand = connection.CreateCommand();
        idCommand.CommandText = "SELECT id FROM collections WHERE name=$name";
        idCommand.Parameters.AddWithValue("$name", name);
        var id = Convert.ToInt32(idCommand.ExecuteScalar());

        foreach (var alias in aliases)
        {
            using var aliasCommand = connection.CreateCommand();
            aliasCommand.CommandText = "INSERT OR IGNORE INTO collection_aliases(collection_id,alias) VALUES($id,$alias)";
            aliasCommand.Parameters.AddWithValue("$id", id);
            aliasCommand.Parameters.AddWithValue("$alias", alias);
            aliasCommand.ExecuteNonQuery();
        }
    }
}
