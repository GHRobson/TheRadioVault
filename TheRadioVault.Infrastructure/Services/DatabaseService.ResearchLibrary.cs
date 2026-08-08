using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;
using TheRadioVault.Models;

namespace TheRadioVault.Services;

public sealed partial class DatabaseService
{
    private sealed record ResearchFieldMergeDecision(
        string FieldName,
        string BeforeValue,
        string AfterValue,
        string Decision,
        string Reason);

    private sealed class KnowledgePackMergePlan
    {
        public string Headline { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string Station { get; set; } = string.Empty;
        public string ArchiveNotes { get; set; } = string.Empty;
        public List<string> Hosts { get; set; } = new();
        public List<string> Guests { get; set; } = new();
        public List<string> Callers { get; set; } = new();
        public List<string> MentionedPeople { get; set; } = new();
        public List<string> Topics { get; set; } = new();
        public List<string> SourceUrls { get; set; } = new();
        public bool ProtectStructuredScalars { get; set; }
        public List<ResearchFieldMergeDecision> Decisions { get; } = new();
    }

    private static KnowledgePackMergePlan BuildKnowledgePackMergePlan(
        EpisodeMetadata existing,
        TrvPackBroadcast item,
        bool protectUserEdits = true,
        IEnumerable<string>? existingSourceUrls = null,
        IEnumerable<string>? existingMomentKeys = null)
    {
        item.ImportPolicy ??= new TrvPackImportPolicy();
        item.Research ??= new TrvPackResearch();
        item.Research.Broadcast ??= new TrvPackBroadcastMetadata();
        item.Research.Quality ??= new TrvPackResearchQuality();
        item.Research.People ??= new TrvPackPeople();
        item.Research.People.Hosts ??= new List<string>();
        item.Research.People.Guests ??= new List<string>();
        item.Research.People.Callers ??= new List<string>();
        item.Research.People.MentionedPeople ??= new List<string>();
        item.Research.Guests ??= new List<string>();
        item.Research.Topics ??= new List<string>();
        item.Research.Moments ??= new List<TrvPackMoment>();
        item.Sources ??= new List<TrvPackSource>();

        var research = item.Research;
        var incomingConfidence = Math.Clamp(research.Quality.Confidence, 0, 100);
        var authoritativeAudit = item.ImportPolicy.AuthoritativeAudit;
        var protectManual = !authoritativeAudit && protectUserEdits && existing.UserModified;
        var plan = new KnowledgePackMergePlan
        {
            ProtectStructuredScalars = protectManual
        };

        var headline = DecideScalar(
            "headline",
            existing.Headline,
            research.Headline,
            protectManual,
            item.ImportPolicy.ReplaceExistingHeadline,
            incomingConfidence,
            existing.MetadataConfidence,
            currentIsWeak: !TitleQualityService.IsMeaningful(existing.Headline, null, null),
            incomingIsUseful: TitleQualityService.IsMeaningful(research.Headline, null, null),
            authoritativeReplace: authoritativeAudit);
        plan.Headline = headline.AfterValue;
        plan.Decisions.Add(headline);

        var summary = DecideScalar(
            "summary",
            existing.Description,
            research.Summary,
            protectManual,
            item.ImportPolicy.ReplaceExistingSummary,
            incomingConfidence,
            existing.MetadataConfidence,
            currentIsWeak: IsGenericResearchSummary(existing.Description),
            incomingIsUseful: !string.IsNullOrWhiteSpace(research.Summary) && !IsGenericResearchSummary(research.Summary),
            authoritativeReplace: authoritativeAudit);
        plan.Summary = summary.AfterValue;
        plan.Decisions.Add(summary);

        var station = DecideScalar(
            "station",
            existing.Edition,
            research.Broadcast.Station ?? research.Edition,
            protectManual,
            false,
            incomingConfidence,
            existing.MetadataConfidence,
            currentIsWeak: string.IsNullOrWhiteSpace(existing.Edition),
            incomingIsUseful: !string.IsNullOrWhiteSpace(research.Broadcast.Station ?? research.Edition),
            authoritativeReplace: authoritativeAudit);
        plan.Station = station.AfterValue;
        plan.Decisions.Add(station);

        var archiveNotes = DecideScalar(
            "archive_notes",
            existing.ArchiveNotes,
            research.ArchiveNotes,
            protectManual,
            false,
            incomingConfidence,
            existing.MetadataConfidence,
            currentIsWeak: string.IsNullOrWhiteSpace(existing.ArchiveNotes),
            incomingIsUseful: !string.IsNullOrWhiteSpace(research.ArchiveNotes),
            authoritativeReplace: authoritativeAudit);
        plan.ArchiveNotes = archiveNotes.AfterValue;
        plan.Decisions.Add(archiveNotes);

        plan.Hosts = DecideList(plan.Decisions, "hosts", SplitResearchNames(existing.Hosts), research.People.Hosts,
            item.ImportPolicy.MergePeople, protectManual, authoritativeReplace: authoritativeAudit);
        plan.Guests = DecideList(plan.Decisions, "guests", SplitResearchNames(existing.Guests),
            research.People.Guests.Concat(research.Guests), item.ImportPolicy.MergeGuests, protectManual,
            authoritativeReplace: authoritativeAudit);
        plan.Callers = DecideList(plan.Decisions, "callers", SplitResearchNames(existing.Callers), research.People.Callers,
            item.ImportPolicy.MergePeople, protectManual, authoritativeReplace: authoritativeAudit);
        plan.MentionedPeople = DecideList(plan.Decisions, "mentioned_people", SplitResearchNames(existing.MentionedPeople),
            research.People.MentionedPeople, item.ImportPolicy.MergePeople, protectManual,
            authoritativeReplace: authoritativeAudit);
        plan.Topics = DecideList(plan.Decisions, "topics", SplitResearchNames(existing.Tags), research.Topics,
            item.ImportPolicy.MergeTopics, protectManual, authoritativeReplace: authoritativeAudit);
        plan.SourceUrls = DecideList(plan.Decisions, "sources", existingSourceUrls ?? Array.Empty<string>(),
            item.Sources.Select(x => x.Url), mergeRequested: true, protectManual: false,
            authoritativeReplace: authoritativeAudit);
        _ = DecideList(plan.Decisions, "moments", existingMomentKeys ?? Array.Empty<string>(),
            research.Moments.Select(x => $"{Math.Max(0, x.TimestampSeconds)}:{x.Title.Trim()}"),
            mergeRequested: item.ImportPolicy.MergeMoments, protectManual: protectManual,
            replaceWhenMergeDisabled: false);
        return plan;
    }

    private static ResearchFieldMergeDecision DecideScalar(
        string field,
        string? current,
        string? incoming,
        bool protectManual,
        bool explicitReplace,
        int incomingConfidence,
        int existingConfidence,
        bool currentIsWeak,
        bool incomingIsUseful,
        bool authoritativeReplace = false)
    {
        var before = current?.Trim() ?? string.Empty;
        var candidate = incoming?.Trim() ?? string.Empty;
        if (authoritativeReplace)
        {
            if (string.Equals(before, candidate, StringComparison.Ordinal))
                return new(field, before, before, "unchanged", "The audited value already matches the archive.");
            return new(field, before, candidate, "applied",
                string.IsNullOrWhiteSpace(candidate)
                    ? "The authoritative audit intentionally clears this stale field."
                    : "The authoritative audit makes this value canonical.");
        }
        if (string.IsNullOrWhiteSpace(candidate))
            return new(field, before, before, "unchanged", "The pack did not supply a value.");
        if (string.Equals(before, candidate, StringComparison.OrdinalIgnoreCase))
            return new(field, before, before, "unchanged", "The incoming value already matches the archive.");
        if (protectManual)
            return new(field, before, before, "protected", "A manual archive edit is protected from automatic replacement, including an intentionally cleared value.");
        if (string.IsNullOrWhiteSpace(before))
            return new(field, before, candidate, "applied", "The archive field is empty.");
        if (explicitReplace)
            return new(field, before, candidate, "applied", "The research pack explicitly permits replacement.");
        if (currentIsWeak && incomingIsUseful)
            return new(field, before, candidate, "applied", "A specific researched value replaces a weak or generic value.");
        if (incomingIsUseful && incomingConfidence >= Math.Max(60, existingConfidence + 10))
            return new(field, before, candidate, "applied", "The incoming research has materially stronger confidence.");
        return new(field, before, before, "preserved", "The existing value remains because the incoming evidence is not clearly stronger.");
    }

