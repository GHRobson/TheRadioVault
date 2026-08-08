namespace TheRadioVault.Core.Playback;

public enum PlaybackStatus
{
    Stopped,
    Opening,
    Paused,
    Playing,
    Buffering,
    Ended,
    Failed
}

public sealed class PlaybackErrorEventArgs : EventArgs
{
    public PlaybackErrorEventArgs(Exception exception)
    {
        ErrorException = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    public Exception ErrorException { get; }
}

public sealed record PlaybackEngineSnapshot(
    PlaybackStatus Status,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan? Duration,
    double Volume,
    double Speed,
    string? MediaPath);

/// <summary>
/// Platform-neutral audio engine contract. UI projects depend on this interface;
/// Windows, Avalonia, iOS and other clients provide their own implementation.
/// </summary>
public interface IPlaybackEngine : IDisposable
{
    event EventHandler? MediaOpened;
    event EventHandler? MediaEnded;
    event EventHandler<PlaybackErrorEventArgs>? MediaFailed;
    event EventHandler<PlaybackEngineSnapshot>? StateChanged;

    PlaybackStatus Status { get; }
    bool IsPlaying { get; }
    string? MediaPath { get; }
    TimeSpan Position { get; set; }
    TimeSpan? Duration { get; }
    double Volume { get; set; }
    double Speed { get; set; }

    void Open(string path);
    void Play();
    void Pause();
    void Stop();
    void Skip(TimeSpan amount);
}
