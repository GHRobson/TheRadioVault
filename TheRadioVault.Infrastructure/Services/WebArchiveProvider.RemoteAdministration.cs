using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TheRadioVault.Core.Events;
using TheRadioVault.Models;
using TheRadioVault.Services.Jobs;
using TheRadioVault.Services.Models;
using TheRadioVault.Services.Services;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Services;

internal sealed partial class WebArchiveProvider
{
    private static readonly TimeSpan ResearchImportSessionLifetime = TimeSpan.FromMinutes(30);
    private const int MaximumResearchPackBytes = KnowledgePackService.MaximumPackageBytes;
    private const int MaximumPendingResearchImports = 4;
    private readonly ConcurrentDictionary<Guid, PendingResearchImport> _pendingResearchImports = new();

    public WebLibraryScanSnapshot GetLibraryScanStatus()
    {
        lock (_libraryScanStatusGate) return _libraryScanStatus;
    }

    public async Task<WebLibraryScanSnapshot> RunLibraryScanAsync(
        string trigger,
        CancellationToken cancellationToken = default)
    {
        var normalizedTrigger = string.IsNullOrWhiteSpace(trigger) ? "manual" : trigger.Trim();
        if (!await _libraryScanGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return GetLibraryScanStatus();

        var startedAt = DateTimeOffset.UtcNow;
        SetLibraryScanStatus(new WebLibraryScanSnapshot(
            IsRunning: true,
            Started: true,
            Trigger: normalizedTrigger,
            StartedAt: startedAt,
            CompletedAt: GetLibraryScanStatus().CompletedAt,
            Message: "Starting server Library scan…",
            FilesFound: 0,
            Added: 0,
            Updated: 0,
            Unchanged: 0,
            Errors: 0,
            CanonicalBroadcastsAdded: 0,
            CanonicalRecordingsAdded: 0,
            CanonicalEpisodesMapped: 0,
            CanonicalItemsNeedingReview: 0));
        AddChange("library-scan", null, $"started:{normalizedTrigger}", startedAt);

        try
        {
            var progress = new InlineProgress<string>(message =>
            {
                lock (_libraryScanStatusGate)
                    _libraryScanStatus = _libraryScanStatus with { Message = message };
            });
            var scanner = new LibraryScannerService(_database, new FilenameParserService());
            var result = await Task.Run(
                () => scanner.ScanAll(progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            var completedAt = DateTimeOffset.UtcNow;
            var snapshot = new WebLibraryScanSnapshot(
                IsRunning: false,
                Started: true,
                Trigger: normalizedTrigger,
                StartedAt: startedAt,
                CompletedAt: completedAt,
                Message: $"Scan complete: {result.FilesFound:N0} found, {result.Added:N0} added, {result.Updated:N0} updated and {result.Errors:N0} errors.",
                FilesFound: result.FilesFound,
                Added: result.Added,
                Updated: result.Updated,
                Unchanged: result.Unchanged,
                Errors: result.Errors,
                CanonicalBroadcastsAdded: result.CanonicalBroadcastsAdded,
                CanonicalRecordingsAdded: result.CanonicalRecordingsAdded,
                CanonicalEpisodesMapped: result.CanonicalEpisodesMapped,
                CanonicalItemsNeedingReview: result.CanonicalItemsNeedingReview);
            SetLibraryScanStatus(snapshot);
            _events.Publish(new LibraryScanCompletedEvent(
                result.FilesFound,
                result.Added,
                result.Updated,
                result.Unchanged,
                result.Errors,
                result.ResearchCandidatesFound,
                completedAt));
            if (result.ResearchApplied > 0)
            {
                _events.Publish(new ResearchUpdatedEvent(null, null, "scan-reconciliation-auto-applied", completedAt));
                _events.Publish(new MetadataChangedEvent(null, "scan-reconciliation-auto-applied", completedAt));
            }
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            var cancelled = GetLibraryScanStatus() with
            {
                IsRunning = false,
                Message = "The Library scan was cancelled. Existing archive data was preserved."
            };
            SetLibraryScanStatus(cancelled);
            throw;
        }
        catch (Exception exception)
        {
            var failed = GetLibraryScanStatus() with
            {
                IsRunning = false,
                Errors = Math.Max(1, GetLibraryScanStatus().Errors),
                Message = $"The Library scan failed: {exception.Message}"
            };
            SetLibraryScanStatus(failed);
            AddChange("library-scan", null, "failed", DateTimeOffset.UtcNow);
            throw;
        }
        finally
        {
            _libraryScanGate.Release();
        }
    }

    private void SetLibraryScanStatus(WebLibraryScanSnapshot snapshot)
    {
        lock (_libraryScanStatusGate) _libraryScanStatus = snapshot;
    }

    public WebAuthoritativeSettingsSnapshot GetAuthoritativeSettings()
    {
        var folders = _database.GetLibraryFolders()
            .Select(x => new WebArchiveFolderSnapshot(x.Id, x.Path, x.CollectionName, x.Recursive, x.LastScanAt))
            .ToArray();
        var storage = _database.GetStorageSummary();
        var preservation = _database.GetPreservationSummary();
        var playback = PlaybackPreferencesService.Load();

        return new WebAuthoritativeSettingsSnapshot(
            folders,
            new WebStorageSnapshot(storage.TotalFiles, storage.AvailableOffline, storage.CloudOnly, storage.Missing, storage.LogicalBytes),
            new WebPreservationSnapshot(
                preservation.TotalFiles,
                preservation.LocalFiles,
                preservation.MissingEvidence,
                preservation.PartialFingerprints,
                preservation.FullHashes,
                preservation.StrongDuplicateFilesAwaitingFullHash,
                preservation.InspectionErrors,
                preservation.LastCompletedScanAt),
            GetArchiveHealth(),
            new WebPlaybackPreferencesSnapshot(
                playback.SkipBackSeconds,
                playback.SkipForwardSeconds,
                playback.CompletionThresholdSeconds,
                DateTimeOffset.UtcNow),
            _database.GetResearchAttentionCount(),
            _database.GetDatabaseQuickCheck(),
            FindLatestAuthoritativeBackup(),
            DateTimeOffset.UtcNow);
    }

    public WebPlaybackPreferencesSnapshot SetPlaybackPreferences(WebPlaybackPreferencesSnapshot preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        PlaybackPreferencesService.Save(
            preferences.SkipBackSeconds,
            preferences.SkipForwardSeconds,
            preferences.CompletionThresholdSeconds);
        var saved = PlaybackPreferencesService.Load();
        var changedAt = DateTimeOffset.UtcNow;
        _events.Publish(new PlaybackPreferencesChangedEvent(
            saved.SkipBackSeconds,
            saved.SkipForwardSeconds,
            saved.CompletionThresholdSeconds,
            changedAt));
        AddChange("settings", null, "playback-preferences", changedAt);
        return new WebPlaybackPreferencesSnapshot(
            saved.SkipBackSeconds,
            saved.SkipForwardSeconds,
            saved.CompletionThresholdSeconds,
            DateTimeOffset.UtcNow);
    }

    public async Task<WebResearchPackPreviewResponse> PreviewResearchPackAsync(
        Stream packageStream,
        string sourceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageStream);

        ExpireResearchImportSessions();
        TrimResearchImportSessions();
        var safeSourceName = SafeResearchPackName(sourceName);
        var sessionId = Guid.NewGuid();
        var sourcePath = await WriteResearchImportSessionFileAsync(
            sessionId, safeSourceName, packageStream, cancellationToken).ConfigureAwait(false);
        return await PreviewStagedResearchPackAsync(sessionId, safeSourceName, sourcePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WebResearchPackPreviewResponse> PreviewResearchPackFileAsync(
        string filePath,
        string sourceName,
        CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(filePath);
        if (!file.Exists) throw new FileNotFoundException("The Archive Knowledge Database no longer exists.", filePath);
        if (file.Length == 0) throw new InvalidDataException("The Archive Knowledge Database is empty.");
        if (file.Length > MaximumResearchPackBytes)
            throw new InvalidDataException($"Archive Knowledge Databases are limited to {MaximumResearchPackBytes / 1024 / 1024} MB for import.");

        ExpireResearchImportSessions();
        TrimResearchImportSessions();
        var safeSourceName = SafeResearchPackName(sourceName);
        var sessionId = Guid.NewGuid();
        var sourcePath = CopyResearchImportSessionFile(sessionId, safeSourceName, file.FullName);
        return await PreviewStagedResearchPackAsync(sessionId, safeSourceName, sourcePath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WebResearchPackPreviewResponse> PreviewStagedResearchPackAsync(
        Guid sessionId,
        string safeSourceName,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var service = new KnowledgePackService();
            var pack = await Task.Run(() => service.Import(sourcePath), cancellationToken).ConfigureAwait(false);
            var preview = await Task.Run(
                () => _database.PreviewKnowledgePack(pack, sourcePath, progress: null, cancellationToken: cancellationToken),
                cancellationToken).ConfigureAwait(false);
            WikiPackPreview? wikiPreview = null;
            if (pack.Wiki is not null)
            {
                string hash;
                await using (var stream = File.OpenRead(sourcePath))
                    hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
                wikiPreview = await new WikiService(_database.PlatformDatabase)
                    .PreviewImportAsync(pack.Wiki, safeSourceName, hash, cancellationToken)
                    .ConfigureAwait(false);
            }

            var expiresAt = DateTimeOffset.UtcNow.Add(ResearchImportSessionLifetime);
            _pendingResearchImports[sessionId] = new PendingResearchImport(pack, sourcePath, expiresAt, new CancellationTokenSource());
            return new WebResearchPackPreviewResponse(sessionId, MapPreview(preview, pack.Transcripts?.Count ?? 0, wikiPreview), expiresAt);
        }
        catch
        {
            DeleteResearchImportSessionFiles(sourcePath);
            throw;
        }
    }

    public WebResearchPackImportJob StartResearchPackImport(Guid sessionId)
    {
        ExpireResearchImportSessions();
        if (!_pendingResearchImports.TryGetValue(sessionId, out var pending))
            throw new InvalidOperationException("The remote research preview has expired. Analyse the pack again before importing it.");

        lock (pending.Sync)
        {
            if (pending.JobId != Guid.Empty) return MapResearchImportJob(sessionId, pending);
            pending.ExpiresAt = DateTimeOffset.UtcNow.AddDays(1);
            pending.JobId = _jobs.Enqueue(new BackgroundJobRequest(
                $"Import {Path.GetFileName(pending.SourcePath)}",
                BackgroundJobCategory.ResearchImport,
                (context, token) => ExecuteResearchPackImportAsync(sessionId, pending, context, token)));
            return MapResearchImportJob(sessionId, pending);
        }
    }

    public WebResearchPackImportJob GetResearchPackImportStatus(Guid sessionId)
    {
        ExpireResearchImportSessions();
        if (!_pendingResearchImports.TryGetValue(sessionId, out var pending))
            throw new InvalidOperationException("The remote research import session is no longer available.");
        return MapResearchImportJob(sessionId, pending);
    }

    private async Task ExecuteResearchPackImportAsync(
        Guid sessionId,
        PendingResearchImport pending,
        BackgroundJobContext context,
        CancellationToken jobCancellation)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(jobCancellation, pending.Cancellation.Token);
        var recordTotal = (pending.Pack.Broadcasts?.Count ?? 0) + (pending.Pack.MissingBroadcasts?.Count ?? 0);
        var recordProgress = new InlineProgress<ResearchPackOperationProgress>(value =>
        {
            lock (pending.Sync)
            {
                pending.Current = value.Current;
                pending.Total = value.Total;
            }
            context.Report(5 + value.Percent * 0.78, value.Message);
        });

        context.Report(1, "Creating and verifying the pre-import safety backup…");
        var backupPath = CreateKnowledgeImportBackup();
        DiagnosticLog.Write("Server Knowledge import", $"Created safety backup before import: {backupPath}");
        linkedCancellation.Token.ThrowIfCancellationRequested();
        lock (pending.Sync)
        {
            pending.Current = 0;
            pending.Total = recordTotal;
        }

        var result = await Task.Run(
            () => _database.ImportKnowledgePack(
                pending.Pack,
                pending.SourcePath,
                recordProgress,
                linkedCancellation.Token),
            linkedCancellation.Token).ConfigureAwait(false);

        WikiPackImportResult? wikiResult = null;
        if (pending.Pack.Wiki is not null)
        {
            lock (pending.Sync)
            {
                pending.Current = 0;
                pending.Total = pending.Pack.Wiki.Pages.Count;
            }
            context.Report(85, $"Importing {pending.Pack.Wiki.Pages.Count:N0} Explore pages and embedded images…");
            string hash;
            await using (var stream = File.OpenRead(pending.SourcePath))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, linkedCancellation.Token).ConfigureAwait(false)).ToLowerInvariant();
            var wikiProgress = new InlineProgress<WikiPackOperationProgress>(value =>
            {
                lock (pending.Sync)
                {
                    pending.Current = value.Current;
                    pending.Total = value.Total;
                }
                context.Report(85 + value.Percent * 0.13, value.Message);
            });
            wikiResult = await new WikiService(_database.PlatformDatabase)
                .ApplyImportAsync(
                    pending.Pack.Wiki,
                    Path.GetFileName(pending.SourcePath),
                    hash,
                    cancellationToken: linkedCancellation.Token,
                    progress: wikiProgress)
                .ConfigureAwait(false);
        }

        context.Report(98, "Refreshing the server archive indexes…");
        lock (pending.Sync) pending.Result = MapImportResult(result, wikiResult);
        InvalidateEpisodeSnapshot();
        var now = DateTimeOffset.UtcNow;
        _events.Publish(new ResearchUpdatedEvent(null, null, $"remote-research-pack-import:{pending.Pack.Manifest.Show}", now));
        AddChange("research", null, "remote-pack-import", now);
        context.Report(100, "Knowledge Database import complete.");
    }

    private WebResearchPackImportJob MapResearchImportJob(Guid sessionId, PendingResearchImport pending)
    {
        lock (pending.Sync)
        {
            if (pending.JobId == Guid.Empty)
                return new WebResearchPackImportJob(
                    sessionId, Guid.Empty, "Pending", 0, "Ready to import", 0,
                    (pending.Pack.Broadcasts?.Count ?? 0) + (pending.Pack.MissingBroadcasts?.Count ?? 0), true);

            var job = _jobs.GetJob(pending.JobId);
            if (job is null)
                return new WebResearchPackImportJob(
                    sessionId, pending.JobId, "Failed", 0,
                    "The server no longer has progress for this import.", pending.Current, pending.Total,
                    false, pending.Result, "The background import job is no longer available.");

            return new WebResearchPackImportJob(
                sessionId,
                pending.JobId,
                job.State.ToString(),
                job.Percent ?? 0,
                job.Message ?? "Working…",
                pending.Current,
                pending.Total,
                job.CanCancel,
                pending.Result,
                job.State == BackgroundJobState.Failed ? job.Error?.Message ?? job.Message : null);
        }
    }

    private string CreateKnowledgeImportBackup()
    {
        var databasePath = _database.PlatformDatabase.DatabasePath;
        if (!File.Exists(databasePath))
            throw new InvalidOperationException("The server database is unavailable, so a safe Knowledge import cannot begin.");
        Directory.CreateDirectory(AppPaths.BackupDirectory);
        var backupPath = Path.Combine(
            AppPaths.BackupDirectory,
            $"RadioVault-before-knowledge-import-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.sqlite");
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        using var check = destination.CreateCommand();
        check.CommandText = "PRAGMA quick_check";
        var result = Convert.ToString(check.ExecuteScalar());
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(backupPath); } catch { }
            throw new InvalidOperationException($"The pre-import safety backup failed verification: {result ?? "unknown error"}.");
        }
        return backupPath;
    }

