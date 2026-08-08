using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text.Json;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Services;

/// <summary>
/// Persistent, foreground-only native download store. Every transfer is staged
/// on the destination volume and a small JSON record becomes the commit point
/// only after all canonical media parts have been flushed and promoted.
/// </summary>
public sealed class NativeDownloadService : INativeDownloadService
{
    private const int CopyBufferSize = 256 * 1024;
    private const long MaximumArtworkBytes = 20L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly LoopbackServerClient _connection;
    private readonly string _rootDirectory;
    private readonly string _recordsDirectory;
    private readonly string _mediaDirectory;
    private readonly string _stagingDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _transferGate = new(1, 1);
    private readonly Dictionary<long, NativeDownloadRecord> _records = new();
    private bool _loaded;

    public NativeDownloadService(LoopbackServerClient connection, string rootDirectory)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("A native download root is required.", nameof(rootDirectory));

        _rootDirectory = Path.GetFullPath(rootDirectory);
        var volumeRoot = Path.GetPathRoot(_rootDirectory);
        if (!string.IsNullOrEmpty(volumeRoot) &&
            string.Equals(
                _rootDirectory,
                Path.GetFullPath(volumeRoot),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("The filesystem root cannot be used as the native download store.", nameof(rootDirectory));

        _recordsDirectory = Path.Combine(_rootDirectory, "records");
        _mediaDirectory = Path.Combine(_rootDirectory, "media");
        _stagingDirectory = Path.Combine(_rootDirectory, "staging");
    }

    public event EventHandler? DownloadsChanged;

    public async Task<IReadOnlyList<NativeDownloadRecord>> GetDownloadsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            await RefreshHealthUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return _records.Values
                .OrderByDescending(record => record.DownloadedAt)
                .ThenBy(record => record.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeDownloadRecord?> GetAsync(
        long representativeEpisodeId,
        CancellationToken cancellationToken = default)
    {
        if (representativeEpisodeId <= 0) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var record = FindRecordUnsafe(string.Empty, representativeEpisodeId);
            if (record is null) return null;
            return await InspectAndPersistUnsafeAsync(record, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeDownloadRecord> DownloadAsync(
        long representativeEpisodeId,
        IProgress<NativeDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (representativeEpisodeId <= 0)
            throw new ArgumentOutOfRangeException(nameof(representativeEpisodeId));

        NativeDownloadRecord completed;
        await _transferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
            completed = await DownloadUnsafeAsync(representativeEpisodeId, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _transferGate.Release();
        }

        NotifyDownloadsChanged();
        return completed;
    }

    public async Task RemoveAsync(
        long representativeEpisodeId,
        CancellationToken cancellationToken = default)
    {
        if (representativeEpisodeId <= 0) return;
        NativeDownloadRecord? removed = null;
        await _transferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
                removed = FindRecordUnsafe(string.Empty, representativeEpisodeId);
                if (removed is null) return;

                var recordPath = RecordPath(removed.RepresentativeEpisodeId);
                if (File.Exists(recordPath)) File.Delete(recordPath);
                _records.Remove(removed.RepresentativeEpisodeId);
            }
            finally
            {
                _gate.Release();
            }
            DeleteRecordMediaBestEffort(removed);
        }
        finally
        {
            _transferGate.Release();
        }

        if (removed is not null) NotifyDownloadsChanged();
    }

    public async Task RemoveAllAsync(CancellationToken cancellationToken = default)
    {
        var changed = false;
        await _transferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
                foreach (var record in _records.Values.ToArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var recordPath = RecordPath(record.RepresentativeEpisodeId);
                    if (File.Exists(recordPath)) File.Delete(recordPath);
                    _records.Remove(record.RepresentativeEpisodeId);
                    changed = true;
                    DeleteRecordMediaBestEffort(record);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            _transferGate.Release();
            if (changed) NotifyDownloadsChanged();
        }
    }

    public async Task<NativeDownloadAuditResult> AuditAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            await RefreshHealthUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var records = _records.Values.ToArray();
            return new NativeDownloadAuditResult(
                records.Length,
                records.Count(record => !record.NeedsRepair),
                records.Count(record => record.NeedsRepair),
                records.Sum(record => Math.Max(0, record.SizeBytes)));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdatePlaybackStateAsync(
        LocalPlaybackSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RepresentativeEpisodeId <= 0) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var record = FindRecordUnsafe(request.CanonicalKey, request.RepresentativeEpisodeId);
            if (record is null) return;

            var duration = Math.Max(record.DurationMs, Math.Max(0, request.DurationMs));
            var position = request.Completed
                ? 0
                : Math.Clamp(request.PositionMs, 0, Math.Max(0, duration));
            var speed = NormalizePlaybackSpeed(request.PlaybackSpeed);
            if (record.ResumePositionMs == position &&
                record.DurationMs == duration &&
                Math.Abs(record.PlaybackSpeed - speed) < 0.001d &&
                record.Completed == request.Completed)
                return;

            var updated = record with
            {
                ResumePositionMs = position,
                DurationMs = duration,
                PlaybackSpeed = speed,
                Completed = request.Completed
            };
            await SaveRecordUnsafeAsync(updated, cancellationToken).ConfigureAwait(false);
            _records.Remove(record.RepresentativeEpisodeId);
            _records[updated.RepresentativeEpisodeId] = updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalPlaybackDescriptor?> TryPrepareAsync(
        string canonicalKey,
        long representativeEpisodeId,
        CancellationToken cancellationToken = default)
    {
        if (representativeEpisodeId <= 0) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var record = FindRecordUnsafe(canonicalKey, representativeEpisodeId);
            if (record is null) return null;
            record = await InspectAndPersistUnsafeAsync(record, cancellationToken).ConfigureAwait(false);
            if (record.NeedsRepair) return null;

            var segments = new List<LocalPlaybackSegment>(record.Parts.Count);
            foreach (var part in record.Parts.OrderBy(part => part.PartNumber))
            {
                if (!TryResolveMediaPath(record, part, out var mediaPath, out _)) return null;
                segments.Add(new LocalPlaybackSegment(
                    part.PartNumber,
                    part.PartTotal,
                    Math.Max(0, part.LogicalStartMs),
                    Math.Max(part.LogicalStartMs, part.LogicalEndMs),
                    mediaPath,
                    Math.Max(0, part.LogicalEndMs - part.LogicalStartMs)));
            }

            if (segments.Count == 0) return null;
            var duration = Math.Max(record.DurationMs, segments.Max(segment => segment.LogicalEndMs));
            var resume = record.Completed
                ? 0
                : Math.Clamp(record.ResumePositionMs, 0, Math.Max(0, duration));
            return new LocalPlaybackDescriptor(
                record.CanonicalKey,
                record.RepresentativeEpisodeId,
                record.BroadcastId,
                record.Title,
                record.CollectionName,
                record.AirDate,
                record.ArtworkPath,
                resume,
                duration,
                NormalizePlaybackSpeed(record.PlaybackSpeed),
                record.Completed,
                record.Favourite,
                segments);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NativeDownloadRecord> DownloadUnsafeAsync(
        long requestedEpisodeId,
        IProgress<NativeDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var summaryTask = _connection.GetJsonOrNullAsync<BroadcastEnvelope>(
            WebApiRoutes.ClientLibraryBroadcast(requestedEpisodeId), cancellationToken);
        var manifestTask = _connection.GetJsonOrNullAsync<WebCanonicalMediaManifest>(
            WebApiRoutes.MediaManifest(requestedEpisodeId), cancellationToken);
        await Task.WhenAll(summaryTask, manifestTask).ConfigureAwait(false);

        var summary = (await summaryTask.ConfigureAwait(false))?.Broadcast
            ?? throw new InvalidOperationException("The selected broadcast no longer exists on Radio Vault Server.");
        var manifest = await manifestTask.ConfigureAwait(false)
            ?? throw new FileNotFoundException("Radio Vault Server could not resolve a complete downloadable recording for this broadcast.");
        ValidateManifest(summary, manifest);

        EnsureDirectories();
        var episodeId = manifest.EpisodeId;
        var generation = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        var stagingPath = Path.Combine(_stagingDirectory, generation);
        var generationRelativePath = Path.Combine("media", episodeId.ToString(CultureInfo.InvariantCulture), generation);
        var finalPath = Path.Combine(_rootDirectory, generationRelativePath);
        var promoted = false;
        var recordCommitted = false;
        var downloadedParts = new List<NativeDownloadPart>(manifest.Parts.Count);
        var totalBytes = SumKnownBytes(manifest.Parts);
        var receivedBytes = 0L;
        var title = string.IsNullOrWhiteSpace(summary.Title)
            ? summary.AirDate?.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture) ?? summary.BroadcastId
            : summary.Title.Trim();

        Directory.CreateDirectory(stagingPath);
        try
        {
            var orderedParts = manifest.Parts.OrderBy(part => part.PartNumber).ToArray();
            ReportProgress(progress, new NativeDownloadProgress(
                episodeId, title, orderedParts[0].PartNumber, orderedParts.Length, 0, totalBytes));

            for (var index = 0; index < orderedParts.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var part = orderedParts[index];
                var route = WebApiRoutes.MediaPart(episodeId, part.MediaFileId) +
                            "?recording=" + Uri.EscapeDataString(manifest.RecordingKey);
                using var response = await _connection.OpenResponseAsync(
                    HttpMethod.Get, route, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw CreateMediaError(response.StatusCode, part.PartNumber);

                var mediaType = response.Content.Headers.ContentType?.MediaType?.Trim().ToLowerInvariant()
                                ?? "application/octet-stream";
                var fileName = $"part-{part.PartNumber:D3}-{part.MediaFileId}{ExtensionFor(mediaType)}";
                var stagedFile = Path.Combine(stagingPath, fileName);
                var expectedBytes = part.SizeBytes > 0
                    ? part.SizeBytes
                    : Math.Max(0, response.Content.Headers.ContentLength ?? 0);
                var partBytes = await CopyResponseAsync(
                    response,
                    stagedFile,
                    bytes => ReportProgress(progress, new NativeDownloadProgress(
                        episodeId,
                        title,
                        part.PartNumber,
                        orderedParts.Length,
                        receivedBytes + bytes,
                        totalBytes)),
                    cancellationToken).ConfigureAwait(false);

                if (expectedBytes > 0 && partBytes != expectedBytes)
                    throw new IOException(
                        $"Part {part.PartNumber} ended at {partBytes:N0} bytes; Radio Vault Server declared {expectedBytes:N0} bytes.");

                receivedBytes += partBytes;
                downloadedParts.Add(new NativeDownloadPart(
                    part.PartNumber,
                    part.PartTotal,
                    Math.Max(0, part.LogicalStartMs),
                    Math.Max(part.LogicalStartMs, part.LogicalEndMs),
                    part.MediaFileId,
                    partBytes,
                    Path.Combine(generationRelativePath, fileName),
                    mediaType));
            }

            var artworkFileName = await TryDownloadArtworkAsync(
                episodeId,
                stagingPath,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            Directory.Move(stagingPath, finalPath);
            promoted = true;

            var downloadedAt = DateTimeOffset.UtcNow;
            var record = new NativeDownloadRecord(
                episodeId,
                manifest.CanonicalKey,
                manifest.RecordingKey,
                summary.BroadcastId,
                title,
                summary.CollectionName,
                summary.AirDate,
                artworkFileName is null
                    ? LocalArtworkPath(summary.ArtworkPath)
                    : Path.Combine(finalPath, artworkFileName),
                summary.Completed ? 0 : Math.Max(0, summary.PositionMs),
                Math.Max(summary.DurationMs, manifest.DurationMs),
                1d,
                summary.Completed,
                summary.Favourite,
                downloadedAt,
                downloadedParts.Sum(part => Math.Max(0, part.SizeBytes)),
                downloadedParts,
                RepairState: string.Empty);

            NativeDownloadRecord? existing;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                existing = FindRecordUnsafe(manifest.CanonicalKey, episodeId);
                await SaveRecordUnsafeAsync(record, cancellationToken).ConfigureAwait(false);
                _records[episodeId] = record;
                recordCommitted = true;
                if (existing is not null && existing.RepresentativeEpisodeId != episodeId)
                {
                    _records.Remove(existing.RepresentativeEpisodeId);
                    DeleteRecordFileBestEffort(existing.RepresentativeEpisodeId);
                }
            }
            finally
            {
                _gate.Release();
            }
            if (existing is not null) DeleteRecordMediaBestEffort(existing);
            ReportProgress(progress, new NativeDownloadProgress(
                episodeId, title, orderedParts[^1].PartNumber, orderedParts.Length,
                record.SizeBytes, record.SizeBytes));
            return record;
        }
        catch
        {
            if (promoted && !recordCommitted) DeleteDirectoryBestEffort(finalPath, _mediaDirectory);
            else DeleteDirectoryBestEffort(stagingPath, _stagingDirectory);
            throw;
        }
    }

    private async Task EnsureLoadedUnsafeAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        EnsureDirectories();
        _records.Clear();
        foreach (var path in Directory.EnumerateFiles(_recordsDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var record = await JsonSerializer.DeserializeAsync<NativeDownloadRecord>(
                    stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                if (record is null || record.RepresentativeEpisodeId <= 0 ||
                    record.Parts is null || record.Parts.Count == 0 || record.Parts.Any(part => part is null))
                    continue;
                _records[record.RepresentativeEpisodeId] = record;
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Write("Native downloads", $"The download record '{Path.GetFileName(path)}' could not be read.", exception);
            }
        }
        _loaded = true;
    }

    private async Task RefreshHealthUnsafeAsync(CancellationToken cancellationToken)
    {
        foreach (var record in _records.Values.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await InspectAndPersistUnsafeAsync(record, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<NativeDownloadRecord> InspectAndPersistUnsafeAsync(
        NativeDownloadRecord record,
        CancellationToken cancellationToken)
    {
        var repairState = Inspect(record, out var actualBytes);
        var updated = record;
        if (!string.Equals(record.RepairState, repairState, StringComparison.Ordinal) ||
            (!record.NeedsRepair && actualBytes != record.SizeBytes))
        {
            updated = record with
            {
                RepairState = repairState,
                SizeBytes = string.IsNullOrEmpty(repairState) ? actualBytes : record.SizeBytes
            };
            await SaveRecordUnsafeAsync(updated, cancellationToken).ConfigureAwait(false);
            _records[updated.RepresentativeEpisodeId] = updated;
        }
        return updated;
    }

    private string Inspect(NativeDownloadRecord record, out long actualBytes)
    {
        actualBytes = 0;
        if (record.Parts is null || record.Parts.Count == 0) return "missing-parts";
        string? generationDirectory = null;
        foreach (var part in record.Parts)
        {
            if (part is null) return "invalid-path";
            if (!TryResolveMediaPath(record, part, out var path, out var partGenerationDirectory))
                return "invalid-path";
            if (generationDirectory is not null &&
                !string.Equals(generationDirectory, partGenerationDirectory, PathComparison))
                return "invalid-path";
            generationDirectory = partGenerationDirectory;
            if (!File.Exists(path)) return "missing-file";
            long length;
            try { length = new FileInfo(path).Length; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return "unreadable-file";
            }
            if (part.SizeBytes > 0 && length != part.SizeBytes) return "size-mismatch";
            actualBytes = checked(actualBytes + Math.Max(0, length));
        }
        return string.Empty;
    }

    private async Task SaveRecordUnsafeAsync(
        NativeDownloadRecord record,
        CancellationToken cancellationToken)
    {
        EnsureDirectories();
        var target = RecordPath(record.RepresentativeEpisodeId);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<long> CopyResponseAsync(
        HttpResponseMessage response,
        string targetPath,
        Action<long> progress,
        CancellationToken cancellationToken,
        long maximumBytes = long.MaxValue)
    {
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(
            targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long written = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0) break;
                if (written > maximumBytes - read)
                    throw new InvalidDataException("The response exceeded the permitted download size.");
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written = checked(written + read);
                progress(written);
            }
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            target.Flush(flushToDisk: true);
            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<string?> TryDownloadArtworkAsync(
        long episodeId,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        string? targetPath = null;
        try
        {
            using var response = await _connection.OpenResponseAsync(
                HttpMethod.Get,
                WebApiRoutes.Artwork(episodeId),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var mediaType = response.Content.Headers.ContentType?.MediaType?.Trim().ToLowerInvariant();
            var extension = ArtworkExtensionFor(mediaType);
            if (extension is null) return null;
            if (response.Content.Headers.ContentLength is > MaximumArtworkBytes) return null;

            var fileName = "artwork" + extension;
            targetPath = Path.Combine(stagingPath, fileName);
            var written = await CopyResponseAsync(
                response,
                targetPath,
                _ => { },
                cancellationToken,
                MaximumArtworkBytes).ConfigureAwait(false);
            if (written <= 0)
            {
                File.Delete(targetPath);
                return null;
            }
            return fileName;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write("Native downloads", "Optional downloaded artwork could not be stored.", exception);
            try { if (targetPath is not null && File.Exists(targetPath)) File.Delete(targetPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }
    }

    private NativeDownloadRecord? FindRecordUnsafe(string canonicalKey, long representativeEpisodeId)
    {
        if (_records.TryGetValue(representativeEpisodeId, out var exact)) return exact;
        if (string.IsNullOrWhiteSpace(canonicalKey)) return null;
        return _records.Values.FirstOrDefault(record =>
            string.Equals(record.CanonicalKey, canonicalKey, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateManifest(
        WebClientLibraryBroadcastSummary summary,
        WebCanonicalMediaManifest manifest)
    {
        if (manifest.EpisodeId <= 0 || summary.RepresentativeEpisodeId <= 0 ||
            manifest.EpisodeId != summary.RepresentativeEpisodeId)
            throw new InvalidDataException("Radio Vault Server returned mismatched broadcast identities for the download.");
        if (!string.IsNullOrWhiteSpace(summary.CanonicalKey) &&
            !string.Equals(summary.CanonicalKey, manifest.CanonicalKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Radio Vault Server returned mismatched canonical identities for the download.");
        if (string.IsNullOrWhiteSpace(manifest.CanonicalKey) || string.IsNullOrWhiteSpace(manifest.RecordingKey))
            throw new InvalidDataException("Radio Vault Server returned an incomplete canonical download identity.");
        if (manifest.DurationMs < 0)
            throw new InvalidDataException("Radio Vault Server returned an invalid canonical duration.");
        if (manifest.Parts is null || manifest.Parts.Count == 0)
            throw new FileNotFoundException("Radio Vault Server returned an empty canonical download plan.");
        if (manifest.Parts.Any(part => part.PartNumber <= 0 || part.MediaFileId <= 0 ||
                                      part.LogicalStartMs < 0 || part.LogicalEndMs < part.LogicalStartMs ||
                                      part.SizeBytes < 0))
            throw new InvalidDataException("Radio Vault Server returned an invalid canonical media part.");
        if (manifest.Parts.Select(part => part.MediaFileId).Distinct().Count() != manifest.Parts.Count ||
            manifest.Parts.Select(part => part.PartNumber).Distinct().Count() != manifest.Parts.Count)
            throw new InvalidDataException("Radio Vault Server returned duplicate canonical media parts.");
    }

    private static long SumKnownBytes(IEnumerable<WebCanonicalMediaPart> parts)
    {
        long total = 0;
        foreach (var part in parts)
        {
            if (part.SizeBytes <= 0) return 0;
            total = checked(total + Math.Max(0, part.SizeBytes));
        }
        return total;
    }

    private static double NormalizePlaybackSpeed(double speed)
        => double.IsFinite(speed) && speed > 0
            ? Math.Clamp(speed, 0.5d, 3d)
            : 1d;

    private static Exception CreateMediaError(HttpStatusCode statusCode, int partNumber)
        => statusCode == HttpStatusCode.NotFound
            ? new FileNotFoundException($"Part {partNumber} is no longer available from Radio Vault Server.")
            : new HttpRequestException(
                $"Radio Vault Server rejected part {partNumber} of the download ({(int)statusCode}).",
                null,
                statusCode);

    private static void ReportProgress(
        IProgress<NativeDownloadProgress>? progress,
        NativeDownloadProgress value)
    {
        if (progress is null) return;
        try { progress.Report(value); }
        catch (Exception exception)
        {
            DiagnosticLog.Write("Native downloads", "A download progress observer failed.", exception);
        }
    }

    private static string ExtensionFor(string mediaType)
        => mediaType switch
        {
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "audio/aac" => ".aac",
            "audio/wav" or "audio/wave" or "audio/x-wav" => ".wav",
            "audio/flac" or "audio/x-flac" => ".flac",
            "audio/ogg" => ".ogg",
            "audio/x-ms-wma" => ".wma",
            _ => ".media"
        };

    private static string? ArtworkExtensionFor(string? mediaType)
        => mediaType switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tiff",
            _ => null
        };

    private string? LocalArtworkPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch { return null; }
    }

    private string RecordPath(long episodeId)
        => Path.Combine(_recordsDirectory, episodeId.ToString(CultureInfo.InvariantCulture) + ".json");

    private void DeleteRecordFileBestEffort(long episodeId)
    {
        try
        {
            var path = RecordPath(episodeId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException exception)
        {
            DiagnosticLog.Write("Native downloads", $"The stale download record for episode {episodeId} could not be removed.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            DiagnosticLog.Write("Native downloads", $"The stale download record for episode {episodeId} could not be removed.", exception);
        }
    }

    private bool TryResolveRelativePath(string? relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return false;
        try
        {
            var candidate = Path.GetFullPath(Path.Combine(_rootDirectory, relativePath));
            if (!IsDescendant(candidate, _rootDirectory)) return false;
            fullPath = candidate;
            return true;
        }
        catch { return false; }
    }

    private bool TryResolveMediaPath(
        NativeDownloadRecord record,
        NativeDownloadPart part,
        out string fullPath,
        out string generationDirectory)
    {
        generationDirectory = string.Empty;
        if (!TryResolveRelativePath(part.RelativePath, out fullPath)) return false;
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory)) return false;
        var episodeDirectory = Path.Combine(
            _mediaDirectory,
            record.RepresentativeEpisodeId.ToString(CultureInfo.InvariantCulture));
        if (!IsDescendant(directory, episodeDirectory) ||
            !string.Equals(Path.GetDirectoryName(directory), episodeDirectory, PathComparison))
            return false;
        generationDirectory = directory;
        return true;
    }

    private void DeleteRecordMediaBestEffort(NativeDownloadRecord record)
    {
        if (record.Parts is null) return;
        foreach (var directory in record.Parts
                     .Select(part => part is not null && TryResolveMediaPath(record, part, out _, out var generationDirectory)
                         ? generationDirectory
                         : null)
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(PathComparer))
            DeleteDirectoryBestEffort(directory!, _mediaDirectory);
    }

    private static void DeleteDirectoryBestEffort(string target, string allowedRoot)
    {
        try
        {
            var resolvedTarget = Path.GetFullPath(target);
            var resolvedRoot = Path.GetFullPath(allowedRoot);
            if (!IsDescendant(resolvedTarget, resolvedRoot) || !Directory.Exists(resolvedTarget)) return;
            Directory.Delete(resolvedTarget, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsDescendant(string path, string root)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(_rootDirectory);
        Directory.CreateDirectory(_recordsDirectory);
        Directory.CreateDirectory(_mediaDirectory);
        Directory.CreateDirectory(_stagingDirectory);
    }

    private void NotifyDownloadsChanged()
    {
        var handlers = DownloadsChanged;
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception exception)
            {
                DiagnosticLog.Write("Native downloads", "A download change observer failed.", exception);
            }
        }
    }

    private sealed record BroadcastEnvelope(WebClientLibraryBroadcastSummary Broadcast);
}
