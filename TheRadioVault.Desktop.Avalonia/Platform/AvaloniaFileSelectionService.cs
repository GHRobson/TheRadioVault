using Avalonia.Platform.Storage;
using TheRadioVault.Application.Abstractions;
using TheRadioVault.Application.Models;

namespace TheRadioVault.Desktop.Avalonia.Platform;

public sealed class AvaloniaFileSelectionService : IFileSelectionService
{
    private readonly AvaloniaWindowProvider _windows;
    public AvaloniaFileSelectionService(AvaloniaWindowProvider windows) => _windows = windows;

    public async Task<string?> PickOpenFileAsync(FileSelectionRequest request, CancellationToken cancellationToken = default)
    {
        var provider = RequireProvider();
        var result = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = request.Title,
            AllowMultiple = false,
            FileTypeFilter = ParseFileTypes(request.Filter)
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickSaveFileAsync(FileSelectionRequest request, CancellationToken cancellationToken = default)
    {
        var provider = RequireProvider();
        var result = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = request.Title,
            DefaultExtension = request.DefaultExtension,
            SuggestedFileName = request.SuggestedFileName,
            FileTypeChoices = ParseFileTypes(request.Filter)
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
        return result?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(FileSelectionRequest request, CancellationToken cancellationToken = default)
    {
        var provider = RequireProvider();
        var result = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = request.Title,
            AllowMultiple = false
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    private IStorageProvider RequireProvider() =>
        _windows.MainWindow?.StorageProvider
        ?? throw new InvalidOperationException("The main window storage provider is not available yet.");

    private static IReadOnlyList<FilePickerFileType>? ParseFileTypes(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return null;
        var segments = filter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<FilePickerFileType>();
        for (var index = 0; index + 1 < segments.Length; index += 2)
        {
            var patterns = segments[index + 1]
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
            result.Add(new FilePickerFileType(segments[index]) { Patterns = patterns });
        }
        return result.Count == 0 ? null : result;
    }
}
