using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Contracts;

public interface ITranscriptionEngine
{
    string Id { get; }
    string DisplayName { get; }
    string Version { get; }
    bool IsAvailable { get; }
    bool SupportsWordTimings { get; }
    bool SupportsSpeakerDiarization { get; }
    string AvailabilityMessage { get; }

    Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<TranscriptionEngineProgress> progress,
        CancellationToken cancellationToken);
}
