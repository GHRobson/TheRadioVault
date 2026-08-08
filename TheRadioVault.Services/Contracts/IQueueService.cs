using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Contracts;

public interface IQueueService
{
    Task<IReadOnlyList<QueueRecord>> GetAsync(CancellationToken cancellationToken = default);
    Task<long> AddAsync(long broadcastId, bool playNext = false, CancellationToken cancellationToken = default);
    Task RemoveAsync(long queueItemId, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
    Task MoveAsync(long queueItemId, int direction, CancellationToken cancellationToken = default);
}
