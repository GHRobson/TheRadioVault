using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Contracts;

/// <summary>
/// Coordinates the single authoritative playback output shared by the server,
/// desktop clients and browser/mobile clients through a prepare/verify/commit
/// transaction. The current output is never stopped during target preparation.
/// </summary>
public interface IPlaybackHandoffService
{
    bool IsAvailable { get; }
    string CurrentDeviceId { get; }
    string CurrentDeviceName { get; }
    long CurrentGeneration { get; }
    Task<PlaybackHandoffSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<PlaybackHandoffSnapshot?> ClaimPlaybackAsync(
        long representativeEpisodeId,
        long positionMs,
        long durationMs,
        double speed,
        bool isPlaying,
        CancellationToken cancellationToken = default);
    Task<PlaybackTransferPlan> BeginTransferAsync(
        long representativeEpisodeId,
        long positionMs,
        long durationMs,
        double speed,
        bool play,
        CancellationToken cancellationToken = default);
    Task<PlaybackTransferPlan> MarkTransferReadyAsync(
        PlaybackTransferPlan transfer,
        long preparedPositionMs,
        long preparedDurationMs,
        bool decoderReady,
        bool desiredPlaying,
        bool overrideDesiredPlaying,
        double speed,
        CancellationToken cancellationToken = default);
    Task<PlaybackHandoffSnapshot> CommitTransferAsync(
        PlaybackTransferPlan transfer,
        long preparedPositionMs,
        bool decoderRunningMuted,
        CancellationToken cancellationToken = default);
    Task CancelTransferAsync(
        PlaybackTransferPlan transfer,
        string reason,
        CancellationToken cancellationToken = default);
    Task AcknowledgeSourceStoppedAsync(
        Guid transferId,
        long generation,
        CancellationToken cancellationToken = default);
    Task ReportAsync(
        long representativeEpisodeId,
        long positionMs,
        long durationMs,
        double speed,
        bool isPlaying,
        bool completed,
        bool explicitSeek = false,
        CancellationToken cancellationToken = default);
}
