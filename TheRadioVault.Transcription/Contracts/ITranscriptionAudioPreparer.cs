using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Contracts;

public interface ITranscriptionAudioPreparer
{
    Task<PreparedTranscriptionAudio> PrepareAsync(
        string sourcePath,
        long startMs,
        long? durationMs,
        string workingDirectory,
        IProgress<TranscriptionEngineProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record PreparedTranscriptionAudio(string AudioPath, long? InputOffsetMs = null);
