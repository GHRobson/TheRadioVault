using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Contracts;

/// <summary>
/// Resolves canonical broadcasts into ordered local-media segments and persists
/// canonical listening state without exposing SQLite or physical-file identity
/// to the presentation layer.
/// </summary>
public interface ILocalPlaybackLibraryService
{
    Task<LocalPlaybackDescriptor> PrepareAsync(
        string canonicalKey,
        long representativeEpisodeId,
        CancellationToken cancellationToken = default);

    Task<LocalPlaybackDescriptor> PrepareAsync(
        long representativeEpisodeId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        LocalPlaybackSaveRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Flushes any queued progress write and durable client-side cache before shutdown.
    /// Local implementations may complete immediately once their database transaction is committed.
    /// </summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}
