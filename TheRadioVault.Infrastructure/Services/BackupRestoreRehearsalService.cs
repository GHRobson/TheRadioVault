using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace TheRadioVault.Services;

public sealed record BackupRestoreRehearsalResult(
    bool CanRestore,
    string QuickCheck,
    int ForeignKeyViolations,
    int SchemaVersion,
    int TableCount,
    long BroadcastCount,
    long DatabaseBytes,
    int ArtworkFiles,
    long ArtworkBytes,
    string Message);

/// <summary>
/// Performs a real clean-server restore into disposable storage and validates
/// the resulting SQLite archive before any live database is replaced.
/// </summary>
public sealed class BackupRestoreRehearsalService
{
    private const int MaximumEntries = 100_000;
    private const long MaximumExpandedBytes = 64L * 1024 * 1024 * 1024;

    public BackupRestoreRehearsalResult Rehearse(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
            throw new ArgumentException("A backup path is required.", nameof(backupPath));
        var source = Path.GetFullPath(backupPath);
        if (!File.Exists(source)) throw new FileNotFoundException("The Radio Vault backup could not be found.", source);

        var rehearsalDirectory = Path.Combine(Path.GetTempPath(), $"trv-restore-rehearsal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rehearsalDirectory);
        try
        {
            var restoredDatabase = Path.Combine(rehearsalDirectory, "radio_vault.db");
            var artworkFiles = 0;
            long artworkBytes = 0;
            using (var archive = ZipFile.OpenRead(source))
            {
                if (archive.Entries.Count > MaximumEntries)
                    throw new InvalidDataException("The backup contains too many files to restore safely.");
                var expandedBytes = archive.Entries.Sum(entry => Math.Max(0, entry.Length));
                if (expandedBytes > MaximumExpandedBytes)
                    throw new InvalidDataException("The expanded backup is too large to restore safely.");

                var databaseEntry = archive.Entries.SingleOrDefault(entry =>
                    string.Equals(NormalizeEntry(entry.FullName), "radio_vault.db", StringComparison.OrdinalIgnoreCase));
                if (databaseEntry is not { Length: > 0 })
                    throw new InvalidDataException("This backup does not contain a non-empty Radio Vault database.");

                using (var input = databaseEntry.Open())
                using (var output = new FileStream(restoredDatabase, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }

                foreach (var entry in archive.Entries)
                {
                    var normalized = NormalizeEntry(entry.FullName);
                    if (!normalized.StartsWith("Artwork/", StringComparison.OrdinalIgnoreCase) || entry.Length <= 0) continue;
                    artworkFiles++;
                    artworkBytes += entry.Length;
                }
            }

            var database = InspectDatabase(restoredDatabase);
            var canRestore = string.Equals(database.QuickCheck, "ok", StringComparison.OrdinalIgnoreCase) &&
                             database.ForeignKeyViolations == 0 &&
                             database.TableCount > 0 &&
                             database.HasRequiredTables;
            return new BackupRestoreRehearsalResult(
                canRestore,
                database.QuickCheck,
                database.ForeignKeyViolations,
                database.SchemaVersion,
                database.TableCount,
                database.BroadcastCount,
                new FileInfo(restoredDatabase).Length,
                artworkFiles,
                artworkBytes,
                canRestore
                    ? $"Restore rehearsal passed: {database.BroadcastCount:N0} broadcasts, schema {database.SchemaVersion}, SQLite {database.QuickCheck}."
                    : "Restore rehearsal failed integrity or schema validation.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(rehearsalDirectory)) Directory.Delete(rehearsalDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public bool Verify(string backupPath)
    {
        try { return Rehearse(backupPath).CanRestore; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or SqliteException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string InspectQuickCheck(string databasePath)
        => InspectDatabase(Path.GetFullPath(databasePath)).QuickCheck;

    private static DatabaseInspection InspectDatabase(string databasePath)
    {
        // Integrity inspection is also used for temporary backup files that are
        // renamed immediately afterwards. A pooled read-only connection keeps
        // the file handle alive on Windows even after disposal, which prevents
        // that verified file from being moved into place.
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using var quick = connection.CreateCommand();
        quick.CommandText = "PRAGMA quick_check";
        var quickCheck = Convert.ToString(quick.ExecuteScalar()) ?? "unknown";

        using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check";
        var violations = 0;
        using (var reader = foreignKeys.ExecuteReader())
            while (reader.Read()) violations++;

        using var schema = connection.CreateCommand();
        schema.CommandText = "PRAGMA user_version";
        var schemaVersion = Convert.ToInt32(schema.ExecuteScalar());

        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = tables.ExecuteReader())
            while (reader.Read()) tableNames.Add(reader.GetString(0));

        long broadcasts = 0;
        if (tableNames.Contains("episodes"))
        {
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM episodes";
            broadcasts = Convert.ToInt64(count.ExecuteScalar());
        }

        return new DatabaseInspection(
            quickCheck,
            violations,
            schemaVersion,
            tableNames.Count,
            broadcasts,
            tableNames.Contains("episodes") && tableNames.Contains("collections"));
    }

    private static string NormalizeEntry(string entry)
    {
        var normalized = entry.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == ".."))
            throw new InvalidDataException("The backup contains an unsafe file path.");
        return normalized;
    }

    private sealed record DatabaseInspection(
        string QuickCheck,
        int ForeignKeyViolations,
        int SchemaVersion,
        int TableCount,
        long BroadcastCount,
        bool HasRequiredTables);
}
