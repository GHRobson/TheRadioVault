using Foundation;
using MediaPlayer;
using TheRadioVault.Client.Mobile.Platform;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class IosNowPlayingService : IMobileNowPlayingService
{
    private readonly object _gate = new();
    private readonly MPRemoteCommandCenter _commandCenter;
    private readonly List<(MPRemoteCommand Command, NSObject Token)> _targets = [];
    private PendingNowPlayingUpdate? _pendingUpdate;
    private bool _updateScheduled;
    private bool _disposed;
    private MPMediaItemArtwork? _artwork;
    private UIImage? _artworkImage;
    private byte[]? _artworkBytes;

    public IosNowPlayingService()
    {
        _commandCenter = MPRemoteCommandCenter.Shared;
        _commandCenter.SkipBackwardCommand.PreferredIntervals = [15d];
        _commandCenter.SkipForwardCommand.PreferredIntervals = [30d];
        SetCommandsEnabled(available: false, isPlaying: false);

        Register(_commandCenter.PlayCommand, _ => Raise(MobileRemoteCommandKind.Play));
        Register(_commandCenter.PauseCommand, _ => Raise(MobileRemoteCommandKind.Pause));
        Register(_commandCenter.TogglePlayPauseCommand, _ => Raise(MobileRemoteCommandKind.TogglePlayPause));
        Register(_commandCenter.SkipBackwardCommand, _ => Raise(MobileRemoteCommandKind.SkipBack));
        Register(_commandCenter.SkipForwardCommand, _ => Raise(MobileRemoteCommandKind.SkipForward));
        Register(_commandCenter.ChangePlaybackPositionCommand, command =>
        {
            if (command is not MPChangePlaybackPositionCommandEvent position)
                return MPRemoteCommandHandlerStatus.CommandFailed;
            return Raise(MobileRemoteCommandKind.Seek, TimeSpan.FromSeconds(Math.Max(0, position.PositionTime)));
        });
    }

    public event EventHandler<MobileRemoteCommand>? CommandReceived;

    public void Update(MobileNowPlayingSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);
        QueueUpdate(new PendingNowPlayingUpdate(snapshot.IsAvailable ? snapshot : null));
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }
        QueueUpdate(new PendingNowPlayingUpdate(null));
    }

    private void QueueUpdate(PendingNowPlayingUpdate update)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pendingUpdate = update;
            if (_updateScheduled) return;
            _updateScheduled = true;
        }

        UIApplication.SharedApplication.BeginInvokeOnMainThread(ApplyPendingUpdate);
    }

    private void ApplyPendingUpdate()
    {
        PendingNowPlayingUpdate? update;
        lock (_gate)
        {
            if (_disposed)
            {
                _pendingUpdate = null;
                _updateScheduled = false;
                return;
            }
            update = _pendingUpdate;
            _pendingUpdate = null;
            _updateScheduled = false;
        }

        var snapshot = update?.Snapshot;
        if (snapshot is null)
        {
            MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = null!;
            SetCommandsEnabled(available: false, isPlaying: false);
            ReleaseArtwork();
            return;
        }

        var info = new MPNowPlayingInfo
        {
            Title = snapshot.Title,
            Artist = snapshot.Subtitle,
            PlaybackDuration = Math.Max(0, snapshot.Duration.TotalSeconds),
            ElapsedPlaybackTime = Math.Max(0, snapshot.Position.TotalSeconds),
            PlaybackRate = snapshot.IsPlaying ? snapshot.Rate : 0d,
            DefaultPlaybackRate = snapshot.Rate
        };
        var artwork = ResolveArtwork(snapshot.Artwork);
        if (artwork is not null) info.Artwork = artwork;
        MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = info;
        SetCommandsEnabled(available: true, isPlaying: snapshot.IsPlaying, isLive: snapshot.IsLive);
    }

    private MPMediaItemArtwork? ResolveArtwork(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            ReleaseArtwork();
            return null;
        }
        if (_artwork is not null && ArtworkMatches(bytes)) return _artwork;

        using var data = NSData.FromArray(bytes);
        var image = UIImage.LoadFromData(data);
        var artwork = image is null ? null : new MPMediaItemArtwork(image.Size, _ => image);
        ReleaseArtwork();
        _artworkBytes = bytes.ToArray();
        _artworkImage = image;
        _artwork = artwork;
        return _artwork;
    }

    private bool ArtworkMatches(byte[] bytes)
        => ReferenceEquals(bytes, _artworkBytes)
           || (_artworkBytes is { Length: > 0 } current && bytes.AsSpan().SequenceEqual(current));

    private void ReleaseArtwork()
    {
        _artwork?.Dispose();
        _artwork = null;
        _artworkImage?.Dispose();
        _artworkImage = null;
        _artworkBytes = null;
    }

    private void SetCommandsEnabled(bool available, bool isPlaying, bool isLive = false)
    {
        _commandCenter.PlayCommand.Enabled = available && !isPlaying;
        _commandCenter.PauseCommand.Enabled = available && isPlaying;
        _commandCenter.TogglePlayPauseCommand.Enabled = available;
        _commandCenter.SkipBackwardCommand.Enabled = available && !isLive;
        _commandCenter.SkipForwardCommand.Enabled = available && !isLive;
        _commandCenter.ChangePlaybackPositionCommand.Enabled = available && !isLive;
    }

    private void Register(MPRemoteCommand command, Func<MPRemoteCommandEvent, MPRemoteCommandHandlerStatus> handler)
    {
        var token = command.AddTarget(handler);
        _targets.Add((command, token));
    }

    private MPRemoteCommandHandlerStatus Raise(MobileRemoteCommandKind kind, TimeSpan? position = null)
    {
        lock (_gate)
        {
            if (_disposed) return MPRemoteCommandHandlerStatus.CommandFailed;
        }

        var command = new MobileRemoteCommand(kind, position);
        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            lock (_gate)
            {
                if (_disposed) return;
            }
            CommandReceived?.Invoke(this, command);
        });
        return MPRemoteCommandHandlerStatus.Success;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pendingUpdate = null;
            _updateScheduled = false;
        }

        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            foreach (var (command, token) in _targets) command.RemoveTarget(token);
            _targets.Clear();
            MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = null!;
            SetCommandsEnabled(available: false, isPlaying: false);
            ReleaseArtwork();
        });
    }

    private sealed record PendingNowPlayingUpdate(MobileNowPlayingSnapshot? Snapshot);
}
