using System.IO;
using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace TheRadioVault.Services;

public sealed record BackupRestoreResult(bool PreservedLocalLibraries, int LibraryFolderCount);

public sealed class BackupService
{
    public string CreateBackup(string destinationPath)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("The backup destination is invalid.");
        Directory.CreateDirectory(destinationDirectory);

        var workingDirectory = Path.Combine(Path.GetTempPath(), $"trv-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        var snapshotDb = Path.Combine(workingDirectory, "radio_vault.db");
        var temporaryArchive = Path.Combine(workingDirectory, "backup.trvbackup");

        try
        {
            CreateDatabaseSnapshot(snapshotDb);

            using (var archive = ZipFile.Open(temporaryArchive, ZipArchiveMode.Create))
            {
                AddFileToArchive(archive, snapshotDb, "radio_vault.db");

                if (Directory.Exists(AppPaths.ArtworkDirectory))
                {
                    foreach (var file in Directory.EnumerateFiles(AppPaths.ArtworkDirectory, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(AppPaths.ArtworkDirectory, file);
                        AddFileToArchive(archive, file, Path.Combine("Artwork", relative));
                    }
                }
            }

            // Build the entire backup away from the selected destination, then replace it
            // in one operation. This avoids partial files and handles overwriting safely.
            File.Move(temporaryArchive, destinationPath, true);
            return destinationPath;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(workingDirectory)) Directory.Delete(workingDirectory, true); }
            catch { /* A successful backup should not fail because temporary cleanup was delayed. */ }
        }
    }

    private static void CreateDatabaseSnapshot(string snapshotPath)
    {
        using var source = new SqliteConnection($"Data Source={AppPaths.DatabasePath};Mode=ReadWriteCreate;Cache=Shared");
        using var target = new SqliteConnection($"Data Source={snapshotPath};Mode=ReadWriteCreate");
        source.Open();
        target.Open();
        source.BackupDatabase(target);
    }

    private static void AddFileToArchive(ZipArchive archive, string sourcePath, string entryName)
    {
        // Artwork can still be displayed by WPF while a backup is created. Sharing read,
        // write and delete access prevents those harmless readers from breaking backup.
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            FileOptions.SequentialScan);
        var entry = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);
        using var destination = entry.Open();
        source.CopyTo(destination);
    }

    public BackupRestoreResult RestoreBackup(string backupPath)
    {
        var rehearsal = new BackupRestoreRehearsalService().Rehearse(backupPath);
        if (!rehearsal.CanRestore)
            throw new InvalidDataException(rehearsal.Message);
        var extract = Path.Combine(Path.GetTempPath(), $"trv-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extract);
        string? safety = null;
        try
        {
            ZipFile.ExtractToDirectory(backupPath, extract);
            var restoredDb = Path.Combine(extract, "radio_vault.db");
            if (!File.Exists(restoredDb)) throw new InvalidDataException("This backup does not contain a Radio Vault database.");
            safety = Path.Combine(AppPaths.BackupDirectory, $"pre-restore-{DateTime.Now:yyyyMMdd-HHmmss}.db");
            Directory.CreateDirectory(AppPaths.BackupDirectory);
            SqliteConnection.ClearAllPools();

            var localFolders = CaptureLibraryFolders(AppPaths.DatabasePath);
            if (File.Exists(AppPaths.DatabasePath)) File.Copy(AppPaths.DatabasePath, safety, true);
            File.Copy(restoredDb, AppPaths.DatabasePath, true);

            var restoredFolders = CaptureLibraryFolders(AppPaths.DatabasePath);
            var preserveLocalLibraries = localFolders.Count > 0 && !SameLibraryRoots(localFolders, restoredFolders);
            if (preserveLocalLibraries)
                ApplyDestinationLibraryState(AppPaths.DatabasePath, localFolders);

            var quickCheck = BackupRestoreRehearsalService.InspectQuickCheck(AppPaths.DatabasePath);
            if (!string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The restored database failed SQLite validation: {quickCheck}.");

            var artwork = Path.Combine(extract, "Artwork");
            if (Directory.Exists(artwork)) CopyDirectory(artwork, AppPaths.ArtworkDirectory);
            return new BackupRestoreResult(preserveLocalLibraries, preserveLocalLibraries ? localFolders.Count : restoredFolders.Count);
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            if (!string.IsNullOrWhiteSpace(safety) && File.Exists(safety))
                File.Copy(safety, AppPaths.DatabasePath, overwrite: true);
            throw;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(extract)) Directory.Delete(extract, true); } catch { }
        }
    }

    private static List<LibraryFolderSnapshot> CaptureLibraryFolders(string databasePath)
    {
        var result = new List<LibraryFolderSnapshot>();
        if (!File.Exists(databasePath)) return result;
        try
        {
            using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            connection.Open();
            using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='library_folders'";
            if (Convert.ToInt32(exists.ExecuteScalar()) == 0) return result;

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT lf.path,c.name,COALESCE(lf.enabled,1),COALESCE(lf.recursive,1) FROM library_folders lf LEFT JOIN collections c ON c.id=lf.assigned_collection_id ORDER BY lf.path";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new LibraryFolderSnapshot(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt32(2) != 0,
                    reader.GetInt32(3) != 0));
            }
        }
        catch (SqliteException)
        {
            // A pre-database install or very old backup has no portable folder state.
        }
        return result;
    }

    private static bool SameLibraryRoots(IReadOnlyCollection<LibraryFolderSnapshot> left, IReadOnlyCollection<LibraryFolderSnapshot> right)
    {
        if (left.Count != right.Count) return false;
        var leftRoots = left.Select(x => NormalisePath(x.Path)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var rightRoots = right.Select(x => NormalisePath(x.Path)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        return leftRoots.SequenceEqual(rightRoots, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalisePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void ApplyDestinationLibraryState(string databasePath, IReadOnlyCollection<LibraryFolderSnapshot> folders)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite;Cache=Shared");
        connection.Open();
        using var transaction = connection.BeginTransaction();

        using (var clearFolders = connection.CreateCommand())
        {
            clearFolders.Transaction = transaction;
            clearFolders.CommandText = "DELETE FROM library_folders";
            clearFolders.ExecuteNonQuery();
        }

        foreach (var folder in folders)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO library_folders(path,assigned_collection_id,enabled,recursive,last_scan_at) VALUES($path,(SELECT id FROM collections WHERE lower(name)=lower($collection) LIMIT 1),$enabled,$recursive,NULL)";
            insert.Parameters.AddWithValue("$path", folder.Path);
            insert.Parameters.AddWithValue("$collection", string.IsNullOrWhiteSpace(folder.AssignedCollectionName) ? DBNull.Value : folder.AssignedCollectionName);
            insert.Parameters.AddWithValue("$enabled", folder.Enabled ? 1 : 0);
            insert.Parameters.AddWithValue("$recursive", folder.Recursive ? 1 : 0);
            insert.ExecuteNonQuery();
        }

        using (var makeImportedLocationsHistorical = connection.CreateCommand())
        {
            makeImportedLocationsHistorical.Transaction = transaction;
            makeImportedLocationsHistorical.CommandText = "UPDATE media_files SET is_missing=1,storage_state='Unavailable'";
            makeImportedLocationsHistorical.ExecuteNonQuery();
        }

        // Scan history describes the computer that created the backup, not this one.
        using (var clearScanHistory = connection.CreateCommand())
        {
            clearScanHistory.Transaction = transaction;
            clearScanHistory.CommandText = "DELETE FROM scan_runs";
            clearScanHistory.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private sealed record LibraryFolderSnapshot(string Path, string? AssignedCollectionName, bool Enabled, bool Recursive);

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }
}
