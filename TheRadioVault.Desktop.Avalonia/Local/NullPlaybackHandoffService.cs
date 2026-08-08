using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Desktop.Avalonia.Local;

public sealed class NullPlaybackHandoffService : IPlaybackHandoffService
{
    public bool IsAvailable => false;
    public string CurrentDeviceId => string.Empty;
    public string CurrentDeviceName => "This device";
    public long CurrentGeneration => 0;
    public Task<PlaybackHandoffSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<PlaybackHandoffSnapshot?>(null);
    public Task<PlaybackHandoffSnapshot?> ClaimPlaybackAsync(long representativeEpisodeId, long positionMs, long durationMs, double speed, bool isPlaying, CancellationToken cancellationToken = default)
        => Task.FromResult<PlaybackHandoffSnapshot?>(null);
    public Task<PlaybackTransferPlan> BeginTransferAsync(long representativeEpisodeId, long positionMs, long durationMs, double speed, bool play, CancellationToken cancellationToken = default)
        => Task.FromException<PlaybackTransferPlan>(new InvalidOperationException("Shared playback handoff is unavailable."));
    public Task<PlaybackTransferPlan> MarkTransferReadyAsync(PlaybackTransferPlan transfer, long preparedPositionMs, long preparedDurationMs, bool decoderReady, bool desiredPlaying, bool overrideDesiredPlaying, double speed, CancellationToken cancellationToken = default)
        => Task.FromException<PlaybackTransferPlan>(new InvalidOperationException("Shared playback handoff is unavailable."));
    public Task<PlaybackHandoffSnapshot> CommitTransferAsync(PlaybackTransferPlan transfer, long preparedPositionMs, bool decoderRunningMuted, CancellationToken cancellationToken = default)
        => Task.FromException<PlaybackHandoffSnapshot>(new InvalidOperationException("Shared playback handoff is unavailable."));
    public Task CancelTransferAsync(PlaybackTransferPlan transfer, string reason, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task AcknowledgeSourceStoppedAsync(Guid transferId, long generation, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task ReportAsync(long representativeEpisodeId, long positionMs, long durationMs, double speed, bool isPlaying, bool completed, bool explicitSeek = false, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
