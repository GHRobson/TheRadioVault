using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using TheRadioVault.Core.Models;
using TheRadioVault.Core.Services;
using TheRadioVault.Data.Database;

namespace TheRadioVault.Services;

internal sealed class ManagedArchiveRssCoordinator
{
    private readonly SqliteDatabase _database;
    private readonly FilenameParserService _parser = new();

    public ManagedArchiveRssCoordinator(SqliteDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public ManagedArchiveRssRepairResult Repair(CancellationToken cancellationToken = default)
    {
        var state = ResolveState(cancellationToken);
        if (state is null) return ManagedArchiveRssRepairResult.NotConfigured;

        ConfigureManagedFolderAndFeeds(state, cancellationToken);
        var linked = RepairDownloadedItems(state, cancellationToken);
        return new(true, state.ManagedRoot, linked.Relinked, linked.Moved, linked.Held,
            $"Managed archive ready. RSS feeds now use {state.ManagedRoot}. " +
            $"{linked.Relinked:N0} existing RSS record(s) were relinked and {linked.Moved:N0} post-consolidation file(s) were moved into the managed layout." +
            (linked.Held == 0 ? string.Empty : $" {linked.Held:N0} file(s) were left untouched because their identity could not be verified safely."));
    }

    private ManagedArchiveState? ResolveState(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        using (var saved = connection.CreateCommand())
        {
            saved.CommandText = "SELECT library_folder_id,managed_root,quarantine_root FROM managed_archive_state WHERE id=1;";
            using var reader = saved.ExecuteReader();
            if (reader.Read())
                return new(reader.GetInt64(0), Path.GetFullPath(reader.GetString(1)), reader.GetString(2));
        }

        var quarantined = Scalar(connection,
            "SELECT COUNT(*) FROM media_files WHERE storage_state='Quarantined';");
        if (quarantined == 0) return null;

        var folders = new List<(long Id, string Path, bool Marked)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,path,COALESCE(is_managed_archive,0) FROM library_folders WHERE enabled=1;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                try { folders.Add((reader.GetInt64(0), Path.GetFullPath(reader.GetString(1)), reader.GetBoolean(2))); }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        }
        if (folders.Count == 0) return null;

        var paths = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT path FROM media_files WHERE is_missing=0 AND is_preferred=1;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                try { paths.Add(Path.GetFullPath(reader.GetString(0))); }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        }
        if (paths.Count == 0) return null;

        var ranked = folders
            .Select(folder => (Folder: folder, Count: paths.Count(path => IsWithin(path, folder.Path))))
            .OrderByDescending(value => value.Folder.Marked)
            .ThenByDescending(value => value.Count)
            .ThenBy(value => value.Folder.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var best = ranked[0];
        if (!best.Folder.Marked && (best.Count == 0 || best.Count * 2 < paths.Count))
            return null;

        var quarantineRoot = InferQuarantineRoot(connection);
        var state = new ManagedArchiveState(best.Folder.Id, best.Folder.Path, quarantineRoot);
        SaveState(connection, state);
        return state;
    }

    private void ConfigureManagedFolderAndFeeds(ManagedArchiveState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.UtcDateTime.ToString("O");

        Execute(connection, transaction,
            "UPDATE library_folders SET is_managed_archive=CASE WHEN id=$id THEN 1 ELSE 0 END;",
            ("$id", state.LibraryFolderId));
        Execute(connection, transaction,
            "UPDATE library_folders SET enabled=1,recursive=1,path=$path WHERE id=$id;",
            ("$id", state.LibraryFolderId), ("$path", state.ManagedRoot));
        Execute(connection, transaction, """
            UPDATE rss_feed_subscriptions
               SET collection_id=COALESCE(
                       collection_id,
                       (SELECT assigned_collection_id FROM library_folders
                         WHERE id=rss_feed_subscriptions.library_folder_id)),
                   library_folder_id=$folder,updated_at=$now;
            """, ("$folder", state.LibraryFolderId), ("$now", now));
        transaction.Commit();
    }

