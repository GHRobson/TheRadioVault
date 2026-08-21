using System.Globalization;
using TheRadioVault.Core.Services;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

public sealed class LibraryFolderService : ILibraryFolderService
{
    private readonly SqliteDatabase _database;
    public LibraryFolderService(SqliteDatabase database) => _database = database;

    public async Task<IReadOnlyList<LibraryFolderRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<LibraryFolderRecord>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id,f.path,f.assigned_collection_id,c.name,f.recursive,f.enabled,f.last_scan_at,
                   COALESCE(f.is_managed_archive,0)
              FROM library_folders f
              LEFT JOIN collections c ON c.id=f.assigned_collection_id
             ORDER BY COALESCE(f.is_managed_archive,0) DESC,COALESCE(c.sort_name,''),f.path;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            DateTimeOffset? lastScan = null;
            if (!reader.IsDBNull(6) && DateTimeOffset.TryParse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) lastScan = parsed;
            results.Add(new LibraryFolderRecord(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4) != 0, reader.GetInt32(5) != 0, lastScan,
                reader.GetInt32(7) != 0));
        }
        return results;
    }

    public async Task<IReadOnlyList<LibraryFolderCollectionOption>> GetAssignableCollectionsAsync(
        CancellationToken cancellationToken = default)
    {
        var databaseCollections = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name FROM collections ORDER BY sort_name,name";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            databaseCollections[reader.GetString(1)] = reader.GetInt32(0);

        return KnownShowCatalog.Collections
            .Where(show => !show.CanonicalName.Equals(KnownShowCatalog.Unsorted, StringComparison.OrdinalIgnoreCase))
            .Select(show => databaseCollections.TryGetValue(show.CanonicalName, out var collectionId)
                ? new LibraryFolderCollectionOption(collectionId, show.CanonicalName)
                : null)
            .Where(option => option is not null)
            .Select(option => option!)
            .ToArray();
    }

    public async Task<long> AddAsync(string path, int? collectionId, bool recursive = true, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A folder path is required.", nameof(path));
        var normalizedPath = Path.GetFullPath(path.Trim());
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
            VALUES($path,$collectionId,$recursive,1)
            ON CONFLICT(path) DO UPDATE SET assigned_collection_id=excluded.assigned_collection_id,recursive=excluded.recursive,enabled=1;
            SELECT id FROM library_folders WHERE path=$path;
            """;
        command.Parameters.AddWithValue("$path", normalizedPath);
        command.Parameters.AddWithValue("$collectionId", collectionId.HasValue ? collectionId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$recursive", recursive ? 1 : 0);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task SetEnabledAsync(long folderId, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE library_folders SET enabled=$enabled WHERE id=$id";
        command.Parameters.AddWithValue("$id", folderId);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetCollectionAsync(long folderId, int? collectionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE library_folders SET assigned_collection_id=$collectionId WHERE id=$id";
        command.Parameters.AddWithValue("$id", folderId);
        command.Parameters.AddWithValue("$collectionId", collectionId.HasValue ? collectionId.Value : DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new InvalidOperationException("The selected Library folder is no longer registered.");
    }

    public async Task RemoveAsync(long folderId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM library_folders WHERE id=$id";
        command.Parameters.AddWithValue("$id", folderId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
