using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

/// <summary>
/// Open, agent-friendly .rvwiki exchange. Markdown remains human readable;
/// JSON carries stable identities, citations and temporal/image metadata.
/// </summary>
public sealed class WikiAuthoringPackService
{
    public const int SchemaVersion = 1;
    public const int MaximumPackageBytes = 512 * 1024 * 1024;
    private const long MaximumExpandedBytes = 1024L * 1024 * 1024;
    private const int MaximumEntries = 100_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public byte[] Export(WikiAuthoringSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        AddJson(entries, "pages/index.json", snapshot.Pages);
        foreach (var page in snapshot.Pages)
        {
            var body = snapshot.PageMarkdown.GetValueOrDefault(page.PageId) ?? string.Empty;
            entries[$"pages/{page.PageId:D}.md"] = Encoding.UTF8.GetBytes(NormalizeMarkdown(body));
        }
        AddJson(entries, "relationships.json", snapshot.Relationships);
        AddJson(entries, "sources.json", snapshot.Sources);
        AddJson(entries, "citations.json", snapshot.Citations.Select(x => x with { Source = null }).ToArray());
        AddJson(entries, "images/index.json", snapshot.Images);
        AddJson(entries, "page-images.json", snapshot.PageImages.Select(x => x with { Image = null }).ToArray());
        AddJson(entries, "timeline.json", snapshot.TimelineEvents);
        if (snapshot.ArchiveContext is not null)
            AddJson(entries, "archive-context.json", snapshot.ArchiveContext);
        entries["AUTHORING.md"] = Encoding.UTF8.GetBytes(AuthoringGuide());
        entries["schema/wiki-authoring-pack.schema.json"] = Encoding.UTF8.GetBytes(SchemaGuide());
        foreach (var record in snapshot.Images)
        {
            if (!snapshot.ImageBytes.TryGetValue(record.Image.ImageId, out var bytes))
                throw new InvalidDataException($"Image {record.Image.ImageId:D} has no content.");
            if (!string.Equals(Sha256(bytes), record.Image.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Image {record.Image.OriginalFileName} failed its checksum before export.");
            entries[NormalizeArchivePath(record.ArchivePath)] = bytes;
        }

        var hashes = entries.OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => Sha256(x.Value), StringComparer.Ordinal);
        var manifest = snapshot.Manifest with
        {
            SchemaVersion = SchemaVersion,
            PageCount = snapshot.Pages.Count,
            SourceCount = snapshot.Sources.Count,
            CitationCount = snapshot.Citations.Count,
            ImageCount = snapshot.Images.Count,
            TimelineEventCount = snapshot.TimelineEvents.Count,
            FileSha256 = hashes
        };
        AddJson(entries, "manifest.json", manifest);

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var pair in entries.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(pair.Key, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(pair.Value);
            }
        }
        var package = output.ToArray();
        if (package.Length > MaximumPackageBytes)
            throw new InvalidDataException($"The wiki authoring pack exceeds the {MaximumPackageBytes / 1024 / 1024} MB limit.");
        return package;
    }

    public WikiAuthoringSnapshot Import(byte[] packageBytes)
    {
        ArgumentNullException.ThrowIfNull(packageBytes);
        if (packageBytes.Length == 0) throw new InvalidDataException("The wiki authoring pack is empty.");
        if (packageBytes.Length > MaximumPackageBytes)
            throw new InvalidDataException($"Wiki authoring packs are limited to {MaximumPackageBytes / 1024 / 1024} MB.");

        using var input = new MemoryStream(packageBytes, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaximumEntries) throw new InvalidDataException("The wiki authoring pack contains too many files.");
        var byPath = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var path = NormalizeArchivePath(entry.FullName);
            if (path.EndsWith("/", StringComparison.Ordinal)) continue;
            if (!byPath.TryAdd(path, entry)) throw new InvalidDataException($"The wiki authoring pack contains duplicate file '{path}'.");
            expandedBytes += entry.Length;
            if (expandedBytes > MaximumExpandedBytes) throw new InvalidDataException("The wiki authoring pack expands beyond the supported size.");
        }

