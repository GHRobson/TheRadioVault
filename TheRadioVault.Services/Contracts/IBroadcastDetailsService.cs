using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Contracts;

public interface IBroadcastDetailsService
{
    Task<BroadcastDetails?> GetAsync(long representativeEpisodeId, CancellationToken cancellationToken = default);
}
