using Microsoft.Data.Sqlite;

namespace TheRadioVault.Data.Database.Migrations;

internal sealed class Migration049CreateSavedCollections : ISqliteMigration
{
    public int Version => 49;
    public string Name => "Create saved collections";

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE saved_collections (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL COLLATE NOCASE,
                kind TEXT NOT NULL CHECK(kind IN ('Manual','Smart')),
                smart_rule_json TEXT,
                revision INTEGER NOT NULL DEFAULT 1 CHECK(revision > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                CHECK((kind='Manual' AND smart_rule_json IS NULL) OR
                      (kind='Smart' AND smart_rule_json IS NOT NULL))
            );
            CREATE UNIQUE INDEX ux_saved_collections_name
                ON saved_collections(name COLLATE NOCASE);
            CREATE INDEX ix_saved_collections_updated
                ON saved_collections(updated_at DESC,id);

            CREATE TABLE saved_collection_items (
                collection_id INTEGER NOT NULL REFERENCES saved_collections(id) ON DELETE CASCADE,
                episode_id INTEGER NOT NULL REFERENCES episodes(id) ON DELETE CASCADE,
                item_position INTEGER NOT NULL CHECK(item_position >= 0),
                added_at TEXT NOT NULL,
                PRIMARY KEY(collection_id,episode_id)
            );
            CREATE INDEX ix_saved_collection_items_order
                ON saved_collection_items(collection_id,item_position,episode_id);
            CREATE INDEX ix_saved_collection_items_episode
                ON saved_collection_items(episode_id,collection_id);
            """;
        command.ExecuteNonQuery();
    }
}
