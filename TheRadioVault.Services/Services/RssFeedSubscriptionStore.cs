using Microsoft.Data.Sqlite;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

internal sealed class RssFeedSubscriptionStore
{
    private readonly SqliteDatabase _database;

    public RssFeedSubscriptionStore(SqliteDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<IReadOnlyList<RssFeedSubscription>> GetAllAsync(CancellationToken cancellationToken = default)
        => (await ReadAsync(null, cancellationToken).ConfigureAwait(false))
            .Select(value => value.Subscription)
            .ToArray();

    public async Task<RssFeedSubscriptionState?> GetAsync(long id, CancellationToken cancellationToken = default)
        => (await ReadAsync(id, cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    public async Task<IReadOnlyList<RssFeedSubscriptionState>> GetDueAsync(
        DateTimeOffset now,
        long? feedId,
        bool force,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id,f.name,f.display_url,f.protected_source,f.library_folder_id,
                   lf.path,COALESCE(fc.name,c.name,''),f.check_interval_minutes,f.enabled,lf.enabled,
                   f.import_existing_on_first_check,f.initialized,f.etag,f.last_modified,
                   f.last_checked_at,f.last_success_at,f.next_check_at,f.last_error,
                   f.downloaded_count,f.seen_count,f.collection_id,COALESCE(lf.is_managed_archive,0)
              FROM rss_feed_subscriptions f
              JOIN library_folders lf ON lf.id=f.library_folder_id
            LEFT JOIN collections c ON c.id=lf.assigned_collection_id
            LEFT JOIN collections fc ON fc.id=f.collection_id
             WHERE f.enabled=1 AND lf.enabled=1
               AND ($feed_id IS NULL OR f.id=$feed_id)
               AND ($force=1 OR f.next_check_at IS NULL OR f.next_check_at<=$now)
            ORDER BY f.name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$feed_id", feedId.HasValue ? feedId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$force", force ? 1 : 0);
        command.Parameters.AddWithValue("$now", now.UtcDateTime.ToString("O"));
        return await ReadRowsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RssFeedSubscription> CreateAsync(
        RssFeedSaveRequest request,
        string displayUrl,
        string protectedSource,
        CancellationToken cancellationToken = default)
    {
        Validate(request, displayUrl, protectedSource);
        var now = DateTimeOffset.UtcNow;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureFolderAsync(connection, request.LibraryFolderId, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO rss_feed_subscriptions(
                name,display_url,protected_source,library_folder_id,collection_id,check_interval_minutes,
                enabled,import_existing_on_first_check,initialized,next_check_at,created_at,updated_at)
            VALUES($name,$url,$source,$folder,
                   COALESCE($collection,(SELECT assigned_collection_id FROM library_folders WHERE id=$folder)),
                   $interval,$enabled,$existing,0,$next,$now,$now)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$name", request.Name.Trim());
        command.Parameters.AddWithValue("$url", displayUrl);
        command.Parameters.AddWithValue("$source", protectedSource);
        command.Parameters.AddWithValue("$folder", request.LibraryFolderId);
        command.Parameters.AddWithValue("$collection", request.CollectionId.HasValue ? request.CollectionId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$interval", request.CheckIntervalMinutes);
        command.Parameters.AddWithValue("$enabled", request.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$existing", request.ImportExistingOnFirstCheck ? 1 : 0);
        command.Parameters.AddWithValue("$next", now.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$now", now.UtcDateTime.ToString("O"));
        try
        {
            var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            return (await GetAsync(id, cancellationToken).ConfigureAwait(false))!.Subscription;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("An RSS feed with that name already exists.", exception);
        }
    }

    public async Task<RssFeedSubscription> UpdateAsync(
        long id,
        RssFeedSaveRequest request,
        string displayUrl,
        string protectedSource,
        CancellationToken cancellationToken = default)
    {
        Validate(request, displayUrl, protectedSource);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureFolderAsync(connection, request.LibraryFolderId, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE rss_feed_subscriptions
               SET name=$name,display_url=$url,protected_source=$source,library_folder_id=$folder,
                   collection_id=COALESCE($collection,(SELECT assigned_collection_id FROM library_folders WHERE id=$folder)),
                   check_interval_minutes=$interval,enabled=$enabled,
                   import_existing_on_first_check=$existing,next_check_at=$next,last_error='',updated_at=$now
             WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", request.Name.Trim());
        command.Parameters.AddWithValue("$url", displayUrl);
        command.Parameters.AddWithValue("$source", protectedSource);
        command.Parameters.AddWithValue("$folder", request.LibraryFolderId);
        command.Parameters.AddWithValue("$collection", request.CollectionId.HasValue ? request.CollectionId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$interval", request.CheckIntervalMinutes);
        command.Parameters.AddWithValue("$enabled", request.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$existing", request.ImportExistingOnFirstCheck ? 1 : 0);
        command.Parameters.AddWithValue("$next", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                throw new KeyNotFoundException("The RSS feed no longer exists.");
            return (await GetAsync(id, cancellationToken).ConfigureAwait(false))!.Subscription;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("An RSS feed with that name already exists.", exception);
        }
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM rss_feed_subscriptions WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetEnabledAsync(long id, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE rss_feed_subscriptions
               SET enabled=$enabled,next_check_at=$next,last_error='',updated_at=$now
             WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$next", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            throw new KeyNotFoundException("The RSS feed no longer exists.");
    }

    public async Task<RssFeedItemRegistration> RegisterItemAsync(
        long feedId,
        RssFeedItemCandidate candidate,
        bool suppressInitialDownload,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var wasAdded = false;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO rss_feed_items(
                    feed_id,stable_key,title,published_at,enclosure_hash,status,first_seen_at)
                VALUES($feed,$key,$title,$published,$enclosure,$status,$now);
                """;
            insert.Parameters.AddWithValue("$feed", feedId);
            insert.Parameters.AddWithValue("$key", candidate.StableKey);
            insert.Parameters.AddWithValue("$title", candidate.Title);
            insert.Parameters.AddWithValue("$published", candidate.PublishedAt?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
            insert.Parameters.AddWithValue("$enclosure", candidate.EnclosureHash);
            insert.Parameters.AddWithValue("$status", suppressInitialDownload ? "Seen" : "Pending");
            insert.Parameters.AddWithValue("$now", now.UtcDateTime.ToString("O"));
            wasAdded = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT id,status FROM rss_feed_items WHERE feed_id=$feed AND stable_key=$key;";
        select.Parameters.AddWithValue("$feed", feedId);
        select.Parameters.AddWithValue("$key", candidate.StableKey);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The RSS item could not be recorded.");
        var id = reader.GetInt64(0);
        var status = reader.GetString(1);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RssFeedItemRegistration(id, status, wasAdded, status is "Pending" or "Failed");
    }

    public async Task<string?> FindExistingContentPathAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(contentHash)) return null;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path
              FROM media_files
             WHERE full_hash=$hash COLLATE NOCASE AND is_missing=0
            UNION ALL
            SELECT file_path
              FROM rss_feed_items
             WHERE content_hash=$hash COLLATE NOCASE AND file_path IS NOT NULL AND status IN ('Downloaded','Imported')
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("$hash", contentHash);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    public async Task<IReadOnlyList<RssLegacyFileNameCandidate>> GetLegacyFileNameCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.id,i.title,i.published_at,i.file_name,i.file_path,lf.path
              FROM rss_feed_items i
              JOIN rss_feed_subscriptions f ON f.id=i.feed_id
              JOIN library_folders lf ON lf.id=f.library_folder_id
             WHERE i.status IN ('Downloaded','Imported')
               AND i.published_at IS NOT NULL
               AND trim(COALESCE(i.file_name,''))<>''
               AND trim(COALESCE(i.file_path,''))<>''
             ORDER BY i.id;
            """;
        var values = new List<RssLegacyFileNameCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!DateTimeOffset.TryParse(reader.GetString(2), out var publishedAt))
                continue;
            values.Add(new RssLegacyFileNameCandidate(
                reader.GetInt64(0),
                reader.GetString(1),
                publishedAt,
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }
        return values;
    }

    public async Task UpdateDownloadedFileLocationAsync(
        long itemId,
        string expectedOldPath,
        string fileName,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var item = connection.CreateCommand())
        {
            item.Transaction = transaction;
            item.CommandText = """
                UPDATE rss_feed_items
                   SET file_name=$name,file_path=$path
                 WHERE id=$id AND file_path=$old_path;
                """;
            item.Parameters.AddWithValue("$id", itemId);
            item.Parameters.AddWithValue("$old_path", expectedOldPath);
            item.Parameters.AddWithValue("$name", fileName);
            item.Parameters.AddWithValue("$path", filePath);
            if (await item.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The RSS download record changed before its legacy filename could be updated.");
        }

        await using (var media = connection.CreateCommand())
        {
            media.Transaction = transaction;
            media.CommandText = """
                UPDATE media_files
                   SET path=$path,original_filename=$name
                 WHERE path=$old_path;
                """;
            media.Parameters.AddWithValue("$old_path", expectedOldPath);
            media.Parameters.AddWithValue("$name", fileName);
            media.Parameters.AddWithValue("$path", filePath);
            await media.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkItemDownloadedAsync(
        long itemId,
        string fileName,
        string filePath,
        string contentHash,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE rss_feed_items
               SET file_name=$name,file_path=$path,content_hash=$hash,status='Downloaded',last_error='',downloaded_at=$now
             WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", itemId);
        command.Parameters.AddWithValue("$name", fileName);
        command.Parameters.AddWithValue("$path", filePath);
        command.Parameters.AddWithValue("$hash", contentHash);
        command.Parameters.AddWithValue("$now", now.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkItemFailedAsync(long itemId, string message, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE rss_feed_items SET status='Failed',last_error=$error WHERE id=$id;";
        command.Parameters.AddWithValue("$id", itemId);
        command.Parameters.AddWithValue("$error", SafeError(message));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasDownloadsAwaitingScanAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM rss_feed_items WHERE status='Downloaded' LIMIT 1);";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    public async Task MarkDownloadsScannedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE rss_feed_items SET status='Imported',last_error='' WHERE status='Downloaded';";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkCheckSucceededAsync(
        long feedId,
        string? etag,
        string? lastModified,
        int downloaded,
        int seen,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE rss_feed_subscriptions
               SET initialized=1,etag=$etag,last_modified=$modified,last_checked_at=$now,last_success_at=$now,
                   next_check_at=$next,last_error='',downloaded_count=downloaded_count+$downloaded,
                   seen_count=seen_count+$seen,updated_at=$now
             WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", feedId);
        command.Parameters.AddWithValue("$etag", etag ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$modified", lastModified ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$downloaded", downloaded);
        command.Parameters.AddWithValue("$seen", seen);
        command.Parameters.AddWithValue("$now", now.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$next", NextCheck(now, feedId, connection));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkCheckFailedAsync(
        long feedId,
        string message,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE rss_feed_subscriptions
               SET last_checked_at=$now,next_check_at=$next,last_error=$error,updated_at=$now
             WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", feedId);
        command.Parameters.AddWithValue("$now", now.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$next", NextCheck(now, feedId, connection));
        command.Parameters.AddWithValue("$error", SafeError(message));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RssFeedSubscriptionState>> ReadAsync(long? id, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id,f.name,f.display_url,f.protected_source,f.library_folder_id,
                   lf.path,COALESCE(fc.name,c.name,''),f.check_interval_minutes,f.enabled,lf.enabled,
                   f.import_existing_on_first_check,f.initialized,f.etag,f.last_modified,
                   f.last_checked_at,f.last_success_at,f.next_check_at,f.last_error,
                   f.downloaded_count,f.seen_count,f.collection_id,COALESCE(lf.is_managed_archive,0)
              FROM rss_feed_subscriptions f
              JOIN library_folders lf ON lf.id=f.library_folder_id
            LEFT JOIN collections c ON c.id=lf.assigned_collection_id
            LEFT JOIN collections fc ON fc.id=f.collection_id
             WHERE $id IS NULL OR f.id=$id
            ORDER BY f.name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$id", id.HasValue ? id.Value : DBNull.Value);
        return await ReadRowsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<RssFeedSubscriptionState>> ReadRowsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var values = new List<RssFeedSubscriptionState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new RssFeedSubscriptionState(
                new RssFeedSubscription(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(4),
                    reader.GetString(5), reader.GetString(6), reader.GetInt32(7), reader.GetBoolean(8),
                    reader.GetBoolean(9), reader.GetBoolean(10), reader.GetBoolean(11),
                    ReadDate(reader, 14), ReadDate(reader, 15), ReadDate(reader, 16), reader.GetString(17),
                    reader.GetInt32(18), reader.GetInt32(19), reader.IsDBNull(20) ? null : reader.GetInt32(20),
                    reader.GetBoolean(21)),
                reader.GetString(3),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13)));
        }
        return values;
    }

    private static async Task EnsureFolderAsync(SqliteConnection connection, long folderId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM library_folders WHERE id=$id;";
        command.Parameters.AddWithValue("$id", folderId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) == 0)
            throw new InvalidOperationException("Choose a registered server Library folder for this feed.");
    }

    private static object NextCheck(DateTimeOffset now, long feedId, SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT check_interval_minutes FROM rss_feed_subscriptions WHERE id=$id;";
        command.Parameters.AddWithValue("$id", feedId);
        var minutes = Math.Clamp(Convert.ToInt32(command.ExecuteScalar()), 5, 10080);
        return now.AddMinutes(minutes).UtcDateTime.ToString("O");
    }

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) || !DateTimeOffset.TryParse(reader.GetString(ordinal), out var value) ? null : value;

    private static void Validate(RssFeedSaveRequest request, string displayUrl, string protectedSource)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Give the RSS feed a name.", nameof(request));
        if (request.Name.Trim().Length > 120) throw new ArgumentException("RSS feed names cannot exceed 120 characters.", nameof(request));
        if (string.IsNullOrWhiteSpace(displayUrl) || string.IsNullOrWhiteSpace(protectedSource))
            throw new ArgumentException("A valid RSS feed address is required.", nameof(request));
        if (request.CheckIntervalMinutes is < 5 or > 10080)
            throw new ArgumentOutOfRangeException(nameof(request), "RSS checks must be between 5 minutes and 7 days apart.");
    }

    private static string SafeError(string? value)
    {
        var message = string.IsNullOrWhiteSpace(value) ? "The RSS feed could not be checked." : value.Trim();
        return message.Length <= 500 ? message : message[..500];
    }
}

internal sealed record RssLegacyFileNameCandidate(
    long ItemId,
    string Title,
    DateTimeOffset PublishedAt,
    string FileName,
    string FilePath,
    string DestinationPath);
