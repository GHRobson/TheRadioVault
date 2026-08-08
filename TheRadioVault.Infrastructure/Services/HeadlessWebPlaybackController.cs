using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Services;

/// <summary>
/// Server-owned playback session state for a host that has no audio output of
/// its own. Clients remain responsible for rendering audio; the server keeps a
/// revisioned, conflict-safe state until full client handoff is connected.
/// </summary>
public sealed class HeadlessWebPlaybackController : IWebPlaybackController
{
    private readonly object _gate = new();
    private WebPlaybackState _state = Idle();

    public WebPlaybackState GetPlaybackState()
    {
        lock (_gate) return _state;
    }

    public WebPlaybackCommandResult ExecutePlaybackCommand(WebPlaybackCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            if (!command.Force && command.ExpectedRevision.HasValue && command.ExpectedRevision.Value != _state.Revision)
                return new WebPlaybackCommandResult(false, true, "Playback changed on another client.", _state);

            var name = (command.Command ?? string.Empty).Trim().ToLowerInvariant();
            var episodeId = command.EpisodeId ?? _state.EpisodeId;
            var positionMs = command.PositionMs ?? _state.PositionMs;
            var speed = command.Speed.HasValue ? Math.Clamp(command.Speed.Value, 0.5d, 3d) : _state.Speed;
            var playing = _state.IsPlaying;
            var status = _state.Status;
            var changed = false;
            var message = "Playback session refreshed.";

            switch (name)
            {
                case "play-episode":
                case "play":
                    if (!episodeId.HasValue)
                        return new WebPlaybackCommandResult(false, false, "A broadcast is required.", _state);
                    playing = true;
                    status = "InProgress";
                    changed = episodeId != _state.EpisodeId || positionMs != _state.PositionMs || !_state.IsPlaying;
                    message = "Playback session claimed by the client.";
                    break;
                case "pause":
                    playing = false;
                    status = episodeId.HasValue ? "Paused" : "Idle";
                    changed = _state.IsPlaying || positionMs != _state.PositionMs;
                    message = "Playback session paused.";
                    break;
                case "seek":
                    if (!episodeId.HasValue || !command.PositionMs.HasValue)
                        return new WebPlaybackCommandResult(false, false, "A loaded broadcast and position are required.", _state);
                    changed = positionMs != _state.PositionMs;
                    message = "Playback position changed.";
                    break;
                case "skip":
                    if (!episodeId.HasValue || !command.DeltaSeconds.HasValue)
                        return new WebPlaybackCommandResult(false, false, "A loaded broadcast and skip distance are required.", _state);
                    positionMs = Math.Max(0, _state.PositionMs + command.DeltaSeconds.Value * 1_000L);
                    changed = positionMs != _state.PositionMs;
                    message = command.DeltaSeconds.Value < 0 ? "Playback moved back." : "Playback moved forward.";
                    break;
                case "speed":
                    if (!episodeId.HasValue || !command.Speed.HasValue)
                        return new WebPlaybackCommandResult(false, false, "A loaded broadcast and speed are required.", _state);
                    changed = Math.Abs(speed - _state.Speed) > 0.0001d;
                    message = $"Playback speed set to {speed:0.##}×.";
                    break;
                case "transfer-to-web":
                case "claim-phone":
                case "claim-device":
                case "claim-device-paused":
                case "pause-for-handoff-commit":
                    playing = false;
                    status = episodeId.HasValue ? "Paused" : "Idle";
                    changed = _state.IsPlaying || command.PositionMs.HasValue;
                    message = "Playback output released for client handoff.";
                    break;
                default:
                    return new WebPlaybackCommandResult(false, false, "Unknown playback command.", _state);
            }

            if (!changed) return new WebPlaybackCommandResult(false, false, message, _state);
            _state = _state with
            {
                EpisodeId = episodeId,
                PositionMs = positionMs,
                Status = status,
                IsPlaying = playing,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastPlayedAt = episodeId.HasValue ? DateTime.UtcNow : null,
                Device = string.IsNullOrWhiteSpace(command.DeviceName) ? "RadioVault client" : command.DeviceName.Trim(),
                Speed = speed,
                Revision = _state.Revision + 1,
                ControllerClientId = string.IsNullOrWhiteSpace(command.ClientId) ? "unknown-client" : command.ClientId.Trim()
            };
            return new WebPlaybackCommandResult(true, false, message, _state);
        }
    }

    private static WebPlaybackState Idle() => new(
        EpisodeId: null,
        Show: string.Empty,
        Title: string.Empty,
        PositionMs: 0,
        DurationMs: 0,
        Status: "Idle",
        LastPlayedAt: null,
        IsPlaying: false,
        UpdatedAt: DateTimeOffset.UtcNow,
        Device: "RadioVault Server",
        Speed: 1d,
        Revision: 0,
        ControllerClientId: "radio-vault-server");
}
