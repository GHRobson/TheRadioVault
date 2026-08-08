using TheRadioVault.Web.Contracts;
using System.Text.Json;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Web.Services;

public sealed partial class LocalWebServer
{
    private async Task HandleFederationParityAsync(Stream stream, bool headOnly, CancellationToken cancellationToken)
    {
        var server = BuildServerInfo();
        var changes = _archive.GetChangeFeed(0, 1);
        var episodes = _archive.GetEpisodes();
        var revision = ComputeParityLibraryRevision(episodes);
        var features = new[]
        {
            Feature("dashboard", "Dashboard and Continue Listening", "read-write"),
            Feature("library", "Canonical Library, filters and collections", "read-write"),
            Feature("search", "Global search and Explore facets", "read"),
            Feature("playback", "Playback, seeking, speed and multipart transitions", "read-write"),
            Feature("playback-handoff", "Server, desktop-client and phone ownership handoff", "read-write"),
            Feature("queue", "Shared queue", "read-write"),
            Feature("favourites", "Favourites and listened status", "read-write"),
            Feature("moments", "Moments browse, create and delete", "read-write"),
            Feature("broadcast-info", "Broadcast information and research", "read-write"),
            Feature("metadata", "Metadata editor", "read-write"),
            Feature("research-packs", "Research pack import and export", "read-write"),
            Feature("research-workspace", "Research browse, sources and import history", "read"),
            Feature("transcripts", "Transcript browse, viewing and search", "read"),
            Feature("settings", "Server archive state and synchronized playback settings", "read-write"),
            Feature("archive-health", "Authoritative server Archive Health report", "read"),
            Feature("library-scan", "Manual scan and hourly server Library refresh", "read-write"),
            Feature("cache", "Persistent encrypted metadata cache", "client"),
            Feature("reconnect", "Reconnect, cached mode and stream recovery", "client"),
            Feature("diagnostics", "Connection and parity diagnostics", "read")
        };
        var snapshot = new WebRemoteClientParitySnapshot(
            server.InstanceId,
            server.DisplayName,
            server.AppVersion,
            server.CapabilityGeneration,
            server.ApiVersion,
            revision,
            changes.CurrentSequence,
            features,
            DateTimeOffset.UtcNow);
        await WriteJsonAsync(stream, new { apiVersion = WebApiRoutes.Version, parity = snapshot }, headOnly, cancellationToken).ConfigureAwait(false);
    }

    private WebRemoteClientParityFeature Feature(string id, string name, string access)
        => new(id, name, access, LanFederationEnabled,
            LanFederationEnabled ? string.Empty : "Multi-Device Library Access is not currently available on this server.");

    private static string ComputeParityLibraryRevision(IReadOnlyList<WebEpisode> episodes)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var payload = string.Join("\n", episodes.OrderBy(x => x.Id).Select(x =>
            $"{x.Id}|{x.Title}|{x.PositionMs}|{x.Status}|{x.Favourite}|{x.DateAdded:O}"));
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
