using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheRadioVault.Core.Playback;
using TheRadioVault.Models;
using TheRadioVault.Services.Models;
using TheRadioVault.Services.Services;

namespace TheRadioVault.Services;

public sealed partial class DatabaseService
{
    private readonly TheRadioVault.Data.Database.SqliteDatabase _database;
    private readonly CanonicalLibraryQueryService _canonicalLibrary;
    private readonly CanonicalScanPromotionService _canonicalScanPromotion;
    private readonly object _initializationGate = new();
    private readonly object _researchTriageGate = new();
    private bool _initialized;

    public DatabaseService()
        : this(new TheRadioVault.Data.Database.SqliteDatabase(AppPaths.DatabasePath))
    {
    }

    public DatabaseService(TheRadioVault.Data.Database.SqliteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _canonicalLibrary = new CanonicalLibraryQueryService(_database);
        _canonicalScanPromotion = new CanonicalScanPromotionService(_database);
    }

    private SqliteConnection OpenConnection() => _database.OpenConnection();

    public TheRadioVault.Data.Database.SqliteDatabase PlatformDatabase => _database;

    public void Initialize()
    {
        lock (_initializationGate)
        {
            if (_initialized) return;
            _database.Initialize();
            MigrateLegacyResearchLedger();
            _initialized = true;
        }
    }

