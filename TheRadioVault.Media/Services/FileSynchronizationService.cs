using System.Text.Json;
using TheRadioVault.Media.Contracts;
using TheRadioVault.Media.Models;

namespace TheRadioVault.Media.Services;

/// <summary>Portable, cancellation-aware file synchronisation workflow.</summary>
public sealed class FileSynchronizationService : IFileSynchronizationService
{
    private readonly IAudioMetadataWriter _metadataWriter;
    private readonly IMediaRenamePlanner _renamePlanner;

    public FileSynchronizationService(IAudioMetadataWriter metadataWriter, IMediaRenamePlanner renamePlanner)
    {
        _metadataWriter = metadataWriter;
        _renamePlanner = renamePlanner;
    }

    public IReadOnlyList<FileSynchronizationItem> Preview(IEnumerable<FileSynchronizationItem> items) =>
        items.Where(item => File.Exists(item.CurrentPath)).ToList();

    public async Task<FileSynchronizationResult> ApplyAsync(
        IReadOnlyList<FileSynchronizationItem> items,
        FileSynchronizationOptions options,
        string backupDirectory,
        IProgress<FileSynchronizationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        Directory.CreateDirectory(backupDirectory);
        var manifest = new List<object>();
        var errors = new List<string>();
        var processed = 0;
        var renamed = 0;
        var tagsWritten = 0;

        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[index];
            progress?.Report(new FileSynchronizationProgress(index, items.Count, item.CurrentPath, "Preparing"));
            if (!File.Exists(item.CurrentPath))
            {
                errors.Add($"File is unavailable: {item.CurrentPath}");
                continue;
            }

            try
            {
                var target = options.RenameFiles
                    ? _renamePlanner.FindAvailablePath(item.ProposedPath, item.CurrentPath)
                    : item.CurrentPath;
                manifest.Add(new
                {
                    item.BroadcastId,
                    item.BroadcastUid,
                    OriginalPath = item.CurrentPath,
                    NewPath = target
                });

                if (options.WriteTags)
                {
                    progress?.Report(new FileSynchronizationProgress(index, items.Count, item.CurrentPath, "Writing metadata"));
                    _metadataWriter.Write(new MediaWriteRequest(
                        item.CurrentPath,
                        string.IsNullOrWhiteSpace(item.Title) ? item.CollectionName : item.Title!,
                        item.AirDate.HasValue ? $"{item.CollectionName} ({item.AirDate.Value.Year})" : item.CollectionName,
                        new[] { item.CollectionName },
                        Array.Empty<string>(),
                        new[] { "Talk Radio" },
                        item.AirDate.HasValue ? (uint)item.AirDate.Value.Year : null,
                        $"Broadcast ID: {item.BroadcastUid}\nOriginal filename: {Path.GetFileName(item.CurrentPath)}\nManaged by The Radio Vault",
                        options.EmbedArtwork ? item.ArtworkPath : null));
                    tagsWritten++;
                }

                if (options.RenameFiles && !string.Equals(item.CurrentPath, target, StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report(new FileSynchronizationProgress(index, items.Count, item.CurrentPath, "Renaming"));
                    File.Move(item.CurrentPath, target);
                    renamed++;
                }
                processed++;
            }
            catch (Exception ex)
            {
                errors.Add($"{item.CurrentPath}: {ex.Message}");
            }

            progress?.Report(new FileSynchronizationProgress(index + 1, items.Count, item.CurrentPath, "Complete"));
            await Task.Yield();
        }

        string? manifestPath = null;
        if (options.CreateUndoManifest)
        {
            manifestPath = Path.Combine(backupDirectory, $"FileSyncUndo-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken).ConfigureAwait(false);
        }

        return new FileSynchronizationResult(processed, renamed, tagsWritten, errors.Count, manifestPath, errors);
    }
}
