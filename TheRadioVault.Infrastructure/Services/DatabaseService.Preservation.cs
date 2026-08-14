using System.Text.Json;
using Microsoft.Data.Sqlite;
using TheRadioVault.Models;

namespace TheRadioVault.Services;

public sealed partial class DatabaseService
{
    public PreservationSummary GetPreservationSummary()
    {
        using var connection = OpenConnection();

        DateTimeOffset? lastCompleted = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT completed_at FROM preservation_scan_runs WHERE status='completed' AND completed_at IS NOT NULL ORDER BY id DESC LIMIT 1";
            var value = Convert.ToString(command.ExecuteScalar());
            if (DateTimeOffset.TryParse(value, out var parsed)) lastCompleted = parsed;
        }

        var totalFiles = 0;
        var localFiles = 0;
        var missingEvidence = 0;
        var partialFingerprints = 0;
        var fullHashes = 0;
        var inspectionErrors = 0;
        using (var command = connection.CreateCommand())
        {
            // One aggregate pass replaces six separate whole-table scans.
            command.CommandText = """
                SELECT COUNT(*),
                       COALESCE(SUM(CASE WHEN is_missing=0 AND storage_state='AvailableOffline' THEN 1 ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN is_missing=0 AND storage_state='AvailableOffline'
                                             AND (duration_ms<=0 OR COALESCE(partial_hash,'')='') THEN 1 ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN COALESCE(partial_hash,'')<>'' THEN 1 ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN COALESCE(full_hash,'')<>'' THEN 1 ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN COALESCE(inspection_error,'')<>'' THEN 1 ELSE 0 END),0)
                  FROM media_files;
                """;
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                totalFiles = reader.GetInt32(0);
                localFiles = reader.GetInt32(1);
                missingEvidence = reader.GetInt32(2);
                partialFingerprints = reader.GetInt32(3);
                fullHashes = reader.GetInt32(4);
                inspectionErrors = reader.GetInt32(5);
            }
        }

        var strongDuplicateFilesAwaitingFullHash = 0;
        using (var command = connection.CreateCommand())
        {
            // Count members of duplicate-evidence groups once, rather than
            // running a correlated EXISTS probe for every media-file row.
            command.CommandText = """
                SELECT COALESCE(SUM(group_size),0)
                  FROM (
                        SELECT COUNT(*) AS group_size
                          FROM media_files
                         WHERE is_missing=0
                           AND storage_state='AvailableOffline'
                           AND COALESCE(partial_hash,'')<>''
                           AND COALESCE(full_hash,'')=''
                         GROUP BY file_size,partial_hash
                        HAVING COUNT(*)>1
                       );
                """;
            strongDuplicateFilesAwaitingFullHash = Convert.ToInt32(command.ExecuteScalar());
        }

        return new PreservationSummary
        {
            TotalFiles = totalFiles,
            LocalFiles = localFiles,
            MissingEvidence = missingEvidence,
            PartialFingerprints = partialFingerprints,
            FullHashes = fullHashes,
            StrongDuplicateFilesAwaitingFullHash = strongDuplicateFilesAwaitingFullHash,
            InspectionErrors = inspectionErrors,
            LastCompletedScanAt = lastCompleted
        };
    }

    public IReadOnlyList<PreservationFileCandidate> GetPreservationCandidates(PreservationScanOptions options)
    {
        var result = new List<PreservationFileCandidate>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = options.ReinspectAllLocalFiles
            ? """
                SELECT mf.id,mf.episode_id,mf.path,mf.original_filename,mf.file_size,
                       COALESCE(mf.duration_ms,0),COALESCE(mf.partial_hash,''),COALESCE(mf.full_hash,''),
                       COALESCE(mf.inspection_error,''),COALESCE(mf.is_preferred,0)
                  FROM media_files mf
                 WHERE mf.is_missing=0 AND mf.storage_state='AvailableOffline'
                 ORDER BY mf.id
                """
            : """
                SELECT mf.id,mf.episode_id,mf.path,mf.original_filename,mf.file_size,
                       COALESCE(mf.duration_ms,0),COALESCE(mf.partial_hash,''),COALESCE(mf.full_hash,''),
                       COALESCE(mf.inspection_error,''),COALESCE(mf.is_preferred,0)
                  FROM media_files mf
                 WHERE mf.is_missing=0 AND mf.storage_state='AvailableOffline'
                   AND (
                       mf.duration_ms<=0 OR COALESCE(mf.partial_hash,'')=''
                       OR ($retry=1 AND COALESCE(mf.inspection_error,'')<>'')
                   )
                 ORDER BY mf.id
                """;
        command.Parameters.AddWithValue("$retry", options.RetryPreviousErrors ? 1 : 0);
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(ReadPreservationCandidate(reader));
        return result;
    }

    public IReadOnlyList<PreservationFileCandidate> GetStrongDuplicateFullHashCandidates()
    {
        var result = new List<PreservationFileCandidate>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mf.id,mf.episode_id,mf.path,mf.original_filename,mf.file_size,
                   COALESCE(mf.duration_ms,0),COALESCE(mf.partial_hash,''),COALESCE(mf.full_hash,''),
                   COALESCE(mf.inspection_error,''),COALESCE(mf.is_preferred,0)
              FROM media_files mf
             WHERE mf.is_missing=0 AND mf.storage_state='AvailableOffline'
               AND COALESCE(mf.partial_hash,'')<>'' AND COALESCE(mf.full_hash,'')=''
               AND EXISTS(
                   SELECT 1 FROM media_files other
                    WHERE other.id<>mf.id AND other.is_missing=0
                      AND other.file_size=mf.file_size
                      AND other.partial_hash=mf.partial_hash)
             ORDER BY mf.file_size,mf.partial_hash,mf.id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(ReadPreservationCandidate(reader));
        return result;
    }

    private static PreservationFileCandidate ReadPreservationCandidate(SqliteDataReader reader)
        => new()
        {
            MediaFileId = reader.GetInt64(0),
            EpisodeId = reader.GetInt64(1),
            Path = reader.GetString(2),
            Filename = reader.GetString(3),
            FileSize = reader.GetInt64(4),
            DurationMs = reader.GetInt64(5),
            PartialHash = reader.GetString(6),
            FullHash = reader.GetString(7),
            InspectionError = reader.GetString(8),
            IsPreferred = reader.GetInt64(9) != 0
        };

    public void UpdatePreservationEvidence(
        long mediaFileId,
        long episodeId,
        long durationMs,
        string partialHash,
        string? fullHash,
        string? error)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE media_files
                   SET duration_ms=CASE WHEN $duration>0 THEN $duration ELSE duration_ms END,
                       partial_hash=CASE WHEN $partial<>'' THEN $partial ELSE partial_hash END,
                       full_hash=CASE WHEN $full<>'' THEN $full ELSE full_hash END,
                       fingerprinted_at=CASE WHEN $partial<>'' THEN $now ELSE fingerprinted_at END,
                       full_hashed_at=CASE WHEN $full<>'' THEN $now ELSE full_hashed_at END,
                       inspection_error=$error,
                       inspection_error_at=CASE WHEN $error='' THEN NULL ELSE $now END
                 WHERE id=$id
                """;
            command.Parameters.AddWithValue("$duration", Math.Max(0, durationMs));
            command.Parameters.AddWithValue("$partial", partialHash ?? "");
            command.Parameters.AddWithValue("$full", fullHash ?? "");
            command.Parameters.AddWithValue("$error", error ?? "");
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$id", mediaFileId);
            command.ExecuteNonQuery();
        }

        if (durationMs > 0)
        {
            using var playback = connection.CreateCommand();
            playback.Transaction = transaction;
            playback.CommandText = """
                UPDATE playback_state
                   SET duration_ms=$duration
                 WHERE episode_id=$episode
                   AND (duration_ms<=0 OR EXISTS(
                       SELECT 1 FROM media_files mf
                        WHERE mf.id=$media AND mf.episode_id=$episode AND mf.is_preferred=1))
                """;
            playback.Parameters.AddWithValue("$duration", durationMs);
            playback.Parameters.AddWithValue("$episode", episodeId);
            playback.Parameters.AddWithValue("$media", mediaFileId);
            playback.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public long BeginPreservationScan(string machineId, PreservationScanOptions options, int totalFiles)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var started = DateTimeOffset.UtcNow.ToString("O");
        using (var abandon = connection.CreateCommand())
        {
            abandon.Transaction = transaction;
            abandon.CommandText = "UPDATE preservation_scan_runs SET status='interrupted',completed_at=$now,message='Interrupted before completion' WHERE status='running'";
            abandon.Parameters.AddWithValue("$now", started);
            abandon.ExecuteNonQuery();
        }
        long id;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO preservation_scan_runs(machine_id,started_at,status,options_json,total_files,message)
                VALUES($machine,$started,'running',$options,$total,'Starting preservation scan');
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$machine", machineId ?? "");
            command.Parameters.AddWithValue("$started", started);
            command.Parameters.AddWithValue("$options", JsonSerializer.Serialize(options));
            command.Parameters.AddWithValue("$total", Math.Max(0, totalFiles));
            id = Convert.ToInt64(command.ExecuteScalar());
        }
        transaction.Commit();
        return id;
    }

    public void UpdatePreservationScan(long runId, PreservationScanResult result, string message)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE preservation_scan_runs
               SET total_files=$total,processed_files=$processed,fingerprinted_files=$fingerprinted,
                   full_hashed_files=$full,errors=$errors,message=$message
             WHERE id=$id
            """;
        command.Parameters.AddWithValue("$total", result.FilesConsidered);
        command.Parameters.AddWithValue("$processed", result.FilesInspected);
        command.Parameters.AddWithValue("$fingerprinted", result.Fingerprinted);
        command.Parameters.AddWithValue("$full", result.FullHashed);
        command.Parameters.AddWithValue("$errors", result.Errors);
        command.Parameters.AddWithValue("$message", message ?? "");
        command.Parameters.AddWithValue("$id", runId);
        command.ExecuteNonQuery();
    }

    public void CompletePreservationScan(long runId, PreservationScanResult result, string status, string message)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE preservation_scan_runs
               SET completed_at=$completed,status=$status,total_files=$total,processed_files=$processed,
                   fingerprinted_files=$fingerprinted,full_hashed_files=$full,
                   errors=$errors,message=$message
             WHERE id=$id
            """;
        command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$total", result.FilesConsidered);
        command.Parameters.AddWithValue("$processed", result.FilesInspected);
        command.Parameters.AddWithValue("$fingerprinted", result.Fingerprinted);
        command.Parameters.AddWithValue("$full", result.FullHashed);
        command.Parameters.AddWithValue("$errors", result.Errors);
        command.Parameters.AddWithValue("$message", message ?? "");
        command.Parameters.AddWithValue("$id", runId);
        command.ExecuteNonQuery();
    }

    public ArchiveManifest CreateArchiveManifestSnapshot(MachineIdentity machine)
    {
        using var connection = OpenConnection();
        var roots = new List<ArchiveManifestRoot>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT lf.id,lf.path,COALESCE(c.name,'')
                  FROM library_folders lf
                  LEFT JOIN collections c ON c.id=lf.assigned_collection_id
                 WHERE lf.enabled=1
                 ORDER BY length(lf.path) DESC,lf.path
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(1);
                roots.Add(new ArchiveManifestRoot
                {
                    RootId = reader.GetInt32(0),
                    DisplayName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    Path = path,
                    AssignedCollection = reader.GetString(2)
                });
            }
        }

        var manifest = new ArchiveManifest
        {
            AppVersion = AppVersionService.Version,
            GeneratedAt = DateTimeOffset.UtcNow,
            Machine = new ArchiveManifestMachine
            {
                MachineId = machine.MachineId,
                MachineName = machine.MachineName,
                OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription
            },
            LibraryRoots = roots.OrderBy(x => x.RootId).ToList()
        };

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT mf.id,COALESCE(e.broadcast_uid,''),c.name,e.air_date,
                       COALESCE(e.broadcast_slot,''),COALESCE(e.part_number,1),e.total_parts,
                       COALESCE(e.title,''),mf.path,mf.original_filename,mf.file_size,
                       COALESCE(mf.duration_ms,0),COALESCE(mf.partial_hash,''),COALESCE(mf.full_hash,''),
                       COALESCE(mf.storage_state,''),COALESCE(mf.is_preferred,0),mf.modified_time
                  FROM media_files mf
                  JOIN episodes e ON e.id=mf.episode_id
                  JOIN collections c ON c.id=e.collection_id
                 WHERE mf.is_missing=0
                 ORDER BY c.name,e.air_date,e.broadcast_slot,e.part_number,mf.original_filename
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(8);
                var root = FindMostSpecificRoot(path, roots);
                var relative = root is null ? reader.GetString(9) : SafeRelativePath(root.Path, path);
                var fileKey = $"{machine.MachineId}:{reader.GetInt64(0)}";
                manifest.Files.Add(new ArchiveManifestFile
                {
                    FileKey = fileKey,
                    ContentKey = ArchiveContentIdentity.Create(
                        reader.GetString(13), reader.GetString(12), reader.GetInt64(10), reader.GetInt64(11), fileKey),
                    BroadcastUid = reader.GetString(1),
                    Show = reader.GetString(2),
                    AirDate = reader.IsDBNull(3) ? null : reader.GetString(3),
                    BroadcastSlot = reader.GetString(4),
                    PartNumber = reader.GetInt32(5),
                    TotalParts = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    Headline = reader.GetString(7),
                    RootId = root?.RootId ?? 0,
                    RelativePath = relative,
                    Filename = reader.GetString(9),
                    Extension = Path.GetExtension(path).ToLowerInvariant(),
                    FileSize = reader.GetInt64(10),
                    DurationMs = reader.GetInt64(11),
                    PartialSha256 = reader.GetString(12),
                    FullSha256 = reader.GetString(13),
                    StorageState = reader.GetString(14),
                    IsPreferred = reader.GetInt64(15) != 0,
                    ModifiedAt = !reader.IsDBNull(16) && DateTimeOffset.TryParse(reader.GetString(16), out var modified) ? modified : null
                });
            }
        }
        return manifest;
    }

    private static ArchiveManifestRoot? FindMostSpecificRoot(string path, IEnumerable<ArchiveManifestRoot> roots)
    {
        foreach (var root in roots.OrderByDescending(x => x.Path.Length))
        {
            var normalized = root.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (path.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(normalized + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return root;
        }
        return null;
    }

    private static string SafeRelativePath(string root, string path)
    {
        try { return Path.GetRelativePath(root, path); }
        catch { return Path.GetFileName(path); }
    }
}
