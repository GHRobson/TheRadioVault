using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Contracts;

public interface ITranscriptRepository
{
    Task<TranscriptDocument?> GetForEpisodeAsync(long episodeId, CancellationToken cancellationToken = default);
    Task<TranscriptSummary?> GetSummaryForEpisodeAsync(long episodeId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TranscriptSummary>> GetSummariesAsync(CancellationToken cancellationToken = default);
    Task<TranscriptEpisodeIdentity?> GetEpisodeIdentityAsync(long episodeId, CancellationToken cancellationToken = default);
    Task<TranscriptionContext> GetTranscriptionContextAsync(long episodeId, CancellationToken cancellationToken = default);
    Task<string?> GetPreferredMediaPathAsync(long episodeId, CancellationToken cancellationToken = default);
    Task<long> GetEpisodeDurationMsAsync(long episodeId, CancellationToken cancellationToken = default);
    Task<TranscriptDocument> SaveAsync(TranscriptDocument document, CancellationToken cancellationToken = default);
    Task DeleteAsync(long episodeId, CancellationToken cancellationToken = default);

    Task CreateJobAsync(TranscriptionJobRecord job, CancellationToken cancellationToken = default);
    Task UpdateJobAsync(TranscriptionJobRecord job, CancellationToken cancellationToken = default);
    Task<TranscriptionJobRecord?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TranscriptionJobRecord>> GetJobsAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task RecordImportAsync(long episodeId, string packageId, string sourcePath, string checksum, int replacedRevision, CancellationToken cancellationToken = default);
}
