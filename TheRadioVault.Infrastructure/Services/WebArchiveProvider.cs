using System.Collections.Concurrent;
using TheRadioVault.Models;
using TheRadioVault.Core.Events;
using TheRadioVault.Core.Playback;
using TheRadioVault.Services.Jobs;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;
using TheRadioVault.Web.Services;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Services;

namespace TheRadioVault.Services;

internal sealed partial class WebArchiveProvider : IWebArchiveProvider, IDisposable
{
    private const int ChangeHistoryLimit = 300;
    private static readonly TimeSpan WebPlaybackLeaseDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RemoteOwnerStaleAfter = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan RemoteControlLeaseDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PendingPlaybackClaimDuration = TimeSpan.FromSeconds(45);

    private readonly DatabaseService _database;
    private readonly IApplicationEventBus _events;
    private readonly ILivePlaybackStateStore _livePlayback;
    private readonly IBackgroundJobQueue _jobs;
    private readonly IWebPlaybackController _playbackController;
    private readonly ServerTranscriptionRuntime? _transcription;
    private readonly ITranscriptRepository _transcripts;
    private readonly ISpeakerIdentityRepository _speakers;
    private readonly ConcurrentQueue<WebChangeEvent> _changes = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly object _playbackGate = new();
    private readonly object _desktopCommandGate = new();
    private readonly object _episodeSnapshotGate = new();
    private readonly object _libraryScanStatusGate = new();
    private readonly SemaphoreSlim _libraryScanGate = new(1, 1);
    private readonly PlaybackTransferCoordinator _playbackTransfers = new();
    private WebPlaybackCommittedTransfer? _lastCommittedTransfer;
    private WebPlaybackTransferTicket? _lastCommittedTransferTicket;
    private EpisodeSnapshot? _episodeSnapshot;
    private long _changeSequence;
    private long _webRevision;
    private WebPlaybackState _webPlayback = IdleWebPlayback();
    private string _webPlaybackClientId = string.Empty;
    private DateTimeOffset _webPlaybackLeaseExpiresAt = DateTimeOffset.MinValue;
    private string _remoteControlClientId = string.Empty;
    private DateTimeOffset _remoteControlLeaseExpiresAt = DateTimeOffset.MinValue;
    private string _pendingPhoneOwnerClientId = string.Empty;
    private DateTimeOffset _pendingPhoneOwnerExpiresAt = DateTimeOffset.MinValue;
    private string _pendingRemoteDeviceName = "Phone";
    private string _pendingRemoteDeviceKind = "Phone";
    private readonly Dictionary<string, WebPlaybackDevice> _remotePlaybackDevices = new(StringComparer.Ordinal);
    private string _playbackOwnerDevice = "Server";
    private string _playbackOwnerClientId = string.Empty;
    private long _playbackSessionGeneration;
    private WebLibraryScanSnapshot _libraryScanStatus = new(
        IsRunning: false,
        Started: false,
        Trigger: string.Empty,
        StartedAt: null,
        CompletedAt: null,
        Message: "No Library scan has completed in this session.",
        FilesFound: 0,
        Added: 0,
        Updated: 0,
        Unchanged: 0,
        Errors: 0,
        CanonicalBroadcastsAdded: 0,
        CanonicalRecordingsAdded: 0,
        CanonicalEpisodesMapped: 0,
        CanonicalItemsNeedingReview: 0);

    public WebArchiveProvider(
        DatabaseService database,
        IApplicationEventBus events,
        ILivePlaybackStateStore livePlayback,
        IBackgroundJobQueue jobs,
        IWebPlaybackController playbackController,
        ServerTranscriptionRuntime? transcription = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _livePlayback = livePlayback ?? throw new ArgumentNullException(nameof(livePlayback));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _playbackController = playbackController ?? throw new ArgumentNullException(nameof(playbackController));
        _transcription = transcription;
        _transcripts = transcription?.TranscriptRepository ?? new SqliteTranscriptRepository(_database.PlatformDatabase);
        _speakers = transcription?.SpeakerRepository ?? new SqliteSpeakerIdentityRepository(_database.PlatformDatabase);

        var lastFolderScan = _database.GetLibraryFolders()
            .Where(x => x.LastScanAt.HasValue)
            .Select(x => new DateTimeOffset(DateTime.SpecifyKind(x.LastScanAt!.Value, DateTimeKind.Local)))
            .DefaultIfEmpty()
            .Max();
        if (lastFolderScan != default)
        {
            _libraryScanStatus = _libraryScanStatus with
            {
                CompletedAt = lastFolderScan,
                Message = $"Last Library scan completed {lastFolderScan.ToLocalTime():dd MMM yyyy HH:mm}."
            };
        }

        _subscriptions.Add(_events.Subscribe<LibraryScanCompletedEvent>(x =>
        {
            InvalidateEpisodeSnapshot();
            AddChange("library", null, "scan-completed", x.OccurredAt);
        }));
        _subscriptions.Add(_events.Subscribe<ResearchAuditCompletedEvent>(x => AddChange("research", null, "quality-audit-completed", x.OccurredAt)));
        _subscriptions.Add(_events.Subscribe<ResearchUpdatedEvent>(x =>
        {
            InvalidateEpisodeSnapshot();
            AddChange("research", x.EpisodeId, x.Reason, x.OccurredAt);
        }));
        _subscriptions.Add(_events.Subscribe<MetadataChangedEvent>(x =>
        {
            InvalidateEpisodeSnapshot();
            AddChange("metadata", x.EpisodeId, x.Reason, x.OccurredAt);
        }));
        _subscriptions.Add(_events.Subscribe<FavouritesChangedEvent>(x =>
        {
            InvalidateEpisodeSnapshot();
            foreach (var id in x.EpisodeIds) AddChange("favourite", id, x.Favourite ? "favourited" : "unfavourited", x.OccurredAt);
        }));
        _subscriptions.Add(_events.Subscribe<ListeningStatusChangedEvent>(x =>
        {
            InvalidateEpisodeSnapshot();
            foreach (var id in x.EpisodeIds) AddChange("listening-status", id, x.Status, x.OccurredAt);
        }));
        _subscriptions.Add(_events.Subscribe<QueueChangedEvent>(x =>
        {
            if (x.EpisodeIds.Count == 0) AddChange("queue", null, x.Reason, x.OccurredAt);
            else foreach (var id in x.EpisodeIds) AddChange("queue", id, x.Reason, x.OccurredAt);
        }));
        _subscriptions.Add(_events.Subscribe<PlaybackChangedEvent>(x =>
        {
            InvalidateEpisodeSnapshot();
            AddChange("player", x.EpisodeId, x.IsPlaying ? "playing" : "paused", x.OccurredAt);
        }));
        _subscriptions.Add(_events.Subscribe<PlaybackOwnershipChangedEvent>(HandlePlaybackOwnershipChanged));
        _subscriptions.Add(_events.Subscribe<BackgroundJobChangedEvent>(x => AddChange("job", null, $"{x.Job.Category}:{x.Job.State}", x.OccurredAt)));
    }

    public IReadOnlyList<WebEpisode> GetEpisodes()
        => GetEpisodeSnapshot().Episodes;

    public WebEpisode? GetEpisode(long episodeId)
    {
        lock (_episodeSnapshotGate)
        {
            if (_episodeSnapshot?.ById.TryGetValue(episodeId, out var cached) == true)
                return cached;
        }

        return GetEpisodeDirect(episodeId);
    }

    private WebEpisode? GetEpisodeDirect(long episodeId)
    {
        var episode = _database.GetEpisode(episodeId);
        return episode is null ? null : Map(episode);
    }