    public IReadOnlyList<Collection> GetCollections()
    {
        var result = new List<Collection>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT id,name FROM collections ORDER BY sort_name";
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new Collection { Id = reader.GetInt32(0), Name = reader.GetString(1) });
        return result;
    }

    public IReadOnlyList<CollectionSummary> GetCollectionSummaries()
    {
        var canonical = _canonicalLibrary.GetSummary();
        if (canonical.IsCutoverReady)
        {
            return _canonicalLibrary.GetCollectionSummaries()
                .Select(x => new CollectionSummary
                {
                    Id = x.CollectionId,
                    Name = x.CollectionName,
                    EpisodeCount = x.BroadcastCount
                })
                .ToArray();
        }

        var result = new List<CollectionSummary>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.id,c.name,COUNT(e.id) FROM collections c LEFT JOIN episodes e ON e.collection_id=c.id AND COALESCE(e.hidden,0)=0 GROUP BY c.id,c.name ORDER BY c.sort_name";
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new CollectionSummary { Id = reader.GetInt32(0), Name = reader.GetString(1), EpisodeCount = reader.GetInt32(2) });
        return result;
    }

    public CanonicalLibrarySummary GetCanonicalLibrarySummary()
        => _canonicalLibrary.GetSummary();

    public CanonicalPlaybackPlan? GetCanonicalPlaybackPlan(string canonicalKey, string? recordingKey = null)
        => _canonicalLibrary.GetPlaybackPlan(canonicalKey, recordingKey);

    public IReadOnlyList<CanonicalRecordingOption> GetCanonicalRecordingOptions(string canonicalKey)
        => _canonicalLibrary.GetRecordingOptions(canonicalKey);

    public CanonicalDownloadManifest? GetCanonicalDownloadManifest(string canonicalKey, string? recordingKey = null)
        => _canonicalLibrary.GetDownloadManifest(canonicalKey, recordingKey);

    public CanonicalScanPromotionResult PromoteScannedFilesIntoCanonicalLibrary()
        => _canonicalScanPromotion.PromoteUnmappedEpisodes();

    public CanonicalTimelineLocation? ResolveCanonicalTimelineLocation(long episodeId, string? recordingKey = null)
        => _canonicalLibrary.ResolveTimelineLocation(episodeId, recordingKey);

    public CanonicalEpisodeResolution? ResolveCanonicalEpisode(long episodeId)
        => _canonicalLibrary.ResolveEpisode(episodeId);

    public IReadOnlyList<long> ExpandCanonicalStateEpisodeIds(long episodeId)
        => _canonicalLibrary.ExpandStateEpisodeIds(episodeId);

    public long? ResolvePlaybackRecoveryEpisodeId(long episodeId, string? canonicalKey, string? broadcastUid)
    {
        if (episodeId > 0)
        {
            using var exactConnection = OpenConnection();
            using var exact = exactConnection.CreateCommand();
            exact.CommandText = "SELECT COUNT(*) FROM episodes WHERE id=$id";
            exact.Parameters.AddWithValue("$id", episodeId);
            if (Convert.ToInt32(exact.ExecuteScalar()) > 0)
                return ResolveCanonicalEpisode(episodeId)?.RepresentativeEpisodeId ?? episodeId;
        }

        using var connection = OpenConnection();
        if (!string.IsNullOrWhiteSpace(canonicalKey))
        {
            using (var adopted = connection.CreateCommand())
            {
                adopted.CommandText = """
                    SELECT survivor_episode_id
                      FROM episode_canonical_map
                     WHERE canonical_key=$key
                     ORDER BY is_survivor DESC,episode_id
                     LIMIT 1
                    """;
                adopted.Parameters.AddWithValue("$key", canonicalKey.Trim());
                var value = adopted.ExecuteScalar();
                if (value is not null && value is not DBNull) return Convert.ToInt64(value);
            }

            using (var held = connection.CreateCommand())
            {
                held.CommandText = """
                    WITH latest AS (
                        SELECT truth_run_id AS run_id
                          FROM library_truth_adoption_runs
                         WHERE status='completed' AND commit_verified=1
                           AND foreign_key_violations=0 AND lower(integrity_check)='ok'
                         ORDER BY id DESC
                         LIMIT 1
                    )
                    SELECT f.current_episode_id
                      FROM library_truth_files f
                      JOIN latest l ON l.run_id=f.run_id
                      JOIN media_files mf ON mf.id=f.media_file_id
                     WHERE f.canonical_broadcast_key=$key
                     ORDER BY CASE WHEN COALESCE(mf.is_missing,0)=0 THEN 0 ELSE 1 END,
                              CASE WHEN f.proposed_part=1 THEN 0 ELSE 1 END,
                              f.media_file_id
                     LIMIT 1
                    """;
                held.Parameters.AddWithValue("$key", canonicalKey.Trim());
                var value = held.ExecuteScalar();
                if (value is not null && value is not DBNull) return Convert.ToInt64(value);
            }
        }

        if (!string.IsNullOrWhiteSpace(broadcastUid))
        {
            using var uid = connection.CreateCommand();
            uid.CommandText = """
                SELECT id
                  FROM episodes
                 WHERE broadcast_uid=$uid
                 ORDER BY hidden,id
                 LIMIT 1
                """;
            uid.Parameters.AddWithValue("$uid", broadcastUid.Trim());
            var value = uid.ExecuteScalar();
            if (value is not null && value is not DBNull) return Convert.ToInt64(value);
        }

        return null;
    }

    public int ResolveCollectionId(string? detectedName, int? assignedId)
    {
        if (assignedId.HasValue) return assignedId.Value;
        using var connection = OpenConnection();
        if (!string.IsNullOrWhiteSpace(detectedName))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM collections WHERE lower(name)=lower($name) LIMIT 1";
            command.Parameters.AddWithValue("$name", detectedName);
            var value = command.ExecuteScalar();
            if (value is not null) return Convert.ToInt32(value);
        }
        using var fallback = connection.CreateCommand(); fallback.CommandText = "SELECT id FROM collections WHERE name='Unsorted'";
        return Convert.ToInt32(fallback.ExecuteScalar());
    }

    public IReadOnlyList<LibraryFolder> GetLibraryFolders()
    {
        var result = new List<LibraryFolder>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT lf.id,lf.path,lf.assigned_collection_id,lf.enabled,lf.recursive,lf.last_scan_at,
                   COALESCE(c.name,'Auto detect')
              FROM library_folders lf
              LEFT JOIN collections c ON c.id=lf.assigned_collection_id
             WHERE lf.enabled=1
             ORDER BY lf.path
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new LibraryFolder
            {
                Id = reader.GetInt32(0),
                Path = reader.GetString(1),
                AssignedCollectionId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                Enabled = reader.GetInt32(3) == 1,
                Recursive = reader.GetInt32(4) == 1,
                LastScanAt = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5)),
                CollectionName = reader.GetString(6)
            });
        }
        return result;
    }

    public void AddLibraryFolder(string path, int? assignedCollectionId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO library_folders(path,assigned_collection_id) VALUES($path,$collection) ON CONFLICT(path) DO UPDATE SET assigned_collection_id=excluded.assigned_collection_id, enabled=1";
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$collection", assignedCollectionId.HasValue ? assignedCollectionId.Value : DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void RemoveLibraryFolder(string path, bool hideExclusiveBroadcasts)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand()) { cmd.Transaction=transaction; cmd.CommandText="DELETE FROM library_folders WHERE path=$path"; cmd.Parameters.AddWithValue("$path",path); cmd.ExecuteNonQuery(); }
        var prefix=path.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
        using (var cmd=connection.CreateCommand()) { cmd.Transaction=transaction; cmd.CommandText="UPDATE media_files SET is_missing=1,storage_state='Missing' WHERE path=$root OR path LIKE $prefix"; cmd.Parameters.AddWithValue("$root",path); cmd.Parameters.AddWithValue("$prefix",prefix+"%"); cmd.ExecuteNonQuery(); }
        if(hideExclusiveBroadcasts){ using var cmd=connection.CreateCommand(); cmd.Transaction=transaction; cmd.CommandText="UPDATE episodes SET hidden=1 WHERE NOT EXISTS(SELECT 1 FROM media_files mf WHERE mf.episode_id=episodes.id AND mf.is_missing=0)"; cmd.ExecuteNonQuery(); }
        transaction.Commit();
    }

    public IReadOnlyList<EpisodeListItem> GetEpisodes()
    {
        var summary = _canonicalLibrary.GetSummary();
        if (!summary.IsCutoverReady) return GetLegacyEpisodes();

        var canonical = _canonicalLibrary.GetBroadcasts();
        if (canonical.Count != summary.Broadcasts)
        {
            DiagnosticLog.Write(
                "Canonical library",
                $"Canonical projection returned {canonical.Count:N0}/{summary.Broadcasts:N0} broadcasts. The canonical result is retained rather than exposing duplicate legacy file rows.");
        }

        return canonical.Select(MapCanonicalEpisode).ToArray();
    }

    public EpisodeListItem? GetEpisode(long episodeId)
    {
        if (episodeId <= 0) return null;
        var summary = _canonicalLibrary.GetSummary();
        if (!summary.IsCutoverReady) return GetLegacyEpisodes(episodeId).FirstOrDefault();

        var canonical = _canonicalLibrary.GetBroadcast(episodeId);
        return canonical is null ? null : MapCanonicalEpisode(canonical);
    }

    private static EpisodeListItem MapCanonicalEpisode(CanonicalLibraryEntry item)
    {
        var duration = Math.Max(0, item.DurationMs);
        var position = duration > 0
            ? Math.Clamp(item.PositionMs, 0, duration)
            : Math.Max(0, item.PositionMs);
        return new EpisodeListItem
        {
            Id = item.RepresentativeEpisodeId,
            CanonicalKey = item.CanonicalKey,
            BroadcastUid = item.BroadcastUid,
            PartNumber = 1,
            TotalParts = item.SegmentCount > 1 ? item.SegmentCount : null,
            RecordingCount = Math.Max(1, item.RecordingCount),
            SegmentCount = Math.Max(1, item.SegmentCount),
            PhysicalFileCount = Math.Max(1, item.PhysicalFileCount),
            NeedsAttention = item.NeedsAttention,
            AttentionState = item.AttentionState,
            AttentionReason = item.AttentionReason,
            CollectionId = item.CollectionId,
            CollectionName = item.CollectionName,
            AirDate = item.AirDate?.ToDateTime(TimeOnly.MinValue),
            BroadcastSlot = item.BroadcastSlot,
            DisplayTitle = item.Headline,
            Summary = item.Description,
            OriginalFilename = item.OriginalFilename,
            Path = item.Path,
            StorageState = Enum.TryParse<EpisodeStorageState>(item.StorageState, out var storageState)
                ? storageState
                : EpisodeStorageState.Missing,
            Favourite = item.Favourite,
            Status = item.ListeningStatus,
            PositionMs = position,
            DurationMs = duration,
            LastPlayedAt = item.LastPlayedAt?.LocalDateTime,
            DateAdded = item.DateAdded == DateTimeOffset.MinValue ? DateTime.MinValue : item.DateAdded.LocalDateTime,
            Guests = item.Guests,
            Tags = item.Tags,
            ArtworkPath = item.ArtworkPath,
            Edition = item.Edition,
            MetadataConfidence = item.MetadataConfidence,
            MetadataConfidenceReason = item.MetadataConfidenceReason
        };
    }

    private IReadOnlyList<EpisodeListItem> GetLegacyEpisodes(long? episodeId = null)
    {
        var result = new List<EpisodeListItem>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
        WITH guest_names AS (
            SELECT eg.episode_id,group_concat(g.name, ', ') AS names
              FROM episode_guests eg
              JOIN guests g ON g.id=eg.guest_id
             GROUP BY eg.episode_id
        ),
        tag_names AS (
            SELECT et.episode_id,group_concat(t.name, ', ') AS names
              FROM episode_tags et
              JOIN tags t ON t.id=et.tag_id
             GROUP BY et.episode_id
        )
        SELECT e.id,e.broadcast_uid,COALESCE(e.part_number,1),e.total_parts,e.collection_id,c.name,e.air_date,e.title,mf.original_filename,mf.path,
               CASE WHEN COALESCE(ps.completed,0)=1 THEN 'Completed' WHEN COALESCE(ps.position_ms,0)>0 THEN 'In Progress' ELSE 'Unplayed' END,
               COALESCE(ps.position_ms,0),COALESCE(ps.duration_ms,0),ps.last_played_at,e.date_added,COALESCE(e.favourite,0),
               COALESCE(gn.names,''),COALESCE(tn.names,''),COALESCE(e.artwork_path,''),COALESCE(mf.storage_state,'AvailableOffline'),COALESCE(e.broadcast_slot,''),COALESCE(e.edition,''),COALESCE(e.metadata_confidence,0),COALESCE(e.metadata_confidence_reason,''),COALESCE(e.description,'')
          FROM episodes e
          JOIN collections c ON c.id=e.collection_id
          JOIN media_files mf ON mf.episode_id=e.id AND mf.is_missing=0 AND mf.is_preferred=1
          LEFT JOIN playback_state ps ON ps.episode_id=e.id
          LEFT JOIN guest_names gn ON gn.episode_id=e.id
          LEFT JOIN tag_names tn ON tn.episode_id=e.id
         WHERE COALESCE(e.hidden,0)=0
           AND ($episode IS NULL OR e.id=$episode)
        """;
        command.Parameters.AddWithValue("$episode", episodeId.HasValue ? episodeId.Value : DBNull.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new EpisodeListItem
            {
                Id=reader.GetInt64(0), BroadcastUid=reader.IsDBNull(1)?"":reader.GetString(1), PartNumber=reader.GetInt32(2), TotalParts=reader.IsDBNull(3)?null:reader.GetInt32(3), CollectionId=reader.GetInt32(4), CollectionName=reader.GetString(5),
                AirDate=reader.IsDBNull(6)?null:DateTime.Parse(reader.GetString(6)), DisplayTitle=reader.GetString(7), OriginalFilename=reader.GetString(8), Path=reader.GetString(9), Status=reader.GetString(10),
                PositionMs=reader.GetInt64(11), DurationMs=reader.GetInt64(12), LastPlayedAt=reader.IsDBNull(13)?null:DateTime.Parse(reader.GetString(13)), DateAdded=DateTime.Parse(reader.GetString(14)), Favourite=reader.GetInt32(15)==1,
                Guests=reader.GetString(16), Tags=reader.GetString(17), ArtworkPath=string.IsNullOrWhiteSpace(reader.GetString(18))?null:reader.GetString(18), StorageState=Enum.TryParse<EpisodeStorageState>(reader.GetString(19),out var storageState)?storageState:EpisodeStorageState.AvailableOffline, BroadcastSlot=reader.GetString(20), Edition=reader.GetString(21), MetadataConfidence=reader.GetInt32(22), MetadataConfidenceReason=reader.GetString(23), Summary=reader.GetString(24)
            });
        }
        return result;
    }

    public void UpdateStorageState(long episodeId, EpisodeStorageState state)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE media_files SET storage_state=$state WHERE episode_id=$id";
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$id", episodeId);
        command.ExecuteNonQuery();
    }

    public void UpdateMediaFileStorageState(long mediaFileId, EpisodeStorageState state)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE media_files SET storage_state=$state WHERE id=$id";
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$id", mediaFileId);
        command.ExecuteNonQuery();
    }

    public void ReconcileStorageStates()
    {
        var roots = GetLibraryFolders()
            .Where(folder => folder.Enabled)
            .Select(folder => folder.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        if (roots.Length == 0) return;

        var files = new List<(long Id, string Path)>();
        using (var connection = OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,path FROM media_files";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(1);
                if (roots.Any(root => IsPathWithinRoot(path, root)))
                    files.Add((reader.GetInt64(0), path));
            }
        }

        var cloudFiles = new CloudFileService();
        using var updateConnection = OpenConnection();
        using var transaction = updateConnection.BeginTransaction();
        foreach (var file in files)
        {
            var state = cloudFiles.GetStorageState(file.Path);
            using var update = updateConnection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE media_files SET storage_state=$state,is_missing=$missing,last_seen_at=CASE WHEN $missing=0 THEN $now ELSE last_seen_at END WHERE id=$id";
            update.Parameters.AddWithValue("$state", state.ToString());
            update.Parameters.AddWithValue("$missing", state == EpisodeStorageState.Missing ? 1 : 0);
            update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$id", file.Id);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = root + Path.DirectorySeparatorChar;
        var alternatePrefix = root + Path.AltDirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(alternatePrefix, StringComparison.OrdinalIgnoreCase);
    }

    public string GetDatabaseQuickCheck()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        return Convert.ToString(command.ExecuteScalar())?.Trim() ?? "unknown";
    }

    public StorageSummary GetStorageSummary()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT COUNT(*),
               SUM(CASE WHEN mf.storage_state='AvailableOffline' AND mf.is_missing=0 THEN 1 ELSE 0 END),
               SUM(CASE WHEN mf.storage_state='CloudOnly' AND mf.is_missing=0 THEN 1 ELSE 0 END),
               SUM(CASE WHEN mf.is_missing=1 OR mf.storage_state IN ('Missing','Unavailable') THEN 1 ELSE 0 END),
               COALESCE(SUM(mf.file_size),0)
        FROM media_files mf
        WHERE EXISTS (
            SELECT 1
            FROM library_folders lf
            WHERE lf.enabled=1
              AND (
                  replace(mf.path,'\','/') = rtrim(replace(lf.path,'\','/'),'/')
                  OR replace(mf.path,'\','/') LIKE rtrim(replace(lf.path,'\','/'),'/') || '/%'
              )
        );
        """;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new StorageSummary();
        return new StorageSummary
        {
            TotalFiles = reader.GetInt32(0),
            AvailableOffline = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            CloudOnly = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            Missing = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            LogicalBytes = reader.IsDBNull(4) ? 0 : reader.GetInt64(4)
        };
    }

    public PlaybackState GetPlaybackState(long episodeId)
    {
        var ids = ExpandCanonicalStateEpisodeIds(episodeId);
        if (ids.Count == 0) return new PlaybackState { EpisodeId = episodeId };

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var parameters = ids.Select((_, index) => $"$id{index}").ToArray();
        command.CommandText = $"""
            SELECT position_ms,completed,duration_ms,playback_speed,first_played_at,last_played_at,play_count,completion_count
              FROM playback_state
             WHERE episode_id IN ({string.Join(',', parameters)})
             ORDER BY CASE WHEN last_played_at IS NULL THEN 1 ELSE 0 END,last_played_at DESC,position_ms DESC
            """;
        for (var index = 0; index < ids.Count; index++)
            command.Parameters.AddWithValue(parameters[index], ids[index]);

        var result = new PlaybackState { EpisodeId = episodeId };
        var newestSpeedRead = false;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.PositionMs = Math.Max(result.PositionMs, reader.GetInt64(0));
            result.Completed |= reader.GetInt32(1) == 1;
            result.DurationMs = Math.Max(result.DurationMs, reader.GetInt64(2));
            if (!newestSpeedRead)
            {
                result.PlaybackSpeed = reader.GetDouble(3);
                newestSpeedRead = true;
            }

            DateTime? firstPlayed = reader.IsDBNull(4)
                ? null
                : DateTime.Parse(reader.GetString(4));
            DateTime? lastPlayed = reader.IsDBNull(5)
                ? null
                : DateTime.Parse(reader.GetString(5));
            if (firstPlayed.HasValue && (!result.FirstPlayedAt.HasValue || firstPlayed.Value < result.FirstPlayedAt.Value))
                result.FirstPlayedAt = firstPlayed;
            if (lastPlayed.HasValue && (!result.LastPlayedAt.HasValue || lastPlayed.Value > result.LastPlayedAt.Value))
                result.LastPlayedAt = lastPlayed;
            result.PlayCount = Math.Max(result.PlayCount, reader.IsDBNull(6) ? 0 : reader.GetInt32(6));
            result.CompletionCount = Math.Max(result.CompletionCount, reader.IsDBNull(7) ? 0 : reader.GetInt32(7));
        }

        return result;
    }

    public void SavePlaybackState(
        long episodeId,
        long positionMs,
        long durationMs,
        bool completed,
        double playbackSpeed,
        bool incrementPlayCount = false,
        bool incrementCompletionCount = false,
        bool allowPositionReset = false)
    {
        var ids = ExpandCanonicalStateEpisodeIds(episodeId);
        if (ids.Count == 0) return;

        var existing = GetPlaybackState(episodeId);
        var requestedPosition = Math.Max(0, positionMs);
        var effectivePosition = PlaybackPersistencePolicy.ResolvePosition(requestedPosition, existing.PositionMs, allowPositionReset);
        var preserveExistingProgress = effectivePosition != requestedPosition;
        var effectiveDuration = Math.Max(Math.Max(0, durationMs), existing.DurationMs);
        var effectiveCompleted = preserveExistingProgress ? existing.Completed : completed;
        var effectiveSpeed = playbackSpeed > 0 ? playbackSpeed : existing.PlaybackSpeed > 0 ? existing.PlaybackSpeed : 1d;
        var now = DateTime.UtcNow.ToString("O");

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var stateEpisodeId in ids)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
            INSERT INTO playback_state(
              episode_id,position_ms,completed,last_played_at,play_count,duration_ms,playback_speed,
              completed_at,first_played_at,completion_count)
            VALUES(
              $id,$position,$completed,$now,$playCount,$duration,$speed,
              CASE WHEN $completed=1 THEN COALESCE($completedAt,$now) ELSE $completedAt END,
              CASE WHEN $increment=1 THEN COALESCE($firstPlayedAt,$now) ELSE $firstPlayedAt END,
              $completionCount)
            ON CONFLICT(episode_id) DO UPDATE SET
              position_ms=excluded.position_ms,
              completed=excluded.completed,
              last_played_at=excluded.last_played_at,
              play_count=MAX(playback_state.play_count,$basePlayCount)+$increment,
              duration_ms=MAX(playback_state.duration_ms,excluded.duration_ms),
              playback_speed=excluded.playback_speed,
              completed_at=CASE WHEN excluded.completed=1 THEN COALESCE(playback_state.completed_at,excluded.completed_at) ELSE playback_state.completed_at END,
              first_played_at=COALESCE(playback_state.first_played_at,excluded.first_played_at),
              completion_count=MAX(playback_state.completion_count,$baseCompletionCount)+$completionIncrement
            """;
            command.Parameters.AddWithValue("$id", stateEpisodeId);
            command.Parameters.AddWithValue("$position", effectivePosition);
            command.Parameters.AddWithValue("$duration", effectiveDuration);
            command.Parameters.AddWithValue("$completed", effectiveCompleted ? 1 : 0);
            command.Parameters.AddWithValue("$speed", effectiveSpeed);
            command.Parameters.AddWithValue("$increment", incrementPlayCount ? 1 : 0);
            command.Parameters.AddWithValue("$completionIncrement", incrementCompletionCount ? 1 : 0);
            command.Parameters.AddWithValue("$basePlayCount", existing.PlayCount);
            command.Parameters.AddWithValue("$baseCompletionCount", existing.CompletionCount);
            command.Parameters.AddWithValue("$playCount", existing.PlayCount + (incrementPlayCount ? 1 : 0));
            command.Parameters.AddWithValue("$completionCount", existing.CompletionCount + (incrementCompletionCount ? 1 : 0));
            command.Parameters.AddWithValue("$firstPlayedAt", existing.FirstPlayedAt?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$completedAt", effectiveCompleted && existing.LastPlayedAt.HasValue
                ? existing.LastPlayedAt.Value.ToUniversalTime().ToString("O")
                : (object)DBNull.Value);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();

            using var status = connection.CreateCommand();
            status.Transaction = transaction;
            status.CommandText = "UPDATE episodes SET status=$status,updated_at=$now WHERE id=$id";
            status.Parameters.AddWithValue("$status", effectiveCompleted ? "Completed" : effectivePosition > 0 ? "In Progress" : "Unplayed");
            status.Parameters.AddWithValue("$now", now);
            status.Parameters.AddWithValue("$id", stateEpisodeId);
            status.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void MarkCompleted(long episodeId, bool completed)
    {
        var state = GetPlaybackState(episodeId);
        var position = completed && state.DurationMs > 0
            ? state.DurationMs
            : completed ? state.PositionMs : 0;
        SavePlaybackState(
            episodeId,
            position,
            state.DurationMs,
            completed,
            state.PlaybackSpeed,
            allowPositionReset: !completed);
    }

    public void SetFavourite(long episodeId, bool favourite)
    {
        var ids = ExpandCanonicalStateEpisodeIds(episodeId);
        if (ids.Count == 0) return;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var stateEpisodeId in ids)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE episodes SET favourite=$value,updated_at=$now WHERE id=$id";
            command.Parameters.AddWithValue("$value", favourite ? 1 : 0);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", stateEpisodeId);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void UpdateEpisodeMetadata(long episodeId, string title, string description, string notes)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE episodes SET title=$title,description=$description,notes=$notes,user_modified=1,updated_at=$now WHERE id=$id";
        command.Parameters.AddWithValue("$title", title.Trim());
        command.Parameters.AddWithValue("$description", description.Trim());
        command.Parameters.AddWithValue("$notes", notes.Trim());
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", episodeId);
        command.ExecuteNonQuery();
        RecordManualFieldProvenance(connection, transaction, episodeId, "headline", title.Trim(), "Manual metadata edit");
        RecordManualFieldProvenance(connection, transaction, episodeId, "summary", description.Trim(), "Manual metadata edit");
        transaction.Commit();
    }

    public (string Description, string Notes) GetEpisodeDetails(long episodeId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(description,''),COALESCE(notes,'') FROM episodes WHERE id=$id";
        command.Parameters.AddWithValue("$id", episodeId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : ("", "");
    }


    public long? GetEpisodeIdByPath(string path)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT episode_id FROM media_files WHERE path=$path LIMIT 1";
        command.Parameters.AddWithValue("$path", path);
        var value = command.ExecuteScalar();
        return value is null ? null : Convert.ToInt64(value);
    }

    public void ApplyScannedMetadata(long episodeId, ScannedAudioMetadata metadata, string? artworkPath)
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        using (var update = connection.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
            UPDATE episodes SET
              title=CASE WHEN COALESCE(user_modified,0)=0 AND $title<>'' THEN $title ELSE title END,
              description=CASE WHEN COALESCE(user_modified,0)=0 AND COALESCE(description,'')='' THEN $description ELSE description END,
              artwork_path=COALESCE(artwork_path,$artwork), updated_at=$now
            WHERE id=$id;
            UPDATE media_files SET duration_ms=$duration WHERE episode_id=$id;
            UPDATE playback_state SET duration_ms=CASE WHEN duration_ms=0 THEN $duration ELSE duration_ms END WHERE episode_id=$id;
            """;
            update.Parameters.AddWithValue("$title", metadata.Title ?? "");
            update.Parameters.AddWithValue("$description", metadata.Description ?? "");
            update.Parameters.AddWithValue("$artwork", artworkPath ?? (object)DBNull.Value);
            update.Parameters.AddWithValue("$duration", metadata.DurationMs);
            update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$id", episodeId);
            update.ExecuteNonQuery();
        }
        // A library scan is a low-priority metadata source. It may seed embedded
        // performers and genres for a newly discovered broadcast, but it must
        // never replace people or topics that were imported from a research pack
        // or edited by the user. In v0.25.0 ReplaceNames cleared those richer
        // links on every rescan, which made researched guests disappear.
        SeedScannedNamesIfUnmodifiedAndEmpty(connection, tx, episodeId, "guests", "episode_guests", "guest_id", metadata.Guests);
        SeedScannedNamesIfUnmodifiedAndEmpty(connection, tx, episodeId, "tags", "episode_tags", "tag_id", metadata.Tags);
        tx.Commit();
    }

    private static void SeedScannedNamesIfUnmodifiedAndEmpty(
        SqliteConnection connection,
        SqliteTransaction tx,
        long episodeId,
        string entityTable,
        string joinTable,
        string idColumn,
        IEnumerable<string> names)
    {
        using var eligibility = connection.CreateCommand();
        eligibility.Transaction = tx;
        eligibility.CommandText = $"""
            SELECT CASE
              WHEN COALESCE((SELECT user_modified FROM episodes WHERE id=$episode),0)=0
               AND NOT EXISTS(SELECT 1 FROM {joinTable} WHERE episode_id=$episode)
              THEN 1 ELSE 0 END
            """;
        eligibility.Parameters.AddWithValue("$episode", episodeId);
        if (Convert.ToInt32(eligibility.ExecuteScalar()) != 1) return;

        foreach (var name in names
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Select(x => x.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var add = connection.CreateCommand();
            add.Transaction = tx;
            add.CommandText = $"INSERT OR IGNORE INTO {entityTable}(name) VALUES($name); SELECT id FROM {entityTable} WHERE name=$name";
            add.Parameters.AddWithValue("$name", name);
            var id = Convert.ToInt64(add.ExecuteScalar());

            using var link = connection.CreateCommand();
            link.Transaction = tx;
            link.CommandText = $"INSERT OR IGNORE INTO {joinTable}(episode_id,{idColumn}) VALUES($episode,$id)";
            link.Parameters.AddWithValue("$episode", episodeId);
            link.Parameters.AddWithValue("$id", id);
            link.ExecuteNonQuery();
        }
    }

    private static void ReplaceNames(SqliteConnection connection, SqliteTransaction tx, long episodeId, string entityTable, string joinTable, string idColumn, IEnumerable<string> names)
    {
        using var clear = connection.CreateCommand(); clear.Transaction = tx;
        clear.CommandText = $"DELETE FROM {joinTable} WHERE episode_id=$episode";
        clear.Parameters.AddWithValue("$episode", episodeId); clear.ExecuteNonQuery();
        foreach (var name in names.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var add = connection.CreateCommand(); add.Transaction = tx;
            add.CommandText = $"INSERT OR IGNORE INTO {entityTable}(name) VALUES($name); SELECT id FROM {entityTable} WHERE name=$name";
            add.Parameters.AddWithValue("$name", name);
            var id = Convert.ToInt64(add.ExecuteScalar());
            using var link = connection.CreateCommand(); link.Transaction = tx;
            link.CommandText = $"INSERT OR IGNORE INTO {joinTable}(episode_id,{idColumn}) VALUES($episode,$id)";
            link.Parameters.AddWithValue("$episode", episodeId); link.Parameters.AddWithValue("$id", id); link.ExecuteNonQuery();
        }
    }

    public EpisodeMetadata GetRichEpisodeMetadata(long episodeId)
    {
        using var connection = OpenConnection();
        return GetRichEpisodeMetadata(connection, null, episodeId);
    }

    private static EpisodeMetadata GetRichEpisodeMetadata(SqliteConnection connection, SqliteTransaction? transaction, long episodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
        SELECT e.title,COALESCE(e.description,''),COALESCE(e.notes,''),COALESCE(e.artwork_path,''),COALESCE(e.user_modified,0),COALESCE(e.edition,''),COALESCE(e.metadata_confidence,0),COALESCE(e.metadata_confidence_reason,''),
          COALESCE((SELECT group_concat(g.name, ', ') FROM episode_guests eg JOIN guests g ON g.id=eg.guest_id WHERE eg.episode_id=e.id),''),
          COALESCE((SELECT group_concat(t.name, ', ') FROM episode_tags et JOIN tags t ON t.id=et.tag_id WHERE et.episode_id=e.id),''),COALESCE(e.archive_notes,''),
          COALESCE(e.hosts,''),COALESCE(e.callers,''),COALESCE(e.mentioned_people,'')
        FROM episodes e WHERE e.id=$id
        """;
        command.Parameters.AddWithValue("$id", episodeId);
        using var r = command.ExecuteReader();
        if (!r.Read()) return new EpisodeMetadata { EpisodeId = episodeId };
        return new EpisodeMetadata { EpisodeId=episodeId, Title=r.GetString(0), Description=r.GetString(1), Notes=r.GetString(2), ArtworkPath=string.IsNullOrWhiteSpace(r.GetString(3))?null:r.GetString(3), UserModified=r.GetInt32(4)==1, Edition=r.GetString(5), MetadataConfidence=r.GetInt32(6), MetadataConfidenceReason=r.GetString(7), Guests=r.GetString(8), Tags=r.GetString(9), ArchiveNotes=r.GetString(10), Hosts=string.Join(", ", SplitPipe(r.GetString(11))), Callers=string.Join(", ", SplitPipe(r.GetString(12))), MentionedPeople=string.Join(", ", SplitPipe(r.GetString(13))) };
    }

    public void UpdateRichEpisodeMetadata(long episodeId, string title, string description, string notes, string guests, string tags, string? artworkPath, string? edition = null, string? archiveNotes = null)
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = "UPDATE episodes SET title=$title,description=$description,notes=$notes,archive_notes=COALESCE($archiveNotes,archive_notes),artwork_path=$artwork,edition=COALESCE($edition,edition),user_modified=1,updated_at=$now WHERE id=$id";
            command.Parameters.AddWithValue("$title", title.Trim()); command.Parameters.AddWithValue("$description", description.Trim()); command.Parameters.AddWithValue("$notes", notes.Trim());
            command.Parameters.AddWithValue("$artwork", string.IsNullOrWhiteSpace(artworkPath)?DBNull.Value:artworkPath); command.Parameters.AddWithValue("$edition", edition is null ? DBNull.Value : edition.Trim()); command.Parameters.AddWithValue("$archiveNotes", archiveNotes is null ? DBNull.Value : archiveNotes.Trim()); command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O")); command.Parameters.AddWithValue("$id", episodeId); command.ExecuteNonQuery();
        }
        var guestNames = NormalizeNames(guests.Split(',', StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries));
        var topicNames = NormalizeNames(tags.Split(',', StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries));
        ReplaceNames(connection, tx, episodeId, "guests", "episode_guests", "guest_id", guestNames);
        ReplaceNames(connection, tx, episodeId, "tags", "episode_tags", "tag_id", topicNames);
        RecordManualFieldProvenance(connection, tx, episodeId, "headline", title.Trim(), "Broadcast Info metadata edit");
        RecordManualFieldProvenance(connection, tx, episodeId, "summary", description.Trim(), "Broadcast Info metadata edit");
        if (edition is not null) RecordManualFieldProvenance(connection, tx, episodeId, "station", edition.Trim(), "Broadcast Info metadata edit");
        if (archiveNotes is not null) RecordManualFieldProvenance(connection, tx, episodeId, "archive_notes", archiveNotes.Trim(), "Broadcast Info metadata edit");
        RecordManualFieldProvenance(connection, tx, episodeId, "guests", JsonSerializer.Serialize(guestNames), "Broadcast Info metadata edit");
        RecordManualFieldProvenance(connection, tx, episodeId, "topics", JsonSerializer.Serialize(topicNames), "Broadcast Info metadata edit");
        tx.Commit();
    }


    public void UpdateEpisodePeople(long episodeId, string hosts, string guests, string callers, string mentionedPeople)
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = "UPDATE episodes SET hosts=$hosts,callers=$callers,mentioned_people=$mentioned,user_modified=1,updated_at=$now WHERE id=$id";
            command.Parameters.AddWithValue("$hosts", JoinPipe(hosts));
            command.Parameters.AddWithValue("$callers", JoinPipe(callers));
            command.Parameters.AddWithValue("$mentioned", JoinPipe(mentionedPeople));
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", episodeId);
            command.ExecuteNonQuery();
        }
        var hostNames = NormalizeNames(SplitPeople(hosts));
        var guestNames = NormalizeNames(SplitPeople(guests));
        var callerNames = NormalizeNames(SplitPeople(callers));
        var mentionedNames = NormalizeNames(SplitPeople(mentionedPeople));
        ReplaceNames(connection, tx, episodeId, "guests", "episode_guests", "guest_id", guestNames);
        RecordManualFieldProvenance(connection, tx, episodeId, "hosts", JsonSerializer.Serialize(hostNames), "People metadata edit");
        RecordManualFieldProvenance(connection, tx, episodeId, "guests", JsonSerializer.Serialize(guestNames), "People metadata edit");
        RecordManualFieldProvenance(connection, tx, episodeId, "callers", JsonSerializer.Serialize(callerNames), "People metadata edit");
        RecordManualFieldProvenance(connection, tx, episodeId, "mentioned_people", JsonSerializer.Serialize(mentionedNames), "People metadata edit");
        tx.Commit();
    }

    private static IEnumerable<string> SplitPeople(string value) =>
        value.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string JoinPipe(string value) =>
        string.Join("|", SplitPeople(value).Distinct(StringComparer.OrdinalIgnoreCase));

    public MetadataPackage ExportMetadataPackage(string appVersion)
    {
        var package = new MetadataPackage { AppVersion = appVersion };
        using var connection = OpenConnection();
        using (var repair = connection.BeginTransaction())
        {
            RepairDuplicateMoments(connection, repair);
            repair.Commit();
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
            SELECT e.id,COALESCE(e.broadcast_uid,''),c.name,e.air_date,e.part_number,e.total_parts,
              COALESCE(mf.original_filename,''),COALESCE(e.title,''),COALESCE(e.description,''),COALESCE(e.notes,''),COALESCE(e.archive_notes,''),
              COALESCE(e.edition,''),COALESCE(e.broadcast_slot,''),COALESCE(e.broadcast_variant,''),COALESCE(e.broadcast_era,''),COALESCE(e.episode_type,''),
              COALESCE(e.metadata_confidence,0),COALESCE(e.metadata_confidence_reason,''),COALESCE(e.research_sources,''),COALESCE(e.artwork_path,''),
              COALESCE(mf.duration_ms,ps.duration_ms,0),COALESCE(mf.storage_state,''),COALESCE(e.favourite,0),
              COALESCE(e.hosts,''),COALESCE(e.callers,''),COALESCE(e.mentioned_people,''),
              COALESCE((SELECT group_concat(g.name, '|') FROM episode_guests eg JOIN guests g ON g.id=eg.guest_id WHERE eg.episode_id=e.id),''),
              COALESCE((SELECT group_concat(t.name, '|') FROM episode_tags et JOIN tags t ON t.id=et.tag_id WHERE et.episode_id=e.id),'')
            FROM episodes e
            JOIN collections c ON c.id=e.collection_id
            LEFT JOIN media_files mf ON mf.episode_id=e.id AND mf.is_preferred=1
            LEFT JOIN playback_state ps ON ps.episode_id=e.id
            WHERE e.hidden=0
            ORDER BY c.name,e.air_date,e.part_number
            """;
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                var edition = r.GetString(11);
                var slot = r.GetString(12);
                if (string.Equals(edition, "OpieRadio Edition", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(slot))
                {
                    slot = "OpieRadio Edition";
                    edition = "";
                }

                var episode = new MetadataPackageEpisode
                {
                    BroadcastUid=r.GetString(1), Collection=r.GetString(2), AirDate=r.IsDBNull(3)?null:r.GetString(3), PartNumber=r.GetInt32(4), TotalParts=r.IsDBNull(5)?null:r.GetInt32(5),
                    OriginalFilename=r.GetString(6), Headline=r.GetString(7), Summary=r.GetString(8), PersonalNotes=r.GetString(9), ArchiveNotes=r.GetString(10),
                    Broadcast=new MetadataBroadcastFields { Station=edition, Slot=slot, Variant=r.GetString(13), Era=r.GetString(14), EpisodeType=r.GetString(15) },
                    People=new MetadataPeopleFields { Hosts=SplitPipe(r.GetString(23)), Callers=SplitPipe(r.GetString(24)), MentionedPeople=SplitPipe(r.GetString(25)), Guests=SplitPipe(r.GetString(26)) }, Topics=SplitPipe(r.GetString(27)),
                    Research=new MetadataResearchFields { Confidence=r.GetInt32(16), ConfidenceReason=r.GetString(17), Sources=SplitLines(r.GetString(18)) },
                    Archive=new MetadataArchiveFields { ArtworkPath=r.GetString(19), DurationMs=r.GetInt64(20), StorageState=r.GetString(21), Favourite=r.GetInt32(22)==1 }
                };
                episode.Moments = GetMetadataMoments(connection, r.GetInt64(0));
                package.Episodes.Add(episode);
            }
        }
        return package;
    }

    public MetadataImportReport ImportMetadataPackage(MetadataPackage package)
    {
        var report = new MetadataImportReport { Total = package.Episodes.Count };
        using var connection = OpenConnection();
        foreach (var item in package.Episodes)
        {
            var matches = FindMetadataEpisodeMatches(connection, item);
            if (matches.Count == 0) { report.Unmatched++; continue; }
            if (matches.Count > 1) { report.Ambiguous++; continue; }
            var id = matches[0];
            report.Matched++;
            string? existingArtwork;
            using (var artworkLookup = connection.CreateCommand())
            {
                artworkLookup.CommandText = "SELECT artwork_path FROM episodes WHERE id=$id";
                artworkLookup.Parameters.AddWithValue("$id", id);
                existingArtwork = artworkLookup.ExecuteScalar() as string;
            }
            var importedEdition = item.Broadcast?.Station ?? "";
            var importedSlot = item.Broadcast?.Slot ?? "";
            if (string.Equals(importedEdition, "OpieRadio Edition", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(importedSlot))
            {
                importedSlot = "OpieRadio Edition";
                importedEdition = "";
            }

            UpdateRichEpisodeMetadata(id, item.Headline, item.Summary, item.PersonalNotes,
                string.Join(", ", item.People?.Guests ?? Array.Empty<string>()),
                string.Join(", ", item.Topics ?? Array.Empty<string>()),
                string.IsNullOrWhiteSpace(item.Archive?.ArtworkPath) ? existingArtwork : item.Archive.ArtworkPath,
                importedEdition, item.ArchiveNotes);

            using (var update = connection.CreateCommand())
            {
                update.CommandText = """
                UPDATE episodes SET broadcast_slot=$slot,broadcast_variant=$variant,broadcast_era=$era,episode_type=$type,
                  metadata_confidence=$confidence,metadata_confidence_reason=$reason,research_sources=$sources,
                  hosts=$hosts,callers=$callers,mentioned_people=$mentioned,
                  favourite=$favourite,updated_at=$now WHERE id=$id
                """;
                update.Parameters.AddWithValue("$slot", importedSlot);
                update.Parameters.AddWithValue("$variant", item.Broadcast?.Variant ?? "");
                update.Parameters.AddWithValue("$era", item.Broadcast?.Era ?? "");
                update.Parameters.AddWithValue("$type", item.Broadcast?.EpisodeType ?? "");
                update.Parameters.AddWithValue("$confidence", item.Research?.Confidence ?? 0);
                update.Parameters.AddWithValue("$reason", item.Research?.ConfidenceReason ?? "");
                update.Parameters.AddWithValue("$sources", string.Join("\n", item.Research?.Sources ?? Array.Empty<string>()));
                update.Parameters.AddWithValue("$hosts", string.Join("|", item.People?.Hosts ?? Array.Empty<string>()));
                update.Parameters.AddWithValue("$callers", string.Join("|", item.People?.Callers ?? Array.Empty<string>()));
                update.Parameters.AddWithValue("$mentioned", string.Join("|", item.People?.MentionedPeople ?? Array.Empty<string>()));
                update.Parameters.AddWithValue("$favourite", item.Archive?.Favourite == true ? 1 : 0);
                update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                update.Parameters.AddWithValue("$id", id);
                update.ExecuteNonQuery();
            }
            foreach (var moment in item.Moments ?? new List<MetadataPackageMoment>())
            {
                using var exists = connection.CreateCommand();
                exists.CommandText = "SELECT 1 FROM moments WHERE episode_id=$episode AND position_ms=$position AND lower(title)=lower($title) LIMIT 1";
                exists.Parameters.AddWithValue("$episode", id); exists.Parameters.AddWithValue("$position", Math.Max(0,moment.PositionMs)); exists.Parameters.AddWithValue("$title", moment.Title ?? "");
                if (exists.ExecuteScalar() is not null) continue;
                AddMoment(id, moment.PositionMs, moment.Title ?? "", moment.Notes ?? ""); report.MomentsAdded++;
            }
            report.Updated++;
        }
        return report;
    }

    // Compatibility wrappers retained for older callers and files.
    public IReadOnlyList<MetadataExportEpisode> ExportMetadata() => ExportMetadataPackage("").Episodes.Select(x => new MetadataExportEpisode
    {
        Collection=x.Collection, AirDate=x.AirDate, Title=x.Headline, Description=x.Summary, Notes=x.PersonalNotes, ArchiveNotes=x.ArchiveNotes,
        Edition=x.Broadcast.Station, Guests=x.People.Guests, Tags=x.Topics, OriginalFilename=x.OriginalFilename
    }).ToList();

    public int ImportMetadata(IEnumerable<MetadataExportEpisode> items)
    {
        var package = new MetadataPackage { SchemaVersion=1, Episodes=items.Select(x => new MetadataPackageEpisode
        {
            Collection=x.Collection,AirDate=x.AirDate,OriginalFilename=x.OriginalFilename,Headline=x.Title,Summary=x.Description,PersonalNotes=x.Notes,ArchiveNotes=x.ArchiveNotes,
            Broadcast=new MetadataBroadcastFields{Station=x.Edition},People=new MetadataPeopleFields{Guests=x.Guests},Topics=x.Tags
        }).ToList() };
        return ImportMetadataPackage(package).Updated;
    }

    private static string[] SplitPipe(string value) => value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string[] SplitLines(string value) => value.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private List<MetadataPackageMoment> GetMetadataMoments(SqliteConnection ignoredConnection, long episodeId)
    {
        var result = new List<MetadataPackageMoment>();
        using var connection = OpenConnection();
        using var cmd=connection.CreateCommand();
        cmd.CommandText="SELECT position_ms,title,notes,created_at FROM moments WHERE episode_id=$id ORDER BY position_ms";
        cmd.Parameters.AddWithValue("$id",episodeId);
        using var r=cmd.ExecuteReader();
        while(r.Read()) result.Add(new MetadataPackageMoment { PositionMs=r.GetInt64(0),Title=r.GetString(1),Notes=r.GetString(2),CreatedUtc=DateTime.TryParse(r.GetString(3),out var created)?created:null });
        return result;
    }

    private static List<long> FindMetadataEpisodeMatches(SqliteConnection connection, MetadataPackageEpisode item)
    {
        var result=new List<long>();
        using var find=connection.CreateCommand();
        find.CommandText="""
        SELECT DISTINCT e.id FROM episodes e JOIN collections c ON c.id=e.collection_id LEFT JOIN media_files mf ON mf.episode_id=e.id
        WHERE (COALESCE($uid,'')<>'' AND e.broadcast_uid=$uid)
           OR (COALESCE($file,'')<>'' AND lower(mf.original_filename)=lower($file))
           OR (lower(c.name)=lower($collection) AND e.air_date=$date AND e.part_number=$part)
        ORDER BY CASE WHEN e.broadcast_uid=$uid THEN 0 WHEN lower(COALESCE(mf.original_filename,''))=lower($file) THEN 1 ELSE 2 END
        """;
        find.Parameters.AddWithValue("$uid",item.BroadcastUid??""); find.Parameters.AddWithValue("$file",item.OriginalFilename??"");
        find.Parameters.AddWithValue("$collection",item.Collection??""); find.Parameters.AddWithValue("$date",item.AirDate??(object)DBNull.Value); find.Parameters.AddWithValue("$part",Math.Max(1,item.PartNumber));
        using var r=find.ExecuteReader(); while(r.Read()) result.Add(r.GetInt64(0));
        if(result.Count>1 && !string.IsNullOrWhiteSpace(item.BroadcastUid)) return result.Take(1).ToList();
        if(result.Count>1 && !string.IsNullOrWhiteSpace(item.OriginalFilename))
        {
            using var exact=connection.CreateCommand(); exact.CommandText="SELECT DISTINCT e.id FROM episodes e JOIN media_files mf ON mf.episode_id=e.id WHERE lower(mf.original_filename)=lower($file)"; exact.Parameters.AddWithValue("$file",item.OriginalFilename);
            var exacts=new List<long>(); using var er=exact.ExecuteReader(); while(er.Read()) exacts.Add(er.GetInt64(0)); if(exacts.Count==1)return exacts;
        }
        return result.Distinct().ToList();
    }


    public IReadOnlyList<HeadlineReviewItem> GetHeadlineReviewCandidates(string? search = null)
    {
        var result = new List<HeadlineReviewItem>();
        var parser = new TheRadioVault.Core.Services.FilenameParserService();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT e.id,c.name,e.air_date,e.broadcast_uid,mf.original_filename,mf.path,
               COALESCE(hr.decision,''),COALESCE(e.title,''),COALESCE(e.user_modified,0)
        FROM episodes e
        JOIN collections c ON c.id=e.collection_id
        JOIN media_files mf ON mf.episode_id=e.id AND mf.is_preferred=1
        LEFT JOIN headline_reviews hr ON hr.episode_id=e.id
        WHERE COALESCE(e.hidden,0)=0
          AND COALESCE(hr.decision,'') NOT IN ('Accepted','Rejected')
        ORDER BY e.air_date DESC,e.id DESC
        """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var parsed = parser.Parse(reader.GetString(5));
            if (string.IsNullOrWhiteSpace(parsed.HeadlineCandidate) || parsed.HeadlineConfidence is "None" or "Low") continue;
            var item = new HeadlineReviewItem
            {
                EpisodeId=reader.GetInt64(0), CollectionName=reader.GetString(1),
                AirDate=reader.IsDBNull(2)?null:DateTime.Parse(reader.GetString(2)),
                BroadcastUid=reader.IsDBNull(3)?"":reader.GetString(3), OriginalFilename=reader.GetString(4), Path=reader.GetString(5),
                Candidate=parsed.HeadlineCandidate!, Confidence=parsed.HeadlineConfidence, Reasoning=parsed.HeadlineReasoning,
                CurrentHeadline=reader.IsDBNull(7)?"":reader.GetString(7), IsAlreadyApplied=!reader.IsDBNull(7) && string.Equals(reader.GetString(7), parsed.HeadlineCandidate, StringComparison.OrdinalIgnoreCase),
                WasUserModified=!reader.IsDBNull(8) && reader.GetInt64(8)!=0,
                ParserVersion=TheRadioVault.Core.Services.FilenameParserService.CurrentParserVersion,
                PreviousDecision=reader.IsDBNull(6)?"":reader.GetString(6)
            };
            if (!string.IsNullOrWhiteSpace(search))
            {
                var q=search.Trim();
                if (!item.CollectionName.Contains(q,StringComparison.OrdinalIgnoreCase) && !item.Candidate.Contains(q,StringComparison.OrdinalIgnoreCase) && !item.OriginalFilename.Contains(q,StringComparison.OrdinalIgnoreCase) && !item.AirDateDisplay.Contains(q,StringComparison.OrdinalIgnoreCase)) continue;
            }
            result.Add(item);
        }
        return result;
    }

    public void AcceptHeadlineCandidate(long episodeId, string candidate, string headline, string confidence, string reasoning)
    {
        using var connection = OpenConnection(); using var tx=connection.BeginTransaction();
        using(var update=connection.CreateCommand()){update.Transaction=tx;update.CommandText="UPDATE episodes SET title=$title,user_modified=1,updated_at=$now WHERE id=$id";update.Parameters.AddWithValue("$title",headline.Trim());update.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));update.Parameters.AddWithValue("$id",episodeId);update.ExecuteNonQuery();}
        UpsertHeadlineReview(connection,tx,episodeId,candidate,headline,confidence,reasoning,"Accepted");
        RecordManualFieldProvenance(connection, tx, episodeId, "headline", headline.Trim(), "Accepted headline candidate");
        tx.Commit();
    }

    public void RejectHeadlineCandidate(long episodeId, string candidate, string confidence, string reasoning)
    {
        using var connection = OpenConnection(); using var tx=connection.BeginTransaction();
        // A high-confidence filename headline may have been provisionally applied during scanning.
        // Rejecting it should remove only that automatic value, never a user-edited headline.
        using(var clear=connection.CreateCommand())
        {
            clear.Transaction=tx;
            clear.CommandText="UPDATE episodes SET title='',updated_at=$now WHERE id=$id AND COALESCE(user_modified,0)=0 AND LOWER(TRIM(COALESCE(title,'')))=LOWER(TRIM($candidate))";
            clear.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));
            clear.Parameters.AddWithValue("$id",episodeId);
            clear.Parameters.AddWithValue("$candidate",candidate);
            clear.ExecuteNonQuery();
        }
        UpsertHeadlineReview(connection,tx,episodeId,candidate,"",confidence,reasoning,"Rejected"); tx.Commit();
    }

    public void SkipHeadlineCandidate(long episodeId, string candidate, string confidence, string reasoning)
    {
        using var connection = OpenConnection(); using var tx=connection.BeginTransaction();
        UpsertHeadlineReview(connection,tx,episodeId,candidate,"",confidence,reasoning,"Skipped"); tx.Commit();
    }

    private static void UpsertHeadlineReview(SqliteConnection connection, SqliteTransaction tx, long episodeId, string candidate, string reviewedHeadline, string confidence, string reasoning, string decision)
    {
        using var cmd=connection.CreateCommand(); cmd.Transaction=tx;
        cmd.CommandText="INSERT INTO headline_reviews(episode_id,candidate,reviewed_headline,confidence,reasoning,decision,parser_version,updated_at) VALUES($id,$candidate,$reviewed,$confidence,$reasoning,$decision,$parser,$now) ON CONFLICT(episode_id) DO UPDATE SET candidate=excluded.candidate,reviewed_headline=excluded.reviewed_headline,confidence=excluded.confidence,reasoning=excluded.reasoning,decision=excluded.decision,parser_version=excluded.parser_version,updated_at=excluded.updated_at";
        cmd.Parameters.AddWithValue("$id",episodeId);cmd.Parameters.AddWithValue("$candidate",candidate);cmd.Parameters.AddWithValue("$reviewed",reviewedHeadline);cmd.Parameters.AddWithValue("$confidence",confidence);cmd.Parameters.AddWithValue("$reasoning",reasoning);cmd.Parameters.AddWithValue("$decision",decision);cmd.Parameters.AddWithValue("$parser",TheRadioVault.Core.Services.FilenameParserService.CurrentParserVersion);cmd.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));cmd.ExecuteNonQuery();
    }

    public BroadcastKnowledge GetBroadcastKnowledge(long episodeId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT e.id,COALESCE(e.broadcast_uid,''),c.name,e.air_date,COALESCE(e.broadcast_slot,''),COALESCE(e.part_number,1),e.total_parts,
               COALESCE(e.title,''),COALESCE(e.description,''),COALESCE(e.archive_notes,''),COALESCE(e.notes,''),COALESCE(e.edition,''),COALESCE(e.artwork_path,''),e.collection_id,
               COALESCE(e.hosts,''),COALESCE(e.callers,''),COALESCE(e.mentioned_people,'')
        FROM episodes e JOIN collections c ON c.id=e.collection_id WHERE e.id=$id
        """;
        command.Parameters.AddWithValue("$id", episodeId);
        using var r = command.ExecuteReader();
        if (!r.Read()) return new BroadcastKnowledge { EpisodeId = episodeId };
        var knowledge = new BroadcastKnowledge
        {
            EpisodeId=r.GetInt64(0), BroadcastUid=r.GetString(1), CollectionName=r.GetString(2), AirDate=r.IsDBNull(3)?null:DateTime.Parse(r.GetString(3)),
            BroadcastSlot=r.GetString(4), PartNumber=r.GetInt32(5), TotalParts=r.IsDBNull(6)?null:r.GetInt32(6), Headline=r.GetString(7), Summary=r.GetString(8),
            ArchiveNotes=r.GetString(9), PersonalNotes=r.GetString(10), Edition=r.GetString(11), ArtworkPath=r.GetString(12)
        };
        var collectionId = r.GetInt32(13);
        knowledge.Hosts = SplitPipe(r.GetString(14)).ToList();
        knowledge.Callers = SplitPipe(r.GetString(15)).ToList();
        knowledge.MentionedPeople = SplitPipe(r.GetString(16)).ToList();
        r.Close();
        knowledge.Guests = ReadNames(connection, episodeId, "episode_guests", "guests", "guest_id");
        knowledge.Topics = ReadNames(connection, episodeId, "episode_tags", "tags", "tag_id");
        if (knowledge.AirDate.HasValue)
        {
            using var related = connection.CreateCommand();
            related.CommandText = """
            SELECT id,air_date,COALESCE(title,'') FROM episodes
            WHERE collection_id=$collection AND id<>$id AND air_date IS NOT NULL
            ORDER BY ABS(julianday(air_date)-julianday($date)) LIMIT 4
            """;
            related.Parameters.AddWithValue("$collection", collectionId); related.Parameters.AddWithValue("$id", episodeId); related.Parameters.AddWithValue("$date", knowledge.AirDate.Value.ToString("yyyy-MM-dd"));
            using var rr=related.ExecuteReader();
            while(rr.Read()) knowledge.Related.Add(new RelatedBroadcastItem { EpisodeId=rr.GetInt64(0), Label=string.IsNullOrWhiteSpace(rr.GetString(2))?knowledge.CollectionName:rr.GetString(2), Subtitle=DateTime.Parse(rr.GetString(1)).ToString("d MMMM yyyy") });
        }
        return knowledge;
    }

    private static List<string> ReadNames(SqliteConnection connection, long episodeId, string joinTable, string entityTable, string idColumn, SqliteTransaction? transaction = null)
    {
        var names=new List<string>(); using var c=connection.CreateCommand();
        c.Transaction = transaction;
        c.CommandText=$"SELECT e.name FROM {joinTable} j JOIN {entityTable} e ON e.id=j.{idColumn} WHERE j.episode_id=$id ORDER BY e.name";
        c.Parameters.AddWithValue("$id",episodeId); using var r=c.ExecuteReader(); while(r.Read()) names.Add(r.GetString(0)); return names;
    }

    public TrvKnowledgePack BuildKnowledgePack(int collectionId, int? year, string appVersion)
    {
        var pack=new TrvKnowledgePack();
        using var connection = OpenConnection();
        using (var repair = connection.BeginTransaction())
        {
            RepairDuplicateMoments(connection, repair);
            repair.Commit();
        }
        using var nameCommand=connection.CreateCommand(); nameCommand.CommandText="SELECT name FROM collections WHERE id=$id"; nameCommand.Parameters.AddWithValue("$id",collectionId);
        var show=Convert.ToString(nameCommand.ExecuteScalar())??"Unknown show";
        using var command=connection.CreateCommand();
        command.CommandText="""
        SELECT e.id,COALESCE(e.broadcast_uid,''),e.air_date,COALESCE(e.broadcast_slot,''),COALESCE(e.part_number,1),e.total_parts,
          COALESCE(e.title,''),COALESCE(e.description,''),COALESCE(e.edition,''),COALESCE(e.archive_notes,''),
          COALESCE(e.broadcast_variant,''),COALESCE(e.broadcast_era,''),COALESCE(e.episode_type,''),
          COALESCE(e.hosts,''),COALESCE(e.callers,''),COALESCE(e.mentioned_people,''),
          COALESCE(e.metadata_confidence,0),COALESCE(e.metadata_confidence_reason,''),COALESCE(e.research_sources,'')
        FROM episodes e WHERE e.collection_id=$collection AND ($year IS NULL OR CAST(strftime('%Y',e.air_date) AS INTEGER)=$year) AND COALESCE(e.hidden,0)=0 ORDER BY e.air_date,e.part_number
        """;
        command.Parameters.AddWithValue("$collection",collectionId); command.Parameters.AddWithValue("$year",year is null?DBNull.Value:year.Value);
        var rows = new List<(long Id,string Uid,string? Date,string Slot,int Part,int? Total,string Headline,string Summary,string Edition,string ArchiveNotes,string Variant,string Era,string EpisodeType,string Hosts,string Callers,string Mentioned,int Confidence,string ConfidenceReason,string Sources)>();
        using (var r=command.ExecuteReader())
        {
            while(r.Read()) rows.Add((r.GetInt64(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.GetString(3),r.GetInt32(4),r.IsDBNull(5)?null:r.GetInt32(5),r.GetString(6),r.GetString(7),r.GetString(8),r.GetString(9),r.GetString(10),r.GetString(11),r.GetString(12),r.GetString(13),r.GetString(14),r.GetString(15),r.GetInt32(16),r.GetString(17),r.GetString(18)));
        }
        foreach (var row in rows)
        {
            var preservedResearch=TryDeserializeResolvedResearchForEpisode(row.Id);
            var sourceRecords=(preservedResearch?.Sources??new List<TrvPackSource>())
                .Where(x=>!string.IsNullOrWhiteSpace(x.Url))
                .GroupBy(x=>x.Url,StringComparer.OrdinalIgnoreCase)
                .Select(x=>x.First())
                .ToList();
            foreach(var url in SplitLines(row.Sources))
                if(!sourceRecords.Any(x=>string.Equals(x.Url,url,StringComparison.OrdinalIgnoreCase)))
                    sourceRecords.Add(new TrvPackSource { Url=url, Title=url });

            pack.Broadcasts.Add(new TrvPackBroadcast
            {
                BroadcastId=row.Uid, Show=show, BroadcastDate=row.Date, Slot=NullIfEmpty(row.Slot), PartNumber=row.Part, TotalParts=row.Total,
                Research=new TrvPackResearch
                {
                    Headline=NullIfEmpty(row.Headline), Summary=NullIfEmpty(row.Summary), ArchiveNotes=NullIfEmpty(row.ArchiveNotes),
                    Broadcast=new TrvPackBroadcastMetadata { Station=NullIfEmpty(row.Edition), Slot=NullIfEmpty(row.Slot), Variant=NullIfEmpty(row.Variant), Era=NullIfEmpty(row.Era), EpisodeType=NullIfEmpty(row.EpisodeType) },
                    People=new TrvPackPeople { Hosts=SplitPipe(row.Hosts).ToList(), Guests=ReadNames(connection,row.Id,"episode_guests","guests","guest_id"), Callers=SplitPipe(row.Callers).ToList(), MentionedPeople=SplitPipe(row.Mentioned).ToList() },
                    Topics=ReadNames(connection,row.Id,"episode_tags","tags","tag_id"),
                    Quality=new TrvPackResearchQuality { Confidence=row.Confidence, ConfidenceReason=NullIfEmpty(row.ConfidenceReason) },
                    Catalogue=preservedResearch?.Research?.Catalogue??new TrvPackCatalogueMetadata(),
                    Moments=GetMetadataMoments(connection,row.Id).Select(x=>new TrvPackMoment
                    {
                        TimestampSeconds=Math.Max(0,x.PositionMs/1000),
                        Title=x.Title,
                        Description=NullIfEmpty(x.Notes)
                    }).ToList()
                },
                Sources=sourceRecords,
                ImportPolicy=preservedResearch?.ImportPolicy??new TrvPackImportPolicy()
            });
        }
        foreach (var missing in GetMissingBroadcastResearch(show, year, includeResolved: false))
        {
            var item = TryDeserializeMissingBroadcast(missing.Id);
            if (item is not null) pack.MissingBroadcasts.Add(item);
        }
        pack.Manifest=new TrvPackManifest
        {
            SchemaVersion=5,
            AppVersion=appVersion,
            Show=show,
            Year=year,
            BroadcastCount=pack.Broadcasts.Count,
            MissingBroadcastCount=pack.MissingBroadcasts.Count
        };
        return pack;
    }

    public TrvKnowledgePack BuildCompleteKnowledgePack(string appVersion)
    {
        var pack = new TrvKnowledgePack
        {
            Manifest = new TrvPackManifest
            {
                SchemaVersion = 5,
                AppVersion = appVersion,
                Show = "Whole archive",
                Year = null,
                Purpose = "Complete Radio Vault knowledge database"
            }
        };

        foreach (var collection in GetCollections())
        {
            var collectionPack = BuildKnowledgePack(collection.Id, null, appVersion);
            pack.Broadcasts.AddRange(collectionPack.Broadcasts);
            pack.MissingBroadcasts.AddRange(collectionPack.MissingBroadcasts);
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id,COALESCE(e.broadcast_uid,''),c.name,e.air_date,COALESCE(e.part_number,1),
                   t.status,t.language,t.engine_id,t.model_id,t.full_text,t.has_speaker_diarization
              FROM transcripts t
              JOIN episodes e ON e.id=t.episode_id
              JOIN collections c ON c.id=e.collection_id
             WHERE COALESCE(e.hidden,0)=0
             ORDER BY c.sort_name,e.air_date,e.part_number,t.id;
            """;
        var transcripts = new List<(long Id, TrvPackTranscript Transcript)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                transcripts.Add((reader.GetInt64(0), new TrvPackTranscript
                {
                    BroadcastId = reader.GetString(1),
                    Show = reader.GetString(2),
                    BroadcastDate = reader.IsDBNull(3) ? null : reader.GetString(3),
                    PartNumber = Math.Max(1, reader.GetInt32(4)),
                    Status = reader.GetString(5),
                    Language = reader.GetString(6),
                    Engine = reader.GetString(7),
                    Model = reader.GetString(8),
                    FullText = reader.GetString(9),
                    HasSpeakerDiarization = reader.GetInt32(10) == 1
                }));
            }
        }

        foreach (var item in transcripts)
        {
            using var segments = connection.CreateCommand();
            segments.CommandText = """
                SELECT segment_index,start_ms,end_ms,COALESCE(speaker,''),COALESCE(speaker_key,''),text
                  FROM transcript_segments
                 WHERE transcript_id=$transcript
                 ORDER BY segment_index;
                """;
            segments.Parameters.AddWithValue("$transcript", item.Id);
            using var reader = segments.ExecuteReader();
            while (reader.Read())
            {
                item.Transcript.Segments.Add(new TrvPackTranscriptSegment
                {
                    Index = reader.GetInt32(0),
                    StartMs = reader.GetInt64(1),
                    EndMs = reader.GetInt64(2),
                    Speaker = reader.GetString(3),
                    SpeakerKey = reader.GetString(4),
                    Text = reader.GetString(5)
                });
            }
            pack.Transcripts.Add(item.Transcript);
        }

        pack.Manifest.BroadcastCount = pack.Broadcasts.Count;
        pack.Manifest.MissingBroadcastCount = pack.MissingBroadcasts.Count;
        pack.Manifest.TranscriptCount = pack.Transcripts.Count;
        return pack;
    }

    private static string? NullIfEmpty(string value)=>string.IsNullOrWhiteSpace(value)?null:value;

    public KnowledgePackImportResult ImportKnowledgePack(
        TrvKnowledgePack pack,
        string? sourcePath = null,
        IProgress<ResearchPackOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => ImportKnowledgePackWithResearchLibrary(pack, sourcePath, progress, cancellationToken);


    private void ApplyKnowledgePackBroadcast(long episodeId, TrvPackBroadcast item, bool protectUserEdits = false)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var existing = GetRichEpisodeMetadata(connection, transaction, episodeId);
        var plan = BuildKnowledgePackMergePlan(
            existing,
            item,
            protectUserEdits,
            ReadEpisodeResearchSources(connection, episodeId, transaction),
            ReadEpisodeMomentKeys(connection, transaction, episodeId));
        ApplyKnowledgePackBroadcast(connection, transaction, episodeId, item, plan);
        transaction.Commit();
    }

    private void ApplyKnowledgePackBroadcast(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long episodeId,
        TrvPackBroadcast item,
        KnowledgePackMergePlan plan)
    {
        item.ImportPolicy ??= new TrvPackImportPolicy();
        item.Sources ??= new List<TrvPackSource>();
        var research = item.Research ?? new TrvPackResearch();
        research.Broadcast ??= new TrvPackBroadcastMetadata();
        research.People ??= new TrvPackPeople();
        research.People.Hosts ??= new List<string>();
        research.People.Guests ??= new List<string>();
        research.People.Callers ??= new List<string>();
        research.People.MentionedPeople ??= new List<string>();
        research.Quality ??= new TrvPackResearchQuality();
        research.Guests ??= new List<string>();
        research.Topics ??= new List<string>();
        research.Moments ??= new List<TrvPackMoment>();

        var hostNames = plan.Hosts;
        var guestNames = plan.Guests;
        var callerNames = plan.Callers;
        var mentionedNames = plan.MentionedPeople;
        var topicNames = plan.Topics;
        var sourceUrls = plan.SourceUrls;

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE episodes SET
                  title=$headline,
                  description=$summary,
                  archive_notes=$archive,
                  edition=$station,
                  hosts=$hosts,
                  callers=$callers,
                  mentioned_people=$mentioned,
                  broadcast_slot=CASE WHEN $authoritative=1 THEN $slot WHEN $protectStructured=1 THEN broadcast_slot ELSE COALESCE(NULLIF($slot,''),broadcast_slot) END,
                  broadcast_variant=CASE WHEN $authoritative=1 THEN $variant WHEN $protectStructured=1 THEN broadcast_variant ELSE COALESCE(NULLIF($variant,''),broadcast_variant) END,
                  broadcast_era=CASE WHEN $authoritative=1 THEN $era WHEN $protectStructured=1 THEN broadcast_era ELSE COALESCE(NULLIF($era,''),broadcast_era) END,
                  episode_type=CASE WHEN $authoritative=1 THEN $type WHEN $protectStructured=1 THEN episode_type ELSE COALESCE(NULLIF($type,''),episode_type) END,
                  metadata_confidence=CASE WHEN $authoritative=1 THEN $confidence ELSE MAX(metadata_confidence,$confidence) END,
                  metadata_confidence_reason=CASE WHEN $authoritative=1 THEN $reason WHEN $confidence>=metadata_confidence AND $reason<>'' THEN $reason ELSE metadata_confidence_reason END,
                  research_sources=CASE WHEN $authoritative=1 THEN $sources WHEN $sources<>'' THEN $sources ELSE research_sources END,
                  updated_at=$now
                WHERE id=$id
                """;
            update.Parameters.AddWithValue("$headline", plan.Headline);
            update.Parameters.AddWithValue("$summary", plan.Summary);
            update.Parameters.AddWithValue("$archive", plan.ArchiveNotes);
            update.Parameters.AddWithValue("$station", plan.Station);
            update.Parameters.AddWithValue("$hosts", string.Join("|", NormalizeNames(hostNames)));
            update.Parameters.AddWithValue("$callers", string.Join("|", NormalizeNames(callerNames)));
            update.Parameters.AddWithValue("$mentioned", string.Join("|", NormalizeNames(mentionedNames)));
            update.Parameters.AddWithValue("$authoritative", item.ImportPolicy.AuthoritativeAudit ? 1 : 0);
            update.Parameters.AddWithValue("$protectStructured", plan.ProtectStructuredScalars ? 1 : 0);
            update.Parameters.AddWithValue("$slot", research.Broadcast.Slot ?? item.Slot ?? "");
            update.Parameters.AddWithValue("$variant", research.Broadcast.Variant ?? "");
            update.Parameters.AddWithValue("$era", research.Broadcast.Era ?? "");
            update.Parameters.AddWithValue("$type", research.Broadcast.EpisodeType ?? "");
            update.Parameters.AddWithValue("$confidence", Math.Clamp(research.Quality.Confidence, 0, 100));
            update.Parameters.AddWithValue("$reason", research.Quality.ConfidenceReason ?? "");
            update.Parameters.AddWithValue("$sources", string.Join("\n", sourceUrls));
            update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$id", episodeId);
            update.ExecuteNonQuery();
        }

        ReplaceNames(connection, transaction, episodeId, "guests", "episode_guests", "guest_id", guestNames);
        ReplaceNames(connection, transaction, episodeId, "tags", "episode_tags", "tag_id", topicNames);

        if (item.ImportPolicy.MergeMoments && research.Moments.Count > 0)
        {
            foreach (var moment in research.Moments.Where(x => x.TimestampSeconds >= 0 && !string.IsNullOrWhiteSpace(x.Title)))
                AddMomentIfMissing(connection, transaction, episodeId, moment.TimestampSeconds * 1000L, moment.Title, moment.Description ?? "");
        }
    }

    private static IEnumerable<string> ReadEpisodeResearchSources(SqliteConnection connection,long episodeId, SqliteTransaction? transaction = null)
    {
        using var command=connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText="SELECT COALESCE(research_sources,'') FROM episodes WHERE id=$id";
        command.Parameters.AddWithValue("$id",episodeId);
        return SplitLines(Convert.ToString(command.ExecuteScalar())??"");
    }

    private static IReadOnlyList<string> ReadEpisodeMomentKeys(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long episodeId)
    {
        var result = new List<string>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT position_ms,title FROM moments WHERE episode_id=$id ORDER BY position_ms,title";
        command.Parameters.AddWithValue("$id", episodeId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add($"{Math.Max(0, reader.GetInt64(0) / 1000)}:{reader.GetString(1).Trim()}");
        return result;
    }

    private static List<string> NormalizeNames(IEnumerable<string>? names) =>
        (names??Array.Empty<string>())
            .Where(x=>!string.IsNullOrWhiteSpace(x))
            .Select(x=>x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> MergeNames(IEnumerable<string>? existing,IEnumerable<string>? incoming) =>
        NormalizeNames((existing??Array.Empty<string>()).Concat(incoming??Array.Empty<string>()));

    private static void PrepareKnowledgePackItem(
        SqliteConnection connection,
        TrvPackBroadcast item,
        string manifestShow,
        IReadOnlyDictionary<string, string>? canonicalShowMap = null)
    {
        item.Show = string.IsNullOrWhiteSpace(item.Show) ? (manifestShow ?? "").Trim() : item.Show.Trim();
        if (!string.IsNullOrWhiteSpace(item.Show))
        {
            if (canonicalShowMap is not null && canonicalShowMap.TryGetValue(item.Show, out var mappedShow))
            {
                item.Show = mappedShow;
            }
            else
            {
                using var canonical = connection.CreateCommand();
                canonical.CommandText = """
                    SELECT c.name FROM collections c
                    LEFT JOIN collection_aliases ca ON ca.collection_id=c.id
                    WHERE lower(c.name)=lower($show) OR lower(ca.alias)=lower($show)
                    ORDER BY CASE WHEN lower(c.name)=lower($show) THEN 0 ELSE 1 END
                    LIMIT 1
                    """;
                canonical.Parameters.AddWithValue("$show", item.Show);
                var canonicalShow = Convert.ToString(canonical.ExecuteScalar());
                if (!string.IsNullOrWhiteSpace(canonicalShow)) item.Show = canonicalShow;
            }
        }
        item.PartNumber = Math.Max(1, item.PartNumber);
        item.Research ??= new TrvPackResearch();
        item.Research.Broadcast ??= new TrvPackBroadcastMetadata();
        item.Research.People ??= new TrvPackPeople();
        item.Research.Quality ??= new TrvPackResearchQuality();
        item.Research.Guests ??= new List<string>();
        item.Research.Topics ??= new List<string>();
        item.Research.Moments ??= new List<TrvPackMoment>();
        item.Sources ??= new List<TrvPackSource>();
        item.ImportPolicy ??= new TrvPackImportPolicy();

        var effectiveSlot = GetKnowledgePackSlot(item);
        item.Slot = string.IsNullOrWhiteSpace(effectiveSlot) ? null : effectiveSlot;
        if (string.IsNullOrWhiteSpace(item.Research.Broadcast.Slot))
            item.Research.Broadcast.Slot = item.Slot;

        if (!string.IsNullOrWhiteSpace(item.BroadcastDate) && DateTime.TryParse(item.BroadcastDate, out var parsedDate))
            item.BroadcastDate = parsedDate.ToString("yyyy-MM-dd");

        if (string.IsNullOrWhiteSpace(item.BroadcastId) &&
            DateOnly.TryParse(item.BroadcastDate, out var airDate) &&
            !string.IsNullOrWhiteSpace(item.Show))
        {
            item.BroadcastId = BroadcastIdentityService.CreateStableId(item.Show, airDate, item.PartNumber, item.Slot);
        }
    }

    private static string? GetKnowledgePackSlot(TrvPackBroadcast item)
        => !string.IsNullOrWhiteSpace(item.Slot)
            ? item.Slot.Trim()
            : item.Research?.Broadcast?.Slot?.Trim();

    private static IReadOnlyList<long> FindKnowledgePackMatches(SqliteConnection connection, TrvPackBroadcast item)
        => FindKnowledgePackMatches(connection, null, item);

    private sealed class KnowledgePackMatchIndex
    {
        public Dictionary<string, List<long>> ByBroadcastUid { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<long>> ByIdentity { get; } = new(StringComparer.Ordinal);
    }

    private static KnowledgePackMatchIndex BuildKnowledgePackMatchIndex(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        var index = new KnowledgePackMatchIndex();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT e.id,COALESCE(e.broadcast_uid,''),c.name,COALESCE(e.air_date,''),
                   COALESCE(e.broadcast_slot,''),COALESCE(e.part_number,1)
            FROM episodes e
            JOIN collections c ON c.id=e.collection_id
            WHERE EXISTS(
                SELECT 1 FROM media_files mf
                WHERE mf.episode_id=e.id AND mf.is_missing=0)
            ORDER BY e.id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var episodeId = reader.GetInt64(0);
            var uid = reader.GetString(1).Trim();
            if (uid.Length > 0) AddKnowledgePackMatch(index.ByBroadcastUid, uid, episodeId);

            var date = reader.GetString(3).Trim();
            if (date.Length == 0) continue;
            var identity = BuildKnowledgePackMatchIdentity(
                reader.GetString(2), date, reader.GetString(4), reader.GetInt32(5));
            AddKnowledgePackMatch(index.ByIdentity, identity, episodeId);
        }
        return index;
    }

    private static IReadOnlyList<long> FindKnowledgePackMatches(KnowledgePackMatchIndex index, TrvPackBroadcast item)
    {
        if (!string.IsNullOrWhiteSpace(item.BroadcastId) &&
            index.ByBroadcastUid.TryGetValue(item.BroadcastId.Trim(), out var uidMatches))
            return uidMatches;

        if (string.IsNullOrWhiteSpace(item.Show) || string.IsNullOrWhiteSpace(item.BroadcastDate))
            return Array.Empty<long>();
        var identity = BuildKnowledgePackMatchIdentity(
            item.Show, item.BroadcastDate, GetKnowledgePackSlot(item), Math.Max(1, item.PartNumber));
        return index.ByIdentity.TryGetValue(identity, out var identityMatches)
            ? identityMatches
            : Array.Empty<long>();
    }

    private static void AddKnowledgePackMatch(Dictionary<string, List<long>> target, string key, long episodeId)
    {
        if (!target.TryGetValue(key, out var matches))
        {
            matches = new List<long>(1);
            target.Add(key, matches);
        }
        if (matches.Count < 3) matches.Add(episodeId);
    }

    private static string BuildKnowledgePackMatchIdentity(string show, string date, string? slot, int partNumber)
        => string.Join('\u001f', show.Trim().ToUpperInvariant(), date.Trim(), slot?.Trim() ?? string.Empty,
            Math.Max(1, partNumber).ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static IReadOnlyList<long> FindKnowledgePackMatches(SqliteConnection connection, SqliteTransaction? transaction, TrvPackBroadcast item)
    {
        var matches = new List<long>();
        if (!string.IsNullOrWhiteSpace(item.BroadcastId))
        {
            using var byUid = connection.CreateCommand();
            byUid.Transaction = transaction;
            byUid.CommandText = """
                SELECT e.id FROM episodes e
                WHERE e.broadcast_uid=$uid
                  AND EXISTS(SELECT 1 FROM media_files mf WHERE mf.episode_id=e.id AND mf.is_missing=0)
                ORDER BY e.id LIMIT 3
                """;
            byUid.Parameters.AddWithValue("$uid", item.BroadcastId.Trim());
            using var reader = byUid.ExecuteReader();
            while (reader.Read()) matches.Add(reader.GetInt64(0));
            if (matches.Count > 0) return matches;
        }

        if (string.IsNullOrWhiteSpace(item.Show) || string.IsNullOrWhiteSpace(item.BroadcastDate))
            return matches;

        using var byIdentity = connection.CreateCommand();
        byIdentity.Transaction = transaction;
        byIdentity.CommandText = """
            SELECT e.id FROM episodes e
            JOIN collections c ON c.id=e.collection_id
            WHERE e.air_date=$date
              AND COALESCE(e.broadcast_slot,'')=COALESCE($slot,'')
              AND COALESCE(e.part_number,1)=$part
              AND (
                    lower(c.name)=lower($show)
                    OR EXISTS(
                        SELECT 1 FROM collection_aliases ca
                        WHERE ca.collection_id=c.id AND lower(ca.alias)=lower($show)
                    )
                  )
              AND EXISTS(SELECT 1 FROM media_files mf WHERE mf.episode_id=e.id AND mf.is_missing=0)
            ORDER BY e.id LIMIT 3
            """;
        byIdentity.Parameters.AddWithValue("$show", item.Show.Trim());
        byIdentity.Parameters.AddWithValue("$date", item.BroadcastDate.Trim());
        var effectiveSlot = GetKnowledgePackSlot(item);
        byIdentity.Parameters.AddWithValue("$slot", string.IsNullOrWhiteSpace(effectiveSlot) ? DBNull.Value : effectiveSlot);
        byIdentity.Parameters.AddWithValue("$part", Math.Max(1, item.PartNumber));
        using var identityReader = byIdentity.ExecuteReader();
        while (identityReader.Read()) matches.Add(identityReader.GetInt64(0));
        return matches;
    }

    private static void UpsertMissingBroadcastResearch(SqliteConnection connection, TrvPackBroadcast item, string status, string notes)
        => UpsertMissingBroadcastResearch(connection, null, item, status, notes);

    private static void UpsertMissingBroadcastResearch(SqliteConnection connection, SqliteTransaction? transaction, TrvPackBroadcast item, string status, string notes)
    {
        var json = KnowledgePackService.SerializeBroadcast(item);
        var stableKey = BuildMissingResearchStableKey(item, json);
        var effectiveSlot = GetKnowledgePackSlot(item) ?? "";
        var research = item.Research ?? new TrvPackResearch();
        research.Quality ??= new TrvPackResearchQuality();
        var now = DateTime.UtcNow.ToString("O");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO missing_broadcast_research(
                stable_key,broadcast_uid,show_name,normalized_show_name,broadcast_date,
                slot,normalized_slot,part_number,total_parts,headline,summary,confidence,
                confidence_reason,research_json,status,matched_episode_id,match_notes,
                created_at,updated_at,resolved_at)
            VALUES($key,$uid,$show,$normalizedShow,$date,$slot,$normalizedSlot,$part,$total,
                   $headline,$summary,$confidence,$reason,$json,$status,NULL,$notes,$now,$now,NULL)
            ON CONFLICT(stable_key) DO UPDATE SET
                broadcast_uid=excluded.broadcast_uid,
                show_name=excluded.show_name,
                normalized_show_name=excluded.normalized_show_name,
                broadcast_date=excluded.broadcast_date,
                slot=excluded.slot,
                normalized_slot=excluded.normalized_slot,
                part_number=excluded.part_number,
                total_parts=excluded.total_parts,
                headline=excluded.headline,
                summary=excluded.summary,
                confidence=excluded.confidence,
                confidence_reason=excluded.confidence_reason,
                research_json=excluded.research_json,
                status=excluded.status,
                matched_episode_id=NULL,
                match_notes=excluded.match_notes,
                updated_at=excluded.updated_at,
                resolved_at=NULL
            """;
        command.Parameters.AddWithValue("$key", stableKey);
        command.Parameters.AddWithValue("$uid", item.BroadcastId?.Trim() ?? "");
        command.Parameters.AddWithValue("$show", item.Show?.Trim() ?? "Unknown show");
        command.Parameters.AddWithValue("$normalizedShow", NormalizeResearchKeyPart(item.Show));
        command.Parameters.AddWithValue("$date", string.IsNullOrWhiteSpace(item.BroadcastDate) ? DBNull.Value : item.BroadcastDate.Trim());
        command.Parameters.AddWithValue("$slot", effectiveSlot);
        command.Parameters.AddWithValue("$normalizedSlot", NormalizeResearchKeyPart(effectiveSlot));
        command.Parameters.AddWithValue("$part", Math.Max(1, item.PartNumber));
        command.Parameters.AddWithValue("$total", item.TotalParts.HasValue ? item.TotalParts.Value : DBNull.Value);
        command.Parameters.AddWithValue("$headline", research.Headline?.Trim() ?? "");
        command.Parameters.AddWithValue("$summary", research.Summary?.Trim() ?? "");
        command.Parameters.AddWithValue("$confidence", Math.Clamp(research.Quality.Confidence, 0, 100));
        command.Parameters.AddWithValue("$reason", research.Quality.ConfidenceReason?.Trim() ?? "");
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private static int MarkMissingBroadcastResearchResolved(SqliteConnection connection, TrvPackBroadcast item, long episodeId, string notes)
        => MarkMissingBroadcastResearchResolved(connection, null, item, episodeId, notes);

    private static int MarkMissingBroadcastResearchResolved(SqliteConnection connection, SqliteTransaction? transaction, TrvPackBroadcast item, long episodeId, string notes)
    {
        var stableKey = BuildMissingResearchStableKey(item, KnowledgePackService.SerializeBroadcast(item));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE missing_broadcast_research SET
                status='resolved',matched_episode_id=$episode,match_notes=$notes,
                updated_at=$now,resolved_at=$now
            WHERE stable_key=$key AND status<>'resolved'
            """;
        command.Parameters.AddWithValue("$episode", episodeId);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$key", stableKey);
        return command.ExecuteNonQuery();
    }

    private static string BuildMissingResearchStableKey(TrvPackBroadcast item, string json)
    {
        var show = NormalizeResearchKeyPart(item.Show);
        var slot = NormalizeResearchKeyPart(GetKnowledgePackSlot(item));
        var part = Math.Max(1, item.PartNumber);
        string identity;
        if (!string.IsNullOrWhiteSpace(item.BroadcastDate))
            identity = $"{show}|{item.BroadcastDate.Trim()}|{slot}|{part}|{NormalizeResearchKeyPart(item.BroadcastId)}";
        else if (!string.IsNullOrWhiteSpace(item.BroadcastId))
            identity = $"{show}|uid|{NormalizeResearchKeyPart(item.BroadcastId)}";
        else
            identity = $"{show}|undated|{slot}|{part}|{json}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static string NormalizeResearchKeyPart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var expanded = value.Replace("&", " and ", StringComparison.Ordinal);
        var builder = new StringBuilder(expanded.Length);
        var previousSpace = false;
        foreach (var character in expanded.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousSpace = false;
            }
            else if (!previousSpace && builder.Length > 0)
            {
                builder.Append(' ');
                previousSpace = true;
            }
        }
        var normalized = builder.ToString().Trim();
        return normalized.StartsWith("the ", StringComparison.Ordinal) ? normalized[4..] : normalized;
    }

    public IReadOnlyList<MissingBroadcastResearchRecord> GetMissingBroadcastResearch(string? show = null, int? year = null, bool includeResolved = false)
    {
        var result = new List<MissingBroadcastResearchRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,broadcast_uid,show_name,broadcast_date,slot,part_number,total_parts,
                   headline,summary,confidence,confidence_reason,status,matched_episode_id,
                   match_notes,updated_at
            FROM missing_broadcast_research
            WHERE ($show='' OR normalized_show_name=$show)
              AND ($year IS NULL OR CAST(substr(broadcast_date,1,4) AS INTEGER)=$year)
              AND ($includeResolved=1 OR status IN ('pending','ambiguous'))
            ORDER BY normalized_show_name,broadcast_date,normalized_slot,part_number,id
            """;
        command.Parameters.AddWithValue("$show", NormalizeResearchKeyPart(show));
        command.Parameters.AddWithValue("$year", year.HasValue ? year.Value : DBNull.Value);
        command.Parameters.AddWithValue("$includeResolved", includeResolved ? 1 : 0);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new MissingBroadcastResearchRecord
            {
                Id=reader.GetInt64(0),
                BroadcastId=reader.GetString(1),
                Show=reader.GetString(2),
                BroadcastDate=reader.IsDBNull(3)?null:DateTime.Parse(reader.GetString(3)),
                Slot=reader.GetString(4),
                PartNumber=reader.GetInt32(5),
                TotalParts=reader.IsDBNull(6)?null:reader.GetInt32(6),
                Headline=reader.GetString(7),
                Summary=reader.GetString(8),
                Confidence=reader.GetInt32(9),
                ConfidenceReason=reader.GetString(10),
                Status=reader.GetString(11),
                MatchedEpisodeId=reader.IsDBNull(12)?null:reader.GetInt64(12),
                MatchNotes=reader.GetString(13),
                UpdatedAt=DateTime.Parse(reader.GetString(14))
            });
        }
        return result;
    }

    public MissingResearchSummary GetMissingResearchSummary()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              SUM(CASE WHEN status='pending' THEN 1 ELSE 0 END),
              SUM(CASE WHEN status='ambiguous' THEN 1 ELSE 0 END),
              SUM(CASE WHEN status='resolved' THEN 1 ELSE 0 END),
              SUM(CASE WHEN status='ignored' THEN 1 ELSE 0 END)
            FROM missing_broadcast_research
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new MissingResearchSummary();
        return new MissingResearchSummary
        {
            Pending=reader.IsDBNull(0)?0:reader.GetInt32(0),
            Ambiguous=reader.IsDBNull(1)?0:reader.GetInt32(1),
            Resolved=reader.IsDBNull(2)?0:reader.GetInt32(2),
            Ignored=reader.IsDBNull(3)?0:reader.GetInt32(3)
        };
    }

    public MissingBroadcastResearchDetails? GetMissingBroadcastResearchDetails(long id)
    {
        var record = GetMissingBroadcastResearch(includeResolved: true)
            .FirstOrDefault(x => x.Id == id);
        if (record is null) return null;

        var item = TryDeserializeMissingBroadcast(id);
        if (item is null) return null;

        item.Research ??= new TrvPackResearch();
        item.Research.People ??= new TrvPackPeople();
        item.Research.Topics ??= new List<string>();
        item.Research.Guests ??= new List<string>();
        item.Research.Moments ??= new List<TrvPackMoment>();
        item.Sources ??= new List<TrvPackSource>();

        var people = item.Research.People;
        people.Hosts ??= new List<string>();
        people.Guests ??= new List<string>();
        people.Callers ??= new List<string>();
        people.MentionedPeople ??= new List<string>();
        return new MissingBroadcastResearchDetails
        {
            Record = record,
            Broadcast = item,
            Hosts = NormalizeNames(people.Hosts),
            Guests = NormalizeNames(people.Guests.Concat(item.Research.Guests)),
            Callers = NormalizeNames(people.Callers),
            MentionedPeople = NormalizeNames(people.MentionedPeople),
            Topics = NormalizeNames(item.Research.Topics),
            Sources = item.Sources
                .Where(x => !string.IsNullOrWhiteSpace(x.Url) || !string.IsNullOrWhiteSpace(x.Title))
                .ToList(),
            Moments = item.Research.Moments
                .Where(x => !string.IsNullOrWhiteSpace(x.Title))
                .OrderBy(x => x.TimestampSeconds)
                .ToList()
        };
    }

    public void SetMissingBroadcastResearchStatus(long id, string status, string? notes = null)
    {
        var normalized = (status ?? "").Trim().ToLowerInvariant();
        if (normalized is not ("pending" or "ambiguous" or "ignored"))
            throw new ArgumentException("Status must be pending, ambiguous or ignored.", nameof(status));

        using var connection = OpenConnection();
        SetMissingResearchState(connection, id, normalized, null, notes ?? "Status changed in Research & Metadata.");
        SyncResearchStatusFromLegacy(connection, id, normalized);
    }

    public void AttachMissingBroadcastResearch(long researchId, long episodeId)
    {
        var item = TryDeserializeMissingBroadcast(researchId)
            ?? throw new InvalidOperationException("The saved research record could not be read.");

        using var connection = OpenConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*) FROM episodes e
                WHERE e.id=$episode
                  AND EXISTS(
                    SELECT 1 FROM media_files mf
                    WHERE mf.episode_id=e.id AND mf.is_missing=0
                  )
                """;
            command.Parameters.AddWithValue("$episode", episodeId);
            if (Convert.ToInt32(command.ExecuteScalar()) != 1)
                throw new InvalidOperationException("The selected archive broadcast is no longer available.");
        }

        PrepareKnowledgePackItem(connection, item, item.Show);
        var collectionId = ResolveResearchCollectionId(connection, item.Show);
        var before = GetRichEpisodeMetadata(episodeId);
        var durableResearchId = UpsertResearchLibraryRecord(
            connection,
            item,
            collectionId,
            episodeId: null,
            existenceStatus: DeriveExistenceStatus(item),
            needsReview: false,
            importRunId: null,
            legacyMissingResearchId: researchId);
        RecordScalarResearchConflicts(connection, durableResearchId, episodeId, item, before);

        ApplyKnowledgePackBroadcast(episodeId, item);
        AttachResearchRecord(connection, durableResearchId, episodeId);
        SetMissingResearchState(
            connection, researchId, "resolved", episodeId,
            "Manually attached from Broadcasts to find.");
    }

    private TrvPackBroadcast? TryDeserializeResolvedResearchForEpisode(long episodeId)
    {
        using var connection = OpenConnection();
        string? json;
        using (var durable = connection.CreateCommand())
        {
            durable.CommandText = """
                SELECT research_json FROM research_broadcasts
                WHERE episode_id=$episode
                ORDER BY confidence DESC,updated_at DESC,id DESC LIMIT 1
                """;
            durable.Parameters.AddWithValue("$episode", episodeId);
            json = durable.ExecuteScalar() as string;
        }
        if (string.IsNullOrWhiteSpace(json))
        {
            using var legacy = connection.CreateCommand();
            legacy.CommandText = """
                SELECT research_json FROM missing_broadcast_research
                WHERE status='resolved' AND matched_episode_id=$episode
                ORDER BY resolved_at DESC,id DESC LIMIT 1
                """;
            legacy.Parameters.AddWithValue("$episode", episodeId);
            json = legacy.ExecuteScalar() as string;
        }
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return KnowledgePackService.DeserializeBroadcast(json); }
        catch { return null; }
    }

    private TrvPackBroadcast? TryDeserializeMissingBroadcast(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT research_json FROM missing_broadcast_research WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        var json = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return KnowledgePackService.DeserializeBroadcast(json); }
        catch { return null; }
    }

    private void AddMomentIfMissing(long episodeId, long positionMs, string title, string notes)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        AddMomentIfMissing(connection, transaction, episodeId, positionMs, title, notes);
        transaction.Commit();
    }

    private static void AddMomentIfMissing(SqliteConnection connection, SqliteTransaction transaction, long episodeId, long positionMs, string title, string notes)
    {
        AddMomentIdempotent(
            connection,
            transaction,
            episodeId,
            positionMs,
            title,
            notes,
            DateTime.UtcNow.ToString("O"));
    }

    public MissingResearchReconciliationResult ReconcileMissingResearchForEpisode(long episodeId)
    {
        try
        {
            return ReconcileResearchLibraryForEpisode(episodeId);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Research reconciliation", $"Could not reconcile episode {episodeId}.", ex);
            return new MissingResearchReconciliationResult { Invalid = 1 };
        }
    }


    private static void SetMissingResearchState(SqliteConnection connection, long id, string status, long? episodeId, string notes)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE missing_broadcast_research SET
              status=$status,matched_episode_id=$episode,match_notes=$notes,updated_at=$now,
              resolved_at=CASE WHEN $status='resolved' THEN $now ELSE NULL END
            WHERE id=$id
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$episode", episodeId.HasValue ? episodeId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }


    public IReadOnlyList<GlobalSearchResult> SearchGlobal(string query, int? collectionId = null, string status = "All", bool favouritesOnly = false, DateTime? fromDate = null, DateTime? toDate = null, int limit = 150)
    {
        var terms = (query ?? "").Trim();
        if (terms.Length < 2) return Array.Empty<GlobalSearchResult>();
        var pattern = $"%{terms.Replace("%", "[%]").Replace("_", "[_]")}%";
        var results = new List<GlobalSearchResult>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
        WITH episode_search AS (
            SELECT e.id AS episode_id, NULL AS moment_id, NULL AS research_id, 0 AS position_ms,
                   c.name AS collection_name, e.air_date, COALESCE(e.title,'') AS headline,
                   CASE WHEN COALESCE(e.title,'')<>'' THEN e.title ELSE c.name END AS match_title,
                   trim(COALESCE(e.description,'') ||
                        CASE WHEN COALESCE(e.archive_notes,'')<>'' THEN '  '||e.archive_notes ELSE '' END ||
                        CASE WHEN COALESCE(e.notes,'')<>'' THEN '  '||e.notes ELSE '' END) AS excerpt,
                   COALESCE((SELECT group_concat(g.name, ', ') FROM episode_guests eg JOIN guests g ON g.id=eg.guest_id WHERE eg.episode_id=e.id),'') AS guests,
                   COALESCE((SELECT group_concat(t.name, ', ') FROM episode_tags et JOIN tags t ON t.id=et.tag_id WHERE et.episode_id=e.id),'') AS topics,
                   CASE WHEN COALESCE(ps.completed,0)=1 THEN 'Completed' WHEN COALESCE(ps.position_ms,0)>0 THEN 'In Progress' ELSE 'Unplayed' END AS status,
                   COALESCE(e.favourite,0) AS favourite, 0 AS kind
            FROM episodes e JOIN collections c ON c.id=e.collection_id
            LEFT JOIN playback_state ps ON ps.episode_id=e.id
            WHERE COALESCE(e.hidden,0)=0
              AND ($collection IS NULL OR e.collection_id=$collection)
              AND ($status='All' OR ($status='Completed' AND COALESCE(ps.completed,0)=1) OR ($status='In Progress' AND COALESCE(ps.completed,0)=0 AND COALESCE(ps.position_ms,0)>0) OR ($status='Unplayed' AND COALESCE(ps.completed,0)=0 AND COALESCE(ps.position_ms,0)=0))
              AND ($favourites=0 OR COALESCE(e.favourite,0)=1)
              AND ($fromDate IS NULL OR e.air_date >= $fromDate)
              AND ($toDate IS NULL OR e.air_date <= $toDate)
              AND lower(c.name || ' ' || COALESCE(e.title,'') || ' ' || COALESCE(e.description,'') || ' ' || COALESCE(e.archive_notes,'') || ' ' || COALESCE(e.notes,'') || ' ' || COALESCE(e.edition,'') || ' ' || COALESCE(e.broadcast_era,'') || ' ' || COALESCE(e.air_date,'') || ' ' || COALESCE(e.hosts,'') || ' ' || COALESCE(e.callers,'') || ' ' || COALESCE(e.mentioned_people,'') || ' ' || COALESCE((SELECT group_concat(g.name, ' ') FROM episode_guests eg JOIN guests g ON g.id=eg.guest_id WHERE eg.episode_id=e.id),'') || ' ' || COALESCE((SELECT group_concat(t.name, ' ') FROM episode_tags et JOIN tags t ON t.id=et.tag_id WHERE et.episode_id=e.id),'')) LIKE lower($pattern)
        ), moment_search AS (
            SELECT e.id AS episode_id, m.id AS moment_id, NULL AS research_id, m.position_ms,
                   c.name AS collection_name, e.air_date, COALESCE(e.title,'') AS headline,
                   m.title AS match_title, m.notes AS excerpt, '' AS guests, '' AS topics,
                   CASE WHEN COALESCE(ps.completed,0)=1 THEN 'Completed' WHEN COALESCE(ps.position_ms,0)>0 THEN 'In Progress' ELSE 'Unplayed' END AS status,
                   COALESCE(e.favourite,0) AS favourite, 1 AS kind
            FROM moments m JOIN episodes e ON e.id=m.episode_id JOIN collections c ON c.id=e.collection_id
            LEFT JOIN playback_state ps ON ps.episode_id=e.id
            WHERE COALESCE(e.hidden,0)=0
              AND ($collection IS NULL OR e.collection_id=$collection)
              AND ($status='All' OR ($status='Completed' AND COALESCE(ps.completed,0)=1) OR ($status='In Progress' AND COALESCE(ps.completed,0)=0 AND COALESCE(ps.position_ms,0)>0) OR ($status='Unplayed' AND COALESCE(ps.completed,0)=0 AND COALESCE(ps.position_ms,0)=0))
              AND ($favourites=0 OR COALESCE(e.favourite,0)=1)
              AND ($fromDate IS NULL OR e.air_date >= $fromDate)
              AND ($toDate IS NULL OR e.air_date <= $toDate)
              AND lower(m.title || ' ' || COALESCE(m.notes,'') || ' ' || c.name || ' ' || COALESCE(e.title,'')) LIKE lower($pattern)
        ), research_search AS (
            SELECT COALESCE(rb.episode_id,0) AS episode_id, NULL AS moment_id, rb.id AS research_id, 0 AS position_ms,
                   c.name AS collection_name, rb.air_date, rb.headline,
                   CASE WHEN trim(rb.headline)<>'' THEN rb.headline ELSE c.name || ' research record' END AS match_title,
                   trim(COALESCE(rb.summary,'') ||
                        CASE WHEN trim(COALESCE(rb.archive_notes,''))<>'' THEN '  '||rb.archive_notes ELSE '' END ||
                        CASE WHEN trim(COALESCE(rb.confidence_reason,''))<>'' THEN '  '||rb.confidence_reason ELSE '' END) AS excerpt,
                   COALESCE((SELECT group_concat(rp.name, ', ') FROM research_people rp WHERE rp.research_broadcast_id=rb.id AND rp.role='guest'),'') AS guests,
                   COALESCE((SELECT group_concat(rt.topic, ', ') FROM research_topics rt WHERE rt.research_broadcast_id=rb.id),'') AS topics,
                   CASE
                     WHEN EXISTS(SELECT 1 FROM research_conflicts rc WHERE rc.research_broadcast_id=rb.id AND rc.resolution='unresolved') THEN 'Research conflict'
                     WHEN rb.needs_review=1 THEN 'Needs your decision'
                     WHEN rb.episode_id IS NOT NULL THEN 'In library'
                     WHEN rb.existence_status='confirmed_missing' THEN 'Confirmed missing'
                     WHEN rb.existence_status='probable_missing' THEN 'Probable missing'
                     ELSE 'Unknown gap' END AS status,
                   COALESCE(e.favourite,0) AS favourite, 2 AS kind
            FROM research_broadcasts rb
            JOIN collections c ON c.id=rb.collection_id
            LEFT JOIN episodes e ON e.id=rb.episode_id
            LEFT JOIN playback_state ps ON ps.episode_id=rb.episode_id
            WHERE ($collection IS NULL OR rb.collection_id=$collection)
              AND ($status='All' OR (rb.episode_id IS NOT NULL AND (($status='Completed' AND COALESCE(ps.completed,0)=1) OR ($status='In Progress' AND COALESCE(ps.completed,0)=0 AND COALESCE(ps.position_ms,0)>0) OR ($status='Unplayed' AND COALESCE(ps.completed,0)=0 AND COALESCE(ps.position_ms,0)=0))))
              AND ($favourites=0 OR COALESCE(e.favourite,0)=1)
              AND ($fromDate IS NULL OR rb.air_date >= $fromDate)
              AND ($toDate IS NULL OR rb.air_date <= $toDate)
              AND lower(c.name || ' ' || COALESCE(rb.air_date,'') || ' ' || COALESCE(rb.headline,'') || ' ' || COALESCE(rb.summary,'') || ' ' || COALESCE(rb.station,'') || ' ' || COALESCE(rb.edition,'') || ' ' || COALESCE(rb.broadcast_variant,'') || ' ' || COALESCE(rb.broadcast_era,'') || ' ' || COALESCE(rb.episode_type,'') || ' ' || COALESCE(rb.archive_notes,'') || ' ' || COALESCE(rb.confidence_reason,'') || ' ' || COALESCE((SELECT group_concat(rp.name, ' ') FROM research_people rp WHERE rp.research_broadcast_id=rb.id),'') || ' ' || COALESCE((SELECT group_concat(rt.topic, ' ') FROM research_topics rt WHERE rt.research_broadcast_id=rb.id),'') || ' ' || COALESCE((SELECT group_concat(rs.publisher || ' ' || rs.title || ' ' || rs.url, ' ') FROM research_sources rs WHERE rs.research_broadcast_id=rb.id),'')) LIKE lower($pattern)
        )
        SELECT * FROM (
            SELECT * FROM episode_search
            UNION ALL SELECT * FROM moment_search
            UNION ALL SELECT * FROM research_search)
        ORDER BY CASE WHEN lower(match_title)=lower($query) THEN 0 WHEN lower(match_title) LIKE lower($prefix) THEN 1 ELSE 2 END,
                 air_date DESC,kind,match_title
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$pattern", pattern);
        command.Parameters.AddWithValue("$query", terms);
        command.Parameters.AddWithValue("$prefix", terms + "%");
        command.Parameters.AddWithValue("$collection", collectionId.HasValue ? collectionId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$status", string.IsNullOrWhiteSpace(status) ? "All" : status);
        command.Parameters.AddWithValue("$favourites", favouritesOnly ? 1 : 0);
        command.Parameters.AddWithValue("$fromDate", fromDate.HasValue ? fromDate.Value.ToString("yyyy-MM-dd") : DBNull.Value);
        command.Parameters.AddWithValue("$toDate", toDate.HasValue ? toDate.Value.ToString("yyyy-MM-dd") : DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var kind = reader.GetInt32(13) switch
            {
                1 => GlobalSearchResultKind.Moment,
                2 => GlobalSearchResultKind.Research,
                _ => GlobalSearchResultKind.Episode
            };
            results.Add(new GlobalSearchResult
            {
                EpisodeId = reader.GetInt64(0),
                MomentId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                ResearchBroadcastId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                PositionMs = reader.GetInt64(3),
                CollectionName = reader.GetString(4),
                AirDate = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5)),
                Headline = reader.GetString(6),
                MatchTitle = reader.GetString(7),
                MatchExcerpt = reader.GetString(8),
                Guests = reader.GetString(9),
                Topics = reader.GetString(10),
                Status = reader.GetString(11),
                Favourite = reader.GetInt32(12) == 1,
                Kind = kind
            });
        }
        var seenBroadcasts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var canonicalResults = new List<GlobalSearchResult>(results.Count);
        foreach (var result in results)
        {
            if (result.EpisodeId > 0)
            {
                var resolution = ResolveCanonicalEpisode(result.EpisodeId);
                if (resolution is not null)
                {
                    result.EpisodeId = resolution.RepresentativeEpisodeId;
                    if (result.Kind == GlobalSearchResultKind.Episode &&
                        !seenBroadcasts.Add(resolution.CanonicalKey))
                        continue;
                }
            }
            canonicalResults.Add(result);
            if (canonicalResults.Count >= Math.Clamp(limit, 1, 500)) break;
        }
        return canonicalResults;
    }

}
