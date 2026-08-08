using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TheRadioVault.Core.Services;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

public sealed class BroadcastDetailsService : IBroadcastDetailsService
{
    private readonly SqliteDatabase _database;

    public BroadcastDetailsService(SqliteDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<BroadcastDetails?> GetAsync(long representativeEpisodeId, CancellationToken cancellationToken = default)
    {
        if (representativeEpisodeId <= 0) return null;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        long? researchId = null;
        string canonicalKey = $"LEGACY-{representativeEpisodeId}";
        string broadcastId = string.Empty;
        int collectionId;
        string collectionName;
        DateOnly? airDate;
        string slot;
        string title;
        string summary;
        string station;
        string edition;
        string variant;
        string era;
        string episodeType;
        string archiveNotes;
        string personalNotes;
        string? artworkPath;
        int recordingCount;
        int segmentCount;
        int fileCount;
        string episodeHosts;
        string episodeCallers;
        string episodeMentioned;
        string researchJson;
        string originalFilename;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT e.id,
                       COALESCE(ecm.canonical_key,'LEGACY-' || e.id),
                       COALESCE(e.broadcast_uid,''),
                       c.id,c.name,e.air_date,COALESCE(e.broadcast_slot,''),COALESCE(e.title,''),
                       COALESCE(NULLIF(rb.summary,''),NULLIF(e.description,''),''),
                       COALESCE(rb.station,''),COALESCE(rb.edition,e.edition,''),
                       COALESCE(rb.broadcast_variant,e.broadcast_variant,''),
                       COALESCE(rb.broadcast_era,e.broadcast_era,''),
                       COALESCE(rb.episode_type,e.episode_type,''),
                       COALESCE(NULLIF(rb.archive_notes,''),e.archive_notes,''),COALESCE(e.notes,''),
                       COALESCE(e.artwork_path,''),rb.id,
                       CASE WHEN ecm.canonical_key IS NULL THEN 1 ELSE
                           MAX(1,(SELECT COUNT(*) FROM recordings r WHERE r.canonical_key=ecm.canonical_key)) END,
                       CASE WHEN ecm.canonical_key IS NULL THEN MAX(1,COALESCE(e.total_parts,1)) ELSE
                           MAX(1,(SELECT COUNT(*) FROM recording_segments rs JOIN recordings r ON r.recording_key=rs.recording_key WHERE r.canonical_key=ecm.canonical_key)) END,
                       CASE WHEN ecm.canonical_key IS NULL THEN MAX(1,(SELECT COUNT(*) FROM media_files mf WHERE mf.episode_id=e.id)) ELSE
                           MAX(1,(SELECT COUNT(DISTINCT mf.id) FROM episode_canonical_map map JOIN media_files mf ON mf.episode_id=map.episode_id WHERE map.canonical_key=ecm.canonical_key)) END,
                       COALESCE(e.hosts,''),COALESCE(e.callers,''),COALESCE(e.mentioned_people,''),
                       COALESCE(rb.research_json,''),
                       COALESCE((SELECT mf.original_filename FROM media_files mf
                                 WHERE mf.episode_id=e.id AND COALESCE(mf.is_missing,0)=0
                                 ORDER BY COALESCE(mf.is_preferred,0) DESC,mf.id LIMIT 1),'')
                FROM episodes e
                JOIN collections c ON c.id=e.collection_id
                LEFT JOIN episode_canonical_map ecm ON ecm.episode_id=e.id
                LEFT JOIN research_broadcasts rb ON rb.episode_id=e.id
                WHERE e.id=$id
                ORDER BY rb.updated_at DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$id", representativeEpisodeId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            canonicalKey = ReadString(reader, 1, canonicalKey);
            broadcastId = ReadString(reader, 2);
            collectionId = reader.GetInt32(3);
            collectionName = reader.GetString(4);
            airDate = ParseDate(reader, 5);
            slot = ReadString(reader, 6);
            title = ReadString(reader, 7);
            summary = ReadString(reader, 8);
            station = ReadString(reader, 9);
            edition = ReadString(reader, 10);
            variant = ReadString(reader, 11);
            era = ReadString(reader, 12);
            episodeType = ReadString(reader, 13);
            archiveNotes = ReadString(reader, 14);
            personalNotes = ReadString(reader, 15);
            artworkPath = ReadString(reader, 16);
            if (string.IsNullOrWhiteSpace(artworkPath)) artworkPath = null;
            researchId = reader.IsDBNull(17) ? null : reader.GetInt64(17);
            recordingCount = reader.GetInt32(18);
            segmentCount = reader.GetInt32(19);
            fileCount = reader.GetInt32(20);
            episodeHosts = ReadString(reader, 21);
            episodeCallers = ReadString(reader, 22);
            episodeMentioned = ReadString(reader, 23);
            researchJson = ReadString(reader, 24);
            originalFilename = ReadString(reader, 25);
        }

        var hosts = SplitValues(episodeHosts);
        var guests = new List<string>();
        var callers = SplitValues(episodeCallers);
        var mentioned = SplitValues(episodeMentioned);
        var topics = new List<string>();

        await using (var guestCommand = connection.CreateCommand())
        {
            guestCommand.CommandText = "SELECT g.name FROM episode_guests eg JOIN guests g ON g.id=eg.guest_id WHERE eg.episode_id=$id ORDER BY g.name COLLATE NOCASE";
            guestCommand.Parameters.AddWithValue("$id", representativeEpisodeId);
            await using var reader = await guestCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) AddDistinct(guests, reader.GetString(0));
        }

