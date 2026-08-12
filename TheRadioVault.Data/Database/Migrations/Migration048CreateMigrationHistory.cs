using Microsoft.Data.Sqlite;

namespace TheRadioVault.Data.Database.Migrations;

internal sealed class Migration048CreateMigrationHistory : ISqliteMigration
{
    public int Version => 48;
    public string Name => "Create migration history";

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }
}
