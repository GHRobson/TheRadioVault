namespace TheRadioVault.Core.Services;

public sealed record KnownShowDefinition(
    string CanonicalName,
    string SortName,
    IReadOnlyList<string> Aliases,
    bool UsesUsDateOrder = true,
    bool SupportsUndatedCatalogueItems = false);

/// <summary>
/// Canonical first-class show identities understood by the local archive.
/// This catalog is deliberately independent of native client/server support so
/// Library scanning and Research packs use the same names everywhere.
/// </summary>
public static class KnownShowCatalog
{
    public const string RonFez = "Ron & Fez";
    public const string Bennington = "Bennington";
    public const string OpieAnthony = "Opie & Anthony";
    public const string RonRon = "The Ron & Ron Show";
    public const string Unmasked = "Unmasked";
    public const string RonBenningtonInterviews = "Ron Bennington Interviews";
    public const string Unsorted = "Unsorted";

    public static IReadOnlyList<KnownShowDefinition> Collections { get; } = new[]
    {
        new KnownShowDefinition(RonFez, "Ron and Fez", new[]
        {
            "ron and fez", "ron & fez", "r&f", "ronfez", "rf", "raf"
        }),
        new KnownShowDefinition(Bennington, "Bennington", new[]
        {
            "bennington", "the bennington show", "ron bennington", "benningtoon", "benningotn"
        }),
        new KnownShowDefinition(OpieAnthony, "Opie and Anthony", new[]
        {
            "opie and anthony", "opie & anthony", "o&a", "oand a", "oanda", "oa"
        }),
        new KnownShowDefinition(RonRon, "Ron and Ron Show", new[]
        {
            "the ron and ron show", "the ron & ron show", "ron and ron show", "ron & ron show",
            "ron and ron", "ron & ron", "ronronshow"
        }, SupportsUndatedCatalogueItems: true),
        new KnownShowDefinition(RonBenningtonInterviews, "Ron Bennington Interviews", new[]
        {
            "ron bennington interviews", "ron bennington interview", "ron interviews",
            "ronbenningtoninterviews", "rbi"
        }, SupportsUndatedCatalogueItems: true),
        new KnownShowDefinition(Unmasked, "Unmasked", new[]
        {
            "unmasked", "unmaked", "siriusxm unmasked", "unmasked with ron bennington"
        }, SupportsUndatedCatalogueItems: true),
        new KnownShowDefinition(Unsorted, "Unsorted", new[] { "unsorted" }, UsesUsDateOrder: false)
    };

    private static readonly IReadOnlyDictionary<string, string> AliasMap = BuildAliasMap();
    private static readonly HashSet<string> UsDateShows = Collections
        .Where(x => x.UsesUsDateOrder)
        .Select(x => x.CanonicalName)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return AliasMap.TryGetValue(Compact(trimmed), out var canonical) ? canonical : trimmed;
    }

    public static bool UsesUsArchiveDateOrder(string? collectionName)
        => !string.IsNullOrWhiteSpace(collectionName) && UsDateShows.Contains(collectionName.Trim());

    public static bool SupportsUndatedCatalogueItems(string? collectionName)
    {
        var normalized = Normalize(collectionName);
        return normalized is not null && Collections.Any(x =>
            x.CanonicalName.Equals(normalized, StringComparison.OrdinalIgnoreCase) &&
            x.SupportsUndatedCatalogueItems);
    }

    /// <summary>
    /// The guarded Research date-review workflow is available for every
    /// first-class show. Unsorted remains a holding collection rather than a
    /// programme whose chronology should be curated.
    /// </summary>
    public static bool SupportsDateReview(string? collectionName)
    {
        var normalized = Normalize(collectionName);
        return normalized is not null
            && !normalized.Equals(Unsorted, StringComparison.OrdinalIgnoreCase)
            && Collections.Any(x => x.CanonicalName.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsKnownShowName(string? value)
    {
        var normalized = Normalize(value);
        return normalized is not null && Collections.Any(x =>
            x.CanonicalName.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyDictionary<string, string> BuildAliasMap()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var show in Collections)
        {
            result[Compact(show.CanonicalName)] = show.CanonicalName;
            foreach (var alias in show.Aliases)
                result.TryAdd(Compact(alias), show.CanonicalName);
        }
        return result;
    }

    private static string Compact(string value)
        => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
