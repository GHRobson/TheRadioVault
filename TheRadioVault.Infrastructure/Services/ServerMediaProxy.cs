using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TheRadioVault.Services;

/// <summary>
/// Presents authenticated server media as private loopback HTTP URLs. Windows
/// Media Foundation can then seek and decode every supported archive format,
/// while the server token and pinned TLS connection remain inside Radio Vault.
/// </summary>
public sealed class ServerMediaProxy : IDisposable
{
    private const int MaximumHeaderBytes = 64 * 1024;
    private readonly LoopbackServerClient _server;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, string> _routes = new(StringComparer.Ordinal);
    private readonly Task _acceptLoop;
    private bool _disposed;

    public ServerMediaProxy(LoopbackServerClient server)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_lifetime.Token);
    }

    public int Port { get; }

    public string Register(string serverPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(serverPath) || !serverPath.StartsWith('/'))
            throw new ArgumentException("A server-relative media path is required.", nameof(serverPath));
        var key = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        _routes[key] = serverPath;
        return $"http://127.0.0.1:{Port}/media/{key}";
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var socket = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleAsync(socket, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                DiagnosticLog.Write("Native media bridge", "The local media bridge could not accept a player request.", exception);
            }
        }
    }

    private async Task HandleAsync(TcpClient socket, CancellationToken cancellationToken)
    {
        using (socket)
        {
            socket.NoDelay = true;
            try
            {
                await using var stream = socket.GetStream();
                var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    await WriteErrorAsync(stream, 400, "Bad Request", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var key = request.Path.StartsWith("/media/", StringComparison.Ordinal)
                    ? request.Path[7..].Split('?', 2)[0]
                    : string.Empty;
                if (!_routes.TryGetValue(key, out var serverPath))
                {
                    await WriteErrorAsync(stream, 404, "Not Found", cancellationToken).ConfigureAwait(false);
                    return;
                }

                // Some Media Foundation probes use HEAD. Fetch the server headers
                // with GET because old servers may only route GET for media parts.
                using var response = await _server.OpenResponseAsync(
                    HttpMethod.Get, serverPath, request.Range, cancellationToken).ConfigureAwait(false);
                await WriteResponseAsync(stream, response, request.HeadOnly, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (IOException) { }
            catch (Exception exception)
            {
                DiagnosticLog.Write("Native media bridge", "A server audio request failed.", exception);
                try { await WriteErrorAsync(socket.GetStream(), 502, "Bad Gateway", CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }
        }
    }

    private static async Task<ProxyRequest?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(1024);
        var buffer = new byte[1024];
        while (bytes.Count < MaximumHeaderBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0) return null;
            bytes.AddRange(buffer.AsSpan(0, read).ToArray());
            var count = bytes.Count;
            if (count >= 4 && bytes[count - 4] == '\r' && bytes[count - 3] == '\n' && bytes[count - 2] == '\r' && bytes[count - 1] == '\n')
                break;
        }
        if (bytes.Count >= MaximumHeaderBytes) return null;
        var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n", StringSplitOptions.None);
        var first = lines.FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (first is null || first.Length < 2 || (first[0] != "GET" && first[0] != "HEAD")) return null;
        var range = lines.Skip(1)
            .Select(line => line.Split(':', 2))
            .FirstOrDefault(parts => parts.Length == 2 && parts[0].Equals("Range", StringComparison.OrdinalIgnoreCase));
        return new ProxyRequest(first[1], first[0] == "HEAD", range is null ? null : range[1].Trim());
    }

    private static async Task WriteResponseAsync(
        Stream target,
        HttpResponseMessage response,
        bool headOnly,
        CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase) ? "Response" : response.ReasonPhrase;
        var headers = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Connection: close\r\n")
            .Append("Cache-Control: private, no-store\r\n");
        CopyHeader(response, headers, "Accept-Ranges");
        CopyHeader(response, headers, "Content-Range");
        CopyHeader(response, headers, "ETag");
        CopyHeader(response, headers, "Last-Modified");
        if (response.Content.Headers.ContentType is not null)
            headers.Append("Content-Type: ").Append(response.Content.Headers.ContentType).Append("\r\n");
        if (response.Content.Headers.ContentLength is long length)
            headers.Append("Content-Length: ").Append(length).Append("\r\n");
        headers.Append("\r\n");
        await target.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), cancellationToken).ConfigureAwait(false);
        if (!headOnly)
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await source.CopyToAsync(target, 256 * 1024, cancellationToken).ConfigureAwait(false);
        }
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void CopyHeader(HttpResponseMessage response, StringBuilder target, string name)
    {
        IEnumerable<string>? values = null;
        if (!response.Headers.TryGetValues(name, out values) && !response.Content.Headers.TryGetValues(name, out values)) return;
        foreach (var value in values) target.Append(name).Append(": ").Append(value).Append("\r\n");
    }

    private static async Task WriteErrorAsync(Stream stream, int status, string reason, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(reason);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _listener.Stop();
        try { _acceptLoop.GetAwaiter().GetResult(); } catch { }
        _lifetime.Dispose();
        _routes.Clear();
    }

    private sealed record ProxyRequest(string Path, bool HeadOnly, string? Range);
}