        var manifest = ReadJson<WikiAuthoringPackManifest>(Require(byPath, "manifest.json"));
        if (manifest.SchemaVersion != SchemaVersion)
            throw new InvalidDataException($"This wiki authoring pack uses schema {manifest.SchemaVersion}; Radio Vault supports schema {SchemaVersion}.");
        foreach (var expected in manifest.FileSha256)
        {
            var entry = Require(byPath, expected.Key);
            var bytes = ReadBytes(entry);
            if (!string.Equals(Sha256(bytes), expected.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"'{expected.Key}' was changed without rebuilding the package manifest.");
        }

        var pages = ReadJson<List<WikiAuthoringPageRecord>>(Require(byPath, "pages/index.json"));
        var pageMarkdown = new Dictionary<Guid, string>();
        foreach (var page in pages)
        {
            var path = $"pages/{page.PageId:D}.md";
            pageMarkdown[page.PageId] = Encoding.UTF8.GetString(ReadBytes(Require(byPath, path)));
        }
        var relationships = ReadOptionalJson(byPath, "relationships.json", new List<WikiRelationshipRecord>());
        var sources = ReadOptionalJson(byPath, "sources.json", new List<WikiSourceRecord>());
        var citations = ReadOptionalJson(byPath, "citations.json", new List<WikiCitationRecord>());
        var images = ReadOptionalJson(byPath, "images/index.json", new List<WikiAuthoringImageRecord>());
        var pageImages = ReadOptionalJson(byPath, "page-images.json", new List<WikiPageImageLink>());
        var timeline = ReadOptionalJson(byPath, "timeline.json", new List<WikiTimelineEventRecord>());
        var archiveContext = ReadOptionalJson<WikiArchiveContext?>(byPath, "archive-context.json", null);
        var imageBytes = new Dictionary<Guid, byte[]>();
        foreach (var image in images)
        {
            var bytes = ReadBytes(Require(byPath, image.ArchivePath));
            if (bytes.LongLength != image.Image.ByteCount)
                throw new InvalidDataException($"Image '{image.Image.OriginalFileName}' has the wrong byte count.");
            if (!string.Equals(Sha256(bytes), image.Image.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Image '{image.Image.OriginalFileName}' failed its checksum.");
            imageBytes[image.Image.ImageId] = bytes;
        }

        if (manifest.PageCount != pages.Count || manifest.SourceCount != sources.Count ||
            manifest.CitationCount != citations.Count || manifest.ImageCount != images.Count ||
            manifest.TimelineEventCount != timeline.Count)
            throw new InvalidDataException("The wiki authoring pack counts do not match its manifest.");
        return new WikiAuthoringSnapshot(manifest, pages, pageMarkdown, relationships, sources, citations,
            images, imageBytes, pageImages, timeline, archiveContext);
    }

    public static string ImageArchivePath(WikiImageRecord image)
    {
        var extension = Path.GetExtension(image.OriginalFileName).ToLowerInvariant();
        if (extension.Length is < 2 or > 10 || extension.Any(ch => !char.IsLetterOrDigit(ch) && ch != '.'))
            extension = image.MediaType.ToLowerInvariant() switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                "image/avif" => ".avif",
                _ => ".image"
            };
        return $"images/{image.ImageId:D}{extension}";
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string NormalizeArchivePath(string path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0 || normalized.Split('/').Any(x => x is "" or "." or ".."))
            throw new InvalidDataException("The wiki authoring pack contains an unsafe file path.");
        return normalized;
    }

    private static ZipArchiveEntry Require(IReadOnlyDictionary<string, ZipArchiveEntry> entries, string path)
    {
        var normalized = NormalizeArchivePath(path);
        return entries.TryGetValue(normalized, out var value)
            ? value
            : throw new InvalidDataException($"The wiki authoring pack is missing '{normalized}'.");
    }

    private static byte[] ReadBytes(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var output = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static T ReadJson<T>(ZipArchiveEntry entry)
        => JsonSerializer.Deserialize<T>(ReadBytes(entry), JsonOptions)
           ?? throw new InvalidDataException($"'{entry.FullName}' does not contain valid wiki data.");

    private static T ReadOptionalJson<T>(IReadOnlyDictionary<string, ZipArchiveEntry> entries, string path, T fallback)
        => entries.TryGetValue(path, out var entry) ? ReadJson<T>(entry) : fallback;

    private static void AddJson<T>(IDictionary<string, byte[]> entries, string path, T value)
        => entries[NormalizeArchivePath(path)] = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    private static string NormalizeMarkdown(string value)
        => (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd() + "\n";

    private static string AuthoringGuide() => """
        # Radio Vault Wiki Authoring Pack

        This package is an open, human-readable snapshot intended for careful human or AI-assisted editing.

        ## Non-negotiable identity and concurrency rules

        - Never change an existing `pageId` or `baseRevision` in `pages/index.json`.
        - Use a new RFC 4122 GUID for every new page, source, citation, relationship, image or timeline event.
        - A changed existing page is accepted only when its `baseRevision` still matches Radio Vault. Newer human edits become review conflicts and are never overwritten.
        - Keep Markdown in `pages/{pageId}.md`; keep structured facts, links and provenance in the JSON files.
        - `archive-context.json` is a read-only catalogue of shows, broadcasts, known people/topics and transcript availability. Use its stable episode IDs when connecting facts to the archive; do not invent IDs or copy this file back as Wiki prose without evidence.

        ## Wikipedia-style sourcing

        - Every factual claim that is not obvious page structure should have a citation.
        - Define each source once in `sources.json`, then reference its `sourceId` from `citations.json` and timeline events.
        - Prefer primary sources and Radio Vault broadcasts/transcript passages. Preserve exact episode IDs, broadcast UIDs and timestamp ranges supplied by the export.
        - Web sources require a title, publisher, URL and access date where available. Do not invent dates, quotations or bibliographic details.
        - `quotedText` must be a short supporting excerpt, not a substitute for original prose.

        ## Images as historical evidence

        - Put image files below `images/` and describe them in `images/index.json`.
        - Record creator, source, copyright/licence, caption and alt text.
        - Use `capturedDate`, `representativeFrom`, `representativeTo`, `datePrecision` and `dateNotes` to tie each image to a point or period in time.
        - Never add an image without provenance and a supportable right to retain it.

        ## Show timelines

        - Timeline events belong to a page and require a stable ID, title, date precision and at least one supporting source whenever possible.
        - Link the most relevant broadcasts, Moments, timestamp ranges and contemporary images.
        - Use significance 0–100 so Radio Vault can offer major-events and full-detail views.

        ## Repacking

        After editing, update every changed file's lowercase SHA-256 value in `manifest.json`, update the manifest counts, and preserve the ZIP-relative paths. Radio Vault validates all hashes before showing an import preview.
        """;

    private static string SchemaGuide() => """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "https://radiovault.local/schemas/wiki-authoring-pack-v1.json",
          "title": "Radio Vault Wiki Authoring Pack v1",
          "description": "The authoritative field definitions are the JSON records exported in this package. Stable GUIDs and baseRevision provide lossless round-trip identity and optimistic concurrency.",
          "type": "object",
          "required": ["manifest.json", "pages/index.json", "sources.json", "citations.json", "images/index.json", "timeline.json"]
        }
        """;
}
