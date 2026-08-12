using System.Text.Json;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Web.Tests.Fixtures;

internal sealed partial class TestWebArchiveProvider
{
    public IReadOnlyList<WebQueueItem> GetQueue() => _queue.OrderBy(x => x.Position).ToArray();
    public WebQueueMutationResult AddToQueue(long episodeId, bool playNext)
    {
        if (episodeId != _episode.Id) return new WebQueueMutationResult(false, "Not found", GetQueue());
        if (playNext)
        {
            for (var i = 0; i < _queue.Count; i++) _queue[i] = _queue[i] with { Position = _queue[i].Position + 1 };
        }
        _queue.Add(new WebQueueItem(++_queueId, playNext ? 0 : _queue.Count, _episode));
        return new WebQueueMutationResult(true, "Added", GetQueue());
    }
    public WebQueueMutationResult RemoveFromQueue(long queueId)
    {
        var changed = _queue.RemoveAll(x => x.QueueId == queueId) > 0;
        NormalizeQueue();
        return new WebQueueMutationResult(changed, changed ? "Removed" : "Not found", GetQueue());
    }
    public WebQueueMutationResult ClearQueue()
    {
        var changed = _queue.Count > 0;
        _queue.Clear();
        return new WebQueueMutationResult(changed, "Cleared", GetQueue());
    }
    public WebQueueMutationResult MoveQueueItem(long queueId, int direction)
    {
        var index = _queue.FindIndex(x => x.QueueId == queueId);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= _queue.Count) return new WebQueueMutationResult(false, "Cannot move", GetQueue());
        (_queue[index], _queue[target]) = (_queue[target], _queue[index]);
        NormalizeQueue();
        return new WebQueueMutationResult(true, "Moved", GetQueue());
    }
    public IReadOnlyList<WebChangeEvent> GetChanges(long afterSequence, int limit) => _changes.Where(x => x.Sequence > afterSequence).Take(limit).ToArray();
    public WebChangeFeedSnapshot GetChangeFeed(long afterSequence, int limit)
    {
        var current = _sequence;
        var earliest = _changes.Count == 0 ? current + 1 : _changes.Min(x => x.Sequence);
        return new WebChangeFeedSnapshot(current, earliest, GetChanges(afterSequence, limit));
    }
    public IReadOnlyList<WebJobSummary> GetJobs() => new[] { new WebJobSummary(JobId, "Test job", "General", "Running", 50, "Working", true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null) };
    public WebJobActionResult CancelJob(Guid jobId) => jobId == JobId
        ? new WebJobActionResult(true, "Cancellation requested.")
        : new WebJobActionResult(false, "Not found.");
    public WebMutationResult SetFavourite(long episodeId, bool favourite)
    {
        if (episodeId != _episode.Id) return new WebMutationResult(false, "Not found");
        _episode = _episode with { Favourite = favourite };
        AddChange("favourite", episodeId);
        return new WebMutationResult(true, "Changed", _episode);
    }
    public WebMutationResult SetPlayed(long episodeId, bool played)
    {
        if (episodeId != _episode.Id) return new WebMutationResult(false, "Not found");
        _episode = _episode with { Status = played ? "Completed" : "Unplayed", PositionMs = played ? _episode.DurationMs : 0 };
        AddChange("listening-status", episodeId);
        return new WebMutationResult(true, "Changed", _episode);
    }
    public WebMutationResult UpdateBroadcastMetadata(long episodeId, WebBroadcastMetadataMutation mutation)
    {
        if (episodeId != _episode.Id) return new WebMutationResult(false, "Not found");
        _episode = _episode with { Title = mutation.Title, Summary = mutation.Description };
        AddChange("metadata", episodeId);
        return new WebMutationResult(true, "Saved", _episode);
    }
    public WebAuthoritativeSettingsSnapshot GetAuthoritativeSettings()
        => new(
            Array.Empty<WebArchiveFolderSnapshot>(),
            new WebStorageSnapshot(1, 1, 0, 0, 1234),
            new WebPreservationSnapshot(1, 1, 0, 0, 1, 0, 0, DateTimeOffset.UtcNow),
            GetArchiveHealth(),
            new WebPlaybackPreferencesSnapshot(15, 30, 90, DateTimeOffset.UtcNow),
            0,
            "ok",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    public WebResearchWorkspaceSnapshot GetResearchWorkspace()
        => new(
            new WebResearchWorkspaceOverview(1, 1, 0, 0, 0, 0, 0, 0, 1, 0),
            new[]
            {
                new WebResearchWorkspaceRecord(99, _episode.Id, "RON-FEZ-2005-05-12", _episode.Show, _episode.AirDate,
                    "Standard", 1, null, _episode.Title, _episode.Summary, "researched", "in_library", 90,
                    "Synthetic test evidence", false, 0, 0, 1, 1, 1, 0, DateTime.UtcNow)
            },
            Array.Empty<WebResearchWorkspaceImport>(),
            new[] { new WebResearchWorkspaceSourceSummary("Test publisher", "archive", "example.test", 1, 1, 90, DateTime.UtcNow) },
            DateTimeOffset.UtcNow);
    public WebResearchWorkspaceRecordDetails? GetResearchWorkspaceRecord(long researchBroadcastId)
        => researchBroadcastId == 99
            ? new WebResearchWorkspaceRecordDetails(
                GetResearchWorkspace().Records[0], "XM", "", "", "Talk radio", "Synthetic archive note",
                new[] { "Ron Bennington" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                new[] { "Comedy" },
                new[] { new WebResearchWorkspaceSource("https://example.test/source", "Test source", "Test publisher", "archive", 90, "2026-07-24", new[] { "headline" }, "") },
                Array.Empty<WebResearchWorkspaceMoment>(), Array.Empty<WebResearchWorkspaceConflict>())
            : null;
    public IReadOnlyList<WebUndatedBroadcast> GetUndatedResearchBroadcasts(int? collectionId = null)
        => Array.Empty<WebUndatedBroadcast>();
    public WebAssignBroadcastDateResult AssignResearchBroadcastDate(long episodeId, DateTime broadcastDate)
        => new(episodeId, broadcastDate.Date, episodeId == _episode.Id);
    public WebResearchCoverageShow? GetResearchCoverage(int collectionId)
        => new(collectionId, _episode.Show, _episode.AirDate!.Value.Date, _episode.AirDate.Value.Date,
            new[] { new WebResearchCoverageDay(_episode.AirDate.Value.Date, false, true, true, false, 1, 100, string.Empty, _episode.Id, 99) });
    public WebResearchCoverageShow? GetResearchCoverageByShow(string show)
        => string.Equals(show, _episode.Show, StringComparison.OrdinalIgnoreCase) ? GetResearchCoverage(1) : null;
    public WebPlaybackPreferencesSnapshot SetPlaybackPreferences(WebPlaybackPreferencesSnapshot preferences) => preferences;
    public Task<object?> ExecuteClientResearchAsync(string operation, JsonElement payload, CancellationToken cancellationToken = default)
        => Task.FromResult<object?>(operation == "overview" ? GetResearchWorkspace().Overview : true);
    public Task<object?> ExecuteClientTranscriptAsync(string operation, JsonElement payload, CancellationToken cancellationToken = default)
        => Task.FromResult<object?>(operation == "jobs" ? Array.Empty<object>() : true);
    public Task<object?> ExecuteClientSpeakerAsync(string operation, JsonElement payload, CancellationToken cancellationToken = default)
        => Task.FromResult<object?>(Array.Empty<object>());
    public Task<object?> ExecuteClientTranscriptionAsync(string operation, JsonElement payload, CancellationToken cancellationToken = default)
        => Task.FromResult<object?>(operation switch
        {
            "jobs" => Array.Empty<object>(),
            "queue" or "retry" => JobId,
            _ => true
        });
    public Task<object?> ExecuteClientWikiAsync(string operation, JsonElement payload, CancellationToken cancellationToken = default)
        => Task.FromResult<object?>(operation switch
        {
            "overview" => new { PageCount = 0, PublishedCount = 0, DraftCount = 0, SourceCount = 0, CitationCount = 0, ImageCount = 0, TimelineEventCount = 0, LastUpdatedAt = (DateTimeOffset?)null, LastImportedAt = (DateTimeOffset?)null },
            "browse" => Array.Empty<object>(),
            _ => null
        });
    public Task<WebResearchPackPreviewResponse> PreviewResearchPackAsync(Stream packageStream, string sourceName, CancellationToken cancellationToken = default)
        => Task.FromResult(new WebResearchPackPreviewResponse(
            Guid.NewGuid(),
            new WebResearchPackPreview(
                sourceName, _episode.Show, 3, 2, 1, 0, 0, 0, 1, 1,
                0, 0, 4, 1, 8, 0, false, false, "test-package-hash", 1),
            DateTimeOffset.UtcNow.AddMinutes(20)));
    public WebResearchPackImportJob StartResearchPackImport(Guid sessionId)
        => CompletedResearchImport(sessionId);
    public WebResearchPackImportJob GetResearchPackImportStatus(Guid sessionId)
        => CompletedResearchImport(sessionId);
    public bool CancelResearchPackImport(Guid sessionId) => false;
    public Task<WebResearchPackExportPayload> ExportResearchPackAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<WebWikiPackPreview> PreviewWikiPackAsync(Stream packageStream, string sourceName, CancellationToken cancellationToken = default)
        => Task.FromResult(new WebWikiPackPreview(sourceName, "hash", 1, 1, 0, 0, 0, 1, 1, 1, 1, Array.Empty<string>(), true, "Ready", Array.Empty<WebWikiPackPageChangePreview>()));
    public Task<WebWikiPackImportResult> ApplyWikiPackAsync(Stream packageStream, string sourceName, string expectedSha256, CancellationToken cancellationToken = default)
        => Task.FromResult(new WebWikiPackImportResult(1, 0, 0, 0, 1, 1, 1, 1, 1, "Imported"));
    public Task<WebWikiPackExportPayload> ExportWikiPackAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new WebWikiPackExportPayload(Array.Empty<byte>(), "test.rvwiki", 0, 0));

    private static WebResearchPackImportJob CompletedResearchImport(Guid sessionId)
        => new(sessionId, JobId, "Completed", 100, "Complete", 1, 1, false,
            new WebResearchPackImportResult(1, 1, 1, 0, 0, 0, 1, 1, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0));

    private void NormalizeQueue()
    {
        for (var i = 0; i < _queue.Count; i++) _queue[i] = _queue[i] with { Position = i };
    }
    private void AddChange(string kind, long episodeId) => _changes.Add(new WebChangeEvent(++_sequence, kind, episodeId, "test", DateTimeOffset.UtcNow));
}
