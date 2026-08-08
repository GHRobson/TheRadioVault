using TheRadioVault.Application.Models;

namespace TheRadioVault.Application.Abstractions;

public interface IArchiveBackupService
{
    Task<string> CreateAsync(string destinationPath, CancellationToken cancellationToken = default);
    Task<ArchiveBackupRestoreResult> RestoreAsync(string backupPath, CancellationToken cancellationToken = default);
}
