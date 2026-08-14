using TheRadioVault.Models;
using TheRadioVault.Core.Services;

namespace TheRadioVault.Services;

public sealed partial class DatabaseService
{
    public IReadOnlyDictionary<string, int> GetCollectionLookup()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name,id FROM collections
            UNION ALL
            SELECT ca.alias,ca.collection_id
              FROM collection_aliases ca
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0).Trim();
            if (key.Length > 0) result.TryAdd(key, reader.GetInt32(1));
        }

        return result;
    }

    public ScannedFileUpsertResult UpsertScannedFile(
        string path,
        long size,
        DateTime modified,
        int collectionId,
        ParsedFilename parsed,
        EpisodeStorageState storageState = EpisodeStorageState.AvailableOffline,
        string? partialHash = null,
        string? fullHash = null,
        long? durationMs = null)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");

        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT id,episode_id FROM media_files WHERE path=$path";
            lookup.Parameters.AddWithValue("$path", path);
            using var reader = lookup.ExecuteReader();
            if (reader.Read())
            {
                var mediaId = reader.GetInt64(0);
                var existingEpisodeId = reader.GetInt64(1);
                reader.Close();

                UpdateExistingMediaFile(
                    connection,
                    transaction,
                    mediaId,
                    size,
                    modified,
                    storageState,
                    partialHash,
                    fullHash,
                    durationMs,
                    now);
                ApplyParsedIdentityToEpisode(
                    connection,
                    transaction,
                    existingEpisodeId,
                    collectionId,
                    parsed,
                    Path.GetFileName(path),
                    now);

                transaction.Commit();
                return new ScannedFileUpsertResult(false, existingEpisodeId);
            }
        }

        var matchedEpisodeId = FindExistingEpisodeForScannedFile(
            connection,
            transaction,
            size,
            collectionId,
            parsed,
            Path.GetFileName(path),
            partialHash,
            fullHash,
            durationMs);
        if (matchedEpisodeId.HasValue)
        {
            using (var oldPreferred = connection.CreateCommand())
            {
                oldPreferred.Transaction = transaction;
                oldPreferred.CommandText = "UPDATE media_files SET is_preferred=0 WHERE episode_id=$id";
                oldPreferred.Parameters.AddWithValue("$id", matchedEpisodeId.Value);
                oldPreferred.ExecuteNonQuery();
            }

            InsertMediaFile(
                connection,
                transaction,
                matchedEpisodeId.Value,
                path,
                size,
                modified,
                storageState,
                partialHash,
                fullHash,
                durationMs,
                now,
                includeMissingColumn: true);
            ApplyParsedIdentityToEpisode(
                connection,
                transaction,
                matchedEpisodeId.Value,
                collectionId,
                parsed,
                Path.GetFileName(path),
                now);

            transaction.Commit();
            return new ScannedFileUpsertResult(false, matchedEpisodeId.Value);
        }

        var broadcastUid = CreateBroadcastUid(
            connection,
            transaction,
            collectionId,
            parsed.AirDate,
            parsed.PartNumber,
            parsed.BroadcastSlot);
        long episodeId;
        using (var insertEpisode = connection.CreateCommand())
        {
            insertEpisode.Transaction = transaction;
            insertEpisode.CommandText = """
                INSERT INTO episodes(
                    collection_id,air_date,date_confidence,title,broadcast_uid,
                    part_number,total_parts,broadcast_slot,edition,
                    metadata_confidence,metadata_confidence_reason,date_added,updated_at)
                VALUES(
                    $collection,$date,$dateConfidence,$title,$uid,
                    $part,$totalParts,$slot,$edition,
                    $metadataConfidence,$metadataReason,$now,$now);
                SELECT last_insert_rowid();
                """;
            insertEpisode.Parameters.AddWithValue("$collection", collectionId);
            insertEpisode.Parameters.AddWithValue("$date", parsed.AirDate?.ToString("yyyy-MM-dd") ?? (object)DBNull.Value);
            insertEpisode.Parameters.AddWithValue("$dateConfidence", parsed.DateConfidence);
            insertEpisode.Parameters.AddWithValue(
                "$title",
                parsed.HeadlineConfidence == "High" ? parsed.HeadlineCandidate ?? "" : "");
            insertEpisode.Parameters.AddWithValue("$uid", broadcastUid);
            insertEpisode.Parameters.AddWithValue("$part", parsed.PartNumber);
            insertEpisode.Parameters.AddWithValue("$totalParts", parsed.TotalParts ?? (object)DBNull.Value);
            insertEpisode.Parameters.AddWithValue("$slot", parsed.BroadcastSlot ?? (object)DBNull.Value);
            insertEpisode.Parameters.AddWithValue("$edition", parsed.Edition ?? (object)DBNull.Value);
            insertEpisode.Parameters.AddWithValue("$metadataConfidence", parsed.MetadataConfidence);
            insertEpisode.Parameters.AddWithValue("$metadataReason", parsed.MetadataConfidenceReasoning);
            insertEpisode.Parameters.AddWithValue("$now", now);
            episodeId = Convert.ToInt64(insertEpisode.ExecuteScalar());
        }

        InsertMediaFile(
            connection,
            transaction,
            episodeId,
            path,
            size,
            modified,
            storageState,
            partialHash,
            fullHash,
            durationMs,
            now,
            includeMissingColumn: false);

        using (var playbackState = connection.CreateCommand())
        {
            playbackState.Transaction = transaction;
            playbackState.CommandText = "INSERT INTO playback_state(episode_id) VALUES($id)";
            playbackState.Parameters.AddWithValue("$id", episodeId);
            playbackState.ExecuteNonQuery();
        }

        transaction.Commit();
        return new ScannedFileUpsertResult(true, episodeId);
    }

    private static void UpdateExistingMediaFile(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        long mediaId,
        long size,
        DateTime modified,
        EpisodeStorageState storageState,
        string? partialHash,
        string? fullHash,
        long? durationMs,
        string now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE media_files
               SET file_size=$size,
                   modified_time=$modified,
                   is_missing=0,
                   is_preferred=1,
                   storage_state=$storage,
                   partial_hash=COALESCE($partial,partial_hash),
                   full_hash=COALESCE($full,full_hash),
                   duration_ms=COALESCE($duration,duration_ms),
                   fingerprinted_at=CASE WHEN $partial IS NOT NULL THEN $now ELSE fingerprinted_at END,
                   full_hashed_at=CASE WHEN $full IS NOT NULL THEN $now ELSE full_hashed_at END,
                   inspection_error=CASE WHEN $partial IS NOT NULL THEN '' ELSE inspection_error END,
                   inspection_error_at=CASE WHEN $partial IS NOT NULL THEN NULL ELSE inspection_error_at END,
                   last_seen_at=$now
             WHERE id=$id
            """;
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$modified", modified.ToString("O"));
        command.Parameters.AddWithValue("$storage", storageState.ToString());
        command.Parameters.AddWithValue("$partial", partialHash ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$full", fullHash ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$duration", durationMs is > 0 ? durationMs.Value : (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", mediaId);
        command.ExecuteNonQuery();
    }

    private static long? FindExistingEpisodeForScannedFile(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        long size,
        int collectionId,
        ParsedFilename parsed,
        string originalFilename,
        string? partialHash,
        string? fullHash,
        long? durationMs)
    {
        // A full-file digest is the only path-independent content identity that
        // is safe on its own. If it is ambiguous across multiple episodes, do
        // not guess which database row owns the returning file.
        if (!string.IsNullOrWhiteSpace(fullHash))
        {
            var fullMatch = FindUniqueFingerprintEpisode(
                connection,
                transaction,
                "full_hash=$hash COLLATE NOCASE",
                collectionId,
                parsed,
                command => command.Parameters.AddWithValue("$hash", fullHash.Trim()));
            if (fullMatch.HasValue) return fullMatch;
        }

        // The partial digest samples only the beginning and end of a file. The
        // old scanner treated partial hash + byte length as unique, which could
        // attach different recordings with identical outer blocks. Duration is
        // now required as the third part of this strong (but not exact) key.
        if (!string.IsNullOrWhiteSpace(partialHash) && durationMs is > 0)
        {
            var strongMatch = FindUniqueFingerprintEpisode(
                connection,
                transaction,
                "file_size=$size AND partial_hash=$hash COLLATE NOCASE AND ABS(duration_ms-$duration)<=1000",
                collectionId,
                parsed,
                command =>
                {
                    command.Parameters.AddWithValue("$size", size);
                    command.Parameters.AddWithValue("$hash", partialHash.Trim());
                    command.Parameters.AddWithValue("$duration", durationMs.Value);
                });
            if (strongMatch.HasValue) return strongMatch;
        }

        if (!parsed.AirDate.HasValue || parsed.DateConfidence != "High") return null;

        using var dateMatch = connection.CreateCommand();
        dateMatch.Transaction = transaction;
        dateMatch.CommandText = """
            SELECT e.id,
                   MAX(CASE
                       WHEN lower(mf.original_filename)=lower($filename) THEN 2
                       WHEN mf.file_size=$size THEN 1
                       ELSE 0
                   END) AS match_score
              FROM episodes e
              JOIN media_files mf ON mf.episode_id=e.id AND mf.is_preferred=1 AND mf.is_missing=1
             WHERE e.collection_id=$collection
               AND e.air_date=$date
               AND COALESCE(e.part_number,1)=$part
               AND (
                    COALESCE(e.broadcast_slot,'')=COALESCE($slot,'')
                    OR (
                        lower(COALESCE($slot,''))='opieradio edition'
                        AND trim(COALESCE(e.broadcast_slot,''))=''
                        AND lower(trim(COALESCE(e.edition,'')))='opieradio edition'
                    )
               )
             GROUP BY e.id
             ORDER BY match_score DESC,e.id
             LIMIT 2
            """;
        dateMatch.Parameters.AddWithValue("$collection", collectionId);
        dateMatch.Parameters.AddWithValue("$date", parsed.AirDate.Value.ToString("yyyy-MM-dd"));
        dateMatch.Parameters.AddWithValue("$part", parsed.PartNumber);
        dateMatch.Parameters.AddWithValue("$slot", parsed.BroadcastSlot ?? (object)DBNull.Value);
        dateMatch.Parameters.AddWithValue("$filename", originalFilename);
        dateMatch.Parameters.AddWithValue("$size", size);
        var matches = new List<(long Id, int Score)>();
        using var dateReader = dateMatch.ExecuteReader();
        while (dateReader.Read()) matches.Add((dateReader.GetInt64(0), dateReader.GetInt32(1)));
        if (matches.Count == 1) return matches[0].Id;
        if (matches.Count > 1 && matches[0].Score > matches[1].Score) return matches[0].Id;
        return null;
    }

    private static long? FindUniqueFingerprintEpisode(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string predicate,
        int collectionId,
        ParsedFilename parsed,
        Action<Microsoft.Data.Sqlite.SqliteCommand> bind)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT DISTINCT mf.episode_id
              FROM media_files mf
              JOIN episodes e ON e.id=mf.episode_id
             WHERE {predicate}
               AND e.collection_id=$collection
               AND COALESCE(e.part_number,1)=$part
               AND ($date IS NULL OR e.air_date=$date)
               AND (
                    trim(COALESCE($slot,''))=''
                    OR lower(trim(COALESCE(e.broadcast_slot,'')))=lower(trim($slot))
                    OR (
                        lower(trim($slot))='opieradio edition'
                        AND trim(COALESCE(e.broadcast_slot,''))=''
                        AND lower(trim(COALESCE(e.edition,'')))='opieradio edition'
                    )
               )
             ORDER BY mf.episode_id
             LIMIT 2
            """;
        command.Parameters.AddWithValue("$collection", collectionId);
        command.Parameters.AddWithValue("$part", Math.Max(1, parsed.PartNumber));
        command.Parameters.AddWithValue("$date", parsed.AirDate.HasValue && parsed.DateConfidence == "High"
            ? parsed.AirDate.Value.ToString("yyyy-MM-dd")
            : (object)DBNull.Value);
        command.Parameters.AddWithValue("$slot", parsed.BroadcastSlot ?? string.Empty);
        bind(command);
        var ids = new List<long>(2);
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids.Count == 1 ? ids[0] : null;
    }

    private static void InsertMediaFile(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        long episodeId,
        string path,
        long size,
        DateTime modified,
        EpisodeStorageState storageState,
        string? partialHash,
        string? fullHash,
        long? durationMs,
        string now,
        bool includeMissingColumn)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = includeMissingColumn
            ? """
                INSERT INTO media_files(
                    episode_id,path,original_filename,file_size,modified_time,
                    storage_state,is_preferred,is_missing,partial_hash,full_hash,duration_ms,
                    fingerprinted_at,full_hashed_at,last_seen_at)
                VALUES(
                    $episode,$path,$name,$size,$modified,
                    $storage,1,0,$partial,$full,COALESCE($duration,0),
                    CASE WHEN $partial IS NOT NULL THEN $now ELSE NULL END,
                    CASE WHEN $full IS NOT NULL THEN $now ELSE NULL END,$now)
                """
            : """
                INSERT INTO media_files(
                    episode_id,path,original_filename,file_size,modified_time,
                    storage_state,is_preferred,partial_hash,full_hash,duration_ms,
                    fingerprinted_at,full_hashed_at,last_seen_at)
                VALUES(
                    $episode,$path,$name,$size,$modified,
                    $storage,1,$partial,$full,COALESCE($duration,0),
                    CASE WHEN $partial IS NOT NULL THEN $now ELSE NULL END,
                    CASE WHEN $full IS NOT NULL THEN $now ELSE NULL END,$now)
                """;
        command.Parameters.AddWithValue("$episode", episodeId);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$name", Path.GetFileName(path));
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$modified", modified.ToString("O"));
        command.Parameters.AddWithValue("$storage", storageState.ToString());
        command.Parameters.AddWithValue("$partial", partialHash ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$full", fullHash ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$duration", durationMs is > 0 ? durationMs.Value : (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private static string CreateBroadcastUid(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        int collectionId,
        DateTime? date,
        int part,
        string? slot,
        long? excludeEpisodeId = null)
    {
        using var collectionName = connection.CreateCommand();
        collectionName.Transaction = transaction;
        collectionName.CommandText = "SELECT name FROM collections WHERE id=$id";
        collectionName.Parameters.AddWithValue("$id", collectionId);
        var name = Convert.ToString(collectionName.ExecuteScalar()) ?? "Broadcast";
        var baseId = BroadcastIdentityService.CreateStableId(
            name,
            date.HasValue ? DateOnly.FromDateTime(date.Value) : null,
            part,
            slot);
        var uid = baseId;
        var suffix = 2;

        while (true)
        {
            using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = excludeEpisodeId.HasValue
                ? "SELECT COUNT(*) FROM episodes WHERE broadcast_uid=$uid AND id<>$exclude"
                : "SELECT COUNT(*) FROM episodes WHERE broadcast_uid=$uid";
            query.Parameters.AddWithValue("$uid", uid);
            if (excludeEpisodeId.HasValue) query.Parameters.AddWithValue("$exclude", excludeEpisodeId.Value);
            if (Convert.ToInt32(query.ExecuteScalar()) == 0) return uid;
            uid = $"{baseId}-{suffix++}";
        }
    }

    private static void ApplyParsedIdentityToEpisode(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        long episodeId,
        int collectionId,
        ParsedFilename parsed,
        string sourceFilename,
        string now)
    {
        var currentHeadline = "";
        var currentUid = "";
        DateTime? currentDate = null;
        var currentDateConfidence = "Unknown";
        var userModified = false;
        var currentPart = 1;
        int? currentTotalParts = null;
        string? currentSlot = null;
        var currentEdition = "";
        var currentCollectionId = collectionId;

        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT COALESCE(title,''),COALESCE(user_modified,0),COALESCE(broadcast_uid,''),
                       air_date,COALESCE(date_confidence,'Unknown'),COALESCE(part_number,1),
                       total_parts,broadcast_slot,COALESCE(edition,''),collection_id
                  FROM episodes
                 WHERE id=$id
                """;
            existing.Parameters.AddWithValue("$id", episodeId);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                currentHeadline = reader.GetString(0);
                userModified = reader.GetInt64(1) != 0;
                currentUid = reader.GetString(2);
                currentDate = reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3));
                currentDateConfidence = reader.GetString(4);
                currentPart = reader.GetInt32(5);
                currentTotalParts = reader.IsDBNull(6) ? null : reader.GetInt32(6);
                currentSlot = reader.IsDBNull(7) ? null : reader.GetString(7);
                currentEdition = reader.GetString(8);
                currentCollectionId = reader.GetInt32(9);
            }
        }

        var manualDateProtected = currentDate.HasValue &&
            string.Equals(currentDateConfidence, "Manual", StringComparison.OrdinalIgnoreCase);
        var parsedDateIsAuthoritative = !manualDateProtected && parsed.AirDate.HasValue && parsed.DateConfidence == "High";
        var replaceDate = parsedDateIsAuthoritative &&
                          (!currentDate.HasValue || currentDate.Value.Date != parsed.AirDate!.Value.Date);
        var upgradeDateConfidence = parsedDateIsAuthoritative && currentDate.HasValue &&
                                    currentDate.Value.Date == parsed.AirDate!.Value.Date &&
                                    !string.Equals(currentDateConfidence, "High", StringComparison.OrdinalIgnoreCase);
        var replaceCollection = currentCollectionId != collectionId;
        var effectiveDate = replaceDate ? parsed.AirDate : currentDate;
        var effectivePart = parsed.PartNumber > 0 ? parsed.PartNumber : currentPart;
        var effectiveTotalParts = parsed.TotalParts ?? currentTotalParts;
        var effectiveSlot = string.IsNullOrWhiteSpace(parsed.BroadcastSlot) ? currentSlot : parsed.BroadcastSlot;
        var migrateOpieRadioEdition = string.Equals(effectiveSlot, "OpieRadio Edition", StringComparison.OrdinalIgnoreCase) &&
                                      string.Equals(currentEdition, "OpieRadio Edition", StringComparison.OrdinalIgnoreCase);

        var redundantDateHeadline = FilenameParserService.IsRedundantDateHeadline(currentHeadline, effectiveDate);
        var parserStructuralHeadline = IsParserStructuralHeadline(currentHeadline, effectiveSlot);
        var clearAutomaticHeadline = redundantDateHeadline || parserStructuralHeadline ||
            (!userModified &&
             string.IsNullOrWhiteSpace(parsed.HeadlineCandidate) &&
             (FilenameParserService.IsStructuralOnlyText(currentHeadline, parsed.CollectionName) ||
              !TitleQualityService.IsMeaningful(currentHeadline, parsed.CollectionName, sourceFilename)));
        var applyCatalogueHeadline = !userModified &&
            KnownShowCatalog.SupportsUndatedCatalogueItems(parsed.CollectionName) &&
            parsed.HeadlineConfidence.Equals("High", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(parsed.HeadlineCandidate);

        var shouldRegenerateUid = effectiveDate.HasValue &&
            (replaceDate ||
             replaceCollection ||
             currentUid.Contains("UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
             currentPart != effectivePart ||
             !string.Equals(currentSlot ?? "", effectiveSlot ?? "", StringComparison.OrdinalIgnoreCase));
        var newUid = shouldRegenerateUid
            ? CreateBroadcastUid(
                connection,
                transaction,
                collectionId,
                effectiveDate,
                effectivePart,
                effectiveSlot,
                episodeId)
            : currentUid;

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE episodes SET
              hidden=0,
              collection_id=$collection,
              air_date=CASE WHEN $replaceDate=1 THEN $date WHEN air_date IS NULL AND $date IS NOT NULL THEN $date ELSE air_date END,
              date_confidence=CASE WHEN $replaceDate=1 OR $upgradeConfidence=1 THEN $confidence WHEN air_date IS NULL AND $date IS NOT NULL THEN $confidence ELSE date_confidence END,
              title=CASE WHEN $applyParsedTitle=1 THEN $parsedTitle WHEN $clearTitle=1 THEN '' ELSE title END,
              broadcast_uid=$uid,
              part_number=$part,
              total_parts=$totalParts,
              broadcast_slot=$slot,
              edition=CASE WHEN $clearEdition=1 THEN '' WHEN $edition IS NOT NULL AND trim($edition)<>'' THEN $edition ELSE edition END,
              metadata_confidence=$metadataConfidence,
              metadata_confidence_reason=$metadataReason,
              updated_at=$now
            WHERE id=$id
            """;
        update.Parameters.AddWithValue("$collection", collectionId);
        update.Parameters.AddWithValue("$replaceDate", replaceDate ? 1 : 0);
        update.Parameters.AddWithValue("$upgradeConfidence", upgradeDateConfidence ? 1 : 0);
        update.Parameters.AddWithValue("$date", effectiveDate?.ToString("yyyy-MM-dd") ?? (object)DBNull.Value);
        update.Parameters.AddWithValue("$confidence", replaceDate || upgradeDateConfidence ? parsed.DateConfidence : currentDateConfidence);
        update.Parameters.AddWithValue("$applyParsedTitle", applyCatalogueHeadline ? 1 : 0);
        update.Parameters.AddWithValue("$parsedTitle", applyCatalogueHeadline ? parsed.HeadlineCandidate!.Trim() : string.Empty);
        update.Parameters.AddWithValue("$clearTitle", clearAutomaticHeadline ? 1 : 0);
        update.Parameters.AddWithValue("$uid", newUid);
        update.Parameters.AddWithValue("$part", effectivePart);
        update.Parameters.AddWithValue("$totalParts", effectiveTotalParts ?? (object)DBNull.Value);
        update.Parameters.AddWithValue("$slot", effectiveSlot ?? (object)DBNull.Value);
        update.Parameters.AddWithValue("$clearEdition", migrateOpieRadioEdition ? 1 : 0);
        update.Parameters.AddWithValue("$edition", parsed.Edition ?? (object)DBNull.Value);
        update.Parameters.AddWithValue("$metadataConfidence", parsed.MetadataConfidence);
        update.Parameters.AddWithValue("$metadataReason", parsed.MetadataConfidenceReasoning);
        update.Parameters.AddWithValue("$now", now);
        update.Parameters.AddWithValue("$id", episodeId);
        update.ExecuteNonQuery();

        if (parserStructuralHeadline)
        {
            using var reviewCleanup = connection.CreateCommand();
            reviewCleanup.Transaction = transaction;
            reviewCleanup.CommandText = "DELETE FROM headline_reviews WHERE episode_id=$id AND lower(trim(COALESCE(reviewed_headline,'')))=lower(trim($headline))";
            reviewCleanup.Parameters.AddWithValue("$id", episodeId);
            reviewCleanup.Parameters.AddWithValue("$headline", currentHeadline);
            reviewCleanup.ExecuteNonQuery();
        }
    }

    private static bool IsParserStructuralHeadline(string? headline, string? slot)
    {
        if (string.IsNullOrWhiteSpace(headline)) return false;
        var value = headline.Trim();
        if (!string.IsNullOrWhiteSpace(slot) && string.Equals(value, slot, StringComparison.OrdinalIgnoreCase)) return true;
        return value.Equals("Midday", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Midday show", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("OpieRadio", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("OpieRadio Edition", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, LibraryScanFileSnapshot> GetFolderFileScanSnapshots(
        string folderPath,
        bool recursive)
    {
        var result = new Dictionary<string, LibraryScanFileSnapshot>(StringComparer.OrdinalIgnoreCase);
        var folderPrefix = BuildFolderPrefix(folderPath);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = recursive
            ? """
                SELECT mf.path,mf.episode_id,mf.file_size,mf.modified_time,e.collection_id,
                       COALESCE(mf.storage_state,'AvailableOffline'),COALESCE(mf.is_missing,0)
                  FROM media_files mf
                  JOIN episodes e ON e.id=mf.episode_id
                 WHERE mf.path LIKE $prefix ESCAPE '\'
                """
            : """
                SELECT mf.path,mf.episode_id,mf.file_size,mf.modified_time,e.collection_id,
                       COALESCE(mf.storage_state,'AvailableOffline'),COALESCE(mf.is_missing,0)
                  FROM media_files mf
                  JOIN episodes e ON e.id=mf.episode_id
                 WHERE mf.path LIKE $prefix ESCAPE '\'
                   AND instr(replace(substr(mf.path,$relativeStart),'\','/'),'/')=0
                """;
        command.Parameters.AddWithValue("$prefix", BuildEscapedLikePrefix(folderPrefix));
        if (!recursive) command.Parameters.AddWithValue("$relativeStart", folderPrefix.Length + 1);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var modified = DateTime.TryParse(
                reader.GetString(3),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsedModified)
                ? parsedModified.ToUniversalTime()
                : DateTime.MinValue;
            var storageText = reader.GetString(5);
            var storage = Enum.TryParse<EpisodeStorageState>(storageText, true, out var parsedStorage)
                ? parsedStorage
                : EpisodeStorageState.AvailableOffline;
            result[reader.GetString(0)] = new LibraryScanFileSnapshot
            {
                EpisodeId = reader.GetInt64(1),
                FileSize = reader.GetInt64(2),
                ModifiedUtc = modified,
                CollectionId = reader.GetInt32(4),
                StorageState = storage,
                WasMissing = reader.GetInt64(6) != 0
            };
        }

        return result;
    }

    /// <summary>
    /// Records that a file still exists when a later parsing or metadata step
    /// fails. This prevents a readable archive root from falsely turning an
    /// existing recording into a missing-file entry.
    /// </summary>
    public void TouchScannedFile(string path, EpisodeStorageState storageState)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE media_files
               SET is_missing=0,storage_state=$storage,last_seen_at=$seen
             WHERE path=$path
            """;
        command.Parameters.AddWithValue("$storage", storageState.ToString());
        command.Parameters.AddWithValue("$seen", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Marks only files that were not observed during a successfully completed
    /// folder scan as missing. A cancelled or inaccessible scan never calls
    /// this method, so existing availability state is preserved.
    /// </summary>
    public void CompleteFolderScan(
        int folderId,
        string folderPath,
        bool recursive,
        DateTime scanStartedUtc)
    {
        var folderPrefix = BuildFolderPrefix(folderPath);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = recursive
                ? """
                    UPDATE media_files
                       SET is_missing=1,storage_state='Missing'
                     WHERE path LIKE $prefix ESCAPE '\'
                       AND last_seen_at<$cutoff
                    """
                : """
                    UPDATE media_files
                       SET is_missing=1,storage_state='Missing'
                     WHERE path LIKE $prefix ESCAPE '\'
                       AND instr(replace(substr(path,$relativeStart),'\','/'),'/')=0
                       AND last_seen_at<$cutoff
                    """;
            command.Parameters.AddWithValue("$prefix", BuildEscapedLikePrefix(folderPrefix));
            if (!recursive) command.Parameters.AddWithValue("$relativeStart", folderPrefix.Length + 1);
            command.Parameters.AddWithValue("$cutoff", scanStartedUtc.ToString("O"));
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE library_folders SET last_scan_at=$completed WHERE id=$id";
            command.Parameters.AddWithValue("$completed", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", folderId);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static string BuildFolderPrefix(string folderPath)
        => folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
           + Path.DirectorySeparatorChar;

    private static string BuildEscapedLikePrefix(string folderPrefix)
        => folderPrefix.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";
}
