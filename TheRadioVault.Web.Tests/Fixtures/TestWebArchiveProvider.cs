using System.Text.Json;
using TheRadioVault.Core.Domain;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;
using TheRadioVault.Web.Services;

namespace TheRadioVault.Web.Tests.Fixtures;

internal sealed partial class TestWebArchiveProvider : IWebArchiveProvider
{
    public static readonly Guid JobId = Guid.Parse("99d9f88d-c8d5-4d60-86b0-82157625d7d5");
    private WebEpisode _episode = new(9, "Ron & Fez", "Phoenix test broadcast", new DateTime(2005, 5, 12),
        "A specific test summary.", "Ron Bennington, Fez Whatley", "Comedy", 3_600_000, 120_000, "In Progress", true,
        new DateTime(2026, 7, 16), new DateTime(2026, 7, 16), "C:\\Audio\\9.mp3", string.Empty);
    private readonly List<WebChangeEvent> _changes = new();
    private readonly List<WebQueueItem> _queue = new();
    private WebPlaybackState _desktop;
    private WebPlaybackState _web = new(null, string.Empty, string.Empty, 0, 0, "Idle", null, false, null, "Phone");
    private string _webClient = string.Empty;
    private string _ownerDevice = "Server";
    private string _ownerClientId = string.Empty;
    private long _generation;
    private long _sequence;
    private long _queueId;
    private readonly bool _throwPlayback;
    private readonly PlaybackTransferCoordinator _playbackTransfers = new();
    private WebPlaybackCommittedTransfer? _committedTransfer;
    private WebPlaybackTransferTicket? _committedTicket;

    public TestWebArchiveProvider(bool throwPlayback = false, string? audioPath = null)
    {
        _throwPlayback = throwPlayback;
        if (!string.IsNullOrWhiteSpace(audioPath))
            _episode = _episode with { AudioPath = audioPath };
        _desktop = new WebPlaybackState(_episode.Id, _episode.Show, _episode.Title, _episode.PositionMs, _episode.DurationMs, _episode.Status, _episode.LastPlayedAt, true, DateTimeOffset.UtcNow, "Server", 1, 100);
        _queue.Add(new WebQueueItem(++_queueId, 0, _episode));
    }

