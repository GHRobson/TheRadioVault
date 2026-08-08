using System.Runtime.InteropServices;
using System.Text;
using TheRadioVault.Core.Playback;

namespace TheRadioVault.Desktop.Avalonia.Playback;

/// <summary>
/// Native macOS audio playback backed by AVPlayer. Radio Vault's authenticated
/// server stream is exposed through the existing private loopback media proxy,
/// so AVFoundation never receives the pairing token or pinned-server details.
/// </summary>
public sealed class MacAvFoundationPlaybackEngine : IPlaybackEngine
{
    private readonly object _gate = new();
    private readonly Timer _stateTimer;
    private nint _player;
    private PlaybackStatus _status = PlaybackStatus.Stopped;
    private string? _mediaPath;
    private double _volume = 0.8d;
    private double _speed = 1d;
    private bool _playRequested;
    private bool _mediaOpenedRaised;
    private bool _mediaEndedRaised;
    private bool _disposed;

    public MacAvFoundationPlaybackEngine()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("AVFoundation playback is available only on macOS.");

        MacNative.EnsureFrameworksLoaded();
        _stateTimer = new Timer(PollState, null, Timeout.Infinite, Timeout.Infinite);
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
        get
        {
            lock (_gate)
                return _player == 0 ? TimeSpan.Zero : MacNative.GetPosition(_player);
        }
        set
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_player == 0) return;
                var target = ClampLocked(value);
                MacNative.Seek(_player, target);
                _mediaEndedRaised = false;
                if (_status == PlaybackStatus.Ended)
                    _status = _playRequested ? PlaybackStatus.Buffering : PlaybackStatus.Paused;
                RaiseStateChangedLocked();
            }
        }
    }

    public TimeSpan? Duration
    {
        get
        {
            lock (_gate)
                return _player == 0 ? null : MacNative.GetDuration(_player);
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
                if (_player != 0) MacNative.SetVolume(_player, _volume);
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
                _speed = Math.Clamp(value, 0.5d, 3d);
                if (_player != 0 && _playRequested) MacNative.SetRate(_player, _speed);
                RaiseStateChangedLocked();
            }
        }
    }

    public void Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A media path is required.", nameof(path));

        var isRemote = Uri.TryCreate(path, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        if (!isRemote)
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path))
                throw new FileNotFoundException("The audio file is not available offline.", path);
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            try
            {
                DisposePlayerLocked();
                _status = PlaybackStatus.Opening;
                _mediaPath = path;
                _playRequested = false;
                _mediaOpenedRaised = false;
                _mediaEndedRaised = false;
                RaiseStateChangedLocked();

                _player = MacNative.CreatePlayer(path, isRemote);
                MacNative.SetVolume(_player, _volume);
                _stateTimer.Change(50, 250);
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
            if (_player == 0) return;
            try
            {
                if (_status == PlaybackStatus.Ended)
                    MacNative.Seek(_player, TimeSpan.Zero);
                _mediaEndedRaised = false;
                _playRequested = true;
                _status = _mediaOpenedRaised ? PlaybackStatus.Playing : PlaybackStatus.Buffering;
                MacNative.Play(_player, _speed);
                RaiseStateChangedLocked();
            }
            catch (Exception exception)
            {
                FailLocked(exception);
                throw;
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_player == 0) return;
            _playRequested = false;
            MacNative.Pause(_player);
            _status = PlaybackStatus.Paused;
            RaiseStateChangedLocked();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_player == 0) return;
            _playRequested = false;
            MacNative.Pause(_player);
            MacNative.Seek(_player, TimeSpan.Zero);
            _mediaEndedRaised = false;
            _status = PlaybackStatus.Stopped;
            RaiseStateChangedLocked();
        }
    }

    public void Skip(TimeSpan amount) => Position = Position + amount;

    private void PollState(object? state)
    {
        lock (_gate)
        {
            if (_disposed || _player == 0) return;
            try
            {
                var itemStatus = MacNative.GetItemStatus(_player);
                if (itemStatus == MacNative.ItemStatusFailed)
                {
                    FailLocked(new InvalidOperationException(MacNative.GetItemError(_player)
                        ?? "AVFoundation could not open this broadcast."));
                    return;
                }

                if (itemStatus == MacNative.ItemStatusReady && !_mediaOpenedRaised)
                {
                    _mediaOpenedRaised = true;
                    _status = _playRequested ? PlaybackStatus.Playing : PlaybackStatus.Paused;
                    if (_playRequested) MacNative.Play(_player, _speed);
                    MediaOpened?.Invoke(this, EventArgs.Empty);
                }
                else if (_mediaOpenedRaised && _playRequested)
                {
                    _status = MacNative.GetRate(_player) > 0.001d
                        ? PlaybackStatus.Playing
                        : PlaybackStatus.Buffering;
                }

                var position = MacNative.GetPosition(_player);
                var duration = MacNative.GetDuration(_player);
                if (_playRequested && duration is TimeSpan total && total > TimeSpan.Zero
                    && position >= total - TimeSpan.FromMilliseconds(250))
                {
                    _playRequested = false;
                    _status = PlaybackStatus.Ended;
                    if (!_mediaEndedRaised)
                    {
                        _mediaEndedRaised = true;
                        MediaEnded?.Invoke(this, EventArgs.Empty);
                    }
                }

                RaiseStateChangedLocked();
            }
            catch (Exception exception)
            {
                FailLocked(exception);
            }
        }
    }

    private TimeSpan ClampLocked(TimeSpan value)
    {
        if (value < TimeSpan.Zero) return TimeSpan.Zero;
        var duration = _player == 0 ? null : MacNative.GetDuration(_player);
        return duration is TimeSpan total && total > TimeSpan.Zero && value > total ? total : value;
    }

    private void FailLocked(Exception exception)
    {
        _playRequested = false;
        _status = PlaybackStatus.Failed;
        MediaFailed?.Invoke(this, new PlaybackErrorEventArgs(exception));
        RaiseStateChangedLocked();
    }

    private void RaiseStateChangedLocked()
    {
        var position = _player == 0 ? TimeSpan.Zero : MacNative.GetPosition(_player);
        var duration = _player == 0 ? null : MacNative.GetDuration(_player);
        StateChanged?.Invoke(this, new PlaybackEngineSnapshot(
            _status,
            _status == PlaybackStatus.Playing,
            position,
            duration,
            _volume,
            _speed,
            _mediaPath));
    }

    private void DisposePlayerLocked()
    {
        _stateTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (_player != 0)
        {
            try { MacNative.Pause(_player); }
            catch { }
            MacNative.Release(_player);
            _player = 0;
        }
        _mediaPath = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MacAvFoundationPlaybackEngine));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DisposePlayerLocked();
            _status = PlaybackStatus.Stopped;
        }
        _stateTimer.Dispose();
    }

    private static class MacNative
    {
        internal const long ItemStatusReady = 1;
        internal const long ItemStatusFailed = 2;

        private const string LibObjC = "/usr/lib/libobjc.A.dylib";
        private const string CoreMedia = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
        private const string AVFoundation = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";

        private static readonly nint SelPlayerWithUrl = Selector("playerWithURL:");
        private static readonly nint SelUrlWithString = Selector("URLWithString:");
        private static readonly nint SelFileUrlWithPath = Selector("fileURLWithPath:");
        private static readonly nint SelStringWithUtf8 = Selector("stringWithUTF8String:");
        private static readonly nint SelRetain = Selector("retain");
        private static readonly nint SelRelease = Selector("release");
        private static readonly nint SelCurrentItem = Selector("currentItem");
        private static readonly nint SelStatus = Selector("status");
        private static readonly nint SelError = Selector("error");
        private static readonly nint SelLocalizedDescription = Selector("localizedDescription");
        private static readonly nint SelUtf8String = Selector("UTF8String");
        private static readonly nint SelPlay = Selector("play");
        private static readonly nint SelPause = Selector("pause");
        private static readonly nint SelRate = Selector("rate");
        private static readonly nint SelSetRate = Selector("setRate:");
        private static readonly nint SelSetVolume = Selector("setVolume:");
        private static readonly nint SelCurrentTime = Selector("currentTime");
        private static readonly nint SelDuration = Selector("duration");
        private static readonly nint SelSeekToTime = Selector("seekToTime:");
        private static nint _frameworkHandle;

        internal static void EnsureFrameworksLoaded()
        {
            if (_frameworkHandle != 0) return;
            _frameworkHandle = NativeLibrary.Load(AVFoundation);
        }

        internal static nint CreatePlayer(string path, bool remote)
        {
            var pool = AutoreleasePoolPush();
            try
            {
                var text = CreateString(path);
                var urlClass = GetClass("NSURL");
                var url = remote
                    ? SendIntPtrIntPtr(urlClass, SelUrlWithString, text)
                    : SendIntPtrIntPtr(urlClass, SelFileUrlWithPath, text);
                if (url == 0) throw new InvalidOperationException("macOS could not create the broadcast URL.");

                var playerClass = GetClass("AVPlayer");
                var player = SendIntPtrIntPtr(playerClass, SelPlayerWithUrl, url);
                if (player == 0) throw new InvalidOperationException("macOS could not create the AVPlayer instance.");
                return SendIntPtr(player, SelRetain);
            }
            finally
            {
                AutoreleasePoolPop(pool);
            }
        }

        internal static void Play(nint player, double rate)
        {
            SendVoid(player, SelPlay);
            SetRate(player, rate);
        }

        internal static void Pause(nint player) => SendVoid(player, SelPause);
        internal static void SetRate(nint player, double value) => SendVoidFloat(player, SelSetRate, (float)value);
        internal static double GetRate(nint player) => SendFloat(player, SelRate);
        internal static void SetVolume(nint player, double value) => SendVoidFloat(player, SelSetVolume, (float)value);

        internal static TimeSpan GetPosition(nint player)
            => ToTimeSpan(GetTime(player, SelCurrentTime)) ?? TimeSpan.Zero;

        internal static TimeSpan? GetDuration(nint player)
        {
            var item = SendIntPtr(player, SelCurrentItem);
            return item == 0 ? null : ToTimeSpan(GetTime(item, SelDuration));
        }

        internal static void Seek(nint player, TimeSpan position)
            => SendVoidTime(player, SelSeekToTime, CMTimeMakeWithSeconds(Math.Max(0d, position.TotalSeconds), 600));

        internal static long GetItemStatus(nint player)
        {
            var item = SendIntPtr(player, SelCurrentItem);
            return item == 0 ? 0 : SendIntPtr(item, SelStatus).ToInt64();
        }

        internal static string? GetItemError(nint player)
        {
            var pool = AutoreleasePoolPush();
            try
            {
                var item = SendIntPtr(player, SelCurrentItem);
                var error = item == 0 ? 0 : SendIntPtr(item, SelError);
                var description = error == 0 ? 0 : SendIntPtr(error, SelLocalizedDescription);
                var utf8 = description == 0 ? 0 : SendIntPtr(description, SelUtf8String);
                return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
            }
            finally
            {
                AutoreleasePoolPop(pool);
            }
        }

        internal static void Release(nint instance)
        {
            if (instance != 0) SendVoid(instance, SelRelease);
        }

        private static TimeSpan? ToTimeSpan(CMTime time)
        {
            if ((time.Flags & 1u) == 0) return null;
            var seconds = CMTimeGetSeconds(time);
            return double.IsFinite(seconds) && seconds >= 0d ? TimeSpan.FromSeconds(seconds) : null;
        }

        private static CMTime GetTime(nint receiver, nint selector)
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            {
                SendTimeIntel(out var value, receiver, selector);
                return value;
            }
            return SendTimeArm(receiver, selector);
        }

        private static nint CreateString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                return SendIntPtrIntPtr(GetClass("NSString"), SelStringWithUtf8, handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        private static nint GetClass(string name)
        {
            var value = ObjCGetClass(name);
            return value == 0 ? throw new InvalidOperationException($"The macOS class '{name}' is unavailable.") : value;
        }

        private static nint Selector(string name) => SelRegisterName(name);

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct CMTime
        {
            public readonly long Value;
            public readonly int Timescale;
            public readonly uint Flags;
            public readonly long Epoch;
        }

        [DllImport(LibObjC, EntryPoint = "objc_getClass")]
        private static extern nint ObjCGetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(LibObjC, EntryPoint = "sel_registerName")]
        private static extern nint SelRegisterName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(LibObjC, EntryPoint = "objc_autoreleasePoolPush")]
        private static extern nint AutoreleasePoolPush();

        [DllImport(LibObjC, EntryPoint = "objc_autoreleasePoolPop")]
        private static extern void AutoreleasePoolPop(nint pool);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern nint SendIntPtr(nint receiver, nint selector);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern nint SendIntPtrIntPtr(nint receiver, nint selector, nint argument);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendVoid(nint receiver, nint selector);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendVoidFloat(nint receiver, nint selector, float value);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern float SendFloat(nint receiver, nint selector);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern CMTime SendTimeArm(nint receiver, nint selector);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend_stret")]
        private static extern void SendTimeIntel(out CMTime result, nint receiver, nint selector);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendVoidTime(nint receiver, nint selector, CMTime time);

        [DllImport(CoreMedia)]
        private static extern CMTime CMTimeMakeWithSeconds(double seconds, int preferredTimescale);

        [DllImport(CoreMedia)]
        private static extern double CMTimeGetSeconds(CMTime time);
    }
}
