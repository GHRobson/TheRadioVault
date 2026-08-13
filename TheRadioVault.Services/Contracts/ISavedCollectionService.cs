using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Contracts;

public interface ISavedCollectionService
{
    Task<IReadOnlyList<SavedCollectionSummary>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<SavedCollectionDetails?> GetAsync(long collectionId, CancellationToken cancellationToken = default);
    Task<SavedCollectionDetails> CreateAsync(
        string name,
        SavedCollectionKind kind,
        SavedCollectionRule? rule = null,
        IReadOnlyList<long>? episodeIds = null,
        CancellationToken cancellationToken = default);
    Task<SavedCollectionDetails> UpdateAsync(
        long collectionId,
        string name,
        SavedCollectionRule? rule,
        long expectedRevision,
        CancellationToken cancellationToken = default);
    Task<SavedCollectionDetails> AddAsync(
        long collectionId,
        long episodeId,
        long expectedRevision,
        CancellationToken cancellationToken = default);
    Task<SavedCollectionDetails> RemoveAsync(
        long collectionId,
        long episodeId,
        long expectedRevision,
        CancellationToken cancellationToken = default);
    Task<SavedCollectionDetails> MoveAsync(
        long collectionId,
        long episodeId,
        int targetIndex,
        long expectedRevision,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(
        long collectionId,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}
