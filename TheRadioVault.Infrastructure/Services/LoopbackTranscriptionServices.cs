using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;
using TheRadioVault.Transcription.Services;
using TheRadioVault.Web.Contracts;

namespace TheRadioVault.Services;

public sealed class LoopbackServerTranscriptionAdministrationService : IServerTranscriptionAdministrationService
{
    private readonly LoopbackServerClient _connection;

    public LoopbackServerTranscriptionAdministrationService(LoopbackServerClient connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<ServerTranscriptionAdministrationSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var status = await CallAsync<ServerTranscriptionAdministrationStatus>("status", new { }, cancellationToken).ConfigureAwait(false);
        var settings = await CallAsync<WhisperCppEngineSettings>("settings", new { }, cancellationToken).ConfigureAwait(false);
        return new ServerTranscriptionAdministrationSnapshot(status, settings);
    }

    public async Task<ServerTranscriptionAdministrationSnapshot> SaveAsync(
        WhisperCppEngineSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var saved = await CallAsync<WhisperCppEngineSettings>("save-settings", settings, cancellationToken).ConfigureAwait(false);
        var status = await CallAsync<ServerTranscriptionAdministrationStatus>("status", new { }, cancellationToken).ConfigureAwait(false);
        return new ServerTranscriptionAdministrationSnapshot(status, saved);
    }

    public async Task<ServerTranscriptionAdministrationSnapshot> InstallRecommendedAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var status = await CallAsync<ServerTranscriptionAdministrationStatus>(
            "install-recommended",
            new { modelId = string.IsNullOrWhiteSpace(modelId) ? "base.en" : modelId.Trim() },
            cancellationToken).ConfigureAwait(false);
        var settings = await CallAsync<WhisperCppEngineSettings>("settings", new { }, cancellationToken).ConfigureAwait(false);
        return new ServerTranscriptionAdministrationSnapshot(status, settings);
    }

    private async Task<T> CallAsync<T>(string operation, object body, CancellationToken cancellationToken)
    {
        var envelope = await _connection.SendJsonAsync<ValueEnvelope<T>>(
            HttpMethod.Post,
            WebApiRoutes.ClientTranscriptionOperation(operation),
            body,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return envelope.Value;
    }

    private sealed record ValueEnvelope<T>(T Value);
}

public sealed class LoopbackTranscriptRepository : ITranscriptRepository
{
    private readonly LoopbackServerClient _connection;

