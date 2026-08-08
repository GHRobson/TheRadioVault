using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Contracts;

public interface ITranscriptionCoordinator
{
    ITranscriptionEngine Engine { get; }
    IMultiSpeakerDiarizationEngine? DiarizationEngine { get; }

    Task<Guid> QueueAsync(
        long episodeId,
        TranscriptionJobOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<Guid> RetryAsync(Guid transcriptionJobId, CancellationToken cancellationToken = default);
    Task<bool> PauseAsync(Guid transcriptionJobId, CancellationToken cancellationToken = default);
    Task<bool> ResumeAsync(Guid transcriptionJobId, CancellationToken cancellationToken = default);
    bool Cancel(Guid transcriptionJobId);
    Task<IReadOnlyList<TranscriptionJobRecord>> GetJobsAsync(int limit = 100, CancellationToken cancellationToken = default);
}