    private (int Relinked, int Moved, int Held) RepairDownloadedItems(
        ManagedArchiveState state,
        CancellationToken cancellationToken)
    {
        var relinked = 0;
        var moved = 0;
        var held = 0;
        foreach (var item in ReadRssItems(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var activePath = FindActivePath(item.ContentHash);
                if (!string.IsNullOrWhiteSpace(activePath) && File.Exists(activePath) && IsWithin(activePath, state.ManagedRoot))
                {
                    if (!PathsEqual(item.FilePath, activePath))
                    {
                        if (File.Exists(item.FilePath) && !IsWithin(item.FilePath, state.ManagedRoot))
                            RetainDuplicateOriginal(item, activePath, state, cancellationToken);
                        UpdateRssItem(item.Id, activePath);
                        relinked++;
                    }
                    continue;
                }

                if (!File.Exists(item.FilePath) || IsWithin(item.FilePath, state.ManagedRoot)) continue;
                if (MoveIntoManagedArchive(item, state, cancellationToken)) moved++;
                else held++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                held++;
                DiagnosticLog.Write("RSS managed archive",
                    $"RSS file ‘{item.FileName}’ was left unchanged because its managed-archive migration could not be completed safely.",
                    exception);
            }
        }
        return (relinked, moved, held);
    }

    private bool MoveIntoManagedArchive(RssManagedItem item, ManagedArchiveState state, CancellationToken cancellationToken)
    {
        var sourceHash = ComputeSha256(item.FilePath, cancellationToken);
        if (!string.IsNullOrWhiteSpace(item.ContentHash) &&
            !sourceHash.Equals(item.ContentHash, StringComparison.OrdinalIgnoreCase))
            return false;

        var metadata = ReadMediaMetadata(item.FilePath);
        var parsed = _parser.Parse(item.FileName,
            new FilenameParseContext(false, "RSS feed collection", item.CollectionName));
        var show = metadata?.ShowName ?? parsed.CollectionName ?? item.CollectionName ?? item.FeedName;
        var date = metadata?.AirDate ?? (parsed.AirDate.HasValue ? DateOnly.FromDateTime(parsed.AirDate.Value) : null);
        var slot = metadata?.BroadcastSlot ?? parsed.BroadcastSlot;
        var title = metadata?.Title ?? parsed.HeadlineCandidate;
        var part = metadata?.PartNumber ?? parsed.PartNumber;
        var totalParts = metadata?.TotalParts ?? parsed.TotalParts;
        var desired = date.HasValue
            ? ManagedArchivePathBuilder.Build(state.ManagedRoot, show, date.Value, slot, title, part, totalParts, totalParts ?? 1,
                Path.GetExtension(item.FilePath))
            : ManagedArchivePathBuilder.BuildUndated(state.ManagedRoot, show, item.FileName);
        var target = ChooseVerifiedTarget(desired, sourceHash, cancellationToken);
        EnsureVerifiedCopy(item.FilePath, target, sourceHash, cancellationToken);

        var quarantinePath = string.IsNullOrWhiteSpace(state.QuarantineRoot)
            ? string.Empty
            : BuildRssQuarantinePath(state.QuarantineRoot, item);
        if (!string.IsNullOrWhiteSpace(quarantinePath))
            MoveOriginalToQuarantine(item.FilePath, quarantinePath, sourceHash, cancellationToken);

        UpdateMovedItem(item, target, quarantinePath, sourceHash);
        DiagnosticLog.Write("RSS managed archive",
            $"Moved RSS file ‘{item.FileName}’ into ‘{target}’. The original was " +
            (string.IsNullOrWhiteSpace(quarantinePath) ? "left in place because no consolidation quarantine was available." : "retained in the consolidation quarantine."));
        return true;
    }

