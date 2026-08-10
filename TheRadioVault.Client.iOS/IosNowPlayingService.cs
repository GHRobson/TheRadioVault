using Foundation;
using MediaPlayer;
using TheRadioVault.Client.Mobile.Platform;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class IosNowPlayingService : IMobileNowPlayingService
{
    private readonly List<(MPRemoteCommand Command, NSObject Token)> _targets = [];
    private bool _disposed;
    private MPMediaItemArtwork? _artwork;
    private UIImage? _artworkImage;
    private int _artworkHash;

    public IosNowPlayingService()
    {
        var center = MPRemoteCommandCenter.Shared;
        center.PlayCommand.Enabled = true;
        center.PauseCommand.Enabled = true;
        center.TogglePlayPauseCommand.Enabled = true;
        center.SkipBackwardCommand.Enabled = true;
        center.SkipForwardCommand.Enabled = true;
        center.ChangePlaybackPositionCommand.Enabled = true;
        center.SkipBackwardCommand.PreferredIntervals = [15d];
        center.SkipForwardCommand.PreferredIntervals = [30d];

        Register(center.PlayCommand, _ => Raise(MobileRemoteCommandKind.Play));
        Register(center.PauseCommand, _ => Raise(MobileRemoteCommandKind.Pause));
        Register(center.TogglePlayPauseCommand, _ => Raise(MobileRemoteCommandKind.TogglePlayPause));
        Register(center.SkipBackwardCommand, _ => Raise(MobileRemoteCommandKind.SkipBack));
        Register(center.SkipForwardCommand, _ => Raise(MobileRemoteCommandKind.SkipForward));
        Register(center.ChangePlaybackPositionCommand, command =>
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
        if (!snapshot.IsAvailable)
        {
            Clear();
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
        if (snapshot.Artwork is { Length: > 0 } bytes)
        {
            var hash = HashCode.Combine(bytes.Length, bytes[0], bytes[^1]);
            if (_artwork is null || hash != _artworkHash)
            {
                using var data = NSData.FromArray(bytes);
                var image = UIImage.LoadFromData(data);
                _artwork?.Dispose();
                _artworkImage?.Dispose();
                _artworkImage = image;
                _artwork = image is null ? null : new MPMediaItemArtwork(image.Size, _ => image);
                _artworkHash = hash;
            }
            info.Artwork = _artwork;
        }
        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (!_disposed) MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = info;
        });
    }

    public void Clear()
    {
        if (_disposed) return;
        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = null!);
    }

    private void Register(MPRemoteCommand command, Func<MPRemoteCommandEvent, MPRemoteCommandHandlerStatus> handler)
    {
        var token = command.AddTarget(handler);
        _targets.Add((command, token));
    }

    private MPRemoteCommandHandlerStatus Raise(MobileRemoteCommandKind kind, TimeSpan? position = null)
    {
        if (_disposed) return MPRemoteCommandHandlerStatus.CommandFailed;
        CommandReceived?.Invoke(this, new MobileRemoteCommand(kind, position));
        return MPRemoteCommandHandlerStatus.Success;
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var (command, token) in _targets) command.RemoveTarget(token);
        _targets.Clear();
        Clear();
        _artwork?.Dispose();
        _artwork = null;
        _artworkImage?.Dispose();
        _artworkImage = null;
        _disposed = true;
    }
}