    public WebBroadcastDetails? GetBroadcastDetails(long episodeId)
    {
        var episode = GetEpisode(episodeId);
        if (episode is null) return null;

        var knowledge = _database.GetBroadcastKnowledge(episodeId);
        var people = knowledge.Hosts.Select(x => new WebPerson(x, "host"))
            .Concat(knowledge.Guests.Select(x => new WebPerson(x, "guest")))
            .Concat(knowledge.Callers.Select(x => new WebPerson(x, "caller")))
            .Concat(knowledge.MentionedPeople.Select(x => new WebPerson(x, "mentioned")))
            .ToArray();
        var moments = _database.GetMoments(episodeId)
            .Select(x => new WebMoment(x.Id, x.PositionMs, x.Title, x.Notes))
            .ToArray();

        WebResearchDetails? research = null;
        IReadOnlyList<WebMetadataField> catalogueFields = Array.Empty<WebMetadataField>();
        var researchRecord = _database.GetResearchLibraryRecords()
            .Where(x => x.EpisodeId == episodeId)
            .OrderByDescending(x => x.Confidence)
            .ThenByDescending(x => x.UpdatedAt)
            .FirstOrDefault();
        if (researchRecord is not null)
        {
            var details = _database.GetResearchLibraryRecordDetails(researchRecord.Id);
            catalogueFields = BuildCatalogueFields(details?.Broadcast?.Research?.Catalogue);
            research = new WebResearchDetails
            {
                ResearchBroadcastId = researchRecord.Id,
                Confidence = researchRecord.Confidence,
                ResearchState = researchRecord.ResearchState,
                ExistenceStatus = researchRecord.ExistenceStatus,
                NeedsReview = researchRecord.NeedsReview,
                ConflictCount = researchRecord.ConflictCount,
                Sources = details?.SourceDetails.Select(x => new WebResearchSource(
                    x.DisplayTitle,
                    SafeWebUrl(x.Url),
                    x.Publisher,
                    x.SourceType,
                    x.Confidence)).ToArray() ?? Array.Empty<WebResearchSource>()
            };
        }

        return new WebBroadcastDetails
        {
            Episode = episode,
            BroadcastUid = knowledge.BroadcastUid,
            Station = knowledge.Edition,
            Slot = knowledge.BroadcastSlot,
            PartNumber = knowledge.PartNumber,
            TotalParts = knowledge.TotalParts,
            ArchiveNotes = knowledge.ArchiveNotes,
            PersonalNotes = knowledge.PersonalNotes,
            People = people,
            Topics = knowledge.Topics.ToArray(),
            CatalogueFields = catalogueFields,
            Moments = moments,
            Research = research
        };
    }

    private static IReadOnlyList<WebMetadataField> BuildCatalogueFields(TrvPackCatalogueMetadata? catalogue)
    {
        if (catalogue is null) return Array.Empty<WebMetadataField>();
        return new WebMetadataField[]
        {
            new("Series", catalogue.Series ?? string.Empty),
            new("Programme", catalogue.Programme ?? string.Empty),
            new("Format", catalogue.Format ?? string.Empty),
            new("Original release", catalogue.OriginalReleaseDate ?? string.Empty),
            new("Recorded", catalogue.RecordingDate ?? string.Empty),
            new("Venue", catalogue.Venue ?? string.Empty),
            new("Event", catalogue.Event ?? string.Empty),
            new("Network / platform", catalogue.Network ?? string.Empty),
            new("Catalogue number", catalogue.CatalogueNumber ?? string.Empty),
            new("Original filename", catalogue.OriginalFilename ?? string.Empty),
            new("Provenance", catalogue.Provenance ?? string.Empty),
            new("Research notes", catalogue.ResearchNotes ?? string.Empty)
        }.Where(field => !string.IsNullOrWhiteSpace(field.Value)).ToArray();
    }

