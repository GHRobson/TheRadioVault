using System.Buffers;
using System.Globalization;
using System.Text;

namespace TheRadioVault.Web.Services;

internal sealed class HttpRequest : IDisposable
{
    private readonly string? _stagedBodyPath;
    private bool _disposed;

    public HttpRequest(
        string method,
        string target,
        IReadOnlyDictionary<string, string> headers,
        byte[] body,
        string? stagedBodyPath = null)
    {
        Method = method;
        Target = target;
        Headers = headers;
        Body = body;
        _stagedBodyPath = stagedBodyPath;
    }

    public string Method { get; }
    public string Target { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public byte[] Body { get; }
    public bool IsBodyStaged => _stagedBodyPath is not null;
    internal string? StagedBodyPath => _stagedBodyPath;
    public long BodyLength => _stagedBodyPath is null ? Body.LongLength : new FileInfo(_stagedBodyPath).Length;

    public Stream OpenBodyStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _stagedBodyPath is null
            ? new MemoryStream(Body, writable: false)
            : new FileStream(_stagedBodyPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_stagedBodyPath is null) return;
        try { File.Delete(_stagedBodyPath); } catch { }
    }
}

internal enum WebHttpRequestReadFailure
{
    None,
    EndOfStream,
    TimedOut,
    HeaderTooLarge,
    BodyTooLarge,
    Malformed
}

internal readonly record struct WebHttpRequestBodyPolicy(
    int MaximumBytes,
    TimeSpan Timeout,
    bool StageToFile = false);

internal readonly record struct WebHttpRequestReadResult(HttpRequest? Request, WebHttpRequestReadFailure Failure)
{
    public static WebHttpRequestReadResult Success(HttpRequest request) => new(request, WebHttpRequestReadFailure.None);
    public static WebHttpRequestReadResult Failed(WebHttpRequestReadFailure failure) => new(null, failure);
}

internal sealed class WebHttpRequestReader
{
    internal const int DefaultMaximumHeaderBytes = 32 * 1024;
    internal static readonly TimeSpan DefaultHeaderTimeout = TimeSpan.FromSeconds(10);

    private const int ReadBufferBytes = 2048;
    private const int MaximumTrailerBytes = 8 * 1024;
    private readonly Func<string, WebHttpRequestBodyPolicy> _bodyPolicy;
    private readonly int _maximumHeaderBytes;
    private readonly TimeSpan _headerTimeout;

    public WebHttpRequestReader(
        Func<string, WebHttpRequestBodyPolicy> bodyPolicy,
        int maximumHeaderBytes = DefaultMaximumHeaderBytes,
        TimeSpan? headerTimeout = null)
    {
        _bodyPolicy = bodyPolicy ?? throw new ArgumentNullException(nameof(bodyPolicy));
        _maximumHeaderBytes = maximumHeaderBytes > 0
            ? maximumHeaderBytes
            : throw new ArgumentOutOfRangeException(nameof(maximumHeaderBytes));
        _headerTimeout = headerTimeout ?? DefaultHeaderTimeout;
        if (_headerTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(headerTimeout));
    }

    public async Task<WebHttpRequestReadResult> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(_headerTimeout);
        var rented = ArrayPool<byte>.Shared.Rent(_maximumHeaderBytes + ReadBufferBytes);
        string? stagedBodyPath = null;
        try
        {
            var bytesRead = 0;
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                if (bytesRead >= _maximumHeaderBytes)
                    return WebHttpRequestReadResult.Failed(WebHttpRequestReadFailure.HeaderTooLarge);

                var readSize = Math.Min(ReadBufferBytes, rented.Length - bytesRead);
                var read = await stream.ReadAsync(rented.AsMemory(bytesRead, readSize), headerTimeout.Token).ConfigureAwait(false);
                if (read <= 0)
                    return WebHttpRequestReadResult.Failed(WebHttpRequestReadFailure.EndOfStream);

                var scanFrom = Math.Max(0, bytesRead - 3);
                bytesRead += read;
                headerEnd = FindHeaderEnd(rented, bytesRead, scanFrom);
                if (headerEnd >= 0 && headerEnd + 4 > _maximumHeaderBytes)
                    return WebHttpRequestReadResult.Failed(WebHttpRequestReadFailure.HeaderTooLarge);
            }

