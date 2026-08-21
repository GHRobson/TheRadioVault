using System.IO;
using TheRadioVault.Media.Contracts;
using TheRadioVault.Media.Services;
using TheRadioVault.Models;

namespace TheRadioVault.Services;

/// <summary>
/// Windows host adapter for the shared media inspection pipeline. Folder
/// enumeration and cloud-placeholder checks remain platform-specific, while
/// metadata, artwork and fingerprint processing are reusable by future hosts.
/// </summary>
public sealed class LibraryScannerService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".wav", ".wma" };

    private readonly DatabaseService _database;
    private readonly FilenameParserService _parser;
    private readonly IMediaInspectionService _mediaInspection;
    private readonly CloudFileService _cloudFiles;

    public LibraryScannerService(DatabaseService database, FilenameParserService parser)
        : this(
            database,
            parser,
            new MediaInspectionService(
                new TagLibAudioMetadataService(),
                new FileArtworkCache(AppPaths.ArtworkDirectory),
                new MediaFingerprintService()),
            new CloudFileService())
    {
    }

    public LibraryScannerService(
        DatabaseService database,
        FilenameParserService parser,
        IMediaInspectionService mediaInspection,
        CloudFileService cloudFiles)
    {
        _database = database;
        _parser = parser;
        _mediaInspection = mediaInspection;
        _cloudFiles = cloudFiles;
    }

    public ScanResult ScanAll(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = new ScanResult();
        var scanId = _database.BeginScan();
        var collectionLookup = _database.GetCollectionLookup();
        collectionLookup.TryGetValue("Unsorted", out var unsortedCollectionId);

        try
        {
            foreach (var folder in _database.GetLibraryFolders())
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Checking archive folder: {folder.Path}");
                if (!Directory.Exists(folder.Path))
                {
                    result.Errors++;
                    progress?.Report($"Archive unavailable: {folder.Path}");
                    continue;
                }

                var folderScanStartedUtc = DateTime.UtcNow;
                List<string> files;
                Dictionary<string, TheRadioVault.Core.Models.FilenameParseContext> folderParseContexts;
                IReadOnlyDictionary<string, LibraryScanFileSnapshot> previousFiles;
                try
                {
                    var searchOption = folder.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    files = Directory.EnumerateFiles(folder.Path, "*.*", searchOption)
                        .Where(file => SupportedExtensions.Contains(Path.GetExtension(file)))
                        .ToList();
                    previousFiles = _database.GetFolderFileScanSnapshots(folder.Path, folder.Recursive);
                    progress?.Report($"Found {files.Count:N0} supported audio files in {folder.Path}");
                    folderParseContexts = files
                        .GroupBy(file => Path.GetDirectoryName(file) ?? folder.Path, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => _parser.AnalyseFolder(group), StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    result.Errors++;
                    progress?.Report($"Could not read {folder.Path}: {ex.Message}");
                    continue;
                }

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.FilesFound++;
                    if (result.FilesFound == 1 || result.FilesFound % 50 == 0)
                        progress?.Report($"Scanning file {result.FilesFound:N0}: {Path.GetFileName(file)}");

                    var storageState = EpisodeStorageState.Missing;
                    previousFiles.TryGetValue(file, out var previous);
                    try
                    {
                        var info = new FileInfo(file);
                        storageState = _cloudFiles.GetStorageState(file);
                        var directory = Path.GetDirectoryName(file) ?? folder.Path;
                        folderParseContexts.TryGetValue(directory, out var parseContext);
                        parseContext ??= TheRadioVault.Core.Models.FilenameParseContext.None;
                        if (folder.AssignedCollectionId.HasValue)
                            parseContext = parseContext with { AssignedCollectionName = folder.CollectionName };
                        var parsed = _parser.Parse(file, parseContext);
                        var collectionId = ResolveCollectionId(parsed.CollectionName, parsed.CollectionDetectedFromFilename, folder.AssignedCollectionId, collectionLookup, unsortedCollectionId);
                        var unchanged = IsUnchanged(previous, info, collectionId, storageState);

                        // Reading tags, artwork and fingerprints is by far the most
                        // expensive part of a rescan. Preserve parser/identity updates
                        // for every file, but inspect media only when the file changed.
                        TheRadioVault.Media.Models.MediaInspection? inspection = null;
                        if (!unchanged && storageState == EpisodeStorageState.AvailableOffline)
                        {
                            try
                            {
                                inspection = _mediaInspection.Inspect(file);
                            }
                            catch (Exception ex)
                            {
                                // A damaged tag should not prevent the file itself being indexed.
                                progress?.Report($"Media details unavailable for {Path.GetFileName(file)}: {ex.Message}");
                            }
                        }

                        var upsert = _database.UpsertScannedFile(
                            file,
                            info.Length,
                            info.LastWriteTimeUtc,
                            collectionId,
                            parsed,
                            storageState,
                            inspection?.Fingerprint.PartialSha256,
                            inspection?.Fingerprint.FullSha256,
                            inspection?.Metadata.DurationMs);

                        if (upsert.Added) result.Added++;
                        else if (unchanged) result.Unchanged++;
                        else result.Updated++;

                        var episodeId = upsert.EpisodeId;
                        if (inspection is not null)
                            ApplyInspectedMetadata(episodeId, file, parsed, inspection);

                        // New and returning recordings are the cases where a saved
                        // missing-broadcast record may need attaching. Rechecking every
                        // unchanged file made large rescans unnecessarily expensive.
                        if (upsert.Added || previous is null || previous.WasMissing)
                            ReconcileSavedResearch(episodeId, file, result, progress);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // The file was enumerated successfully, so keep an existing
                        // record present even when parsing or metadata extraction fails.
                        if (previous is not null)
                        {
                            try { _database.TouchScannedFile(file, storageState); }
                            catch { /* Preserve the original scan error. */ }
                        }
                        result.Errors++;
                        progress?.Report($"Could not scan {Path.GetFileName(file)}: {ex.Message}");
                    }
                }

                // Missing status is reconciled only after the complete folder was
                // processed. Cancellation or a disconnected root therefore cannot
                // falsely mark the unvisited remainder of an archive as missing.
                _database.CompleteFolderScan(folder.Id, folder.Path, folder.Recursive, folderScanStartedUtc);
            }

            // Once the guarded canonical cutover has happened, the ordinary
            // file scan must also append trustworthy new broadcasts/recordings
            // to that canonical model. Otherwise the physical files are indexed
            // successfully but remain invisible to both the desktop and clients.
            try
            {
                var promotion = _database.PromoteScannedFilesIntoCanonicalLibrary();
                result.CanonicalBroadcastsAdded = promotion.BroadcastsAdded;
                result.CanonicalRecordingsAdded = promotion.RecordingsAdded;
                result.CanonicalEpisodesMapped = promotion.EpisodesMapped;
                result.CanonicalItemsNeedingReview = promotion.ItemsNeedingReview;
                if (promotion.BroadcastsAdded > 0 || promotion.EpisodesMapped > 0)
                    progress?.Report($"Canonical library updated: {promotion.BroadcastsAdded:N0} broadcast(s) added and {promotion.EpisodesMapped:N0} episode row(s) attached.");
            }
            catch (Exception ex)
            {
                result.Errors++;
                DiagnosticLog.Write("Canonical scan promotion",
                    "The physical scan completed, but newly indexed files could not be appended to the canonical library.", ex);
                progress?.Report("The files were indexed, but the canonical library update failed. Existing canonical data was preserved.");
            }

            // Reconciliation can involve thousands of restored portable records.
            // It must not hold the library scan open after the final audio file,
            // especially on a large desktop archive. The Research decisions window
            // performs this work on a background thread and remains responsive.
            try
            {
                result.ResearchAmbiguous = _database.GetPendingResearchReconciliationCount();
                progress?.Report(result.ResearchAmbiguous > 0
                    ? $"Audio scan complete. {result.ResearchAmbiguous:N0} grouped research decision{(result.ResearchAmbiguous == 1 ? " is" : "s are")} queued for background triage."
                    : "Audio scan complete. No research match decisions are queued.");
            }
            catch (Exception ex)
            {
                result.ResearchTriageFailed = true;
                DiagnosticLog.Write("Research reconciliation", "The audio scan completed, but the pending research count could not be read.", ex);
                result.ResearchAmbiguous = Math.Max(0, result.ResearchCandidatesFound);
                progress?.Report("Audio scan complete. Open Research to process saved research matches.");
            }
        }
        finally
        {
            _database.CompleteScan(scanId, result);
        }
        return result;
    }

    private int ResolveCollectionId(
        string? detectedName,
        bool detectedFromFilename,
        int? assignedCollectionId,
        IReadOnlyDictionary<string, int> collectionLookup,
        int unsortedCollectionId)
    {
        if (detectedFromFilename && !string.IsNullOrWhiteSpace(detectedName) &&
            collectionLookup.TryGetValue(detectedName.Trim(), out var explicitCollectionId))
            return explicitCollectionId;
        if (assignedCollectionId.HasValue) return assignedCollectionId.Value;
        if (!string.IsNullOrWhiteSpace(detectedName) && collectionLookup.TryGetValue(detectedName.Trim(), out var collectionId))
            return collectionId;
        if (unsortedCollectionId > 0) return unsortedCollectionId;
        return _database.ResolveCollectionId(detectedName, null);
    }

    private static bool IsUnchanged(
        LibraryScanFileSnapshot? previous,
        FileInfo current,
        int collectionId,
        EpisodeStorageState storageState)
    {
        if (previous is null || previous.WasMissing) return false;
        if (previous.FileSize != current.Length || previous.CollectionId != collectionId || previous.StorageState != storageState)
            return false;

        // Some file systems round write times to a coarser precision than SQLite's
        // round-trip timestamp. A one-second tolerance avoids needless tag reads.
        return Math.Abs((previous.ModifiedUtc - current.LastWriteTimeUtc).TotalSeconds) <= 1;
    }

    private void ApplyInspectedMetadata(
        long episodeId,
        string file,
        TheRadioVault.Core.Models.ParsedFilename parsed,
        TheRadioVault.Media.Models.MediaInspection inspection)
    {
        var title = inspection.Metadata.Title;
        if (!TitleQualityService.IsMeaningful(title, parsed.CollectionName, Path.GetFileName(file)))
            title = parsed.HeadlineConfidence == "High" ? parsed.HeadlineCandidate : null;

        _database.ApplyScannedMetadata(
            episodeId,
            new ScannedAudioMetadata
            {
                Title = title,
                Description = inspection.Metadata.Description,
                Guests = inspection.Metadata.Performers.ToArray(),
                Tags = inspection.Metadata.Genres.ToArray(),
                DurationMs = inspection.Metadata.DurationMs,
                ArtworkBytes = inspection.Metadata.ArtworkBytes,
                ArtworkMimeType = inspection.Metadata.ArtworkMimeType
            },
            inspection.CachedArtworkPath);
    }

    private void ReconcileSavedResearch(long episodeId, string file, ScanResult result, IProgress<string>? progress)
    {
        var reconciliation = _database.ReconcileMissingResearchForEpisode(episodeId);
        result.ResearchApplied += reconciliation.Applied;
        result.ResearchAmbiguous += reconciliation.Ambiguous + reconciliation.Invalid;
        result.ResearchCandidatesFound += reconciliation.CandidatesFound;
        result.PreviouslyMissingMatches += reconciliation.PreviouslyMissingMatches;
        result.AlternateCaptureCandidates += reconciliation.AlternateCaptureCandidates;
        if (reconciliation.CandidatesFound > 0)
            progress?.Report($"Saved research match for {Path.GetFileName(file)} is ready to review");
        else if (reconciliation.Invalid > 0)
            progress?.Report($"Saved research for {Path.GetFileName(file)} could not be compared");
    }
}
