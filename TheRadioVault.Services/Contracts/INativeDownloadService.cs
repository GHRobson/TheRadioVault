using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Contracts;

/// <summary>
/// Owns explicit device-local copies of canonical server recordings. Downloads
/// are foreground-only; this contract does not permit automatic prefetching.
/// </summary>
public interface INativeDownloadService
{
    event EventHandler? DownloadsChanged;

    Task<IReadOnlyList<NativeDownloadRecord>> GetDownloadsAsync(
        CancellationToken cancellationToken = default);

    Task<NativeDownloadRecord?> GetAsync(
        long representativeEpisodeId,
        CancellationToken cancellationToken = default);

    Task<NativeDownloadRecord> DownloadAsync(
        long representativeEpisodeId,
        IProgress<NativeDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        long representativeEpisodeId,
        CancellationToken cancellationToken = default);

    Task RemoveAllAsync(CancellationToken cancellationToken = default);

    Task<NativeDownloadAuditResult> AuditAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the personal playback snapshot attached to an existing local
    /// copy. This is a no-op when the broadcast has not been downloaded.
    /// </summary>
    Task UpdatePlaybackStateAsync(
        LocalPlaybackSaveRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a complete local playback descriptor when a healthy downloaded
    /// copy exists. Null means normal server playback should be used.
    /// </summary>
    Task<LocalPlaybackDescriptor?> TryPrepareAsync(
        string canonicalKey,
        long representativeEpisodeId,
        CancellationToken cancellationToken = default);
}
