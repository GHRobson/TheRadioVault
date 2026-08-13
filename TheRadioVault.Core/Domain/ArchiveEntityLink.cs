using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace TheRadioVault.Core.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<ArchiveEntityKind>))]
public enum ArchiveEntityKind
{
    Article,
    Show,
    Broadcast,
    Person,
    Topic,
    Image,
    Timeline
}

/// <summary>
/// Stable cross-client reference to something in the archive. EntityId owns
/// identity, TargetId carries the value a client needs to open it, and Route
/// provides one platform-neutral deep-link representation.
/// </summary>
public sealed record ArchiveEntityLink(
    ArchiveEntityKind Kind,
    string EntityId,
    string Label,
    string Route,
    string TargetId,
    string Relationship = "")
{
    public string EntityKey => $"{Kind.ToString().ToLowerInvariant()}:{EntityId}";
}

public static class ArchiveEntityLinkFactory
{
    public static ArchiveEntityLink Create(
        ArchiveEntityKind kind,
        string entityId,
        string label,
        string? targetId = null,
        string relationship = "")
    {
        var canonicalId = Require(entityId, nameof(entityId));
        var target = string.IsNullOrWhiteSpace(targetId) ? canonicalId : targetId.Trim();
        var kindName = kind.ToString().ToLowerInvariant();
        var route = $"radiovault://entity/{kindName}/{Uri.EscapeDataString(canonicalId)}";
        if (!string.Equals(target, canonicalId, StringComparison.Ordinal))
            route += $"?target={Uri.EscapeDataString(target)}";
        return new ArchiveEntityLink(
            kind,
            canonicalId,
            string.IsNullOrWhiteSpace(label) ? canonicalId : label.Trim(),
            route,
            target,
            relationship.Trim().ToLowerInvariant());
    }

    public static ArchiveEntityLink ForShow(long collectionId, string name)
        => Create(ArchiveEntityKind.Show, $"collection:{collectionId}", name, collectionId.ToString(CultureInfo.InvariantCulture));

    public static ArchiveEntityLink ForBroadcast(string canonicalKey, long episodeId, string title)
        => Create(
            ArchiveEntityKind.Broadcast,
            string.IsNullOrWhiteSpace(canonicalKey) ? $"episode:{episodeId}" : canonicalKey,
            title,
            episodeId.ToString(CultureInfo.InvariantCulture));

    public static ArchiveEntityLink ForBroadcast(long episodeId, string title)
        => ForBroadcast($"episode:{episodeId}", episodeId, title);

    public static ArchiveEntityLink ForPerson(string name, string relationship = "")
        => Create(ArchiveEntityKind.Person, NormalizeNamedId(name), name, relationship: relationship);

    public static ArchiveEntityLink ForTopic(string name)
        => Create(ArchiveEntityKind.Topic, NormalizeNamedId(name), name);

    public static ArchiveEntityLink ForImage(Guid imageId, string label)
        => Create(ArchiveEntityKind.Image, imageId.ToString("D"), label);

    public static ArchiveEntityLink ForTimeline(Guid eventId, string label)
        => Create(ArchiveEntityKind.Timeline, eventId.ToString("D"), label);

    public static ArchiveEntityLink ForWikiPage(
        Guid pageId,
        string pageType,
        string title)
        => Create(PageKind(pageType), pageId.ToString("D"), title);

    public static IReadOnlyList<ArchiveEntityLink> ForDelimitedNames(string? value, string relationship)
        => (value ?? string.Empty)
            .Split([',', ';', '|', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Select(name => ForPerson(name, relationship))
            .ToArray();

    public static bool TryParse(string? route, out ArchiveEntityKind kind, out string entityId, out string targetId)
    {
        kind = default;
        entityId = string.Empty;
        targetId = string.Empty;
        if (!Uri.TryCreate(route, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "radiovault", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "entity", StringComparison.OrdinalIgnoreCase))
            return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || !Enum.TryParse(segments[0], true, out kind)) return false;
        entityId = Uri.UnescapeDataString(segments[1]);
        if (entityId.Length == 0) return false;
        targetId = ReadTarget(uri.Query) ?? entityId;
        return true;
    }

    private static ArchiveEntityKind PageKind(string? pageType)
        => pageType?.Trim().ToLowerInvariant() switch
        {
            "show" => ArchiveEntityKind.Show,
            "broadcast" => ArchiveEntityKind.Broadcast,
            "person" or "host" or "guest" => ArchiveEntityKind.Person,
            "topic" => ArchiveEntityKind.Topic,
            "image" => ArchiveEntityKind.Image,
            "timeline" => ArchiveEntityKind.Timeline,
            _ => ArchiveEntityKind.Article
        };

    private static string NormalizeNamedId(string value)
    {
        var normalized = Require(value, nameof(value)).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        var pendingSeparator = false;
        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && builder.Length > 0) builder.Append('-');
                builder.Append(character);
                pendingSeparator = false;
            }
            else pendingSeparator = true;
        }
        return builder.Length == 0 ? throw new ArgumentException("Entity names must contain a letter or number.", nameof(value)) : builder.ToString();
    }

    private static string Require(string value, string parameter)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Entity identity cannot be empty.", parameter)
            : value.Trim();

    private static string? ReadTarget(string query)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(pair[0], "target", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[1]);
        }
        return null;
    }
}
