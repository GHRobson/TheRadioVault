using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Services;

public sealed class VoiceLearningCoordinator : IVoiceLearningCoordinator
{
    private readonly ISpeakerIdentityRepository _speakerRepository;
    private readonly ITranscriptRepository _transcriptRepository;

    public VoiceLearningCoordinator(
        ISpeakerIdentityRepository speakerRepository,
        ITranscriptRepository transcriptRepository,
        IVoiceEmbeddingEngine engine)
    {
        _speakerRepository = speakerRepository ?? throw new ArgumentNullException(nameof(speakerRepository));
        _transcriptRepository = transcriptRepository ?? throw new ArgumentNullException(nameof(transcriptRepository));
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public IVoiceEmbeddingEngine Engine { get; }

    public async Task<int> ProcessPendingAsync(
        int limit = 100,
        IProgress<VoiceLearningProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Engine.IsAvailable)
            throw new InvalidOperationException($"{Engine.DisplayName}. Confirmed voice samples will remain queued until a local voice engine is configured.");

        var pending = await _speakerRepository.GetPendingVoiceSamplesAsync(limit, cancellationToken).ConfigureAwait(false);
        var completed = 0;
        for (var index = 0; index < pending.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = pending[index];
            progress?.Report(new VoiceLearningProgress(index, pending.Count, sample.PersonName, "Preparing voice sample"));
            try
            {
                var audioPath = await _transcriptRepository.GetPreferredMediaPathAsync(sample.EpisodeId, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
                    throw new FileNotFoundException("The source audio for this voice sample is not available locally.", audioPath);

                var request = new VoiceEmbeddingRequest(
                    sample.EpisodeId,
                    audioPath,
                    sample.StartMs,
                    sample.EndMs,
                    sample.SpeakerKey);
                var embedding = await Engine.CreateEmbeddingAsync(request, cancellationToken).ConfigureAwait(false);
                await _speakerRepository.SaveVoiceEmbeddingAsync(sample.Id, embedding, cancellationToken).ConfigureAwait(false);
                completed++;
                progress?.Report(new VoiceLearningProgress(index + 1, pending.Count, sample.PersonName, "Voice profile updated"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await _speakerRepository.MarkVoiceSampleFailedAsync(sample.Id, ex.Message, CancellationToken.None).ConfigureAwait(false);
                progress?.Report(new VoiceLearningProgress(index + 1, pending.Count, sample.PersonName, "Voice sample failed"));
            }
        }
        return completed;
    }
}
