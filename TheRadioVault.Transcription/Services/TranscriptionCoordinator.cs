using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using TheRadioVault.Services.Jobs;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Services;

public sealed class TranscriptionCoordinator : ITranscriptionCoordinator
{
    private readonly ITranscriptRepository _repository;
    private readonly IBackgroundJobQueue _backgroundJobs;
    private readonly ITranscriptionAudioPreparer? _audioPreparer;
    private readonly ConcurrentDictionary<Guid, Guid> _backgroundJobIds = new();

    public TranscriptionCoordinator(
        ITranscriptRepository repository,
        ITranscriptionEngine engine,
        IBackgroundJobQueue backgroundJobs,
        IMultiSpeakerDiarizationEngine? diarizationEngine = null,
        ITranscriptionAudioPreparer? audioPreparer = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _backgroundJobs = backgroundJobs ?? throw new ArgumentNullException(nameof(backgroundJobs));
        DiarizationEngine = diarizationEngine;
        _audioPreparer = audioPreparer;
    }

    public ITranscriptionEngine Engine { get; }
    public IMultiSpeakerDiarizationEngine? DiarizationEngine { get; }

    public async Task<Guid> QueueAsync(
        long episodeId,
        TranscriptionJobOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (episodeId <= 0) throw new ArgumentOutOfRangeException(nameof(episodeId));
        if (!Engine.IsAvailable) throw new InvalidOperationException(Engine.AvailabilityMessage);
        options ??= new TranscriptionJobOptions();
        ValidateOptions(options);
        if (options.EnableSpeakerDiarization && (DiarizationEngine is null || !DiarizationEngine.IsAvailable))
            throw new InvalidOperationException(DiarizationEngine?.AvailabilityMessage ?? "Multi-speaker diarization is not installed.");

        var existing = await _repository.GetForEpisodeAsync(episodeId, cancellationToken).ConfigureAwait(false);
        if (existing is not null && !options.ReplaceExistingTranscript)
            throw new InvalidOperationException("This broadcast already has a transcript. Choose Replace existing transcript to run it again.");
        var activeJobs = await _repository.GetJobsAsync(1000, cancellationToken).ConfigureAwait(false);
        if (activeJobs.Any(x => x.EpisodeId == episodeId && x.State is TranscriptionJobState.Queued or TranscriptionJobState.Running))
            throw new InvalidOperationException("A transcription job for this broadcast is already queued or running.");

        var audioPath = await _repository.GetPreferredMediaPathAsync(episodeId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            throw new FileNotFoundException("The preferred audio file is not available locally.", audioPath);

        var expectedDurationMs = await _repository.GetEpisodeDurationMsAsync(episodeId, cancellationToken).ConfigureAwait(false);
        var modelId = string.IsNullOrWhiteSpace(options.ModelId)
            ? (Engine is WhisperCppTranscriptionEngine whisper ? whisper.GetSettings().ModelId : "")
            : options.ModelId;
        var jobId = Guid.NewGuid();
        var queued = new TranscriptionJobRecord
        {
            JobId = jobId,
            EpisodeId = episodeId,
            State = TranscriptionJobState.Queued,
            EngineId = Engine.Id,
            ModelId = modelId,
            ProgressPercent = 0,
            Message = options.IsPartial ? $"Queued · {options.RangeDisplay}" : "Queued",
            RequestedAt = DateTimeOffset.UtcNow,
            Language = options.Language,
            StartMs = options.StartMs,
            DurationMs = options.DurationMs,
            EnableSpeakerDiarization = options.EnableSpeakerDiarization,
            UseVoiceActivityDetection = options.UseVoiceActivityDetection,
            ReplaceExistingTranscript = options.ReplaceExistingTranscript
        };
        await _repository.CreateJobAsync(queued, cancellationToken).ConfigureAwait(false);
        return await EnqueueExistingRecordAsync(queued, audioPath, expectedDurationMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> RetryAsync(Guid transcriptionJobId, CancellationToken cancellationToken = default)
    {
        var previous = await _repository.GetJobAsync(transcriptionJobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected transcription job no longer exists.");
        if (!previous.CanRetry)
            throw new InvalidOperationException("Only failed, cancelled or interrupted transcription jobs can be retried.");

        return await QueueAsync(previous.EpisodeId, new TranscriptionJobOptions(
            previous.Language,
            previous.ModelId,
            previous.StartMs,
            previous.DurationMs,
            previous.EnableSpeakerDiarization,
            previous.UseVoiceActivityDetection,
            ReplaceExistingTranscript: true), cancellationToken).ConfigureAwait(false);
    }

    public bool Cancel(Guid transcriptionJobId)
    {
        if (Engine is IPausableTranscriptionEngine pausable) pausable.Resume(transcriptionJobId);
        return _backgroundJobIds.TryGetValue(transcriptionJobId, out var backgroundJobId)
               && _backgroundJobs.Cancel(backgroundJobId);
    }

    public async Task<bool> PauseAsync(Guid transcriptionJobId, CancellationToken cancellationToken = default)
    {
        var job = await _repository.GetJobAsync(transcriptionJobId, cancellationToken).ConfigureAwait(false);
        if (job?.CanPause != true || Engine is not IPausableTranscriptionEngine pausable || !pausable.Pause(transcriptionJobId))
            return false;
        await _repository.UpdateJobAsync(CloneJob(job, message: "Paused", isPaused: true), cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> ResumeAsync(Guid transcriptionJobId, CancellationToken cancellationToken = default)
    {
        var job = await _repository.GetJobAsync(transcriptionJobId, cancellationToken).ConfigureAwait(false);
        if (job?.CanResume != true || Engine is not IPausableTranscriptionEngine pausable || !pausable.Resume(transcriptionJobId))
            return false;
        await _repository.UpdateJobAsync(CloneJob(job, message: "Transcription resumed", isPaused: false), cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task<IReadOnlyList<TranscriptionJobRecord>> GetJobsAsync(int limit = 100, CancellationToken cancellationToken = default)
        => _repository.GetJobsAsync(limit, cancellationToken);

    private async Task<Guid> EnqueueExistingRecordAsync(
        TranscriptionJobRecord queued,
        string audioPath,
        long expectedDurationMs,
        CancellationToken cancellationToken)
    {
        var startGate = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var backgroundJobId = _backgroundJobs.Enqueue(new BackgroundJobRequest(
                $"Transcribe broadcast {queued.EpisodeId}",
                BackgroundJobCategory.Transcription,
                async (context, token) =>
                {
                    var assignedBackgroundJobId = await startGate.Task.WaitAsync(token).ConfigureAwait(false);
                    await RunJobAsync(queued, audioPath, expectedDurationMs, assignedBackgroundJobId, context, token).ConfigureAwait(false);
                }));
            _backgroundJobIds[queued.JobId] = backgroundJobId;
            startGate.SetResult(backgroundJobId);
            return queued.JobId;
        }
        catch (Exception ex)
        {
            startGate.TrySetException(ex);
            await _repository.UpdateJobAsync(CloneJob(
                queued,
                state: TranscriptionJobState.Failed,
                message: "Could not queue transcription",
                error: ex.ToString(),
                finishedAt: DateTimeOffset.UtcNow), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task RunJobAsync(
        TranscriptionJobRecord queued,
        string audioPath,
        long expectedDurationMs,
        Guid backgroundJobId,
        BackgroundJobContext context,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var running = CloneJob(
            queued,
            state: TranscriptionJobState.Running,
            progress: 0,
            message: "Preparing server transcription",
            startedAt: startedAt,
            backgroundJobId: backgroundJobId);

        try
        {
            await _repository.UpdateJobAsync(running, cancellationToken).ConfigureAwait(false);
            var progressGate = new object();
            var lastPersistedAt = DateTimeOffset.MinValue;
            double? lastPersistedPercent = null;
            void HandleProgress(TranscriptionEngineProgress update)
            {
                if (update.Percent.HasValue) context.Report(update.Percent.Value, update.Message);
                else context.ReportIndeterminate(update.Message);

                var now = DateTimeOffset.UtcNow;
                var shouldPersist = false;
                lock (progressGate)
                {
                    var percentMoved = update.Percent.HasValue
                        && (!lastPersistedPercent.HasValue || Math.Abs(update.Percent.Value - lastPersistedPercent.Value) >= 1);
                    if (percentMoved || now - lastPersistedAt >= TimeSpan.FromMilliseconds(750))
                    {
                        lastPersistedAt = now;
                        lastPersistedPercent = update.Percent;
                        shouldPersist = true;
                    }
                }
                if (!shouldPersist) return;

                var persisted = CloneJob(
                    running,
                    progress: update.Percent,
                    message: update.Message,
                    startedAt: startedAt);
                try
                {
                    _repository.UpdateJobAsync(persisted, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch
                {
                    // Progress persistence must not abort the native worker.
                }
            }

            var useDiarization = queued.EnableSpeakerDiarization && DiarizationEngine is not null;
            var workingDirectory = Path.Combine(Path.GetTempPath(), "RadioVault", "Transcription", queued.JobId.ToString("N"));
            var preparationProgress = new InlineProgress<TranscriptionEngineProgress>(update => HandleProgress(
                update.Percent.HasValue ? update with { Percent = update.Percent.Value * 0.08 } : update));
            var preparedAudio = _audioPreparer is null
                ? new PreparedTranscriptionAudio(audioPath)
                : await _audioPreparer.PrepareAsync(
                    audioPath,
                    queued.StartMs,
                    queued.DurationMs,
                    workingDirectory,
                    preparationProgress,
                    cancellationToken).ConfigureAwait(false);
            var transcriptionProgress = new InlineProgress<TranscriptionEngineProgress>(update => HandleProgress(
                update.Percent.HasValue
                    ? update with { Percent = 8 + (update.Percent.Value * (useDiarization ? 0.62 : 0.92)) }
                    : update));

            var contextData = await _repository.GetTranscriptionContextAsync(queued.EpisodeId, cancellationToken).ConfigureAwait(false);
            var useContext = Engine is not WhisperCppTranscriptionEngine whisperEngine || whisperEngine.GetSettings().UseArchiveContext;
            var useVoiceActivityDetection = TranscriptionSafety.ShouldUseVoiceActivityDetection(
                queued.UseVoiceActivityDetection,
                queued.DurationMs);
            var request = new TranscriptionRequest(
                queued.EpisodeId,
                preparedAudio.AudioPath,
                queued.Language,
                queued.ModelId,
                expectedDurationMs,
                workingDirectory,
                queued.StartMs,
                queued.DurationMs,
                false,
                useVoiceActivityDetection,
                useContext ? contextData.Prompt : "",
                preparedAudio.InputOffsetMs,
                queued.JobId);
            var result = await Engine.TranscribeAsync(request, transcriptionProgress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<TranscriptSegment> finalSegments = result.Segments;
            var metadataJson = result.MetadataJson;
            var speakerCount = 0;
            var speakerAnalysisRejected = false;
            if (useDiarization)
            {
                var diarizationProgress = new InlineProgress<TranscriptionEngineProgress>(update => HandleProgress(
                    update.Percent.HasValue
                        ? update with { Percent = 70 + (update.Percent.Value * 0.29) }
                        : update));
                var settings = Engine is WhisperCppTranscriptionEngine configured ? configured.GetSettings() : null;
                var diarization = await DiarizationEngine!.DiarizeAsync(new SpeakerDiarizationRequest(
                    audioPath,
                    queued.StartMs,
                    queued.DurationMs,
                    settings?.DiarizationClusteringThreshold ?? 0.9), diarizationProgress, cancellationToken).ConfigureAwait(false);
                var analysedDurationMs = queued.DurationMs ?? expectedDurationMs;
                speakerAnalysisRejected = !TranscriptionSafety.IsSpeakerCountPlausible(
                    diarization.SpeakerCount,
                    analysedDurationMs);
                if (!speakerAnalysisRejected)
                {
                    finalSegments = TranscriptSpeakerMerger.Apply(result.Segments, diarization.Turns);
                    speakerCount = diarization.SpeakerCount;
                }
                var metadata = JsonNode.Parse(string.IsNullOrWhiteSpace(result.MetadataJson) ? "{}" : result.MetadataJson) as JsonObject ?? new JsonObject();
                metadata["diarization"] = new JsonObject
                {
                    ["engine"] = diarization.EngineId,
                    ["engineVersion"] = diarization.EngineVersion,
                    ["speakerCount"] = diarization.SpeakerCount,
                    ["segmentationModel"] = diarization.SegmentationModel,
                    ["embeddingModel"] = diarization.EmbeddingModel,
                    ["clusteringThreshold"] = settings?.DiarizationClusteringThreshold ?? 0.9,
                    ["accepted"] = !speakerAnalysisRejected,
                    ["maximumPlausibleSpeakers"] = TranscriptionSafety.MaximumPlausibleSpeakerCount(analysedDurationMs)
                };
                metadataJson = metadata.ToJsonString();
            }

            var isPartial = queued.StartMs > 0 || queued.DurationMs.HasValue;
            var transcript = new TranscriptDocument
            {
                EpisodeId = queued.EpisodeId,
                Status = isPartial ? TranscriptStatus.Draft : TranscriptStatus.Complete,
                Language = result.Language,
                EngineId = result.EngineId,
                EngineVersion = result.EngineVersion,
                ModelId = result.ModelId,
                // A transcript produced by this Radio Vault installation is a
                // local transcript even when the worker lives in the dedicated
                // server process. The persisted schema intentionally uses the
                // portable source vocabulary local/import/manual/shared.
                Source = "local",
                FullText = result.FullText,
                DurationMs = result.DurationMs,
                HasWordTimings = result.HasWordTimings,
                HasSpeakerDiarization = speakerCount > 0,
                CompletedAt = isPartial ? null : DateTimeOffset.UtcNow,
                MetadataJson = metadataJson,
                Segments = finalSegments
            };
            await _repository.SaveAsync(transcript, cancellationToken).ConfigureAwait(false);
            await _repository.UpdateJobAsync(CloneJob(
                running,
                state: TranscriptionJobState.Completed,
                progress: 100,
                message: speakerCount > 0
                    ? $"Transcript saved · {speakerCount} speaker{(speakerCount == 1 ? "" : "s")}"
                    : speakerAnalysisRejected
                        ? "Transcript saved · implausible speaker analysis was discarded"
                    : transcript.Status == TranscriptStatus.Draft ? "Sample transcript saved" : "Transcript saved",
                startedAt: startedAt,
                finishedAt: DateTimeOffset.UtcNow), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _repository.UpdateJobAsync(CloneJob(
                running,
                state: TranscriptionJobState.Cancelled,
                message: "Cancelled",
                startedAt: startedAt,
                finishedAt: DateTimeOffset.UtcNow), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await _repository.UpdateJobAsync(CloneJob(
                running,
                state: TranscriptionJobState.Failed,
                message: $"Failed · {FailureSummary(ex)}",
                error: ex.ToString(),
                startedAt: startedAt,
                finishedAt: DateTimeOffset.UtcNow), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            TryDeleteDirectory(Path.Combine(Path.GetTempPath(), "RadioVault", "Transcription", queued.JobId.ToString("N")));
            _backgroundJobIds.TryRemove(queued.JobId, out _);
        }
    }

    private static void ValidateOptions(TranscriptionJobOptions options)
    {
        if (options.StartMs < 0) throw new ArgumentOutOfRangeException(nameof(options), "The transcription start must not be negative.");
        if (options.DurationMs is <= 0) throw new ArgumentOutOfRangeException(nameof(options), "A selected range must be longer than zero.");
    }

    private static TranscriptionJobRecord CloneJob(
        TranscriptionJobRecord source,
        TranscriptionJobState? state = null,
        double? progress = null,
        string? message = null,
        string? error = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? finishedAt = null,
        Guid? backgroundJobId = null,
        bool? isPaused = null)
        => new()
        {
            JobId = source.JobId,
            EpisodeId = source.EpisodeId,
            State = state ?? source.State,
            EngineId = source.EngineId,
            ModelId = source.ModelId,
            ProgressPercent = progress ?? source.ProgressPercent,
            Message = message ?? source.Message,
            Error = error ?? source.Error,
            RequestedAt = source.RequestedAt,
            StartedAt = startedAt ?? source.StartedAt,
            FinishedAt = finishedAt ?? source.FinishedAt,
            BackgroundJobId = backgroundJobId ?? source.BackgroundJobId,
            Language = source.Language,
            StartMs = source.StartMs,
            DurationMs = source.DurationMs,
            EnableSpeakerDiarization = source.EnableSpeakerDiarization,
            UseVoiceActivityDetection = source.UseVoiceActivityDetection,
            ReplaceExistingTranscript = source.ReplaceExistingTranscript,
            IsPaused = isPaused ?? source.IsPaused
        };

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public InlineProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    private static string FailureSummary(Exception exception)
    {
        var message = exception.Message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? "Transcription failed";
        return message.Length <= 150 ? message : message[..147] + "…";
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}
