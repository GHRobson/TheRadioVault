using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TheRadioVault.Models;
using TheRadioVault.Services.Models;
using TheRadioVault.Services.Services;

namespace TheRadioVault.Services;

/// <summary>
/// Reads and writes Radio Vault's single portable Research/Wiki SQLite database.
/// The database is deliberately inspectable by people and AI tools without first
/// unpacking private application files or reconstructing relationships from several formats.
/// </summary>
public sealed class KnowledgePackService
{
    public const int MaximumPackageBytes = 512 * 1024 * 1024;
    public const int SchemaVersion = 1;
    public const string Format = "radiovault.archive-knowledge-database";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new FlexibleStringListJsonConverter(), new FlexibleStringJsonConverter() }
    };

    public void Export(string path, TrvKnowledgePack pack)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.writing");
        try
        {
            BuildDatabase(temporaryPath, pack);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { TryDelete(temporaryPath); }
    }

    public byte[] ExportBytes(TrvKnowledgePack pack)
    {
        var path = TemporaryDatabasePath();
        try
        {
            BuildDatabase(path, pack);
            return File.ReadAllBytes(path);
        }
        finally { TryDelete(path); }
    }

    public void Export(Stream destination, TrvKnowledgePack pack)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(ExportBytes(pack));
    }

    public TrvKnowledgePack Import(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The Knowledge Database does not exist.", fullPath);
        ValidatePackageSize(fullPath);
        return ReadDatabase(fullPath);
    }

    public TrvKnowledgePack Import(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var stream = new MemoryStream(bytes, writable: false);
        return Import(stream);
    }

    public TrvKnowledgePack Import(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var path = TemporaryDatabasePath();
        try
        {
            using (var output = File.Create(path)) source.CopyTo(output);
            ValidatePackageSize(path);
            return ReadDatabase(path);
        }
        finally { TryDelete(path); }
    }

    private static void ValidatePackageSize(string path)
    {
        if (new FileInfo(path).Length is 0 or > MaximumPackageBytes)
            throw new InvalidDataException($"Knowledge databases must be between 1 byte and {MaximumPackageBytes / 1024 / 1024} MB.");
    }

    private static TrvKnowledgePack ReadDatabase(string path)
    {
        try
        {
            using var connection = Open(path, SqliteOpenMode.ReadOnly);
            ValidateReadableDatabase(connection);
            var manifest = ReadSetting<TrvPackManifest>(connection, "manifest")
                ?? throw new InvalidDataException("The knowledge database has no valid manifest.");
            if (!string.Equals(manifest.Format, Format, StringComparison.OrdinalIgnoreCase) || manifest.SchemaVersion != SchemaVersion)
                throw new InvalidDataException($"This is not a Radio Vault Archive Knowledge Database schema {SchemaVersion}.");

            var broadcasts = ReadJsonColumn<TrvPackBroadcast>(connection,
                "SELECT record_json FROM research_broadcasts WHERE is_missing=0 ORDER BY row_order");
            var missing = ReadJsonColumn<TrvPackBroadcast>(connection,
                "SELECT record_json FROM research_broadcasts WHERE is_missing=1 ORDER BY row_order");
            var transcripts = ReadJsonColumn<TrvPackTranscript>(connection,
                "SELECT transcript_json FROM transcripts ORDER BY row_order");
            manifest.BroadcastCount = broadcasts.Count;
            manifest.MissingBroadcastCount = missing.Count;
            manifest.TranscriptCount = transcripts.Count;
            return new TrvKnowledgePack
            {
                Manifest = manifest,
                Broadcasts = broadcasts,
                MissingBroadcasts = missing,
                Transcripts = transcripts,
                Wiki = ReadWiki(connection)
            };
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The selected file is not a readable Radio Vault knowledge database.", exception);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The knowledge database contains invalid structured data at {exception.Path ?? "an unknown field"}.", exception);
        }
    }

    public static string SerializeBroadcast(TrvPackBroadcast broadcast) => Json(broadcast);
    public static TrvPackBroadcast? DeserializeBroadcast(string json) => JsonSerializer.Deserialize<TrvPackBroadcast>(json, JsonOptions);

    private static void BuildDatabase(string path, TrvKnowledgePack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        pack.Broadcasts ??= new();
        pack.MissingBroadcasts ??= new();
        pack.Transcripts ??= new();
        pack.Manifest.Format = Format;
        pack.Manifest.SchemaVersion = SchemaVersion;
        pack.Manifest.BroadcastCount = pack.Broadcasts.Count;
        pack.Manifest.MissingBroadcastCount = pack.MissingBroadcasts.Count;
        pack.Manifest.TranscriptCount = pack.Transcripts.Count;
        pack.Manifest.WikiPageCount = pack.Wiki?.Pages.Count ?? 0;
        pack.Manifest.WikiImageCount = pack.Wiki?.Images.Count ?? 0;
        pack.Manifest.WikiTimelineEventCount = pack.Wiki?.TimelineEvents.Count ?? 0;

        using var connection = Open(path, SqliteOpenMode.ReadWriteCreate);
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                PRAGMA journal_mode=OFF;
                PRAGMA synchronous=OFF;
                CREATE TABLE pack_settings(key TEXT PRIMARY KEY,value_json TEXT NOT NULL);
                CREATE TABLE agent_instructions(content TEXT NOT NULL);
                CREATE TABLE pack_documentation(document_key TEXT PRIMARY KEY,title TEXT NOT NULL,audience TEXT NOT NULL,
                    sort_order INTEGER NOT NULL,content_markdown TEXT NOT NULL);
                CREATE TABLE pack_schema(table_name TEXT PRIMARY KEY,purpose TEXT NOT NULL,stable_identity TEXT NOT NULL,
                    ai_write_policy TEXT NOT NULL,columns_json TEXT NOT NULL);
                CREATE TABLE pack_change_log(change_id INTEGER PRIMARY KEY AUTOINCREMENT,changed_at TEXT NOT NULL,
                    agent TEXT NOT NULL,summary TEXT NOT NULL,details_markdown TEXT NOT NULL DEFAULT '');
                CREATE TABLE pack_validation(check_name TEXT PRIMARY KEY,expected_value TEXT NOT NULL,notes TEXT NOT NULL DEFAULT '');
                CREATE TABLE research_broadcasts(row_id TEXT PRIMARY KEY,row_order INTEGER NOT NULL,is_missing INTEGER NOT NULL,
                    broadcast_id TEXT NOT NULL,show_name TEXT NOT NULL,broadcast_date TEXT,slot TEXT,part_number INTEGER,total_parts INTEGER,record_json TEXT NOT NULL);
                CREATE TABLE transcripts(row_id TEXT PRIMARY KEY,row_order INTEGER NOT NULL,broadcast_id TEXT NOT NULL,show_name TEXT NOT NULL,
                    broadcast_date TEXT,part_number INTEGER,full_text TEXT NOT NULL,transcript_json TEXT NOT NULL);
                CREATE TABLE entities(entity_id TEXT PRIMARY KEY,entity_type TEXT NOT NULL,canonical_name TEXT NOT NULL,normalised_key TEXT NOT NULL,
                    UNIQUE(entity_type,normalised_key));
                CREATE TABLE knowledge_links(link_id INTEGER PRIMARY KEY AUTOINCREMENT,subject_type TEXT NOT NULL,subject_id TEXT NOT NULL,
                    predicate TEXT NOT NULL,object_type TEXT NOT NULL,object_id TEXT NOT NULL,label TEXT NOT NULL DEFAULT '',provenance TEXT NOT NULL DEFAULT '');
                CREATE INDEX ix_knowledge_links_subject ON knowledge_links(subject_type,subject_id);
                CREATE INDEX ix_knowledge_links_object ON knowledge_links(object_type,object_id);
                CREATE TABLE wiki_pages(page_id TEXT PRIMARY KEY,base_revision INTEGER NOT NULL,slug TEXT NOT NULL,title TEXT NOT NULL,page_type TEXT NOT NULL,
                    summary TEXT NOT NULL,status TEXT NOT NULL,created_by TEXT NOT NULL,last_editor TEXT NOT NULL,body_markdown TEXT NOT NULL,aliases_json TEXT NOT NULL);
                CREATE TABLE wiki_relationships(relationship_id TEXT PRIMARY KEY,from_page_id TEXT NOT NULL,to_page_id TEXT NOT NULL,relationship_type TEXT NOT NULL,data_json TEXT NOT NULL);
                CREATE TABLE wiki_sources(source_id TEXT PRIMARY KEY,source_type TEXT NOT NULL,title TEXT NOT NULL,url TEXT NOT NULL,episode_id INTEGER,moment_id INTEGER,start_ms INTEGER,end_ms INTEGER,data_json TEXT NOT NULL);
                CREATE TABLE wiki_citations(citation_id TEXT PRIMARY KEY,page_id TEXT NOT NULL,source_id TEXT NOT NULL,ordinal INTEGER NOT NULL,section_anchor TEXT NOT NULL,data_json TEXT NOT NULL);
                CREATE TABLE wiki_images(image_id TEXT PRIMARY KEY,original_file_name TEXT NOT NULL,media_type TEXT NOT NULL,sha256 TEXT NOT NULL,byte_count INTEGER NOT NULL,metadata_json TEXT NOT NULL,content BLOB NOT NULL);
                CREATE TABLE wiki_page_images(page_id TEXT NOT NULL,image_id TEXT NOT NULL,role TEXT NOT NULL,sort_order INTEGER NOT NULL,data_json TEXT NOT NULL,PRIMARY KEY(page_id,image_id,role));
                CREATE TABLE wiki_timeline_events(event_id TEXT PRIMARY KEY,page_id TEXT NOT NULL,title TEXT NOT NULL,start_date TEXT,end_date TEXT,date_precision TEXT NOT NULL,significance INTEGER NOT NULL,data_json TEXT NOT NULL);
                CREATE TABLE archive_shows(collection_id INTEGER PRIMARY KEY,name TEXT NOT NULL,broadcast_count INTEGER NOT NULL,first_broadcast TEXT,last_broadcast TEXT,data_json TEXT NOT NULL);
                CREATE TABLE archive_broadcasts(episode_id INTEGER PRIMARY KEY,collection_id INTEGER NOT NULL,show_name TEXT NOT NULL,title TEXT NOT NULL,air_date TEXT,broadcast_uid TEXT NOT NULL,
                    duration_ms INTEGER NOT NULL,has_transcript INTEGER NOT NULL,people_json TEXT NOT NULL,topics_json TEXT NOT NULL,data_json TEXT NOT NULL);
                CREATE VIEW ai_broadcast_knowledge AS
                    SELECT r.broadcast_id,r.show_name,r.broadcast_date,r.slot,r.part_number,r.is_missing,r.record_json,
                           (SELECT COUNT(*) FROM knowledge_links l WHERE l.subject_type='broadcast' AND l.subject_id=r.broadcast_id) AS linked_facts
                      FROM research_broadcasts r;
                """;
            schema.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        WriteSetting(connection, transaction, "manifest", pack.Manifest);
        Execute(connection, transaction, "INSERT INTO agent_instructions(content) VALUES($content)", ("$content", BuildInstructions(pack)));
        WriteDocumentation(connection, transaction, pack);
        if (pack.Wiki is not null)
        {
            WriteSetting(connection, transaction, "wiki_manifest", pack.Wiki.Manifest);
            WriteSetting(connection, transaction, "archive_context", pack.Wiki.ArchiveContext);
        }

        var order = 0;
        foreach (var item in pack.Broadcasts) WriteBroadcast(connection, transaction, item, false, order++);
        foreach (var item in pack.MissingBroadcasts) WriteBroadcast(connection, transaction, item, true, order++);
        order = 0;
        foreach (var transcript in pack.Transcripts)
        {
            var id = StableRowId("transcript", transcript.BroadcastId, transcript.Show, transcript.BroadcastDate, transcript.PartNumber.ToString(CultureInfo.InvariantCulture));
            Execute(connection, transaction, "INSERT OR REPLACE INTO transcripts VALUES($id,$order,$broadcast,$show,$date,$part,$text,$json)",
                ("$id", id), ("$order", order++), ("$broadcast", transcript.BroadcastId ?? ""), ("$show", transcript.Show ?? ""),
                ("$date", Db(transcript.BroadcastDate)), ("$part", transcript.PartNumber), ("$text", transcript.FullText ?? ""), ("$json", Json(transcript)));
            Link(connection, transaction, "transcript", id, "describes", "broadcast", transcript.BroadcastId ?? "", transcript.Show, "transcript");
        }
        if (pack.Wiki is not null) WriteWiki(connection, transaction, pack.Wiki);
        WriteValidationSummary(connection, transaction, pack);
        transaction.Commit();
        ValidateReadableDatabase(connection);
        connection.Close();

        if (new FileInfo(path).Length > MaximumPackageBytes)
        {
            TryDelete(path);
            throw new InvalidDataException($"The knowledge database exceeds the {MaximumPackageBytes / 1024 / 1024} MB limit.");
        }
    }

    private static void WriteBroadcast(SqliteConnection connection, SqliteTransaction transaction, TrvPackBroadcast item, bool missing, int order)
    {
        var id = string.IsNullOrWhiteSpace(item.BroadcastId)
            ? StableRowId("broadcast", item.Show, item.BroadcastDate, item.Slot, item.PartNumber.ToString(CultureInfo.InvariantCulture))
            : item.BroadcastId;
        Execute(connection, transaction, "INSERT OR REPLACE INTO research_broadcasts VALUES($row,$order,$missing,$broadcast,$show,$date,$slot,$part,$total,$json)",
            ("$row", StableRowId("row", id, missing.ToString())), ("$order", order), ("$missing", missing ? 1 : 0), ("$broadcast", id),
            ("$show", item.Show ?? ""), ("$date", Db(item.BroadcastDate)), ("$slot", Db(item.Slot)), ("$part", item.PartNumber),
            ("$total", item.TotalParts.HasValue ? item.TotalParts.Value : DBNull.Value), ("$json", Json(item)));
        Link(connection, transaction, "broadcast", id, "belongs_to", "entity", Entity(connection, transaction, "show", item.Show), item.Show, "research");
        foreach (var person in AllPeople(item.Research?.People))
            Link(connection, transaction, "broadcast", id, "features_person", "entity", Entity(connection, transaction, "person", person), person, "research");
        foreach (var topic in item.Research?.Topics ?? new())
            Link(connection, transaction, "broadcast", id, "covers_topic", "entity", Entity(connection, transaction, "topic", topic), topic, "research");
    }

    private static void WriteDocumentation(SqliteConnection connection, SqliteTransaction transaction, TrvKnowledgePack pack)
    {
        var documents = new[]
        {
            ("00_start_here", "Start here: purpose and workflow", "AI agents and people", 0, BuildInstructions(pack)),
            ("10_research_rules", "Research and evidence rules", "AI research agents", 10, ResearchRules),
            ("20_identity_links", "Stable identities and relationships", "AI agents and developers", 20, IdentityRules),
            ("30_safe_return", "Validation and return checklist", "AI agents and people", 30, ReturnChecklist)
        };
        foreach (var document in documents)
            Execute(connection, transaction,
                "INSERT INTO pack_documentation(document_key,title,audience,sort_order,content_markdown) VALUES($key,$title,$audience,$sort,$content)",
                ("$key", document.Item1), ("$title", document.Item2), ("$audience", document.Item3),
                ("$sort", document.Item4), ("$content", document.Item5));

        foreach (var schema in SchemaDocumentation)
            Execute(connection, transaction,
                "INSERT INTO pack_schema(table_name,purpose,stable_identity,ai_write_policy,columns_json) VALUES($table,$purpose,$identity,$policy,$columns)",
                ("$table", schema.Table), ("$purpose", schema.Purpose), ("$identity", schema.Identity),
                ("$policy", schema.Policy), ("$columns", schema.ColumnsJson));
    }

    private static void WriteValidationSummary(SqliteConnection connection, SqliteTransaction transaction, TrvKnowledgePack pack)
    {
        var checks = new[]
        {
            ("format", Format, "Must remain radiovault.archive-knowledge-database."),
            ("schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture), "Do not change unless Radio Vault documents a migration."),
            ("research_broadcasts", (pack.Broadcasts.Count + pack.MissingBroadcasts.Count).ToString(CultureInfo.InvariantCulture), "Advisory row count at export time; update after adding or removing records."),
            ("transcripts", pack.Transcripts.Count.ToString(CultureInfo.InvariantCulture), "Advisory row count at export time."),
            ("wiki_pages", (pack.Wiki?.Pages.Count ?? 0).ToString(CultureInfo.InvariantCulture), "Advisory row count at export time."),
            ("sqlite_quick_check", "ok", "Run PRAGMA quick_check before returning the database.")
        };
        foreach (var check in checks)
            Execute(connection, transaction,
                "INSERT INTO pack_validation(check_name,expected_value,notes) VALUES($name,$value,$notes)",
                ("$name", check.Item1), ("$value", check.Item2), ("$notes", check.Item3));
    }

    private static void WriteWiki(SqliteConnection connection, SqliteTransaction transaction, WikiAuthoringSnapshot wiki)
    {
        foreach (var page in wiki.Pages)
        {
            Execute(connection, transaction, "INSERT INTO wiki_pages VALUES($id,$revision,$slug,$title,$type,$summary,$status,$created,$editor,$body,$aliases)",
                ("$id", page.PageId.ToString("D")), ("$revision", page.BaseRevision), ("$slug", page.Slug), ("$title", page.Title),
                ("$type", page.PageType), ("$summary", page.Summary), ("$status", page.Status), ("$created", page.CreatedBy),
                ("$editor", page.LastEditor), ("$body", wiki.PageMarkdown.GetValueOrDefault(page.PageId) ?? ""), ("$aliases", Json(page.Aliases)));
            Link(connection, transaction, "wiki_page", page.PageId.ToString("D"), "describes", "entity",
                Entity(connection, transaction, page.PageType.ToLowerInvariant(), page.Title), page.Title, "wiki");
        }
        foreach (var relationship in wiki.Relationships)
        {
            Execute(connection, transaction, "INSERT INTO wiki_relationships VALUES($id,$from,$to,$type,$json)",
                ("$id", relationship.RelationshipId.ToString("D")), ("$from", relationship.FromPageId.ToString("D")),
                ("$to", relationship.ToPageId.ToString("D")), ("$type", relationship.RelationshipType), ("$json", Json(relationship)));
            Link(connection, transaction, "wiki_page", relationship.FromPageId.ToString("D"), relationship.RelationshipType,
                "wiki_page", relationship.ToPageId.ToString("D"), relationship.Notes, "wiki relationship");
        }
        foreach (var source in wiki.Sources)
        {
            Execute(connection, transaction, "INSERT INTO wiki_sources VALUES($id,$type,$title,$url,$episode,$moment,$start,$end,$json)",
                ("$id", source.SourceId.ToString("D")), ("$type", source.SourceType), ("$title", source.Title), ("$url", source.Url),
                ("$episode", source.EpisodeId.HasValue ? source.EpisodeId.Value : DBNull.Value), ("$moment", source.MomentId.HasValue ? source.MomentId.Value : DBNull.Value),
                ("$start", source.StartMs.HasValue ? source.StartMs.Value : DBNull.Value), ("$end", source.EndMs.HasValue ? source.EndMs.Value : DBNull.Value), ("$json", Json(source)));
            if (source.EpisodeId.HasValue) Link(connection, transaction, "source", source.SourceId.ToString("D"), "references", "broadcast", source.EpisodeId.Value.ToString(CultureInfo.InvariantCulture), source.Title, "wiki source");
            if (source.MomentId.HasValue) Link(connection, transaction, "source", source.SourceId.ToString("D"), "references", "moment", source.MomentId.Value.ToString(CultureInfo.InvariantCulture), source.Title, "wiki source");
        }
        foreach (var citation in wiki.Citations)
        {
            Execute(connection, transaction, "INSERT INTO wiki_citations VALUES($id,$page,$source,$ordinal,$anchor,$json)",
                ("$id", citation.CitationId.ToString("D")), ("$page", citation.PageId.ToString("D")), ("$source", citation.SourceId.ToString("D")),
                ("$ordinal", citation.Ordinal), ("$anchor", citation.SectionAnchor), ("$json", Json(citation with { Source = null })));
            Link(connection, transaction, "wiki_page", citation.PageId.ToString("D"), "cites", "source", citation.SourceId.ToString("D"), citation.Note, "wiki citation");
        }
        foreach (var image in wiki.Images)
        {
            var bytes = wiki.ImageBytes.GetValueOrDefault(image.Image.ImageId) ?? Array.Empty<byte>();
            Execute(connection, transaction, "INSERT INTO wiki_images VALUES($id,$name,$media,$sha,$count,$json,$content)",
                ("$id", image.Image.ImageId.ToString("D")), ("$name", image.Image.OriginalFileName), ("$media", image.Image.MediaType),
                ("$sha", image.Image.Sha256), ("$count", image.Image.ByteCount), ("$json", Json(image.Image)), ("$content", bytes));
        }
        foreach (var pageImage in wiki.PageImages)
            Execute(connection, transaction, "INSERT INTO wiki_page_images VALUES($page,$image,$role,$sort,$json)",
                ("$page", pageImage.PageId.ToString("D")), ("$image", pageImage.ImageId.ToString("D")), ("$role", pageImage.Role),
                ("$sort", pageImage.SortOrder), ("$json", Json(pageImage with { Image = null })));
        foreach (var timeline in wiki.TimelineEvents)
        {
            Execute(connection, transaction, "INSERT INTO wiki_timeline_events VALUES($id,$page,$title,$start,$end,$precision,$significance,$json)",
                ("$id", timeline.EventId.ToString("D")), ("$page", timeline.PageId.ToString("D")), ("$title", timeline.Title),
                ("$start", Db(timeline.StartDate?.ToString("yyyy-MM-dd"))), ("$end", Db(timeline.EndDate?.ToString("yyyy-MM-dd"))),
                ("$precision", timeline.DatePrecision), ("$significance", timeline.Significance), ("$json", Json(timeline)));
            Link(connection, transaction, "wiki_page", timeline.PageId.ToString("D"), "has_timeline_event", "timeline_event", timeline.EventId.ToString("D"), timeline.Title, "wiki timeline");
            foreach (var broadcast in timeline.Broadcasts)
            {
                Link(connection, transaction, "timeline_event", timeline.EventId.ToString("D"), "references", "broadcast", broadcast.EpisodeId.ToString(CultureInfo.InvariantCulture), broadcast.Label, "wiki timeline");
                if (broadcast.MomentId.HasValue) Link(connection, transaction, "timeline_event", timeline.EventId.ToString("D"), "references", "moment", broadcast.MomentId.Value.ToString(CultureInfo.InvariantCulture), broadcast.Label, "wiki timeline");
            }
        }
        foreach (var show in wiki.ArchiveContext?.Shows ?? Array.Empty<WikiArchiveShowContext>())
            Execute(connection, transaction, "INSERT INTO archive_shows VALUES($id,$name,$count,$first,$last,$json)",
                ("$id", show.CollectionId), ("$name", show.Name), ("$count", show.BroadcastCount), ("$first", Db(show.FirstBroadcast?.ToString("yyyy-MM-dd"))),
                ("$last", Db(show.LastBroadcast?.ToString("yyyy-MM-dd"))), ("$json", Json(show)));
        foreach (var broadcast in wiki.ArchiveContext?.Broadcasts ?? Array.Empty<WikiArchiveBroadcastContext>())
            Execute(connection, transaction, "INSERT INTO archive_broadcasts VALUES($id,$collection,$show,$title,$date,$uid,$duration,$transcript,$people,$topics,$json)",
                ("$id", broadcast.EpisodeId), ("$collection", broadcast.CollectionId), ("$show", broadcast.Show), ("$title", broadcast.Title),
                ("$date", Db(broadcast.AirDate?.ToString("yyyy-MM-dd"))), ("$uid", broadcast.BroadcastUid), ("$duration", broadcast.DurationMs),
                ("$transcript", broadcast.HasTranscript ? 1 : 0), ("$people", Json(broadcast.People)), ("$topics", Json(broadcast.Topics)), ("$json", Json(broadcast)));
    }

    private static WikiAuthoringSnapshot? ReadWiki(SqliteConnection connection)
    {
        var manifest = ReadSetting<WikiAuthoringPackManifest>(connection, "wiki_manifest");
        if (manifest is null) return null;
        var pages = new List<WikiAuthoringPageRecord>();
        var markdown = new Dictionary<Guid, string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT page_id,base_revision,slug,title,page_type,summary,status,created_by,last_editor,body_markdown,aliases_json FROM wiki_pages ORDER BY title";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = Guid.Parse(reader.GetString(0));
                pages.Add(new WikiAuthoringPageRecord(id, reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), FromJson<List<string>>(reader.GetString(10))));
                markdown[id] = reader.GetString(9);
            }
        }
        var relationships = ReadJsonColumn<WikiRelationshipRecord>(connection, "SELECT data_json FROM wiki_relationships ORDER BY relationship_id");
        var sources = ReadJsonColumn<WikiSourceRecord>(connection, "SELECT data_json FROM wiki_sources ORDER BY source_id");
        var citations = ReadJsonColumn<WikiCitationRecord>(connection, "SELECT data_json FROM wiki_citations ORDER BY page_id,ordinal");
        var pageImages = ReadJsonColumn<WikiPageImageLink>(connection, "SELECT data_json FROM wiki_page_images ORDER BY page_id,sort_order");
        var timeline = ReadJsonColumn<WikiTimelineEventRecord>(connection, "SELECT data_json FROM wiki_timeline_events ORDER BY start_date,event_id");
        var images = new List<WikiAuthoringImageRecord>();
        var imageBytes = new Dictionary<Guid, byte[]>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT metadata_json,content FROM wiki_images ORDER BY image_id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var image = FromJson<WikiImageRecord>(reader.GetString(0));
                images.Add(new WikiAuthoringImageRecord(image, WikiAuthoringPackService.ImageArchivePath(image)));
                imageBytes[image.ImageId] = (byte[])reader[1];
            }
        }
        return new WikiAuthoringSnapshot(manifest, pages, markdown, relationships, sources, citations, images, imageBytes,
            pageImages, timeline, ReadSetting<WikiArchiveContext>(connection, "archive_context"));
    }

    private static List<T> ReadJsonColumn<T>(SqliteConnection connection, string sql)
    {
        var result = new List<T>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(FromJson<T>(reader.GetString(0)));
        return result;
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void ValidateReadableDatabase(SqliteConnection connection)
    {
        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA quick_check";
            var result = Convert.ToString(integrity.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The knowledge database failed SQLite integrity checking: {result ?? "unknown error"}.");
        }

        var required = new[]
        {
            "pack_settings", "research_broadcasts", "transcripts", "wiki_pages", "wiki_relationships",
            "wiki_sources", "wiki_citations", "wiki_images", "wiki_page_images", "wiki_timeline_events"
        };
        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        using var reader = tables.ExecuteReader();
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) available.Add(reader.GetString(0));
        var missing = required.Where(table => !available.Contains(table)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"The knowledge database is incomplete. Missing required table{(missing.Length == 1 ? "" : "s")}: {string.Join(", ", missing)}.");
    }

    private static void WriteSetting<T>(SqliteConnection connection, SqliteTransaction transaction, string key, T value)
        => Execute(connection, transaction, "INSERT INTO pack_settings(key,value_json) VALUES($key,$value)", ("$key", key), ("$value", Json(value)));

    private static T? ReadSetting<T>(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_json FROM pack_settings WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is string json && !string.Equals(json, "null", StringComparison.OrdinalIgnoreCase) ? FromJson<T>(json) : default;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static string Entity(SqliteConnection connection, SqliteTransaction transaction, string type, string? name)
    {
        var canonical = (name ?? "").Trim();
        var key = Normalise(canonical);
        var id = StableRowId("entity", type, key);
        Execute(connection, transaction, "INSERT OR IGNORE INTO entities VALUES($id,$type,$name,$key)",
            ("$id", id), ("$type", type), ("$name", canonical), ("$key", key));
        return id;
    }

    private static void Link(SqliteConnection connection, SqliteTransaction transaction, string subjectType, string subjectId,
        string predicate, string objectType, string objectId, string? label, string provenance)
    {
        if (string.IsNullOrWhiteSpace(subjectId) || string.IsNullOrWhiteSpace(objectId)) return;
        Execute(connection, transaction, "INSERT INTO knowledge_links(subject_type,subject_id,predicate,object_type,object_id,label,provenance) VALUES($st,$sid,$p,$ot,$oid,$label,$source)",
            ("$st", subjectType), ("$sid", subjectId), ("$p", predicate), ("$ot", objectType), ("$oid", objectId), ("$label", label ?? ""), ("$source", provenance));
    }

    private static IEnumerable<string> AllPeople(TrvPackPeople? people)
        => (people?.Hosts ?? new()).Concat(people?.Guests ?? new()).Concat(people?.Callers ?? new()).Concat(people?.MentionedPeople ?? new())
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase);

    private static string Normalise(string value)
    {
        var chars = value.ToLowerInvariant().Select(x => char.IsLetterOrDigit(x) ? x : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string StableRowId(params string?[] values)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", values)))).ToLowerInvariant()[..32];
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static T FromJson<T>(string value) => JsonSerializer.Deserialize<T>(value, JsonOptions) ?? throw new JsonException($"Could not read {typeof(T).Name}.");
    private static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
    private static string TemporaryDatabasePath() => Path.Combine(Path.GetTempPath(), $"radiovault-knowledge-{Guid.NewGuid():N}.sqlite");
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private static string BuildInstructions(TrvKnowledgePack pack) => $"""
        # Radio Vault Archive Knowledge Database

        This inspectable SQLite file is a portable Knowledge database for {pack.Manifest.Show}{(pack.Manifest.Year.HasValue ? $" ({pack.Manifest.Year})" : "")}.
        Its goal is to let a research agent understand the archive, enrich it with well-sourced knowledge, connect related people/topics/events,
        and return one file that Radio Vault can preview and import safely.

        Export scope: {pack.Manifest.ExportScope}.
        {BuildScopeAssignment(pack.Manifest.ExportScope)}

        Export contents: {pack.Broadcasts.Count:N0} present broadcasts, {pack.MissingBroadcasts.Count:N0} known archive gaps,
        {pack.Transcripts.Count:N0} transcripts, {pack.Wiki?.Pages.Count ?? 0:N0} Explore pages,
        {pack.Wiki?.Sources.Count ?? 0:N0} sources and {pack.Wiki?.TimelineEvents.Count ?? 0:N0} timeline events.

        ## First steps
        1. Read every row in pack_documentation in sort_order.
        2. Read pack_schema before editing any table. JSON columns are the authoritative import payload; indexed columns must remain consistent with them.
        3. Use knowledge_links as the graph joining broadcasts, people, topics, Explore pages, sources, Moments and timeline events.
        4. Use ai_broadcast_knowledge and archive_broadcasts to discover evidence without guessing identities.
        5. Record a concise account of material work in pack_change_log.

        Preserve broadcast IDs, episode IDs, Moment IDs, page GUIDs, source GUIDs and base revisions. Never invent an archive identity.
        Transcript speech is primary audio evidence, not automatically verified fact. Every factual article claim should cite wiki_sources through wiki_citations.
        Human edits are protected by base_revision. Images belong in wiki_images with provenance, captions and temporal metadata in metadata_json.
        Add genuinely absent broadcasts with is_missing=1. Leave unsupported facts empty and state uncertainty in quality fields or source notes.
        Run PRAGMA quick_check, check references, and return this same SQLite database with the .trvknowledge extension.
        """;

    private static string BuildScopeAssignment(string? scope) => scope?.Trim().ToLowerInvariant() switch
    {
        "undated" => """
            This is a focused date-research assignment. Every included record lacks an established broadcast date.
            Research and fill broadcast_date only when reliable evidence supports an exact YYYY-MM-DD date. Apply one confirmed date
            consistently to every legitimate part of the same multipart broadcast. Preserve broadcast_id, show_name, part numbers and
            all unrelated metadata. If an exact date cannot be supported, leave it empty and document the evidence and uncertainty.
            """,
        "missing-topics-or-summaries" => """
            This is a focused metadata-research assignment. Every included record is missing topics, a summary, or both.
            Add a concise factual summary and useful topic labels from the supplied transcript and sources. Preserve broadcast_id,
            broadcast_date and all unrelated metadata. Do not invent subjects that the evidence does not support; leave a field empty
            when the available material is insufficient.
            """,
        _ => "This is the complete archive-wide Knowledge export, including Explore and linked research material."
    };

    private const string ResearchRules = """
        # Research and evidence rules

        * Distinguish what a broadcast or transcript directly demonstrates from contextual claims supplied by outside sources.
        * Cite factual Explore prose. Store each source once in wiki_sources and connect it with wiki_citations.
        * Prefer stable episode_id or broadcast_uid references. Titles and headlines are display text and may change.
        * Never fabricate a date. Use the available precision fields and explain uncertainty in notes.
        * Do not rewrite transcript wording merely to make an article read better. Correct transcripts only from the audio evidence.
        * Preserve contrary evidence and ambiguity. Radio Vault is an archive, not a system for forcing uncertain material into one answer.
        * Images require a caption, useful alt text, provenance/licensing where known, and the date or time range they represent where possible.
        """;

    private const string IdentityRules = """
        # Stable identities and relationships

        IDs are the durable links; names, titles, headlines and slugs are labels. Do not replace an ID because wording changes.

        * research_broadcasts.broadcast_id connects Research records and transcripts to a broadcast identity.
        * archive_broadcasts.episode_id is Radio Vault's server identity for opening Broadcast Info.
        * wiki_pages.page_id is the durable Explore-page GUID. base_revision protects newer human work during import.
        * wiki_sources.source_id is cited by wiki_citations.source_id and may also support images and timeline events.
        * Moments use numeric moment_id values and must remain tied to their episode and position.
        * knowledge_links is a searchable graph/index. Keep it consistent when adding entities or relationships.

        If two pages appear to describe the same subject, preserve both IDs and document the proposed merge unless the database already supplies an alias or canonical mapping.
        """;

    private const string ReturnChecklist = """
        # Safe return checklist

        Before returning the file:

        1. Keep the filename extension .trvknowledge and the SQLite file itself—not SQL text, JSON, ZIP or a renamed document.
        2. Do not change pack_settings.manifest format or schema_version.
        3. Ensure every JSON payload is valid and agrees with the important indexed columns beside it.
        4. Ensure every page/source/image/event ID is unique and every citation or relationship target exists.
        5. Ensure image byte_count and sha256 still match the BLOB content.
        6. Run PRAGMA quick_check and require the result `ok`.
        7. Add a pack_change_log row describing the agent, changes, evidence limits and any unresolved questions.
        8. Do not delete unknown fields or tables. Radio Vault will show a preview and protect conflicting human revisions at import.
        """;

    private static readonly PackSchemaDocument[] SchemaDocumentation =
    {
        new("pack_settings", "Format manifest and archive context.", "key", "Preserve format/schema keys; update only documented values.", "{\"value_json\":\"Authoritative JSON value for the named setting\"}"),
        new("research_broadcasts", "Present and missing broadcast Research records.", "broadcast_id plus is_missing", "Edit record_json first, then keep show/date/slot/part index columns consistent.", "{\"record_json\":\"Authoritative TrvPackBroadcast payload\",\"is_missing\":\"1 only for a known archive gap\"}"),
        new("transcripts", "Transcript documents connected to broadcasts.", "row_id and broadcast_id", "Preserve timing and speaker evidence; edit transcript_json and full_text consistently.", "{\"transcript_json\":\"Authoritative transcript including timed segments\",\"full_text\":\"Searchable copy\"}"),
        new("wiki_pages", "Human-readable Explore articles.", "page_id GUID", "Preserve page_id and base_revision; edit body_markdown and metadata columns.", "{\"body_markdown\":\"Article Markdown\",\"aliases_json\":\"Alternative page names\"}"),
        new("wiki_sources", "Reusable evidence sources for article claims.", "source_id GUID", "Keep source identity stable; supply a readable title and provenance.", "{\"data_json\":\"Authoritative WikiSourceRecord\",\"episode_id\":\"Optional stable archive episode\"}"),
        new("wiki_citations", "Links article claims to sources.", "citation_id GUID", "page_id and source_id must exist; ordinal controls display order.", "{\"data_json\":\"Authoritative WikiCitationRecord\"}"),
        new("wiki_images", "Image BLOBs plus provenance and temporal metadata.", "image_id GUID", "Keep content, sha256, byte_count and metadata_json consistent.", "{\"content\":\"Original image bytes\",\"metadata_json\":\"Caption, alt text, rights and date context\"}"),
        new("wiki_timeline_events", "Dated events used by vertical timeline exploration.", "event_id GUID", "page_id must exist; retain source/image/broadcast links in data_json.", "{\"data_json\":\"Authoritative event record and linked archive IDs\"}"),
        new("archive_broadcasts", "Read-only archive context used to find real broadcasts.", "episode_id", "Treat as reference context; do not invent or renumber episodes.", "{\"data_json\":\"Full archive context\",\"broadcast_uid\":\"Portable stable broadcast identity\"}"),
        new("knowledge_links", "Cross-domain graph joining archive and knowledge entities.", "link_id", "Add links only when both endpoint identities are supported.", "{\"subject_id\":\"Stable source identity\",\"predicate\":\"Relationship\",\"object_id\":\"Stable target identity\"}"),
        new("pack_change_log", "Human-readable audit trail of AI work performed on this portable file.", "change_id", "Append one row per material work session; do not erase earlier rows.", "{\"summary\":\"Short change summary\",\"details_markdown\":\"Evidence limits and unresolved work\"}")
    };

    private sealed record PackSchemaDocument(string Table, string Purpose, string Identity, string Policy, string ColumnsJson);
}

internal sealed class FlexibleStringListJsonConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return new();
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            var single = FlexibleStringJsonConverter.ReadValue(ref reader);
            return string.IsNullOrWhiteSpace(single) ? new() : new() { single };
        }
        var result = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                throw new JsonException("A text list cannot contain nested objects or arrays.");
            var value = FlexibleStringJsonConverter.ReadValue(ref reader);
            if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value ?? new()) writer.WriteStringValue(item);
        writer.WriteEndArray();
    }
}

internal sealed class FlexibleStringJsonConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => ReadValue(ref reader);
    internal static string? ReadValue(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.Null => null,
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number => reader.TryGetInt64(out var integer) ? integer.ToString(CultureInfo.InvariantCulture) : reader.GetDouble().ToString("R", CultureInfo.InvariantCulture),
        JsonTokenType.True => bool.TrueString,
        JsonTokenType.False => bool.FalseString,
        _ => throw new JsonException("Expected text, a number, a Boolean or null.")
    };
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
