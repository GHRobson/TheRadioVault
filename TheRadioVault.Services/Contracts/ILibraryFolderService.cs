using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Contracts;

public interface ILibraryFolderService
{
    Task<IReadOnlyList<LibraryFolderRecord>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LibraryFolderCollectionOption>> GetAssignableCollectionsAsync(CancellationToken cancellationToken = default);
    Task<long> AddAsync(string path, int? collectionId, bool recursive = true, CancellationToken cancellationToken = default);
    Task SetCollectionAsync(long folderId, int? collectionId, CancellationToken cancellationToken = default);
    Task SetEnabledAsync(long folderId, bool enabled, CancellationToken cancellationToken = default);
    Task RemoveAsync(long folderId, CancellationToken cancellationToken = default);
}
