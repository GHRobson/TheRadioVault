using Microsoft.Data.Sqlite;

namespace TheRadioVault.Data.Database.Migrations;

internal sealed class Migration050CreateRssFeedSubscriptions : ISqliteMigration
{
    public int Version => 50;
    public string Name => "Create RSS feed subscriptions";

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE rss_feed_subscriptions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                display_url TEXT NOT NULL,
                protected_source TEXT NOT NULL,
                library_folder_id INTEGER NOT NULL REFERENCES library_folders(id) ON DELETE RESTRICT,
                check_interval_minutes INTEGER NOT NULL DEFAULT 30 CHECK(check_interval_minutes BETWEEN 5 AND 10080),
                enabled INTEGER NOT NULL DEFAULT 1,
                import_existing_on_first_check INTEGER NOT NULL DEFAULT 0,
                initialized INTEGER NOT NULL DEFAULT 0,
                etag TEXT NULL,
                last_modified TEXT NULL,
                last_checked_at TEXT NULL,
                last_success_at TEXT NULL,
                next_check_at TEXT NULL,
                last_error TEXT NOT NULL DEFAULT '',
                downloaded_count INTEGER NOT NULL DEFAULT 0,
                seen_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX ix_rss_feed_subscriptions_due
                ON rss_feed_subscriptions(enabled, next_check_at);

            CREATE TABLE rss_feed_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                feed_id INTEGER NOT NULL REFERENCES rss_feed_subscriptions(id) ON DELETE CASCADE,
                stable_key TEXT NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                published_at TEXT NULL,
                enclosure_hash TEXT NOT NULL,
                file_name TEXT NULL,
                file_path TEXT NULL,
                content_hash TEXT NULL,
                status TEXT NOT NULL DEFAULT 'Pending',
                last_error TEXT NOT NULL DEFAULT '',
                first_seen_at TEXT NOT NULL,
                downloaded_at TEXT NULL,
                UNIQUE(feed_id, stable_key)
            );
            CREATE INDEX ix_rss_feed_items_feed_status
                ON rss_feed_items(feed_id, status);
            CREATE INDEX ix_rss_feed_items_content_hash
                ON rss_feed_items(content_hash) WHERE content_hash IS NOT NULL AND content_hash<>'';
            """;
        command.ExecuteNonQuery();
    }
}
