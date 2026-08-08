using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Contracts;

public interface IMomentsService
{
    Task<IReadOnlyList<MomentRecord>> GetForBroadcastAsync(long broadcastId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MomentRecord>> SearchAsync(string? searchText, int limit = 500, CancellationToken cancellationToken = default);
    Task<long> AddAsync(long broadcastId, long positionMs, string title, string? notes = null, CancellationToken cancellationToken = default);
    Task UpdateAsync(long momentId, string title, string? notes, CancellationToken cancellationToken = default);
    Task DeleteAsync(long momentId, CancellationToken cancellationToken = default);
}
