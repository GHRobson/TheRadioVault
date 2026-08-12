using System.Text;
using TheRadioVault.Web.Services;
using static TheRadioVault.Web.Tests.Fixtures.WebServerFixture;
using static TheRadioVault.Web.Tests.TestAssert;

namespace TheRadioVault.Web.Tests;

internal static class WebHttpInfrastructureTests
{
    private static readonly WebHttpRequestBodyPolicy TestBodyPolicy = new(64, TimeSpan.FromSeconds(1));

    public static IReadOnlyList<(string Name, Action Run)> Cases { get; } =
    [
        ("HTTP reader parses a bounded fixed request", HttpReaderParsesBoundedFixedRequest),
        ("HTTP reader parses chunk extensions and trailers", HttpReaderParsesChunkExtensionsAndTrailers),
        ("HTTP reader rejects oversized bodies before allocation", HttpReaderRejectsOversizedBody),
        ("HTTP reader rejects oversized headers", HttpReaderRejectsOversizedHeader),
        ("HTTP reader rejects ambiguous message framing", HttpReaderRejectsAmbiguousFraming),
        ("HTTP reader rejects unsupported transfer coding", HttpReaderRejectsUnsupportedTransferCoding),
        ("HTTP reader reports its own timeout", HttpReaderReportsTimeout),
        ("HTTP writer preserves HEAD content length", HttpWriterPreservesHeadContentLength),
        ("HTTP writer sanitises redirect locations", HttpWriterSanitisesRedirectLocation),
        ("HTTP server reports oversized request bodies", HttpServerReportsOversizedBodies),
        ("HTTP server reports oversized request headers", HttpServerReportsOversizedHeaders),
        ("HTTP server rejects ambiguous message framing", HttpServerRejectsAmbiguousFraming)
    ];

    private static void HttpReaderParsesBoundedFixedRequest()
    {
        var requestBytes = Encoding.ASCII.GetBytes(
            "POST /api/test?view=all HTTP/1.1\r\nHost: radiovault.local\r\nContent-Length: 5\r\n\r\nhello");
        using var stream = new MemoryStream(requestBytes);
        var result = Reader().ReadAsync(stream, CancellationToken.None).GetAwaiter().GetResult();

        Equal(WebHttpRequestReadFailure.None, result.Failure);
        Equal("POST", result.Request?.Method);
        Equal("/api/test?view=all", result.Request?.Target);
        Equal("radiovault.local", result.Request?.Headers["Host"]);
        Equal("hello", Encoding.ASCII.GetString(result.Request?.Body ?? []));
    }

    private static void HttpReaderParsesChunkExtensionsAndTrailers()
    {
        var requestBytes = Encoding.ASCII.GetBytes(
            "POST /api/test HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n" +
            "4;source=ios\r\nWiki\r\n5\r\npedia\r\n0\r\nX-Trace: complete\r\n\r\n");
        using var stream = new MemoryStream(requestBytes);
        var result = Reader().ReadAsync(stream, CancellationToken.None).GetAwaiter().GetResult();

        Equal(WebHttpRequestReadFailure.None, result.Failure);
        Equal("Wikipedia", Encoding.ASCII.GetString(result.Request?.Body ?? []));
    }

    private static void HttpReaderRejectsOversizedBody()
    {
        var requestBytes = Encoding.ASCII.GetBytes(
            "POST /api/test HTTP/1.1\r\nContent-Length: 65\r\n\r\n");
        using var stream = new MemoryStream(requestBytes);
        var result = Reader().ReadAsync(stream, CancellationToken.None).GetAwaiter().GetResult();

        Equal(WebHttpRequestReadFailure.BodyTooLarge, result.Failure);
        True(result.Request is null);
    }

    private static void HttpReaderRejectsOversizedHeader()
    {
        var requestBytes = Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nX-Oversized: " + new string('x', 48) + "\r\n\r\n");
        using var stream = new MemoryStream(requestBytes);
        var result = new WebHttpRequestReader(_ => TestBodyPolicy, maximumHeaderBytes: 32)
            .ReadAsync(stream, CancellationToken.None).GetAwaiter().GetResult();

        Equal(WebHttpRequestReadFailure.HeaderTooLarge, result.Failure);
        True(result.Request is null);
    }

    private static void HttpReaderRejectsAmbiguousFraming()
    {
        var requestBytes = Encoding.ASCII.GetBytes(
            "POST /api/test HTTP/1.1\r\nContent-Length: 4\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n");
        using var stream = new MemoryStream(requestBytes);
        var result = Reader().ReadAsync(stream, CancellationToken.None).GetAwaiter().GetResult();

        Equal(WebHttpRequestReadFailure.Malformed, result.Failure);
        True(result.Request is null);
    }

