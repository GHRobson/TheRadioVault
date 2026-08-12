using Microsoft.Data.Sqlite;

namespace TheRadioVault.Data.Database.Migrations;

/// <summary>
/// One forward-only SQLite schema transition. Implementations must contain
/// only the change for their version and must use the supplied transaction.
/// </summary>
public interface ISqliteMigration
{
    int Version { get; }
    string Name { get; }

    void Apply(SqliteConnection connection, SqliteTransaction transaction);
}