    private void RetainDuplicateOriginal(
        RssManagedItem item,
        string activePath,
        ManagedArchiveState state,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.QuarantineRoot)) return;
        var hash = ComputeSha256(item.FilePath, cancellationToken);
        if (!string.IsNullOrWhiteSpace(item.ContentHash) &&
            !hash.Equals(item.ContentHash, StringComparison.OrdinalIgnoreCase)) return;
        var quarantinePath = BuildRssQuarantinePath(state.QuarantineRoot, item);
        MoveOriginalToQuarantine(item.FilePath, quarantinePath, hash, cancellationToken);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE media_files
               SET path=$quarantine,is_missing=1,storage_state='Quarantined',is_preferred=0
             WHERE path=$source AND lower(path)<>lower($active);
            """;
        command.Parameters.AddWithValue("$source", item.FilePath);
        command.Parameters.AddWithValue("$active", activePath);
        command.Parameters.AddWithValue("$quarantine", quarantinePath);
        command.ExecuteNonQuery();
    }

    private void UpdateMovedItem(RssManagedItem item, string target, string quarantinePath, string hash)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var media = connection.CreateCommand())
        {
            media.Transaction = transaction;
            media.CommandText = """
                UPDATE media_files
                   SET path=$target,original_filename=$name,is_missing=0,storage_state='AvailableOffline',
                       is_preferred=1,full_hash=$hash,full_hashed_at=$now,last_seen_at=$now
                 WHERE path=$source
                   AND NOT EXISTS(SELECT 1 FROM media_files other WHERE lower(other.path)=lower($target));
                """;
            media.Parameters.AddWithValue("$source", item.FilePath);
            media.Parameters.AddWithValue("$target", target);
            media.Parameters.AddWithValue("$name", Path.GetFileName(target));
            media.Parameters.AddWithValue("$hash", hash);
            media.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
            media.ExecuteNonQuery();
        }
        if (!string.IsNullOrWhiteSpace(quarantinePath))
        {
            using var duplicate = connection.CreateCommand();
            duplicate.Transaction = transaction;
            duplicate.CommandText = """
                UPDATE media_files
                   SET path=$quarantine,is_missing=1,storage_state='Quarantined',is_preferred=0
                 WHERE path=$source;
                """;
            duplicate.Parameters.AddWithValue("$source", item.FilePath);
            duplicate.Parameters.AddWithValue("$quarantine", quarantinePath);
            duplicate.ExecuteNonQuery();
        }
        using (var rss = connection.CreateCommand())
        {
            rss.Transaction = transaction;
            rss.CommandText = "UPDATE rss_feed_items SET file_path=$path,file_name=$name WHERE id=$id;";
            rss.Parameters.AddWithValue("$id", item.Id);
            rss.Parameters.AddWithValue("$path", target);
            rss.Parameters.AddWithValue("$name", Path.GetFileName(target));
            rss.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private IReadOnlyList<RssManagedItem> ReadRssItems(CancellationToken cancellationToken)
    {
        var result = new List<RssManagedItem>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.id,f.name,COALESCE(c.name,''),i.title,COALESCE(i.file_name,''),
                   COALESCE(i.file_path,''),COALESCE(i.content_hash,'')
              FROM rss_feed_items i
              JOIN rss_feed_subscriptions f ON f.id=i.feed_id
              LEFT JOIN collections c ON c.id=f.collection_id
             WHERE i.status IN ('Downloaded','Imported') AND trim(COALESCE(i.file_path,''))<>''
             ORDER BY i.id;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), Path.GetFullPath(reader.GetString(5)), reader.GetString(6)));
        }
        return result;
    }

    private MediaMetadata? ReadMediaMetadata(string path)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.name,e.air_date,COALESCE(e.broadcast_slot,''),COALESCE(e.title,''),
                   COALESCE(e.part_number,1),e.total_parts
              FROM media_files mf
              JOIN episodes e ON e.id=mf.episode_id
              JOIN collections c ON c.id=e.collection_id
             WHERE lower(mf.path)=lower($path) LIMIT 1;
            """;
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        DateOnly? date = null;
        if (!reader.IsDBNull(1) && DateOnly.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            date = parsed;
        return new(reader.GetString(0), date, reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5));
    }

    private string? FindActivePath(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return null;
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path FROM media_files
             WHERE full_hash=$hash COLLATE NOCASE AND is_missing=0 AND is_preferred=1
             ORDER BY id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$hash", hash);
        return command.ExecuteScalar() as string;
    }

    private void UpdateRssItem(long id, string path)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE rss_feed_items SET file_path=$path,file_name=$name WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$name", Path.GetFileName(path));
        command.ExecuteNonQuery();
    }

    private static string ChooseVerifiedTarget(string desired, string hash, CancellationToken cancellationToken)
    {
        if (!File.Exists(desired) || ComputeSha256(desired, cancellationToken).Equals(hash, StringComparison.OrdinalIgnoreCase))
            return desired;
        var directory = Path.GetDirectoryName(desired)!;
        var stem = Path.GetFileNameWithoutExtension(desired);
        var extension = Path.GetExtension(desired);
        for (var suffix = 2; suffix <= 999; suffix++)
        {
            var candidate = Path.Combine(directory, $"{stem} - RSS copy {suffix}{extension}");
            if (!File.Exists(candidate) || ComputeSha256(candidate, cancellationToken).Equals(hash, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        throw new IOException("Radio Vault could not choose a collision-free managed RSS path.");
    }

    private static void EnsureVerifiedCopy(string source, string target, string hash, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            if (!ComputeSha256(target, cancellationToken).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The managed RSS destination contains different audio.");
            return;
        }
        var partial = target + ".rss-migration.partial";
        if (File.Exists(partial)) File.Delete(partial);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
        using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
        {
            var buffer = new byte[1024 * 1024];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                output.Write(buffer, 0, read);
            }
            output.Flush(flushToDisk: true);
        }
        if (!ComputeSha256(partial, cancellationToken).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The copied RSS audio did not match its source.");
        File.Move(partial, target);
    }

    private static void MoveOriginalToQuarantine(string source, string target, string hash, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            if (!ComputeSha256(target, cancellationToken).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The RSS quarantine destination contains different audio.");
            if (File.Exists(source)) throw new IOException("Both the RSS source and quarantine copy exist; neither was overwritten.");
            return;
        }
        File.Move(source, target);
        if (!ComputeSha256(target, cancellationToken).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The quarantined RSS original did not preserve its identity.");
    }

    private static string BuildRssQuarantinePath(string root, RssManagedItem item)
        => Path.Combine(Path.GetFullPath(root), "RadioVault-RSS-Migration", DateTime.UtcNow.ToString("yyyyMMdd"),
            ManagedArchivePathBuilder.SafeComponent(item.FeedName, "RSS feed", 60),
            $"{item.Id}-{ManagedArchivePathBuilder.SafeComponent(item.FileName, "RSS broadcast", 120)}");

    private static string ComputeSha256(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string InferQuarantineRoot(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM media_files WHERE storage_state='Quarantined' ORDER BY id LIMIT 100;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            DirectoryInfo? directory;
            try { directory = new FileInfo(reader.GetString(0)).Directory; }
            catch { continue; }
            while (directory is not null)
            {
                if (directory.Name.Equals("RadioVault-Consolidation", StringComparison.OrdinalIgnoreCase))
                    return directory.Parent?.FullName ?? string.Empty;
                directory = directory.Parent;
            }
        }
        return string.Empty;
    }

    private static void SaveState(SqliteConnection connection, ManagedArchiveState state)
    {
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "UPDATE library_folders SET is_managed_archive=CASE WHEN id=$id THEN 1 ELSE 0 END;",
            ("$id", state.LibraryFolderId));
        Execute(connection, transaction, """
            INSERT INTO managed_archive_state(id,library_folder_id,managed_root,quarantine_root,consolidated_at)
            VALUES(1,$folder,$managed,$quarantine,$now)
            ON CONFLICT(id) DO UPDATE SET library_folder_id=excluded.library_folder_id,
                managed_root=excluded.managed_root,quarantine_root=excluded.quarantine_root,
                consolidated_at=excluded.consolidated_at;
            """, ("$folder", state.LibraryFolderId), ("$managed", state.ManagedRoot),
            ("$quarantine", state.QuarantineRoot), ("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O")));
        transaction.Commit();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }

    private static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right)
        => Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

    private sealed record ManagedArchiveState(long LibraryFolderId, string ManagedRoot, string QuarantineRoot);
    private sealed record RssManagedItem(long Id, string FeedName, string CollectionName, string Title,
        string FileName, string FilePath, string ContentHash);
    private sealed record MediaMetadata(string ShowName, DateOnly? AirDate, string BroadcastSlot, string Title,
        int PartNumber, int? TotalParts);
}

internal sealed record ManagedArchiveRssRepairResult(
    bool Configured,
    string ManagedRoot,
    int RelinkedItems,
    int MovedItems,
    int HeldItems,
    string Message)
{
    public static ManagedArchiveRssRepairResult NotConfigured { get; } =
        new(false, string.Empty, 0, 0, 0, "No completed managed archive is configured.");
}
