using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Contracts;

public interface ISpeakerIdentityRepository
{
    Task<IReadOnlyList<TranscriptSpeakerCluster>> GetClustersForEpisodeAsync(long episodeId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TranscriptPersonCandidate>> GetEpisodePeopleAsync(long episodeId, CancellationToken cancellationToken = default);
    Task<VoicePersonRecord> GetOrCreateVoicePersonAsync(string personName, CancellationToken cancellationToken = default);
    Task<SpeakerAssignmentResult> AssignClusterAsync(long episodeId, string speakerKey, string personName, bool trainVoice, CancellationToken cancellationToken = default);
    Task ClearAssignmentAsync(long episodeId, string speakerKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VoiceProfileSummary>> GetVoiceProfilesAsync(CancellationToken cancellationToken = default);
    Task<VoiceProfileSummary?> GetVoiceProfileAsync(long voicePersonId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VoiceSampleRecord>> GetPendingVoiceSamplesAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task SaveVoiceEmbeddingAsync(long sampleId, VoiceEmbeddingResult result, CancellationToken cancellationToken = default);
    Task MarkVoiceSampleFailedAsync(long sampleId, string error, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SpeakerMatchSuggestion>> MatchClusterAsync(long episodeId, string speakerKey, VoiceEmbeddingResult embedding, int limit = 5, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SpeakerMatchSuggestion>> GetSuggestionsAsync(long transcriptSpeakerId, CancellationToken cancellationToken = default);
}
