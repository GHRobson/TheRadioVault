using TheRadioVault.Application.Models;

namespace TheRadioVault.Application.Abstractions;

public interface IFileSelectionService
{
    Task<string?> PickOpenFileAsync(FileSelectionRequest request, CancellationToken cancellationToken = default);
    Task<string?> PickSaveFileAsync(FileSelectionRequest request, CancellationToken cancellationToken = default);
    Task<string?> PickFolderAsync(FileSelectionRequest request, CancellationToken cancellationToken = default);
}