    public LoopbackTranscriptRepository(LoopbackServerClient connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public Task<TranscriptDocument?> GetForEpisodeAsync(long episodeId, CancellationToken cancellationToken = default)
        => CallAsync<TranscriptDocument?>("get", new { episodeId }, cancellationToken);
    public Task<TranscriptSummary?> GetSummaryForEpisodeAsync(long episodeId, CancellationToken cancellationToken = default)
        => CallAsync<TranscriptSummary?>("summary", new { episodeId }, cancellationToken);
    public Task<IReadOnlyList<TranscriptSummary>> GetSummariesAsync(CancellationToken cancellationToken = default)
        => CallListAsync<TranscriptSummary>("summaries", new { }, cancellationToken);
    public Task<TranscriptEpisodeIdentity?> GetEpisodeIdentityAsync(long episodeId, CancellationToken cancellationToken = default)
        => CallAsync<TranscriptEpisodeIdentity?>("identity", new { episodeId }, cancellationToken);
    public Task<TranscriptionContext> GetTranscriptionContextAsync(long episodeId, CancellationToken cancellationToken = default)
        => CallAsync<TranscriptionContext>("context", new { episodeId }, cancellationToken);
    public Task<string?> GetPreferredMediaPathAsync(long episodeId, CancellationToken cancellationToken = default)
        => CallAsync<string?>("media-path", new { episodeId }, cancellationToken);
    public Task<long> GetEpisodeDurationMsAsync(long episodeId, CancellationToken cancellationToken = default)
        => CallAsync<long>("duration", new { episodeId }, cancellationToken);
    public Task<TranscriptDocument> SaveAsync(TranscriptDocument document, CancellationToken cancellationToken = default)
        => CallAsync<TranscriptDocument>("save", document, cancellationToken);
    public async Task DeleteAsync(long episodeId, CancellationToken cancellationToken = default)
        => _ = await CallAsync<bool>("delete", new { episodeId }, cancellationToken).ConfigureAwait(false);
    public async Task CreateJobAsync(TranscriptionJobRecord job, CancellationToken cancellationToken = default)
        => _ = await CallAsync<bool>("create-job", job, cancellationToken).ConfigureAwait(false);
    public async Task UpdateJobAsync(TranscriptionJobRecord job, CancellationToken cancellationToken = default)
        => _ = await CallAsync<bool>("update-job", job, cancellationToken).ConfigureAwait(false);
    public Task<TranscriptionJobRecord?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        => CallAsync<TranscriptionJobRecord?>("job", new { jobId }, cancellationToken);
    public Task<IReadOnlyList<TranscriptionJobRecord>> GetJobsAsync(int limit = 100, CancellationToken cancellationToken = default)
        => CallListAsync<TranscriptionJobRecord>("jobs", new { limit }, cancellationToken);
    public async Task RecordImportAsync(long episodeId, string packageId, string sourcePath, string checksum, int replacedRevision, CancellationToken cancellationToken = default)
        => _ = await CallAsync<bool>("record-import", new { episodeId, packageId, sourcePath, checksum, replacedRevision }, cancellationToken).ConfigureAwait(false);

    private async Task<T> CallAsync<T>(string operation, object body, CancellationToken cancellationToken)
    {
        var envelope = await _connection.SendJsonAsync<ValueEnvelope<T>>(
            HttpMethod.Post, WebApiRoutes.ClientTranscriptOperation(operation), body,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return envelope.Value;
    }

    private async Task<IReadOnlyList<T>> CallListAsync<T>(string operation, object body, CancellationToken cancellationToken)
        => await CallAsync<List<T>>(operation, body, cancellationToken).ConfigureAwait(false);

    private sealed record ValueEnvelope<T>(T Value);
}

public sealed class LoopbackSpeakerIdentityRepository : ISpeakerIdentityRepository
{
    private readonly LoopbackServerClient _connection;

    public LoopbackSpeakerIdentityRepository(LoopbackServerClient connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public Task<IReadOnlyList<TranscriptSpeakerCluster>> GetClustersForEpisodeAsync(long episodeId, CancellationToken cancellationToken = default)
        => CallListAsync<TranscriptSpeakerCluster>("clusters", new { episodeId }, cancellationToken);
    public Task<IReadOnlyList<TranscriptPersonCandidate>> GetEpisodePeopleAsync(long episodeId, CancellationToken cancellationToken = default)
        => CallListAsync<TranscriptPersonCandidate>("episode-people", new { episodeId }, cancellationToken);
    public Task<VoicePersonRecord> GetOrCreateVoicePersonAsync(string personName, CancellationToken cancellationToken = default)
        => CallAsync<VoicePersonRecord>("get-or-create-person", new { personName }, cancellationToken);
    public Task<SpeakerAssignmentResult> AssignClusterAsync(long episodeId, string speakerKey, string personName, bool trainVoice, CancellationToken cancellationToken = default)
        => CallAsync<SpeakerAssignmentResult>("assign", new { episodeId, speakerKey, personName, trainVoice }, cancellationToken);
    public async Task ClearAssignmentAsync(long episodeId, string speakerKey, CancellationToken cancellationToken = default)
        => _ = await CallAsync<bool>("clear", new { episodeId, speakerKey }, cancellationToken).ConfigureAwait(false);
    public Task<IReadOnlyList<VoiceProfileSummary>> GetVoiceProfilesAsync(CancellationToken cancellationToken = default)
        => CallListAsync<VoiceProfileSummary>("profiles", new { }, cancellationToken);
    public Task<VoiceProfileSummary?> GetVoiceProfileAsync(long voicePersonId, CancellationToken cancellationToken = default)
        => CallAsync<VoiceProfileSummary?>("profile", new { voicePersonId }, cancellationToken);
    public Task<IReadOnlyList<VoiceSampleRecord>> GetPendingVoiceSamplesAsync(int limit = 100, CancellationToken cancellationToken = default)
        => CallListAsync<VoiceSampleRecord>("pending-samples", new { limit }, cancellationToken);
    public async Task SaveVoiceEmbeddingAsync(long sampleId, VoiceEmbeddingResult result, CancellationToken cancellationToken = default)
        => _ = await CallAsync<bool>("save-embedding", new { sampleId, result }, cancellationToken).ConfigureAwait(false);
    public async Task MarkVoiceSampleFailedAsync(long sampleId, string error, CancellationToken cancellationToken = default)
        => _ = await CallAsync<bool>("sample-failed", new { sampleId, error }, cancellationToken).ConfigureAwait(false);
    public Task<IReadOnlyList<SpeakerMatchSuggestion>> MatchClusterAsync(long episodeId, string speakerKey, VoiceEmbeddingResult embedding, int limit = 5, CancellationToken cancellationToken = default)
        => CallListAsync<SpeakerMatchSuggestion>("match", new { episodeId, speakerKey, embedding, limit }, cancellationToken);
    public Task<IReadOnlyList<SpeakerMatchSuggestion>> GetSuggestionsAsync(long transcriptSpeakerId, CancellationToken cancellationToken = default)
        => CallListAsync<SpeakerMatchSuggestion>("suggestions", new { transcriptSpeakerId }, cancellationToken);

    private async Task<T> CallAsync<T>(string operation, object body, CancellationToken cancellationToken)
    {
        var envelope = await _connection.SendJsonAsync<ValueEnvelope<T>>(
            HttpMethod.Post, WebApiRoutes.ClientSpeakerOperation(operation), body,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return envelope.Value;
    }

    private async Task<IReadOnlyList<T>> CallListAsync<T>(string operation, object body, CancellationToken cancellationToken)
        => await CallAsync<List<T>>(operation, body, cancellationToken).ConfigureAwait(false);

    private sealed record ValueEnvelope<T>(T Value);
}

public sealed class LoopbackTranscriptionCoordinator : ITranscriptionCoordinator
{
    private readonly LoopbackServerClient _connection;

    public LoopbackTranscriptionCoordinator(LoopbackServerClient connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public ITranscriptionEngine Engine { get; } = new ServerOwnedTranscriptionEngine();
    public IMultiSpeakerDiarizationEngine? DiarizationEngine => null;

    public async Task<Guid> QueueAsync(long episodeId, TranscriptionJobOptions? options = null, CancellationToken cancellationToken = default)
    {
        await CallAsync<WhisperCppEngineSettings>("reload-settings", new { }, cancellationToken).ConfigureAwait(false);
        return await CallAsync<Guid>("queue", new { episodeId, options }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> RetryAsync(Guid transcriptionJobId, CancellationToken cancellationToken = default)
    {
        return await CallAsync<Guid>("retry", new { jobId = transcriptionJobId }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> PauseAsync(Guid transcriptionJobId, CancellationToken cancellationToken = default)
    {
        return await CallAsync<bool>("pause", new { jobId = transcriptionJobId }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ResumeAsync(Guid transcriptionJobId, CancellationToken cancellationToken = default)
    {
        return await CallAsync<bool>("resume", new { jobId = transcriptionJobId }, cancellationToken).ConfigureAwait(false);
    }

    public bool Cancel(Guid transcriptionJobId)
    {
        try
        {
            return CallAsync<bool>("cancel", new { jobId = transcriptionJobId }, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<TranscriptionJobRecord>> GetJobsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        var envelope = await _connection.SendJsonAsync<ValueEnvelope<List<TranscriptionJobRecord>>>(
            HttpMethod.Post,
            WebApiRoutes.ClientTranscriptionOperation("jobs"),
            new { limit },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return envelope.Value;
    }

    private async Task<T> CallAsync<T>(string operation, object body, CancellationToken cancellationToken)
    {
        var envelope = await _connection.SendJsonAsync<ValueEnvelope<T>>(
            HttpMethod.Post,
            WebApiRoutes.ClientTranscriptionOperation(operation),
            body,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return envelope.Value;
    }

    private sealed record ValueEnvelope<T>(T Value);
}

public sealed class LoopbackTranscriptionBatchCoordinator : ITranscriptionBatchCoordinator
{
    private readonly LoopbackServerClient _connection;
    private bool _disposed;

    public LoopbackTranscriptionBatchCoordinator(LoopbackServerClient connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public Task<TranscriptionBatchRecord> CreateAndStartAsync(TranscriptionBatchCreateRequest request, CancellationToken cancellationToken = default)
        => CallAsync<TranscriptionBatchRecord>("batch-create", request, cancellationToken);
    public async Task<IReadOnlyList<TranscriptionBatchRecord>> GetBatchesAsync(int limit = 50, CancellationToken cancellationToken = default)
        => await CallAsync<List<TranscriptionBatchRecord>>("batches", new { limit }, cancellationToken).ConfigureAwait(false);
    public async Task<IReadOnlyList<TranscriptionBatchItemRecord>> GetItemsAsync(Guid batchId, CancellationToken cancellationToken = default)
        => await CallAsync<List<TranscriptionBatchItemRecord>>("batch-items", new { batchId }, cancellationToken).ConfigureAwait(false);
    public async Task PauseAsync(Guid batchId, CancellationToken cancellationToken = default)
        => _ = await CallAsync<bool>("batch-pause", new { batchId }, cancellationToken).ConfigureAwait(false);
    public async Task ResumeAsync(Guid batchId, CancellationToken cancellationToken = default)
        => _ = await CallAsync<bool>("batch-resume", new { batchId }, cancellationToken).ConfigureAwait(false);
    public async Task CancelAsync(Guid batchId, CancellationToken cancellationToken = default)
        => _ = await CallAsync<bool>("batch-cancel", new { batchId }, cancellationToken).ConfigureAwait(false);
    public async Task RetryFailedAsync(Guid batchId, CancellationToken cancellationToken = default)
        => _ = await CallAsync<bool>("batch-retry", new { batchId }, cancellationToken).ConfigureAwait(false);
    public Task<bool> MoveItemAsync(Guid batchId, long itemId, int direction, CancellationToken cancellationToken = default)
        => CallAsync<bool>("batch-move", new { batchId, itemId, direction }, cancellationToken);

    private async Task<T> CallAsync<T>(string operation, object body, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var envelope = await _connection.SendJsonAsync<ValueEnvelope<T>>(
            HttpMethod.Post,
            WebApiRoutes.ClientTranscriptionOperation(operation),
            body,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return envelope.Value;
    }

    public void Dispose() => _disposed = true;
    private sealed record ValueEnvelope<T>(T Value);
}

public sealed class LoopbackVoiceLearningCoordinator : IVoiceLearningCoordinator
{
    private readonly LoopbackServerClient _connection;

    public LoopbackVoiceLearningCoordinator(LoopbackServerClient connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public IVoiceEmbeddingEngine Engine { get; } = new ServerOwnedVoiceEmbeddingEngine();

    public async Task<int> ProcessPendingAsync(
        int limit = 100,
        IProgress<VoiceLearningProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new VoiceLearningProgress(0, 0, string.Empty, "The server is updating remembered voices"));
        var envelope = await _connection.SendJsonAsync<ValueEnvelope<int>>(
            HttpMethod.Post,
            WebApiRoutes.ClientTranscriptionOperation("voice-process"),
            new { limit },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        progress?.Report(new VoiceLearningProgress(envelope.Value, envelope.Value, string.Empty, "Remembered voices updated"));
        return envelope.Value;
    }

    private sealed record ValueEnvelope<T>(T Value);
}

internal sealed class ServerOwnedTranscriptionEngine : ITranscriptionEngine
{
    public string Id => "radiovault.server";
    public string DisplayName => "Radio Vault Server transcription";
    public string Version => "server";
    public bool IsAvailable => false;
    public bool SupportsWordTimings => true;
    public bool SupportsSpeakerDiarization => true;
    public string AvailabilityMessage => "Transcription is owned by the active Radio Vault Server.";

    public Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<TranscriptionEngineProgress> progress,
        CancellationToken cancellationToken)
        => Task.FromException<TranscriptionResult>(new InvalidOperationException(
            "The native client cannot run a local transcription engine. Send the job to the active Radio Vault Server."));
}

internal sealed class ServerOwnedVoiceEmbeddingEngine : IVoiceEmbeddingEngine
{
    public string Id => "radiovault.server.voice";
    public string DisplayName => "Radio Vault Server remembered voices";
    public string Version => "server";
    public bool IsAvailable => false;
    public int Dimensions => 0;

    public Task<VoiceEmbeddingResult> CreateEmbeddingAsync(
        VoiceEmbeddingRequest request,
        CancellationToken cancellationToken)
        => Task.FromException<VoiceEmbeddingResult>(new InvalidOperationException(
            "Remembered-voice processing is owned by the active Radio Vault Server."));
}
