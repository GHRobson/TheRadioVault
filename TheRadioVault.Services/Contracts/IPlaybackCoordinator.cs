using TheRadioVault.Core.Playback;

namespace TheRadioVault.Services.Contracts;

public sealed record PlaybackSnapshot(
    long? BroadcastId,
    bool IsLoaded,
    PlaybackStatus Status,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan? Duration,
    double Speed,
    double Volume,
    string? MediaPath);

public interface IPlaybackCoordinator
{
    event EventHandler<PlaybackSnapshot>? StateChanged;
    event EventHandler? MediaEnded;
    event EventHandler<PlaybackErrorEventArgs>? PlaybackFailed;

    PlaybackSnapshot Current { get; }
    Task LoadAsync(long broadcastId, string mediaPath, TimeSpan resumePosition, CancellationToken cancellationToken = default);
    void Play();
    void Pause();
    void Stop();
    void Toggle();
    void Seek(TimeSpan position);
    void Skip(TimeSpan amount);
    void SetSpeed(double speed);
    void SetVolume(double volume);
}
