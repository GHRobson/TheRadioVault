using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Contracts;

public interface ILibraryMaintenanceService
{
    bool IsAvailable { get; }
    Task<LibraryMaintenanceSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<LibraryMaintenanceSnapshot> ScanAsync(CancellationToken cancellationToken = default);
}