    private static List<string> DecideList(
        ICollection<ResearchFieldMergeDecision> decisions,
        string field,
        IEnumerable<string> existing,
        IEnumerable<string> incoming,
        bool mergeRequested,
        bool protectManual,
        bool replaceWhenMergeDisabled = true,
        bool authoritativeReplace = false)
    {
        var before = NormalizeNames(existing);
        var candidate = NormalizeNames(incoming);
        if (authoritativeReplace)
        {
            if (before.SequenceEqual(candidate, StringComparer.OrdinalIgnoreCase))
            {
                decisions.Add(new(field, JsonSerializer.Serialize(before), JsonSerializer.Serialize(before),
                    "unchanged", "The audited values already match the archive."));
                return before;
            }

            decisions.Add(new(field, JsonSerializer.Serialize(before), JsonSerializer.Serialize(candidate),
                "applied", candidate.Count == 0
                    ? "The authoritative audit intentionally clears this stale list."
                    : "The authoritative audit replaces this list with its canonical values."));
            return candidate;
        }
        if (candidate.Count == 0)
        {
            decisions.Add(new(field, JsonSerializer.Serialize(before), JsonSerializer.Serialize(before),
                "unchanged", "The pack did not supply any new values."));
            return before;
        }

        if (protectManual || mergeRequested)
        {
            var merged = MergeNames(before, candidate);
            var added = merged.Count - before.Count;
            if (added > 0)
            {
                var reason = protectManual
                    ? $"{added:N0} new value{(added == 1 ? "" : "s")} will be added without removing manually maintained data."
                    : $"{added:N0} new value{(added == 1 ? "" : "s")} will be added without removing existing data.";
                decisions.Add(new(field, JsonSerializer.Serialize(before), JsonSerializer.Serialize(merged), "merged", reason));
                return merged;
            }

            decisions.Add(new(field, JsonSerializer.Serialize(before), JsonSerializer.Serialize(before),
                "unchanged", "The incoming values are already present."));
            return before;
        }

        if (!replaceWhenMergeDisabled)
        {
            decisions.Add(new(field, JsonSerializer.Serialize(before), JsonSerializer.Serialize(before),
                "preserved", "This pack is not permitted to merge this field."));
            return before;
        }

        if (before.SequenceEqual(candidate, StringComparer.OrdinalIgnoreCase))
        {
            decisions.Add(new(field, JsonSerializer.Serialize(before), JsonSerializer.Serialize(before),
                "unchanged", "The incoming values already match the archive."));
            return before;
        }

        decisions.Add(new(field, JsonSerializer.Serialize(before), JsonSerializer.Serialize(candidate),
            "applied", "The research pack requests replacement rather than merging for this field."));
        return candidate;
    }

