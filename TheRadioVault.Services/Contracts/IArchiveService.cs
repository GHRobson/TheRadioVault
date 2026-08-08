using TheRadioVault.Core.Domain;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Contracts;

public interface IArchiveService
{
    Task<IReadOnlyList<BroadcastSummary>> SearchAsync(ArchiveSearchRequest request, CancellationToken cancellationToken = default);
    Task<BroadcastSummary?> GetByIdAsync(long broadcastId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArchivePeriodSummary>> GetYearsAsync(int collectionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArchivePeriodSummary>> GetMonthsAsync(int collectionId, int year, CancellationToken cancellationToken = default);
    Task SetFavouriteAsync(IEnumerable<long> broadcastIds, bool favourite, CancellationToken cancellationToken = default);
    Task SetPlayedAsync(IEnumerable<long> broadcastIds, bool played, CancellationToken cancellationToken = default);
}
