using Microsoft.Data.Sqlite;
using System.Text.Json;
using TheRadioVault.Models;
using TheRadioVault.Research.Models;

namespace TheRadioVault.Services;

public sealed partial class DatabaseService
{
    public IReadOnlyList<ResearchLibraryBrowseRecord> GetResearchLibraryRecords()
    {
        using var connection = OpenConnection();
        return ReadResearchLibraryBrowseRecords(connection, null);
    }

    public IReadOnlyList<ResearchAuditRecord> GetResearchAuditRecords(CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var builders = new Dictionary<long, ResearchAuditRecordBuilder>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT rb.id,rb.episode_id,c.name,rb.air_date,rb.headline,rb.summary,
                       rb.research_state,rb.confidence
                FROM research_broadcasts rb
                JOIN collections c ON c.id=rb.collection_id
                ORDER BY rb.id
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = reader.GetInt64(0);
                builders[id] = new ResearchAuditRecordBuilder
                {
                    ResearchBroadcastId = id,
                    EpisodeId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    Show = reader.GetString(2),
                    BroadcastDate = ParseResearchDate(reader, 3),
                    Headline = reader.GetString(4),
                    Summary = reader.GetString(5),
                    ResearchState = reader.GetString(6),
                    Confidence = reader.GetInt32(7)
                };
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT research_broadcast_id,name,role FROM research_people ORDER BY research_broadcast_id,id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (builders.TryGetValue(reader.GetInt64(0), out var builder))
                    builder.People.Add(new ResearchAuditPerson(reader.GetString(1), reader.GetString(2)));
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT research_broadcast_id,topic FROM research_topics ORDER BY research_broadcast_id,id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (builders.TryGetValue(reader.GetInt64(0), out var builder))
                    builder.Topics.Add(reader.GetString(1));
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT research_broadcast_id,url,title,source_type,confidence FROM research_sources ORDER BY research_broadcast_id,id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (builders.TryGetValue(reader.GetInt64(0), out var builder))
                    builder.Sources.Add(new ResearchAuditSource(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)));
            }
        }

        return builders.Values.Select(x => x.Build()).ToArray();
    }

    private sealed class ResearchAuditRecordBuilder
    {
        public long ResearchBroadcastId { get; init; }
        public long? EpisodeId { get; init; }
        public string Show { get; init; } = string.Empty;
        public DateTime? BroadcastDate { get; init; }
        public string Headline { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public string ResearchState { get; init; } = string.Empty;
        public int Confidence { get; init; }
        public List<ResearchAuditPerson> People { get; } = new();
        public List<string> Topics { get; } = new();
        public List<ResearchAuditSource> Sources { get; } = new();

        public ResearchAuditRecord Build() => new()
        {
            ResearchBroadcastId = ResearchBroadcastId,
            EpisodeId = EpisodeId,
            Show = Show,
            BroadcastDate = BroadcastDate,
            Headline = Headline,
            Summary = Summary,
            ResearchState = ResearchState,
            Confidence = Confidence,
            HasAudio = EpisodeId.HasValue,
            People = People.ToArray(),
            Topics = Topics.ToArray(),
            Sources = Sources.ToArray()
        };
    }

    private static List<ResearchLibraryBrowseRecord> ReadResearchLibraryBrowseRecords(SqliteConnection connection, long? researchId)
    {
        var result = new List<ResearchLibraryBrowseRecord>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rb.id,rb.episode_id,rb.legacy_missing_research_id,rb.source_broadcast_id,
                   c.name,rb.air_date,rb.slot,rb.part_number,rb.total_parts,rb.headline,rb.summary,
                   rb.research_state,rb.existence_status,rb.confidence,rb.confidence_reason,
                   rb.needs_review,rb.updated_at,
                   (SELECT COUNT(*) FROM research_conflicts rc
                    WHERE rc.research_broadcast_id=rb.id AND rc.resolution='unresolved'),
                   (SELECT COUNT(*) FROM research_reconciliation_candidates rrc
                    WHERE rrc.research_broadcast_id=rb.id AND rrc.status='pending' AND rrc.requires_review=1),
                   (SELECT COUNT(*) FROM research_sources rs WHERE rs.research_broadcast_id=rb.id),
                   (SELECT COUNT(*) FROM research_people rp WHERE rp.research_broadcast_id=rb.id),
                   (SELECT COUNT(*) FROM research_topics rt WHERE rt.research_broadcast_id=rb.id),
                   (SELECT COUNT(*) FROM research_moments rm WHERE rm.research_broadcast_id=rb.id)
            FROM research_broadcasts rb
            JOIN collections c ON c.id=rb.collection_id
            WHERE ($id IS NULL OR rb.id=$id)
            ORDER BY c.sort_name,rb.air_date,rb.slot,rb.part_number,rb.id
            """;
        command.Parameters.AddWithValue("$id", researchId.HasValue ? researchId.Value : DBNull.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ResearchLibraryBrowseRecord
            {
                Id = reader.GetInt64(0),
                EpisodeId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                LegacyMissingResearchId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                BroadcastId = reader.GetString(3),
                Show = reader.GetString(4),
                BroadcastDate = ParseResearchDate(reader, 5),
                Slot = reader.GetString(6),
                PartNumber = reader.GetInt32(7),
                TotalParts = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                Headline = reader.GetString(9),
                Summary = reader.GetString(10),
                ResearchState = reader.GetString(11),
                ExistenceStatus = reader.GetString(12),
                Confidence = reader.GetInt32(13),
                ConfidenceReason = reader.GetString(14),
                NeedsReview = reader.GetInt32(15) == 1,
                UpdatedAt = ParseResearchTimestamp(reader.GetString(16)),
                ConflictCount = Convert.ToInt32(reader.GetInt64(17)),
                PendingDecisionCount = Convert.ToInt32(reader.GetInt64(18)),
                SourceCount = Convert.ToInt32(reader.GetInt64(19)),
                PeopleCount = Convert.ToInt32(reader.GetInt64(20)),
                TopicCount = Convert.ToInt32(reader.GetInt64(21)),
                MomentCount = Convert.ToInt32(reader.GetInt64(22))
            });
        }
        return result;
    }

    public ResearchLibraryRecordDetails? GetResearchLibraryRecordDetails(long id)
    {
        using var connection = OpenConnection();
        var record = ReadResearchLibraryBrowseRecords(connection, id).FirstOrDefault();
        if (record is null) return null;

        string json;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT research_json FROM research_broadcasts WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            json = Convert.ToString(command.ExecuteScalar()) ?? "";
        }

        TrvPackBroadcast broadcast;
        try { broadcast = KnowledgePackService.DeserializeBroadcast(json) ?? new TrvPackBroadcast(); }
        catch { broadcast = new TrvPackBroadcast(); }

        broadcast.Show = string.IsNullOrWhiteSpace(broadcast.Show) ? record.Show : broadcast.Show;
        broadcast.BroadcastId = string.IsNullOrWhiteSpace(broadcast.BroadcastId) ? record.BroadcastId : broadcast.BroadcastId;
        broadcast.BroadcastDate = string.IsNullOrWhiteSpace(broadcast.BroadcastDate)
            ? record.BroadcastDate?.ToString("yyyy-MM-dd") ?? ""
            : broadcast.BroadcastDate;
        broadcast.Research ??= new TrvPackResearch();
        broadcast.Research.People ??= new TrvPackPeople();
        broadcast.Research.Topics ??= new List<string>();
        broadcast.Research.Guests ??= new List<string>();
        broadcast.Research.Moments ??= new List<TrvPackMoment>();
        broadcast.Sources ??= new List<TrvPackSource>();

        var hosts = new List<string>();
        var guests = new List<string>();
        var callers = new List<string>();
        var mentioned = new List<string>();
        using (var people = connection.CreateCommand())
        {
            people.CommandText = "SELECT name,role FROM research_people WHERE research_broadcast_id=$id ORDER BY role,name COLLATE NOCASE";
            people.Parameters.AddWithValue("$id", id);
            using var reader = people.ExecuteReader();
            while (reader.Read())
            {
                var target = reader.GetString(1) switch
                {
                    "host" => hosts,
                    "guest" => guests,
                    "caller" => callers,
                    _ => mentioned
                };
                target.Add(reader.GetString(0));
            }
        }

        var topics = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT topic FROM research_topics WHERE research_broadcast_id=$id ORDER BY topic COLLATE NOCASE";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read()) topics.Add(reader.GetString(0));
        }

        var sources = new List<TrvPackSource>();
        var sourceDetails = new List<ResearchSourceDetailRecord>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT url,title,publisher,COALESCE(accessed_at,''),supports,notes,source_type,confidence
                FROM research_sources WHERE research_broadcast_id=$id
                ORDER BY source_type,title COLLATE NOCASE,url COLLATE NOCASE
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var supports = SplitResearchList(reader.GetString(4));
                sources.Add(new TrvPackSource
                {
                    Url = reader.GetString(0),
                    Title = reader.GetString(1),
                    Publisher = reader.GetString(2),
                    Accessed = reader.GetString(3),
                    Supports = supports,
                    Notes = reader.GetString(5)
                });
                sourceDetails.Add(new ResearchSourceDetailRecord
                {
                    Url = reader.GetString(0),
                    Title = reader.GetString(1),
                    Publisher = reader.GetString(2),
                    Accessed = reader.GetString(3),
                    Supports = supports,
                    Notes = reader.GetString(5),
                    SourceType = reader.GetString(6),
                    Confidence = reader.GetInt32(7)
                });
            }
        }

        var moments = new List<TrvPackMoment>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT timestamp_seconds,title,description,tags
                FROM research_moments WHERE research_broadcast_id=$id
                ORDER BY timestamp_seconds,title COLLATE NOCASE
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                moments.Add(new TrvPackMoment
                {
                    TimestampSeconds = reader.GetInt64(0),
                    Title = reader.GetString(1),
                    Description = reader.GetString(2),
                    Tags = SplitResearchList(reader.GetString(3))
                });
            }
        }

        var conflicts = new List<ResearchConflictRecord>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id,research_broadcast_id,episode_id,field_name,existing_value,incoming_value,resolution,created_at
                FROM research_conflicts
                WHERE research_broadcast_id=$id AND resolution='unresolved'
                ORDER BY created_at DESC,id DESC
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                conflicts.Add(new ResearchConflictRecord
                {
                    Id = reader.GetInt64(0),
                    ResearchBroadcastId = reader.GetInt64(1),
                    EpisodeId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    FieldName = reader.GetString(3),
                    ExistingValue = reader.GetString(4),
                    IncomingValue = reader.GetString(5),
                    Resolution = reader.GetString(6),
                    CreatedAt = ParseResearchTimestamp(reader.GetString(7))
                });
            }
        }

        return new ResearchLibraryRecordDetails
        {
            Record = record,
            Broadcast = broadcast,
            Hosts = hosts,
            Guests = guests,
            Callers = callers,
            MentionedPeople = mentioned,
            Topics = topics,
            Sources = sources,
            SourceDetails = sourceDetails,
            Moments = moments,
            Conflicts = conflicts
        };
    }

    public IReadOnlyList<ResearchShowHealthRecord> GetResearchShowHealth()
    {
        var result = new List<ResearchShowHealthRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id,c.name,
                   COUNT(rb.id),
                   SUM(CASE WHEN rb.episode_id IS NOT NULL THEN 1 ELSE 0 END),
                   SUM(CASE WHEN rb.episode_id IS NULL THEN 1 ELSE 0 END),
                   SUM(CASE WHEN rb.needs_review=1 THEN 1 ELSE 0 END),
                   SUM(CASE WHEN EXISTS(SELECT 1 FROM research_conflicts rc WHERE rc.research_broadcast_id=rb.id AND rc.resolution='unresolved') THEN 1 ELSE 0 END),
                   SUM(CASE WHEN trim(rb.summary)<>'' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN EXISTS(SELECT 1 FROM research_people rp WHERE rp.research_broadcast_id=rb.id) THEN 1 ELSE 0 END),
                   SUM(CASE WHEN EXISTS(SELECT 1 FROM research_topics rt WHERE rt.research_broadcast_id=rb.id) THEN 1 ELSE 0 END),
                   SUM(CASE WHEN EXISTS(SELECT 1 FROM research_sources rs WHERE rs.research_broadcast_id=rb.id) THEN 1 ELSE 0 END)
            FROM collections c
            JOIN research_broadcasts rb ON rb.collection_id=c.id
            GROUP BY c.id,c.name,c.sort_name
            ORDER BY c.sort_name,c.name
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ResearchShowHealthRecord
            {
                CollectionId = reader.GetInt32(0),
                Show = reader.GetString(1),
                Total = Convert.ToInt32(reader.GetInt64(2)),
                Attached = Convert.ToInt32(reader.GetInt64(3)),
                Missing = Convert.ToInt32(reader.GetInt64(4)),
                NeedsReview = Convert.ToInt32(reader.GetInt64(5)),
                Conflicts = Convert.ToInt32(reader.GetInt64(6)),
                WithSummaries = Convert.ToInt32(reader.GetInt64(7)),
                WithPeople = Convert.ToInt32(reader.GetInt64(8)),
                WithTopics = Convert.ToInt32(reader.GetInt64(9)),
                WithSources = Convert.ToInt32(reader.GetInt64(10))
            });
        }
        return result;
    }

    public IReadOnlyList<ResearchImportRunRecord> GetResearchImportHistory(int limit = 250)
    {
        var result = new List<ResearchImportRunRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,package_name,package_sha256,schema_version,app_version,imported_at,
                   imported_count,matched_count,missing_count,conflict_count,summary_json,rollback_json,
                   status,restored_change_count,blocked_rollback_count,last_rollback_at,
                   (SELECT COUNT(*) FROM research_import_changes ric WHERE ric.import_run_id=research_import_runs.id)
            FROM research_import_runs
            ORDER BY imported_at DESC,id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var summaryJson = reader.GetString(10);
            var rollbackJson = reader.GetString(11);
            var record = new ResearchImportRunRecord
            {
                Id = reader.GetInt64(0),
                PackageName = reader.GetString(1),
                PackageHash = reader.GetString(2),
                SchemaVersion = reader.GetInt32(3),
                AppVersion = reader.GetString(4),
                ImportedAt = ParseResearchTimestamp(reader.GetString(5)),
                ImportedCount = reader.GetInt32(6),
                MatchedCount = reader.GetInt32(7),
                MissingCount = reader.GetInt32(8),
                ConflictCount = reader.GetInt32(9),
                Status = reader.GetString(12),
                RestoredChangeCount = reader.GetInt32(13),
                BlockedRollbackCount = reader.GetInt32(14),
                LastRollbackAt = reader.IsDBNull(15) ? null : ParseResearchTimestamp(reader.GetString(15)),
                ChangeCount = Convert.ToInt32(reader.GetInt64(16))
            };
            ApplyResearchImportSummary(record, summaryJson, rollbackJson);
            result.Add(record);
        }
        return result;
    }

    public IReadOnlyList<ResearchImportChangeRecord> GetResearchImportChanges(long importRunId)
    {
        var result = new List<ResearchImportChangeRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,import_run_id,research_broadcast_id,episode_id,record_identity,
                   field_name,before_value,after_value,decision,reason,created_at
            FROM research_import_changes
            WHERE import_run_id=$run
            ORDER BY id
            """;
        command.Parameters.AddWithValue("$run", importRunId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ResearchImportChangeRecord
            {
                Id = reader.GetInt64(0),
                ImportRunId = reader.GetInt64(1),
                ResearchBroadcastId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                EpisodeId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                RecordIdentity = reader.GetString(4),
                FieldName = reader.GetString(5),
                BeforeValue = reader.GetString(6),
                AfterValue = reader.GetString(7),
                Decision = reader.GetString(8),
                Reason = reader.GetString(9),
                CreatedAt = ParseResearchTimestamp(reader.GetString(10))
            });
        }
        return result;
    }

    private static void ApplyResearchImportSummary(ResearchImportRunRecord record, string summaryJson, string rollbackJson)
    {
        if (string.IsNullOrWhiteSpace(record.Status)) record.Status = "completed";
        try
        {
            using var document = JsonDocument.Parse(summaryJson);
            var root = document.RootElement;
            if (record.Status == "completed" && root.TryGetProperty("Status", out var status))
                record.Status = status.GetString() ?? "completed";
            if (root.TryGetProperty("FieldsApplied", out var applied)) record.FieldsApplied = applied.GetInt32();
            if (root.TryGetProperty("FieldsMerged", out var merged)) record.FieldsMerged = merged.GetInt32();
            if (root.TryGetProperty("FieldsPreserved", out var preserved)) record.FieldsPreserved = preserved.GetInt32();
            if (root.TryGetProperty("ManualFieldsProtected", out var protectedFields)) record.ManualFieldsProtected = protectedFields.GetInt32();
        }
        catch
        {
            // Schema-31 import summaries did not contain merge-ledger fields.
        }

        try
        {
            using var document = JsonDocument.Parse(rollbackJson);
            record.RollbackDataCaptured = document.RootElement.TryGetProperty("SnapshotPath", out var path)
                                          && !string.IsNullOrWhiteSpace(path.GetString())
                                          && File.Exists(path.GetString());
        }
        catch
        {
            record.RollbackDataCaptured = false;
        }
    }

    public IReadOnlyList<ResearchSourceSummaryRecord> GetResearchSourceSummary()
    {
        var result = new List<ResearchSourceSummaryRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT publisher,source_type,url,COUNT(*),COUNT(DISTINCT research_broadcast_id),
                   CAST(ROUND(AVG(confidence)) AS INTEGER),MAX(accessed_at)
            FROM research_sources
            GROUP BY publisher,source_type,
                     CASE
                       WHEN instr(replace(replace(url,'https://',''),'http://',''),'/')>0
                         THEN substr(replace(replace(url,'https://',''),'http://',''),1,instr(replace(replace(url,'https://',''),'http://',''),'/')-1)
                       ELSE replace(replace(url,'https://',''),'http://','')
                     END
            ORDER BY COUNT(DISTINCT research_broadcast_id) DESC,COUNT(*) DESC,publisher COLLATE NOCASE
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var domain = ExtractResearchDomain(reader.GetString(2));
            result.Add(new ResearchSourceSummaryRecord
            {
                Publisher = reader.GetString(0),
                SourceType = reader.GetString(1),
                Domain = domain,
                SourceCount = Convert.ToInt32(reader.GetInt64(3)),
                BroadcastCount = Convert.ToInt32(reader.GetInt64(4)),
                AverageConfidence = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                LatestAccessedAt = reader.IsDBNull(6) ? null : ParseResearchTimestamp(reader.GetString(6))
            });
        }
        return result;
    }

    public IReadOnlyList<ResearchConflictTriageItem> GetUnresolvedResearchConflicts()
    {
        var result = new List<ResearchConflictTriageItem>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rc.id,rc.research_broadcast_id,rc.episode_id,c.name,rb.air_date,rb.slot,
                   rb.part_number,rb.headline,rc.field_name,rc.existing_value,rc.incoming_value,
                   rc.created_at,
                   (SELECT COUNT(*) FROM research_sources rs WHERE rs.research_broadcast_id=rb.id),
                   rb.confidence
            FROM research_conflicts rc
            JOIN research_broadcasts rb ON rb.id=rc.research_broadcast_id
            JOIN collections c ON c.id=rb.collection_id
            WHERE rc.resolution='unresolved'
            ORDER BY c.sort_name,rb.air_date,rb.slot,rb.part_number,rc.created_at,rc.id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ResearchConflictTriageItem
            {
                Id = reader.GetInt64(0),
                ResearchBroadcastId = reader.GetInt64(1),
                EpisodeId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                Show = reader.GetString(3),
                BroadcastDate = ParseResearchDate(reader, 4),
                Slot = reader.GetString(5),
                PartNumber = reader.GetInt32(6),
                Headline = reader.GetString(7),
                FieldName = reader.GetString(8),
                ExistingValue = reader.GetString(9),
                IncomingValue = reader.GetString(10),
                CreatedAt = ParseResearchTimestamp(reader.GetString(11)),
                SourceCount = Convert.ToInt32(reader.GetInt64(12)),
                Confidence = reader.GetInt32(13)
            });
        }
        return result;
    }

    public string UndoResearchConflictResolution(long conflictId)
    {
        using var connection = OpenConnection();
        long researchId;
        long? episodeId;
        string fieldName;
        string existingValue;
        string incomingValue;
        string resolution;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT research_broadcast_id,episode_id,field_name,existing_value,incoming_value,resolution
                FROM research_conflicts
                WHERE id=$id AND resolution IN ('use_incoming','keep_existing')
                """;
            read.Parameters.AddWithValue("$id", conflictId);
            using var reader = read.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("This conflict can no longer be undone.");
            researchId = reader.GetInt64(0);
            episodeId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            fieldName = reader.GetString(2);
            existingValue = reader.GetString(3);
            incomingValue = reader.GetString(4);
            resolution = reader.GetString(5);
        }

        var (episodeColumn, researchColumn) = fieldName switch
        {
            "headline" => ("title", "headline"),
            "summary" => ("description", "summary"),
            "station" => ("edition", "station"),
            _ => throw new InvalidOperationException($"Radio Vault cannot safely undo the '{fieldName}' field.")
        };

        var expectedResearchValue = resolution == "keep_existing" ? existingValue : incomingValue;
        using (var guard = connection.CreateCommand())
        {
            guard.CommandText = $"SELECT {researchColumn} FROM research_broadcasts WHERE id=$research";
            guard.Parameters.AddWithValue("$research", researchId);
            var current = Convert.ToString(guard.ExecuteScalar()) ?? string.Empty;
            if (!string.Equals(current, expectedResearchValue, StringComparison.Ordinal))
                throw new InvalidOperationException("The research value changed after this decision, so Radio Vault will not overwrite the later edit.");
        }
        if (resolution == "use_incoming" && episodeId.HasValue)
        {
            using var guard = connection.CreateCommand();
            guard.CommandText = $"SELECT {episodeColumn} FROM episodes WHERE id=$episode";
            guard.Parameters.AddWithValue("$episode", episodeId.Value);
            var current = Convert.ToString(guard.ExecuteScalar()) ?? string.Empty;
            if (!string.Equals(current, incomingValue, StringComparison.Ordinal))
                throw new InvalidOperationException("The library value changed after this decision, so Radio Vault will not overwrite the later edit.");
        }

        string researchJson;
        using (var readJson = connection.CreateCommand())
        {
            readJson.CommandText = "SELECT research_json FROM research_broadcasts WHERE id=$id";
            readJson.Parameters.AddWithValue("$id", researchId);
            researchJson = Convert.ToString(readJson.ExecuteScalar()) ?? string.Empty;
        }
        TrvPackBroadcast payload;
        try { payload = KnowledgePackService.DeserializeBroadcast(researchJson) ?? new TrvPackBroadcast(); }
        catch { payload = new TrvPackBroadcast(); }
        payload.Research ??= new TrvPackResearch();
        payload.Research.Broadcast ??= new TrvPackBroadcastMetadata();
        switch (fieldName)
        {
            case "headline": payload.Research.Headline = incomingValue; break;
            case "summary": payload.Research.Summary = incomingValue; break;
            case "station": payload.Research.Broadcast.Station = incomingValue; payload.Research.Edition = incomingValue; break;
        }
        var updatedJson = KnowledgePackService.SerializeBroadcast(payload);

        using var transaction = connection.BeginTransaction();
        if (resolution == "use_incoming" && episodeId.HasValue)
        {
            using var restoreEpisode = connection.CreateCommand();
            restoreEpisode.Transaction = transaction;
            restoreEpisode.CommandText = $"UPDATE episodes SET {episodeColumn}=$value,user_modified=1,updated_at=$now WHERE id=$episode";
            restoreEpisode.Parameters.AddWithValue("$value", existingValue);
            restoreEpisode.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            restoreEpisode.Parameters.AddWithValue("$episode", episodeId.Value);
            if (restoreEpisode.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The linked archive broadcast could not be restored.");
        }
        using (var restoreResearch = connection.CreateCommand())
        {
            restoreResearch.Transaction = transaction;
            restoreResearch.CommandText = $"UPDATE research_broadcasts SET {researchColumn}=$value,research_json=$json,needs_review=1,research_state='conflicting_information',updated_at=$now WHERE id=$research";
            restoreResearch.Parameters.AddWithValue("$value", incomingValue);
            restoreResearch.Parameters.AddWithValue("$json", updatedJson);
            restoreResearch.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            restoreResearch.Parameters.AddWithValue("$research", researchId);
            if (restoreResearch.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The research record could not be restored.");
        }
        using (var reopen = connection.CreateCommand())
        {
            reopen.Transaction = transaction;
            reopen.CommandText = "UPDATE research_conflicts SET resolution='unresolved',resolved_at=NULL WHERE id=$id";
            reopen.Parameters.AddWithValue("$id", conflictId);
            if (reopen.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The conflict could not be reopened.");
        }
        transaction.Commit();
        SyncLegacyResearchState(connection, researchId, "ambiguous", episodeId, $"Reopened the {fieldName} conflict after undo.");
        return $"Undid the last {fieldName} choice. The conflict is waiting again.";
    }

    public void MarkResearchLibraryRecordReviewed(long researchId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE research_broadcasts
            SET needs_review=CASE
                    WHEN EXISTS(SELECT 1 FROM research_conflicts rc WHERE rc.research_broadcast_id=research_broadcasts.id AND rc.resolution='unresolved') THEN 1
                    WHEN EXISTS(SELECT 1 FROM research_reconciliation_candidates rrc WHERE rrc.research_broadcast_id=research_broadcasts.id AND rrc.status='pending' AND rrc.requires_review=1) THEN 1
                    ELSE 0 END,
                research_state=CASE
                    WHEN EXISTS(SELECT 1 FROM research_conflicts rc WHERE rc.research_broadcast_id=research_broadcasts.id AND rc.resolution='unresolved') THEN 'conflicting_information'
                    WHEN EXISTS(SELECT 1 FROM research_reconciliation_candidates rrc WHERE rrc.research_broadcast_id=research_broadcasts.id AND rrc.status='pending' AND rrc.requires_review=1) THEN 'conflicting_information'
                    WHEN episode_id IS NOT NULL THEN 'in_library'
                    ELSE 'missing_recording' END,
                updated_at=$now
            WHERE id=$id
            """;
        command.Parameters.AddWithValue("$id", researchId);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public string ResolveResearchConflict(long conflictId, bool useIncomingValue)
    {
        using var connection = OpenConnection();
        long researchId;
        long? episodeId;
        string fieldName;
        string existingValue;
        string incomingValue;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT research_broadcast_id,episode_id,field_name,existing_value,incoming_value
                FROM research_conflicts
                WHERE id=$id AND resolution='unresolved'
                """;
            read.Parameters.AddWithValue("$id", conflictId);
            using var reader = read.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("This research conflict has already been resolved or no longer exists.");
            researchId = reader.GetInt64(0);
            episodeId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            fieldName = reader.GetString(2);
            existingValue = reader.GetString(3);
            incomingValue = reader.GetString(4);
        }

        var (episodeColumn, researchColumn) = fieldName switch
        {
            "headline" => ("title", "headline"),
            "summary" => ("description", "summary"),
            "station" => ("edition", "station"),
            _ => throw new InvalidOperationException($"Radio Vault cannot safely resolve the '{fieldName}' field automatically.")
        };
        var chosenValue = useIncomingValue ? incomingValue : existingValue;
        if (useIncomingValue && !episodeId.HasValue)
            throw new InvalidOperationException("The linked archive broadcast is no longer available.");

        string researchJson;
        using (var readJson = connection.CreateCommand())
        {
            readJson.CommandText = "SELECT research_json FROM research_broadcasts WHERE id=$id";
            readJson.Parameters.AddWithValue("$id", researchId);
            researchJson = Convert.ToString(readJson.ExecuteScalar()) ?? "";
        }
        TrvPackBroadcast payload;
        try { payload = KnowledgePackService.DeserializeBroadcast(researchJson) ?? new TrvPackBroadcast(); }
        catch { payload = new TrvPackBroadcast(); }
        payload.Research ??= new TrvPackResearch();
        payload.Research.Broadcast ??= new TrvPackBroadcastMetadata();
        switch (fieldName)
        {
            case "headline": payload.Research.Headline = chosenValue; break;
            case "summary": payload.Research.Summary = chosenValue; break;
            case "station": payload.Research.Broadcast.Station = chosenValue; payload.Research.Edition = chosenValue; break;
        }
        var updatedJson = KnowledgePackService.SerializeBroadcast(payload);

        using var transaction = connection.BeginTransaction();
        if (useIncomingValue)
        {
            using var updateEpisode = connection.CreateCommand();
            updateEpisode.Transaction = transaction;
            updateEpisode.CommandText = $"UPDATE episodes SET {episodeColumn}=$value,user_modified=1,updated_at=$now WHERE id=$episode";
            updateEpisode.Parameters.AddWithValue("$value", chosenValue);
            updateEpisode.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            updateEpisode.Parameters.AddWithValue("$episode", episodeId!.Value);
            if (updateEpisode.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The linked archive broadcast could not be updated.");
        }

        using (var updateResearch = connection.CreateCommand())
        {
            updateResearch.Transaction = transaction;
            updateResearch.CommandText = $"UPDATE research_broadcasts SET {researchColumn}=$value,research_json=$json,updated_at=$now WHERE id=$research";
            updateResearch.Parameters.AddWithValue("$value", chosenValue);
            updateResearch.Parameters.AddWithValue("$json", updatedJson);
            updateResearch.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            updateResearch.Parameters.AddWithValue("$research", researchId);
            updateResearch.ExecuteNonQuery();
        }
        using (var resolve = connection.CreateCommand())
        {
            resolve.Transaction = transaction;
            resolve.CommandText = "UPDATE research_conflicts SET resolution=$resolution,resolved_at=$now WHERE id=$id AND resolution='unresolved'";
            resolve.Parameters.AddWithValue("$resolution", useIncomingValue ? "use_incoming" : "keep_existing");
            resolve.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            resolve.Parameters.AddWithValue("$id", conflictId);
            if (resolve.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("This research conflict was resolved elsewhere before the change could be saved.");
        }
        using (var refresh = connection.CreateCommand())
        {
            refresh.Transaction = transaction;
            refresh.CommandText = """
                UPDATE research_broadcasts SET
                    needs_review=CASE
                        WHEN EXISTS(SELECT 1 FROM research_conflicts rc WHERE rc.research_broadcast_id=$research AND rc.resolution='unresolved') THEN 1
                        WHEN EXISTS(SELECT 1 FROM research_reconciliation_candidates rrc WHERE rrc.research_broadcast_id=$research AND rrc.status='pending' AND rrc.requires_review=1) THEN 1
                        ELSE 0 END,
                    research_state=CASE
                        WHEN EXISTS(SELECT 1 FROM research_conflicts rc WHERE rc.research_broadcast_id=$research AND rc.resolution='unresolved') THEN 'conflicting_information'
                        WHEN EXISTS(SELECT 1 FROM research_reconciliation_candidates rrc WHERE rrc.research_broadcast_id=$research AND rrc.status='pending' AND rrc.requires_review=1) THEN 'conflicting_information'
                        WHEN episode_id IS NOT NULL THEN 'in_library'
                        ELSE 'missing_recording' END,
                    updated_at=$now
                WHERE id=$research
                """;
            refresh.Parameters.AddWithValue("$research", researchId);
            refresh.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            refresh.ExecuteNonQuery();
        }
        transaction.Commit();
        using var remainingCommand = connection.CreateCommand();
        remainingCommand.CommandText = """
            SELECT
                EXISTS(SELECT 1 FROM research_conflicts WHERE research_broadcast_id=$research AND resolution='unresolved')
             OR EXISTS(SELECT 1 FROM research_reconciliation_candidates WHERE research_broadcast_id=$research AND status='pending' AND requires_review=1)
            """;
        remainingCommand.Parameters.AddWithValue("$research", researchId);
        var stillNeedsDecision = Convert.ToInt32(remainingCommand.ExecuteScalar()) == 1;
        SyncLegacyResearchState(connection, researchId, stillNeedsDecision ? "ambiguous" : "resolved", episodeId,
            useIncomingValue ? $"Used the incoming research {fieldName}." : $"Kept the library {fieldName}.");
        return useIncomingValue
            ? $"The research {fieldName} is now the library value."
            : $"The library {fieldName} was kept and made canonical for this research record.";
    }

    public void AttachResearchLibraryRecord(long researchId, long episodeId)
    {
        using var connection = OpenConnection();
        using (var check = connection.CreateCommand())
        {
            check.CommandText = """
                SELECT COUNT(*) FROM episodes e
                WHERE e.id=$episode
                  AND EXISTS(SELECT 1 FROM media_files mf WHERE mf.episode_id=e.id AND mf.is_missing=0)
                """;
            check.Parameters.AddWithValue("$episode", episodeId);
            if (Convert.ToInt32(check.ExecuteScalar()) != 1)
                throw new InvalidOperationException("The selected archive broadcast is no longer available.");
        }

        var item = TryDeserializeResearchBroadcast(connection, researchId)
            ?? throw new InvalidOperationException("The saved research record could not be read.");
        var before = GetRichEpisodeMetadata(episodeId);
        RecordScalarResearchConflicts(connection, researchId, episodeId, item, before);
        if (before.UserModified)
        {
            item.Research ??= new TrvPackResearch();
            item.Research.Broadcast ??= new TrvPackBroadcastMetadata();
            item.ImportPolicy ??= new TrvPackImportPolicy();
            item.Research.Headline = before.Headline;
            item.Research.Summary = before.Description;
            item.Research.Broadcast.Station = before.Edition;
            item.ImportPolicy.ReplaceExistingHeadline = false;
            item.ImportPolicy.ReplaceExistingSummary = false;
        }
        ApplyKnowledgePackBroadcast(episodeId, item, protectUserEdits: false);
        AttachResearchRecord(connection, researchId, episodeId);
        ApproveReconciliationCandidate(connection, researchId, episodeId, 100, "Manually attached from Broadcasts to find.");
        SyncLegacyResearchState(connection, researchId, "resolved", episodeId, "Manually attached from Broadcasts to find.");
    }

    private static DateTime? ParseResearchDate(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        return DateTime.TryParse(reader.GetString(ordinal), out var value) ? value : null;
    }

    private static DateTime ParseResearchTimestamp(string value)
        => DateTime.TryParse(value, out var parsed) ? parsed : DateTime.MinValue;

    private static List<string> SplitResearchList(string value)
        => string.IsNullOrWhiteSpace(value)
            ? new List<string>()
            : value.Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static string ExtractResearchDomain(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) return uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        return string.Empty;
    }
}
