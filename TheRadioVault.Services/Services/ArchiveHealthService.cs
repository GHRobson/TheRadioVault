using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TheRadioVault.Core.Services;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

public sealed class ArchiveHealthService : IArchiveHealthService
{
    private readonly SqliteDatabase _database;

    public ArchiveHealthService(SqliteDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task<ArchiveHealthReport> AnalyseAsync(ArchiveHealthOptions? options = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Analyse(options ?? new ArchiveHealthOptions(), cancellationToken), cancellationToken);

    private ArchiveHealthReport Analyse(ArchiveHealthOptions options, CancellationToken cancellationToken)
    {
        _database.Initialize();
        using var connection = _database.OpenConnection();

        var totalBroadcasts = Scalar(connection, $"""
            SELECT COUNT(*) FROM episodes e
            WHERE COALESCE(e.hidden,0)=0
              AND {ActiveEpisodePredicate("e")};
            """);
        var registeredFolders = Scalar(connection, "SELECT COUNT(*) FROM library_folders WHERE enabled=1");
        var neverScannedFolders = Scalar(connection, "SELECT COUNT(*) FROM library_folders WHERE enabled=1 AND last_scan_at IS NULL");
        var missingFiles = Scalar(connection, $"""
            SELECT COUNT(*) FROM media_files mf
            WHERE (mf.is_missing=1 OR mf.storage_state IN ('Missing','Unavailable'))
              AND {ActiveFolderPredicate("mf")};
            """);
        var cloudOnlyFiles = Scalar(connection, $"""
            SELECT COUNT(*) FROM media_files mf
            WHERE mf.is_missing=0 AND mf.storage_state='CloudOnly'
              AND {ActiveFolderPredicate("mf")};
            """);
        var missingArtwork = Scalar(connection, $"""
            SELECT COUNT(*) FROM episodes e
            WHERE COALESCE(e.hidden,0)=0
              AND (e.artwork_path IS NULL OR trim(e.artwork_path)='')
              AND {ActiveEpisodePredicate("e")};
            """);
        var needsReview = Scalar(connection, $"""
            SELECT COUNT(*) FROM episodes e
            WHERE COALESCE(e.hidden,0)=0
              AND (e.air_date IS NULL OR e.date_confidence IN ('Unknown','Ambiguous'))
              AND {ActiveEpisodePredicate("e")};
            """);
        var unfingerprintedFiles = Scalar(connection, $"""
            SELECT COUNT(*) FROM media_files mf
            WHERE mf.is_missing=0 AND mf.storage_state='AvailableOffline'
              AND (mf.partial_hash IS NULL OR trim(mf.partial_hash)='')
              AND {ActiveFolderPredicate("mf")};
            """);

        var totalResearchRecords = Scalar(connection, "SELECT COUNT(*) FROM research_broadcasts");
        var confirmedMissingBroadcasts = Scalar(connection, "SELECT COUNT(*) FROM research_broadcasts WHERE episode_id IS NULL AND existence_status='confirmed_missing'");
        var probableMissingBroadcasts = Scalar(connection, "SELECT COUNT(*) FROM research_broadcasts WHERE episode_id IS NULL AND existence_status='probable_missing'");
        var unknownResearchGaps = Scalar(connection, "SELECT COUNT(*) FROM research_broadcasts WHERE episode_id IS NULL AND existence_status='unknown_gap'");
        var researchNeedsReview = Scalar(connection, """
            SELECT COUNT(*) FROM (
                SELECT research_broadcast_id
                FROM research_reconciliation_candidates
                WHERE status='pending' AND requires_review=1
                UNION
                SELECT research_broadcast_id
                FROM research_conflicts
                WHERE resolution='unresolved'
            )
            """);
        var researchConflicts = Scalar(connection, "SELECT COUNT(DISTINCT research_broadcast_id) FROM research_conflicts WHERE resolution='unresolved'");
        var unsourcedResearchRecords = Scalar(connection, "SELECT COUNT(*) FROM research_broadcasts rb WHERE NOT EXISTS(SELECT 1 FROM research_sources rs WHERE rs.research_broadcast_id=rb.id)");
        var lowConfidenceResearchRecords = Scalar(connection, "SELECT COUNT(*) FROM research_broadcasts WHERE confidence>0 AND confidence<60");
        var pendingReconciliationCandidates = Scalar(connection, "SELECT COUNT(DISTINCT research_broadcast_id) FROM research_reconciliation_candidates WHERE status='pending' AND requires_review=1");
        var researchWithSummaries = Scalar(connection, "SELECT COUNT(*) FROM research_broadcasts WHERE trim(summary)<>''");
        var researchWithPeople = Scalar(connection, "SELECT COUNT(*) FROM research_broadcasts rb WHERE EXISTS(SELECT 1 FROM research_people rp WHERE rp.research_broadcast_id=rb.id)");
        var researchWithTopics = Scalar(connection, "SELECT COUNT(*) FROM research_broadcasts rb WHERE EXISTS(SELECT 1 FROM research_topics rt WHERE rt.research_broadcast_id=rb.id)");
        var researchWithSources = totalResearchRecords - unsourcedResearchRecords;
        var lastCompletedScanAt = LastCompletedScan(connection);

        var issues = new List<ArchiveHealthIssue>();
        AddCollectionFileIssues(connection, issues, options.IncludeCloudOnlyInHealth, cancellationToken);
        AddKnownArchiveGapIssues(issues, confirmedMissingBroadcasts, probableMissingBroadcasts, unknownResearchGaps);
        var duplicateCandidates = AddDuplicatePreservationIssues(connection, issues, cancellationToken);
        AddSuspiciousMediaIssues(connection, issues, cancellationToken);
        var genericTitles = AddMetadataIssues(connection, issues, cancellationToken);
        AddResearchIssues(connection, issues, unsourcedResearchRecords, lowConfidenceResearchRecords, pendingReconciliationCandidates, cancellationToken);
        AddFingerprintPreservationIssues(connection, issues, cancellationToken);
        AddScanFreshnessIssue(issues, registeredFolders, lastCompletedScanAt);

        if (neverScannedFolders > 0)
        {
            issues.Add(new ArchiveHealthIssue(
                ArchiveHealthArea.Collection,
                ArchiveHealthSeverity.Suggestion,
                null,
                null,
                "Library",
                null,
                $"{neverScannedFolders:N0} archive folder(s) have never been scanned",
                "Those locations will not contribute broadcasts until a scan is run.",
                "Run a library scan from Settings when convenient."));
        }

        var unavailableForScore = missingFiles + (options.IncludeCloudOnlyInHealth ? cloudOnlyFiles : 0);
        var collectionScore = CalculateCollectionScore(totalBroadcasts, unavailableForScore, confirmedMissingBroadcasts, probableMissingBroadcasts, neverScannedFolders);
        var metadataScore = CalculateMetadataScore(totalBroadcasts, needsReview, genericTitles, missingArtwork);
        var researchScore = CalculateResearchScore(totalResearchRecords, researchWithSummaries, researchWithPeople, researchWithTopics, researchWithSources, researchNeedsReview, researchConflicts);
        var preservationScore = CalculatePreservationScore(totalBroadcasts, unfingerprintedFiles, duplicateCandidates, lastCompletedScanAt);
        var healthScore = CalculateOverallScore(collectionScore, metadataScore, researchScore, preservationScore, totalResearchRecords > 0);

        return new ArchiveHealthReport(
            healthScore,
            collectionScore,
            metadataScore,
            researchScore,
            preservationScore,
            totalBroadcasts,
            registeredFolders,
            missingFiles,
            cloudOnlyFiles,
            duplicateCandidates,
            needsReview,
            missingArtwork,
            genericTitles,
            unfingerprintedFiles,
            neverScannedFolders,
            totalResearchRecords,
            confirmedMissingBroadcasts,
            probableMissingBroadcasts,
            unknownResearchGaps,
            researchNeedsReview,
            researchConflicts,
            unsourcedResearchRecords,
            lowConfidenceResearchRecords,
            pendingReconciliationCandidates,
            lastCompletedScanAt,
            issues);
    }

    private static int Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static DateTime? LastCompletedScan(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT completed_at FROM scan_runs WHERE completed_at IS NOT NULL ORDER BY id DESC LIMIT 1";
        var value = command.ExecuteScalar()?.ToString();
        return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private static void AddKnownArchiveGapIssues(
        ICollection<ArchiveHealthIssue> issues,
        int confirmedMissing,
        int probableMissing,
        int unknownGaps)
    {
        if (confirmedMissing > 0)
        {
            issues.Add(new ArchiveHealthIssue(
                ArchiveHealthArea.Collection,
                ArchiveHealthSeverity.Suggestion,
                null,
                null,
                "Broadcasts to find",
                null,
                $"{confirmedMissing:N0} confirmed broadcast{(confirmedMissing == 1 ? " is" : "s are")} worth looking out for",
                "Research confirms these broadcasts aired, but they are not currently present in your archive. This does not indicate that Radio Vault lost a file.",
                "Open Research → Broadcasts to find to review the leads."));
        }

        if (probableMissing > 0)
        {
            issues.Add(new ArchiveHealthIssue(
                ArchiveHealthArea.Collection,
                ArchiveHealthSeverity.Suggestion,
                null,
                null,
                "Broadcasts to find",
                null,
                $"{probableMissing:N0} strong broadcast lead{(probableMissing == 1 ? " is" : "s are")} recorded",
                "Community or schedule evidence suggests these broadcasts existed, but the evidence is not yet conclusive.",
                "Open Research → Broadcasts to find when you want to inspect the evidence."));
        }

        if (unknownGaps > 0)
        {
            issues.Add(new ArchiveHealthIssue(
                ArchiveHealthArea.Collection,
                ArchiveHealthSeverity.Information,
                null,
                null,
                "Broadcasts to find",
                null,
                $"{unknownGaps:N0} tentative broadcast lead{(unknownGaps == 1 ? " is" : "s are")} being tracked",
                "These are research leads only and do not imply that anything has disappeared from the archive.",
                "No action is required unless stronger evidence or a recording turns up."));
        }
    }

    private static void AddResearchIssues(
        SqliteConnection connection,
        ICollection<ArchiveHealthIssue> issues,
        int unsourcedRecords,
        int lowConfidenceRecords,
        int pendingCandidates,
        CancellationToken cancellationToken)
    {
        using (var conflicts = connection.CreateCommand())
        {
            conflicts.CommandText = """
                SELECT rb.id,rb.episode_id,c.name,rb.air_date,rb.headline,COUNT(rc.id)
                FROM research_broadcasts rb
                JOIN collections c ON c.id=rb.collection_id
                JOIN research_conflicts rc ON rc.research_broadcast_id=rb.id AND rc.resolution='unresolved'
                GROUP BY rb.id,rb.episode_id,c.name,rb.air_date,rb.headline
                ORDER BY COUNT(rc.id) DESC,c.sort_name,rb.air_date
                LIMIT 500;
                """;
            using var reader = conflicts.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var conflictCount = Convert.ToInt32(reader.GetInt64(5));
                issues.Add(new ArchiveHealthIssue(
                    ArchiveHealthArea.Research,
                    ArchiveHealthSeverity.Warning,
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    null,
                    reader.GetString(2),
                    ParseDate(reader, 3),
                    string.IsNullOrWhiteSpace(reader.GetString(4)) ? reader.GetString(2) : reader.GetString(4),
                    $"{conflictCount:N0} unresolved research conflict{(conflictCount == 1 ? "" : "s")}",
                    "Open Research → Needs your decision, select the record, then choose Keep library value or Use research value.",
                    ResearchBroadcastId: reader.GetInt64(0)));
            }
        }

        using (var review = connection.CreateCommand())
        {
            review.CommandText = """
                SELECT rb.id,rb.episode_id,c.name,rb.air_date,rb.headline,rb.confidence,rb.confidence_reason
                FROM research_broadcasts rb
                JOIN collections c ON c.id=rb.collection_id
                WHERE rb.needs_review=1
                  AND NOT EXISTS(
                      SELECT 1 FROM research_conflicts rc
                      WHERE rc.research_broadcast_id=rb.id AND rc.resolution='unresolved')
                ORDER BY rb.confidence,c.sort_name,rb.air_date
                LIMIT 500;
                """;
            using var reader = review.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var confidence = reader.GetInt32(5);
                var reason = reader.GetString(6);
                issues.Add(new ArchiveHealthIssue(
                    ArchiveHealthArea.Research,
                    ArchiveHealthSeverity.Suggestion,
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    null,
                    reader.GetString(2),
                    ParseDate(reader, 3),
                    string.IsNullOrWhiteSpace(reader.GetString(4)) ? reader.GetString(2) : reader.GetString(4),
                    confidence > 0
                        ? $"Research record needs review ({confidence}% confidence){(string.IsNullOrWhiteSpace(reason) ? "" : ": " + reason)}"
                        : "Research record is marked for review",
                    "Open Research → Needs your decision. Resolve any highlighted match or value, or use Mark reviewed when the explanation is sufficient.",
                    ResearchBroadcastId: reader.GetInt64(0)));
            }
        }

        if (unsourcedRecords > 0)
        {
            issues.Add(new ArchiveHealthIssue(
                ArchiveHealthArea.Research,
                ArchiveHealthSeverity.Suggestion,
                null,
                null,
                "Research & Metadata",
                null,
                $"{unsourcedRecords:N0} research record(s) have no source",
                "The information is preserved, but its provenance cannot currently be checked.",
                "Add a source URL, archive listing, listening thread or manual provenance note."));
        }

        if (lowConfidenceRecords > 0)
        {
            issues.Add(new ArchiveHealthIssue(
                ArchiveHealthArea.Research,
                ArchiveHealthSeverity.Suggestion,
                null,
                null,
                "Research & Metadata",
                null,
                $"{lowConfidenceRecords:N0} research record(s) are below 60% confidence",
                "These records may still be useful, but they should not silently override stronger metadata.",
                "Review them before reconciliation or add corroborating sources."));
        }

        if (pendingCandidates > 0)
        {
            issues.Add(new ArchiveHealthIssue(
                ArchiveHealthArea.Research,
                ArchiveHealthSeverity.Suggestion,
                null,
                null,
                "Research & Metadata",
                null,
                $"{pendingCandidates:N0} research decision{(pendingCandidates == 1 ? " is" : "s are")} waiting for you",
                "Radio Vault resolved safe matches automatically and grouped the remaining ambiguity by researched broadcast.",
                "Open Research → Needs your decision to choose the correct broadcast or leave the research unlinked."));
        }
    }

