using System.Diagnostics.CodeAnalysis;
using TheRadioVault.Application.Models;
using TheRadioVault.Core.Playback;

namespace TheRadioVault.Application.Services;

/// <summary>
/// Platform-neutral owner of a single playback-engine session. Presentation
/// code supplies the host engine at the composition edge and issues playback
/// commands through this coordinator rather than controlling the engine
/// directly.
/// </summary>
public sealed class PlaybackSessionCoordinator : IDisposable
{
    private readonly IPlaybackEngine _engine;
    private long? _broadcastId;
    private bool _disposed;

    public PlaybackSessionCoordinator(IPlaybackEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _engine.MediaOpened += EngineOnMediaOpened;
        _engine.MediaEnded += EngineOnMediaEnded;
        _engine.MediaFailed += EngineOnMediaFailed;
        _engine.StateChanged += EngineOnStateChanged;
    }

    public event EventHandler? MediaOpened;
    public event EventHandler? MediaEnded;
    public event EventHandler<PlaybackErrorEventArgs>? MediaFailed;
    public event EventHandler<PlaybackSessionSnapshot>? StateChanged;

    public long? BroadcastId => _broadcastId;
    public PlaybackStatus Status => _engine.Status;
    public bool IsPlaying => _engine.IsPlaying;
    public string? MediaPath => _engine.MediaPath;
    public TimeSpan Position => _engine.Position;
    public TimeSpan? Duration => _engine.Duration;
    public double Volume => _engine.Volume;
    public double Speed => _engine.Speed;

    public PlaybackSessionSnapshot Current => new(
        _broadcastId,
        _engine.Status,
        _engine.IsPlaying,
        _engine.Position,
        _engine.Duration,
        _engine.Volume,
        _engine.Speed,
        _engine.MediaPath);

    public void SelectBroadcast(long broadcastId)
    {
        ThrowIfDisposed();
        if (broadcastId <= 0) throw new ArgumentOutOfRangeException(nameof(broadcastId));
        // Selecting a new broadcast must not publish the previous engine state under
        // the new broadcast identity. The first state notification for the new
        // broadcast is raised by Open once the new media path is attached.
        _broadcastId = broadcastId;
    }

    public void Open(string mediaPath)
    {
        ThrowIfDisposed();
        if (!_broadcastId.HasValue)
            throw new InvalidOperationException("Select a broadcast before opening media.");
        _engine.Open(mediaPath);
    }

    public void Play()
    {
        ThrowIfDisposed();
        _engine.Play();
    }

    public void Pause()
    {
        ThrowIfDisposed();
        _engine.Pause();
    }

    public void Stop()
    {
        ThrowIfDisposed();
        _engine.Stop();
    }

    public void Toggle()
    {
        ThrowIfDisposed();
        if (_engine.IsPlaying) _engine.Pause();
        else _engine.Play();
    }

    public void Seek(TimeSpan position)
    {
        ThrowIfDisposed();
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (_engine.Duration is TimeSpan duration && duration > TimeSpan.Zero && position > duration)
            position = duration;
        _engine.Position = position;
    }

    public void Skip(TimeSpan amount)
    {
        ThrowIfDisposed();
        _engine.Skip(amount);
    }

    public void SetSpeed(double speed)
    {
        ThrowIfDisposed();
        _engine.Speed = Math.Clamp(speed, 0.5d, 3d);
    }

    public void SetVolume(double volume)
    {
        ThrowIfDisposed();
        _engine.Volume = Math.Clamp(volume, 0d, 1d);
    }

    public bool TryGetCapability<TCapability>([NotNullWhen(true)] out TCapability? capability)
        where TCapability : class
    {
        ThrowIfDisposed();
        capability = _engine as TCapability;
        return capability is not null;
    }

    private void EngineOnMediaOpened(object? sender, EventArgs e) => MediaOpened?.Invoke(this, e);
    private void EngineOnMediaEnded(object? sender, EventArgs e) => MediaEnded?.Invoke(this, e);
    private void EngineOnMediaFailed(object? sender, PlaybackErrorEventArgs e) => MediaFailed?.Invoke(this, e);
    private void EngineOnStateChanged(object? sender, PlaybackEngineSnapshot e)
        => StateChanged?.Invoke(this, PlaybackSessionSnapshot.From(_broadcastId, e));

    private void RaiseStateChanged() => StateChanged?.Invoke(this, Current);

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PlaybackSessionCoordinator));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.MediaOpened -= EngineOnMediaOpened;
        _engine.MediaEnded -= EngineOnMediaEnded;
        _engine.MediaFailed -= EngineOnMediaFailed;
        _engine.StateChanged -= EngineOnStateChanged;
        _engine.Dispose();
    }
}
