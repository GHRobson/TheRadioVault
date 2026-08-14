using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace TheRadioVault.Server.Services;

public sealed class ServerFolderSelectionService
{
    private readonly Window _owner;

    public ServerFolderSelectionService(Window owner)
        => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public async Task<string?> PickLibraryFolderAsync(
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        var results = await _owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = string.IsNullOrWhiteSpace(title)
                ? "Choose a Library folder on this server computer"
                : title,
            AllowMultiple = false
        }).WaitAsync(cancellationToken).ConfigureAwait(true);
        return results.FirstOrDefault()?.TryGetLocalPath();
    }
}
