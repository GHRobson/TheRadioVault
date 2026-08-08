using TheRadioVault.Application.Abstractions;
using TheRadioVault.Application.Models;
using TheRadioVault.Services;

namespace TheRadioVault.Desktop.Avalonia.Platform;

public sealed class AvaloniaArchiveBackupService : IArchiveBackupService
{
    private readonly BackupService _backup = new();

    public Task<string> CreateAsync(string destinationPath, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _backup.CreateBackup(destinationPath);
        }, cancellationToken);

    public Task<ArchiveBackupRestoreResult> RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = _backup.RestoreBackup(backupPath);
            var message = result.PreservedLocalLibraries
                ? $"Backup restored while preserving {result.LibraryFolderCount:N0} local Library folder registrations. Restart Radio Vault now."
                : "Backup restored. Restart Radio Vault now.";
            return new ArchiveBackupRestoreResult(result.PreservedLocalLibraries, result.LibraryFolderCount, message);
        }, cancellationToken);
}
