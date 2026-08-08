using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Contracts;

public interface ISpeakerIdentityService
{
    IVoiceEmbeddingEngine VoiceEngine { get; }
    Task<IReadOnlyList<TranscriptSpeakerCluster>> GetClustersAsync(long episodeId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TranscriptPersonCandidate>> GetPeopleCandidatesAsync(long episodeId, CancellationToken cancellationToken = default);
    Task<SpeakerAssignmentResult> ConfirmAsync(long episodeId, string speakerKey, string personName, bool trainVoice = true, CancellationToken cancellationToken = default);
    Task ClearAsync(long episodeId, string speakerKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VoiceProfileSummary>> GetVoiceProfilesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SpeakerMatchSuggestion>> GetSuggestionsAsync(long transcriptSpeakerId, CancellationToken cancellationToken = default);
}
