using System.Text.Json;

namespace TheRadioVault.Web.Services;

public sealed partial class LocalWebServer
{
    private async Task HandleClientOperationAsync(
        Stream stream,
        HttpRequest request,
        string operation,
        Func<string, JsonElement, CancellationToken, Task<object?>> execute,
        CancellationToken cancellationToken)
    {
        if (!request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Client service operations require POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        JsonElement payload;
        try
        {
            using var document = request.Body.Length == 0
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(request.Body);
            payload = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A valid JSON client request is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        var value = await execute(operation, payload, cancellationToken).ConfigureAwait(false);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = Contracts.WebApiRoutes.Version, value }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", false,
            cancellationToken, "Cache-Control: private, no-store\r\n").ConfigureAwait(false);
    }

    private static bool TryMatchClientOperation(string path, string root, out string operation)
    {
        operation = string.Empty;
        var prefix = root + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        operation = path[prefix.Length..].Trim('/').ToLowerInvariant();
        return operation.Length > 0 && !operation.Contains('/');
    }
}
