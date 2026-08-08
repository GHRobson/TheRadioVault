using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Web.Services;

public sealed partial class LocalWebServer
{
    private async Task HandleFederationResearchWorkspaceAsync(Stream stream, bool headOnly, CancellationToken cancellationToken)
    {
        var snapshot = _archive.GetResearchWorkspace();
        await WriteJsonAsync(stream, new { apiVersion = WebApiRoutes.Version, research = snapshot }, headOnly, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleFederationResearchWorkspaceRecordAsync(Stream stream, long researchBroadcastId, bool headOnly, CancellationToken cancellationToken)
    {
        var details = _archive.GetResearchWorkspaceRecord(researchBroadcastId);
        if (details is null)
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "Research record not found.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        await WriteJsonAsync(stream, new { apiVersion = WebApiRoutes.Version, record = details }, headOnly, cancellationToken).ConfigureAwait(false);
    }


    private async Task HandleFederationResearchUndatedAsync(Stream stream, bool headOnly, CancellationToken cancellationToken)
    {
        var broadcasts = _archive.GetUndatedResearchBroadcasts();
        await WriteJsonAsync(stream, new { apiVersion = WebApiRoutes.Version, broadcasts }, headOnly, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleFederationResearchCoverageByShowAsync(Stream stream, string show, bool headOnly, CancellationToken cancellationToken)
    {
        var coverage = _archive.GetResearchCoverageByShow(show);
        if (coverage is null)
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "No dated Research coverage exists for this show.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        await WriteJsonAsync(stream, new { apiVersion = WebApiRoutes.Version, coverage }, headOnly, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleFederationResearchCoverageAsync(Stream stream, int collectionId, bool headOnly, CancellationToken cancellationToken)
    {
        var coverage = _archive.GetResearchCoverage(collectionId);
        if (coverage is null)
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "No dated Research coverage exists for this show.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        await WriteJsonAsync(stream, new { apiVersion = WebApiRoutes.Version, coverage }, headOnly, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleFederationResearchUndatedDateAsync(
        Stream stream,
        HttpRequest request,
        long episodeId,
        CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out WebAssignBroadcastDateRequest? mutation) || mutation is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A broadcast date is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        try
        {
            var result = _archive.AssignResearchBroadcastDate(episodeId, mutation.BroadcastDate);
            await WriteJsonAsync(stream, new { apiVersion = WebApiRoutes.Version, result }, false, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", exception.Message, "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", exception.Message, "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool TryMatchFederationResearchCoverageByShow(string path, out string show)
    {
        show = string.Empty;
        var prefix = WebApiRoutes.FederationResearchCoverage + "/show/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        show = Uri.UnescapeDataString(path[prefix.Length..]).Trim();
        return show.Length > 0;
    }

    private static bool TryMatchFederationResearchCoverage(string path, out int collectionId)
    {
        collectionId = 0;
        var prefix = WebApiRoutes.FederationResearchCoverage + "/";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && int.TryParse(path[prefix.Length..], out collectionId)
               && collectionId > 0;
    }

    private static bool TryMatchFederationResearchUndatedDate(string path, out long episodeId)
    {
        episodeId = 0;
        var prefix = WebApiRoutes.FederationResearchUndated + "/";
        const string suffix = "/date";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;
        var value = path[prefix.Length..^suffix.Length];
        return long.TryParse(value, out episodeId) && episodeId > 0;
    }

    private static bool TryMatchFederationResearchWorkspaceRecord(string path, out long researchBroadcastId)
    {
        researchBroadcastId = 0;
        var prefix = WebApiRoutes.FederationResearchWorkspace + "/";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && long.TryParse(path[prefix.Length..], out researchBroadcastId)
               && researchBroadcastId > 0;
    }
}
