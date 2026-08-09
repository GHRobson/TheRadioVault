using System.Globalization;
using System.Text.Json;
using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile;

public sealed class MobileDownloadService
{
    private readonly MobileServerClient _server;
    private readonly string _rootDirectory;
    private readonly string _mediaDirectory;
    private readonly string _stagingDirectory;
    private readonly string _indexPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<long, MobileDownloadRecord> _records = [];
    private bool _loaded;

    public MobileDownloadService(MobileServerClient server, string rootDirectory)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("A downloads directory is required.", nameof(rootDirectory));
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _mediaDirectory = Path.Combine(_rootDirectory, "media");
        _stagingDirectory = Path.Combine(_rootDirectory, "staging");
        _indexPath = Path.Combine(_rootDirectory, "downloads.json");
    }

    public async Task<IReadOnlyList<MobileBroadcastItem>> GetBroadcastsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return _records.Values
                .OrderByDescending(record => record.DownloadedAt)
                .Select(record => new MobileBroadcastItem(record.Summary))
                .ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> IsDownloadedAsync(long episodeId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return _records.ContainsKey(episodeId);
        }
        finally { _gate.Release(); }
    }

    public async Task<MobileDownloadRecord?> GetAsync(long episodeId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return _records.GetValueOrDefault(episodeId);
        }
        finally { _gate.Release(); }
    }

    public async Task<MobileDownloadRecord> DownloadAsync(
        MobileBroadcastItem broadcast,
        IProgress<MobileDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broadcast);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var summaryTask = _server.GetBroadcastSummaryAsync(broadcast.EpisodeId, cancellationToken);
            var manifestTask = _server.GetMediaManifestAsync(broadcast.EpisodeId, cancellationToken);
            await Task.WhenAll(summaryTask, manifestTask).ConfigureAwait(false);
            var summary = await summaryTask.ConfigureAwait(false);
            var manifest = await manifestTask.ConfigureAwait(false);
            var parts = ValidateManifest(manifest);

            EnsureDirectories();
            var generation = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) +
                             "-" + Guid.NewGuid().ToString("N");
            var relativeDirectory = Path.Combine("media", manifest.EpisodeId.ToString(CultureInfo.InvariantCulture), generation);
            var stagingPath = Path.Combine(_stagingDirectory, manifest.EpisodeId.ToString(CultureInfo.InvariantCulture));
            var finalPath = Path.Combine(_rootDirectory, relativeDirectory);
            Directory.CreateDirectory(stagingPath);
            var downloadedParts = new List<MobileDownloadPart>(parts.Length);
            var totalBytes = parts.Sum(part => Math.Max(0, part.SizeBytes));
            var receivedBytes = parts.Sum(part => ExistingPartBytes(stagingPath, part.PartNumber, part.MediaFileId, part.SizeBytes));
            var promoted = false;
            var committed = false;
            var existing = _records.GetValueOrDefault(manifest.EpisodeId);

            try
            {
                for (var index = 0; index < parts.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var part = parts[index];
                    var prefix = $"part-{part.PartNumber:D3}-{part.MediaFileId}";
                    var completedPath = Directory.EnumerateFiles(stagingPath, prefix + ".*")
                        .FirstOrDefault(path => !path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase));
                    if (completedPath is not null)
                    {
                        var completedBytes = new FileInfo(completedPath).Length;
                        if ((part.SizeBytes <= 0 && completedBytes > 0) || completedBytes == part.SizeBytes)
                        {
                            downloadedParts.Add(CreateDownloadedPart(part, completedPath, completedBytes));
                            continue;
                        }
                        File.Delete(completedPath);
                        receivedBytes = Math.Max(0, receivedBytes - completedBytes);
                    }

                    var partialPath = Directory.EnumerateFiles(stagingPath, prefix + ".*.partial").FirstOrDefault();
                    var partialBytes = partialPath is null ? 0 : new FileInfo(partialPath).Length;
                    if (partialPath is not null && part.SizeBytes > 0 && partialBytes == part.SizeBytes)
                    {
                        var completedFromPartial = partialPath[..^".partial".Length];
                        File.Move(partialPath, completedFromPartial, overwrite: true);
                        downloadedParts.Add(CreateDownloadedPart(part, completedFromPartial, partialBytes));
                        continue;
                    }
                    if (partialPath is not null && part.SizeBytes > 0 && partialBytes > part.SizeBytes)
                    {
                        File.Delete(partialPath);
                        receivedBytes = Math.Max(0, receivedBytes - partialBytes);
                        partialPath = null;
                        partialBytes = 0;
                    }
                    var route = WebApiRoutes.MediaPart(manifest.EpisodeId, part.MediaFileId) +
                                "?recording=" + Uri.EscapeDataString(manifest.RecordingKey);
                    using var response = await _server.OpenResponseAsync(
                        route,
                        partialBytes > 0 ? $"bytes={partialBytes}-" : null,
                        cancellationToken).ConfigureAwait(false);
                    var mediaType = response.Content.Headers.ContentType?.MediaType?.Trim().ToLowerInvariant()
                                    ?? "application/octet-stream";
                    var fileName = $"part-{part.PartNumber:D3}-{part.MediaFileId}{ExtensionFor(mediaType)}";
                    var path = Path.Combine(stagingPath, fileName);
                    var expectedPartialPath = path + ".partial";
                    if (partialPath is not null && !string.Equals(partialPath, expectedPartialPath, StringComparison.Ordinal))
                    {
                        File.Move(partialPath, expectedPartialPath, overwrite: true);
                        partialPath = expectedPartialPath;
                    }
                    partialPath ??= expectedPartialPath;
                    var canAppend = partialBytes > 0 &&
                                    response.StatusCode == System.Net.HttpStatusCode.PartialContent &&
                                    response.Content.Headers.ContentRange?.From == partialBytes;
                    if (!canAppend && partialBytes > 0)
                    {
                        File.Delete(partialPath);
                        receivedBytes = Math.Max(0, receivedBytes - partialBytes);
                        partialBytes = 0;
                    }
                    var expectedBytes = part.SizeBytes > 0
                        ? part.SizeBytes
                        : Math.Max(0, response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength ?? 0);
                    var partBytes = await CopyResponseAsync(
                        response,
                        partialPath,
                        canAppend,
                        partialBytes,
                        bytes => progress?.Report(new MobileDownloadProgress(
                            manifest.EpisodeId,
                            broadcast.Title,
                            part.PartNumber,
                            parts.Length,
                            receivedBytes - partialBytes + bytes,
                            totalBytes)),
                        cancellationToken).ConfigureAwait(false);
                    if (expectedBytes > 0 && partBytes != expectedBytes)
                        throw new IOException(
                            $"Part {part.PartNumber} contained {partBytes:N0} bytes; the server declared {expectedBytes:N0} bytes.");
                    receivedBytes += partBytes - partialBytes;
                    File.Move(partialPath, path, overwrite: true);
                    downloadedParts.Add(new MobileDownloadPart(
                        part.PartNumber,
                        part.PartTotal,
                        Math.Max(0, part.LogicalStartMs),
                        Math.Max(part.LogicalStartMs, part.LogicalEndMs),
                        part.MediaFileId,
                        partBytes,
                        fileName,
                        mediaType));
                }

                var expectedFiles = downloadedParts.Select(part => part.FileName).ToHashSet(StringComparer.Ordinal);
                foreach (var path in Directory.EnumerateFiles(stagingPath))
                    if (!expectedFiles.Contains(Path.GetFileName(path))) File.Delete(path);
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                Directory.Move(stagingPath, finalPath);
                promoted = true;
                var record = new MobileDownloadRecord(
                    summary,
                    manifest.CanonicalKey,
                    manifest.RecordingKey,
                    Math.Max(summary.DurationMs, manifest.DurationMs),
                    DateTimeOffset.UtcNow,
                    relativeDirectory,
                    downloadedParts);
                _records[record.EpisodeId] = record;
                try
                {
                    await SaveIndexUnsafeAsync(cancellationToken).ConfigureAwait(false);
                    committed = true;
                }
                catch
                {
                    if (existing is null) _records.Remove(record.EpisodeId);
                    else _records[record.EpisodeId] = existing;
                    throw;
                }
                if (existing is not null) DeleteRecordMediaBestEffort(existing);
                progress?.Report(new MobileDownloadProgress(
                    record.EpisodeId,
                    broadcast.Title,
                    parts[^1].PartNumber,
                    parts.Length,
                    record.SizeBytes,
                    record.SizeBytes));
                return record;
            }
            catch
            {
                if (!committed && promoted) DeleteDirectoryBestEffort(finalPath);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(long episodeId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (!_records.Remove(episodeId, out var record)) return;
            await SaveIndexUnsafeAsync(cancellationToken).ConfigureAwait(false);
            DeleteRecordMediaBestEffort(record);
            DeleteDirectoryBestEffort(PendingPath(episodeId));
        }
        finally { _gate.Release(); }
    }

    public async Task DiscardPendingAsync(long episodeId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { DeleteDirectoryBestEffort(PendingPath(episodeId)); }
        finally { _gate.Release(); }
    }

    public async Task<MobileDownloadStorage> GetStorageAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var completed = _records.Values.Sum(record => Math.Max(0, record.SizeBytes));
            var pending = Directory.Exists(_stagingDirectory)
                ? Directory.EnumerateFiles(_stagingDirectory, "*", SearchOption.AllDirectories)
                    .Sum(path => Math.Max(0, new FileInfo(path).Length))
                : 0;
            return new MobileDownloadStorage(_records.Count, completed, pending);
        }
        finally { _gate.Release(); }
    }

    public string GetPartUri(MobileDownloadRecord record, MobileDownloadPart part)
    {
        var path = ResolvePartPath(record, part);
        if (!File.Exists(path)) throw new FileNotFoundException("A downloaded media part is missing.", path);
        return new Uri(path).AbsoluteUri;
    }

    public async Task UpdateProgressAsync(
        long episodeId,
        long positionMs,
        bool completed,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (!_records.TryGetValue(episodeId, out var record)) return;
            var summary = record.Summary with
            {
                PositionMs = Math.Max(0, positionMs),
                Completed = completed,
                InProgress = !completed && positionMs > 0,
                LastPlayedAt = DateTimeOffset.UtcNow
            };
            _records[episodeId] = record with { Summary = summary };
            await SaveIndexUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureLoadedUnsafeAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        EnsureDirectories();
        _records.Clear();
        if (File.Exists(_indexPath))
        {
            try
            {
                await using var stream = new FileStream(
                    _indexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var index = await JsonSerializer.DeserializeAsync(
                    stream, MobileJsonContext.Default.MobileDownloadIndex, cancellationToken).ConfigureAwait(false);
                foreach (var record in index?.Downloads ?? [])
                {
                    if (record.EpisodeId <= 0 || record.Parts.Count == 0 || !IsHealthy(record)) continue;
                    _records[record.EpisodeId] = record;
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.WriteLine($"[iOS downloads] Could not load the download index: {exception}");
            }
        }
        _loaded = true;
    }

    private async Task SaveIndexUnsafeAsync(CancellationToken cancellationToken)
    {
        EnsureDirectories();
        var temporary = _indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new MobileDownloadIndex { Downloads = _records.Values.ToList() },
                    MobileJsonContext.Default.MobileDownloadIndex,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _indexPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private bool IsHealthy(MobileDownloadRecord record)
    {
        try
        {
            return record.Parts.All(part =>
            {
                var path = ResolvePartPath(record, part);
                return File.Exists(path) && (part.SizeBytes <= 0 || new FileInfo(path).Length == part.SizeBytes);
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private string ResolvePartPath(MobileDownloadRecord record, MobileDownloadPart part)
    {
        if (Path.IsPathRooted(record.StorageDirectory) || Path.IsPathRooted(part.FileName))
            throw new InvalidOperationException("A download contains an invalid path.");
        var path = Path.GetFullPath(Path.Combine(_rootDirectory, record.StorageDirectory, part.FileName));
        var prefix = _rootDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("A download escaped its storage directory.");
        return path;
    }

    private static WebCanonicalMediaPart[] ValidateManifest(WebCanonicalMediaManifest manifest)
    {
        if (manifest.EpisodeId <= 0 || manifest.Parts.Count == 0)
            throw new InvalidOperationException("The server did not provide downloadable media parts.");
        var parts = manifest.Parts.OrderBy(part => part.PartNumber).ToArray();
        if (parts.Any(part => part.PartNumber <= 0 || part.MediaFileId <= 0 ||
                              part.LogicalStartMs < 0 || part.LogicalEndMs < part.LogicalStartMs ||
                              part.SizeBytes < 0) ||
            parts.Select(part => part.PartNumber).Distinct().Count() != parts.Length)
            throw new InvalidOperationException("The server returned an invalid download manifest.");
        return parts;
    }

    private static async Task<long> CopyResponseAsync(
        HttpResponseMessage response,
        string targetPath,
        bool append,
        long initialBytes,
        Action<long> progress,
        CancellationToken cancellationToken)
    {
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(
            targetPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        var buffer = new byte[128 * 1024];
        var total = Math.Max(0, initialBytes);
        progress(total);
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
            progress(total);
        }
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        target.Flush(flushToDisk: true);
        return total;
    }

    private static string ExtensionFor(string mediaType) => mediaType switch
    {
        "audio/mpeg" => ".mp3",
        "audio/mp4" or "audio/x-m4a" => ".m4a",
        "audio/aac" => ".aac",
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/flac" or "audio/x-flac" => ".flac",
        _ => ".audio"
    };

    private static string MediaTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".aac" => "audio/aac",
        ".wav" => "audio/wav",
        ".flac" => "audio/flac",
        _ => "application/octet-stream"
    };

    private static MobileDownloadPart CreateDownloadedPart(
        WebCanonicalMediaPart part,
        string path,
        long sizeBytes)
        => new(
            part.PartNumber,
            part.PartTotal,
            Math.Max(0, part.LogicalStartMs),
            Math.Max(part.LogicalStartMs, part.LogicalEndMs),
            part.MediaFileId,
            sizeBytes,
            Path.GetFileName(path),
            MediaTypeFor(path));

    private static long ExistingPartBytes(
        string stagingPath,
        int partNumber,
        long mediaFileId,
        long expectedBytes)
    {
        var prefix = $"part-{partNumber:D3}-{mediaFileId}.*";
        return Directory.EnumerateFiles(stagingPath, prefix)
            .Select(path => Math.Max(0, new FileInfo(path).Length))
            .Where(length => expectedBytes <= 0 || length <= expectedBytes)
            .DefaultIfEmpty(0)
            .Max();
    }

    private string PendingPath(long episodeId)
        => Path.Combine(_stagingDirectory, episodeId.ToString(CultureInfo.InvariantCulture));

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(_rootDirectory);
        Directory.CreateDirectory(_mediaDirectory);
        Directory.CreateDirectory(_stagingDirectory);
    }

    private void DeleteRecordMediaBestEffort(MobileDownloadRecord record)
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(_rootDirectory, record.StorageDirectory));
            var prefix = _mediaDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (path.StartsWith(prefix, StringComparison.Ordinal)) DeleteDirectoryBestEffort(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
