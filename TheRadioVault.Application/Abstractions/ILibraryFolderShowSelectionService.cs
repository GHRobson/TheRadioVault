using TheRadioVault.Application.Models;

namespace TheRadioVault.Application.Abstractions;

public interface ILibraryFolderShowSelectionService
{
    Task<LibraryFolderShowChoice?> ChooseAsync(
        string folderPath,
        IReadOnlyList<LibraryFolderShowChoice> choices,
        CancellationToken cancellationToken = default);
}