            var parsed = ParseHeaders(rented.AsSpan(0, headerEnd));
            if (parsed is null)
                return WebHttpRequestReadResult.Failed(WebHttpRequestReadFailure.Malformed);

            var (method, target, headers) = parsed.Value;
            var policy = _bodyPolicy(target);
            if (policy.MaximumBytes < 0 || policy.Timeout <= TimeSpan.Zero)
                throw new InvalidOperationException("The HTTP request body policy is invalid.");

            var bodyOffset = headerEnd + 4;
            var initialBodyLength = Math.Max(0, bytesRead - bodyOffset);
            var initialBody = rented.AsMemory(bodyOffset, initialBodyLength);

            var hasTransferEncoding = headers.TryGetValue("transfer-encoding", out var transferEncoding);
            var transferEncodings = hasTransferEncoding
                ? transferEncoding!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
            var isChunked = transferEncodings.Length == 1 &&
                            transferEncodings[0].Equals("chunked", StringComparison.OrdinalIgnoreCase);
            var hasContentLength = headers.TryGetValue("content-length", out var rawLength);
            if (isChunked && hasContentLength)
                return WebHttpRequestReadResult.Failed(WebHttpRequestReadFailure.Malformed);

            byte[] body;
            if (isChunked)
            {
                var chunked = await ReadChunkedBodyAsync(
                        stream,
                        initialBody,
                        policy.MaximumBytes,
                        policy.Timeout,
                        policy.StageToFile,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (chunked.Failure != WebHttpRequestReadFailure.None)
                    return WebHttpRequestReadResult.Failed(chunked.Failure);
                body = chunked.Body ?? [];
                stagedBodyPath = chunked.StagedBodyPath;
            }
            else
            {
                if (hasTransferEncoding)
                    return WebHttpRequestReadResult.Failed(WebHttpRequestReadFailure.Malformed);
                var contentLength = 0;
                if (hasContentLength && (!int.TryParse(rawLength, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) || contentLength < 0))
                    return WebHttpRequestReadResult.Failed(WebHttpRequestReadFailure.Malformed);

                if (contentLength > policy.MaximumBytes)
                    return WebHttpRequestReadResult.Failed(WebHttpRequestReadFailure.BodyTooLarge);

                if (policy.StageToFile && contentLength > 0)
                {
                    stagedBodyPath = CreateStagedBodyPath();
                    await using var output = CreateStagedBodyStream(stagedBodyPath);
                    var copied = Math.Min(initialBody.Length, contentLength);
                    await output.WriteAsync(initialBody[..copied], cancellationToken).ConfigureAwait(false);
                    var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                    try
                    {
                        while (copied < contentLength)
                        {
                            var requested = Math.Min(buffer.Length, contentLength - copied);
                            var read = await ReadWithInactivityTimeoutAsync(
                                stream, buffer.AsMemory(0, requested), policy.Timeout, cancellationToken).ConfigureAwait(false);
                            if (read <= 0)
                                return WebHttpRequestReadResult.Failed(WebHttpRequestReadFailure.EndOfStream);
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            copied += read;
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    body = [];
                }
                else
                {
                    body = GC.AllocateUninitializedArray<byte>(contentLength);
                    var copied = Math.Min(initialBody.Length, body.Length);
                    initialBody.Span[..copied].CopyTo(body);
                    while (copied < body.Length)
                    {
                        var read = await ReadWithInactivityTimeoutAsync(
                            stream, body.AsMemory(copied), policy.Timeout, cancellationToken).ConfigureAwait(false);
                        if (read <= 0)
                            return WebHttpRequestReadResult.Failed(WebHttpRequestReadFailure.EndOfStream);
                        copied += read;
                    }
                }
            }

            var request = new HttpRequest(method, target, headers, body, stagedBodyPath);
            stagedBodyPath = null;
            return WebHttpRequestReadResult.Success(request);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WebHttpRequestReadResult.Failed(WebHttpRequestReadFailure.TimedOut);
        }
        finally
        {
            if (stagedBodyPath is not null)
            {
                try { File.Delete(stagedBodyPath); } catch { }
            }
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static (string Method, string Target, IReadOnlyDictionary<string, string> Headers)? ParseHeaders(
        ReadOnlySpan<byte> headerBytes)
    {
        var headerText = Encoding.ASCII.GetString(headerBytes);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0) return null;

        var first = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (first.Length != 3 || first[0].Length == 0 || first[1].Length == 0 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) return null;
            var name = line[..colon].Trim();
            if (name.Length == 0) return null;
            var value = line[(colon + 1)..].Trim();
            if ((name.Equals("content-length", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase)) &&
                headers.ContainsKey(name))
            {
                return null;
            }
            headers[name] = value;
        }

        return (first[0], first[1], headers);
    }

    private static int FindHeaderEnd(byte[] bytes, int length, int start)
    {
        for (var index = start; index <= length - 4; index++)
        {
            if (bytes[index] == '\r' && bytes[index + 1] == '\n' &&
                bytes[index + 2] == '\r' && bytes[index + 3] == '\n')
            {
                return index;
            }
        }
        return -1;
    }

    private static async Task<(byte[]? Body, string? StagedBodyPath, WebHttpRequestReadFailure Failure)> ReadChunkedBodyAsync(
        Stream stream,
        ReadOnlyMemory<byte> initialBody,
        int maximumBodyBytes,
        TimeSpan inactivityTimeout,
        bool stageToFile,
        CancellationToken cancellationToken)
    {
        var reader = new PrefixedBodyReader(stream, initialBody, inactivityTimeout);
        var stagedBodyPath = stageToFile ? CreateStagedBodyPath() : null;
        await using Stream output = stagedBodyPath is null
            ? new MemoryStream()
            : CreateStagedBodyStream(stagedBodyPath);
        var completed = false;

        try
        {
            while (true)
            {
                var sizeLine = await reader.ReadAsciiLineAsync(128, cancellationToken).ConfigureAwait(false);
                if (sizeLine is null) return (null, null, WebHttpRequestReadFailure.Malformed);
                var extension = sizeLine.IndexOf(';');
                var sizeText = (extension >= 0 ? sizeLine[..extension] : sizeLine).Trim();
                if (!int.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var chunkSize) || chunkSize < 0)
                    return (null, null, WebHttpRequestReadFailure.Malformed);

                if (chunkSize == 0)
                {
                    var trailerBytes = 0;
                    while (true)
                    {
                        var trailer = await reader.ReadAsciiLineAsync(2048, cancellationToken).ConfigureAwait(false);
                        if (trailer is null) return (null, null, WebHttpRequestReadFailure.Malformed);
                        if (trailer.Length == 0)
                        {
                            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                            if (output is MemoryStream memory)
                            {
                                completed = true;
                                return (memory.ToArray(), null, WebHttpRequestReadFailure.None);
                            }
                            completed = true;
                            return (null, stagedBodyPath, WebHttpRequestReadFailure.None);
                        }
                        trailerBytes += trailer.Length + 2;
                        if (trailerBytes > MaximumTrailerBytes)
                            return (null, null, WebHttpRequestReadFailure.Malformed);
                    }
                }

                if (output.Length + chunkSize > maximumBodyBytes)
                    return (null, null, WebHttpRequestReadFailure.BodyTooLarge);

                var remaining = chunkSize;
                var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(128 * 1024, Math.Max(1, chunkSize)));
                try
                {
                    while (remaining > 0)
                    {
                        var requested = Math.Min(buffer.Length, remaining);
                        if (!await reader.ReadExactlyAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false))
                            return (null, null, WebHttpRequestReadFailure.Malformed);
                        await output.WriteAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                        remaining -= requested;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }

                var carriageReturn = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
                var lineFeed = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (carriageReturn != '\r' || lineFeed != '\n')
                    return (null, null, WebHttpRequestReadFailure.Malformed);
            }
        }
        finally
        {
            if (stagedBodyPath is not null && !completed)
            {
                try { File.Delete(stagedBodyPath); } catch { }
            }
        }
    }

    private static string CreateStagedBodyPath()
        => Path.Combine(Path.GetTempPath(), $"radiovault-http-{Environment.ProcessId}-{Guid.NewGuid():N}.upload");

    private static FileStream CreateStagedBodyStream(string path)
        => new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async ValueTask<int> ReadWithInactivityTimeoutAsync(
        Stream stream,
        Memory<byte> destination,
        TimeSpan inactivityTimeout,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(inactivityTimeout);
        return await stream.ReadAsync(destination, timeout.Token).ConfigureAwait(false);
    }

    private sealed class PrefixedBodyReader
    {
        private readonly Stream _stream;
        private readonly ReadOnlyMemory<byte> _prefix;
        private int _prefixOffset;
        private readonly byte[] _networkBuffer = new byte[ReadBufferBytes];
        private readonly byte[] _lineBuffer = new byte[ReadBufferBytes];
        private int _networkOffset;
        private int _networkCount;

        private readonly TimeSpan _inactivityTimeout;

        public PrefixedBodyReader(Stream stream, ReadOnlyMemory<byte> prefix, TimeSpan inactivityTimeout)
        {
            _stream = stream;
            _prefix = prefix;
            _inactivityTimeout = inactivityTimeout;
        }

        public async Task<int> ReadByteAsync(CancellationToken cancellationToken)
        {
            if (_prefixOffset < _prefix.Length) return _prefix.Span[_prefixOffset++];
            if (_networkOffset >= _networkCount)
            {
                _networkCount = await ReadWithInactivityTimeoutAsync(
                    _stream, _networkBuffer, _inactivityTimeout, cancellationToken).ConfigureAwait(false);
                _networkOffset = 0;
                if (_networkCount <= 0) return -1;
            }
            return _networkBuffer[_networkOffset++];
        }

        public async Task<bool> ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            var written = 0;
            if (_prefixOffset < _prefix.Length)
            {
                var copied = Math.Min(destination.Length, _prefix.Length - _prefixOffset);
                _prefix.Span.Slice(_prefixOffset, copied).CopyTo(destination.Span);
                _prefixOffset += copied;
                written += copied;
            }

            if (written < destination.Length && _networkOffset < _networkCount)
            {
                var copied = Math.Min(destination.Length - written, _networkCount - _networkOffset);
                _networkBuffer.AsSpan(_networkOffset, copied).CopyTo(destination.Span[written..]);
                _networkOffset += copied;
                written += copied;
            }

            while (written < destination.Length)
            {
                var read = await ReadWithInactivityTimeoutAsync(
                    _stream, destination[written..], _inactivityTimeout, cancellationToken).ConfigureAwait(false);
                if (read <= 0) return false;
                written += read;
            }
            return true;
        }

        public async Task<string?> ReadAsciiLineAsync(int maximumBytes, CancellationToken cancellationToken)
        {
            if (maximumBytes <= 0 || maximumBytes > _lineBuffer.Length)
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            var length = 0;
            while (length < maximumBytes)
            {
                var value = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (value < 0) return null;
                if (value == '\r')
                {
                    var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                    return next == '\n' ? Encoding.ASCII.GetString(_lineBuffer, 0, length) : null;
                }
                _lineBuffer[length++] = (byte)value;
            }
            return null;
        }
    }
}
