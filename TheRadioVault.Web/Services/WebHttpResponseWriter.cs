using System.Buffers;
using System.Text;
using System.Text.Json;

namespace TheRadioVault.Web.Services;

internal static class WebHttpResponseWriter
{
    private const string SecurityHeaders = "X-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n";

    public static Task WriteJsonAsync<T>(
        Stream stream,
        T payload,
        JsonSerializerOptions serializerOptions,
        bool headOnly,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, serializerOptions);
        return WriteBytesAsync(
            stream,
            200,
            "OK",
            bytes,
            "application/json; charset=utf-8",
            headOnly,
            cancellationToken,
            "Cache-Control: no-store\r\n");
    }

    public static Task WriteTextAsync(
        Stream stream,
        int code,
        string reason,
        string text,
        string contentType,
        CancellationToken cancellationToken)
        => WriteBytesAsync(
            stream,
            code,
            reason,
            Encoding.UTF8.GetBytes(text),
            contentType,
            headOnly: false,
            cancellationToken);

    public static async Task WriteBytesAsync(
        Stream stream,
        int code,
        string reason,
        ReadOnlyMemory<byte> bytes,
        string contentType,
        bool headOnly,
        CancellationToken cancellationToken,
        string extraHeaders = "")
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = $"HTTP/1.1 {code} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\n{SecurityHeaders}{extraHeaders}Connection: close\r\n\r\n";
        await WriteAsciiAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (!headOnly && !bytes.IsEmpty)
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteRedirectAsync(
        Stream stream,
        string location,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var safeLocation = location.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
        var header = $"HTTP/1.1 302 Found\r\nLocation: {safeLocation}\r\nContent-Length: 0\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await WriteAsciiAsync(stream, header, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAsciiAsync(Stream stream, string value, CancellationToken cancellationToken)
    {
        var requiredBytes = Encoding.ASCII.GetByteCount(value);
        var rented = ArrayPool<byte>.Shared.Rent(requiredBytes);
        try
        {
            var written = Encoding.ASCII.GetBytes(value.AsSpan(), rented.AsSpan());
            await stream.WriteAsync(rented.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }
}