    public bool CancelResearchPackImport(Guid sessionId)
    {
        if (!_pendingResearchImports.TryGetValue(sessionId, out var pending)) return false;
        pending.Cancellation.Cancel();
        if (pending.JobId != Guid.Empty) _jobs.Cancel(pending.JobId);
        return true;
    }

    public async Task<WebResearchPackExportPayload> ExportResearchPackAsync(
        CancellationToken cancellationToken = default)
    {
        var pack = await Task.Run(
            () => _database.BuildCompleteKnowledgePack(AppVersionService.Version),
            cancellationToken).ConfigureAwait(false);
        var databaseName = Path.GetFileName(_database.PlatformDatabase.DatabasePath);
        var identityBytes = SHA256.HashData(Encoding.UTF8.GetBytes(databaseName));
        var databaseIdentity = Convert.ToHexString(identityBytes)[..16].ToLowerInvariant();
        pack.Wiki = await new WikiService(_database.PlatformDatabase)
            .GetAuthoringSnapshotAsync(AppVersionService.Version, databaseIdentity, cancellationToken)
            .ConfigureAwait(false);
        var bytes = await Task.Run(() => new KnowledgePackService().ExportBytes(pack), cancellationToken).ConfigureAwait(false);
        var fileName = "RadioVault-Archive-Knowledge.trvknowledge";
        return new WebResearchPackExportPayload(bytes, fileName, pack.Broadcasts.Count, pack.MissingBroadcasts.Count,
            pack.Transcripts?.Count ?? 0, pack.Wiki.Pages.Count);
    }

