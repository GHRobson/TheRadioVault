using System.Text.Json;
using TheRadioVault.Web.Contracts;

namespace TheRadioVault.Web.Services;

public sealed partial class LocalWebServer
{
    private async Task HandleFederationWikiImportPreviewAsync(
        Stream stream,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var diagnosticId = Guid.NewGuid().ToString("N")[..8];
        try
        {
            var sourceName = ReadWikiFileName(request);
            await using var packageStream = request.OpenBodyStream();
            var result = await _archive.PreviewWikiPackAsync(packageStream, sourceName, cancellationToken).ConfigureAwait(false);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
            await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", false,
                cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            await WriteApiErrorAsync(stream, 400, "Bad Request", "invalid-wiki-pack", ex.Message,
                diagnosticId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Federation wiki preview {diagnosticId} failed: {ex}");
            await WriteApiErrorAsync(stream, 500, "Internal Server Error", "wiki-preview-failed",
                "The server could not analyse the wiki authoring pack.", diagnosticId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleFederationWikiImportApplyAsync(
        Stream stream,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var diagnosticId = Guid.NewGuid().ToString("N")[..8];
        try
        {
            if (!request.Headers.TryGetValue("X-Radio-Vault-Package-Sha256", out var expectedSha256) ||
                string.IsNullOrWhiteSpace(expectedSha256))
            {
                await WriteApiErrorAsync(stream, 400, "Bad Request", "wiki-preview-required",
                    "Preview this exact wiki pack before importing it.", diagnosticId, cancellationToken).ConfigureAwait(false);
                return;
            }
            await using var packageStream = request.OpenBodyStream();
            var result = await _archive.ApplyWikiPackAsync(
                packageStream, ReadWikiFileName(request), expectedSha256, cancellationToken).ConfigureAwait(false);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
            await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", false,
                cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            await WriteApiErrorAsync(stream, 409, "Conflict", "wiki-pack-changed", ex.Message,
                diagnosticId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Federation wiki import {diagnosticId} failed: {ex}");
            await WriteApiErrorAsync(stream, 500, "Internal Server Error", "wiki-import-failed",
                "The server could not import the wiki authoring pack.", diagnosticId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleFederationWikiExportAsync(Stream stream, CancellationToken cancellationToken)
    {
        var diagnosticId = Guid.NewGuid().ToString("N")[..8];
        try
        {
            var result = await _archive.ExportWikiPackAsync(cancellationToken).ConfigureAwait(false);
            var safeName = Uri.EscapeDataString(result.FileName);
            var headers = $"Cache-Control: no-store\r\nContent-Disposition: attachment; filename*=UTF-8''{safeName}\r\n" +
                          $"X-Radio-Vault-Wiki-Page-Count: {result.PageCount}\r\n" +
                          $"X-Radio-Vault-Wiki-Image-Count: {result.ImageCount}\r\n";
            await WriteBytesResponseAsync(stream, 200, "OK", result.Bytes,
                "application/vnd.radiovault.wiki+zip", false, cancellationToken, headers).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Federation wiki export {diagnosticId} failed: {ex}");
            await WriteApiErrorAsync(stream, 500, "Internal Server Error", "wiki-export-failed",
                "The server could not create the wiki authoring pack.", diagnosticId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ReadWikiFileName(HttpRequest request)
        => request.Headers.TryGetValue("X-Radio-Vault-File-Name", out var rawName)
            ? Uri.UnescapeDataString(rawName)
            : "RadioVault-Wiki.rvwiki";
}
