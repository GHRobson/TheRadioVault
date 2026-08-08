using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Contracts;

public interface IVoiceLearningCoordinator
{
    IVoiceEmbeddingEngine Engine { get; }
    Task<int> ProcessPendingAsync(
        int limit = 100,
        IProgress<VoiceLearningProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
