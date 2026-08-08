using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Contracts;

public interface IMultiSpeakerDiarizationEngine
{
    string Id { get; }
    string DisplayName { get; }
    string Version { get; }
    bool IsAvailable { get; }
    string AvailabilityMessage { get; }

    Task<SpeakerDiarizationResult> DiarizeAsync(
        SpeakerDiarizationRequest request,
        IProgress<TranscriptionEngineProgress> progress,
        CancellationToken cancellationToken);
}
