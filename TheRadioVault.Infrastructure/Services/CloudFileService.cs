using System.IO;
using TheRadioVault.Models;

namespace TheRadioVault.Services;

public sealed class CloudFileService
{
    private const uint Offline = 0x00001000;
    private const uint RecallOnOpen = 0x00040000;
    private const uint RecallOnDataAccess = 0x00400000;
    private const uint ReparsePoint = 0x00000400;

    public EpisodeStorageState GetStorageState(string path)
    {
        if (!File.Exists(path)) return EpisodeStorageState.Missing;
        try
        {
            var flags = unchecked((uint)File.GetAttributes(path));
            var hasCloudRecallFlag = (flags & (Offline | RecallOnOpen | RecallOnDataAccess)) != 0;
            var isPlaceholder = (flags & ReparsePoint) != 0;
            if (hasCloudRecallFlag && isPlaceholder)
                return EpisodeStorageState.CloudOnly;
            return EpisodeStorageState.AvailableOffline;
        }
        catch
        {
            return EpisodeStorageState.Missing;
        }
    }

    public async Task<bool> EnsureAvailableAsync(string path, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return false;
        if (GetStorageState(path) == EpisodeStorageState.AvailableOffline) return true;

        progress?.Report("Making the recording available locally…");
        try
        {
            // Opening and reading the first byte asks the Windows cloud-files provider to hydrate the placeholder.
            await Task.Run(() =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
                _ = stream.ReadByte();
            }, cancellationToken);

            for (var attempt = 0; attempt < 120; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (GetStorageState(path) == EpisodeStorageState.AvailableOffline) return true;
                await Task.Delay(500, cancellationToken);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
        return GetStorageState(path) == EpisodeStorageState.AvailableOffline;
    }
}
