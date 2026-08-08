using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Services;

/// <summary>
/// Safe foundation engine used until a concrete local speech model is installed.
/// Keeping the unavailable state explicit prevents the UI or job queue from
/// pretending that transcription can run before its model/runtime is configured.
/// </summary>
public sealed class UnavailableTranscriptionEngine : ITranscriptionEngine
{
    public string Id => "not-configured";
    public string DisplayName => "No local transcription engine configured";
    public string Version => "0";
    public bool IsAvailable => false;
    public bool SupportsWordTimings => false;
    public bool SupportsSpeakerDiarization => false;
    public string AvailabilityMessage => "Configure whisper.cpp and a local model in Settings → Transcription.";

    public Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<TranscriptionEngineProgress> progress,
        CancellationToken cancellationToken)
        => throw new InvalidOperationException(
            "A local transcription engine has not been configured yet. " +
            "Radio Vault v0.27 requires the durable transcript, queue and exchange foundation only.");
}
