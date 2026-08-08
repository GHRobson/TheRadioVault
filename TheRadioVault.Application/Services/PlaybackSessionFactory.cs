using TheRadioVault.Core.Playback;

namespace TheRadioVault.Application.Services;

/// <summary>
/// Creates one application-owned playback session around a host-selected engine.
/// Keeping this construction in the Application layer makes session ownership
/// identical for the current WPF shell and the future Avalonia shell.
/// </summary>
public sealed class PlaybackSessionFactory
{
    public PlaybackSessionCoordinator Create(IPlaybackEngine engine)
        => new(engine ?? throw new ArgumentNullException(nameof(engine)));
}
