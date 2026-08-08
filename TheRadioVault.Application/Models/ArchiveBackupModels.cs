namespace TheRadioVault.Application.Models;

public sealed record ArchiveBackupRestoreResult(
    bool PreservedLocalLibraries,
    int LibraryFolderCount,
    string Message);
