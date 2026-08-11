using System.Text.Json;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Web.Services;

public sealed partial class LocalWebServer
{
    private async Task<bool> TryHandlePlaybackQueueRouteAsync(
        Stream stream,
        string path,
        HttpRequest request,
        bool isHead,
        bool isPost,
        CancellationToken cancellationToken)
    {
        if (path.Equals(WebApiRoutes.PlayerTransferBegin, StringComparison.OrdinalIgnoreCase))
        {
            if (!isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandlePlaybackTransferBeginAsync(stream, request, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.PlayerTransferReady, StringComparison.OrdinalIgnoreCase))
        {
            if (!isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandlePlaybackTransferReadyAsync(stream, request, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.PlayerTransferCommit, StringComparison.OrdinalIgnoreCase))
        {
            if (!isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandlePlaybackTransferCommitAsync(stream, request, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.PlayerTransferCancel, StringComparison.OrdinalIgnoreCase))
        {
            if (!isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandlePlaybackTransferCancelAsync(stream, request, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.PlayerTransferSourceStopped, StringComparison.OrdinalIgnoreCase))
        {
            if (!isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandlePlaybackTransferSourceStoppedAsync(stream, request, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.PlayerCommand, StringComparison.OrdinalIgnoreCase))
        {
            if (!isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandlePlaybackCommandAsync(stream, request, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.PlayerWebProgress, StringComparison.OrdinalIgnoreCase))
        {
            if (!isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleWebPlaybackUpdateAsync(stream, request, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.Player, StringComparison.OrdinalIgnoreCase))
        {
            await HandlePlayerApiAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.QueueAdd, StringComparison.OrdinalIgnoreCase))
        {
            if (!isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleQueueAddAsync(stream, request, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.QueueClear, StringComparison.OrdinalIgnoreCase))
        {
            if (!isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleQueueClearAsync(stream, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (TryMatchQueueAction(path, "remove", out var removeQueueId))
        {
            if (!isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleQueueRemoveAsync(stream, removeQueueId, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (TryMatchQueueAction(path, "move", out var moveQueueId))
        {
            if (!isPost)
            {
                await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return true;
            }
            await HandleQueueMoveAsync(stream, moveQueueId, request, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.Equals(WebApiRoutes.Queue, StringComparison.OrdinalIgnoreCase))
        {
            await HandleQueueApiAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task HandlePlayerApiAsync(Stream stream, bool headOnly, CancellationToken cancellationToken)
    {
        var session = _archive.GetPlaybackSession();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            apiVersion = WebApiRoutes.Version,
            player = session.Player,
            desktop = session.Desktop,
            web = session.Phone,
            session
        }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandlePlaybackTransferBeginAsync(Stream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out WebPlaybackTransferBeginRequest? transfer) || transfer is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON playback transfer request is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        await WritePlaybackTransferResultAsync(stream, _archive.BeginPlaybackTransfer(transfer), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandlePlaybackTransferReadyAsync(Stream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out WebPlaybackTransferReadyRequest? transfer) || transfer is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON playback readiness report is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        await WritePlaybackTransferResultAsync(stream, _archive.MarkPlaybackTransferReady(transfer), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandlePlaybackTransferCommitAsync(Stream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out WebPlaybackTransferCommitRequest? transfer) || transfer is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON playback commit request is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        await WritePlaybackTransferResultAsync(stream, _archive.CommitPlaybackTransfer(transfer), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandlePlaybackTransferCancelAsync(Stream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out WebPlaybackTransferCancelRequest? transfer) || transfer is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON playback cancellation request is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        await WritePlaybackTransferResultAsync(stream, _archive.CancelPlaybackTransfer(transfer), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandlePlaybackTransferSourceStoppedAsync(Stream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out WebPlaybackTransferSourceStoppedRequest? acknowledgement) || acknowledgement is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON playback source-stop acknowledgement is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        await WritePlaybackTransferResultAsync(stream,
            _archive.AcknowledgePlaybackTransferSourceStopped(acknowledgement), cancellationToken).ConfigureAwait(false);
    }

    private async Task WritePlaybackTransferResultAsync(Stream stream, WebPlaybackTransferResult result, CancellationToken cancellationToken)
    {
        var code = result.Conflict ? 409 : 200;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, code, result.Conflict ? "Conflict" : "OK", bytes,
            "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandlePlaybackCommandAsync(Stream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out WebPlaybackCommand? command) || command is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON playback command is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        var result = _archive.ExecutePlaybackCommand(command);
        var code = result.Conflict ? 409 : 200;
        var reason = result.Conflict ? "Conflict" : "OK";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, code, reason, bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleWebPlaybackUpdateAsync(Stream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out WebClientPlaybackUpdate? update) || update is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON phone playback update is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        var result = _archive.UpdateWebPlayback(update);
        var code = result.Conflict ? 409 : 200;
        var reason = result.Conflict ? "Conflict" : "OK";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, code, reason, bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleQueueApiAsync(Stream stream, bool headOnly, CancellationToken cancellationToken)
    {
        var queue = _archive.GetQueue();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, queue, count = queue.Count }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleQueueAddAsync(Stream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out QueueAddMutation? mutation) || mutation is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON episodeId is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (await TryWriteDuplicateMutationResponseAsync(stream, request, cancellationToken).ConfigureAwait(false)) return;
        var result = _archive.AddToQueue(mutation.EpisodeId, mutation.PlayNext);
        var code = result.Changed ? 200 : 404;
        if (code == 200) MarkMutationProcessed(request);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, code, result.Changed ? "OK" : "Not Found", bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleQueueRemoveAsync(Stream stream, long queueId, CancellationToken cancellationToken)
    {
        var result = _archive.RemoveFromQueue(queueId);
        var code = result.Changed ? 200 : 404;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, code, result.Changed ? "OK" : "Not Found", bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleQueueClearAsync(Stream stream, CancellationToken cancellationToken)
    {
        var result = _archive.ClearQueue();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleQueueMoveAsync(Stream stream, long queueId, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out QueueMoveMutation? mutation) || mutation is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON direction is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        var result = _archive.MoveQueueItem(queueId, mutation.Direction);
        var code = result.Changed ? 200 : 409;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, code, result.Changed ? "OK" : "Conflict", bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private static bool TryMatchQueueAction(string path, string action, out long queueId)
    {
        queueId = 0;
        var prefix = WebApiRoutes.Queue + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var tail = path[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return tail.Length == 2 && tail[1].Equals(action, StringComparison.OrdinalIgnoreCase) && long.TryParse(tail[0], out queueId);
    }
}
