using System.Globalization;
using System.Text.Json;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Web.Services;

public sealed partial class LocalWebServer
{
    private async Task HandleClientLibraryOverviewAsync(Stream stream, bool headOnly, CancellationToken cancellationToken)
        => await WriteClientJsonAsync(stream, new { apiVersion = WebApiRoutes.Version, overview = _archive.GetClientLibraryOverview() }, headOnly, cancellationToken).ConfigureAwait(false);

    private async Task HandleClientLiveRadioAsync(Stream stream, bool headOnly, CancellationToken cancellationToken)
        => await WriteClientJsonAsync(stream, new { apiVersion = WebApiRoutes.Version, station = _archive.GetLiveRadioSnapshot() }, headOnly, cancellationToken).ConfigureAwait(false);

    private async Task HandleClientLibraryBroadcastAsync(Stream stream, long episodeId, bool headOnly, CancellationToken cancellationToken)
    {
        var broadcast = _archive.GetClientLibraryBroadcast(episodeId);
        if (broadcast is null)
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "Broadcast not found.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        await WriteClientJsonAsync(stream, new { apiVersion = WebApiRoutes.Version, broadcast }, headOnly, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleClientLibraryBrowseAsync(
        Stream stream,
        IReadOnlyDictionary<string, string> query,
        bool headOnly,
        CancellationToken cancellationToken)
    {
        var request = new WebClientLibraryBrowseRequest(
            QueryText(query, "q"),
            QueryInt(query, "collectionId"),
            QueryText(query, "filter", "All"),
            QueryInt(query, "year"),
            QueryInt(query, "month"),
            Math.Clamp(QueryInt(query, "limit") ?? 250, 1, 10000),
            Math.Max(0, QueryInt(query, "offset") ?? 0),
            QueryBool(query, "newestFirst", true),
            QueryText(query, "scope", "All"),
            QueryBool(query, "hasTranscript", false),
            QueryBool(query, "hideCompleted", false));
        var result = _archive.BrowseClientLibrary(request);
        await WriteClientJsonAsync(stream, new { apiVersion = WebApiRoutes.Version, result }, headOnly, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleClientLibraryArchivePeriodsAsync(
        Stream stream,
        IReadOnlyDictionary<string, string> query,
        bool headOnly,
        CancellationToken cancellationToken)
    {
        var periods = _archive.GetClientLibraryArchivePeriods(
            QueryInt(query, "collectionId"),
            QueryInt(query, "year"),
            QueryBool(query, "hideCompleted", false));
        await WriteClientJsonAsync(stream, new { apiVersion = WebApiRoutes.Version, periods }, headOnly, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleClientLibrarySearchFacetsAsync(Stream stream, bool headOnly, CancellationToken cancellationToken)
        => await WriteClientJsonAsync(stream, new { apiVersion = WebApiRoutes.Version, facets = _archive.GetClientLibrarySearchFacets() }, headOnly, cancellationToken).ConfigureAwait(false);

    private async Task HandleClientLibrarySearchSuggestionsAsync(
        Stream stream,
        IReadOnlyDictionary<string, string> query,
        bool headOnly,
        CancellationToken cancellationToken)
    {
        var suggestions = _archive.GetClientLibrarySearchSuggestions(
            QueryText(query, "prefix"),
            Math.Clamp(QueryInt(query, "limit") ?? 10, 1, 25));
        await WriteClientJsonAsync(stream, new { apiVersion = WebApiRoutes.Version, suggestions }, headOnly, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleClientBroadcastDetailsAsync(Stream stream, long episodeId, bool headOnly, CancellationToken cancellationToken)
    {
        var broadcast = _archive.GetClientBroadcastDetails(episodeId);
        if (broadcast is null)
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "Broadcast not found.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        await WriteClientJsonAsync(stream, new { apiVersion = WebApiRoutes.Version, broadcast }, headOnly, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryMatchClientLibraryBroadcast(string path, out long episodeId)
        => TryMatchClientId(path, WebApiRoutes.ClientLibrary + "/broadcasts/", out episodeId);

    private static bool TryMatchClientBroadcast(string path, out long episodeId)
        => TryMatchClientId(path, WebApiRoutes.ClientBroadcastDetails + "/", out episodeId);

    private static bool TryMatchClientId(string path, string prefix, out long episodeId)
    {
        episodeId = 0;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               long.TryParse(path[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out episodeId) &&
               episodeId > 0;
    }

    private static string QueryText(IReadOnlyDictionary<string, string> query, string name, string fallback = "")
        => query.TryGetValue(name, out var value) ? value.Trim() : fallback;

    private static int? QueryInt(IReadOnlyDictionary<string, string> query, string name)
        => query.TryGetValue(name, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static bool QueryBool(IReadOnlyDictionary<string, string> query, string name, bool fallback)
        => query.TryGetValue(name, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static async Task WriteClientJsonAsync(Stream stream, object payload, bool headOnly, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }
}
