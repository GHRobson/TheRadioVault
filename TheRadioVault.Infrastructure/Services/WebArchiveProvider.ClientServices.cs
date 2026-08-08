using System.Text.Json;
using TheRadioVault.Services.Models;
using TheRadioVault.Services.Services;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;
using TheRadioVault.Transcription.Services;

namespace TheRadioVault.Services;

internal sealed partial class WebArchiveProvider
{
    private static readonly JsonSerializerOptions ClientJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<object?> ExecuteClientResearchAsync(
        string operation,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        var service = new ResearchWorkspaceService(_database.PlatformDatabase);
        return operation switch
        {
            "overview" => await service.GetOverviewAsync(cancellationToken).ConfigureAwait(false),
            "collections" => await service.GetCollectionsAsync(cancellationToken).ConfigureAwait(false),
            "browse" => await service.BrowseAsync(Read<ResearchBrowseQuery>(payload), cancellationToken).ConfigureAwait(false),
            "details" => await service.GetDetailsAsync(Read<IdRequest>(payload).Id, cancellationToken).ConfigureAwait(false),
            "save-metadata" => await SaveResearchMetadataAsync(service, Read<ResearchMetadataUpdate>(payload), cancellationToken).ConfigureAwait(false),
            "set-review" => await SetResearchReviewAsync(service, Read<ReviewRequest>(payload), cancellationToken).ConfigureAwait(false),
            "source-diagnostics" => await service.GetSourceDiagnosticsAsync(cancellationToken).ConfigureAwait(false),
            "import-history" => await service.GetImportHistoryAsync(Read<LimitRequest>(payload).Limit, cancellationToken).ConfigureAwait(false),
            "undated" => await service.GetUndatedBroadcastsAsync(Read<CollectionRequest>(payload).CollectionId, cancellationToken).ConfigureAwait(false),
            "date-reviews" => await GetDateReviewsAsync(service, Read<DateReviewsRequest>(payload), cancellationToken).ConfigureAwait(false),
            "resolve-date-review" => await ResolveDateReviewAsync(service, Read<ResolveDateReviewRequest>(payload), cancellationToken).ConfigureAwait(false),
            "assign-date" => await AssignDateAsync(service, Read<AssignDateRequest>(payload), cancellationToken).ConfigureAwait(false),
            "coverage" => await service.GetCoverageAsync(Read<CollectionRequest>(payload).CollectionId
                ?? throw new ArgumentException("A collection is required."), cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unknown Research client operation '{operation}'.")
        };
    }

    public async Task<object?> ExecuteClientTranscriptAsync(
        string operation,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        var repository = _transcripts;
        return operation switch
        {
            "get" => await repository.GetForEpisodeAsync(Read<EpisodeRequest>(payload).EpisodeId, cancellationToken).ConfigureAwait(false),
            "summary" => await repository.GetSummaryForEpisodeAsync(Read<EpisodeRequest>(payload).EpisodeId, cancellationToken).ConfigureAwait(false),
            "summaries" => await repository.GetSummariesAsync(cancellationToken).ConfigureAwait(false),
            "identity" => await repository.GetEpisodeIdentityAsync(Read<EpisodeRequest>(payload).EpisodeId, cancellationToken).ConfigureAwait(false),
            "context" => await repository.GetTranscriptionContextAsync(Read<EpisodeRequest>(payload).EpisodeId, cancellationToken).ConfigureAwait(false),
            "media-path" => await repository.GetPreferredMediaPathAsync(Read<EpisodeRequest>(payload).EpisodeId, cancellationToken).ConfigureAwait(false),
            "duration" => await repository.GetEpisodeDurationMsAsync(Read<EpisodeRequest>(payload).EpisodeId, cancellationToken).ConfigureAwait(false),
            "save" => await SaveTranscriptAsync(repository, Read<TranscriptDocument>(payload), cancellationToken).ConfigureAwait(false),
            "delete" => await DeleteTranscriptAsync(repository, Read<EpisodeRequest>(payload).EpisodeId, cancellationToken).ConfigureAwait(false),
            "create-job" => await CreateTranscriptJobAsync(repository, Read<TranscriptionJobRecord>(payload), cancellationToken).ConfigureAwait(false),
            "update-job" => await UpdateTranscriptJobAsync(repository, Read<TranscriptionJobRecord>(payload), cancellationToken).ConfigureAwait(false),
            "job" => await repository.GetJobAsync(Read<JobRequest>(payload).JobId, cancellationToken).ConfigureAwait(false),
            "jobs" => await repository.GetJobsAsync(Read<LimitRequest>(payload).Limit, cancellationToken).ConfigureAwait(false),
            "record-import" => await RecordTranscriptImportAsync(repository, Read<TranscriptImportRequest>(payload), cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unknown transcript client operation '{operation}'.")
        };
    }

    public async Task<object?> ExecuteClientSpeakerAsync(
        string operation,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        var repository = _speakers;
        return operation switch
        {
            "clusters" => await repository.GetClustersForEpisodeAsync(Read<EpisodeRequest>(payload).EpisodeId, cancellationToken).ConfigureAwait(false),
            "episode-people" => await repository.GetEpisodePeopleAsync(Read<EpisodeRequest>(payload).EpisodeId, cancellationToken).ConfigureAwait(false),
            "get-or-create-person" => await repository.GetOrCreateVoicePersonAsync(Read<PersonRequest>(payload).PersonName, cancellationToken).ConfigureAwait(false),
            "assign" => await AssignSpeakerAsync(repository, Read<AssignSpeakerRequest>(payload), cancellationToken).ConfigureAwait(false),
            "clear" => await ClearSpeakerAsync(repository, Read<SpeakerRequest>(payload), cancellationToken).ConfigureAwait(false),
            "profiles" => await repository.GetVoiceProfilesAsync(cancellationToken).ConfigureAwait(false),
            "profile" => await repository.GetVoiceProfileAsync(Read<VoicePersonRequest>(payload).VoicePersonId, cancellationToken).ConfigureAwait(false),
            "pending-samples" => await repository.GetPendingVoiceSamplesAsync(Read<LimitRequest>(payload).Limit, cancellationToken).ConfigureAwait(false),
            "save-embedding" => await SaveEmbeddingAsync(repository, Read<SaveEmbeddingRequest>(payload), cancellationToken).ConfigureAwait(false),
            "sample-failed" => await MarkSampleFailedAsync(repository, Read<SampleFailedRequest>(payload), cancellationToken).ConfigureAwait(false),
            "match" => await MatchSpeakerAsync(repository, Read<MatchSpeakerRequest>(payload), cancellationToken).ConfigureAwait(false),
            "suggestions" => await repository.GetSuggestionsAsync(Read<TranscriptSpeakerRequest>(payload).TranscriptSpeakerId, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unknown speaker client operation '{operation}'.")
        };
    }

    public async Task<object?> ExecuteClientTranscriptionAsync(
        string operation,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        var repository = _transcripts;
        switch (operation)
        {
            case "status":
                return RequireTranscription().GetStatus();
            case "settings":
                return RequireTranscription().GetSettings();
            case "save-settings":
                return RequireTranscription().SaveSettings(Read<WhisperCppEngineSettings>(payload));
            case "reload-settings":
                return RequireTranscription().ReloadSettings();
            case "install-recommended":
                return await InstallRecommendedTranscriptionAsync(RequireTranscription(), Read<InstallTranscriptionRequest>(payload), cancellationToken).ConfigureAwait(false);
            case "voice-process":
                return await RequireTranscription().VoiceLearning.ProcessPendingAsync(Read<LimitRequest>(payload).Limit, cancellationToken: cancellationToken).ConfigureAwait(false);
            case "queue":
            {
                var request = Read<QueueTranscriptionRequest>(payload);
                var jobId = await RequireTranscription().Coordinator.QueueAsync(request.EpisodeId, request.Options, cancellationToken).ConfigureAwait(false);
                AddChange("transcription", request.EpisodeId, $"queued:{jobId:D}", DateTimeOffset.UtcNow);
                return jobId;
            }
            case "retry":
            {
                var request = Read<JobRequest>(payload);
                return await RequireTranscription().Coordinator.RetryAsync(request.JobId, cancellationToken).ConfigureAwait(false);
            }
            case "pause":
            {
                var request = Read<JobRequest>(payload);
                return await RequireTranscription().Coordinator.PauseAsync(request.JobId, cancellationToken).ConfigureAwait(false);
            }
            case "resume":
            {
                var request = Read<JobRequest>(payload);
                return await RequireTranscription().Coordinator.ResumeAsync(request.JobId, cancellationToken).ConfigureAwait(false);
            }
            case "cancel":
                return RequireTranscription().Coordinator.Cancel(Read<JobRequest>(payload).JobId);
            case "authorize-queue":
            {
                var request = Read<EpisodeRequest>(payload);
                var path = await repository.GetPreferredMediaPathAsync(request.EpisodeId, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    throw new FileNotFoundException("The server cannot find the preferred audio for this broadcast.");
                AddChange("transcription-control", request.EpisodeId, "queue-authorized", DateTimeOffset.UtcNow);
                return true;
            }
            case "authorize-action":
            {
                var request = Read<TranscriptionActionRequest>(payload);
                var job = await repository.GetJobAsync(request.JobId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The transcription job no longer exists.");
                var allowed = request.Action.ToLowerInvariant() switch
                {
                    "retry" => job.CanRetry,
                    "pause" => job.CanPause,
                    "resume" => job.CanResume,
                    "cancel" => job.CanCancel,
                    _ => false
                };
                if (!allowed) throw new InvalidOperationException($"The transcription job cannot {request.Action} in its current state.");
                AddChange("transcription-control", job.EpisodeId, $"{request.Action}-authorized:{job.JobId:D}", DateTimeOffset.UtcNow);
                return true;
            }
            case "jobs":
                return await repository.GetJobsAsync(Read<LimitRequest>(payload).Limit, cancellationToken).ConfigureAwait(false);
            case "batch-create":
                return await RequireTranscription().BatchCoordinator.CreateAndStartAsync(Read<TranscriptionBatchCreateRequest>(payload), cancellationToken).ConfigureAwait(false);
            case "batches":
                return await RequireTranscription().BatchCoordinator.GetBatchesAsync(Read<LimitRequest>(payload).Limit, cancellationToken).ConfigureAwait(false);
            case "batch-items":
                return await RequireTranscription().BatchCoordinator.GetItemsAsync(Read<BatchRequest>(payload).BatchId, cancellationToken).ConfigureAwait(false);
            case "batch-pause":
                return await PauseBatchAsync(RequireTranscription().BatchCoordinator, Read<BatchRequest>(payload).BatchId, cancellationToken).ConfigureAwait(false);
            case "batch-resume":
                return await ResumeBatchAsync(RequireTranscription().BatchCoordinator, Read<BatchRequest>(payload).BatchId, cancellationToken).ConfigureAwait(false);
            case "batch-cancel":
                return await CancelBatchAsync(RequireTranscription().BatchCoordinator, Read<BatchRequest>(payload).BatchId, cancellationToken).ConfigureAwait(false);
            case "batch-retry":
                return await RetryBatchAsync(RequireTranscription().BatchCoordinator, Read<BatchRequest>(payload).BatchId, cancellationToken).ConfigureAwait(false);
            case "batch-move":
            {
                var request = Read<BatchMoveRequest>(payload);
                return await RequireTranscription().BatchCoordinator.MoveItemAsync(request.BatchId, request.ItemId, request.Direction, cancellationToken).ConfigureAwait(false);
            }
            default:
                throw new InvalidOperationException($"Unknown transcription client operation '{operation}'.");
        }
    }

    private ServerTranscriptionRuntime RequireTranscription()
        => _transcription ?? throw new InvalidOperationException("The dedicated server transcription worker is not running.");

    private static async Task<ServerTranscriptionStatus> InstallRecommendedTranscriptionAsync(
        ServerTranscriptionRuntime runtime,
        InstallTranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var model = WhisperModelCatalog.Items.FirstOrDefault(x => string.Equals(x.Id, request.ModelId, StringComparison.OrdinalIgnoreCase))
            ?? WhisperModelCatalog.Items.First(x => x.Id == "base.en");
        var worker = await runtime.Downloads.InstallLatestWindowsWorkerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var modelPath = await runtime.Downloads.DownloadModelAsync(model, cancellationToken: cancellationToken).ConfigureAwait(false);
        var vadPath = await runtime.Downloads.DownloadVadModelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var diarization = await runtime.Downloads.DownloadDiarizationModelsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var settings = runtime.GetSettings();
        settings.ExecutablePath = worker.ExecutablePath;
        settings.ModelPath = modelPath;
        settings.VadModelPath = vadPath;
        settings.DiarizationSegmentationModelPath = diarization.SegmentationModelPath;
        settings.DiarizationEmbeddingModelPath = diarization.EmbeddingModelPath;
        settings.UseVoiceActivityDetection = true;
        settings.EnableMultiSpeakerDiarization = true;
        runtime.SaveSettings(settings);
        return runtime.GetStatus();
    }

    private static async Task<object?> PauseBatchAsync(ITranscriptionBatchCoordinator coordinator, Guid batchId, CancellationToken token)
    { await coordinator.PauseAsync(batchId, token).ConfigureAwait(false); return true; }
    private static async Task<object?> ResumeBatchAsync(ITranscriptionBatchCoordinator coordinator, Guid batchId, CancellationToken token)
    { await coordinator.ResumeAsync(batchId, token).ConfigureAwait(false); return true; }
    private static async Task<object?> CancelBatchAsync(ITranscriptionBatchCoordinator coordinator, Guid batchId, CancellationToken token)
    { await coordinator.CancelAsync(batchId, token).ConfigureAwait(false); return true; }
    private static async Task<object?> RetryBatchAsync(ITranscriptionBatchCoordinator coordinator, Guid batchId, CancellationToken token)
    { await coordinator.RetryFailedAsync(batchId, token).ConfigureAwait(false); return true; }

    private static T Read<T>(JsonElement payload)
        => payload.Deserialize<T>(ClientJsonOptions)
           ?? throw new ArgumentException($"A valid {typeof(T).Name} request is required.");

    private static async Task<object?> SaveResearchMetadataAsync(ResearchWorkspaceService service, ResearchMetadataUpdate update, CancellationToken token)
    { await service.SaveMetadataAsync(update, token).ConfigureAwait(false); return true; }
    private static async Task<object?> SetResearchReviewAsync(ResearchWorkspaceService service, ReviewRequest request, CancellationToken token)
    { await service.SetNeedsReviewAsync(request.ResearchId, request.NeedsReview, token).ConfigureAwait(false); return true; }
    private static Task<IReadOnlyList<CatalogueDateReviewItem>> GetDateReviewsAsync(ResearchWorkspaceService service, DateReviewsRequest request, CancellationToken token)
        => service.GetCatalogueDateReviewsAsync(request.CollectionId, request.IncludeResolved, token);
    private static async Task<object?> ResolveDateReviewAsync(ResearchWorkspaceService service, ResolveDateReviewRequest request, CancellationToken token)
    { await service.ResolveCatalogueDateReviewAsync(request.ResearchId, request.Action, request.SelectedDate, token).ConfigureAwait(false); return true; }
    private static async Task<object?> AssignDateAsync(ResearchWorkspaceService service, AssignDateRequest request, CancellationToken token)
    { await service.AssignBroadcastDateAsync(request.EpisodeId, request.AirDate, token).ConfigureAwait(false); return true; }
    private static async Task<object?> SaveTranscriptAsync(ITranscriptRepository repository, TranscriptDocument document, CancellationToken token)
        => await repository.SaveAsync(document, token).ConfigureAwait(false);
    private static async Task<object?> DeleteTranscriptAsync(ITranscriptRepository repository, long episodeId, CancellationToken token)
    { await repository.DeleteAsync(episodeId, token).ConfigureAwait(false); return true; }
    private static async Task<object?> CreateTranscriptJobAsync(ITranscriptRepository repository, TranscriptionJobRecord job, CancellationToken token)
    { await repository.CreateJobAsync(job, token).ConfigureAwait(false); return true; }
    private static async Task<object?> UpdateTranscriptJobAsync(ITranscriptRepository repository, TranscriptionJobRecord job, CancellationToken token)
    { await repository.UpdateJobAsync(job, token).ConfigureAwait(false); return true; }
    private static async Task<object?> RecordTranscriptImportAsync(ITranscriptRepository repository, TranscriptImportRequest request, CancellationToken token)
    { await repository.RecordImportAsync(request.EpisodeId, request.PackageId, request.SourcePath, request.Checksum, request.ReplacedRevision, token).ConfigureAwait(false); return true; }
    private static async Task<object?> AssignSpeakerAsync(ISpeakerIdentityRepository repository, AssignSpeakerRequest request, CancellationToken token)
        => await repository.AssignClusterAsync(request.EpisodeId, request.SpeakerKey, request.PersonName, request.TrainVoice, token).ConfigureAwait(false);
    private static async Task<object?> ClearSpeakerAsync(ISpeakerIdentityRepository repository, SpeakerRequest request, CancellationToken token)
    { await repository.ClearAssignmentAsync(request.EpisodeId, request.SpeakerKey, token).ConfigureAwait(false); return true; }
    private static async Task<object?> SaveEmbeddingAsync(ISpeakerIdentityRepository repository, SaveEmbeddingRequest request, CancellationToken token)
    { await repository.SaveVoiceEmbeddingAsync(request.SampleId, request.Result, token).ConfigureAwait(false); return true; }
    private static async Task<object?> MarkSampleFailedAsync(ISpeakerIdentityRepository repository, SampleFailedRequest request, CancellationToken token)
    { await repository.MarkVoiceSampleFailedAsync(request.SampleId, request.Error, token).ConfigureAwait(false); return true; }
    private static async Task<object?> MatchSpeakerAsync(ISpeakerIdentityRepository repository, MatchSpeakerRequest request, CancellationToken token)
        => await repository.MatchClusterAsync(request.EpisodeId, request.SpeakerKey, request.Embedding, request.Limit, token).ConfigureAwait(false);

    private sealed record IdRequest(long Id);
    private sealed record EpisodeRequest(long EpisodeId);
    private sealed record JobRequest(Guid JobId);
    private sealed record LimitRequest(int Limit = 100);
    private sealed record CollectionRequest(int? CollectionId);
    private sealed record ReviewRequest(long ResearchId, bool NeedsReview);
    private sealed record DateReviewsRequest(int? CollectionId, bool IncludeResolved);
    private sealed record ResolveDateReviewRequest(long ResearchId, CatalogueDateReviewAction Action, DateOnly? SelectedDate);
    private sealed record AssignDateRequest(long EpisodeId, DateOnly AirDate);
    private sealed record TranscriptImportRequest(long EpisodeId, string PackageId, string SourcePath, string Checksum, int ReplacedRevision);
    private sealed record PersonRequest(string PersonName);
    private sealed record SpeakerRequest(long EpisodeId, string SpeakerKey);
    private sealed record AssignSpeakerRequest(long EpisodeId, string SpeakerKey, string PersonName, bool TrainVoice);
    private sealed record VoicePersonRequest(long VoicePersonId);
    private sealed record SaveEmbeddingRequest(long SampleId, VoiceEmbeddingResult Result);
    private sealed record SampleFailedRequest(long SampleId, string Error);
    private sealed record MatchSpeakerRequest(long EpisodeId, string SpeakerKey, VoiceEmbeddingResult Embedding, int Limit);
    private sealed record TranscriptSpeakerRequest(long TranscriptSpeakerId);
    private sealed record TranscriptionActionRequest(Guid JobId, string Action);
    private sealed record QueueTranscriptionRequest(long EpisodeId, TranscriptionJobOptions? Options);
    private sealed record BatchRequest(Guid BatchId);
    private sealed record BatchMoveRequest(Guid BatchId, long ItemId, int Direction);
    private sealed record InstallTranscriptionRequest(string ModelId = "base.en");
}
