using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Models;
using TheRadioVault.Services.Services;

namespace TheRadioVault.Services;

/// <summary>
/// Server-owned RSS poller. Feed addresses and credentials stay encrypted in
/// the authoritative database, downloads remain hidden behind .part files,
/// and only complete recordings enter a registered Library folder.
/// </summary>
public sealed class RssFeedIngestionService : IDisposable
{
    private const int MaximumFeedBytes = 8 * 1024 * 1024;
    private const long MaximumAudioBytes = 8L * 1024 * 1024 * 1024;
    private static readonly TimeSpan FeedTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadInactivityTimeout = TimeSpan.FromSeconds(90);
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".wav", ".wma" };

    private readonly RssFeedSubscriptionStore _store;
    private readonly RssFeedSecretProtector _protector;
    private readonly Func<CancellationToken, Task<bool>> _scanLibrary;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Timer? _timer;
    private bool _disposed;

    public RssFeedIngestionService(
        SqliteDatabase database,
        WebServerPreferences preferences,
        Func<CancellationToken, Task<bool>> scanLibrary,
        HttpClient? httpClient = null)
    {
        _store = new RssFeedSubscriptionStore(database ?? throw new ArgumentNullException(nameof(database)));
        _protector = new RssFeedSecretProtector(preferences ?? throw new ArgumentNullException(nameof(preferences)));
        _scanLibrary = scanLibrary ?? throw new ArgumentNullException(nameof(scanLibrary));
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? CreateHttpClient();
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public void Start()
    {
        ThrowIfDisposed();
        _timer ??= new Timer(_ => _ = PollSafelyAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public Task<IReadOnlyList<RssFeedSubscription>> GetAllAsync(CancellationToken cancellationToken = default)
        => _store.GetAllAsync(cancellationToken);

    public async Task<RssFeedSubscription> CreateAsync(
        RssFeedSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var normalized = RssFeedSecretProtector.Normalize(request.Source);
        var safeRequest = request with { Source = normalized };
        return await _store.CreateAsync(
            safeRequest,
            RssFeedSecretProtector.DisplayUrl(normalized),
            _protector.Protect(normalized),
            cancellationToken).ConfigureAwait(false);
    }

    public Task SetEnabledAsync(long id, bool enabled, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _store.SetEnabledAsync(id, enabled, cancellationToken);
    }

    public Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _store.DeleteAsync(id, cancellationToken);
    }

    public Task<RssFeedCheckResult> CheckNowAsync(long? feedId = null, CancellationToken cancellationToken = default)
        => RunAsync(feedId, force: true, cancellationToken);

    public Task<RssFeedCheckResult> RunIfDueAsync(CancellationToken cancellationToken = default)
        => RunAsync(feedId: null, force: false, cancellationToken);

    private async Task<RssFeedCheckResult> RunAsync(long? feedId, bool force, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linkedCancellation.Token;
        if (!await _runGate.WaitAsync(0, token).ConfigureAwait(false))
            return new RssFeedCheckResult(0, 0, 0, 0, false, "An RSS check is already running.");

        var checkedFeeds = 0;
        var downloaded = 0;
        var known = 0;
        var failures = 0;
        var scanStarted = false;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var feeds = await _store.GetDueAsync(now, feedId, force, token).ConfigureAwait(false);
            foreach (var feed in feeds)
            {
                token.ThrowIfCancellationRequested();
                var outcome = await CheckFeedAsync(feed, token).ConfigureAwait(false);
                checkedFeeds++;
                downloaded += outcome.Downloaded;
                known += outcome.Known;
                failures += outcome.Failures;
            }

            var awaitingScan = downloaded > 0 || await _store.HasDownloadsAwaitingScanAsync(token).ConfigureAwait(false);
            if (awaitingScan)
            {
                try
                {
                    scanStarted = await _scanLibrary(token).ConfigureAwait(false);
                    if (scanStarted)
                        await _store.MarkDownloadsScannedAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    failures++;
                    DiagnosticLog.Write("RSS ingestion", "Downloaded RSS audio is safe on disk, but the follow-up Library scan failed.", exception);
                }
            }

            var message = checkedFeeds == 0
                ? "No RSS feeds are due for a check."
                : downloaded == 0
                    ? $"Checked {checkedFeeds:N0} RSS feed{(checkedFeeds == 1 ? string.Empty : "s")}; no new broadcasts were found."
                    : $"Downloaded {downloaded:N0} new broadcast{(downloaded == 1 ? string.Empty : "s")} from {checkedFeeds:N0} RSS feed{(checkedFeeds == 1 ? string.Empty : "s")}.";
            if (failures > 0) message += $" {failures:N0} item{(failures == 1 ? string.Empty : "s")} need another attempt.";
            return new RssFeedCheckResult(checkedFeeds, downloaded, known, failures, scanStarted, message);
        }
        finally { _runGate.Release(); }
    }

    private async Task<FeedOutcome> CheckFeedAsync(RssFeedSubscriptionState state, CancellationToken cancellationToken)
    {
        var source = _protector.Unprotect(state.ProtectedSource);
        var feedUri = new Uri(source.FeedUrl);
        var now = DateTimeOffset.UtcNow;
        var downloaded = 0;
        var known = 0;
        var failures = 0;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, feedUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/rss+xml"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/atom+xml"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml", 0.9));
            if (!string.IsNullOrWhiteSpace(state.ETag))
                request.Headers.TryAddWithoutValidation("If-None-Match", state.ETag);
            if (DateTimeOffset.TryParse(state.LastModified, out var modified))
                request.Headers.IfModifiedSince = modified;
            AddAuthentication(request, source, feedUri);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(FeedTimeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                await _store.MarkCheckSucceededAsync(
                    state.Subscription.Id, state.ETag, state.LastModified, 0, 0, now, cancellationToken).ConfigureAwait(false);
                return new FeedOutcome(0, 0, 0);
            }
            response.EnsureSuccessStatusCode();
            var bytes = await ReadBoundedAsync(response, MaximumFeedBytes, timeout, cancellationToken).ConfigureAwait(false);
            var items = RssFeedDocumentParser.Parse(bytes, feedUri);
            var suppressInitial = !state.Subscription.Initialized && !state.Subscription.ImportExistingOnFirstCheck;
            var newlySeen = 0;

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var registration = await _store.RegisterItemAsync(
                    state.Subscription.Id,
                    new RssFeedItemCandidate(item.StableKey, item.Title, item.PublishedAt, item.EnclosureHash),
                    suppressInitial,
                    now,
                    cancellationToken).ConfigureAwait(false);
                if (registration.WasAdded && registration.Status == "Seen") newlySeen++;
                if (!registration.ShouldDownload)
                {
                    known++;
                    continue;
                }

                try
                {
                    var result = await DownloadAsync(state.Subscription, registration.Id, item, source, cancellationToken)
                        .ConfigureAwait(false);
                    if (result.CreatedNewFile) downloaded++;
                    else known++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    failures++;
                    var message = SafeDownloadError(exception);
                    await _store.MarkItemFailedAsync(registration.Id, message, cancellationToken).ConfigureAwait(false);
                    DiagnosticLog.Write("RSS ingestion", $"A broadcast from RSS feed ‘{state.Subscription.Name}’ could not be downloaded. {message}");
                }
            }

            var etag = response.Headers.ETag?.ToString() ?? state.ETag;
            var lastModified = response.Content.Headers.LastModified?.ToString("R") ?? state.LastModified;
            await _store.MarkCheckSucceededAsync(
                state.Subscription.Id, etag, lastModified, downloaded, newlySeen, now, cancellationToken).ConfigureAwait(false);
            if (failures > 0)
                await _store.MarkCheckFailedAsync(
                    state.Subscription.Id,
                    $"{failures:N0} broadcast{(failures == 1 ? string.Empty : "s")} could not be downloaded and will be retried.",
                    now,
                    cancellationToken).ConfigureAwait(false);
            return new FeedOutcome(downloaded, known, failures);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var message = SafeFeedError(exception);
            await _store.MarkCheckFailedAsync(state.Subscription.Id, message, now, cancellationToken).ConfigureAwait(false);
            DiagnosticLog.Write("RSS ingestion", $"RSS feed ‘{state.Subscription.Name}’ could not be checked. {message}");
            return new FeedOutcome(downloaded, known, failures + 1);
        }
    }

    private async Task<DownloadOutcome> DownloadAsync(
        RssFeedSubscription subscription,
        long itemId,
        RssFeedEnclosure item,
        RssFeedSource source,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(subscription.DestinationPath))
            throw new DirectoryNotFoundException("The selected Library folder is unavailable.");

        using var request = new HttpRequestMessage(HttpMethod.Get, item.EnclosureUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));
        AddAuthentication(request, source, item.EnclosureUri);
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(FeedTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The broadcast server did not respond within 30 seconds.");
        }
        using (response)
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumAudioBytes)
                throw new InvalidDataException("The broadcast is larger than Radio Vault’s 8 GB RSS download limit.");
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true ||
                mediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true ||
                mediaType?.Contains("xml", StringComparison.OrdinalIgnoreCase) == true ||
                mediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidDataException("The enclosure returned a document instead of an audio file.");

            var effectiveUri = response.RequestMessage?.RequestUri ?? item.EnclosureUri;
            var extension = ExtensionFor(effectiveUri, mediaType);
            var fileName = BuildFileName(item.Title, item.PublishedAt, extension);
            var incoming = Path.Combine(subscription.DestinationPath, ".radiovault-incoming");
            Directory.CreateDirectory(incoming);
            var temporary = Path.Combine(incoming, item.StableKey.Replace(':', '-') + ".part");
            try
            {
                var contentHash = await DownloadToTemporaryAsync(response, temporary, cancellationToken).ConfigureAwait(false);
                var existing = await _store.FindExistingContentPathAsync(contentHash, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(existing) && File.Exists(existing))
                {
                    File.Delete(temporary);
                    await _store.MarkItemDownloadedAsync(
                        itemId, Path.GetFileName(existing), existing, contentHash, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                    return new DownloadOutcome(false);
                }

                var target = await ChooseTargetAsync(subscription.DestinationPath, fileName, contentHash, cancellationToken).ConfigureAwait(false);
                if (File.Exists(target))
                {
                    File.Delete(temporary);
                    await _store.MarkItemDownloadedAsync(
                        itemId, Path.GetFileName(target), target, contentHash, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                    return new DownloadOutcome(false);
                }

                File.Move(temporary, target);
                await _store.MarkItemDownloadedAsync(
                    itemId, Path.GetFileName(target), target, contentHash, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                return new DownloadOutcome(true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
    }

    private static async Task<string> DownloadToTemporaryAsync(
        HttpResponseMessage response,
        string temporary,
        CancellationToken cancellationToken)
    {
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(temporary, new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.Create,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 128 * 1024
        });
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var buffer = new byte[128 * 1024];
        long total = 0;
        try
        {
            while (true)
            {
                inactivity.CancelAfter(DownloadInactivityTimeout);
                var read = await source.ReadAsync(buffer, inactivity.Token).ConfigureAwait(false);
                if (read == 0) break;
                inactivity.CancelAfter(Timeout.InfiniteTimeSpan);
                total += read;
                if (total > MaximumAudioBytes)
                    throw new InvalidDataException("The broadcast exceeded Radio Vault’s 8 GB RSS download limit.");
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The broadcast download stopped receiving data for 90 seconds.");
        }
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (total == 0) throw new InvalidDataException("The broadcast download was empty.");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<string> ChooseTargetAsync(
        string destination,
        string fileName,
        string contentHash,
        CancellationToken cancellationToken)
    {
        var candidate = Path.Combine(destination, fileName);
        if (!File.Exists(candidate)) return candidate;
        if (string.Equals(await HashFileAsync(candidate, cancellationToken).ConfigureAwait(false), contentHash, StringComparison.OrdinalIgnoreCase))
            return candidate;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 2; suffix <= 999; suffix++)
        {
            candidate = Path.Combine(destination, $"{stem} ({suffix}){extension}");
            if (!File.Exists(candidate)) return candidate;
            if (string.Equals(await HashFileAsync(candidate, cancellationToken).ConfigureAwait(false), contentHash, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        throw new IOException("Radio Vault could not choose a unique filename for the RSS broadcast.");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationTokenSource timeout,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("The RSS document is larger than Radio Vault’s 8 MB limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            timeout.CancelAfter(FeedTimeout);
            var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (read == 0) break;
            if (memory.Length + read > maximumBytes)
                throw new InvalidDataException("The RSS document is larger than Radio Vault’s 8 MB limit.");
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return memory.ToArray();
    }

    private static void AddAuthentication(HttpRequestMessage request, RssFeedSource source, Uri requestUri)
    {
        if (string.IsNullOrWhiteSpace(source.Username)) return;
        var feedUri = new Uri(source.FeedUrl);
        if (!string.Equals(feedUri.Scheme, requestUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(feedUri.IdnHost, requestUri.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            feedUri.Port != requestUri.Port) return;
        var value = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{source.Username}:{source.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", value);
    }

    private static string BuildFileName(string title, DateTimeOffset? publishedAt, string extension)
    {
        var prefix = publishedAt.HasValue ? publishedAt.Value.ToLocalTime().ToString("yyyy-MM-dd") + " - " : string.Empty;
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var cleaned = new string((prefix + title).Select(character => invalid.Contains(character) || char.IsControl(character) ? ' ' : character).ToArray());
        cleaned = string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "RSS broadcast";
        var maximumStem = Math.Max(40, 180 - extension.Length);
        if (cleaned.Length > maximumStem) cleaned = cleaned[..maximumStem].TrimEnd();
        return cleaned + extension;
    }

    private static string ExtensionFor(Uri uri, string? mediaType)
    {
        var extension = Path.GetExtension(Uri.UnescapeDataString(uri.AbsolutePath));
        if (AudioExtensions.Contains(extension)) return extension.ToLowerInvariant();
        return mediaType?.ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "audio/aac" => ".aac",
            "audio/flac" or "audio/x-flac" => ".flac",
            "audio/ogg" => ".ogg",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/x-ms-wma" => ".wma",
            _ => ".mp3"
        };
    }

    private async Task PollSafelyAsync()
    {
        try { await RunIfDueAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (Exception exception) { DiagnosticLog.Write("RSS ingestion", "The scheduled RSS check failed safely.", exception); }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RadioVault-Server/1.0");
        return client;
    }

    private static string SafeFeedError(Exception exception)
        => exception switch
        {
            HttpRequestException value when value.StatusCode.HasValue => $"RSS check failed with HTTP {(int)value.StatusCode.Value}.",
            HttpRequestException => "The RSS server could not be reached.",
            CryptographicException => "The saved RSS credentials could not be opened on this server.",
            InvalidDataException value => value.Message,
            TimeoutException value => value.Message,
            OperationCanceledException => "The RSS server did not respond within 30 seconds.",
            _ => "The RSS feed could not be checked."
        };

    private static string SafeDownloadError(Exception exception)
        => exception switch
        {
            HttpRequestException value when value.StatusCode.HasValue => $"Broadcast download failed with HTTP {(int)value.StatusCode.Value}.",
            HttpRequestException => "The broadcast server could not be reached.",
            DirectoryNotFoundException value => value.Message,
            InvalidDataException value => value.Message,
            TimeoutException value => value.Message,
            IOException => "The broadcast could not be written to the selected Library folder.",
            _ => "The broadcast could not be downloaded."
        };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        Stop();
        if (_ownsHttpClient) _http.Dispose();
        _lifetime.Dispose();
    }

    private sealed record FeedOutcome(int Downloaded, int Known, int Failures);
    private sealed record DownloadOutcome(bool CreatedNewFile);
}