        await using (var tagCommand = connection.CreateCommand())
        {
            tagCommand.CommandText = "SELECT t.name FROM episode_tags et JOIN tags t ON t.id=et.tag_id WHERE et.episode_id=$id ORDER BY t.name COLLATE NOCASE";
            tagCommand.Parameters.AddWithValue("$id", representativeEpisodeId);
            await using var reader = await tagCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) AddDistinct(topics, reader.GetString(0));
        }

        if (researchId.HasValue)
        {
            await using (var peopleCommand = connection.CreateCommand())
            {
                peopleCommand.CommandText = "SELECT name,role FROM research_people WHERE research_broadcast_id=$id ORDER BY role,name COLLATE NOCASE";
                peopleCommand.Parameters.AddWithValue("$id", researchId.Value);
                await using var reader = await peopleCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var name = reader.GetString(0);
                    switch (reader.GetString(1))
                    {
                        case "host": AddDistinct(hosts, name); break;
                        case "guest": AddDistinct(guests, name); break;
                        case "caller": AddDistinct(callers, name); break;
                        default: AddDistinct(mentioned, name); break;
                    }
                }
            }

            await using var topicCommand = connection.CreateCommand();
            topicCommand.CommandText = "SELECT topic FROM research_topics WHERE research_broadcast_id=$id ORDER BY topic COLLATE NOCASE";
            topicCommand.Parameters.AddWithValue("$id", researchId.Value);
            await using var topicReader = await topicCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await topicReader.ReadAsync(cancellationToken).ConfigureAwait(false)) AddDistinct(topics, topicReader.GetString(0));
        }

        var catalogue = ReadCatalogueMetadata(researchJson);
        if (KnownShowCatalog.SupportsUndatedCatalogueItems(collectionName))
        {
            catalogue = catalogue with
            {
                Series = FirstValue(catalogue.Series, collectionName),
                Programme = FirstValue(catalogue.Programme, edition),
                Format = FirstValue(catalogue.Format, episodeType),
                OriginalReleaseDate = FirstValue(
                    catalogue.OriginalReleaseDate,
                    airDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    CatalogueDateService.ResolveDisplayText(originalFilename, title)),
                Network = FirstValue(catalogue.Network, station),
                OriginalFilename = FirstValue(catalogue.OriginalFilename, originalFilename)
            };
        }
        return new BroadcastDetails(
            representativeEpisodeId, canonicalKey, broadcastId, collectionId, collectionName, airDate, slot,
            title, summary, station, edition, variant, era, episodeType, archiveNotes,
            catalogue.Series, catalogue.Programme, catalogue.Format, catalogue.OriginalReleaseDate,
            catalogue.RecordingDate, catalogue.Venue, catalogue.Event, catalogue.Network,
            catalogue.CatalogueNumber, catalogue.OriginalFilename, catalogue.Provenance, catalogue.ResearchNotes,
            personalNotes, Join(hosts), Join(guests), Join(callers), Join(mentioned), topics, artworkPath,
            recordingCount, segmentCount, fileCount);
    }

    private static CatalogueMetadataValues ReadCatalogueMetadata(string json)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            var research = root?["research"] as JsonObject;
            var catalogue = research?["catalogue"] as JsonObject;
            if (catalogue is null) return CatalogueMetadataValues.Empty;
            string Value(string name) => catalogue[name]?.GetValue<string>()?.Trim() ?? string.Empty;
            return new CatalogueMetadataValues(
                Value("series"), Value("programme"), Value("format"), Value("original_release_date"),
                Value("recording_date"), Value("venue"), Value("event"), Value("network"),
                Value("catalogue_number"), Value("original_filename"), Value("provenance"), Value("research_notes"));
        }
        catch { return CatalogueMetadataValues.Empty; }
    }

    private sealed record CatalogueMetadataValues(
        string Series, string Programme, string Format, string OriginalReleaseDate,
        string RecordingDate, string Venue, string Event, string Network,
        string CatalogueNumber, string OriginalFilename, string Provenance, string ResearchNotes)
    {
        public static CatalogueMetadataValues Empty { get; } = new(
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
    }

    private static string FirstValue(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string ReadString(SqliteDataReader reader, int ordinal, string fallback = "")
        => reader.IsDBNull(ordinal) ? fallback : reader.GetString(ordinal);

    private static DateOnly? ParseDate(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        return DateOnly.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;
    }

    private static List<string> SplitValues(string? value)
    {
        var result = new List<string>();
        foreach (var item in (value ?? string.Empty).Split(new[] { ',', ';', '\n', '\r', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            AddDistinct(result, item);
        return result;
    }

    private static void AddDistinct(List<string> values, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var normalized = value.Trim();
        if (!values.Contains(normalized, StringComparer.CurrentCultureIgnoreCase)) values.Add(normalized);
    }

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);
}
