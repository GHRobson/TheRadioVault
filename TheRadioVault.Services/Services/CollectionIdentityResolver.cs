using Microsoft.Data.Sqlite;
using TheRadioVault.Core.Services;

namespace TheRadioVault.Services.Services;

/// <summary>
/// Treats legacy collection rows and their canonical first-class show name as
/// one logical show. Older databases can legitimately contain names such as
/// "Opie and Anthony" while newer code uses "Opie & Anthony"; UI projections
/// and Research operations must not lose content merely because those rows have
/// different numeric identifiers.
/// </summary>
public static class CollectionIdentityResolver
{
    public sealed record CollectionRow(int CollectionId, string StoredName, string SortName, string CanonicalName);

    public sealed record CollectionFamily(
        int PreferredCollectionId,
        string CanonicalName,
        string SortName,
        IReadOnlyList<int> CollectionIds);

    public static IReadOnlyList<CollectionRow> LoadRows(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var rows = new List<CollectionRow>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,sort_name FROM collections ORDER BY sort_name,name,id";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var storedName = reader.GetString(1).Trim();
            if (storedName.Length == 0) continue;
            rows.Add(new CollectionRow(
                reader.GetInt32(0),
                storedName,
                reader.IsDBNull(2) ? storedName : reader.GetString(2),
                Canonicalize(storedName)));
        }
        return rows;
    }

    public static IReadOnlyList<CollectionFamily> LoadFamilies(SqliteConnection connection)
    {
        var rows = LoadRows(connection);
        return rows
            .GroupBy(row => row.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var canonicalName = group.Key;
                var preferred = group.FirstOrDefault(row =>
                        row.StoredName.Equals(canonicalName, StringComparison.OrdinalIgnoreCase))
                    ?? group.OrderBy(row => row.CollectionId).First();
                return new CollectionFamily(
                    preferred.CollectionId,
                    canonicalName,
                    preferred.SortName,
                    group.Select(row => row.CollectionId).Distinct().OrderBy(id => id).ToArray());
            })
            .OrderBy(family => KnownShowOrder(family.CanonicalName))
            .ThenBy(family => family.SortName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(family => family.CanonicalName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static CollectionFamily? ResolveFamily(SqliteConnection connection, int collectionId)
    {
        if (collectionId <= 0) return null;
        return LoadFamilies(connection).FirstOrDefault(family => family.CollectionIds.Contains(collectionId));
    }

    public static string Canonicalize(string? collectionName)
    {
        var trimmed = collectionName?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return KnownShowCatalog.Unsorted;
        return KnownShowCatalog.Normalize(trimmed) ?? trimmed;
    }

    public static bool Matches(string? left, string? right)
        => Canonicalize(left).Equals(Canonicalize(right), StringComparison.OrdinalIgnoreCase);

    public static string AddIdPredicate(
        SqliteCommand command,
        string columnExpression,
        string parameterPrefix,
        IReadOnlyList<int> collectionIds)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (collectionIds.Count == 0) return "1=0";
        var parameters = new string[collectionIds.Count];
        for (var index = 0; index < collectionIds.Count; index++)
        {
            var parameterName = $"${parameterPrefix}{index}";
            parameters[index] = parameterName;
            command.Parameters.AddWithValue(parameterName, collectionIds[index]);
        }
        return $"{columnExpression} IN ({string.Join(",", parameters)})";
    }

    private static int KnownShowOrder(string canonicalName)
    {
        for (var index = 0; index < KnownShowCatalog.Collections.Count; index++)
        {
            if (KnownShowCatalog.Collections[index].CanonicalName.Equals(
                    canonicalName,
                    StringComparison.OrdinalIgnoreCase))
                return index;
        }
        return int.MaxValue;
    }
}
