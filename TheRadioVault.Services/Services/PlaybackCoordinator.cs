using TheRadioVault.Core.Playback;
using TheRadioVault.Services.Contracts;

namespace TheRadioVault.Services.Services;

/// <summary>
/// Shared playback orchestration. It contains no WPF or operating-system code;
/// a platform-specific IPlaybackEngine performs the actual audio work.
/// </summary>
public sealed class PlaybackCoordinator : IPlaybackCoordinator, IDisposable
{
    private readonly IPlaybackEngine _engine;
    private long? _broadcastId;

    public PlaybackCoordinator(IPlaybackEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _engine.StateChanged += EngineOnStateChanged;
        _engine.MediaEnded += EngineOnMediaEnded;
        _engine.MediaFailed += EngineOnMediaFailed;
    }

    public event EventHandler<PlaybackSnapshot>? StateChanged;
    public event EventHandler? MediaEnded;
    public event EventHandler<PlaybackErrorEventArgs>? PlaybackFailed;

    public PlaybackSnapshot Current => new(
        _broadcastId,
        _broadcastId.HasValue && !string.IsNullOrWhiteSpace(_engine.MediaPath),
        _engine.Status,
        _engine.IsPlaying,
        _engine.Position,
        _engine.Duration,
        _engine.Speed,
        _engine.Volume,
        _engine.MediaPath);

    public Task LoadAsync(long broadcastId, string mediaPath, TimeSpan resumePosition, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (broadcastId <= 0) throw new ArgumentOutOfRangeException(nameof(broadcastId));
        if (string.IsNullOrWhiteSpace(mediaPath)) throw new ArgumentException("A media path is required.", nameof(mediaPath));

        _broadcastId = broadcastId;
        _engine.Open(mediaPath);
        if (resumePosition > TimeSpan.Zero) _engine.Position = resumePosition;
        RaiseStateChanged();
        return Task.CompletedTask;
    }

    public void Play() => _engine.Play();
    public void Pause() => _engine.Pause();
    public void Stop() => _engine.Stop();
    public void Toggle() { if (_engine.IsPlaying) _engine.Pause(); else _engine.Play(); }
    public void Seek(TimeSpan position) => _engine.Position = Clamp(position);
    public void Skip(TimeSpan amount) => _engine.Skip(amount);
    public void SetSpeed(double speed) => _engine.Speed = Math.Clamp(speed, 0.5d, 3d);
    public void SetVolume(double volume) => _engine.Volume = Math.Clamp(volume, 0d, 1d);

    private TimeSpan Clamp(TimeSpan position)
    {
        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        if (_engine.Duration is TimeSpan duration && duration > TimeSpan.Zero && position > duration) return duration;
        return position;
    }

    private void EngineOnStateChanged(object? sender, PlaybackEngineSnapshot e) => RaiseStateChanged();
    private void EngineOnMediaEnded(object? sender, EventArgs e) => MediaEnded?.Invoke(this, EventArgs.Empty);
    private void EngineOnMediaFailed(object? sender, PlaybackErrorEventArgs e) => PlaybackFailed?.Invoke(this, e);
    private void RaiseStateChanged() => StateChanged?.Invoke(this, Current);

    public void Dispose()
    {
        _engine.StateChanged -= EngineOnStateChanged;
        _engine.MediaEnded -= EngineOnMediaEnded;
        _engine.MediaFailed -= EngineOnMediaFailed;
    }
}
