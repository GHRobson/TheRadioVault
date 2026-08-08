using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Collections.Concurrent;

namespace TheRadioVault.Services;

/// <summary>
/// Authenticated, certificate-pinned connection shared by native client
/// services that read or mutate the dedicated server over loopback.
/// </summary>
public sealed class LoopbackServerClient : IDisposable
{
    private const int MaximumTransientAttempts = 3;
    private static readonly TimeSpan RemoteRetryAttemptTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client;
    private readonly NativeServerResponseCache? _responseCache;
    private readonly ConcurrentDictionary<string, MemoryResponseEntry> _memoryResponses = new(StringComparer.Ordinal);
    private int _isCachedReadOnly;
    private int _preferPersistentCache;
    private bool _disposed;

    public LoopbackServerClient(
        WebServerPreferences? preferences = null,
        NativeServerConnectionPreferences? remotePreferences = null,
        bool useRemoteServer = false,
        string? responseCacheRoot = null)
    {
        try
        {
            if (useRemoteServer)
            {
                var remote = remotePreferences ?? NativeServerConnectionPreferences.Load();
                if (!remote.HasSavedServer)
                    throw new InvalidOperationException("No paired Radio Vault Server is saved on this client.");
                _client = CreateRemoteClient(remote);
                IsAvailable = true;
                IsRemote = true;
                ServerDisplayName = remote.ServerDisplayName;
                _responseCache = new NativeServerResponseCache(remote, responseCacheRoot);
            }
            else
            {
                var settings = preferences ?? WebServerPreferences.Load();
                _client = CreateLocalClient(settings);
                IsAvailable = settings.Enabled;
                ServerDisplayName = string.IsNullOrWhiteSpace(settings.ServerDisplayName)
                    ? "Radio Vault Server on this computer"
                    : settings.ServerDisplayName.Trim();
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("Native server connection", "The secure loopback client could not be initialised.", exception);
            _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            IsAvailable = false;
        }
    }

    public bool IsAvailable { get; }
    public bool IsRemote { get; }
    public string ServerDisplayName { get; } = "Radio Vault Server";
    public Uri? ServerAddress => _client.BaseAddress;
    public bool IsCachedReadOnly => Volatile.Read(ref _isCachedReadOnly) != 0;
    public long CacheSizeBytes => _responseCache?.SizeBytes ?? 0;

    public void InvalidateMemoryCache() => _memoryResponses.Clear();

    /// <summary>
    /// Records an independently proven live server connection. A recovery clears
    /// short-lived responses once so work performed by another client during the
    /// outage cannot linger, while routine healthy probes leave the warm cache
    /// intact instead of making every tab reload from the network.
    /// </summary>
    public void MarkServerLive(bool invalidateMemoryCache)
    {
        Volatile.Write(ref _isCachedReadOnly, 0);
        if (invalidateMemoryCache) _memoryResponses.Clear();
    }

    /// <summary>
    /// Temporarily favours the encrypted on-device response cache for GETs.
    /// Startup uses this to hydrate already-known views before performing one
    /// live revision check in the background.
    /// </summary>
    public IDisposable UsePersistentCacheFirst()
    {
        Interlocked.Increment(ref _preferPersistentCache);
        return new PersistentCachePreference(this);
    }

    public async Task<T> SendJsonAsync<T>(
        HttpMethod method,
        string path,
        object? body = null,
        bool allowConflict = false,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        if (TryReadMemory(method, path, out var remembered))
            return Deserialize<T>(remembered, HttpStatusCode.OK);
        if (method == HttpMethod.Get && PreferPersistentCache && TryReadPersistent(path, out T? cached))
            return cached!;
        if (method != HttpMethod.Get && method != HttpMethod.Head)
            _memoryResponses.Clear();
        HttpResponseMessage response;
        try
        {
            response = await SendWithReconnectAsync(() =>
            {
                var request = new HttpRequestMessage(method, path);
                if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
                return request;
            }, IsRetrySafe(method, path), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (CanUseCache(method, cancellationToken, exception))
        {
            return ReadCached<T>(path, exception);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode && !(allowConflict && response.StatusCode == HttpStatusCode.Conflict))
            {
                if (method == HttpMethod.Get && IsTransient(response.StatusCode))
                    return ReadCached<T>(path, new HttpRequestException($"Server returned {(int)response.StatusCode}."));
                throw await CreateServerErrorAsync(response, "request", cancellationToken).ConfigureAwait(false);
            }
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (method == HttpMethod.Get) MarkLiveAndCache(path, bytes);
            return Deserialize<T>(bytes, response.StatusCode);
        }
    }

    public async Task<T?> GetJsonOrNullAsync<T>(string path, CancellationToken cancellationToken = default)
        where T : class
    {
        EnsureAvailable();
        if (TryReadMemory(HttpMethod.Get, path, out var remembered))
            return Deserialize<T>(remembered, HttpStatusCode.OK);
        if (PreferPersistentCache && TryReadPersistent(path, out T? cached))
            return cached;
        HttpResponseMessage response;
        try
        {
            response = await SendWithReconnectAsync(
                () => new HttpRequestMessage(HttpMethod.Get, path), true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (CanUseCache(HttpMethod.Get, cancellationToken, exception))
        {
            return ReadCached<T>(path, exception);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
            {
                if (IsTransient(response.StatusCode))
                    return ReadCached<T>(path, new HttpRequestException($"Server returned {(int)response.StatusCode}."));
                throw new InvalidOperationException($"Radio Vault Server rejected the request ({(int)response.StatusCode}).");
            }
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            MarkLiveAndCache(path, bytes);
            return Deserialize<T>(bytes, response.StatusCode);
        }
    }

    public async Task<T> PostBytesForJsonAsync<T>(
        string path,
        byte[] bytes,
        string contentType,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        _memoryResponses.Clear();
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Content = new ByteArrayContent(bytes ?? Array.Empty<byte>());
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        if (headers is not null)
            foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateServerErrorAsync(response, "upload", cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Radio Vault Server returned an empty upload response.");
    }

    public async Task<LoopbackBinaryResponse> PostJsonForBytesAsync(
        string path,
        object body,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        _memoryResponses.Clear();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, body.GetType(), options: JsonOptions)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateServerErrorAsync(response, "download", cancellationToken).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName
            ?? "RadioVault-Archive.trvknowledge";
        fileName = fileName.Trim('"');
        var broadcastCount = ReadIntHeader(response, "X-Radio-Vault-Broadcast-Count");
        var missingCount = ReadIntHeader(response, "X-Radio-Vault-Missing-Count");
        var transcriptCount = ReadIntHeader(response, "X-Radio-Vault-Transcript-Count");
        var wikiPageCount = ReadIntHeader(response, "X-Radio-Vault-Wiki-Page-Count");
        return new LoopbackBinaryResponse(bytes, fileName, broadcastCount, missingCount, transcriptCount, wikiPageCount);
    }

    public async Task<LoopbackFileResponse> PostJsonForFileAsync(
        string path,
        object body,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        _memoryResponses.Clear();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, body.GetType(), options: JsonOptions)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateServerErrorAsync(response, "download", cancellationToken).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName
            ?? "RadioVault-Export.bin";
        return new LoopbackFileResponse(
            bytes,
            fileName.Trim('"'),
            ReadIntHeader(response, "X-Radio-Vault-Wiki-Page-Count"),
            ReadIntHeader(response, "X-Radio-Vault-Wiki-Image-Count"));
    }

    /// <summary>
    /// Performs a live GET without memory or disk fallback. Revision checks use
    /// this path so a cached synchronization response can never be mistaken for
    /// confirmation from the current server process.
    /// </summary>
    public async Task<T> GetLiveJsonAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        using var response = await SendWithReconnectAsync(
            () => new HttpRequestMessage(HttpMethod.Get, path), retrySafe: true, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Radio Vault Server rejected the live refresh ({(int)response.StatusCode}).");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _isCachedReadOnly, 0);
        return Deserialize<T>(bytes, response.StatusCode);
    }

    /// <summary>
    /// Opens an authenticated server response without buffering its body. The
    /// caller owns the returned response and must dispose it after streaming.
    /// </summary>
    public async Task<HttpResponseMessage> OpenResponseAsync(
        HttpMethod method,
        string path,
        string? range = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var response = await SendWithReconnectAsync(() =>
        {
            var request = new HttpRequestMessage(method, path);
            if (!string.IsNullOrWhiteSpace(range))
                request.Headers.TryAddWithoutValidation("Range", range);
            return request;
        }, retrySafe: true, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private static int ReadIntHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), out var value)
            ? value
            : 0;

    private bool CanUseCache(HttpMethod method, CancellationToken cancellationToken, Exception exception)
        => method == HttpMethod.Get && _responseCache is not null && !cancellationToken.IsCancellationRequested &&
           exception is HttpRequestException or TaskCanceledException;

    private T ReadCached<T>(string path, Exception liveError)
    {
        if (_responseCache is null || !_responseCache.TryLoad(path, out var bytes))
            throw new HttpRequestException("Radio Vault Server is unavailable and this view has not been cached yet.", liveError);
        Volatile.Write(ref _isCachedReadOnly, 1);
        DiagnosticLog.Write("Native server cache", $"Serving cached read-only response for {path} while the server reconnects.", liveError);
        return Deserialize<T>(bytes, HttpStatusCode.OK);
    }

    private bool PreferPersistentCache => Volatile.Read(ref _preferPersistentCache) > 0;

    private bool TryReadPersistent<T>(string path, out T? value)
    {
        value = default;
        if (_responseCache is null || !_responseCache.TryLoad(path, out var bytes)) return false;
        try
        {
            value = Deserialize<T>(bytes, HttpStatusCode.OK);
            Volatile.Write(ref _isCachedReadOnly, 1);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void MarkLiveAndCache(string path, byte[] bytes)
    {
        Volatile.Write(ref _isCachedReadOnly, 0);
        if (IsMemoryCacheablePath(path))
            _memoryResponses[path] = new MemoryResponseEntry(bytes, DateTimeOffset.UtcNow.AddSeconds(20));
        _responseCache?.Store(path, bytes);
    }

    private bool TryReadMemory(HttpMethod method, string path, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (method != HttpMethod.Get || !IsMemoryCacheablePath(path)) return false;
        if (!_memoryResponses.TryGetValue(path, out var entry)) return false;
        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _memoryResponses.TryRemove(path, out _);
            return false;
        }
        bytes = entry.Bytes;
        return true;
    }

    private static bool IsMemoryCacheablePath(string path)
        => path.StartsWith(TheRadioVault.Web.Contracts.WebApiRoutes.Root + "/client/", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith(TheRadioVault.Web.Contracts.WebApiRoutes.MomentsAll, StringComparison.OrdinalIgnoreCase) ||
           path.Equals(TheRadioVault.Web.Contracts.WebApiRoutes.Queue, StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith(TheRadioVault.Web.Contracts.WebApiRoutes.FederationResearchWorkspace, StringComparison.OrdinalIgnoreCase) ||
           path.Equals(TheRadioVault.Web.Contracts.WebApiRoutes.FederationSettings, StringComparison.OrdinalIgnoreCase);

    private static T Deserialize<T>(byte[] bytes, HttpStatusCode statusCode)
        => JsonSerializer.Deserialize<T>(bytes, JsonOptions)
           ?? throw new InvalidOperationException($"Radio Vault Server returned an empty response ({(int)statusCode}).");

    private async Task<HttpResponseMessage> SendWithReconnectAsync(
        Func<HttpRequestMessage> requestFactory,
        bool retrySafe,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= (retrySafe ? MaximumTransientAttempts : 1); attempt++)
        {
            using var request = requestFactory();
            using var attemptTimeout = IsRemote && retrySafe
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            attemptTimeout?.CancelAfter(RemoteRetryAttemptTimeout);
            var attemptToken = attemptTimeout?.Token ?? cancellationToken;
            try
            {
                var response = await _client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, attemptToken).ConfigureAwait(false);
                if (!retrySafe || attempt == MaximumTransientAttempts || !IsTransient(response.StatusCode))
                    return response;
                response.Dispose();
            }
            catch (Exception exception) when (
                retrySafe &&
                attempt < MaximumTransientAttempts &&
                !cancellationToken.IsCancellationRequested &&
                exception is HttpRequestException or TaskCanceledException)
            {
                lastError = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(attempt == 1 ? 200 : 650), cancellationToken)
                .ConfigureAwait(false);
        }

        throw lastError ?? new HttpRequestException("Radio Vault Server remained unavailable after reconnect attempts.");
    }

    private static bool IsRetrySafe(HttpMethod method, string path)
        => method == HttpMethod.Get || method == HttpMethod.Head ||
           path.StartsWith("/api/v1/player/transfer/", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("/api/v1/player/web-progress", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransient(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static HttpClient CreateLocalClient(WebServerPreferences preferences)
    {
        HttpMessageHandler handler;
        string scheme;
        int port;
        if (preferences.SecureAccessEnabled)
        {
            using var certificates = SecureWebCertificateService.EnsureCertificates(preferences.CertificatePassword);
            var expectedThumbprint = certificates.ServerThumbprint;
            handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && string.Equals(
                        certificate.GetCertHashString(),
                        expectedThumbprint,
                        StringComparison.OrdinalIgnoreCase)
            };
            scheme = "https";
            port = preferences.SecurePort;
        }
        else
        {
            handler = new HttpClientHandler();
            scheme = "http";
            port = preferences.Port;
        }

        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri($"{scheme}://127.0.0.1:{port}"),
            // Complete Knowledge databases can contain thousands of pages,
            // transcripts and image BLOBs. Local imports need the same bounded
            // long-operation window as imports from a paired computer.
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.Add("X-RadioVault-Token", preferences.AccessToken);
        return client;
    }

    private static HttpClient CreateRemoteClient(NativeServerConnectionPreferences preferences)
    {
        var expectedThumbprint = NativeServerConnectionPreferences.NormalizeThumbprint(preferences.CertificateThumbprint);
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null && string.Equals(
                    NativeServerConnectionPreferences.NormalizeThumbprint(certificate.GetCertHashString()),
                    expectedThumbprint,
                    StringComparison.Ordinal)
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri($"https://{preferences.ServerAddress}:{preferences.SecurePort}"),
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.Add("X-RadioVault-Token", preferences.AccessToken);
        return client;
    }

    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsAvailable)
            throw new InvalidOperationException("The dedicated Radio Vault Server connection is not enabled.");
    }

    private static async Task<InvalidOperationException> CreateServerErrorAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var fallback = $"Radio Vault Server rejected the {operation} ({(int)response.StatusCode}).";
        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("error", out var error))
                return new InvalidOperationException(fallback);
            var message = error.TryGetProperty("message", out var messageValue)
                ? messageValue.GetString()?.Trim()
                : null;
            var diagnosticId = error.TryGetProperty("diagnosticId", out var diagnosticValue)
                ? diagnosticValue.GetString()?.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(message)) return new InvalidOperationException(fallback);
            return new InvalidOperationException(string.IsNullOrWhiteSpace(diagnosticId)
                ? message
                : $"{message} Reference: {diagnosticId}.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new InvalidOperationException(fallback);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }

    private sealed record MemoryResponseEntry(byte[] Bytes, DateTimeOffset ExpiresAt);

    private sealed class PersistentCachePreference : IDisposable
    {
        private LoopbackServerClient? _owner;

        public PersistentCachePreference(LoopbackServerClient owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null) Interlocked.Decrement(ref owner._preferPersistentCache);
        }
    }
}

public sealed record LoopbackBinaryResponse(byte[] Bytes, string FileName, int BroadcastCount, int MissingCount, int TranscriptCount, int WikiPageCount = 0);
public sealed record LoopbackFileResponse(byte[] Bytes, string FileName, int PageCount = 0, int ImageCount = 0);
