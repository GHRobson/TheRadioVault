using TheRadioVault.Media.Models;

namespace TheRadioVault.Media.Contracts;

public interface IAudioMetadataReader
{
    AudioMetadata Read(string path);
}

public interface IAudioMetadataWriter
{
    void Write(MediaWriteRequest request);
}

public interface IArtworkCache
{
    string? Store(byte[]? artworkBytes, string? mimeType);
}

public interface IMediaFingerprintService
{
    MediaFingerprint Create(string path, bool includeFullHash = false);
}

public interface IMediaRenamePlanner
{
    RenamePlan Plan(string currentPath, string collectionName, DateTime? airDate, string? title, int partNumber = 1);
    string FindAvailablePath(string proposedPath, string currentPath);
}

public interface IMediaInspectionService
{
    MediaInspection Inspect(string path, bool includeFullHash = false);
}

public interface IFileSynchronizationService
{
    IReadOnlyList<FileSynchronizationItem> Preview(IEnumerable<FileSynchronizationItem> items);
    Task<FileSynchronizationResult> ApplyAsync(
        IReadOnlyList<FileSynchronizationItem> items,
        FileSynchronizationOptions options,
        string backupDirectory,
        IProgress<FileSynchronizationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
