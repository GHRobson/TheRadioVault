using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using TheRadioVault.Core.Playback;

namespace TheRadioVault.Desktop.Avalonia.Playback;

/// <summary>
/// Avalonia's Windows local-audio engine. Decoded audio is sent through the
/// modern shared-mode WASAPI path used by current Windows applications. At
/// normal speed the decoder is connected directly to the output so the speed
/// adapter cannot alter the stream.
/// </summary>
public sealed class NAudioPlaybackEngine : IPlaybackEngine
{
    private readonly object _gate = new();
    private readonly Timer _stateTimer;
    private WaveStream? _reader;
    private WasapiOut? _output;
    private VolumeSampleProvider? _volumeProvider;
    private PlaybackStatus _status = PlaybackStatus.Stopped;
    private string? _mediaPath;
    private double _volume = 0.8d;
    private double _speed = 1d;
    private bool _disposed;

    public NAudioPlaybackEngine()
    {
        _stateTimer = new Timer(_ =>
        {
            lock (_gate)
            {
                if (_disposed || _reader is null || _status != PlaybackStatus.Playing) return;
                RaiseStateChangedLocked();
            }
        }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler? MediaOpened;
    public event EventHandler? MediaEnded;
    public event EventHandler<PlaybackErrorEventArgs>? MediaFailed;
    public event EventHandler<PlaybackEngineSnapshot>? StateChanged;

    public PlaybackStatus Status { get { lock (_gate) return _status; } }
    public bool IsPlaying { get { lock (_gate) return _status == PlaybackStatus.Playing; } }
    public string? MediaPath { get { lock (_gate) return _mediaPath; } }

    public TimeSpan Position
    {
        get { lock (_gate) return _reader?.CurrentTime ?? TimeSpan.Zero; }
        set
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_reader is null) return;
                var target = ClampLocked(value);
                if (_output is null || _status != PlaybackStatus.Playing)
                {
                    // Before playback begins, or while paused, no callback is reading
                    // the shared stream. Move directly instead of constructing a second
                    // WaveOut pipeline merely to seek to the requested resume point.
                    _reader.CurrentTime = target;
                }
                else
                {
                    // During active playback, rebuild around the target so one final
                    // buffered read cannot overwrite the seek.
                    RebuildOutputLocked(preservePlayback: true, seekPosition: target);
                }
                RaiseStateChangedLocked();
            }
        }
    }

    public TimeSpan? Duration
    {
        get
        {
            lock (_gate)
                return _reader is null || _reader.TotalTime <= TimeSpan.Zero ? null : _reader.TotalTime;
        }
    }

    public double Volume
    {
        get { lock (_gate) return _volume; }
        set
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                _volume = Math.Clamp(value, 0d, 1d);
                if (_volumeProvider is not null) _volumeProvider.Volume = (float)_volume;
                RaiseStateChangedLocked();
            }
        }
    }

    public double Speed
    {
        get { lock (_gate) return _speed; }
        set
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                var speed = Math.Clamp(value, 0.5d, 3d);
                if (Math.Abs(speed - _speed) < 0.001d) return;
                _speed = speed;
                if (_reader is not null && _output is not null) RebuildOutputLocked(preservePlayback: true);
                RaiseStateChangedLocked();
            }
        }
    }

    public void Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A media path is required.", nameof(path));
        var isRemote = Uri.TryCreate(path, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        if (!isRemote)
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException("The audio file is not available offline.", path);
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            try
            {
                DisposeMediaLocked();
                _status = PlaybackStatus.Opening;
                _mediaPath = path;
                RaiseStateChangedLocked();
                _reader = CreateReader(path);
                // The WaveOut device is created lazily by Play after speed, volume and
                // resume position have been applied. This avoids building the output
                // pipeline two or three times for every ordinary playback start.
                _status = PlaybackStatus.Paused;
                _stateTimer.Change(250, 250);
                MediaOpened?.Invoke(this, EventArgs.Empty);
                RaiseStateChangedLocked();
            }
            catch (Exception exception)
            {
                _status = PlaybackStatus.Failed;
                MediaFailed?.Invoke(this, new PlaybackErrorEventArgs(exception));
                RaiseStateChangedLocked();
                throw;
            }
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_reader is null) return;
            try
            {
                if (_reader.Position >= _reader.Length) _reader.Position = 0;
                if (_output is null)
                {
                    _status = PlaybackStatus.Buffering;
                    RaiseStateChangedLocked();
                    RebuildOutputLocked(preservePlayback: false);
                }
                _output?.Play();
                _status = PlaybackStatus.Playing;
                RaiseStateChangedLocked();
            }
            catch (Exception exception)
            {
                _status = PlaybackStatus.Failed;
                MediaFailed?.Invoke(this, new PlaybackErrorEventArgs(exception));
                RaiseStateChangedLocked();
                throw;
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_reader is null) return;
            _output?.Pause();
            _status = PlaybackStatus.Paused;
            RaiseStateChangedLocked();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_reader is null) return;
            if (_output is not null)
            {
                _output.PlaybackStopped -= OutputOnPlaybackStopped;
                try { _output.Stop(); }
                finally { _output.PlaybackStopped += OutputOnPlaybackStopped; }
            }
            _reader.Position = 0;
            _status = PlaybackStatus.Stopped;
            RaiseStateChangedLocked();
        }
    }

    public void Skip(TimeSpan amount) => Position = Position + amount;

    private static WaveStream CreateReader(string path)
    {
        var isRemote = Uri.TryCreate(path, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        if (isRemote) return new MediaFoundationReader(path);
        try { return new AudioFileReader(path); }
        catch { return new MediaFoundationReader(path); }
    }

    private void RebuildOutputLocked(bool preservePlayback, TimeSpan? seekPosition = null)
    {
        if (_reader is null) return;
        var wasPlaying = preservePlayback && _status == PlaybackStatus.Playing;
        var old = _output;
        _output = null;
        _volumeProvider = null;
        if (old is not null)
        {
            old.PlaybackStopped -= OutputOnPlaybackStopped;
            try { old.Stop(); }
            catch { }
            old.Dispose();
        }

        if (seekPosition.HasValue)
            _reader.CurrentTime = ClampLocked(seekPosition.Value);

        var output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 100);
        output.PlaybackStopped += OutputOnPlaybackStopped;
        IWaveProvider source = Math.Abs(_speed - 1d) < 0.001d
            ? _reader
            : new RateAdjustedWaveProvider(_reader, _speed);
        // Apply Radio Vault's slider and transactional handoff mute within the
        // decoded stream. WasapiOut.Volume controls Windows' per-application audio
        // session; assigning it whenever a handoff rebuilt the output could replace
        // the quieter level chosen in Windows Volume Mixer with our 80% default.
        // Leaving the WASAPI session alone also keeps system/device volume strictly
        // local to this computer.
        var volumeProvider = new VolumeSampleProvider(source.ToSampleProvider())
        {
            Volume = (float)_volume
        };
        output.Init(volumeProvider.ToWaveProvider());
        _output = output;
        _volumeProvider = volumeProvider;
        if (wasPlaying) output.Play();
    }

    private void OutputOnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _output)) return;
            if (e.Exception is not null)
            {
                _status = PlaybackStatus.Failed;
                MediaFailed?.Invoke(this, new PlaybackErrorEventArgs(e.Exception));
                RaiseStateChangedLocked();
                return;
            }

            if (_reader is not null && _reader.Length > 0 && _reader.Position >= _reader.Length - Math.Min(_reader.WaveFormat.AverageBytesPerSecond / 4, 8192))
            {
                _status = PlaybackStatus.Ended;
                MediaEnded?.Invoke(this, EventArgs.Empty);
            }
            else if (_status == PlaybackStatus.Playing)
            {
                _status = PlaybackStatus.Paused;
            }
            RaiseStateChangedLocked();
        }
    }

    private TimeSpan ClampLocked(TimeSpan value)
    {
        if (value < TimeSpan.Zero) return TimeSpan.Zero;
        if (_reader is not null && _reader.TotalTime > TimeSpan.Zero && value > _reader.TotalTime) return _reader.TotalTime;
        return value;
    }

    private void RaiseStateChangedLocked()
    {
        var snapshot = new PlaybackEngineSnapshot(
            _status,
            _status == PlaybackStatus.Playing,
            _reader?.CurrentTime ?? TimeSpan.Zero,
            _reader is null || _reader.TotalTime <= TimeSpan.Zero ? null : _reader.TotalTime,
            _volume,
            _speed,
            _mediaPath);
        StateChanged?.Invoke(this, snapshot);
    }

    private void DisposeMediaLocked()
    {
        _stateTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (_output is not null)
        {
            _output.PlaybackStopped -= OutputOnPlaybackStopped;
            try { _output.Stop(); }
            catch { }
            _output.Dispose();
            _output = null;
        }
        _volumeProvider = null;
        _reader?.Dispose();
        _reader = null;
        _mediaPath = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NAudioPlaybackEngine));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeMediaLocked();
            _status = PlaybackStatus.Stopped;
        }
        _stateTimer.Dispose();
    }

    private sealed class RateAdjustedWaveProvider : IWaveProvider
    {
        private readonly IWaveProvider _source;

        public RateAdjustedWaveProvider(IWaveProvider source, double speed)
        {
            _source = source;
            var sourceFormat = source.WaveFormat;
            var sampleRate = Math.Clamp((int)Math.Round(sourceFormat.SampleRate * Math.Clamp(speed, 0.5d, 3d)), 8000, 192000);
            WaveFormat = sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat
                ? WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, sourceFormat.Channels)
                : new WaveFormat(sampleRate, sourceFormat.BitsPerSample, sourceFormat.Channels);
        }

        public WaveFormat WaveFormat { get; }
        public int Read(byte[] buffer, int offset, int count) => _source.Read(buffer, offset, count);
    }
}