    private void ExpireResearchImportSessions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _pendingResearchImports)
            if (entry.Value.ExpiresAt <= now && !IsResearchImportActive(entry.Value))
                RemoveResearchImportSession(entry.Key);
        CleanupOrphanedResearchImportSessions(now);
    }

    private void TrimResearchImportSessions()
    {
        var overflow = _pendingResearchImports.Count - MaximumPendingResearchImports + 1;
        if (overflow <= 0) return;
        foreach (var entry in _pendingResearchImports
                     .Where(x => !IsResearchImportActive(x.Value))
                     .OrderBy(x => x.Value.ExpiresAt)
                     .Take(overflow))
            RemoveResearchImportSession(entry.Key);
    }

    private bool IsResearchImportActive(PendingResearchImport pending)
    {
        if (pending.JobId == Guid.Empty) return false;
        return _jobs.GetJob(pending.JobId)?.State is BackgroundJobState.Queued or BackgroundJobState.Running;
    }

    private void RemoveResearchImportSession(Guid sessionId)
    {
        if (!_pendingResearchImports.TryRemove(sessionId, out var removed)) return;
        removed.Cancellation.Dispose();
        DeleteResearchImportSessionFiles(removed.SourcePath);
    }


    private static async Task<string> WriteResearchImportSessionFileAsync(
        Guid sessionId,
        string sourceName,
        Stream source,
        CancellationToken cancellationToken)
    {
        if (!source.CanRead) throw new InvalidDataException("The Archive Knowledge Database cannot be read.");
        if (source.CanSeek)
        {
            if (source.Length == 0) throw new InvalidDataException("The Archive Knowledge Database is empty.");
            if (source.Length > MaximumResearchPackBytes)
                throw new InvalidDataException($"Archive Knowledge Databases are limited to {MaximumResearchPackBytes / 1024 / 1024} MB for remote import.");
            source.Position = 0;
        }
        var sessionDirectory = Path.Combine(AppPaths.DataDirectory, "remote-research-imports", sessionId.ToString("N"));
        Directory.CreateDirectory(sessionDirectory);
        var path = Path.Combine(sessionDirectory, sourceName);
        try
        {
            await using var destination = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            long written = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;
                written += read;
                if (written > MaximumResearchPackBytes)
                    throw new InvalidDataException($"Archive Knowledge Databases are limited to {MaximumResearchPackBytes / 1024 / 1024} MB for remote import.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (written == 0) throw new InvalidDataException("The Archive Knowledge Database is empty.");
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch
        {
            DeleteResearchImportSessionFiles(path);
            throw;
        }
    }

    private static string CopyResearchImportSessionFile(Guid sessionId, string sourceName, string sourcePath)
    {
        var sessionDirectory = Path.Combine(AppPaths.DataDirectory, "remote-research-imports", sessionId.ToString("N"));
        Directory.CreateDirectory(sessionDirectory);
        var path = Path.Combine(sessionDirectory, sourceName);
        File.Copy(sourcePath, path, overwrite: false);
        return path;
    }

    private static void DeleteResearchImportSessionFiles(string sourcePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(sourcePath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Server research", $"Could not remove a remote research import session: {ex.Message}");
        }
    }

    private static void CleanupOrphanedResearchImportSessions(DateTimeOffset now)
    {
        try
        {
            var root = Path.Combine(AppPaths.DataDirectory, "remote-research-imports");
            if (!Directory.Exists(root)) return;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var lastWrite = new DateTimeOffset(DateTime.SpecifyKind(Directory.GetLastWriteTimeUtc(directory), DateTimeKind.Utc));
                if (now - lastWrite > TimeSpan.FromDays(1)) Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Server research", $"Could not clean old remote research sessions: {ex.Message}");
        }
    }

    private void DisposeResearchImportSessions()
    {
        foreach (var pending in _pendingResearchImports.Values)
        {
            try { pending.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        foreach (var sessionId in _pendingResearchImports.Keys) RemoveResearchImportSession(sessionId);
    }

    private static WebResearchPackPreview MapPreview(ResearchImportPreview value, int transcriptCount, WikiPackPreview? wiki)
        => new(
            value.PackageName,
            value.Show,
            value.TotalRecords,
            value.ExactMatches,
            value.MissingRecords,
            value.AmbiguousMatches,
            value.NewPeople,
            value.NewTopics,
            value.NewSources,
            value.IncomingSummaries,
            value.ProtectedManualRecords,
            value.PotentialConflicts,
            value.FieldsExpectedToApply,
            value.FieldsExpectedToMerge,
            value.FieldsExpectedToPreserve,
            value.FieldsProtectedByManualEdits,
            value.AuthoritativeAudit,
            value.PreviouslyImported,
            value.PackageHash,
            transcriptCount,
            wiki?.TotalPages ?? 0,
            wiki?.ImageCount ?? 0,
            wiki?.TimelineEventCount ?? 0);

    private static WebResearchPackImportResult MapImportResult(KnowledgePackImportResult value, WikiPackImportResult? wiki)
        => new(
            value.Total,
            value.Matched,
            value.Updated,
            value.RetainedMissing,
            value.Ambiguous,
            value.ResolvedPreviousMissing,
            value.ResearchRecordsStored,
            value.AttachedResearchRecords,
            value.ConfirmedMissing,
            value.ProbableMissing,
            value.UnknownGaps,
            value.ConflictsCreated,
            value.ImportRunId,
            value.FieldsApplied,
            value.FieldsMerged,
            value.FieldsPreserved,
            value.ManualFieldsProtected,
            value.ChangeRecordsWritten,
            wiki is null ? 0 : wiki.CreatedPages + wiki.UpdatedPages,
            wiki?.SkippedConflicts ?? 0);

    private static DateTimeOffset? FindLatestAuthoritativeBackup()
    {
        try
        {
            if (!Directory.Exists(AppPaths.BackupDirectory)) return null;
            var paths = Directory.EnumerateFiles(AppPaths.BackupDirectory, "*.trvbackup", SearchOption.TopDirectoryOnly).ToArray();
            if (paths.Length == 0) return null;
            var latest = paths.Max(File.GetLastWriteTimeUtc);
            return new DateTimeOffset(DateTime.SpecifyKind(latest, DateTimeKind.Utc));
        }
        catch
        {
            return null;
        }
    }

    private static string SafeResearchPackName(string value)
    {
        var fileName = Path.GetFileName(string.IsNullOrWhiteSpace(value) ? "remote-archive.trvknowledge" : value.Trim());
        fileName = SanitiseFileName(fileName);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "remote-archive.trvknowledge";
        return fileName.EndsWith(".trvknowledge", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".trvknowledge";
    }

    private static string SanitiseFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch)).Trim();
    }

    private sealed class PendingResearchImport
    {
        public PendingResearchImport(
            TrvKnowledgePack pack,
            string sourcePath,
            DateTimeOffset expiresAt,
            CancellationTokenSource cancellation)
        {
            Pack = pack;
            SourcePath = sourcePath;
            ExpiresAt = expiresAt;
            Cancellation = cancellation;
        }

        public object Sync { get; } = new();
        public TrvKnowledgePack Pack { get; }
        public string SourcePath { get; }
        public DateTimeOffset ExpiresAt { get; set; }
        public CancellationTokenSource Cancellation { get; }
        public Guid JobId { get; set; }
        public int Current { get; set; }
        public int Total { get; set; }
        public WebResearchPackImportResult? Result { get; set; }
    }
}