    private static void AddScanFreshnessIssue(ICollection<ArchiveHealthIssue> issues, int registeredFolders, DateTime? lastCompletedScanAt)
    {
        if (registeredFolders <= 0) return;
        if (!lastCompletedScanAt.HasValue)
        {
            issues.Add(new ArchiveHealthIssue(
                ArchiveHealthArea.Preservation,
                ArchiveHealthSeverity.Suggestion,
                null,
                null,
                "Library",
                null,
                "No completed library scan is recorded",
                "Archive Health cannot confirm that storage, fingerprints and missing-file states are current.",
                "Run a complete library scan when the archive locations are available."));
            return;
        }

        var age = DateTime.UtcNow - lastCompletedScanAt.Value.ToUniversalTime();
        if (age.TotalDays <= 30) return;
        issues.Add(new ArchiveHealthIssue(
            ArchiveHealthArea.Preservation,
            age.TotalDays >= 90 ? ArchiveHealthSeverity.Warning : ArchiveHealthSeverity.Suggestion,
            null,
            null,
            "Library",
            null,
            $"The last completed scan was {Math.Floor(age.TotalDays):N0} days ago",
            "Storage state, duplicate evidence and fingerprints may no longer describe the current archive.",
            "Run a complete scan after reconnecting every archive location."));
    }

