using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Contracts;

/// <summary>
/// Owns explicit device-local copies of canonical server recordings. The caller
/// decides whether a transfer is manual or policy-driven; this boundary keeps
/// both paths on the same atomic download and safe-retention implementation.
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

    Task<NativeDownloadMaintenanceResult> MaintainAsync(
        NativeDownloadMaintenancePolicy policy,
        long? protectedEpisodeId = null,
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

public interface INativeDownloadPreferencesStore
{
    NativeDownloadPreferences Load();
    void Save(NativeDownloadPreferences preferences);
}
