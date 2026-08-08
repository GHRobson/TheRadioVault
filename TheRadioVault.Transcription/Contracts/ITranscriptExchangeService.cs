using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Contracts;

public interface ITranscriptExchangeService
{
    Task ExportAsync(long episodeId, string destinationPath, CancellationToken cancellationToken = default);
    Task<TranscriptImportResult> ImportAsync(long episodeId, string sourcePath, bool replaceExisting, CancellationToken cancellationToken = default);
}
