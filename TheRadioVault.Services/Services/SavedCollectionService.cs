using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

public sealed class SavedCollectionService : ISavedCollectionService
{
    private static readonly JsonSerializerOptions RuleJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteDatabase _database;
    private readonly CanonicalLibraryQueryService _canonical;
    private readonly LibraryBrowseService _library;

    public SavedCollectionService(SqliteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _canonical = new CanonicalLibraryQueryService(database);
        _library = new LibraryBrowseService(database);
    }

    public async Task<IReadOnlyList<SavedCollectionSummary>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<SavedCollectionSummary>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id,c.name,c.kind,c.revision,c.created_at,c.updated_at,
                   CASE WHEN c.kind='Manual' THEN COUNT(i.episode_id) ELSE NULL END
              FROM saved_collections c
              LEFT JOIN saved_collection_items i ON i.collection_id=c.id
             GROUP BY c.id,c.name,c.kind,c.revision,c.created_at,c.updated_at
             ORDER BY c.updated_at DESC,c.name COLLATE NOCASE,c.id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadSummary(reader));
        return result;
    }

    public async Task<SavedCollectionDetails?> GetAsync(
        long collectionId,
        CancellationToken cancellationToken = default)
    {
        if (collectionId <= 0) return null;
        StoredCollection? stored;
        IReadOnlyList<long> episodeIds = [];
        await using (var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            stored = await ReadStoredAsync(connection, null, collectionId, cancellationToken).ConfigureAwait(false);
            if (stored is null) return null;
            if (stored.Kind == SavedCollectionKind.Manual)
                episodeIds = await ReadEpisodeIdsAsync(connection, null, collectionId, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<LibraryBroadcastSummary> broadcasts;
        SavedCollectionRule? rule = null;
        if (stored.Kind == SavedCollectionKind.Smart)
        {
            rule = DeserializeRule(stored.RuleJson);
            broadcasts = (await _library.BrowseAsync(rule.ToBrowseRequest(), cancellationToken).ConfigureAwait(false)).Broadcasts;
        }
        else
        {
            broadcasts = await _library.GetBroadcastsAsync(episodeIds, cancellationToken).ConfigureAwait(false);
        }

        return new SavedCollectionDetails(
            ToSummary(stored, broadcasts.Count),
            rule,
            broadcasts);
    }

    public async Task<SavedCollectionDetails> CreateAsync(
        string name,
        SavedCollectionKind kind,
        SavedCollectionRule? rule = null,
        IReadOnlyList<long>? episodeIds = null,
        CancellationToken cancellationToken = default)
    {
        name = NormalizeName(name);
        var ruleJson = RuleJson(kind, rule);
        var canonicalIds = kind == SavedCollectionKind.Manual
            ? CanonicalizeEpisodeIds(episodeIds ?? [])
            : episodeIds is { Count: > 0 }
                ? throw new ArgumentException("Smart collections cannot contain manually ordered items.", nameof(episodeIds))
                : [];
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        long id;
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO saved_collections(name,kind,smart_rule_json,revision,created_at,updated_at)
                    VALUES($name,$kind,$rule,1,$now,$now);
                    SELECT last_insert_rowid();
                    """;
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$kind", kind.ToString());
                command.Parameters.AddWithValue("$rule", (object?)ruleJson ?? DBNull.Value);
                command.Parameters.AddWithValue("$now", now);
                id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }
            await InsertItemsAsync(connection, transaction, id, canonicalIds, now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (IsConstraintFailure(exception))
        {
            throw new InvalidOperationException($"A saved collection named ‘{name}’ already exists.", exception);
        }
        return await RequireDetailsAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SavedCollectionDetails> UpdateAsync(
        long collectionId,
        string name,
        SavedCollectionRule? rule,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        name = NormalizeName(name);
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var stored = await RequireStoredAsync(connection, transaction, collectionId, expectedRevision, cancellationToken).ConfigureAwait(false);
            var ruleJson = RuleJson(stored.Kind, rule);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE saved_collections
                   SET name=$name,smart_rule_json=$rule,revision=revision+1,updated_at=$now
                 WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$rule", (object?)ruleJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$id", collectionId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (IsConstraintFailure(exception))
        {
            throw new InvalidOperationException($"A saved collection named ‘{name}’ already exists.", exception);
        }
        return await RequireDetailsAsync(collectionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SavedCollectionDetails> AddAsync(
        long collectionId,
        long episodeId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var canonicalId = CanonicalizeEpisodeId(episodeId);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var stored = await RequireStoredAsync(connection, transaction, collectionId, expectedRevision, cancellationToken).ConfigureAwait(false);
        RequireManual(stored);
        var position = await NextPositionAsync(connection, transaction, collectionId, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO saved_collection_items(collection_id,episode_id,item_position,added_at)
            VALUES($collection,$episode,$position,$added);
            """;
        command.Parameters.AddWithValue("$collection", collectionId);
        command.Parameters.AddWithValue("$episode", canonicalId);
        command.Parameters.AddWithValue("$position", position);
        command.Parameters.AddWithValue("$added", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        if (changed) await TouchAsync(connection, transaction, collectionId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await RequireDetailsAsync(collectionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SavedCollectionDetails> RemoveAsync(
        long collectionId,
        long episodeId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var canonicalId = CanonicalizeEpisodeId(episodeId);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var stored = await RequireStoredAsync(connection, transaction, collectionId, expectedRevision, cancellationToken).ConfigureAwait(false);
        RequireManual(stored);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM saved_collection_items WHERE collection_id=$collection AND episode_id=$episode;";
        command.Parameters.AddWithValue("$collection", collectionId);
        command.Parameters.AddWithValue("$episode", canonicalId);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        if (changed)
        {
            await NormalizePositionsAsync(connection, transaction, collectionId, cancellationToken).ConfigureAwait(false);
            await TouchAsync(connection, transaction, collectionId, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await RequireDetailsAsync(collectionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SavedCollectionDetails> MoveAsync(
        long collectionId,
        long episodeId,
        int targetIndex,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var canonicalId = CanonicalizeEpisodeId(episodeId);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var stored = await RequireStoredAsync(connection, transaction, collectionId, expectedRevision, cancellationToken).ConfigureAwait(false);
        RequireManual(stored);
        var ids = (await ReadEpisodeIdsAsync(connection, transaction, collectionId, cancellationToken).ConfigureAwait(false)).ToList();
        var currentIndex = ids.IndexOf(canonicalId);
        if (currentIndex < 0) throw new KeyNotFoundException("The broadcast is not in this saved collection.");
        targetIndex = Math.Clamp(targetIndex, 0, Math.Max(0, ids.Count - 1));
        if (currentIndex != targetIndex)
        {
            ids.RemoveAt(currentIndex);
            ids.Insert(targetIndex, canonicalId);
            await WritePositionsAsync(connection, transaction, collectionId, ids, cancellationToken).ConfigureAwait(false);
            await TouchAsync(connection, transaction, collectionId, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await RequireDetailsAsync(collectionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        long collectionId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        _ = await RequireStoredAsync(connection, transaction, collectionId, expectedRevision, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM saved_collections WHERE id=$id;";
        command.Parameters.AddWithValue("$id", collectionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<long> CanonicalizeEpisodeIds(IEnumerable<long> episodeIds)
        => episodeIds.Where(id => id > 0).Select(CanonicalizeEpisodeId).Distinct().ToArray();

    private long CanonicalizeEpisodeId(long episodeId)
    {
        if (episodeId <= 0) throw new ArgumentOutOfRangeException(nameof(episodeId));
        return _canonical.ResolveEpisode(episodeId)?.RepresentativeEpisodeId ?? episodeId;
    }

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A saved collection name is required.", nameof(name));
        var normalized = string.Join(' ', name.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length > 80) throw new ArgumentException("Saved collection names cannot exceed 80 characters.", nameof(name));
        return normalized;
    }

    private static string? RuleJson(SavedCollectionKind kind, SavedCollectionRule? rule)
    {
        if (kind == SavedCollectionKind.Manual)
        {
            if (rule is not null) throw new ArgumentException("Manual playlists do not use a smart rule.", nameof(rule));
            return null;
        }
        if (kind != SavedCollectionKind.Smart) throw new ArgumentOutOfRangeException(nameof(kind));
        ValidateRule(rule ?? throw new ArgumentException("Smart collections require a rule.", nameof(rule)));
        return JsonSerializer.Serialize(rule, RuleJsonOptions);
    }

    private static void ValidateRule(SavedCollectionRule rule)
    {
        if (rule.Month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(rule.Month));
        if (rule.Year is < 1900 or > 9999) throw new ArgumentOutOfRangeException(nameof(rule.Year));
        if (rule.Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(rule.Limit));
        if (rule.SearchText?.Length > 200) throw new ArgumentException("Smart collection searches cannot exceed 200 characters.", nameof(rule));
    }

    private static SavedCollectionRule DeserializeRule(string? json)
    {
        var rule = string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<SavedCollectionRule>(json, RuleJsonOptions);
        if (rule is null) throw new InvalidDataException("The smart collection rule is missing or invalid.");
        ValidateRule(rule);
        return rule;
    }

    private async Task<SavedCollectionDetails> RequireDetailsAsync(long collectionId, CancellationToken cancellationToken)
        => await GetAsync(collectionId, cancellationToken).ConfigureAwait(false)
           ?? throw new KeyNotFoundException("Saved collection not found.");

    private static void RequireManual(StoredCollection stored)
    {
        if (stored.Kind != SavedCollectionKind.Manual)
            throw new InvalidOperationException("Smart collections update from their rule and cannot be manually reordered.");
    }

    private static async Task<StoredCollection> RequireStoredAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long collectionId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        if (collectionId <= 0) throw new ArgumentOutOfRangeException(nameof(collectionId));
        if (expectedRevision <= 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        var stored = await ReadStoredAsync(connection, transaction, collectionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Saved collection not found.");
        if (stored.Revision != expectedRevision)
            throw new SavedCollectionConflictException(collectionId, expectedRevision, stored.Revision);
        return stored;
    }

    private static async Task<StoredCollection?> ReadStoredAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long collectionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id,name,kind,smart_rule_json,revision,created_at,updated_at,
                   CASE WHEN kind='Manual' THEN
                       (SELECT COUNT(*) FROM saved_collection_items i WHERE i.collection_id=saved_collections.id)
                   ELSE NULL END
              FROM saved_collections
             WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", collectionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new StoredCollection(
            reader.GetInt64(0),
            reader.GetString(1),
            Enum.Parse<SavedCollectionKind>(reader.GetString(2), true),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt64(4),
            ParseTimestamp(reader.GetString(5)),
            ParseTimestamp(reader.GetString(6)),
            reader.IsDBNull(7) ? null : reader.GetInt32(7));
    }

    private static async Task<IReadOnlyList<long>> ReadEpisodeIdsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long collectionId,
        CancellationToken cancellationToken)
    {
        var result = new List<long>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT episode_id FROM saved_collection_items WHERE collection_id=$id ORDER BY item_position,episode_id;";
        command.Parameters.AddWithValue("$id", collectionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(reader.GetInt64(0));
        return result;
    }

    private static async Task InsertItemsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long collectionId,
        IReadOnlyList<long> episodeIds,
        string addedAt,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < episodeIds.Count; index++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO saved_collection_items(collection_id,episode_id,item_position,added_at)
                VALUES($collection,$episode,$position,$added);
                """;
            command.Parameters.AddWithValue("$collection", collectionId);
            command.Parameters.AddWithValue("$episode", episodeIds[index]);
            command.Parameters.AddWithValue("$position", index);
            command.Parameters.AddWithValue("$added", addedAt);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<int> NextPositionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long collectionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(item_position),-1)+1 FROM saved_collection_items WHERE collection_id=$id;";
        command.Parameters.AddWithValue("$id", collectionId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static Task NormalizePositionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long collectionId,
        CancellationToken cancellationToken)
        => RewriteCurrentPositionsAsync(connection, transaction, collectionId, cancellationToken);

    private static async Task RewriteCurrentPositionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long collectionId,
        CancellationToken cancellationToken)
    {
        var ids = await ReadEpisodeIdsAsync(connection, transaction, collectionId, cancellationToken).ConfigureAwait(false);
        await WritePositionsAsync(connection, transaction, collectionId, ids, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WritePositionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long collectionId,
        IReadOnlyList<long> episodeIds,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < episodeIds.Count; index++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE saved_collection_items SET item_position=$position WHERE collection_id=$collection AND episode_id=$episode;";
            command.Parameters.AddWithValue("$position", index);
            command.Parameters.AddWithValue("$collection", collectionId);
            command.Parameters.AddWithValue("$episode", episodeIds[index]);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task TouchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long collectionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE saved_collections SET revision=revision+1,updated_at=$now WHERE id=$id;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", collectionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SavedCollectionSummary ReadSummary(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetString(1),
            Enum.Parse<SavedCollectionKind>(reader.GetString(2), true),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.GetInt64(3),
            ParseTimestamp(reader.GetString(4)),
            ParseTimestamp(reader.GetString(5)));

    private static SavedCollectionSummary ToSummary(StoredCollection stored, int materializedCount)
        => new(
            stored.Id,
            stored.Name,
            stored.Kind,
            stored.Kind == SavedCollectionKind.Manual ? stored.ItemCount ?? materializedCount : materializedCount,
            stored.Revision,
            stored.CreatedAt,
            stored.UpdatedAt);

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private static bool IsConstraintFailure(SqliteException exception)
        => exception.SqliteExtendedErrorCode == 2067 ||
           exception.Message.Contains("saved_collections.name", StringComparison.OrdinalIgnoreCase) ||
           exception.Message.Contains("ux_saved_collections_name", StringComparison.OrdinalIgnoreCase);

    private sealed record StoredCollection(
        long Id,
        string Name,
        SavedCollectionKind Kind,
        string? RuleJson,
        long Revision,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        int? ItemCount);
}