    private static string ActiveFolderPredicate(string mediaAlias) => $"""
        EXISTS (
            SELECT 1
            FROM library_folders lf
            WHERE lf.enabled=1
              AND (
                  replace({mediaAlias}.path,'\','/') = rtrim(replace(lf.path,'\','/'),'/')
                  OR replace({mediaAlias}.path,'\','/') LIKE rtrim(replace(lf.path,'\','/'),'/') || '/%'
              )
        )
        """;

    private static string ActiveEpisodePredicate(string episodeAlias) => $"""
        EXISTS (
            SELECT 1
            FROM media_files active_mf
            WHERE active_mf.episode_id={episodeAlias}.id
              AND {ActiveFolderPredicate("active_mf")}
        )
        """;

    private static void AddCollectionFileIssues(SqliteConnection connection, ICollection<ArchiveHealthIssue> issues, bool includeCloudOnly, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT e.id,mf.id,c.name,e.air_date,e.title,mf.original_filename,mf.path,mf.storage_state,mf.is_missing
            FROM media_files mf
            JOIN episodes e ON e.id=mf.episode_id
            JOIN collections c ON c.id=e.collection_id
            WHERE (mf.is_missing=1 OR mf.storage_state IN ('Missing','Unavailable','CloudOnly'))
              AND {ActiveFolderPredicate("mf")}
            ORDER BY CASE WHEN mf.is_missing=1 OR mf.storage_state IN ('Missing','Unavailable') THEN 0 ELSE 1 END,
                     c.sort_name,e.air_date
            LIMIT 2000;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = reader.IsDBNull(7) ? "AvailableOffline" : reader.GetString(7);
            var missing = reader.GetInt32(8) == 1 || state is "Missing" or "Unavailable";
            if (!missing && state == "CloudOnly" && !includeCloudOnly)
                continue;

            issues.Add(new ArchiveHealthIssue(
                ArchiveHealthArea.Collection,
                missing ? ArchiveHealthSeverity.Error : ArchiveHealthSeverity.Warning,
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                ParseDate(reader, 3),
                CleanTitle(reader.GetString(4), reader.GetString(2)),
                missing ? $"Recording is unavailable: {reader.GetString(5)}" : $"Cloud-only recording: {reader.GetString(5)}",
                missing ? "Reconnect the drive, re-add the folder, or relink the recording." : "No action is required. OneDrive can fetch it when you press Play.",
                reader.GetString(6)));
        }
    }

    private static int AddDuplicatePreservationIssues(SqliteConnection connection, ICollection<ArchiveHealthIssue> issues, CancellationToken cancellationToken)
    {
        var rows = new List<DuplicateRow>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT e.id,mf.id,c.name,e.collection_id,e.air_date,e.title,e.part_number,
                       mf.original_filename,mf.path,mf.partial_hash,mf.full_hash,mf.duration_ms,
                       COALESCE(e.edition,''),mf.file_size,COALESCE(e.broadcast_slot,'')
                FROM media_files mf
                JOIN episodes e ON e.id=mf.episode_id
                JOIN collections c ON c.id=e.collection_id
                WHERE mf.is_missing=0
                  AND {ActiveFolderPredicate("mf")}
                ORDER BY c.sort_name,e.air_date,e.broadcast_slot,e.part_number,mf.original_filename;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(new DuplicateRow(
                    reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetInt32(6),
                    reader.GetString(7), reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? 0 : reader.GetInt64(11),
                    reader.GetString(12), reader.GetInt64(13), reader.GetString(14)));
            }
        }

        var groups = new List<IGrouping<string, DuplicateRow>>();

        // A complete file hash is the only evidence treated as an exact byte duplicate.
        groups.AddRange(rows
            .Where(x => !string.IsNullOrWhiteSpace(x.FullHash))
            .GroupBy(x => "full-hash|" + x.FullHash, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1));

        // Partial fingerprints are useful evidence only inside the same logical broadcast
        // occurrence. Slots and parts are hard boundaries: midday, OpieRadio and Part 2
        // are never merged merely because they share a date or opening audio.
        groups.AddRange(rows
            .Where(x => string.IsNullOrWhiteSpace(x.FullHash) && !string.IsNullOrWhiteSpace(x.PartialHash) && !string.IsNullOrWhiteSpace(x.AirDate))
            .GroupBy(x => string.Join('|',
                "partial-hash",
                x.CollectionId,
                x.AirDate,
                NormaliseIdentityToken(x.BroadcastSlot),
                x.PartNumber,
                DetectSegmentKey(x.OriginalFilename),
                DurationBucket(x.DurationMs),
                x.PartialHash), StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1));

        // With no fingerprint, compare only the same logical occurrence. A same-size
        // pair is a strong candidate; different sizes are retained as possible alternate
        // sources or encodes and do not count as duplicate-health faults.
        groups.AddRange(rows
            .Where(x => string.IsNullOrWhiteSpace(x.FullHash) && string.IsNullOrWhiteSpace(x.PartialHash) && !string.IsNullOrWhiteSpace(x.AirDate))
            .GroupBy(x => string.Join('|',
                "identity",
                x.CollectionId,
                x.AirDate,
                NormaliseIdentityToken(x.BroadcastSlot),
                x.PartNumber,
                DetectSegmentKey(x.OriginalFilename),
                DurationBucket(x.DurationMs)), StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1));

        var seenMediaIds = new HashSet<long>();
        var candidateCount = 0;
        foreach (var group in groups)
        {
            var members = group.Where(x => seenMediaIds.Add(x.MediaFileId)).ToList();
            if (members.Count < 2) continue;

            var exact = group.Key.StartsWith("full-hash|", StringComparison.OrdinalIgnoreCase);
            var partialMatch = group.Key.StartsWith("partial-hash|", StringComparison.OrdinalIgnoreCase);
            var distinctEditions = members
                .Select(x => string.IsNullOrWhiteSpace(x.Edition) ? "Standard" : x.Edition.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var sameNonZeroSize = members.All(x => x.FileSize > 0) && members.Select(x => x.FileSize).Distinct().Count() == 1;

            var distinctLogicalIdentities = members
                .Select(x => string.Join('|', x.CollectionId, x.AirDate ?? "", NormaliseIdentityToken(x.BroadcastSlot), x.PartNumber))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var classification = exact
                ? distinctLogicalIdentities > 1 ? "Exact bytes, conflicting identity" : "Exact byte duplicate"
                : partialMatch
                    ? "Strong fingerprint candidate"
                    : distinctEditions.Length > 1
                        ? "Separate recording editions"
                        : sameNonZeroSize
                            ? "Likely exact copy"
                            : LooksLikeAlternateEncode(members)
                                ? "Alternate encode"
                                : "Possible alternate recording";

            var actionable = classification is "Exact byte duplicate" or "Exact bytes, conflicting identity" or "Strong fingerprint candidate" or "Likely exact copy";
            if (actionable) candidateCount += members.Count - 1;

            foreach (var row in members.Take(1000 - issues.Count(x => x.Area == ArchiveHealthArea.Preservation)))
            {
                var slotText = string.IsNullOrWhiteSpace(row.BroadcastSlot) ? "standard slot" : row.BroadcastSlot;
                var detail = classification switch
                {
                    "Exact byte duplicate" => $"Confirmed full-file hash match ({members.Count} files): {row.OriginalFilename}",
                    "Exact bytes, conflicting identity" => $"Confirmed full-file hash match but the files claim different show/date/slot/part identities: {row.OriginalFilename}",
                    "Strong fingerprint candidate" => $"Matching partial fingerprint within the same {slotText} and part: {row.OriginalFilename}",
                    "Separate recording editions" => $"Separate recording edition ({(string.IsNullOrWhiteSpace(row.Edition) ? "standard" : row.Edition)}): {row.OriginalFilename}",
                    "Likely exact copy" => $"Same broadcast identity and identical file size ({row.FileSize:N0} bytes): {row.OriginalFilename}",
                    "Alternate encode" => $"Same broadcast identity with different encoding or size: {row.OriginalFilename}",
                    _ => $"Possible alternate recording of the same {slotText} and part: {row.OriginalFilename}"
                };
                var action = classification switch
                {
                    "Exact byte duplicate" => "This is safe to place in a future quarantine review. Nothing will be moved or deleted automatically.",
                    "Exact bytes, conflicting identity" => "Do not quarantine either file until the conflicting filename dates or broadcast identities have been reviewed.",
                    "Strong fingerprint candidate" => "Run a deep comparison or full hash before choosing a preferred copy.",
                    "Likely exact copy" => "Create full hashes before moving either file. Identical size alone is not deletion proof.",
                    "Separate recording editions" => "No action is required. Distinct editions remain linked but separately preserved.",
                    _ => "Keep both unless a later audio comparison confirms that one is redundant."
                };
                issues.Add(new ArchiveHealthIssue(
                    ArchiveHealthArea.Preservation,
                    exact ? ArchiveHealthSeverity.Warning : actionable ? ArchiveHealthSeverity.Suggestion : ArchiveHealthSeverity.Information,
                    row.EpisodeId, row.MediaFileId, row.CollectionName,
                    DateOnly.TryParse(row.AirDate, out var date) ? date : null,
                    CleanTitle(row.Title, row.CollectionName), detail, action, row.Path));
            }
        }
        return candidateCount;
    }

    private static void AddSuspiciousMediaIssues(SqliteConnection connection, ICollection<ArchiveHealthIssue> issues, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT e.id,mf.id,c.name,e.air_date,e.title,mf.original_filename,mf.path,mf.file_size,mf.duration_ms
              FROM media_files mf
              JOIN episodes e ON e.id=mf.episode_id
              JOIN collections c ON c.id=e.collection_id
             WHERE mf.is_missing=0
               AND mf.storage_state='AvailableOffline'
               AND mf.file_size>0 AND mf.file_size<262144
               AND {ActiveFolderPredicate("mf")}
             ORDER BY mf.file_size
             LIMIT 250;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var size = reader.GetInt64(7);
            var duration = reader.IsDBNull(8) ? 0 : reader.GetInt64(8);
            var durationText = duration > 0 ? $"; duration {TimeSpan.FromMilliseconds(duration):g}" : "; duration unavailable";
            issues.Add(new ArchiveHealthIssue(
                ArchiveHealthArea.Preservation,
                ArchiveHealthSeverity.Warning,
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                ParseDate(reader, 3),
                CleanTitle(reader.GetString(4), reader.GetString(2)),
                $"Unusually small audio file ({size:N0} bytes{durationText}): {reader.GetString(5)}",
                "Try opening or decoding this recording. Preserve it until you have confirmed whether it is a valid short clip, a truncated download or a damaged file.",
                reader.GetString(6)));
        }
    }

    private static int AddMetadataIssues(SqliteConnection connection, ICollection<ArchiveHealthIssue> issues, CancellationToken cancellationToken)
    {
        var genericTitles = 0;
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT e.id,c.name,e.air_date,e.title,e.date_confidence,e.artwork_path,COALESCE(e.metadata_confidence,0),COALESCE(e.metadata_confidence_reason,''),
                   (SELECT mf.original_filename FROM media_files mf WHERE mf.episode_id=e.id ORDER BY mf.is_preferred DESC,mf.id LIMIT 1)
            FROM episodes e JOIN collections c ON c.id=e.collection_id
            WHERE COALESCE(e.hidden,0)=0
              AND {ActiveEpisodePredicate("e")}
            ORDER BY c.sort_name,e.air_date
            LIMIT 30000;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = reader.GetInt64(0);
            var collection = reader.GetString(1);
            var date = ParseDate(reader, 2);
            var title = reader.GetString(3);
            var confidence = reader.GetString(4);
            var metadataConfidence = reader.GetInt32(6);
            var metadataReason = reader.GetString(7);
            var filename = reader.IsDBNull(8) ? null : reader.GetString(8);
            var usefulTitle = TitleQualityService.IsMeaningful(title, collection, null);
            if (!usefulTitle) genericTitles++;

            // Missing and genuinely ambiguous identity data remain warnings. A usable date parsed
            // from a common non-year-first filename is a review suggestion, not a health fault.
            if (date is null || confidence is "Unknown" or "Ambiguous")
            {
                var reason = ExplainDateConfidence(date, confidence, filename);
                var confidenceDetail = metadataConfidence < 60 && !string.IsNullOrWhiteSpace(metadataReason)
                    ? $" Overall metadata confidence is {metadataConfidence}%: {metadataReason}"
                    : string.Empty;
                issues.Add(new ArchiveHealthIssue(
                    ArchiveHealthArea.Metadata,
                    ArchiveHealthSeverity.Warning,
                    id,
                    null,
                    collection,
                    date,
                    CleanTitle(title, collection),
                    reason + confidenceDetail,
                    date is null
                        ? "Review the filename or set the broadcast date manually before synchronising metadata."
                        : "Review the conflicting date evidence in Metadata Studio."));
                continue;
            }

            if (metadataConfidence < 60)
            {
                issues.Add(new ArchiveHealthIssue(
                    ArchiveHealthArea.Metadata,
                    ArchiveHealthSeverity.Warning,
                    id,
                    null,
                    collection,
                    date,
                    CleanTitle(title, collection),
                    $"Low metadata confidence ({metadataConfidence}%): {metadataReason}",
                    "Review the broadcast identity and filename parsing in Metadata Studio."));
                continue;
            }

            if (confidence == "Probable" || metadataConfidence < 75)
            {
                var reason = confidence == "Probable"
                    ? ExplainDateConfidence(date, confidence, filename)
                    : $"Metadata confidence is {metadataConfidence}%: {metadataReason}";
                issues.Add(new ArchiveHealthIssue(
                    ArchiveHealthArea.Metadata,
                    ArchiveHealthSeverity.Suggestion,
                    id,
                    null,
                    collection,
                    date,
                    CleanTitle(title, collection),
                    reason,
                    "The metadata is usable. Review it only before writing tags back or when curating this broadcast."));
            }

            // Generic titles and absent artwork are tracked in Collection Quality totals. They are
            // intentionally not emitted once per broadcast because neither prevents playback or
            // indicates archive damage, and thousands of such rows hide genuine problems.
        }
        return genericTitles;
    }

    private static void AddFingerprintPreservationIssues(SqliteConnection connection, ICollection<ArchiveHealthIssue> issues, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT e.id,mf.id,c.name,e.air_date,e.title,mf.original_filename,mf.path
            FROM media_files mf
            JOIN episodes e ON e.id=mf.episode_id
            JOIN collections c ON c.id=e.collection_id
            WHERE mf.is_missing=0 AND mf.is_preferred=1
              AND mf.storage_state='AvailableOffline'
              AND {ActiveFolderPredicate("mf")}
              AND (mf.partial_hash IS NULL OR trim(mf.partial_hash)='')
            ORDER BY c.sort_name,e.air_date
            LIMIT 2000;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            issues.Add(new ArchiveHealthIssue(
                ArchiveHealthArea.Preservation,
                ArchiveHealthSeverity.Suggestion,
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                ParseDate(reader, 3),
                CleanTitle(reader.GetString(4), reader.GetString(2)),
                $"The preferred local recording has not yet been fingerprinted: {reader.GetString(5)}",
                "Run a full scan when convenient to create a replacement-safe fingerprint.",
                reader.GetString(6)));
        }
    }

    private static string ExplainDateConfidence(DateOnly? date, string confidence, string? filename)
    {
        var file = string.IsNullOrWhiteSpace(filename) ? "the filename" : filename;
        if (date is null || confidence == "Unknown")
            return $"Broadcast date is missing. No supported date pattern could be confirmed in {file}.";
        if (confidence == "Ambiguous")
            return $"Broadcast date is ambiguous. More than one plausible interpretation or conflicting source was found for {file}.";
        if (confidence == "Probable")
            return $"Broadcast date is probable rather than confirmed. It was parsed from a non-year-first or inferred format in {file}.";
        return $"Broadcast date confidence is {confidence}.";
    }

    private static string DetectSegmentKey(string filename)
    {
        var stem = Path.GetFileNameWithoutExtension(filename).ToLowerInvariant();
        var numbered = Regex.Match(stem, @"(?:^|[\s_.-])(?:hour|hr|part|pt|segment|seg|disc|cd)[\s_.-]*(?<n>\d{1,2})(?:$|[\s_.-])", RegexOptions.IgnoreCase);
        if (numbered.Success) return "segment-" + numbered.Groups["n"].Value;
        var suffix = Regex.Match(stem, @"(?:^|[\s_.-])(?<letter>[abc])(?:$|[\s_.-])", RegexOptions.IgnoreCase);
        if (suffix.Success) return "segment-" + suffix.Groups["letter"].Value.ToLowerInvariant();
        if (Regex.IsMatch(stem, @"\bbest[\s_.-]*of\b", RegexOptions.IgnoreCase)) return "best-of";
        if (Regex.IsMatch(stem, @"\b(replay|rebroadcast)\b", RegexOptions.IgnoreCase)) return "replay";
        return "full";
    }

    private static bool LooksLikeAlternateEncode(IReadOnlyCollection<DuplicateRow> members)
    {
        if (members.Count < 2) return false;
        var sizes = members.Select(x => x.FileSize).Where(x => x > 0).ToArray();
        if (sizes.Length >= 2 && sizes.Max() != sizes.Min()) return true;
        return members.Select(x => Regex.Match(x.OriginalFilename, @"(?<!\d)(?:32|48|56|64|96|112|128|160|192|256|320)\s*k(?:bps)?", RegexOptions.IgnoreCase).Value)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
    }

    private static long DurationBucket(long durationMs)
        => durationMs <= 0 ? 0 : (long)Math.Round(durationMs / 120000.0, MidpointRounding.AwayFromZero);

    private static string NormaliseIdentityToken(string? value)
        => string.IsNullOrWhiteSpace(value) ? "standard" : Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", "-");

    private static DateOnly? ParseDate(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        return DateOnly.TryParse(reader.GetString(ordinal), out var value) ? value : null;
    }

    private static string CleanTitle(string title, string collection)
        => TitleQualityService.IsMeaningful(title, collection, null) ? title : collection;

    private static int CalculateCollectionScore(
        int totalBroadcasts,
        int missingFiles,
        int confirmedMissingBroadcasts,
        int probableMissingBroadcasts,
        int neverScannedFolders)
    {
        var baseCount = Math.Max(totalBroadcasts + confirmedMissingBroadcasts + probableMissingBroadcasts, 1);
        var unavailablePenalty = Math.Min(70.0, missingFiles / (double)Math.Max(totalBroadcasts, 1) * 100.0 * 5.0);
        var confirmedGapPenalty = Math.Min(20.0, confirmedMissingBroadcasts / (double)baseCount * 100.0 * 1.5);
        var probableGapPenalty = Math.Min(7.0, probableMissingBroadcasts / (double)baseCount * 100.0 * 0.4);
        var unscannedPenalty = Math.Min(8.0, neverScannedFolders * 2.0);
        return ClampScore(100.0 - unavailablePenalty - confirmedGapPenalty - probableGapPenalty - unscannedPenalty);
    }

    private static int CalculateMetadataScore(int total, int needsReview, int genericTitles, int missingArtwork)
    {
        if (total <= 0) return 100;
        var dateQuality = 1.0 - Math.Min(1.0, needsReview / (double)total);
        var titleQuality = 1.0 - Math.Min(1.0, genericTitles / (double)total);
        var artworkQuality = 1.0 - Math.Min(1.0, missingArtwork / (double)total);
        return ClampScore(dateQuality * 55.0 + titleQuality * 30.0 + artworkQuality * 15.0);
    }

    private static int CalculateResearchScore(
        int total,
        int withSummaries,
        int withPeople,
        int withTopics,
        int withSources,
        int needsReview,
        int conflicts)
    {
        if (total <= 0) return 100;
        var coverage = (withSummaries + withPeople + withTopics + withSources) / (total * 4.0);
        var reviewPenalty = Math.Min(20.0, needsReview / (double)total * 100.0 * 0.6);
        var conflictPenalty = Math.Min(25.0, conflicts / (double)total * 100.0 * 1.25);
        return ClampScore(coverage * 100.0 - reviewPenalty - conflictPenalty);
    }

    private static int CalculatePreservationScore(int totalBroadcasts, int unfingerprintedFiles, int duplicateCandidates, DateTime? lastCompletedScanAt)
    {
        var baseCount = Math.Max(totalBroadcasts, 1);
        var fingerprintQuality = 1.0 - Math.Min(1.0, unfingerprintedFiles / (double)baseCount);
        var duplicatePenalty = Math.Min(12.0, duplicateCandidates / (double)baseCount * 100.0 * 0.6);
        var scanPenalty = 0.0;
        if (!lastCompletedScanAt.HasValue)
            scanPenalty = 15.0;
        else
        {
            var ageDays = (DateTime.UtcNow - lastCompletedScanAt.Value.ToUniversalTime()).TotalDays;
            if (ageDays > 90) scanPenalty = 12.0;
            else if (ageDays > 30) scanPenalty = 5.0;
        }
        return ClampScore(fingerprintQuality * 88.0 + 12.0 - duplicatePenalty - scanPenalty);
    }

    private static int CalculateOverallScore(int collection, int metadata, int research, int preservation, bool researchAssessed)
    {
        var score = researchAssessed
            ? collection * 0.35 + metadata * 0.25 + research * 0.25 + preservation * 0.15
            : collection * 0.45 + metadata * 0.35 + preservation * 0.20;
        return ClampScore(score);
    }

    private static int ClampScore(double value)
        => Math.Clamp((int)Math.Round(value), 0, 100);

    private sealed record DuplicateRow(
        long EpisodeId,
        long MediaFileId,
        string CollectionName,
        long CollectionId,
        string? AirDate,
        string Title,
        int PartNumber,
        string OriginalFilename,
        string Path,
        string? PartialHash,
        string? FullHash,
        long DurationMs,
        string Edition,
        long FileSize,
        string BroadcastSlot);
}
