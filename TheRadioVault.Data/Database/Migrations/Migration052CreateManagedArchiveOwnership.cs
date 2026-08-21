using Microsoft.Data.Sqlite;

namespace TheRadioVault.Data.Database.Migrations;

internal sealed class Migration052CreateManagedArchiveOwnership : ISqliteMigration
{
    public int Version => 52;
    public string Name => "Create managed archive ownership";

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        AddColumnIfMissing(
            connection,
            transaction,
            "library_folders",
            "is_managed_archive",
            "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(
            connection,
            transaction,
            "rss_feed_subscriptions",
            "collection_id",
            "INTEGER NULL REFERENCES collections(id)");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS managed_archive_state (
                id INTEGER PRIMARY KEY CHECK(id=1),
                library_folder_id INTEGER NOT NULL REFERENCES library_folders(id) ON DELETE RESTRICT,
                managed_root TEXT NOT NULL,
                quarantine_root TEXT NOT NULL DEFAULT '',
                consolidated_at TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_library_folders_managed_archive
                ON library_folders(is_managed_archive) WHERE is_managed_archive=1;
            """;
        command.ExecuteNonQuery();
    }

    private static void AddColumnIfMissing(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string definition)
    {
        using (var inspect = connection.CreateCommand())
        {
            inspect.Transaction = transaction;
            inspect.CommandText = $"PRAGMA table_info({table});";
            using var reader = inspect.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }
}
