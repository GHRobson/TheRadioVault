using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TheRadioVault.Services;

internal sealed record RssFeedEnclosure(
    string StableKey,
    string Title,
    DateTimeOffset? PublishedAt,
    Uri EnclosureUri,
    string EnclosureHash);

internal static class RssFeedDocumentParser
{
    private const int MaximumItems = 1000;

    public static IReadOnlyList<RssFeedEnclosure> Parse(byte[] bytes, Uri feedUri)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(feedUri);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 8 * 1024 * 1024,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        var candidates = document.Descendants()
            .Where(element => element.Name.LocalName is "item" or "entry")
            .Take(MaximumItems)
            .Select(element => ParseItem(element, feedUri))
            .Where(value => value is not null)
            .Cast<RssFeedEnclosure>()
            .GroupBy(value => value.StableKey, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(value => value.PublishedAt).First())
            .OrderBy(value => value.PublishedAt ?? DateTimeOffset.MinValue)
            .ToArray();
        if (candidates.Length == 0)
            throw new InvalidDataException("The RSS feed did not contain any downloadable audio enclosures.");
        return candidates;
    }

    private static RssFeedEnclosure? ParseItem(XElement item, Uri feedUri)
    {
        var enclosureText = item.Elements()
            .Where(element => element.Name.LocalName == "enclosure")
            .Select(element => Attribute(element, "url") ?? Attribute(element, "href"))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        enclosureText ??= item.Elements()
            .Where(element => element.Name.LocalName == "link" &&
                              string.Equals(Attribute(element, "rel"), "enclosure", StringComparison.OrdinalIgnoreCase))
            .Select(element => Attribute(element, "href"))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        enclosureText ??= item.Elements()
            .Where(element => element.Name.LocalName == "content" &&
                              (Attribute(element, "type")?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(element => Attribute(element, "url"))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(enclosureText) ||
            !Uri.TryCreate(feedUri, enclosureText.Trim(), out var enclosure) ||
            enclosure.Scheme is not ("http" or "https")) return null;

        var title = ChildValue(item, "title");
        if (string.IsNullOrWhiteSpace(title)) title = "Untitled broadcast";
        title = title.Trim();
        if (title.Length > 300) title = title[..300];
        var guid = ChildValue(item, "guid") ?? ChildValue(item, "id");
        var publishedText = ChildValue(item, "pubDate") ?? ChildValue(item, "published") ?? ChildValue(item, "updated");
        DateTimeOffset? published = DateTimeOffset.TryParse(publishedText, out var date) ? date : null;
        var normalizedEnclosure = new UriBuilder(enclosure) { Query = string.Empty, Fragment = string.Empty }.Uri.AbsoluteUri;
        var enclosureHash = Hash(enclosure.AbsoluteUri);
        var stableKey = string.IsNullOrWhiteSpace(guid)
            ? "enclosure:" + Hash(normalizedEnclosure)
            : "guid:" + Hash(guid.Trim());
        return new RssFeedEnclosure(stableKey, title, published, enclosure, enclosureHash);
    }

    private static string? ChildValue(XElement element, string localName)
        => element.Elements().FirstOrDefault(value => value.Name.LocalName == localName)?.Value;

    private static string? Attribute(XElement element, string localName)
        => element.Attributes().FirstOrDefault(value => value.Name.LocalName == localName)?.Value;

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
