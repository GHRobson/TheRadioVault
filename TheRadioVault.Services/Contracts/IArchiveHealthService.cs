using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Contracts;

public interface IArchiveHealthService
{
    Task<ArchiveHealthReport> AnalyseAsync(ArchiveHealthOptions? options = null, CancellationToken cancellationToken = default);
}
