namespace TheRadioVault.Services;

public sealed partial class DatabaseService
{
    public IReadOnlyList<ArtworkItem> GetArtworkItems()
    {
        var result = new List<ArtworkItem>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.id,e.title,c.name,e.artwork_path
              FROM episodes e
              JOIN collections c ON c.id=e.collection_id
             WHERE COALESCE(e.artwork_path,'')<>''
             ORDER BY c.name,e.air_date
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ArtworkItem
            {
                EpisodeId = reader.GetInt64(0),
                Title = reader.GetString(1),
                CollectionName = reader.GetString(2),
                ArtworkPath = reader.GetString(3)
            });
        }

        return result;
    }

    public long BeginScan(string scanType = "Full")
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO scan_runs(started_at,scan_type) VALUES($started,$type);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$started", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$type", scanType);
        var id = Convert.ToInt64(command.ExecuteScalar());
        transaction.Commit();
        return id;
    }

    public void CompleteScan(long scanId, ScanResult result)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE scan_runs
               SET completed_at=$done,
                   files_found=$found,
                   files_added=$added,
                   files_updated=$updated,
                   files_unchanged=$unchanged,
                   missing_files=(SELECT COUNT(*) FROM media_files WHERE is_missing=1),
                   errors=$errors
             WHERE id=$id
            """;
        command.Parameters.AddWithValue("$done", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$found", result.FilesFound);
        command.Parameters.AddWithValue("$added", result.Added);
        command.Parameters.AddWithValue("$updated", result.Updated);
        command.Parameters.AddWithValue("$unchanged", result.Unchanged);
        command.Parameters.AddWithValue("$errors", result.Errors);
        command.Parameters.AddWithValue("$id", scanId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ScanHistoryItem> GetScanHistory(int limit = 20)
    {
        var result = new List<ScanHistoryItem>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,started_at,completed_at,scan_type,files_found,files_added,
                   files_updated,files_unchanged,missing_files,errors
              FROM scan_runs
             ORDER BY id DESC
             LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ScanHistoryItem
            {
                Id = reader.GetInt64(0),
                StartedAt = DateTime.Parse(reader.GetString(1)),
                CompletedAt = reader.IsDBNull(2) ? null : DateTime.Parse(reader.GetString(2)),
                ScanType = reader.GetString(3),
                FilesFound = reader.GetInt32(4),
                FilesAdded = reader.GetInt32(5),
                FilesUpdated = reader.GetInt32(6),
                FilesUnchanged = reader.GetInt32(7),
                MissingFiles = reader.GetInt32(8),
                Errors = reader.GetInt32(9)
            });
        }

        return result;
    }

    public LibraryHealthSummary GetLibraryHealth()
    {
        using var connection = OpenConnection();

        int Scalar(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(command.ExecuteScalar());
        }

        return new LibraryHealthSummary
        {
            MissingFiles = Scalar("SELECT COUNT(*) FROM media_files WHERE is_missing=1"),
            DuplicateCandidates = Scalar("""
                SELECT COALESCE(SUM(n-1),0)
                  FROM (
                    SELECT COUNT(*) n
                      FROM episodes e
                      JOIN media_files mf ON mf.episode_id=e.id
                     WHERE mf.is_missing=0
                     GROUP BY COALESCE(
                         NULLIF(mf.full_hash,''),
                         CASE WHEN e.air_date IS NOT NULL AND trim(COALESCE(e.edition,''))='' THEN
                           CAST(e.collection_id AS TEXT)||'|'||COALESCE(e.air_date,'')||'|'||
                           lower(trim(COALESCE(e.broadcast_slot,'')))||'|'||COALESCE(e.part_number,1)||'|'||mf.file_size
                         ELSE 'edition|'||mf.id END)
                    HAVING COUNT(*)>1
                  )
                """),
            NeedsReview = Scalar("SELECT COUNT(*) FROM episodes WHERE air_date IS NULL OR date_confidence IN ('Unknown','Ambiguous')"),
            LibraryFolders = Scalar("SELECT COUNT(*) FROM library_folders WHERE enabled=1")
        };
    }

    public IReadOnlyList<MissingFileItem> GetMissingFiles()
    {
        var result = new List<MissingFileItem>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mf.id,e.id,c.name,e.title,mf.original_filename,mf.path,mf.file_size
              FROM media_files mf
              JOIN episodes e ON e.id=mf.episode_id
              JOIN collections c ON c.id=e.collection_id
             WHERE mf.is_missing=1
             ORDER BY c.name,e.air_date
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new MissingFileItem
            {
                MediaFileId = reader.GetInt64(0),
                EpisodeId = reader.GetInt64(1),
                CollectionName = reader.GetString(2),
                EpisodeTitle = reader.GetString(3),
                OriginalFilename = reader.GetString(4),
                PreviousPath = reader.GetString(5),
                FileSize = reader.GetInt64(6)
            });
        }

        return result;
    }

    public IReadOnlyList<DuplicateGroupItem> GetDuplicateCandidates()
    {
        var result = new List<DuplicateGroupItem>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH keyed AS (
              SELECT e.id AS episode_id,c.name,COALESCE(e.air_date,'Unknown') AS air_date,e.title,
                     mf.original_filename,mf.path,COALESCE(mf.duration_ms,0) AS duration_ms,
                     COALESCE(
                       NULLIF(mf.full_hash,''),
                       CASE WHEN e.air_date IS NOT NULL AND trim(COALESCE(e.edition,''))='' THEN
                         CAST(e.collection_id AS TEXT)||'|'||COALESCE(e.air_date,'Unknown')||'|'||
                         lower(trim(COALESCE(e.broadcast_slot,'')))||'|'||COALESCE(e.part_number,1)||'|'||mf.file_size
                       ELSE 'edition|'||mf.id END) AS group_key
                FROM episodes e
                JOIN collections c ON c.id=e.collection_id
                JOIN media_files mf ON mf.episode_id=e.id
               WHERE mf.is_missing=0
            ), candidates AS (
              SELECT group_key FROM keyed GROUP BY group_key HAVING COUNT(*)>1
            )
            SELECT episode_id,name,air_date,title,original_filename,path,duration_ms,group_key
              FROM keyed
             WHERE group_key IN (SELECT group_key FROM candidates)
             ORDER BY name,air_date,group_key,original_filename
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new DuplicateGroupItem
            {
                EpisodeId = reader.GetInt64(0),
                CollectionName = reader.GetString(1),
                AirDate = reader.GetString(2),
                EpisodeTitle = reader.GetString(3),
                Filename = reader.GetString(4),
                Path = reader.GetString(5),
                DurationMs = reader.GetInt64(6),
                GroupKey = reader.GetString(7)
            });
        }

        return result;
    }

    public bool RelinkMissingFile(long mediaFileId, string newPath)
    {
        var info = new FileInfo(newPath);
        if (!info.Exists) return false;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE media_files
               SET path=$path,
                   original_filename=$name,
                   file_size=$size,
                   modified_time=$modified,
                   is_missing=0,
                   is_preferred=1,
                   storage_state='AvailableOffline',
                   last_seen_at=$now
             WHERE id=$id
            """;
        command.Parameters.AddWithValue("$path", newPath);
        command.Parameters.AddWithValue("$name", info.Name);
        command.Parameters.AddWithValue("$size", info.Length);
        command.Parameters.AddWithValue("$modified", info.LastWriteTimeUtc.ToString("O"));
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", mediaFileId);
        return command.ExecuteNonQuery() == 1;
    }

    public int RepairMissingFiles(string searchRoot)
    {
        if (!Directory.Exists(searchRoot)) return 0;

        var missing = GetMissingFiles();
        var byName = missing
            .GroupBy(item => item.OriginalFilename, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var repaired = 0;

        foreach (var path in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
        {
            if (!byName.TryGetValue(Path.GetFileName(path), out var candidates)) continue;
            var info = new FileInfo(path);
            var match = candidates.FirstOrDefault(item => item.FileSize == info.Length)
                        ?? (candidates.Count == 1 ? candidates[0] : null);
            if (match is null) continue;

            if (RelinkMissingFile(match.MediaFileId, path))
            {
                repaired++;
                candidates.Remove(match);
            }
        }

        return repaired;
    }
}