    private static IEnumerable<string> SplitResearchNames(string? value) =>
        (value ?? string.Empty).Split(new[] { '|', ',', ';' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsGenericResearchSummary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var text = value.Trim();
        if (text.Length < 35) return true;
        var genericFragments = new[]
        {
            "archive broadcast", "this episode features", "topics are discussed", "a variety of topics",
            "the hosts discuss", "conversation and comedy", "from the radio archive"
        };
        return genericFragments.Any(fragment => text.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private void MigrateLegacyResearchLedger()
    {
        using var connection = OpenConnection();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,stable_key,research_json,status,matched_episode_id
            FROM missing_broadcast_research
            ORDER BY id
            """;

        var rows = new List<(long Id, string StableKey, string Json, string Status, long? EpisodeId)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4)));
            }
        }

        foreach (var row in rows)
        {
            using (var migrated = connection.CreateCommand())
            {
                migrated.CommandText = "SELECT 1 FROM research_broadcasts WHERE legacy_missing_research_id=$id LIMIT 1";
                migrated.Parameters.AddWithValue("$id", row.Id);
                if (migrated.ExecuteScalar() is not null) continue;
            }

            TrvPackBroadcast? item;
            try { item = KnowledgePackService.DeserializeBroadcast(row.Json); }
            catch { item = null; }
            if (item is null) continue;

            PrepareKnowledgePackItem(connection, item, item.Show);
            var collectionId = ResolveResearchCollectionId(connection, item.Show);
            var existence = row.EpisodeId.HasValue ? "in_library" : DeriveExistenceStatus(item);
            var needsReview = string.Equals(row.Status, "ambiguous", StringComparison.OrdinalIgnoreCase);
            UpsertResearchLibraryRecord(
                connection,
                item,
                collectionId,
                row.EpisodeId,
                existence,
                needsReview,
                importRunId: null,
                legacyMissingResearchId: row.Id);
        }
    }


    public ResearchImportPreview PreviewKnowledgePack(
        TrvKnowledgePack pack,
        string? sourcePath = null,
        IProgress<ResearchPackOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        pack.Broadcasts ??= new List<TrvPackBroadcast>();
        pack.MissingBroadcasts ??= new List<TrvPackBroadcast>();
        pack.Manifest ??= new TrvPackManifest();

        var preview = new ResearchImportPreview
        {
            PackageName = string.IsNullOrWhiteSpace(sourcePath) ? $"{pack.Manifest.Show} research pack" : Path.GetFileName(sourcePath),
            Show = pack.Manifest.Show,
            TotalRecords = pack.Broadcasts.Count + pack.MissingBroadcasts.Count,
            AuthoritativeAudit = pack.Broadcasts.Concat(pack.MissingBroadcasts)
                .Any(item => item.ImportPolicy?.AuthoritativeAudit == true)
        };

        if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
        {
            using var stream = File.OpenRead(sourcePath);
            preview.PackageHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        using var connection = OpenConnection();
        var canonicalShowMap = BuildCanonicalShowMap(connection);
        var matchIndex = BuildKnowledgePackMatchIndex(connection);
        if (!string.IsNullOrWhiteSpace(preview.PackageHash))
        {
            using var duplicate = connection.CreateCommand();
            duplicate.CommandText = "SELECT 1 FROM research_import_runs WHERE package_sha256=$hash LIMIT 1";
            duplicate.Parameters.AddWithValue("$hash", preview.PackageHash);
            preview.PreviouslyImported = duplicate.ExecuteScalar() is not null;
        }

        var entries = pack.Broadcasts.Concat(pack.MissingBroadcasts).ToList();
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = entries[index];
            PrepareKnowledgePackItem(connection, item, pack.Manifest.Show, canonicalShowMap);
            var matches = FindKnowledgePackMatches(matchIndex, item);
            if (matches.Count == 0) preview.MissingRecords++;
            else if (matches.Count > 1) preview.AmbiguousMatches++;
            else
            {
                preview.ExactMatches++;
                var matchedEpisodeId = matches[0];
                var existing = GetRichEpisodeMetadata(connection, null, matchedEpisodeId);
                if (existing.UserModified && item.ImportPolicy?.AuthoritativeAudit != true) preview.ProtectedManualRecords++;
                var mergePlan = BuildKnowledgePackMergePlan(
                    existing,
                    item,
                    protectUserEdits: item.ImportPolicy?.AuthoritativeAudit != true,
                    existingSourceUrls: ReadEpisodeResearchSources(connection, matchedEpisodeId),
                    existingMomentKeys: ReadEpisodeMomentKeys(connection, null, matchedEpisodeId));
                foreach (var decision in mergePlan.Decisions)
                {
                    switch (decision.Decision)
                    {
                        case "applied": preview.FieldsExpectedToApply++; break;
                        case "merged": preview.FieldsExpectedToMerge++; break;
                        case "protected":
                            preview.FieldsProtectedByManualEdits++;
                            preview.FieldsExpectedToPreserve++;
                            break;
                        case "preserved": preview.FieldsExpectedToPreserve++; break;
                    }
                }
                if (item.ImportPolicy?.AuthoritativeAudit != true)
                {
                    if (!string.IsNullOrWhiteSpace(item.Research?.Headline) && !string.IsNullOrWhiteSpace(existing.Headline) && !string.Equals(item.Research.Headline.Trim(), existing.Headline.Trim(), StringComparison.OrdinalIgnoreCase)) preview.PotentialConflicts++;
                    if (!string.IsNullOrWhiteSpace(item.Research?.Summary) && !string.IsNullOrWhiteSpace(existing.Description) && !string.Equals(item.Research.Summary.Trim(), existing.Description.Trim(), StringComparison.OrdinalIgnoreCase)) preview.PotentialConflicts++;
                }
            }

            var research = item.Research;
            if (!string.IsNullOrWhiteSpace(research?.Summary)) preview.IncomingSummaries++;
            if (research?.People is not null)
                preview.NewPeople += NormalizeNames(research.People.Hosts.Concat(research.People.Guests).Concat(research.People.Callers).Concat(research.People.MentionedPeople)).Count;
            preview.NewTopics += NormalizeNames(research?.Topics ?? new List<string>()).Count;
            preview.NewSources += item.Sources?.Count ?? 0;

            if (index == 0 || index == entries.Count - 1 || (index + 1) % 25 == 0)
                progress?.Report(new ResearchPackOperationProgress(index + 1, entries.Count, "Comparing research with your archive…"));
        }
        return preview;
    }

    private KnowledgePackImportResult ImportKnowledgePackWithResearchLibrary(
        TrvKnowledgePack pack,
        string? sourcePath,
        IProgress<ResearchPackOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        pack.Broadcasts ??= new List<TrvPackBroadcast>();
        pack.MissingBroadcasts ??= new List<TrvPackBroadcast>();
        pack.Manifest ??= new TrvPackManifest();

        var result = new KnowledgePackImportResult
        {
            Total = pack.Broadcasts.Count + pack.MissingBroadcasts.Count
        };

        using var connection = OpenConnection();
        var canonicalShowMap = BuildCanonicalShowMap(connection);
        var entries = pack.Broadcasts
            .Select(item => (Item: item, DeclaredMissing: false))
            .Concat(pack.MissingBroadcasts.Select(item => (Item: item, DeclaredMissing: true)))
            .ToList();

        // Normalisation and read-only matching preparation happen before the write
        // transaction begins, keeping the exclusive write window as short as possible.
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareKnowledgePackItem(connection, entry.Item, pack.Manifest.Show, canonicalShowMap);
        }

        progress?.Report(new ResearchPackOperationProgress(0, entries.Count, "Creating a pre-import safety snapshot…"));
        var rollbackSnapshot = CreateResearchImportSnapshot(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction();
        try
        {
            var importRunId = BeginResearchImportRun(connection, transaction, pack, sourcePath, rollbackSnapshot);
            result.ImportRunId = importRunId;
            var matchIndex = BuildKnowledgePackMatchIndex(connection, transaction);
            var collectionIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[index];
                var item = entry.Item;
                if (index == 0 || index == entries.Count - 1 || (index + 1) % 10 == 0)
                    progress?.Report(new ResearchPackOperationProgress(index + 1, entries.Count, "Applying transactional merge decisions…"));

                var collectionKey = item.Show?.Trim() ?? string.Empty;
                if (!collectionIds.TryGetValue(collectionKey, out var collectionId))
                {
                    collectionId = ResolveResearchCollectionId(connection, transaction, item.Show);
                    collectionIds[collectionKey] = collectionId;
                }
                var matches = FindKnowledgePackMatches(matchIndex, item);
                var identity = BuildImportRecordIdentity(item);

                if (matches.Count == 0)
                {
                    var notes = entry.DeclaredMissing
                        ? "The research pack identifies this broadcast, but no matching audio is currently present in the library."
                        : "No matching audio is currently present in the library.";
                    UpsertMissingBroadcastResearch(connection, transaction, item, "pending", notes);
                    var legacyId = GetLegacyMissingResearchId(connection, transaction, item);
                    var existence = DeriveExistenceStatus(item);
                    var researchId = UpsertResearchLibraryRecord(
                        connection,
                        transaction,
                        item,
                        collectionId,
                        episodeId: null,
                        existenceStatus: existence,
                        needsReview: false,
                        importRunId: importRunId,
                        legacyMissingResearchId: legacyId);
                    RecordDurableResearchImportProvenance(connection, transaction, importRunId, researchId, item);

                    RecordImportChange(connection, transaction, importRunId, researchId, null, identity,
                        "record_status", string.Empty, existence, "retained_missing",
                        "Research was preserved independently because matching audio is not currently available.", result);
                    result.RetainedMissing++;
                    result.ResearchRecordsStored++;
                    IncrementExistenceResult(result, existence);
                    continue;
                }

                if (matches.Count > 1)
                {
                    UpsertMissingBroadcastResearch(connection, transaction, item, "ambiguous", $"{matches.Count} archive episodes match this research record.");
                    var legacyId = GetLegacyMissingResearchId(connection, transaction, item);
                    var existence = DeriveExistenceStatus(item);
                    var researchId = UpsertResearchLibraryRecord(
                        connection,
                        transaction,
                        item,
                        collectionId,
                        episodeId: null,
                        existenceStatus: existence,
                        needsReview: true,
                        importRunId: importRunId,
                        legacyMissingResearchId: legacyId);
                    RecordDurableResearchImportProvenance(connection, transaction, importRunId, researchId, item);

                    foreach (var episodeId in matches)
                        UpsertReconciliationCandidate(connection, transaction, researchId, episodeId, 75, "Multiple archive broadcasts matched during research-pack import.");

                    RecordImportChange(connection, transaction, importRunId, researchId, null, identity,
                        "record_status", string.Empty, "needs_match_review", "ambiguous",
                        $"{matches.Count} archive broadcasts matched this research identity.", result);
                    result.Ambiguous++;
                    result.ResearchRecordsStored++;
                    IncrementExistenceResult(result, existence);
                    continue;
                }

                var matchedEpisodeId = matches[0];
                var before = GetRichEpisodeMetadata(connection, transaction, matchedEpisodeId);
                var mergePlan = BuildKnowledgePackMergePlan(
                    before,
                    item,
                    protectUserEdits: item.ImportPolicy?.AuthoritativeAudit != true,
                    existingSourceUrls: ReadEpisodeResearchSources(connection, matchedEpisodeId, transaction),
                    existingMomentKeys: ReadEpisodeMomentKeys(connection, transaction, matchedEpisodeId));
                var researchBroadcastId = UpsertResearchLibraryRecord(
                    connection,
                    transaction,
                    item,
                    collectionId,
                    episodeId: matchedEpisodeId,
                    existenceStatus: "in_library",
                    needsReview: false,
                    importRunId: importRunId,
                    legacyMissingResearchId: null);

                if (item.ImportPolicy?.AuthoritativeAudit == true)
                    ResolveConflictsSupersededByAuthoritativeAudit(connection, transaction, researchBroadcastId);
                else
                    result.ConflictsCreated += RecordScalarResearchConflicts(connection, transaction, researchBroadcastId, matchedEpisodeId, item, before);
                ApplyKnowledgePackBroadcast(connection, transaction, matchedEpisodeId, item, mergePlan);
                RecordKnowledgePackMergeChanges(connection, transaction, importRunId, researchBroadcastId, matchedEpisodeId, identity, mergePlan, result);
                result.ResolvedPreviousMissing += MarkMissingBroadcastResearchResolved(
                    connection,
                    transaction,
                    item,
                    matchedEpisodeId,
                    "Matched while importing a research pack.");

                result.Matched++;
                result.Updated++;
                result.ResearchRecordsStored++;
                result.AttachedResearchRecords++;
            }

            progress?.Report(new ResearchPackOperationProgress(entries.Count, entries.Count, "Committing import history…"));
            CompleteResearchImportRun(connection, transaction, importRunId, result);
            transaction.Commit();
            return result;
        }
        catch
        {
            try { transaction.Rollback(); }
            catch { /* Preserve the original import failure. */ }
            TryDeleteImportSnapshot(rollbackSnapshot);
            throw;
        }
    }

    private static IReadOnlyDictionary<string, string> BuildCanonicalShowMap(SqliteConnection connection)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.name, c.name
            FROM collections c
            UNION ALL
            SELECT ca.alias, c.name
            FROM collection_aliases ca
            JOIN collections c ON c.id=ca.collection_id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var alias = reader.GetString(0).Trim();
            var canonical = reader.GetString(1).Trim();
            if (!string.IsNullOrWhiteSpace(alias) && !result.ContainsKey(alias)) result[alias] = canonical;
        }
        return result;
    }

    public ResearchLibraryOverview GetResearchLibraryOverview(int? collectionId = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              COUNT(*),
              SUM(CASE WHEN episode_id IS NOT NULL THEN 1 ELSE 0 END),
              SUM(CASE WHEN existence_status='confirmed_missing' AND episode_id IS NULL THEN 1 ELSE 0 END),
              SUM(CASE WHEN existence_status='probable_missing' AND episode_id IS NULL THEN 1 ELSE 0 END),
              SUM(CASE WHEN existence_status='unknown_gap' AND episode_id IS NULL THEN 1 ELSE 0 END),
              SUM(CASE WHEN needs_review=1 THEN 1 ELSE 0 END),
              SUM(CASE WHEN EXISTS(
                  SELECT 1 FROM research_conflicts rc
                  WHERE rc.research_broadcast_id=research_broadcasts.id
                    AND rc.resolution='unresolved'
              ) THEN 1 ELSE 0 END)
            FROM research_broadcasts
            WHERE ($collection IS NULL OR collection_id=$collection)
            """;
        command.Parameters.AddWithValue("$collection", collectionId.HasValue ? collectionId.Value : DBNull.Value);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new ResearchLibraryOverview();
        return new ResearchLibraryOverview
        {
            TotalResearchRecords = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetInt64(0)),
            AttachedRecords = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1)),
            ConfirmedMissing = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetInt64(2)),
            ProbableMissing = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetInt64(3)),
            UnknownGaps = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetInt64(4)),
            NeedsReview = reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetInt64(5)),
            ConflictedRecords = reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader.GetInt64(6))
        };
    }

    private MissingResearchReconciliationResult ReconcileResearchLibraryForEpisode(long episodeId)
    {
        var result = new MissingResearchReconciliationResult();
        var episode = ReadEpisodeResearchSnapshot(episodeId);
        if (episode is null) return result;

        using var connection = OpenConnection();
        using var candidatesCommand = connection.CreateCommand();
        candidatesCommand.CommandText = """
            SELECT rb.id
            FROM research_broadcasts rb
            WHERE rb.collection_id=$collection
              AND (rb.episode_id IS NULL OR rb.episode_id<>$episode)
              AND (
                    ($uid<>'' AND rb.source_broadcast_id=$uid)
                    OR ($date IS NOT NULL AND rb.air_date=$date)
                  )
              AND NOT EXISTS(
                    SELECT 1 FROM research_reconciliation_candidates rrc
                    WHERE rrc.research_broadcast_id=rb.id
                      AND rrc.episode_id=$episode
                      AND rrc.status IN ('approved','rejected')
                  )
            ORDER BY CASE WHEN rb.episode_id IS NULL THEN 0 ELSE 1 END,
                     rb.confidence DESC,rb.updated_at DESC,rb.id
            """;
        candidatesCommand.Parameters.AddWithValue("$collection", episode.CollectionId);
        candidatesCommand.Parameters.AddWithValue("$uid", episode.BroadcastUid);
        candidatesCommand.Parameters.AddWithValue("$date", episode.AirDate.HasValue ? episode.AirDate.Value.ToString("yyyy-MM-dd") : DBNull.Value);
        candidatesCommand.Parameters.AddWithValue("$episode", episodeId);

        var researchIds = new List<long>();
        using (var reader = candidatesCommand.ExecuteReader())
            while (reader.Read()) researchIds.Add(reader.GetInt64(0));

        foreach (var researchId in researchIds)
        {
            var research = ReadResearchBroadcastRecord(connection, researchId);
            if (research is null) continue;
            var match = ResearchReconciliationRules.ScoreMatch(research, episode);
            if (match is null) continue;

            var reason = match.Reason;
            if (research.EpisodeId.HasValue && research.EpisodeId.Value != episodeId)
                reason = $"Possible alternate capture; {reason}";
            else if (research.ExistenceStatus is BroadcastExistenceStatus.ConfirmedMissing or BroadcastExistenceStatus.ProbableMissing)
                reason = $"Previously missing broadcast found; {reason}";

            UpsertReconciliationCandidate(connection, research.Id, episodeId, match.Score, reason);
            result.CandidatesFound++;
            if (research.EpisodeId.HasValue && research.EpisodeId.Value != episodeId)
                result.AlternateCaptureCandidates++;
            else if (research.ExistenceStatus is BroadcastExistenceStatus.ConfirmedMissing or BroadcastExistenceStatus.ProbableMissing)
                result.PreviouslyMissingMatches++;

            // Keep the legacy ledger useful without attaching or changing episode
            // metadata. Every candidate is explicitly review-first in alpha4.
            if (!research.EpisodeId.HasValue)
            {
                MarkResearchNeedsReview(connection, research.Id, true);
                SyncLegacyResearchState(connection, research.Id, "ambiguous", null,
                    $"Potential archive match scored {match.Score}: {reason}");
            }
        }

        result.Ambiguous = result.CandidatesFound;
        return result;
    }

    private EpisodeResearchSnapshot? ReadEpisodeResearchSnapshot(long episodeId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.id,e.collection_id,COALESCE(e.broadcast_uid,''),e.air_date,
                   COALESCE(e.broadcast_slot,''),COALESCE(e.part_number,1),
                   COALESCE((SELECT mf.original_filename FROM media_files mf
                             WHERE mf.episode_id=e.id AND mf.is_missing=0
                             ORDER BY mf.is_preferred DESC,mf.id LIMIT 1),''),
                   COALESCE(e.user_modified,0),COALESCE(e.title,''),COALESCE(e.description,''),
                   COALESCE(e.hosts,''),COALESCE(e.callers,''),COALESCE(e.mentioned_people,'')
            FROM episodes e
            WHERE e.id=$id
              AND EXISTS(SELECT 1 FROM media_files mf WHERE mf.episode_id=e.id AND mf.is_missing=0)
            """;
        command.Parameters.AddWithValue("$id", episodeId);

        long id;
        int collectionId;
        string uid;
        DateOnly? airDate;
        string slot;
        int partNumber;
        string filename;
        bool userModified;
        string headline;
        string summary;
        List<string> hosts;
        List<string> callers;
        List<string> mentioned;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            id = reader.GetInt64(0);
            collectionId = reader.GetInt32(1);
            uid = reader.GetString(2);
            airDate = !reader.IsDBNull(3) && DateOnly.TryParse(reader.GetString(3), out var parsedDate) ? parsedDate : null;
            slot = reader.GetString(4);
            partNumber = reader.GetInt32(5);
            filename = reader.GetString(6);
            userModified = reader.GetInt32(7) == 1;
            headline = reader.GetString(8);
            summary = reader.GetString(9);
            hosts = SplitPipe(reader.GetString(10)).ToList();
            callers = SplitPipe(reader.GetString(11)).ToList();
            mentioned = SplitPipe(reader.GetString(12)).ToList();
        }

        return new EpisodeResearchSnapshot
        {
            EpisodeId = id,
            CollectionId = collectionId,
            BroadcastUid = uid,
            AirDate = airDate,
            Slot = slot,
            PartNumber = partNumber,
            OriginalFilename = filename,
            UserModified = userModified,
            Headline = headline,
            Summary = summary,
            Hosts = hosts,
            Guests = ReadNames(connection, episodeId, "episode_guests", "guests", "guest_id"),
            Callers = callers,
            MentionedPeople = mentioned,
            Topics = ReadNames(connection, episodeId, "episode_tags", "tags", "tag_id")
        };
    }

    private ResearchBroadcastRecord? ReadResearchBroadcastRecord(SqliteConnection connection, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,collection_id,episode_id,source_broadcast_id,air_date,slot,part_number,
                   capture_key,headline,summary,station,edition,broadcast_variant,broadcast_era,
                   episode_type,archive_notes,research_state,existence_status,confidence,
                   confidence_reason,user_modified,needs_review
            FROM research_broadcasts WHERE id=$id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        DateOnly? airDate = null;
        if (!reader.IsDBNull(4) && DateOnly.TryParse(reader.GetString(4), out var parsedDate)) airDate = parsedDate;
        var record = new ResearchBroadcastRecord
        {
            Id = reader.GetInt64(0),
            Identity = new ResearchBroadcastIdentity(reader.GetInt32(1), airDate, reader.GetString(5), reader.GetInt32(6), reader.GetString(7)),
            EpisodeId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
            SourceBroadcastId = reader.GetString(3),
            Headline = reader.GetString(8),
            Summary = reader.GetString(9),
            Station = reader.GetString(10),
            Edition = reader.GetString(11),
            BroadcastVariant = reader.GetString(12),
            BroadcastEra = reader.GetString(13),
            EpisodeType = reader.GetString(14),
            ArchiveNotes = reader.GetString(15),
            State = ParseResearchState(reader.GetString(16)),
            ExistenceStatus = ParseExistenceStatus(reader.GetString(17)),
            Confidence = reader.GetInt32(18),
            ConfidenceReason = reader.GetString(19),
            UserModified = reader.GetInt32(20) == 1,
            NeedsReview = reader.GetInt32(21) == 1
        };
        reader.Close();

        using var aliases = connection.CreateCommand();
        aliases.CommandText = "SELECT alias_type,alias_value,confidence FROM research_aliases WHERE research_broadcast_id=$id";
        aliases.Parameters.AddWithValue("$id", id);
        using var aliasReader = aliases.ExecuteReader();
        while (aliasReader.Read())
            record.Aliases.Add(new ResearchAliasRecord(aliasReader.GetString(0), aliasReader.GetString(1), aliasReader.GetInt32(2)));
        return record;
    }

    private static ResearchBroadcastState ParseResearchState(string value) => value switch
    {
        "in_library" => ResearchBroadcastState.InLibrary,
        "missing_recording" => ResearchBroadcastState.MissingRecording,
        "fully_researched" => ResearchBroadcastState.FullyResearched,
        "conflicting_information" => ResearchBroadcastState.ConflictingInformation,
        "alternate_capture" => ResearchBroadcastState.AlternateCapture,
        "encore_or_replay" => ResearchBroadcastState.EncoreOrReplay,
        "special_edition" => ResearchBroadcastState.SpecialEdition,
        _ => ResearchBroadcastState.PartiallyResearched
    };

    private static BroadcastExistenceStatus ParseExistenceStatus(string value) => value switch
    {
        "in_library" => BroadcastExistenceStatus.InLibrary,
        "confirmed_missing" => BroadcastExistenceStatus.ConfirmedMissing,
        "probable_missing" => BroadcastExistenceStatus.ProbableMissing,
        _ => BroadcastExistenceStatus.UnknownGap
    };

    private long UpsertResearchLibraryRecord(
        SqliteConnection connection,
        TrvPackBroadcast item,
        int collectionId,
        long? episodeId,
        string existenceStatus,
        bool needsReview,
        long? importRunId,
        long? legacyMissingResearchId)
    {
        using var transaction = connection.BeginTransaction();
        var researchId = UpsertResearchLibraryRecord(
            connection, transaction, item, collectionId, episodeId, existenceStatus,
            needsReview, importRunId, legacyMissingResearchId);
        transaction.Commit();
        return researchId;
    }

    private long UpsertResearchLibraryRecord(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TrvPackBroadcast item,
        int collectionId,
        long? episodeId,
        string existenceStatus,
        bool needsReview,
        long? importRunId,
        long? legacyMissingResearchId)
    {
        var research = item.Research ?? new TrvPackResearch();
        research.Broadcast ??= new TrvPackBroadcastMetadata();
        research.People ??= new TrvPackPeople();
        research.People.Hosts ??= new List<string>();
        research.People.Guests ??= new List<string>();
        research.People.Callers ??= new List<string>();
        research.People.MentionedPeople ??= new List<string>();
        research.Quality ??= new TrvPackResearchQuality();
        research.Topics ??= new List<string>();
        research.Guests ??= new List<string>();
        research.Moments ??= new List<TrvPackMoment>();
        item.Sources ??= new List<TrvPackSource>();

        item.ImportPolicy ??= new TrvPackImportPolicy();
        var authoritativeAudit = item.ImportPolicy.AuthoritativeAudit;
        var identityKey = BuildResearchIdentityKey(collectionId, item);
        var confidence = Math.Clamp(research.Quality.Confidence, 0, 100);
        var now = DateTime.UtcNow.ToString("O");
        var state = episodeId.HasValue ? "in_library" : "missing_recording";
        var json = KnowledgePackService.SerializeBroadcast(item);

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO research_broadcasts(
                    identity_key,collection_id,episode_id,legacy_missing_research_id,
                    source_broadcast_id,air_date,slot,part_number,total_parts,capture_key,
                    headline,summary,station,edition,broadcast_variant,broadcast_era,
                    episode_type,archive_notes,research_json,research_state,existence_status,
                    confidence,confidence_reason,user_modified,needs_review,import_run_id,
                    attached_at,created_at,updated_at)
                VALUES($key,$collection,$episode,$legacy,$uid,$date,$slot,$part,$total,'primary',
                       $headline,$summary,$station,$edition,$variant,$era,$type,$archive,$json,
                       $state,$existence,$confidence,$reason,0,$review,$run,
                       CASE WHEN $episode IS NULL THEN NULL ELSE $now END,$now,$now)
                ON CONFLICT(identity_key) DO UPDATE SET
                    episode_id=COALESCE(excluded.episode_id,research_broadcasts.episode_id),
                    legacy_missing_research_id=COALESCE(excluded.legacy_missing_research_id,research_broadcasts.legacy_missing_research_id),
                    source_broadcast_id=CASE WHEN excluded.source_broadcast_id<>'' THEN excluded.source_broadcast_id ELSE research_broadcasts.source_broadcast_id END,
                    air_date=COALESCE(excluded.air_date,research_broadcasts.air_date),
                    slot=CASE WHEN excluded.slot<>'' THEN excluded.slot ELSE research_broadcasts.slot END,
                    part_number=excluded.part_number,
                    total_parts=COALESCE(excluded.total_parts,research_broadcasts.total_parts),
                    headline=CASE WHEN $authoritative=1 THEN excluded.headline WHEN research_broadcasts.user_modified=1 THEN research_broadcasts.headline WHEN excluded.headline<>'' AND (research_broadcasts.headline='' OR excluded.confidence>=research_broadcasts.confidence) THEN excluded.headline ELSE research_broadcasts.headline END,
                    summary=CASE WHEN $authoritative=1 THEN excluded.summary WHEN research_broadcasts.user_modified=1 THEN research_broadcasts.summary WHEN excluded.summary<>'' AND (research_broadcasts.summary='' OR excluded.confidence>=research_broadcasts.confidence) THEN excluded.summary ELSE research_broadcasts.summary END,
                    station=CASE WHEN $authoritative=1 THEN excluded.station WHEN research_broadcasts.user_modified=1 THEN research_broadcasts.station WHEN excluded.station<>'' THEN excluded.station ELSE research_broadcasts.station END,
                    edition=CASE WHEN $authoritative=1 THEN excluded.edition WHEN research_broadcasts.user_modified=1 THEN research_broadcasts.edition WHEN excluded.edition<>'' THEN excluded.edition ELSE research_broadcasts.edition END,
                    broadcast_variant=CASE WHEN $authoritative=1 THEN excluded.broadcast_variant WHEN research_broadcasts.user_modified=1 THEN research_broadcasts.broadcast_variant WHEN excluded.broadcast_variant<>'' THEN excluded.broadcast_variant ELSE research_broadcasts.broadcast_variant END,
                    broadcast_era=CASE WHEN $authoritative=1 THEN excluded.broadcast_era WHEN research_broadcasts.user_modified=1 THEN research_broadcasts.broadcast_era WHEN excluded.broadcast_era<>'' THEN excluded.broadcast_era ELSE research_broadcasts.broadcast_era END,
                    episode_type=CASE WHEN $authoritative=1 THEN excluded.episode_type WHEN research_broadcasts.user_modified=1 THEN research_broadcasts.episode_type WHEN excluded.episode_type<>'' THEN excluded.episode_type ELSE research_broadcasts.episode_type END,
                    archive_notes=CASE WHEN $authoritative=1 THEN excluded.archive_notes WHEN research_broadcasts.user_modified=1 THEN research_broadcasts.archive_notes WHEN excluded.archive_notes<>'' THEN excluded.archive_notes ELSE research_broadcasts.archive_notes END,
                    research_json=CASE WHEN $authoritative=1 THEN excluded.research_json WHEN research_broadcasts.user_modified=1 THEN research_broadcasts.research_json ELSE excluded.research_json END,
                    research_state=CASE WHEN COALESCE(excluded.episode_id,research_broadcasts.episode_id) IS NOT NULL THEN 'in_library' ELSE excluded.research_state END,
                    existence_status=CASE WHEN COALESCE(excluded.episode_id,research_broadcasts.episode_id) IS NOT NULL THEN 'in_library' ELSE excluded.existence_status END,
                    confidence=CASE WHEN $authoritative=1 THEN excluded.confidence ELSE MAX(research_broadcasts.confidence,excluded.confidence) END,
                    confidence_reason=CASE WHEN $authoritative=1 THEN excluded.confidence_reason WHEN excluded.confidence_reason<>'' THEN excluded.confidence_reason ELSE research_broadcasts.confidence_reason END,
                    user_modified=CASE WHEN $authoritative=1 THEN 0 ELSE research_broadcasts.user_modified END,
                    needs_review=CASE WHEN COALESCE(excluded.episode_id,research_broadcasts.episode_id) IS NOT NULL THEN excluded.needs_review ELSE MAX(research_broadcasts.needs_review,excluded.needs_review) END,
                    import_run_id=COALESCE(excluded.import_run_id,research_broadcasts.import_run_id),
                    attached_at=CASE WHEN COALESCE(excluded.episode_id,research_broadcasts.episode_id) IS NOT NULL THEN COALESCE(research_broadcasts.attached_at,excluded.attached_at,$now) ELSE NULL END,
                    updated_at=$now
                """;
            command.Parameters.AddWithValue("$authoritative", authoritativeAudit ? 1 : 0);
            command.Parameters.AddWithValue("$key", identityKey);
            command.Parameters.AddWithValue("$collection", collectionId);
            command.Parameters.AddWithValue("$episode", episodeId.HasValue ? episodeId.Value : DBNull.Value);
            command.Parameters.AddWithValue("$legacy", legacyMissingResearchId.HasValue ? legacyMissingResearchId.Value : DBNull.Value);
            command.Parameters.AddWithValue("$uid", item.BroadcastId?.Trim() ?? "");
            command.Parameters.AddWithValue("$date", string.IsNullOrWhiteSpace(item.BroadcastDate) ? DBNull.Value : item.BroadcastDate.Trim());
            command.Parameters.AddWithValue("$slot", GetKnowledgePackSlot(item) ?? "");
            command.Parameters.AddWithValue("$part", Math.Max(1, item.PartNumber));
            command.Parameters.AddWithValue("$total", item.TotalParts.HasValue ? item.TotalParts.Value : DBNull.Value);
            command.Parameters.AddWithValue("$headline", research.Headline?.Trim() ?? "");
            command.Parameters.AddWithValue("$summary", research.Summary?.Trim() ?? "");
            command.Parameters.AddWithValue("$station", research.Broadcast.Station?.Trim() ?? "");
            command.Parameters.AddWithValue("$edition", research.Edition?.Trim() ?? "");
            command.Parameters.AddWithValue("$variant", research.Broadcast.Variant?.Trim() ?? "");
            command.Parameters.AddWithValue("$era", research.Broadcast.Era?.Trim() ?? "");
            command.Parameters.AddWithValue("$type", research.Broadcast.EpisodeType?.Trim() ?? "");
            command.Parameters.AddWithValue("$archive", research.ArchiveNotes?.Trim() ?? "");
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$existence", existenceStatus);
            command.Parameters.AddWithValue("$confidence", confidence);
            command.Parameters.AddWithValue("$reason", research.Quality.ConfidenceReason?.Trim() ?? "");
            command.Parameters.AddWithValue("$review", needsReview ? 1 : 0);
            command.Parameters.AddWithValue("$run", importRunId.HasValue ? importRunId.Value : DBNull.Value);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }

        long researchId;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id FROM research_broadcasts WHERE identity_key=$key";
            select.Parameters.AddWithValue("$key", identityKey);
            researchId = Convert.ToInt64(select.ExecuteScalar());
        }

        MergeResearchChildren(connection, transaction, researchId, item, confidence, now, authoritativeAudit);
        return researchId;
    }

    private static void MergeResearchChildren(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long researchId,
        TrvPackBroadcast item,
        int confidence,
        string now,
        bool authoritativeAudit)
    {
        if (authoritativeAudit)
        {
            foreach (var table in new[] { "research_sources", "research_people", "research_topics", "research_moments" })
            {
                using var clear = connection.CreateCommand();
                clear.Transaction = transaction;
                clear.CommandText = $"DELETE FROM {table} WHERE research_broadcast_id=$research";
                clear.Parameters.AddWithValue("$research", researchId);
                clear.ExecuteNonQuery();
            }
        }

        foreach (var source in item.Sources
                     .Where(x => !string.IsNullOrWhiteSpace(x.Url) || !string.IsNullOrWhiteSpace(x.Title)))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO research_sources(
                    research_broadcast_id,url,title,publisher,source_type,accessed_at,
                    confidence,supports,notes,created_at)
                VALUES($research,$url,$title,$publisher,$type,$accessed,$confidence,$supports,$notes,$now)
                ON CONFLICT(research_broadcast_id,url,title) DO UPDATE SET
                    publisher=CASE WHEN excluded.publisher<>'' THEN excluded.publisher ELSE research_sources.publisher END,
                    source_type=excluded.source_type,
                    accessed_at=COALESCE(excluded.accessed_at,research_sources.accessed_at),
                    confidence=MAX(research_sources.confidence,excluded.confidence),
                    supports=CASE WHEN excluded.supports<>'' THEN excluded.supports ELSE research_sources.supports END,
                    notes=CASE WHEN excluded.notes<>'' THEN excluded.notes ELSE research_sources.notes END
                """;
            command.Parameters.AddWithValue("$research", researchId);
            command.Parameters.AddWithValue("$url", source.Url?.Trim() ?? "");
            command.Parameters.AddWithValue("$title", source.Title?.Trim() ?? "");
            command.Parameters.AddWithValue("$publisher", source.Publisher?.Trim() ?? "");
            command.Parameters.AddWithValue("$type", ClassifySourceType(source));
            command.Parameters.AddWithValue("$accessed", string.IsNullOrWhiteSpace(source.Accessed) ? DBNull.Value : source.Accessed.Trim());
            command.Parameters.AddWithValue("$confidence", confidence);
            command.Parameters.AddWithValue("$supports", string.Join("|", NormalizeNames(source.Supports)));
            command.Parameters.AddWithValue("$notes", source.Notes?.Trim() ?? "");
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }

        var research = item.Research ?? new TrvPackResearch();
        var people = research.People ?? new TrvPackPeople();
        var roles = new (string Role, IEnumerable<string> Names)[]
        {
            ("host", people.Hosts),
            ("guest", people.Guests.Concat(research.Guests ?? new List<string>())),
            ("caller", people.Callers),
            ("mentioned", people.MentionedPeople)
        };
        foreach (var role in roles)
        foreach (var name in NormalizeNames(role.Names))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO research_people(research_broadcast_id,name,role,confidence,notes,created_at)
                SELECT $research,$name,$role,$confidence,'',$now
                WHERE NOT EXISTS(
                    SELECT 1 FROM research_people
                    WHERE research_broadcast_id=$research
                      AND role=$role
                      AND lower(trim(name))=lower(trim($name))
                )
                """;
            command.Parameters.AddWithValue("$research", researchId);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$role", role.Role);
            command.Parameters.AddWithValue("$confidence", confidence);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }

        foreach (var topic in NormalizeNames(research.Topics))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO research_topics(research_broadcast_id,topic,confidence,notes,created_at)
                SELECT $research,$topic,$confidence,'',$now
                WHERE NOT EXISTS(
                    SELECT 1 FROM research_topics
                    WHERE research_broadcast_id=$research
                      AND lower(trim(topic))=lower(trim($topic))
                )
                """;
            command.Parameters.AddWithValue("$research", researchId);
            command.Parameters.AddWithValue("$topic", topic);
            command.Parameters.AddWithValue("$confidence", confidence);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }

        foreach (var moment in (research.Moments ?? new List<TrvPackMoment>())
                     .Where(x => x.TimestampSeconds >= 0 && !string.IsNullOrWhiteSpace(x.Title)))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO research_moments(
                    research_broadcast_id,timestamp_seconds,title,description,tags,confidence,created_at)
                VALUES($research,$timestamp,$title,$description,$tags,$confidence,$now)
                ON CONFLICT(research_broadcast_id,timestamp_seconds,title) DO UPDATE SET
                    description=CASE WHEN excluded.description<>'' THEN excluded.description ELSE research_moments.description END,
                    tags=CASE WHEN excluded.tags<>'' THEN excluded.tags ELSE research_moments.tags END,
                    confidence=MAX(research_moments.confidence,excluded.confidence)
                """;
            command.Parameters.AddWithValue("$research", researchId);
            command.Parameters.AddWithValue("$timestamp", Math.Max(0, moment.TimestampSeconds));
            command.Parameters.AddWithValue("$title", moment.Title.Trim());
            command.Parameters.AddWithValue("$description", moment.Description?.Trim() ?? "");
            command.Parameters.AddWithValue("$tags", string.Join("|", NormalizeNames(moment.Tags)));
            command.Parameters.AddWithValue("$confidence", confidence);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }

        var aliases = new List<(string Type, string Value, int Confidence)>();
        if (!string.IsNullOrWhiteSpace(item.BroadcastId)) aliases.Add(("broadcast_id", item.BroadcastId.Trim(), 100));
        if (!string.IsNullOrWhiteSpace(item.BroadcastDate)) aliases.Add(("date_label", item.BroadcastDate.Trim(), 100));
        foreach (var alias in aliases)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO research_aliases(
                    research_broadcast_id,alias_type,alias_value,confidence)
                VALUES($research,$type,$value,$confidence)
                """;
            command.Parameters.AddWithValue("$research", researchId);
            command.Parameters.AddWithValue("$type", alias.Type);
            command.Parameters.AddWithValue("$value", alias.Value);
            command.Parameters.AddWithValue("$confidence", alias.Confidence);
            command.ExecuteNonQuery();
        }
    }

    private static string BuildResearchIdentityKey(int collectionId, TrvPackBroadcast item)
    {
        var slot = NormalizeResearchKeyPart(GetKnowledgePackSlot(item));
        var part = Math.Max(1, item.PartNumber);
        string identity;
        if (!string.IsNullOrWhiteSpace(item.BroadcastId))
            identity = $"{collectionId}|uid|{NormalizeResearchKeyPart(item.BroadcastId)}";
        else if (!string.IsNullOrWhiteSpace(item.BroadcastDate))
            identity = $"{collectionId}|date|{item.BroadcastDate.Trim()}|{slot}|{part}|primary";
        else
            identity = $"{collectionId}|undated|{slot}|{part}|{NormalizeResearchKeyPart(item.Research?.Headline)}|{NormalizeResearchKeyPart(item.Sources.FirstOrDefault()?.Url)}";
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static string DeriveExistenceStatus(TrvPackBroadcast item)
    {
        var confidence = Math.Clamp(item.Research?.Quality?.Confidence ?? 0, 0, 100);
        var sourceCount = item.Sources?.Count(source =>
            !string.IsNullOrWhiteSpace(source.Url) || !string.IsNullOrWhiteSpace(source.Title)) ?? 0;
        if (ResearchReconciliationRules.ShouldTreatAsConfirmedMissing(sourceCount, confidence)) return "confirmed_missing";
        if (ResearchReconciliationRules.ShouldTreatAsProbableMissing(sourceCount, confidence)) return "probable_missing";
        return "unknown_gap";
    }

    private static void IncrementExistenceResult(KnowledgePackImportResult result, string existence)
    {
        switch (existence)
        {
            case "confirmed_missing": result.ConfirmedMissing++; break;
            case "probable_missing": result.ProbableMissing++; break;
            default: result.UnknownGaps++; break;
        }
    }

    private static int ResolveResearchCollectionId(SqliteConnection connection, string? show)
        => ResolveResearchCollectionId(connection, null, show);

    private static int ResolveResearchCollectionId(SqliteConnection connection, SqliteTransaction? transaction, string? show)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.id FROM collections c
            LEFT JOIN collection_aliases ca ON ca.collection_id=c.id
            WHERE lower(c.name)=lower($show) OR lower(ca.alias)=lower($show)
            ORDER BY CASE WHEN lower(c.name)=lower($show) THEN 0 ELSE 1 END
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$show", show?.Trim() ?? "");
        var resolved = command.ExecuteScalar();
        if (resolved is not null && resolved != DBNull.Value) return Convert.ToInt32(resolved);

        using var fallback = connection.CreateCommand();
        fallback.Transaction = transaction;
        fallback.CommandText = "SELECT id FROM collections WHERE name='Unsorted' LIMIT 1";
        return Convert.ToInt32(fallback.ExecuteScalar());
    }

    private static long? GetLegacyMissingResearchId(SqliteConnection connection, TrvPackBroadcast item)
        => GetLegacyMissingResearchId(connection, null, item);

    private static long? GetLegacyMissingResearchId(SqliteConnection connection, SqliteTransaction? transaction, TrvPackBroadcast item)
    {
        var key = BuildMissingResearchStableKey(item, KnowledgePackService.SerializeBroadcast(item));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM missing_broadcast_research WHERE stable_key=$key";
        command.Parameters.AddWithValue("$key", key);
        var value = command.ExecuteScalar();
        return value is null || value == DBNull.Value ? null : Convert.ToInt64(value);
    }

    private static long BeginResearchImportRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TrvKnowledgePack pack,
        string? sourcePath,
        string rollbackSnapshot)
    {
        var packageName = string.IsNullOrWhiteSpace(sourcePath) ? $"{pack.Manifest.Show} research pack" : Path.GetFileName(sourcePath);
        var packageHash = "";
        if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
        {
            using var stream = File.OpenRead(sourcePath);
            packageHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        var now = DateTime.UtcNow.ToString("O");
        var pendingSummary = JsonSerializer.Serialize(new { Status = "committing", StartedAt = now });
        var rollback = JsonSerializer.Serialize(new { SnapshotPath = rollbackSnapshot, CapturedAt = now, Kind = "pre_import_database_snapshot" });
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO research_import_runs(
                package_name,package_sha256,schema_version,app_version,imported_at,summary_json,rollback_json,status)
            VALUES($name,$hash,$schema,$version,$now,$summary,$rollback,'committing')
            """;
        command.Parameters.AddWithValue("$name", packageName);
        command.Parameters.AddWithValue("$hash", packageHash);
        command.Parameters.AddWithValue("$schema", pack.Manifest.SchemaVersion);
        command.Parameters.AddWithValue("$version", AppVersionService.Version);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$summary", pendingSummary);
        command.Parameters.AddWithValue("$rollback", rollback);
        command.ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(select.ExecuteScalar());
    }

    private static void CompleteResearchImportRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId,
        KnowledgePackImportResult result)
    {
        var summary = JsonSerializer.Serialize(new
        {
            Status = "completed",
            CompletedAt = DateTime.UtcNow.ToString("O"),
            result.Total,
            result.Matched,
            result.Updated,
            result.RetainedMissing,
            result.Ambiguous,
            result.ResolvedPreviousMissing,
            result.ResearchRecordsStored,
            result.AttachedResearchRecords,
            result.ConfirmedMissing,
            result.ProbableMissing,
            result.UnknownGaps,
            result.ConflictsCreated,
            result.FieldsApplied,
            result.FieldsMerged,
            result.FieldsPreserved,
            result.ManualFieldsProtected,
            result.ChangeRecordsWritten
        });
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE research_import_runs SET
              imported_count=$total,matched_count=$matched,missing_count=$missing,
              conflict_count=$conflicts,summary_json=$summary,status='completed'
            WHERE id=$id
            """;
        command.Parameters.AddWithValue("$total", result.Total);
        command.Parameters.AddWithValue("$matched", result.Matched);
        command.Parameters.AddWithValue("$missing", result.RetainedMissing);
        command.Parameters.AddWithValue("$conflicts", result.ConflictsCreated);
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue("$id", runId);
        command.ExecuteNonQuery();
    }

    private static int RecordScalarResearchConflicts(
        SqliteConnection connection,
        long researchBroadcastId,
        long episodeId,
        TrvPackBroadcast item,
        EpisodeMetadata existing)
        => RecordScalarResearchConflicts(connection, null, researchBroadcastId, episodeId, item, existing);

    private static int RecordScalarResearchConflicts(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long researchBroadcastId,
        long episodeId,
        TrvPackBroadcast item,
        EpisodeMetadata existing)
    {
        var conflicts = new List<(string Field, string Existing, string Incoming)>();
        AddConflict(conflicts, "headline", existing.Headline, item.Research?.Headline);
        AddConflict(conflicts, "summary", existing.Description, item.Research?.Summary);
        AddConflict(conflicts, "station", existing.Edition, item.Research?.Broadcast?.Station ?? item.Research?.Edition);

        var inserted = 0;
        foreach (var conflict in conflicts)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO research_conflicts(
                    research_broadcast_id,episode_id,field_name,existing_value,incoming_value,
                    existing_source,incoming_source,resolution,created_at)
                SELECT $research,$episode,$field,$existing,$incoming,
                       'library episode','research pack','unresolved',$now
                WHERE NOT EXISTS(
                    SELECT 1 FROM research_conflicts
                    WHERE research_broadcast_id=$research
                      AND episode_id=$episode
                      AND field_name=$field
                      AND existing_value=$existing
                      AND incoming_value=$incoming
                      AND resolution='unresolved'
                )
                """;
            command.Parameters.AddWithValue("$research", researchBroadcastId);
            command.Parameters.AddWithValue("$episode", episodeId);
            command.Parameters.AddWithValue("$field", conflict.Field);
            command.Parameters.AddWithValue("$existing", conflict.Existing);
            command.Parameters.AddWithValue("$incoming", conflict.Incoming);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            inserted += command.ExecuteNonQuery();
        }

        if (inserted > 0)
        {
            using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            mark.CommandText = """
                UPDATE research_broadcasts
                SET needs_review=1,research_state='conflicting_information',updated_at=$now
                WHERE id=$id
                """;
            mark.Parameters.AddWithValue("$id", researchBroadcastId);
            mark.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            mark.ExecuteNonQuery();
        }
        return inserted;
    }

    private static void ResolveConflictsSupersededByAuthoritativeAudit(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long researchBroadcastId)
    {
        var now = DateTime.UtcNow.ToString("O");
        using (var resolve = connection.CreateCommand())
        {
            resolve.Transaction = transaction;
            resolve.CommandText = """
                UPDATE research_conflicts
                SET resolution='ignored',resolved_at=$now
                WHERE research_broadcast_id=$research AND resolution='unresolved'
                """;
            resolve.Parameters.AddWithValue("$research", researchBroadcastId);
            resolve.Parameters.AddWithValue("$now", now);
            resolve.ExecuteNonQuery();
        }

        using var refresh = connection.CreateCommand();
        refresh.Transaction = transaction;
        refresh.CommandText = """
            UPDATE research_broadcasts SET
                needs_review=CASE
                    WHEN EXISTS(SELECT 1 FROM research_reconciliation_candidates rrc
                                WHERE rrc.research_broadcast_id=$research
                                  AND rrc.status='pending' AND rrc.requires_review=1) THEN 1
                    ELSE 0 END,
                research_state=CASE
                    WHEN EXISTS(SELECT 1 FROM research_reconciliation_candidates rrc
                                WHERE rrc.research_broadcast_id=$research
                                  AND rrc.status='pending' AND rrc.requires_review=1) THEN 'conflicting_information'
                    WHEN episode_id IS NOT NULL THEN 'in_library'
                    ELSE 'missing_recording' END,
                updated_at=$now
            WHERE id=$research
            """;
        refresh.Parameters.AddWithValue("$research", researchBroadcastId);
        refresh.Parameters.AddWithValue("$now", now);
        refresh.ExecuteNonQuery();
    }

    private string CreateResearchImportSnapshot(string? sourcePath)
    {
        var directory = Path.Combine(AppPaths.BackupDirectory, "Research Imports");
        Directory.CreateDirectory(directory);
        var sourceName = string.IsNullOrWhiteSpace(sourcePath)
            ? "research-pack"
            : Path.GetFileNameWithoutExtension(sourcePath);
        var safeName = new string(sourceName
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character)
            .ToArray())
            .Trim();
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "research-pack";
        if (safeName.Length > 80) safeName = safeName[..80];
        var snapshotPath = Path.Combine(directory, $"pre-import-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{safeName}.sqlite");
        using var source = _database.OpenConnection();
        using var destination = new SqliteConnection($"Data Source={snapshotPath};Mode=ReadWriteCreate");
        destination.Open();
        source.BackupDatabase(destination);
        return snapshotPath;
    }

    private static void TryDeleteImportSnapshot(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A failed import has already rolled back through SQLite. A delayed
            // cleanup of its unused safety snapshot must not mask the real error.
        }
    }

    private static string BuildImportRecordIdentity(TrvPackBroadcast item)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Show)) parts.Add(item.Show.Trim());
        if (!string.IsNullOrWhiteSpace(item.BroadcastDate)) parts.Add(item.BroadcastDate.Trim());
        if (!string.IsNullOrWhiteSpace(GetKnowledgePackSlot(item))) parts.Add(GetKnowledgePackSlot(item)!);
        if (item.PartNumber > 1) parts.Add($"part {item.PartNumber}");
        if (!string.IsNullOrWhiteSpace(item.BroadcastId)) parts.Add(item.BroadcastId.Trim());
        return string.Join(" · ", parts);
    }

    private static void RecordKnowledgePackMergeChanges(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long importRunId,
        long researchBroadcastId,
        long episodeId,
        string identity,
        KnowledgePackMergePlan plan,
        KnowledgePackImportResult result)
    {
        foreach (var decision in plan.Decisions)
        {
            switch (decision.Decision)
            {
                case "applied": result.FieldsApplied++; break;
                case "merged": result.FieldsMerged++; break;
                case "preserved": result.FieldsPreserved++; break;
                case "protected":
                    result.FieldsPreserved++;
                    result.ManualFieldsProtected++;
                    break;
            }

            RecordImportChange(
                connection,
                transaction,
                importRunId,
                researchBroadcastId,
                episodeId,
                identity,
                decision.FieldName,
                decision.BeforeValue,
                decision.AfterValue,
                decision.Decision,
                decision.Reason,
                result);
        }
    }

    private static void RecordImportChange(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long importRunId,
        long? researchBroadcastId,
        long? episodeId,
        string identity,
        string fieldName,
        string beforeValue,
        string afterValue,
        string decision,
        string reason,
        KnowledgePackImportResult result)
    {
        identity ??= string.Empty;
        fieldName ??= string.Empty;
        beforeValue ??= string.Empty;
        afterValue ??= string.Empty;
        decision ??= "unchanged";
        reason ??= string.Empty;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO research_import_changes(
                import_run_id,research_broadcast_id,episode_id,record_identity,
                field_name,before_value,after_value,decision,reason,created_at)
            VALUES($run,$research,$episode,$identity,$field,$before,$after,$decision,$reason,$now)
            """;
        command.Parameters.AddWithValue("$run", importRunId);
        command.Parameters.AddWithValue("$research", researchBroadcastId.HasValue ? researchBroadcastId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$episode", episodeId.HasValue ? episodeId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$identity", identity);
        command.Parameters.AddWithValue("$field", fieldName);
        command.Parameters.AddWithValue("$before", beforeValue);
        command.Parameters.AddWithValue("$after", afterValue);
        command.Parameters.AddWithValue("$decision", decision);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        result.ChangeRecordsWritten++;

        if (decision is "applied" or "merged" or "created" or "retained_missing" or "ambiguous")
            RecordImportFieldProvenance(connection, transaction, importRunId, researchBroadcastId, episodeId, fieldName, afterValue, protectedValue: false);
        else if (decision == "protected")
            RecordImportFieldProvenance(connection, transaction, importRunId, researchBroadcastId, episodeId, fieldName, beforeValue, protectedValue: true);
    }

    private static void AddConflict(List<(string Field, string Existing, string Incoming)> conflicts, string field, string? existing, string? incoming)
    {
        var left = existing?.Trim() ?? "";
        var right = incoming?.Trim() ?? "";
        if (left.Length == 0 || right.Length == 0) return;
        if (string.Equals(NormalizeResearchKeyPart(left), NormalizeResearchKeyPart(right), StringComparison.Ordinal)) return;
        conflicts.Add((field, left, right));
    }

    private static string ClassifySourceType(TrvPackSource source)
    {
        var text = $"{source.Url} {source.Title} {source.Publisher}".ToLowerInvariant();
        if (text.Contains("reddit.com") || text.Contains("discussion thread") || text.Contains("listening thread")) return "listening_thread";
        if (text.Contains("siriusxm") || text.Contains("official")) return "official";
        if (text.Contains("archive.org") || text.Contains("fourble") || text.Contains("archive index")) return "archive_index";
        return "community";
    }

    private static void UpsertReconciliationCandidate(SqliteConnection connection, long researchId, long episodeId, int score, string reason)
        => UpsertReconciliationCandidate(connection, null, researchId, episodeId, score, reason);

    private static void UpsertReconciliationCandidate(SqliteConnection connection, SqliteTransaction? transaction, long researchId, long episodeId, int score, string reason)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO research_reconciliation_candidates(
                research_broadcast_id,episode_id,score,reason,status,created_at,updated_at)
            VALUES($research,$episode,$score,$reason,'pending',$now,$now)
            ON CONFLICT(research_broadcast_id,episode_id) DO UPDATE SET
                score=excluded.score,reason=excluded.reason,
                status=CASE WHEN research_reconciliation_candidates.status IN ('approved','rejected') THEN research_reconciliation_candidates.status ELSE 'pending' END,
                requires_review=CASE WHEN research_reconciliation_candidates.status IN ('approved','rejected') THEN 0 ELSE 1 END,
                review_category=CASE
                    WHEN research_reconciliation_candidates.status IN ('approved','rejected') THEN research_reconciliation_candidates.review_category
                    WHEN research_reconciliation_candidates.review_category='manual_hold' THEN 'manual_hold'
                    ELSE 'ambiguous_match' END,
                recommended_action=CASE
                    WHEN research_reconciliation_candidates.status IN ('approved','rejected') THEN research_reconciliation_candidates.recommended_action
                    WHEN research_reconciliation_candidates.review_category='manual_hold' THEN research_reconciliation_candidates.recommended_action
                    ELSE '' END,
                decision_source=CASE WHEN research_reconciliation_candidates.status IN ('approved','rejected') THEN research_reconciliation_candidates.decision_source ELSE 'manual' END,
                updated_at=excluded.updated_at
            """;
        command.Parameters.AddWithValue("$research", researchId);
        command.Parameters.AddWithValue("$episode", episodeId);
        command.Parameters.AddWithValue("$score", Math.Clamp(score, 0, 100));
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void ApproveReconciliationCandidate(SqliteConnection connection, long researchId, long episodeId, int score, string reason)
    {
        UpsertReconciliationCandidate(connection, researchId, episodeId, score, reason);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE research_reconciliation_candidates
            SET status='approved',requires_review=0,review_category='exact_identity',
                recommended_action='Attached manually from Broadcasts to find.',decision_source='manual',updated_at=$now
            WHERE research_broadcast_id=$research AND episode_id=$episode
            """;
        command.Parameters.AddWithValue("$research", researchId);
        command.Parameters.AddWithValue("$episode", episodeId);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void MarkResearchNeedsReview(SqliteConnection connection, long researchId, bool needsReview)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE research_broadcasts SET needs_review=$review,updated_at=$now WHERE id=$id";
        command.Parameters.AddWithValue("$review", needsReview ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", researchId);
        command.ExecuteNonQuery();
    }

    private static void AttachResearchRecord(SqliteConnection connection, long researchId, long episodeId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE research_broadcasts SET
              episode_id=$episode,existence_status='in_library',
              research_state=CASE WHEN EXISTS(
                  SELECT 1 FROM research_conflicts rc
                  WHERE rc.research_broadcast_id=research_broadcasts.id
                    AND rc.resolution='unresolved'
              ) THEN 'conflicting_information' ELSE 'in_library' END,
              needs_review=CASE WHEN EXISTS(
                  SELECT 1 FROM research_conflicts rc
                  WHERE rc.research_broadcast_id=research_broadcasts.id
                    AND rc.resolution='unresolved'
              ) THEN 1 ELSE 0 END,
              attached_at=$now,updated_at=$now
            WHERE id=$id
            """;
        command.Parameters.AddWithValue("$episode", episodeId);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", researchId);
        command.ExecuteNonQuery();
    }

    private static void SyncLegacyResearchState(SqliteConnection connection, long researchId, string status, long? episodeId, string notes)
    {
        using var find = connection.CreateCommand();
        find.CommandText = "SELECT legacy_missing_research_id FROM research_broadcasts WHERE id=$id";
        find.Parameters.AddWithValue("$id", researchId);
        var value = find.ExecuteScalar();
        if (value is null || value == DBNull.Value) return;
        SetMissingResearchState(connection, Convert.ToInt64(value), status, episodeId, notes);
    }

    private static TrvPackBroadcast? TryDeserializeResearchBroadcast(SqliteConnection connection, long researchId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT research_json FROM research_broadcasts WHERE id=$id";
        command.Parameters.AddWithValue("$id", researchId);
        var json = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return KnowledgePackService.DeserializeBroadcast(json); }
        catch { return null; }
    }
    private static void SyncResearchStatusFromLegacy(SqliteConnection connection, long legacyId, string status)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE research_broadcasts SET
              episode_id=NULL,
              research_state='missing_recording',
              existence_status=CASE
                WHEN confidence>=85 AND EXISTS(SELECT 1 FROM research_sources rs WHERE rs.research_broadcast_id=research_broadcasts.id) THEN 'confirmed_missing'
                WHEN confidence>=60 AND EXISTS(SELECT 1 FROM research_sources rs WHERE rs.research_broadcast_id=research_broadcasts.id) THEN 'probable_missing'
                ELSE 'unknown_gap' END,
              needs_review=CASE WHEN $status='ambiguous' THEN 1 ELSE 0 END,
              attached_at=NULL,updated_at=$now
            WHERE legacy_missing_research_id=$legacy
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$legacy", legacyId);
        command.ExecuteNonQuery();
    }

}
