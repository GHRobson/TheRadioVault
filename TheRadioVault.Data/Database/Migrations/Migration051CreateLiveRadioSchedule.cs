using Microsoft.Data.Sqlite;

namespace TheRadioVault.Data.Database.Migrations;

internal sealed class Migration051CreateLiveRadioSchedule : ISqliteMigration
{
    public int Version => 51;
    public string Name => "Create Radio Vault Live schedule";

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE live_radio_schedule_entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                station_key TEXT NOT NULL,
                schedule_date TEXT NOT NULL,
                starts_at TEXT NOT NULL,
                ends_at TEXT NOT NULL,
                episode_id INTEGER NOT NULL REFERENCES episodes(id) ON DELETE CASCADE,
                canonical_key TEXT NOT NULL,
                selection_reason TEXT NOT NULL,
                generated_at TEXT NOT NULL,
                UNIQUE(station_key,starts_at),
                CHECK(ends_at > starts_at)
            );
            CREATE INDEX ix_live_radio_schedule_current
                ON live_radio_schedule_entries(station_key,starts_at,ends_at);
            CREATE INDEX ix_live_radio_schedule_episode
                ON live_radio_schedule_entries(episode_id,starts_at DESC);

            CREATE TABLE live_radio_show_rules (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                station_key TEXT NOT NULL DEFAULT 'main',
                collection_name TEXT NOT NULL COLLATE NOCASE,
                effective_from TEXT NULL,
                effective_to TEXT NULL,
                weekdays_mask INTEGER NOT NULL DEFAULT 127 CHECK(weekdays_mask BETWEEN 1 AND 127),
                start_minute INTEGER NOT NULL CHECK(start_minute BETWEEN 0 AND 1439),
                end_minute INTEGER NOT NULL CHECK(end_minute BETWEEN 1 AND 1440),
                time_zone_id TEXT NOT NULL,
                source TEXT NOT NULL DEFAULT '',
                confidence INTEGER NOT NULL DEFAULT 50 CHECK(confidence BETWEEN 0 AND 100),
                enabled INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                CHECK(end_minute > start_minute)
            );
            CREATE INDEX ix_live_radio_show_rules_active
                ON live_radio_show_rules(station_key,enabled,collection_name);
            """;
        command.ExecuteNonQuery();
    }
}
