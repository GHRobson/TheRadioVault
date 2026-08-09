using AVFoundation;
using CoreMedia;
using Foundation;
using TheRadioVault.Client.Mobile.Platform;

namespace TheRadioVault.Client.iOS;

public sealed class IosAvPlayerEngine : IMobilePlaybackEngine, IMobileStreamingPlaybackEngine, IMobilePlaybackDiagnostics
{
    private readonly object _gate = new();
    private readonly Timer _timer;
    private AVPlayer? _player;
    private AVUrlAsset? _streamAsset;
    private IosMediaResourceLoader? _streamLoader;
    private CoreFoundation.DispatchQueue? _streamQueue;
    private NSObject? _endObserver;
    private double _rate = 1d;
    private bool _muted;
    private bool _playRequested;
    private string _lastDiagnosticState = string.Empty;
    private MobilePlaybackSnapshot _current = new(false, false, TimeSpan.Zero, null);
    private bool _disposed;

    public IosAvPlayerEngine()
    {
        var audio = AVAudioSession.SharedInstance();
        audio.SetCategory(AVAudioSessionCategory.Playback);
        audio.SetActive(true);
        _timer = new Timer(Poll, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event EventHandler<MobilePlaybackSnapshot>? StateChanged;
    public event EventHandler? MediaEnded;
    public MobilePlaybackSnapshot Current { get { lock (_gate) return _current; } }

    public void WritePlaybackDiagnostic(string message) => IosPlaybackDiagnostics.Write(message);

    public void Open(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("A media URL is required.", nameof(url));
        lock (_gate)
        {
            ThrowIfDisposed();
            DisposePlayerLocked();
            using var nativeUrl = NSUrl.FromString(url)
                ?? throw new InvalidOperationException("The media proxy returned an invalid URL.");
            _player = AVPlayer.FromUrl(nativeUrl);
            ConfigurePlayerLocked();
        }
    }

    public void Open(MobilePlaybackSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            ThrowIfDisposed();
            DisposePlayerLocked();
            using var nativeUrl = NSUrl.FromString(
                $"radiovault-media://stream/{Uri.EscapeDataString(source.Identifier)}")
                ?? throw new InvalidOperationException("Radio Vault could not create its native media source.");
            _streamAsset = new AVUrlAsset(nativeUrl);
            _streamLoader = new IosMediaResourceLoader(source);
            _streamQueue = new CoreFoundation.DispatchQueue("com.ghrobson.theradiovault.media-loader");
            _streamAsset.ResourceLoader.SetDelegate(_streamLoader, _streamQueue);
            using var item = AVPlayerItem.FromAsset(_streamAsset);
            _player = AVPlayer.FromPlayerItem(item);
            IosPlaybackDiagnostics.Write($"[RadioVault iOS playback] Opened native source {source.Identifier}");
            ConfigurePlayerLocked();
        }
    }

    private void ConfigurePlayerLocked()
    {
        if (_player is null) return;
        _player.AutomaticallyWaitsToMinimizeStalling = true;
        _player.Muted = _muted;
        if (_player.CurrentItem is { } item)
            _endObserver = AVPlayerItem.Notifications.ObserveDidPlayToEndTime(item, (_, _) => OnEnded());
        _current = new MobilePlaybackSnapshot(true, false, TimeSpan.Zero, null);
        _timer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
        PublishLocked();
    }

    public void Play()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_player is null) return;
            ActivateAudioSessionLocked();
            _playRequested = true;
            // Play() preserves the request while a remote item is still loading.
            // PlayImmediatelyAtRate() can leave Rate at zero for a live stream and
            // previously made Radio Vault report the temporary wait as a pause.
            _player.Play();
            if (Math.Abs(_rate - 1d) > 0.001d) _player.Rate = (float)_rate;
            IosPlaybackDiagnostics.Write($"[RadioVault iOS playback] Play requested at {_rate:0.##}x");
            UpdateLocked();
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _playRequested = false;
            _player?.Pause();
            UpdateLocked();
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_player is null) return;
            var seconds = Math.Max(0d, position.TotalSeconds);
            var duration = ReadDurationLocked();
            if (duration is { } total) seconds = Math.Min(seconds, total.TotalSeconds);
            _player.Seek(CMTime.FromSeconds(seconds, 1_000_000));
            UpdateLocked();
        }
    }

    public void SetRate(double rate)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _rate = Math.Clamp(rate, 0.5d, 3d);
            if (_player is not null && _playRequested) _player.Rate = (float)_rate;
        }
    }

    public void SetMuted(bool muted)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _muted = muted;
            if (_player is not null) _player.Muted = muted;
        }
    }

    private void Poll(object? state)
    {
        lock (_gate)
        {
            if (_disposed || _player is null) return;
            UpdateLocked();
        }
    }

    private void UpdateLocked()
    {
        if (_player is null)
        {
            _current = new MobilePlaybackSnapshot(false, false, TimeSpan.Zero, null);
            PublishLocked();
            return;
        }

        var error = _player.Error?.LocalizedDescription ?? _player.CurrentItem?.Error?.LocalizedDescription ?? string.Empty;
        if (_playRequested && string.IsNullOrWhiteSpace(error) &&
            _player.CurrentItem?.Status == AVPlayerItemStatus.ReadyToPlay &&
            _player.Rate <= 0.001f)
        {
            ActivateAudioSessionLocked();
            _player.PlayImmediatelyAtRate((float)_rate);
        }
        var seconds = _player.CurrentTime.Seconds;
        var diagnosticState = $"item={_player.CurrentItem?.Status}; control={_player.TimeControlStatus}; " +
                              $"rate={_player.Rate:0.##}; requested={_playRequested}; " +
                              $"waiting={_player.ReasonForWaitingToPlay ?? "none"}; error={error}";
        if (!string.Equals(diagnosticState, _lastDiagnosticState, StringComparison.Ordinal))
        {
            _lastDiagnosticState = diagnosticState;
            IosPlaybackDiagnostics.Write("[RadioVault iOS playback] " + diagnosticState);
        }
        var position = double.IsFinite(seconds) && seconds >= 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
        _current = new MobilePlaybackSnapshot(
            true,
            _playRequested && string.IsNullOrWhiteSpace(error),
            position,
            ReadDurationLocked(),
            error);
        PublishLocked();
    }

    private TimeSpan? ReadDurationLocked()
    {
        var seconds = _player?.CurrentItem?.Duration.Seconds ?? double.NaN;
        return double.IsFinite(seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
    }

    private void OnEnded()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _playRequested = false;
            UpdateLocked();
        }
        MediaEnded?.Invoke(this, EventArgs.Empty);
    }

    private void PublishLocked() => StateChanged?.Invoke(this, _current);

    private void DisposePlayerLocked()
    {
        _playRequested = false;
        _endObserver?.Dispose();
        _endObserver = null;
        _player?.Pause();
        _player?.Dispose();
        _player = null;
        _streamAsset?.ResourceLoader.SetDelegate(null, null);
        _streamLoader?.Dispose();
        _streamLoader = null;
        _streamAsset?.Dispose();
        _streamAsset = null;
        _streamQueue?.Dispose();
        _streamQueue = null;
        _lastDiagnosticState = string.Empty;
    }

    private static void ActivateAudioSessionLocked()
    {
        var audio = AVAudioSession.SharedInstance();
        audio.SetCategory(AVAudioSessionCategory.Playback);
        audio.SetActive(true);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            DisposePlayerLocked();
        }
    }
}
