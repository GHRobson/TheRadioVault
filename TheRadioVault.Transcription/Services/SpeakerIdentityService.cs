using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Services;

public sealed class SpeakerIdentityService : ISpeakerIdentityService
{
    private readonly ISpeakerIdentityRepository _repository;

    public SpeakerIdentityService(ISpeakerIdentityRepository repository, IVoiceEmbeddingEngine voiceEngine)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        VoiceEngine = voiceEngine ?? throw new ArgumentNullException(nameof(voiceEngine));
    }

    public IVoiceEmbeddingEngine VoiceEngine { get; }

    public Task<IReadOnlyList<TranscriptSpeakerCluster>> GetClustersAsync(long episodeId, CancellationToken cancellationToken = default)
        => _repository.GetClustersForEpisodeAsync(episodeId, cancellationToken);

    public Task<IReadOnlyList<TranscriptPersonCandidate>> GetPeopleCandidatesAsync(long episodeId, CancellationToken cancellationToken = default)
        => _repository.GetEpisodePeopleAsync(episodeId, cancellationToken);

    public Task<SpeakerAssignmentResult> ConfirmAsync(
        long episodeId,
        string speakerKey,
        string personName,
        bool trainVoice = true,
        CancellationToken cancellationToken = default)
    {
        if (episodeId <= 0) throw new ArgumentOutOfRangeException(nameof(episodeId));
        if (string.IsNullOrWhiteSpace(speakerKey)) throw new ArgumentException("A speaker cluster is required.", nameof(speakerKey));
        if (string.IsNullOrWhiteSpace(personName)) throw new ArgumentException("Choose or enter a person name.", nameof(personName));
        return _repository.AssignClusterAsync(episodeId, speakerKey.Trim(), personName.Trim(), trainVoice, cancellationToken);
    }

    public Task ClearAsync(long episodeId, string speakerKey, CancellationToken cancellationToken = default)
        => _repository.ClearAssignmentAsync(episodeId, speakerKey, cancellationToken);

    public Task<IReadOnlyList<VoiceProfileSummary>> GetVoiceProfilesAsync(CancellationToken cancellationToken = default)
        => _repository.GetVoiceProfilesAsync(cancellationToken);

    public Task<IReadOnlyList<SpeakerMatchSuggestion>> GetSuggestionsAsync(long transcriptSpeakerId, CancellationToken cancellationToken = default)
        => _repository.GetSuggestionsAsync(transcriptSpeakerId, cancellationToken);
}
