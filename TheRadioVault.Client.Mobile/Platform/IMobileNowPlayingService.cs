namespace TheRadioVault.Client.Mobile.Platform;

public enum MobileRemoteCommandKind
{
    Play,
    Pause,
    TogglePlayPause,
    SkipBack,
    SkipForward,
    Seek
}

public sealed record MobileRemoteCommand(MobileRemoteCommandKind Kind, TimeSpan? Position = null);

public sealed record MobileNowPlayingSnapshot(
    string Title,
    string Subtitle,
    TimeSpan Position,
    TimeSpan Duration,
    double Rate,
    bool IsPlaying,
    bool IsAvailable);

public interface IMobileNowPlayingService : IDisposable
{
    event EventHandler<MobileRemoteCommand>? CommandReceived;
    void Update(MobileNowPlayingSnapshot snapshot);
    void Clear();
}
