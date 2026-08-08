using System.ComponentModel;
using TheRadioVault.Application.Abstractions;
using TheRadioVault.Core.Events;
using TheRadioVault.Core.Playback;
using TheRadioVault.Presentation.ViewModels;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Desktop.Avalonia.Anywhere;

internal sealed class AvaloniaWebPlaybackController : IWebPlaybackController, IDisposable
{
    private readonly PlaybackViewModel _playback;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILivePlaybackStateStore _livePlayback;
    private readonly IApplicationEventBus _events;
    private long _revision;
    private bool _disposed;

    public AvaloniaWebPlaybackController(
        PlaybackViewModel playback,
        IUiDispatcher dispatcher,
        ILivePlaybackStateStore livePlayback,
        IApplicationEventBus events)
    {
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _livePlayback = livePlayback ?? throw new ArgumentNullException(nameof(livePlayback));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _playback.PropertyChanged += PlaybackOnPropertyChanged;
        PublishState(forceEvent: false);
    }

    public WebPlaybackState GetPlaybackState()
        => _dispatcher.CheckAccess()
            ? CreateState()
            : _dispatcher.InvokeAsync(CreateState).GetAwaiter().GetResult();

    public WebPlaybackCommandResult ExecutePlaybackCommand(WebPlaybackCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var before = GetPlaybackState();
        var name = (command.Command ?? string.Empty).Trim().ToLowerInvariant();
        var changed = false;
        var message = "Playback state refreshed.";

        try
        {
            switch (name)
            {
                case "play-episode":
                case "transfer-to-desktop":
                    if (!command.EpisodeId.HasValue)
                        return Result(false, false, "A broadcast is required.");
                    RunAsyncOnUi(() => _playback.LoadAndPlayAtAsync(command.EpisodeId.Value, command.PositionMs ?? 0));
                    changed = true;
                    message = name == "transfer-to-desktop" ? "Playback transferred to this Radio Vault." : "Playback started.";
                    break;

                case "play":
                    if (!_playback.IsLoaded && command.EpisodeId.HasValue)
                    {
                        RunAsyncOnUi(() => _playback.LoadAndPlayAtAsync(command.EpisodeId.Value, command.PositionMs ?? 0));
                        changed = true;
                    }
                    else if (_playback.IsLoaded && !_playback.IsPlaying)
                    {
                        RunOnUi(() =>
                        {
                            if (command.PositionMs.HasValue) _playback.SeekTo(command.PositionMs.Value);
                            _playback.Toggle();
                        });
                        changed = true;
                    }
                    message = changed ? "Playback started." : "Playback was already playing.";
                    break;

                case "pause":
                    RunOnUi(() =>
                    {
                        if (_playback.IsPlaying) _playback.Toggle();
                        if (command.PositionMs.HasValue && _playback.IsLoaded)
                            _playback.SeekTo(command.PositionMs.Value);
                    });
                    changed = before.IsPlaying || command.PositionMs.HasValue;
                    message = changed ? "Playback paused." : "Playback was already paused.";
                    break;

                case "pause-for-handoff-commit":
                    RunOnUi(() => changed = _playback.ReleaseForRemoteHandoff(command.PositionMs, command.DeviceName));
                    changed = changed || before.IsPlaying;
                    message = $"Playback output released for {NormalizeDeviceName(command.DeviceName)} after transactional commit.";
                    break;

                case "transfer-to-web":
                case "claim-phone":
                case "claim-device":
                case "claim-device-paused":
                    RunOnUi(() => changed = _playback.ReleaseForRemoteHandoff(command.PositionMs, command.DeviceName));
                    changed = changed || command.PositionMs.HasValue || name.StartsWith("claim-", StringComparison.Ordinal);
                    message = $"Playback released for {NormalizeDeviceName(command.DeviceName)}.";
                    break;

                case "seek":
                    if (!command.PositionMs.HasValue || !_playback.IsLoaded)
                        return Result(false, false, "There is no loaded broadcast to seek.");
                    RunOnUi(() => _playback.SeekTo(command.PositionMs.Value));
                    changed = true;
                    message = "Playback position changed.";
                    break;

                case "skip":
                    if (!command.DeltaSeconds.HasValue || !_playback.IsLoaded)
                        return Result(false, false, "There is no loaded broadcast to skip.");
                    RunOnUi(() => _playback.Skip(TimeSpan.FromSeconds(command.DeltaSeconds.Value)));
                    changed = true;
                    message = command.DeltaSeconds.Value < 0 ? "Playback moved back." : "Playback moved forward.";
                    break;

                case "speed":
                    if (!command.Speed.HasValue || !_playback.IsLoaded)
                        return Result(false, false, "A loaded broadcast and speed are required.");
                    RunOnUi(() => _playback.SetSpeed(command.Speed.Value));
                    changed = true;
                    message = $"Playback speed set to {Math.Clamp(command.Speed.Value, 0.5d, 3d):0.##}×.";
                    break;

                default:
                    return Result(false, false, "Unknown playback command.");
            }
        }
        catch (Exception exception)
        {
            return Result(false, true, exception.Message);
        }

        if (changed) Interlocked.Increment(ref _revision);
        PublishState(forceEvent: changed);
        return new WebPlaybackCommandResult(changed, false, message, GetPlaybackState());
    }

