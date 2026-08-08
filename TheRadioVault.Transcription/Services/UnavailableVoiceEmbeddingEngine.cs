using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Services;

/// <summary>
/// Explicit placeholder until the local voice-embedding runtime is installed.
/// Manual speaker confirmations can already accumulate durable training samples;
/// this engine will later turn those samples into reusable voice profiles.
/// </summary>
public sealed class UnavailableVoiceEmbeddingEngine : IVoiceEmbeddingEngine
{
    public string Id => "not-configured";
    public string DisplayName => "No local voice-learning engine configured";
    public string Version => "0";
    public bool IsAvailable => false;
    public int Dimensions => 0;

    public Task<VoiceEmbeddingResult> CreateEmbeddingAsync(VoiceEmbeddingRequest request, CancellationToken cancellationToken)
        => throw new InvalidOperationException(
            "A local voice-embedding engine has not been configured yet. " +
            "Confirmed speaker assignments are retained as pending learning samples for the next implementation slice.");
}