    private static void HttpReaderRejectsUnsupportedTransferCoding()
    {
        var requestBytes = Encoding.ASCII.GetBytes(
            "POST /api/test HTTP/1.1\r\nTransfer-Encoding: gzip, chunked\r\n\r\n0\r\n\r\n");
        using var stream = new MemoryStream(requestBytes);
        var result = Reader().ReadAsync(stream, CancellationToken.None).GetAwaiter().GetResult();

        Equal(WebHttpRequestReadFailure.Malformed, result.Failure);
        True(result.Request is null);
    }

    private static void HttpReaderReportsTimeout()
    {
        using var stream = new NeverCompletingReadStream();
        var result = new WebHttpRequestReader(
                _ => TestBodyPolicy,
                headerTimeout: TimeSpan.FromMilliseconds(25))
            .ReadAsync(stream, CancellationToken.None).GetAwaiter().GetResult();

        Equal(WebHttpRequestReadFailure.TimedOut, result.Failure);
        True(result.Request is null);
    }

    private static void HttpWriterPreservesHeadContentLength()
    {
        using var stream = new MemoryStream();
        WebHttpResponseWriter.WriteBytesAsync(
                stream,
                200,
                "OK",
                Encoding.UTF8.GetBytes("hello"),
                "text/plain; charset=utf-8",
                headOnly: true,
                CancellationToken.None,
                "Cache-Control: no-store\r\n")
            .GetAwaiter().GetResult();

        var response = Encoding.ASCII.GetString(stream.ToArray());
        True(response.StartsWith("HTTP/1.1 200 OK\r\n", StringComparison.Ordinal));
        True(response.Contains("Content-Length: 5\r\n", StringComparison.Ordinal));
        True(response.Contains("X-Content-Type-Options: nosniff\r\n", StringComparison.Ordinal));
        True(response.EndsWith("\r\n\r\n", StringComparison.Ordinal));
        True(!response.EndsWith("hello", StringComparison.Ordinal));
    }

    private static void HttpWriterSanitisesRedirectLocation()
    {
        using var stream = new MemoryStream();
        WebHttpResponseWriter.WriteRedirectAsync(
                stream,
                "https://radiovault.local/\r\nInjected: true",
                CancellationToken.None)
            .GetAwaiter().GetResult();

        var response = Encoding.ASCII.GetString(stream.ToArray());
        True(response.StartsWith("HTTP/1.1 302 Found\r\n", StringComparison.Ordinal));
        True(response.Contains("Location: https://radiovault.local/Injected: true\r\n", StringComparison.Ordinal));
        True(!response.Contains("\r\nInjected: true\r\n", StringComparison.Ordinal));
    }

    private static void HttpServerReportsOversizedBodies()
    {
        WithWebServer(async (port, _) =>
        {
            var response = await SendRawRequestAsync(
                port,
                "POST /api/v1/broadcasts/9/favourite HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 16385\r\n\r\n");
            True(response.StartsWith("HTTP/1.1 413 Content Too Large\r\n", StringComparison.Ordinal));
        });
    }

    private static void HttpServerReportsOversizedHeaders()
    {
        WithWebServer(async (port, _) =>
        {
            const string prefix = "GET / HTTP/1.1\r\nX-Oversized: ";
            var request = prefix + new string(
                'x',
                WebHttpRequestReader.DefaultMaximumHeaderBytes - Encoding.ASCII.GetByteCount(prefix));
            var response = await SendRawRequestAsync(port, request);
            True(response.StartsWith("HTTP/1.1 431 Request Header Fields Too Large\r\n", StringComparison.Ordinal));
        });
    }

    private static void HttpServerRejectsAmbiguousFraming()
    {
        WithWebServer(async (port, _) =>
        {
            var response = await SendRawRequestAsync(
                port,
                "POST /api/test HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 4\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n");
            True(response.StartsWith("HTTP/1.1 400 Bad Request\r\n", StringComparison.Ordinal));
        });
    }

    private static async Task<string> SendRawRequestAsync(int port, string request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, port, timeout.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
        await stream.FlushAsync(timeout.Token);
        client.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);

        using var response = new MemoryStream();
        var buffer = new byte[2048];
        int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            response.Write(buffer, 0, read);
        return Encoding.UTF8.GetString(response.ToArray());
    }

    private static WebHttpRequestReader Reader() => new(_ => TestBodyPolicy);

    private sealed class NeverCompletingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }
}
