using System.Text.Json;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Web.Services;

public sealed partial class LocalWebServer
{
    private async Task HandleFederationLibraryScanAsync(
        Stream stream,
        HttpRequest request,
        bool headOnly,
        CancellationToken cancellationToken)
    {
        var diagnosticId = Guid.NewGuid().ToString("N")[..8];
        try
        {
            WebLibraryScanSnapshot snapshot;
            if (request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var trigger = TryDeserialize(request.Body, out WebLibraryScanRequest? mutation) && mutation is not null
                    ? mutation.Trigger
                    : "manual-remote";
                snapshot = await _archive.RunLibraryScanAsync(trigger, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                snapshot = _archive.GetLibraryScanStatus();
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                apiVersion = WebApiRoutes.Version,
                libraryScan = snapshot
            }, JsonOptions);
            await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly,
                cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteApiErrorAsync(stream, 409, "Conflict", "library-scan-cancelled",
                "The server Library scan was cancelled.", diagnosticId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Federation Library scan {diagnosticId} failed: {ex}");
            await WriteApiErrorAsync(stream, 500, "Internal Server Error", "library-scan-failed",
                "The server could not complete the Library scan.", diagnosticId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleFederationSettingsAsync(Stream stream, bool headOnly, CancellationToken cancellationToken)
    {
        var diagnosticId = Guid.NewGuid().ToString("N")[..8];
        try
        {
            var snapshot = _archive.GetAuthoritativeSettings();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                apiVersion = WebApiRoutes.Version,
                settings = snapshot,
                operations = new
                {
                    scheduledBackup = _options.ScheduledBackupStatus?.Invoke(),
                    deviceSync = _mutations.GetDeviceStatuses()
                }
            }, JsonOptions);
            await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly,
                cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Federation settings {diagnosticId} failed: {ex}");
            await WriteApiErrorAsync(stream, 500, "Internal Server Error", "federation-settings-failed",
                "The server archive settings could not be loaded.", diagnosticId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleFederationPlaybackPreferencesAsync(
        Stream stream,
        HttpRequest request,
        bool headOnly,
        CancellationToken cancellationToken)
    {
        var diagnosticId = Guid.NewGuid().ToString("N")[..8];
        try
        {
            WebPlaybackPreferencesSnapshot preferences;
            if (request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryDeserialize(request.Body, out WebPlaybackPreferencesSnapshot? mutation) || mutation is null)
                {
                    await WriteApiErrorAsync(stream, 400, "Bad Request", "invalid-playback-preferences",
                        "A valid playback-preferences payload is required.", diagnosticId, cancellationToken).ConfigureAwait(false);
                    return;
                }
                preferences = _archive.SetPlaybackPreferences(mutation);
            }
            else
            {
                preferences = _archive.GetAuthoritativeSettings().Playback;
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                apiVersion = WebApiRoutes.Version,
                playbackPreferences = preferences
            }, JsonOptions);
            await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly,
                cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Federation playback preferences {diagnosticId} failed: {ex}");
            await WriteApiErrorAsync(stream, 500, "Internal Server Error", "playback-preferences-failed",
                "Playback preferences could not be synchronized with the server.", diagnosticId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleFederationResearchImportPreviewAsync(
        Stream stream,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var diagnosticId = Guid.NewGuid().ToString("N")[..8];
        try
        {
            var sourceName = request.Headers.TryGetValue("X-Radio-Vault-File-Name", out var rawName)
                ? Uri.UnescapeDataString(rawName)
                : "remote-research.trvpack";
            await using var packageStream = request.OpenBodyStream();
            var result = await _archive.PreviewResearchPackAsync(packageStream, sourceName, cancellationToken).ConfigureAwait(false);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                apiVersion = WebApiRoutes.Version,
                result
            }, JsonOptions);
            await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", false,
                cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            await WriteApiErrorAsync(stream, 400, "Bad Request", "invalid-research-pack", ex.Message,
                diagnosticId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Federation research preview {diagnosticId} failed: {ex}");
            await WriteApiErrorAsync(stream, 500, "Internal Server Error", "research-preview-failed",
                ImportFailureMessage("analyse", ex), diagnosticId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleFederationResearchImportApplyAsync(
        Stream stream,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var diagnosticId = Guid.NewGuid().ToString("N")[..8];
        try
        {
            if (!TryDeserialize(request.Body, out WebResearchPackApplyRequest? mutation) || mutation is null || mutation.SessionId == Guid.Empty)
            {
                await WriteApiErrorAsync(stream, 400, "Bad Request", "invalid-research-session",
                    "A valid research preview session is required.", diagnosticId, cancellationToken).ConfigureAwait(false);
                return;
            }
            var result = _archive.StartResearchPackImport(mutation.SessionId);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                apiVersion = WebApiRoutes.Version,
                result
            }, JsonOptions);
            await WriteBytesResponseAsync(stream, 202, "Accepted", bytes, "application/json; charset=utf-8", false,
                cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await WriteApiErrorAsync(stream, 409, "Conflict", "research-session-unavailable", ex.Message,
                diagnosticId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Federation research import {diagnosticId} failed: {ex}");
            await WriteApiErrorAsync(stream, 500, "Internal Server Error", "research-import-failed",
                ImportFailureMessage("import", ex), diagnosticId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleFederationResearchImportStatusAsync(
        Stream stream,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var diagnosticId = Guid.NewGuid().ToString("N")[..8];
        try
        {
            if (!TryDeserialize(request.Body, out WebResearchPackApplyRequest? mutation) || mutation is null || mutation.SessionId == Guid.Empty)
            {
                await WriteApiErrorAsync(stream, 400, "Bad Request", "invalid-research-session",
                    "A valid research import session is required.", diagnosticId, cancellationToken).ConfigureAwait(false);
                return;
            }
            var result = _archive.GetResearchPackImportStatus(mutation.SessionId);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                apiVersion = WebApiRoutes.Version,
                result
            }, JsonOptions);
            await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", false,
                cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await WriteApiErrorAsync(stream, 409, "Conflict", "research-session-unavailable", ex.Message,
                diagnosticId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Federation research import status {diagnosticId} failed: {ex}");
            await WriteApiErrorAsync(stream, 500, "Internal Server Error", "research-import-status-failed",
                "The server could not report research import progress.", diagnosticId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ImportFailureMessage(string action, Exception exception)
    {
        var cause = exception;
        while (cause.InnerException is not null) cause = cause.InnerException;
        var detail = (cause.Message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (detail.Length > 500) detail = detail[..500] + "…";
        return string.IsNullOrWhiteSpace(detail)
            ? $"The server could not {action} the Archive Knowledge Database."
            : $"The server could not {action} the Archive Knowledge Database: {detail}";
    }

    private async Task HandleFederationResearchImportCancelAsync(
        Stream stream,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var diagnosticId = Guid.NewGuid().ToString("N")[..8];
        if (!TryDeserialize(request.Body, out WebResearchPackCancelRequest? mutation) || mutation is null || mutation.SessionId == Guid.Empty)
        {
            await WriteApiErrorAsync(stream, 400, "Bad Request", "invalid-research-session",
                "A valid research preview session is required.", diagnosticId, cancellationToken).ConfigureAwait(false);
            return;
        }
        var cancelled = _archive.CancelResearchPackImport(mutation.SessionId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            apiVersion = WebApiRoutes.Version,
            cancelled
        }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", false,
            cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleFederationResearchExportAsync(
        Stream stream,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var diagnosticId = Guid.NewGuid().ToString("N")[..8];
        try
        {
            if (!TryDeserialize(request.Body, out WebResearchPackExportRequest? exportRequest) || exportRequest is null)
            {
                await WriteApiErrorAsync(stream, 400, "Bad Request", "invalid-research-export",
                    "The export request was invalid.", diagnosticId, cancellationToken).ConfigureAwait(false);
                return;
            }
            var result = await _archive.ExportResearchPackAsync(cancellationToken).ConfigureAwait(false);
            var safeName = Uri.EscapeDataString(result.FileName);
            var headers = $"Cache-Control: no-store\r\nContent-Disposition: attachment; filename*=UTF-8''{safeName}\r\nX-Radio-Vault-Broadcast-Count: {result.BroadcastCount}\r\nX-Radio-Vault-Missing-Count: {result.MissingBroadcastCount}\r\nX-Radio-Vault-Transcript-Count: {result.TranscriptCount}\r\nX-Radio-Vault-Wiki-Page-Count: {result.WikiPageCount}\r\n";
            await WriteBytesResponseAsync(stream, 200, "OK", result.Bytes,
                "application/vnd.theradiovault.research-pack+zip", false, cancellationToken, headers).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await WriteApiErrorAsync(stream, 400, "Bad Request", "research-export-invalid", ex.Message,
                diagnosticId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Federation research export {diagnosticId} failed: {ex}");
            await WriteApiErrorAsync(stream, 500, "Internal Server Error", "research-export-failed",
                "The server could not create the research pack.", diagnosticId, cancellationToken).ConfigureAwait(false);
        }
    }
}
