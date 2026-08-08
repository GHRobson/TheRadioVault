using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Web.Services;

public sealed partial class LocalWebServer
{
    private readonly string _federationSyncSessionId = Guid.NewGuid().ToString("N");

    private async Task HandleFederationLibrarySyncAsync(
        Stream stream,
        IReadOnlyDictionary<string, string> query,
        bool headOnly,
        CancellationToken cancellationToken)
    {
        var diagnosticId = Guid.NewGuid().ToString("N")[..8];
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var requestedSession = query.TryGetValue("session", out var session) ? session.Trim() : string.Empty;
            var requestedRevision = query.TryGetValue("revision", out var revision) ? revision.Trim() : string.Empty;
            var metadataOnly = query.TryGetValue("metadataOnly", out var rawMetadataOnly) &&
                               bool.TryParse(rawMetadataOnly, out var parsedMetadataOnly) &&
                               parsedMetadataOnly;
            var afterSequence = query.TryGetValue("after", out var rawAfter) && long.TryParse(
                rawAfter,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedAfter)
                ? Math.Max(0, parsedAfter)
                : 0;

            var feed = _archive.GetChangeFeed(afterSequence, 200);
            var sessionMatches = string.Equals(requestedSession, _federationSyncSessionId, StringComparison.Ordinal);
            var journalAvailable = afterSequence == 0 || afterSequence >= Math.Max(0, feed.EarliestAvailableSequence - 1);
            var resetRequired = !sessionMatches || !journalAvailable || afterSequence > feed.CurrentSequence;

            var currentRevision = GetFederationLibraryRevision(feed.CurrentSequence);

            if (!resetRequired && !string.Equals(requestedRevision, currentRevision, StringComparison.Ordinal))
            {
                // A mismatched token with no retained event indicates that the
                // remote-client cache cannot be advanced safely from its
                // claimed state. Reset rather than guessing.
                if (feed.Changes.Count == 0) resetRequired = true;
            }

            var changes = resetRequired ? Array.Empty<WebChangeEvent>() : feed.Changes.ToArray();
            if (!resetRequired && changes.Length >= 200 && changes[^1].Sequence < feed.CurrentSequence)
            {
                // The client is farther behind than one bounded delta response.
                // A complete reset is deterministic and avoids skipping an
                // intermediate mutation while keeping responses bounded.
                resetRequired = true;
                changes = Array.Empty<WebChangeEvent>();
            }
            if (!resetRequired && changes.Any(x =>
                    x.EpisodeId is null &&
                    (x.Kind.Equals("library", StringComparison.OrdinalIgnoreCase) ||
                     x.Kind.Equals("metadata", StringComparison.OrdinalIgnoreCase) ||
                     x.Kind.Equals("research", StringComparison.OrdinalIgnoreCase) ||
                     x.Kind.Equals("listening-status", StringComparison.OrdinalIgnoreCase) ||
                     x.Kind.Equals("favourite", StringComparison.OrdinalIgnoreCase))))
            {
                resetRequired = true;
                changes = Array.Empty<WebChangeEvent>();
            }

            var noChanges = !resetRequired &&
                            feed.CurrentSequence == afterSequence &&
                            string.Equals(requestedRevision, currentRevision, StringComparison.Ordinal);

            WebAnywhereBootstrap? bootstrap = null;
            IReadOnlyList<WebEpisode> synchronizedEpisodes = Array.Empty<WebEpisode>();
            IReadOnlyList<long> deletedEpisodeIds = Array.Empty<long>();
            IReadOnlyList<WebTranscriptSummary>? transcriptSummaries = null;
            IReadOnlyList<WebMomentSummary>? moments = null;
            WebAuthoritativeSettingsSnapshot? settingsSnapshot = null;

            if (!noChanges && !metadataOnly)
            {
                var episodes = _archive.GetEpisodes();
                var queue = _archive.GetQueue().Take(200).ToArray();
                bootstrap = BuildFederationLibraryBootstrap(episodes, queue, 50);
                if (resetRequired)
                {
                    synchronizedEpisodes = episodes.OrderBy(x => x.Id).ToArray();
                }
                else
                {
                    var changedIds = changes
                        .Where(x => x.EpisodeId.HasValue)
                        .Select(x => x.EpisodeId!.Value)
                        .Distinct()
                        .ToArray();
                    var byId = episodes.ToDictionary(x => x.Id);
                    synchronizedEpisodes = changedIds
                        .Where(byId.ContainsKey)
                        .Select(id => byId[id])
                        .OrderBy(x => x.Id)
                        .ToArray();
                    deletedEpisodeIds = changedIds.Where(id => !byId.ContainsKey(id)).OrderBy(x => x).ToArray();
                }

                var refreshSupplemental = resetRequired || changes.Any(x =>
                    x.Kind.Equals("library", StringComparison.OrdinalIgnoreCase) ||
                    x.Kind.Equals("research", StringComparison.OrdinalIgnoreCase) ||
                    x.Kind.Equals("metadata", StringComparison.OrdinalIgnoreCase) ||
                    x.Kind.Equals("moment", StringComparison.OrdinalIgnoreCase) ||
                    (x.Kind.Equals("job", StringComparison.OrdinalIgnoreCase) &&
                     x.Reason.StartsWith("Transcription:", StringComparison.OrdinalIgnoreCase)));
                if (refreshSupplemental)
                {
                    transcriptSummaries = _archive.GetTranscripts();
                    moments = _archive.GetMoments();
                }
                var refreshSettings = resetRequired || changes.Any(x =>
                    x.Kind.Equals("settings", StringComparison.OrdinalIgnoreCase) ||
                    x.Kind.Equals("library", StringComparison.OrdinalIgnoreCase));
                if (refreshSettings) settingsSnapshot = _archive.GetAuthoritativeSettings();
            }

            var result = new WebFederationLibrarySync(
                _options.ServerInstanceId,
                _federationSyncSessionId,
                feed.CurrentSequence,
                currentRevision,
                resetRequired,
                noChanges,
                changes,
                synchronizedEpisodes,
                deletedEpisodeIds,
                bootstrap,
                transcriptSummaries,
                moments,
                settingsSnapshot,
                DateTimeOffset.UtcNow);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                apiVersion = WebApiRoutes.Version,
                sync = result
            }, JsonOptions);
            await WriteBytesResponseAsync(
                stream,
                200,
                "OK",
                bytes,
                "application/json; charset=utf-8",
                headOnly,
                cancellationToken,
                "Cache-Control: no-store\r\n").ConfigureAwait(false);
            stopwatch.Stop();
            if (!noChanges || stopwatch.ElapsedMilliseconds >= 2_000)
            {
                _log?.Invoke(
                    $"Federation Library sync {diagnosticId} completed in {stopwatch.ElapsedMilliseconds:N0} ms; " +
                    $"mode={(metadataOnly ? resetRequired ? "metadata-reset" : noChanges ? "metadata-check" : "metadata-delta" : resetRequired ? "full-reset" : noChanges ? "revision-check" : "delta")}, " +
                    $"events={changes.Length:N0}, rows={synchronizedEpisodes.Count:N0}, deletions={deletedEpisodeIds.Count:N0}, sequence={feed.CurrentSequence:N0}.");
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _log?.Invoke($"Federation library sync {diagnosticId} failed after {stopwatch.ElapsedMilliseconds:N0} ms: {ex}");
            await WriteApiErrorAsync(
                stream,
                500,
                "Internal Server Error",
                "federation-library-sync-failed",
                "The server could not synchronize its library with this remote client.",
                diagnosticId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private WebAnywhereBootstrap BuildFederationLibraryBootstrap(
        IReadOnlyList<WebEpisode> episodes,
        IReadOnlyList<WebQueueItem> queue,
        int limit)
    {
        limit = Math.Clamp(limit, 1, 50);
        var shows = WebEpisodeQuery.GetShows(episodes)
            .Select(x => new WebShowSummary(x.Show, x.Count))
            .ToArray();
        return new WebAnywhereBootstrap
        {
            Server = BuildServerInfo(),
            Library = BuildLibrarySummary(episodes, shows.Length),
            Shows = shows,
            Years = WebEpisodeQuery.GetYears(episodes),
            ContinueListening = WebEpisodeQuery.Apply(episodes, "continue", string.Empty, string.Empty, limit, DateTime.Today),
            Recent = WebEpisodeQuery.Apply(episodes, "recent", string.Empty, string.Empty, limit, DateTime.Today),
            Favourites = WebEpisodeQuery.Apply(episodes, "favorites", string.Empty, string.Empty, limit, DateTime.Today),
            OnThisDay = WebEpisodeQuery.Apply(episodes, "onthisday", string.Empty, string.Empty, limit, DateTime.Today),
            Playback = _archive.GetPlaybackSession(),
            Queue = queue,
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }

    private string GetFederationLibraryRevision(long sequence)
    {
        // The bounded event journal is the synchronization authority. The
        // per-process session changes whenever the server restarts, while the
        // monotonically increasing sequence changes for every published
        // Library mutation. Hashing both yields an opaque, cheap revision token
        // without reserializing the whole archive on every six-second poll.
        var payload = string.Join("|", _options.ServerInstanceId, _federationSyncSessionId,
            Math.Max(0, sequence).ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

}
