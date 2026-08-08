using TheRadioVault.Core.Playback;

namespace TheRadioVault.Application.Abstractions;

/// <summary>
/// Creates the platform's local-media playback engine without exposing a UI
/// toolkit or concrete media implementation to the application shell.
/// </summary>
public interface ILocalPlaybackEngineFactory
{
    IPlaybackEngine Create();
}
