using System.Text.Json;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Web.Services;

public sealed partial class LocalWebServer
{
    private async Task<bool> TryHandleSavedCollectionRouteAsync(
        Stream stream,
        string path,
        HttpRequest request,
        WebRequestMethod method,
        CancellationToken cancellationToken)
    {
        if (!path.Equals(WebApiRoutes.ClientSavedCollections, StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith(WebApiRoutes.ClientSavedCollections + "/", StringComparison.OrdinalIgnoreCase))
            return false;

        var suffix = path[WebApiRoutes.ClientSavedCollections.Length..].Trim('/');
        var isRead = method is WebRequestMethod.Get or WebRequestMethod.Head;
        var headOnly = method == WebRequestMethod.Head;
        if (suffix.Length == 0)
        {
            if (isRead)
            {
                await WriteClientJsonAsync(stream, new
                {
                    apiVersion = WebApiRoutes.Version,
                    collections = _archive.GetSavedCollections()
                }, headOnly, cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (method == WebRequestMethod.Post)
            {
                if (!TryDeserialize(request.Body, out WebSavedCollectionCreateRequest? mutation) || mutation is null)
                {
                    await WriteSavedCollectionErrorAsync(stream, 400, "Bad Request", "A valid saved collection request is required.", cancellationToken).ConfigureAwait(false);
                    return true;
                }
                await WriteSavedCollectionMutationAsync(stream, _archive.CreateSavedCollection(mutation), cancellationToken).ConfigureAwait(false);
                return true;
            }

            await WriteSavedCollectionMethodNotAllowedAsync(stream, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var segments = suffix.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!long.TryParse(segments[0], out var collectionId) || collectionId <= 0)
        {
            await WriteSavedCollectionErrorAsync(stream, 404, "Not Found", "Saved collection not found.", cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 1)
        {
            if (!isRead)
            {
                await WriteSavedCollectionMethodNotAllowedAsync(stream, cancellationToken).ConfigureAwait(false);
                return true;
            }
            var collection = _archive.GetSavedCollection(collectionId);
            if (collection is null)
            {
                await WriteSavedCollectionErrorAsync(stream, 404, "Not Found", "Saved collection not found.", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await WriteClientJsonAsync(stream, new { apiVersion = WebApiRoutes.Version, collection }, headOnly, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (method != WebRequestMethod.Post)
        {
            await WriteSavedCollectionMethodNotAllowedAsync(stream, cancellationToken).ConfigureAwait(false);
            return true;
        }

        WebSavedCollectionMutationResult result;
        if (segments.Length == 2 && segments[1].Equals("update", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryDeserialize(request.Body, out WebSavedCollectionUpdateRequest? mutation) || mutation is null)
                return await WriteInvalidSavedCollectionMutationAsync(stream, cancellationToken).ConfigureAwait(false);
            result = _archive.UpdateSavedCollection(collectionId, mutation);
        }
        else if (segments.Length == 2 && segments[1].Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryDeserialize(request.Body, out WebSavedCollectionDeleteRequest? mutation) || mutation is null)
                return await WriteInvalidSavedCollectionMutationAsync(stream, cancellationToken).ConfigureAwait(false);
            result = _archive.DeleteSavedCollection(collectionId, mutation);
        }
        else if (segments.Length == 3 && segments[1].Equals("items", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryDeserialize(request.Body, out WebSavedCollectionItemMutation? mutation) || mutation is null)
                return await WriteInvalidSavedCollectionMutationAsync(stream, cancellationToken).ConfigureAwait(false);
            result = segments[2].ToLowerInvariant() switch
            {
                "add" => _archive.AddSavedCollectionItem(collectionId, mutation),
                "remove" => _archive.RemoveSavedCollectionItem(collectionId, mutation),
                "move" => _archive.MoveSavedCollectionItem(collectionId, mutation),
                _ => new WebSavedCollectionMutationResult(false, false, true, "Saved collection action not found.", null)
            };
        }
        else
        {
            await WriteSavedCollectionErrorAsync(stream, 404, "Not Found", "Saved collection action not found.", cancellationToken).ConfigureAwait(false);
            return true;
        }

        await WriteSavedCollectionMutationAsync(stream, result, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> WriteInvalidSavedCollectionMutationAsync(Stream stream, CancellationToken cancellationToken)
    {
        await WriteSavedCollectionErrorAsync(stream, 400, "Bad Request", "A valid saved collection mutation is required.", cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static Task WriteSavedCollectionMethodNotAllowedAsync(Stream stream, CancellationToken cancellationToken)
        => WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use GET, HEAD or POST for saved collections.", "text/plain; charset=utf-8", cancellationToken);

    private static Task WriteSavedCollectionErrorAsync(Stream stream, int status, string reason, string message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, message }, JsonOptions);
        return WriteBytesResponseAsync(stream, status, reason, bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n");
    }

    private static Task WriteSavedCollectionMutationAsync(Stream stream, WebSavedCollectionMutationResult result, CancellationToken cancellationToken)
    {
        var status = result.Conflict ? 409 : result.NotFound ? 404 : result.Changed ? 200 : 400;
        var reason = status switch { 200 => "OK", 404 => "Not Found", 409 => "Conflict", _ => "Bad Request" };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        return WriteBytesResponseAsync(stream, status, reason, bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n");
    }
}