    private WebPlaybackCommandResult Result(bool changed, bool conflict, string message)
        => new(changed, conflict, message, GetPlaybackState());

    private static string NormalizeDeviceName(string? value)
        => string.IsNullOrWhiteSpace(value) ? "the other device" : value.Trim();

    private WebPlaybackState CreateState()
    {
        long? episodeId = _playback.HasCurrentBroadcast ? _playback.CurrentBroadcastId : null;
        return new WebPlaybackState(
            episodeId,
            _playback.HasCurrentBroadcast ? _playback.Subtitle : string.Empty,
            _playback.HasCurrentBroadcast ? _playback.Title : string.Empty,
            _playback.PositionMs,
            _playback.DurationMs,
            _playback.IsLoaded ? (_playback.IsPlaying ? "InProgress" : "Paused") : "Idle",
            _playback.HasCurrentBroadcast ? DateTime.UtcNow : null,
            _playback.IsPlaying,
            DateTimeOffset.UtcNow,
            "Desktop",
            _playback.Speed,
            Volatile.Read(ref _revision),
            "avalonia-desktop");
    }

    private void PlaybackOnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(PlaybackViewModel.PositionMs)
            or nameof(PlaybackViewModel.DurationMs)
            or nameof(PlaybackViewModel.IsPlaying)
            or nameof(PlaybackViewModel.Title)
            or nameof(PlaybackViewModel.Subtitle)
            or nameof(PlaybackViewModel.Speed))
        {
            PublishState(forceEvent: false);
        }
    }

    private void PublishState(bool forceEvent)
    {
        WebPlaybackState state;
        try { state = GetPlaybackState(); }
        catch { return; }

        _livePlayback.Update(new LivePlaybackSnapshot(
            state.EpisodeId,
            state.Show,
            state.Title,
            state.PositionMs,
            state.DurationMs,
            state.Status,
            state.IsPlaying,
            state.UpdatedAt ?? DateTimeOffset.UtcNow));

        if (forceEvent)
        {
            _events.Publish(new PlaybackChangedEvent(
                state.EpisodeId,
                state.PositionMs,
                state.DurationMs,
                state.IsPlaying,
                DateTimeOffset.UtcNow));
        }
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.InvokeAsync(action).GetAwaiter().GetResult();
    }

    private void RunAsyncOnUi(Func<Task> action)
    {
        if (_dispatcher.CheckAccess())
        {
            action().GetAwaiter().GetResult();
            return;
        }

        var operation = _dispatcher.InvokeAsync(action).GetAwaiter().GetResult();
        operation.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _playback.PropertyChanged -= PlaybackOnPropertyChanged;
    }
}
