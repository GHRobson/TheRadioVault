using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Contracts;

public interface ITranscriptionBatchCoordinator : IDisposable
{
    Task<TranscriptionBatchRecord> CreateAndStartAsync(TranscriptionBatchCreateRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TranscriptionBatchRecord>> GetBatchesAsync(int limit = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TranscriptionBatchItemRecord>> GetItemsAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task PauseAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task ResumeAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task CancelAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task RetryFailedAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task<bool> MoveItemAsync(Guid batchId, long itemId, int direction, CancellationToken cancellationToken = default);
}