    public IReadOnlyList<WebEpisode> GetEpisodes() => new[] { _episode };
    public WebEpisode? GetEpisode(long episodeId) => episodeId == _episode.Id ? _episode : null;
    public WebBroadcastDetails? GetBroadcastDetails(long episodeId) => episodeId == _episode.Id
        ? new WebBroadcastDetails
        {
            Episode = _episode,
            BroadcastUid = "RON-FEZ-2005-05-12",
            Station = "WJFK",
            Slot = "Afternoon",
            PartNumber = 1,
            TotalParts = 2,
            ArchiveNotes = "Server archive note.",
            People = new[] { new WebPerson("Ron Bennington", "host") },
            Topics = new[] { "Comedy" },
            Moments = new[] { new WebMoment(1, 90_000, "Test moment", "Moment notes") },
            Research = new WebResearchDetails { ResearchBroadcastId = 99, Confidence = 90 }
        }
        : null;
    public WebClientLibraryOverview GetClientLibraryOverview() => new(
        1, 0, 1, 1, 0, true,
        new[] { new WebClientLibraryCollectionSummary(1, _episode.Show, 1) },
        new[] { ClientSummary() },
        new[] { ClientSummary() },
        Array.Empty<WebClientLibraryBroadcastSummary>());
    public WebClientLibraryBroadcastSummary? GetClientLibraryBroadcast(long episodeId)
        => episodeId == _episode.Id ? ClientSummary() : null;
    public WebClientLibraryBrowseResult BrowseClientLibrary(WebClientLibraryBrowseRequest request)
        => new(new[] { ClientSummary() }, 1, true);
    public IReadOnlyList<WebClientLibraryArchivePeriodSummary> GetClientLibraryArchivePeriods(int? collectionId, int? year, bool hideCompleted)
        => new[] { new WebClientLibraryArchivePeriodSummary(2005, "2005", 1, 0, 1, 0, "0 listened · 0%", _episode.Show, null) };
    public WebClientLibrarySearchFacets GetClientLibrarySearchFacets()
        => new(new[] { 2005 }, 1);
    public IReadOnlyList<WebClientLibrarySearchSuggestion> GetClientLibrarySearchSuggestions(string prefix, int limit)
        => new[] { new WebClientLibrarySearchSuggestion(_episode.Title, "Broadcast", 1) };
    public WebClientBroadcastDetails? GetClientBroadcastDetails(long episodeId)
        => episodeId == _episode.Id
            ? new WebClientBroadcastDetails(
                _episode.Id, "CANONICAL-9", "RON-FEZ-2005-05-12", 1, _episode.Show,
                DateOnly.FromDateTime(_episode.AirDate!.Value), "Afternoon", _episode.Title, _episode.Summary,
                "WJFK", string.Empty, string.Empty, string.Empty, string.Empty, "Server archive note.",
                string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                string.Empty, "Ron Bennington", string.Empty, string.Empty, string.Empty, new[] { "Comedy" },
                null, 1, 2, 2,
                [
                    ArchiveEntityLinkFactory.ForBroadcast("CANONICAL-9", _episode.Id, _episode.Title),
                    ArchiveEntityLinkFactory.ForShow(1, _episode.Show),
                    ArchiveEntityLinkFactory.ForPerson("Ron Bennington", "host"),
                    ArchiveEntityLinkFactory.ForTopic("Comedy")
                ])
            : null;
    private WebClientLibraryBroadcastSummary ClientSummary() => new(
        "CANONICAL-9", _episode.Id, "RON-FEZ-2005-05-12", 1, _episode.Show,
        DateOnly.FromDateTime(_episode.AirDate!.Value), new DateTimeOffset(_episode.DateAdded), "Afternoon",
        _episode.Title, _episode.Summary, _episode.Favourite, false, true, _episode.PositionMs,
        _episode.DurationMs, _episode.LastPlayedAt.HasValue ? new DateTimeOffset(_episode.LastPlayedAt.Value) : null,
        null, 1, 2, 2, false, string.Empty, string.Empty, 0);
    public WebTranscriptDetails? GetTranscript(long episodeId) => episodeId == _episode.Id
        ? new WebTranscriptDetails
        {
            CanonicalBroadcastId = episodeId,
            Status = "Complete",
            Language = "en",
            WordCount = 4,
            DurationMs = _episode.DurationMs,
            UpdatedAt = DateTimeOffset.UtcNow,
            Segments = new[] { new WebTranscriptSegment(0, 0, 1000, "Synthetic transcript text.", "Ron", "ron", "Speech", true, 0.99) }
        }
        : null;
    public IReadOnlyList<WebTranscriptSummary> GetTranscripts() => new[]
    {
        new WebTranscriptSummary(1, _episode.Id, _episode.Show, _episode.AirDate, _episode.Title, "Complete", "en", "test", "test", "test", 4, 1, 1, 1, _episode.DurationMs, DateTimeOffset.UtcNow)
    };
    public IReadOnlyList<WebMomentSummary> GetMoments() => Array.Empty<WebMomentSummary>();
    public WebMomentMutationResult AddMoment(long episodeId, WebMomentMutation mutation)
        => episodeId == _episode.Id
            ? new WebMomentMutationResult(true, false, "Added", new WebMoment(1, mutation.PositionMs, mutation.Title, mutation.Notes))
            : new WebMomentMutationResult(false, false, "Not found", null);
    public WebMutationResult DeleteMoment(long episodeId, long momentId)
        => new(episodeId == _episode.Id, episodeId == _episode.Id ? "Deleted" : "Not found");
    public WebMutationResult UpdateMoment(long momentId, WebMomentEditMutation mutation)
        => new(momentId == 1, momentId == 1 ? "Updated" : "Not found");
    public WebCanonicalMediaManifest? GetCanonicalMediaManifest(long episodeId, string? recordingKey = null)
        => episodeId == _episode.Id
            ? new WebCanonicalMediaManifest(_episode.Id, "RON-FEZ-2005-05-12", recordingKey ?? "REC-1", "Preferred", _episode.DurationMs,
                new[] { new WebCanonicalMediaPart(1, 1, 0, _episode.DurationMs, 99, 1234, "AvailableOffline", _episode.AudioPath) })
            : null;
    public WebCanonicalMediaPart? GetCanonicalMediaPart(long episodeId, long mediaFileId, string? recordingKey = null)
        => GetCanonicalMediaManifest(episodeId, recordingKey)?.Parts.FirstOrDefault(x => x.MediaFileId == mediaFileId);
    public WebArchiveHealthSummary GetArchiveHealth() => new(95, 98, 94, 92, 96, 2, 1, 1, 0, DateTime.UtcNow);
    public WebLibraryScanSnapshot GetLibraryScanStatus() => new(false, true, "test", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow,
        "Synthetic scan complete.", 1, 0, 0, 1, 0, 0, 0, 0, 0);
    public Task<WebLibraryScanSnapshot> RunLibraryScanAsync(string trigger, CancellationToken cancellationToken = default)
        => Task.FromResult(GetLibraryScanStatus() with { Trigger = trigger });
}
