using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TheRadioVault.Data.Database.Migrations;

public sealed record AppliedSqliteMigration(int Version, string Name);

/// <summary>
/// Validates and applies forward-only migrations after the legacy schema
/// bootstrap. Every migration and its version/history update commit together.
/// </summary>
public sealed class SqliteMigrationRunner
{
    private readonly IReadOnlyList<ISqliteMigration> _migrations;

    public SqliteMigrationRunner(int baselineVersion, IEnumerable<ISqliteMigration> migrations)
    {
        if (baselineVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(baselineVersion));

        BaselineVersion = baselineVersion;
        _migrations = migrations?.OrderBy(migration => migration.Version).ToArray()
            ?? throw new ArgumentNullException(nameof(migrations));

        var expectedVersion = baselineVersion + 1;
        foreach (var migration in _migrations)
        {
            if (migration.Version != expectedVersion)
                throw new InvalidOperationException(
                    $"SQLite migrations must be contiguous after schema {baselineVersion}; " +
                    $"expected {expectedVersion}, found {migration.Version}.");
            if (string.IsNullOrWhiteSpace(migration.Name))
                throw new InvalidOperationException($"SQLite migration {migration.Version} requires a name.");

            expectedVersion++;
        }

        CurrentVersion = _migrations.Count == 0 ? baselineVersion : _migrations[^1].Version;
    }

    public int BaselineVersion { get; }
    public int CurrentVersion { get; }
    public IReadOnlyList<ISqliteMigration> Migrations => _migrations;

    public void EnsureCompatible(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var databaseVersion = ReadVersion(connection);
        if (databaseVersion > CurrentVersion)
        {
            throw new InvalidOperationException(
                $"This Radio Vault build supports database schema {CurrentVersion}, " +
                $"but the database uses newer schema {databaseVersion}.");
        }
    }

    public IReadOnlyList<AppliedSqliteMigration> ApplyPending(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        EnsureCompatible(connection);

        var databaseVersion = ReadVersion(connection);
        if (databaseVersion < BaselineVersion)
        {
            throw new InvalidOperationException(
                $"The legacy database bootstrap must reach schema {BaselineVersion} before numbered migrations run; " +
                $"the database is at schema {databaseVersion}.");
        }

        var applied = new List<AppliedSqliteMigration>();
        foreach (var migration in _migrations.Where(candidate => candidate.Version > databaseVersion))
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                migration.Apply(connection, transaction);
                RecordMigration(connection, transaction, migration);
                SetVersion(connection, transaction, migration.Version);
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            databaseVersion = migration.Version;
            applied.Add(new AppliedSqliteMigration(migration.Version, migration.Name));
        }

        return applied;
    }

    public static int ReadVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void SetVersion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }

    private static void RecordMigration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ISqliteMigration migration)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO schema_migrations(version,name,applied_at)
            VALUES($version,$name,$appliedAt);
            """;
        command.Parameters.AddWithValue("$version", migration.Version);
        command.Parameters.AddWithValue("$name", migration.Name);
        command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }
}
