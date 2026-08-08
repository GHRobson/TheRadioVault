using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Contracts;

public interface ITranscriptionBatchRepository
{
    Task<TranscriptionBatchRecord> CreateAsync(TranscriptionBatchCreateRequest request, CancellationToken cancellationToken = default);
    Task<TranscriptionBatchRecord?> GetAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TranscriptionBatchRecord>> GetBatchesAsync(int limit = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TranscriptionBatchItemRecord>> GetItemsAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task SetBatchStateAsync(Guid batchId, TranscriptionBatchState state, CancellationToken cancellationToken = default);
    Task SetItemStateAsync(long itemId, TranscriptionBatchItemState state, Guid? transcriptionJobId = null, string error = "", CancellationToken cancellationToken = default);
    Task ResetFailedItemsAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task CancelPendingItemsAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task<bool> MoveItemAsync(Guid batchId, long itemId, int direction, CancellationToken cancellationToken = default);
}
