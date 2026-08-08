using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Contracts;

public interface IVoiceEmbeddingEngine
{
    string Id { get; }
    string DisplayName { get; }
    string Version { get; }
    bool IsAvailable { get; }
    int Dimensions { get; }

    Task<VoiceEmbeddingResult> CreateEmbeddingAsync(VoiceEmbeddingRequest request, CancellationToken cancellationToken);
}