    public IReadOnlyList<WebTranscriptSummary> GetTranscripts()
    {
        var result = new List<WebTranscriptSummary>();
        using var connection = _database.PlatformDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id,t.episode_id,c.name,e.air_date,e.title,t.status,t.language,t.engine_id,t.model_id,t.source,
                   t.word_count,(SELECT COUNT(*) FROM transcript_segments s WHERE s.transcript_id=t.id),
                   (SELECT COUNT(*) FROM transcript_speakers ts WHERE ts.transcript_id=t.id),
                   (SELECT COUNT(*) FROM transcript_speakers ts WHERE ts.transcript_id=t.id AND ts.assignment_state='Confirmed' AND ts.voice_person_id IS NOT NULL),
                   t.duration_ms,t.updated_at
              FROM transcripts t
              JOIN episodes e ON e.id=t.episode_id
              JOIN collections c ON c.id=e.collection_id
             ORDER BY t.updated_at DESC,t.id DESC
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new WebTranscriptSummary(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9),
                reader.GetInt32(10), reader.GetInt32(11), reader.GetInt32(12), reader.GetInt32(13), reader.GetInt64(14),
                DateTimeOffset.TryParse(reader.GetString(15), out var updated) ? updated : DateTimeOffset.MinValue));
        }
        return result;
    }

    public WebTranscriptDetails? GetTranscript(long episodeId)
    {
        var resolution = _database.ResolveCanonicalEpisode(episodeId);
        if (resolution is null) return null;
        using var connection = _database.PlatformDatabase.OpenConnection();
        using var transcript = connection.CreateCommand();
        transcript.CommandText = """
            SELECT id,status,language,word_count,duration_ms,has_speaker_diarization,updated_at
              FROM transcripts
             WHERE episode_id IN (SELECT episode_id FROM episode_canonical_map WHERE canonical_key=$key)
                OR episode_id=$episode
             ORDER BY CASE status WHEN 'Complete' THEN 0 ELSE 1 END, updated_at DESC
             LIMIT 1;
            """;
        transcript.Parameters.AddWithValue("$key", resolution.CanonicalKey);
        transcript.Parameters.AddWithValue("$episode", resolution.RepresentativeEpisodeId);
        using var reader = transcript.ExecuteReader();
        if (!reader.Read()) return null;
        var transcriptId = reader.GetInt64(0);
        var result = new WebTranscriptDetails
        {
            CanonicalBroadcastId = resolution.RepresentativeEpisodeId,
            Status = reader.GetString(1),
            Language = reader.GetString(2),
            WordCount = reader.GetInt32(3),
            DurationMs = reader.GetInt64(4),
            HasSpeakerDiarization = reader.GetInt32(5) == 1,
            UpdatedAt = DateTimeOffset.TryParse(reader.GetString(6), out var updated) ? updated : null
        };
        reader.Close();
        using var segments = connection.CreateCommand();
        segments.CommandText = """
            SELECT segment_index,start_ms,end_ms,text,speaker,speaker_key,content_kind,is_reviewed,confidence
              FROM transcript_segments
             WHERE transcript_id=$id
             ORDER BY segment_index;
            """;
        segments.Parameters.AddWithValue("$id", transcriptId);
        var rows = new List<WebTranscriptSegment>();
        using var segmentReader = segments.ExecuteReader();
        while (segmentReader.Read())
            rows.Add(new WebTranscriptSegment(segmentReader.GetInt32(0), segmentReader.GetInt64(1), segmentReader.GetInt64(2),
                segmentReader.GetString(3), segmentReader.GetString(4), segmentReader.GetString(5), segmentReader.GetString(6),
                segmentReader.GetInt32(7) == 1, segmentReader.IsDBNull(8) ? null : segmentReader.GetDouble(8)));
        return new WebTranscriptDetails
        {
            CanonicalBroadcastId = result.CanonicalBroadcastId, Status = result.Status, Language = result.Language,
            WordCount = result.WordCount, DurationMs = result.DurationMs, HasSpeakerDiarization = result.HasSpeakerDiarization,
            UpdatedAt = result.UpdatedAt, Segments = rows
        };
    }

    public WebMutationResult UpdateBroadcastMetadata(long episodeId, WebBroadcastMetadataMutation mutation)
    {
        if (GetEpisode(episodeId) is null) return new(false, "Broadcast not found.");
        var existing = _database.GetRichEpisodeMetadata(episodeId);
        _database.UpdateRichEpisodeMetadata(
            episodeId,
            mutation.Title ?? string.Empty,
            mutation.Description ?? string.Empty,
            mutation.Notes ?? string.Empty,
            mutation.Guests ?? string.Empty,
            mutation.Tags ?? string.Empty,
            artworkPath: existing.ArtworkPath,
            edition: mutation.Edition ?? string.Empty);
        _database.UpdateEpisodePeople(
            episodeId,
            mutation.Hosts ?? string.Empty,
            mutation.Guests ?? string.Empty,
            mutation.Callers ?? string.Empty,
            mutation.MentionedPeople ?? string.Empty);
        InvalidateEpisodeSnapshot();
        _events.Publish(new MetadataChangedEvent(episodeId, "lan-client-metadata-edit", DateTimeOffset.UtcNow));
        return new(true, "Broadcast metadata saved.");
    }

    public IReadOnlyList<WebMomentSummary> GetMoments()
        => _database.GetMoments()
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new WebMomentSummary(
                x.Id,
                x.EpisodeId,
                x.CollectionName,
                x.EpisodeTitle,
                DateTime.TryParse(x.AirDateDisplay, out var airDate) ? airDate : null,
                x.PositionMs,
                x.Title,
                x.Notes,
                x.CreatedAt))
            .ToArray();

    public WebMomentMutationResult AddMoment(long episodeId, WebMomentMutation mutation)
    {
        if (GetEpisode(episodeId) is null) return new(false, false, "Broadcast not found.", null);
        var title = string.IsNullOrWhiteSpace(mutation.Title) ? "Moment" : mutation.Title.Trim();
        var id = _database.AddMoment(episodeId, Math.Max(0, mutation.PositionMs), title, mutation.Notes ?? string.Empty);
        var saved = _database.GetMoments(episodeId).FirstOrDefault(x => x.Id == id);
        var moment = saved is null ? null : new WebMoment(saved.Id, saved.PositionMs, saved.Title, saved.Notes);
        InvalidateEpisodeSnapshot();
        AddChange("moment", episodeId, "created", DateTimeOffset.UtcNow);
        return new(true, false, "Moment saved.", moment);
    }

    public WebMutationResult DeleteMoment(long episodeId, long momentId)
    {
        var exists = _database.GetMoments(episodeId).Any(x => x.Id == momentId);
        if (!exists) return new(false, "Moment not found.");
        _database.DeleteMoment(momentId);
        AddChange("moment", episodeId, "deleted", DateTimeOffset.UtcNow);
        return new(true, "Moment deleted.");
    }

    public WebMutationResult UpdateMoment(long momentId, WebMomentEditMutation mutation)
    {
        var existing = _database.GetMoments().FirstOrDefault(x => x.Id == momentId);
        if (existing is null) return new(false, "Moment not found.");
        if (!_database.UpdateMoment(momentId, mutation.Title ?? string.Empty, mutation.Notes ?? string.Empty))
            return new(false, "Moment not found.");
        AddChange("moment", existing.EpisodeId, "updated", DateTimeOffset.UtcNow);
        return new(true, "Moment updated.");
    }

    public WebCanonicalMediaManifest? GetCanonicalMediaManifest(long episodeId, string? recordingKey = null)
    {
        var resolution = _database.ResolveCanonicalEpisode(episodeId);
        if (resolution is null) return null;
        var manifest = _database.GetCanonicalDownloadManifest(resolution.CanonicalKey, recordingKey);
        if (manifest is null) return null;
        return new WebCanonicalMediaManifest(
            resolution.RepresentativeEpisodeId,
            manifest.CanonicalKey,
            manifest.RecordingKey,
            manifest.Label,
            manifest.DurationMs,
            manifest.Parts.Select(x => new WebCanonicalMediaPart(
                x.PartNumber,x.PartTotal,x.LogicalStartMs,x.LogicalEndMs,
                x.MediaFileId,x.SizeBytes,x.StorageState,x.Path)).ToArray());
    }

    public WebCanonicalMediaPart? GetCanonicalMediaPart(long episodeId, long mediaFileId, string? recordingKey = null)
        => GetCanonicalMediaManifest(episodeId, recordingKey)?.Parts.FirstOrDefault(x => x.MediaFileId == mediaFileId);

    public WebResearchWorkspaceSnapshot GetResearchWorkspace()
    {
        var overview = _database.GetResearchLibraryOverview();
        var reconciliation = _database.GetResearchReconciliationOverview();
        return new WebResearchWorkspaceSnapshot(
            new WebResearchWorkspaceOverview(
                overview.TotalResearchRecords, overview.AttachedRecords, overview.ConfirmedMissing,
                overview.ProbableMissing, overview.UnknownGaps, overview.NeedsReview,
                overview.ConflictedRecords, reconciliation.NeedsDecision,
                reconciliation.AutomaticDecisions, reconciliation.ManualApprovals),
            _database.GetResearchLibraryRecords().Select(MapResearchWorkspaceRecord).ToArray(),
            _database.GetResearchImportHistory().Select(x => new WebResearchWorkspaceImport(
                x.Id, x.PackageName, x.PackageHash, x.SchemaVersion, x.AppVersion, x.ImportedAt,
                x.ImportedCount, x.MatchedCount, x.MissingCount, x.ConflictCount, x.FieldsApplied,
                x.FieldsMerged, x.FieldsPreserved, x.ManualFieldsProtected, x.ChangeCount, x.Status,
                x.RollbackDataCaptured, x.RestoredChangeCount, x.BlockedRollbackCount, x.LastRollbackAt)).ToArray(),
            _database.GetResearchSourceSummary().Select(x => new WebResearchWorkspaceSourceSummary(
                x.Publisher, x.SourceType, x.Domain, x.SourceCount, x.BroadcastCount,
                x.AverageConfidence, x.LatestAccessedAt)).ToArray(),
            DateTimeOffset.UtcNow);
    }

    public WebResearchWorkspaceRecordDetails? GetResearchWorkspaceRecord(long researchBroadcastId)
    {
        var details = _database.GetResearchLibraryRecordDetails(researchBroadcastId);
        if (details is null) return null;
        var metadata = details.Broadcast.Research?.Broadcast;
        return new WebResearchWorkspaceRecordDetails(
            MapResearchWorkspaceRecord(details.Record),
            metadata?.Station ?? string.Empty,
            metadata?.Variant ?? string.Empty,
            metadata?.Era ?? string.Empty,
            metadata?.EpisodeType ?? string.Empty,
            details.Broadcast.Research?.ArchiveNotes ?? string.Empty,
            details.Hosts, details.Guests, details.Callers, details.MentionedPeople, details.Topics,
            details.SourceDetails.Select(x => new WebResearchWorkspaceSource(
                SafeWebUrl(x.Url), x.Title, x.Publisher, x.SourceType, x.Confidence,
                x.Accessed, x.Supports, x.Notes)).ToArray(),
            details.Moments.Select(x => new WebResearchWorkspaceMoment(
                Math.Max(0, x.TimestampSeconds) * 1000L, x.Title, x.Description ?? string.Empty)).ToArray(),
            details.Conflicts.Select(x => new WebResearchWorkspaceConflict(
                x.Id, x.FieldName, x.ExistingValue, x.IncomingValue, x.Resolution, x.CreatedAt)).ToArray());
    }

    public IReadOnlyList<WebUndatedBroadcast> GetUndatedResearchBroadcasts(int? collectionId = null)
    {
        var service = new TheRadioVault.Services.Services.ResearchWorkspaceService(_database.PlatformDatabase);
        return service.GetUndatedBroadcastsAsync(collectionId).GetAwaiter().GetResult()
            .Select(x => new WebUndatedBroadcast(
                x.EpisodeId, x.CollectionId, x.ShowName, x.Title, x.DateConfidence,
                x.PreferredFilename, x.PreferredPath, x.FileCount,
                x.ProposedDate?.ToDateTime(TimeOnly.MinValue), x.ParserEvidence, x.ParserWarnings, x.UpdatedAt))
            .ToArray();
    }

    public WebAssignBroadcastDateResult AssignResearchBroadcastDate(long episodeId, DateTime broadcastDate)
    {
        var date = DateOnly.FromDateTime(broadcastDate);
        var service = new TheRadioVault.Services.Services.ResearchWorkspaceService(_database.PlatformDatabase);
        service.AssignBroadcastDateAsync(episodeId, date).GetAwaiter().GetResult();
        InvalidateEpisodeSnapshot();
        AddChange("research", episodeId, "manual-date-assigned", DateTimeOffset.UtcNow);
        return new WebAssignBroadcastDateResult(episodeId, date.ToDateTime(TimeOnly.MinValue), true);
    }

    public WebResearchCoverageShow? GetResearchCoverageByShow(string show)
    {
        var collection = _database.GetCollections().FirstOrDefault(x =>
            string.Equals(x.Name, show?.Trim(), StringComparison.OrdinalIgnoreCase));
        return collection is null ? null : GetResearchCoverage(collection.Id);
    }

    public WebResearchCoverageShow? GetResearchCoverage(int collectionId)
    {
        var service = new TheRadioVault.Services.Services.ResearchWorkspaceService(_database.PlatformDatabase);
        var coverage = service.GetCoverageAsync(collectionId).GetAwaiter().GetResult();
        if (coverage is null) return null;
        return new WebResearchCoverageShow(
            coverage.CollectionId,
            coverage.ShowName,
            coverage.FirstDate.ToDateTime(TimeOnly.MinValue),
            coverage.LastDate.ToDateTime(TimeOnly.MinValue),
            coverage.Days.Select(x => new WebResearchCoverageDay(
                x.Date.ToDateTime(TimeOnly.MinValue), x.IsWeekend, x.HasAudio, x.HasResearch,
                x.IsKnownMissing, x.BroadcastCount, x.MetadataScore, x.MissingFields,
                x.RepresentativeEpisodeId, x.ResearchId)).ToArray());
    }

    private static WebResearchWorkspaceRecord MapResearchWorkspaceRecord(ResearchLibraryBrowseRecord x)
        => new(
            x.Id, x.EpisodeId, x.BroadcastId, x.Show, x.BroadcastDate, x.Slot, x.PartNumber, x.TotalParts,
            x.Headline, x.Summary, x.ResearchState, x.ExistenceStatus, x.Confidence, x.ConfidenceReason,
            x.NeedsReview, x.ConflictCount, x.PendingDecisionCount, x.SourceCount, x.PeopleCount,
            x.TopicCount, x.MomentCount, x.UpdatedAt);

    public WebArchiveHealthSummary GetArchiveHealth()
    {
        var report = new TheRadioVault.Services.Services.ArchiveHealthService(_database.PlatformDatabase)
            .AnalyseAsync()
            .GetAwaiter()
            .GetResult();
        return new WebArchiveHealthSummary(
            report.HealthScore,
            report.CollectionScore,
            report.MetadataScore,
            report.ResearchScore,
            report.PreservationScore,
            report.ActionableIssues,
            report.ConfirmedMissingBroadcasts + report.ProbableMissingBroadcasts,
            report.ResearchNeedsReview,
            report.PendingReconciliationCandidates,
            report.LastCompletedScanAt,
            report.TotalResearchRecords > 0);
    }

    public WebPlaybackState GetPlaybackState()
    {
        try
        {
            return _playbackController.GetPlaybackState();
        }
        catch (InvalidOperationException)
        {
            var live = _livePlayback.Current;
            return live.EpisodeId.HasValue
                ? new WebPlaybackState(
                    live.EpisodeId,
                    live.Show,
                    live.Title,
                    live.PositionMs,
                    live.DurationMs,
                    live.Status,
                    live.UpdatedAt == DateTimeOffset.MinValue ? null : live.UpdatedAt.LocalDateTime,
                    live.IsPlaying,
                    live.UpdatedAt,
                    "Server",
                    1d,
                    RevisionFrom(live.UpdatedAt))
                : new WebPlaybackState(null, string.Empty, string.Empty, 0, 0, "Idle", null, false, live.UpdatedAt, "Server");
        }
    }

    public WebPlaybackState GetWebPlaybackState()
    {
        WebPlaybackState state;
        bool expiredPlaybackLease;
        lock (_playbackGate)
        {
            var now = DateTimeOffset.UtcNow;
            expiredPlaybackLease = _webPlayback.IsPlaying && now > _webPlaybackLeaseExpiresAt;
            if (expiredPlaybackLease)
            {
                _webPlayback = _webPlayback with
                {
                    IsPlaying = false,
                    Status = _webPlayback.PositionMs > 0 ? "In Progress" : "Paused",
                    UpdatedAt = now,
                    Revision = Interlocked.Increment(ref _webRevision)
                };
                // Ownership deliberately survives an expired audible-playback
                // lease. A suspended Safari tab may stop heartbeats while the
                // user still expects the phone to remain the selected output.
                // The desktop can always reclaim the session with its centre
                // transfer control.
            }
            state = _webPlayback;
        }

        // Keep the desktop's inactive player truthful if Safari is suspended
        // without delivering a pause event. Publish only after leaving the
        // ownership lock because WPF subscribers marshal back to the dispatcher.
        if (expiredPlaybackLease && state.EpisodeId.HasValue)
        {
            _events.Publish(new RemotePlaybackChangedEvent(
                state.EpisodeId,
                state.Show,
                state.Title,
                state.PositionMs,
                state.DurationMs,
                state.Speed,
                false,
                string.IsNullOrWhiteSpace(state.Device) ? "Remote device" : state.Device,
                state.UpdatedAt ?? DateTimeOffset.UtcNow));
            AddChange("player", state.EpisodeId, "remote-lease-paused", state.UpdatedAt ?? DateTimeOffset.UtcNow);
        }

        return state;
    }

    public WebPlaybackSession GetPlaybackSession()
    {
        // Never hold the provider lock while querying the desktop shell. The
        // desktop adapter may marshal to its dispatcher and publish playback
        // events back here.
        var desktop = GetPlaybackState();
        var remote = GetWebPlaybackState();

        string ownerDevice;
        string ownerClientId;
        long generation;
        WebPlaybackDevice[] remoteDevices;
        var releasedStaleOwner = false;
        lock (_playbackGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (!IsServerPlaybackOwner(_playbackOwnerDevice) &&
                !string.IsNullOrWhiteSpace(_playbackOwnerClientId))
            {
                var ownerIsFresh = _remotePlaybackDevices.TryGetValue(_playbackOwnerClientId, out var owner) &&
                    (RetainsPlaybackOwnershipWhileOffline(owner.Kind) ||
                     now - owner.LastSeenAt <= RemoteOwnerStaleAfter);
                if (!ownerIsFresh)
                {
                    _playbackOwnerDevice = "None";
                    _playbackOwnerClientId = string.Empty;
                    _playbackSessionGeneration++;
                    _webPlayback = _webPlayback with
                    {
                        IsPlaying = false,
                        ControllerClientId = string.Empty,
                        UpdatedAt = now,
                        Revision = Interlocked.Increment(ref _webRevision)
                    };
                    _webPlaybackClientId = string.Empty;
                    _playbackTransfers.Clear();
                    releasedStaleOwner = true;
                }
            }

            ownerDevice = _playbackOwnerDevice;
            ownerClientId = _playbackOwnerClientId;
            generation = _playbackSessionGeneration;
            remoteDevices = _remotePlaybackDevices.Values
                .Select(device => device with
                {
                    IsOwner = !string.IsNullOrWhiteSpace(ownerClientId) &&
                        string.Equals(device.DeviceId, ownerClientId, StringComparison.Ordinal),
                    IsOnline = now - device.LastSeenAt <= WebPlaybackLeaseDuration + TimeSpan.FromSeconds(5)
                })
                .OrderByDescending(device => device.IsOwner)
                .ThenByDescending(device => device.LastSeenAt)
                .ToArray();
        }

        if (releasedStaleOwner)
            AddChange("playback-owner", remote.EpisodeId, $"stale-owner-released:{generation}", DateTimeOffset.UtcNow);

        WebPlaybackState player;
        if (!IsServerPlaybackOwner(ownerDevice) && remote.EpisodeId.HasValue)
        {
            player = remote;
        }
        else if (IsServerPlaybackOwner(ownerDevice) && desktop.EpisodeId.HasValue)
        {
            player = desktop;
        }
        else if (remote.EpisodeId.HasValue && !string.IsNullOrWhiteSpace(remote.ControllerClientId))
        {
            ownerDevice = string.IsNullOrWhiteSpace(remote.Device) ? "Remote device" : remote.Device;
            ownerClientId = remote.ControllerClientId;
            player = remote;
        }
        else if (desktop.EpisodeId.HasValue)
        {
            ownerDevice = "Server";
            ownerClientId = string.Empty;
            player = desktop;
        }
        else
        {
            ownerDevice = "None";
            ownerClientId = string.Empty;
            player = desktop;
        }

        if (IsServerPlaybackOwner(ownerDevice))
            ownerClientId = "server";

        var serverDevice = new WebPlaybackDevice(
            "server",
            "Radio Vault server",
            "Server",
            desktop,
            desktop.UpdatedAt ?? DateTimeOffset.UtcNow,
            true,
            IsServerPlaybackOwner(ownerDevice));
        WebPlaybackTransferTicket? pendingTransfer;
        WebPlaybackCommittedTransfer? committedTransfer;
        lock (_playbackGate)
        {
            pendingTransfer = _playbackTransfers.Pending(DateTimeOffset.UtcNow);
            committedTransfer = _lastCommittedTransfer;
        }
        return new WebPlaybackSession(player, desktop, remote, ownerDevice, ownerClientId, generation)
        {
            Devices = new[] { serverDevice }.Concat(remoteDevices).ToArray(),
            PendingTransfer = pendingTransfer,
            CommittedTransfer = committedTransfer
        };
    }

    public WebPlaybackSession ClaimServerPlayback(
        long episodeId,
        long positionMs,
        long durationMs,
        double speed,
        bool isPlaying)
    {
        var episode = GetEpisode(episodeId)
            ?? throw new InvalidOperationException("Broadcast not found.");
        var now = DateTimeOffset.UtcNow;
        long generation;
        lock (_playbackGate)
        {
            var changedOwner = !IsServerPlaybackOwner(_playbackOwnerDevice);
            _playbackOwnerDevice = "Server";
            _playbackOwnerClientId = string.Empty;
            if (changedOwner) _playbackSessionGeneration++;
            generation = _playbackSessionGeneration;

            _webPlayback = _webPlayback with
            {
                IsPlaying = false,
                Status = _webPlayback.PositionMs > 0 ? "In Progress" : "Paused",
                UpdatedAt = now,
                Revision = Interlocked.Increment(ref _webRevision),
                ControllerClientId = string.Empty
            };
            _webPlaybackClientId = string.Empty;
            _webPlaybackLeaseExpiresAt = DateTimeOffset.MinValue;
            _pendingPhoneOwnerClientId = string.Empty;
            _pendingPhoneOwnerExpiresAt = DateTimeOffset.MinValue;
            _remoteControlClientId = string.Empty;
            _remoteControlLeaseExpiresAt = DateTimeOffset.MinValue;
            _playbackTransfers.Clear();
            _lastCommittedTransfer = null;
            _lastCommittedTransferTicket = null;
        }

        AddChange("playback-owner", episodeId, $"server:{generation}", now);
        _events.Publish(new PlaybackOwnershipChangedEvent(
            episodeId,
            Math.Max(0, positionMs),
            Math.Max(0, durationMs),
            Math.Clamp(speed, 0.5d, 3d),
            isPlaying,
            "Server",
            now));
        return GetPlaybackSession();
    }

    public bool ConfirmServerPlaybackOwnership()
    {
        lock (_playbackGate)
            return IsServerPlaybackOwner(_playbackOwnerDevice);
    }

    public WebPlaybackCommandResult ExecutePlaybackCommand(WebPlaybackCommand command)
    {
        if (!ValidClientId(command.ClientId))
            return new WebPlaybackCommandResult(false, false, "A valid remote-client identity is required.", GetPlaybackState());

        var requestedAt = DateTimeOffset.UtcNow;
        var commandName = (command.Command ?? string.Empty).Trim().ToLowerInvariant();
        var isPhoneOwnershipClaim = commandName is "claim-phone" or "claim-device" or "claim-device-paused" or "transfer-to-web";

        if (isPhoneOwnershipClaim)
            return new WebPlaybackCommandResult(false, true,
                "This server requires transactional playback handoff. Refresh or update the target client and try again.",
                GetPlaybackState());

        // Never hold the shared phone-playback state lock while synchronously
        // dispatching a command into WPF. Desktop pause/save notifications can
        // re-enter this provider through the event bus, so holding
        // _playbackGate here creates a classic server-thread/UI-thread lock
        // inversion. A dedicated command gate preserves command ordering without
        // blocking phone heartbeat or ownership-state callbacks.
        lock (_desktopCommandGate)
        {
            bool controlLeaseConflict;
            lock (_playbackGate)
            {
                controlLeaseConflict = !command.Force &&
                    !isPhoneOwnershipClaim &&
                    !string.IsNullOrWhiteSpace(_remoteControlClientId) &&
                    !_remoteControlClientId.Equals(command.ClientId, StringComparison.Ordinal) &&
                    requestedAt < _remoteControlLeaseExpiresAt;
            }

            if (controlLeaseConflict)
                return new WebPlaybackCommandResult(false, true, "Another Radio Vault remote client is currently controlling server playback.", GetPlaybackState());

            var result = _playbackController.ExecutePlaybackCommand(command);
            var completedAt = DateTimeOffset.UtcNow;

            lock (_playbackGate)
            {
                // A local-play gesture on the phone claims a short window in
                // which its first audio heartbeat may become authoritative. The
                // claim is internal; there is no transfer button or separate
                // handoff mode.
                if (!result.Conflict && isPhoneOwnershipClaim)
                {
                    _pendingPhoneOwnerClientId = command.ClientId;
                    _pendingPhoneOwnerExpiresAt = completedAt.Add(PendingPlaybackClaimDuration);
                    _pendingRemoteDeviceName = NormalizeDeviceName(command.DeviceName, command.DeviceKind);
                    _pendingRemoteDeviceKind = NormalizeDeviceKind(command.DeviceKind);
                    _remoteControlClientId = string.Empty;
                    _remoteControlLeaseExpiresAt = DateTimeOffset.MinValue;
                }

                if (result.Changed)
                {
                    if (!isPhoneOwnershipClaim)
                    {
                        _remoteControlClientId = command.ClientId;
                        _remoteControlLeaseExpiresAt = completedAt.Add(RemoteControlLeaseDuration);
                    }

                    if (commandName is "play" or "play-episode" or "transfer-to-desktop" or "next")
                    {
                        _webPlayback = _webPlayback with
                        {
                            IsPlaying = false,
                            Status = _webPlayback.PositionMs > 0 ? "In Progress" : "Paused",
                            UpdatedAt = completedAt,
                            Revision = Interlocked.Increment(ref _webRevision),
                            ControllerClientId = string.Empty
                        };
                        _webPlaybackClientId = string.Empty;
                        _pendingPhoneOwnerClientId = string.Empty;
                        _pendingPhoneOwnerExpiresAt = DateTimeOffset.MinValue;
                        _pendingRemoteDeviceName = "Phone";
                        _pendingRemoteDeviceKind = "Phone";
                    }
                }
            }

            return result;
        }
    }

    public WebClientPlaybackResult UpdateWebPlayback(WebClientPlaybackUpdate update)
    {
        if (!ValidClientId(update.ClientId))
            return new WebClientPlaybackResult(false, false, "A valid remote-client identity is required.", GetWebPlaybackState());
        var episode = GetEpisode(update.EpisodeId);
        if (episode is null)
            return new WebClientPlaybackResult(false, false, "Broadcast not found.", GetWebPlaybackState());

        // Capture server playback outside the ownership lock; the adapter may
        // marshal to the native UI and must never be called while the gate is held.
        var serverPlaybackEpisodeId = update.Force ? GetPlaybackState().EpisodeId : null;
        WebPlaybackState savedPlayback;
        var now = DateTimeOffset.UtcNow;
        var deviceName = NormalizeDeviceName(update.DeviceName, update.DeviceKind);
        var deviceKind = NormalizeDeviceKind(update.DeviceKind);
        var claimedOwnership = false;
        long claimedGeneration = 0;
        lock (_playbackGate)
        {
            var ownsPlayback = !IsServerPlaybackOwner(_playbackOwnerDevice) &&
                _playbackOwnerClientId.Equals(update.ClientId, StringComparison.Ordinal);
            if (!ownsPlayback)
            {
                if (!update.Force)
                    return new WebClientPlaybackResult(false, true,
                        "Listening state was ignored because another device owns playback.", _webPlayback);

                var anotherOutputIsActive = IsServerPlaybackOwner(_playbackOwnerDevice)
                    ? serverPlaybackEpisodeId.HasValue
                    : !string.IsNullOrWhiteSpace(_playbackOwnerClientId) && _webPlayback.EpisodeId.HasValue;
                if (anotherOutputIsActive)
                    return new WebClientPlaybackResult(false, true,
                        "Another device owns playback. Move playback transactionally before reporting decoder state.",
                        _webPlayback);
                if (update.ExpectedGeneration > 0 && update.ExpectedGeneration != _playbackSessionGeneration)
                    return new WebClientPlaybackResult(false, true,
                        "Listening state was ignored because the playback generation changed.", _webPlayback);

                _playbackOwnerDevice = deviceName;
                _playbackOwnerClientId = update.ClientId;
                _playbackSessionGeneration++;
                claimedGeneration = _playbackSessionGeneration;
                claimedOwnership = true;
                _playbackTransfers.Clear();
                _lastCommittedTransfer = null;
                _lastCommittedTransferTicket = null;
                _remoteControlClientId = string.Empty;
                _remoteControlLeaseExpiresAt = DateTimeOffset.MinValue;
            }
            else if (update.ExpectedGeneration > 0 && update.ExpectedGeneration != _playbackSessionGeneration)
            {
                return new WebClientPlaybackResult(false, true,
                    "Listening state was ignored because the playback generation changed.", _webPlayback);
            }

            var duration = Math.Max(0, episode.DurationMs > 0 ? episode.DurationMs : update.DurationMs);
            var position = Math.Clamp(update.PositionMs, 0, duration > 0 ? duration : long.MaxValue);

            // A heartbeat is a disposable live-state report, not permission to
            // rewind durable listening history. Reject delayed startup zeroes and
            // stale decoder callbacks unless the committed owner explicitly sought.
            if (!update.ExplicitSeek && _webPlayback.EpisodeId == episode.Id)
            {
                var projected = Math.Max(0, _webPlayback.PositionMs);
                if (_webPlayback.IsPlaying && _webPlayback.UpdatedAt.HasValue)
                {
                    var elapsed = (now - _webPlayback.UpdatedAt.Value).TotalMilliseconds;
                    if (elapsed > 0 && elapsed <= 15_000)
                        projected += (long)Math.Round(elapsed * Math.Clamp(_webPlayback.Speed, 0.5d, 3d));
                }
                if (duration > 0) projected = Math.Clamp(projected, 0, duration);
                if (projected >= 10_000 && position < projected - 3_000)
                    return new WebClientPlaybackResult(false, true,
                        "A stale decoder position was rejected; the shared playhead was preserved.", _webPlayback);
            }

            var completed = update.Completed || (duration > 0 && position >= Math.Max(0, duration - 5_000));
            var status = completed ? "Completed" : position > 0 ? "In Progress" : "Unplayed";
            savedPlayback = new WebPlaybackState(
                episode.Id, episode.Show, episode.Title, position, duration, status, DateTime.Now,
                update.IsPlaying, now, deviceName, Math.Clamp(update.Speed, 0.5d, 3d),
                Interlocked.Increment(ref _webRevision), update.ClientId);
            _webPlayback = savedPlayback;
            // Paused playback still belongs to the selected web output. Presence
            // heartbeats keep this lease fresh until the tab actually disappears.
            _webPlaybackClientId = update.ClientId;
            _webPlaybackLeaseExpiresAt = now.Add(WebPlaybackLeaseDuration);
            _remotePlaybackDevices[update.ClientId] = new WebPlaybackDevice(
                update.ClientId, deviceName, deviceKind, savedPlayback, now, true, true);
        }

        if (claimedOwnership)
        {
            AddChange("playback-owner", episode.Id, $"{update.ClientId}:{claimedGeneration}", now);
            _events.Publish(new PlaybackOwnershipChangedEvent(
                episode.Id, savedPlayback.PositionMs, savedPlayback.DurationMs, savedPlayback.Speed,
                savedPlayback.IsPlaying, savedPlayback.Device, now));
        }

        // Deliberately no database mutation here. Durable progress is committed by
        // the established canonical progress endpoint every five seconds and at
        // pause, seek, stop, completion and shutdown boundaries.
        _events.Publish(new RemotePlaybackChangedEvent(episode.Id, episode.Show, episode.Title,
            savedPlayback.PositionMs, savedPlayback.DurationMs, savedPlayback.Speed,
            savedPlayback.IsPlaying, savedPlayback.Device, now));
        return new WebClientPlaybackResult(true, false,
            $"{savedPlayback.Device} live playback state updated.", savedPlayback);
    }

    public WebOfflineProgressResult SyncOfflineProgress(WebOfflineProgressUpdate update)
    {
        if (!ValidClientId(update.ClientId))
            return new WebOfflineProgressResult(false, "A valid remote-client identity is required.");

        var episode = GetEpisode(update.EpisodeId);
        if (episode is null)
            return new WebOfflineProgressResult(false, "Broadcast not found.");

        // AllowRewind identifies a live desktop-client progress write rather than an
        // offline-download reconciliation. It may only be accepted from the current
        // playback owner, otherwise a stale laptop could overwrite the device that
        // has just claimed the shared session.
        if (update.AllowRewind)
        {
            lock (_playbackGate)
            {
                if (IsServerPlaybackOwner(_playbackOwnerDevice) ||
                    !_playbackOwnerClientId.Equals(update.ClientId, StringComparison.Ordinal) ||
                    (update.ExpectedGeneration > 0 && update.ExpectedGeneration != _playbackSessionGeneration))
                    return new WebOfflineProgressResult(false,
                        "Listening progress was ignored because another device owns playback or the playback generation changed.",
                        episode, Conflict: true);
            }
        }

        var duration = Math.Max(0, episode.DurationMs > 0 ? episode.DurationMs : update.DurationMs);
        var position = Math.Clamp(update.PositionMs, 0, duration > 0 ? duration : long.MaxValue);

        // Capability-generation 15 clients identify the exact ownership generation
        // that produced a durable progress write. Once the target has committed,
        // ordinary timer/pause writes are monotonic within a small decoder tolerance;
        // only an explicit seek from that exact generation may intentionally move
        // established progress backwards.
        var generationBoundOwnerWrite = update.AllowRewind && update.ExpectedGeneration > 0;
        if (generationBoundOwnerWrite && !update.ExplicitSeek &&
            episode.PositionMs >= 10_000 && position < episode.PositionMs - 3_000)
        {
            return new WebOfflineProgressResult(false,
                "A stale durable position was rejected; the existing listening progress was preserved.",
                episode, Conflict: true);
        }
        var completed = update.Completed || (duration > 0 && position >= Math.Max(0, duration - 5_000));
        if (completed) position = Math.Max(position, episode.PositionMs);

        // Generation-less records can remain in Safari IndexedDB or an older
        // desktop cache after an upgrade. They may still advance progress, but
        // they can never rewind it. This makes legacy retries safe without
        // discarding forward listening from a temporarily disconnected client.
        var mayResetPosition = generationBoundOwnerWrite && update.ExplicitSeek;
        if (!completed && !mayResetPosition && position <= episode.PositionMs)
            return new WebOfflineProgressResult(false,
                "Radio Vault already has equal or newer progress.", episode);

        var speed = Math.Clamp(update.Speed, 0.5d, 3d);
        _database.SavePlaybackState(episode.Id, position, duration, completed, speed,
            incrementPlayCount: update.IncrementPlayCount, allowPositionReset: mayResetPosition);
        var now = DateTimeOffset.UtcNow;
        _events.Publish(new PlaybackChangedEvent(episode.Id, position, duration, false, now));
        if (completed) _events.Publish(new ListeningStatusChangedEvent(new[] { episode.Id }, "Completed", now));
        AddChange("offline-progress", episode.Id, completed ? "offline-completed" : "offline-progress-synced", now);
        return new WebOfflineProgressResult(true, completed ? "Offline listening completion synchronised." : "Offline listening progress synchronised.", GetEpisode(episode.Id));
    }

    public IReadOnlyList<WebQueueItem> GetQueue()
    {
        var episodes = GetEpisodeSnapshot().ById;
        return _database.GetQueue()
            .Where(x => episodes.ContainsKey(x.EpisodeId))
            .Select(x => new WebQueueItem(x.QueueId, x.Position, episodes[x.EpisodeId]))
            .ToArray();
    }

    public WebQueueMutationResult AddToQueue(long episodeId, bool playNext)
    {
        if (GetEpisode(episodeId) is null) return new WebQueueMutationResult(false, "Broadcast not found.", GetQueue());
        var existing = GetQueue().FirstOrDefault(x => x.Episode.Id == episodeId);
        if (existing is not null)
            return new WebQueueMutationResult(true, "Broadcast is already in the queue.", GetQueue());
        _database.AddToQueue(episodeId, playNext);
        _events.Publish(new QueueChangedEvent(new[] { episodeId }, playNext ? "play-next" : "added", DateTimeOffset.UtcNow));
        return new WebQueueMutationResult(true, playNext ? "Added to play next." : "Added to queue.", GetQueue());
    }

    public WebQueueMutationResult RemoveFromQueue(long queueId)
    {
        var item = GetQueue().FirstOrDefault(x => x.QueueId == queueId);
        if (item is null) return new WebQueueMutationResult(false, "Queue item not found.", GetQueue());
        _database.RemoveQueueItem(queueId);
        _events.Publish(new QueueChangedEvent(new[] { item.Episode.Id }, "removed", DateTimeOffset.UtcNow));
        return new WebQueueMutationResult(true, "Removed from queue.", GetQueue());
    }

    public WebQueueMutationResult ClearQueue()
    {
        var ids = GetQueue().Select(x => x.Episode.Id).ToArray();
        if (ids.Length == 0) return new WebQueueMutationResult(false, "Queue is already empty.", Array.Empty<WebQueueItem>());
        _database.ClearQueue();
        _events.Publish(new QueueChangedEvent(ids, "cleared", DateTimeOffset.UtcNow));
        return new WebQueueMutationResult(true, "Queue cleared.", Array.Empty<WebQueueItem>());
    }

    public WebQueueMutationResult MoveQueueItem(long queueId, int direction)
    {
        if (direction is not (-1 or 1)) return new WebQueueMutationResult(false, "Direction must be -1 or 1.", GetQueue());
        var item = GetQueue().FirstOrDefault(x => x.QueueId == queueId);
        if (item is null) return new WebQueueMutationResult(false, "Queue item not found.", GetQueue());
        _database.MoveQueueItem(queueId, direction);
        _events.Publish(new QueueChangedEvent(new[] { item.Episode.Id }, "reordered", DateTimeOffset.UtcNow));
        return new WebQueueMutationResult(true, "Queue reordered.", GetQueue());
    }

    public IReadOnlyList<WebChangeEvent> GetChanges(long afterSequence, int limit)
        => _changes
            .Where(x => x.Sequence > Math.Max(0, afterSequence))
            .OrderBy(x => x.Sequence)
            .Take(Math.Clamp(limit, 1, 200))
            .ToArray();

    public WebChangeFeedSnapshot GetChangeFeed(long afterSequence, int limit)
    {
        var current = Math.Max(0, Interlocked.Read(ref _changeSequence));
        var retained = _changes.OrderBy(x => x.Sequence).ToArray();
        var earliest = retained.Length == 0 ? current + 1 : retained[0].Sequence;
        var changes = retained
            .Where(x => x.Sequence > Math.Max(0, afterSequence))
            .Take(Math.Clamp(limit, 1, 200))
            .ToArray();
        return new WebChangeFeedSnapshot(current, earliest, changes);
    }

    public IReadOnlyList<WebJobSummary> GetJobs()
        => _jobs.GetJobs().Select(x => new WebJobSummary(
            x.JobId,
            x.Name,
            x.Category.ToString(),
            x.State.ToString(),
            x.Percent,
            x.Message ?? string.Empty,
            x.CanCancel,
            x.QueuedAt,
            x.StartedAt,
            x.FinishedAt)).ToArray();

    public WebJobActionResult CancelJob(Guid jobId)
    {
        var job = _jobs.GetJob(jobId);
        if (job is null) return new WebJobActionResult(false, "Background task not found.");
        if (!job.CanCancel) return new WebJobActionResult(false, "This background task can no longer be cancelled.");
        return _jobs.Cancel(jobId)
            ? new WebJobActionResult(true, "Cancellation requested.")
            : new WebJobActionResult(false, "Cancellation could not be requested.");
    }

    public WebMutationResult SetFavourite(long episodeId, bool favourite)
    {
        if (GetEpisode(episodeId) is null) return new WebMutationResult(false, "Broadcast not found.");
        _database.SetFavourite(episodeId, favourite);
        InvalidateEpisodeSnapshot();
        _events.Publish(new FavouritesChangedEvent(new[] { episodeId }, favourite, DateTimeOffset.UtcNow));
        return new WebMutationResult(true, favourite ? "Added to favourites." : "Removed from favourites.", GetEpisode(episodeId));
    }

    public WebMutationResult SetPlayed(long episodeId, bool played)
    {
        if (GetEpisode(episodeId) is null) return new WebMutationResult(false, "Broadcast not found.");
        _database.MarkCompleted(episodeId, played);
        InvalidateEpisodeSnapshot();
        var status = played ? "Completed" : "Unplayed";
        _events.Publish(new ListeningStatusChangedEvent(new[] { episodeId }, status, DateTimeOffset.UtcNow));
        return new WebMutationResult(true, played ? "Marked listened." : "Marked unlistened.", GetEpisode(episodeId));
    }

    private void HandlePlaybackOwnershipChanged(PlaybackOwnershipChangedEvent change)
    {
        if (!IsServerPlaybackOwner(change.Device))
            return;

        long generation;
        lock (_playbackGate)
        {
            var changedOwner = !IsServerPlaybackOwner(_playbackOwnerDevice);
            _playbackOwnerDevice = "Server";
            _playbackOwnerClientId = string.Empty;
            if (changedOwner) _playbackSessionGeneration++;
            generation = _playbackSessionGeneration;

            _webPlayback = _webPlayback with
            {
                IsPlaying = false,
                Status = _webPlayback.PositionMs > 0 ? "In Progress" : "Paused",
                UpdatedAt = change.OccurredAt,
                Revision = Interlocked.Increment(ref _webRevision),
                ControllerClientId = string.Empty
            };
            _webPlaybackClientId = string.Empty;
            _webPlaybackLeaseExpiresAt = DateTimeOffset.MinValue;
            _pendingPhoneOwnerClientId = string.Empty;
            _pendingPhoneOwnerExpiresAt = DateTimeOffset.MinValue;
            _pendingRemoteDeviceName = "Phone";
            _pendingRemoteDeviceKind = "Phone";

            // A server-target transactional commit publishes the normal ownership
            // event after the provider has already installed its generation and
            // source-stop receipt. Preserve that receipt until the remote source has
            // physically stopped and acknowledged; ordinary local playback claims
            // still invalidate any older transaction state.
            var committedTransfer = _lastCommittedTransfer;
            var committedTicket = _lastCommittedTransferTicket;
            var transactionalCommitEcho = committedTransfer is not null &&
                committedTicket is not null &&
                string.Equals(committedTransfer.TargetClientId, "server", StringComparison.OrdinalIgnoreCase) &&
                committedTransfer.Generation == _playbackSessionGeneration &&
                committedTicket.TargetEpisodeId == change.EpisodeId;
            if (!transactionalCommitEcho)
            {
                _playbackTransfers.Clear();
                _lastCommittedTransfer = null;
                _lastCommittedTransferTicket = null;
            }
        }

        AddChange("playback-owner", change.EpisodeId, $"desktop:{generation}", change.OccurredAt);
    }

    private void AddChange(string kind, long? episodeId, string reason, DateTimeOffset occurredAt)
    {
        var sequence = Interlocked.Increment(ref _changeSequence);
        _changes.Enqueue(new WebChangeEvent(sequence, kind, episodeId, reason, occurredAt));
        while (_changes.Count > ChangeHistoryLimit && _changes.TryDequeue(out _)) { }
    }

    private EpisodeSnapshot GetEpisodeSnapshot()
    {
        lock (_episodeSnapshotGate)
        {
            if (_episodeSnapshot is not null) return _episodeSnapshot;
            var mapped = _database.GetEpisodes().Select(Map).ToArray();
            var episodes = mapped
                .GroupBy(x => x.Id)
                .Select(group => group
                    .OrderByDescending(x => x.LastPlayedAt ?? DateTime.MinValue)
                    .ThenByDescending(x => x.PositionMs)
                    .ThenByDescending(x => x.DateAdded)
                    .First())
                .ToArray();
            _episodeSnapshot = new EpisodeSnapshot(
                episodes,
                episodes.ToDictionary(x => x.Id));
            return _episodeSnapshot;
        }
    }

    private void InvalidateEpisodeSnapshot()
    {
        lock (_episodeSnapshotGate) _episodeSnapshot = null;
    }

    private static long RevisionFrom(DateTimeOffset updatedAt)
        => updatedAt == DateTimeOffset.MinValue ? 0 : updatedAt.UtcDateTime.Ticks;

    private static bool IsServerPlaybackOwner(string? device)
        => string.Equals(device, "Server", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(device, "Desktop", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDeviceKind(string? value)
    {
        var kind = (value ?? string.Empty).Trim();
        if (kind.Length == 0) return "Phone";
        return kind.Length > 32 ? kind[..32] : kind;
    }

    private static bool RetainsPlaybackOwnershipWhileOffline(string? kind)
        => string.Equals(kind, "iOSClient", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, "DesktopClient", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDeviceName(string? name, string? kind)
    {
        var value = (name ?? string.Empty).Trim();
        if (value.Length == 0) value = NormalizeDeviceKind(kind) switch
        {
            "DesktopClient" => "Radio Vault desktop client",
            "Browser" => "Web browser",
            _ => "Phone"
        };
        return value.Length > 80 ? value[..80] : value;
    }

    private static bool ValidPlaybackEndpointId(string value)
        => string.Equals(value, "server", StringComparison.OrdinalIgnoreCase) || ValidClientId(value);

    private static bool ValidClientId(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Length is >= 8 and <= 128 && value.All(x => char.IsLetterOrDigit(x) || x is '-' or '_');

    private static WebPlaybackState IdleWebPlayback()
        => new(null, string.Empty, string.Empty, 0, 0, "Idle", null, false, null, "Phone");

    private sealed record EpisodeSnapshot(
        IReadOnlyList<WebEpisode> Episodes,
        IReadOnlyDictionary<long, WebEpisode> ById);

    private static string SafeWebUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return string.Empty;
        return uri.Scheme is "http" or "https" ? uri.AbsoluteUri : string.Empty;
    }

    private static WebEpisode Map(EpisodeListItem episode)
        => new(
            episode.Id,
            episode.CollectionName,
            episode.SmartTitle,
            episode.AirDate,
            episode.Summary,
            string.Join(", ", new[] { episode.Hosts, episode.Guests, episode.Callers, episode.MentionedPeople }
                .Where(x => !string.IsNullOrWhiteSpace(x))),
            episode.Tags,
            episode.DurationMs,
            episode.PositionMs,
            episode.Status,
            episode.Favourite,
            episode.LastPlayedAt,
            episode.DateAdded,
            episode.Path,
            episode.ArtworkPath ?? string.Empty);

    public void Dispose()
    {
        DisposeResearchImportSessions();
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _libraryScanGate.Dispose();
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;
        public InlineProgress(Action<T> report) => _report = report;
        public void Report(T value) => _report(value);
    }
}
