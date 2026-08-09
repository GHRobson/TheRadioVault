using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TheRadioVault.Core.Playback;

namespace TheRadioVault.Desktop.Avalonia.Playback;

/// <summary>
/// Linux playback through mpv's local JSON IPC socket. Pairing credentials stay
/// inside Radio Vault's loopback media proxy; mpv receives only the private local
/// stream URL or an offline file path. Server credentials are never passed to mpv.
/// </summary>
public sealed class LinuxMpvPlaybackEngine : IPlaybackEngine
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly Timer _stateTimer;
    private Process? _process;
    private string _ipcPath = string.Empty;
    private PlaybackStatus _status = PlaybackStatus.Stopped;
    private string? _mediaPath;
    private TimeSpan _position;
    private TimeSpan? _duration;
    private double _volume = 0.8d;
    private double _speed = 1d;
    private bool _mediaEndedRaised;
    private bool _disposed;

    public LinuxMpvPlaybackEngine()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("mpv playback is available only on Linux.");
        _stateTimer = new Timer(_ => _ = PollStateAsync(), null, Timeout.Infinite, Timeout.Infinite);
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
        get { lock (_gate) return _position; }
        set
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_mediaPath is null) return;
                var target = ClampLocked(value);
                SendCommandLocked("seek", target.TotalSeconds, "absolute+exact");
                _position = target;
                _mediaEndedRaised = false;
                RaiseStateChangedLocked();
            }
        }
    }

    public TimeSpan? Duration { get { lock (_gate) return _duration; } }

    public double Volume
    {
        get { lock (_gate) return _volume; }
        set
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                _volume = Math.Clamp(value, 0d, 1d);
                if (IsProcessAvailableLocked()) SendCommandLocked("set_property", "volume", _volume * 100d);
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
                if (IsProcessAvailableLocked()) SendCommandLocked("set_property", "speed", _speed);
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
                EnsureProcessLocked();
                _status = PlaybackStatus.Opening;
                _mediaPath = path;
                _position = TimeSpan.Zero;
                _duration = null;
                _mediaEndedRaised = false;
                RaiseStateChangedLocked();
                SendCommandLocked("loadfile", path, "replace");
                SendCommandLocked("set_property", "pause", true);
                SendCommandLocked("set_property", "volume", _volume * 100d);
                SendCommandLocked("set_property", "speed", _speed);
                _status = PlaybackStatus.Paused;
                _stateTimer.Change(250, 250);
                MediaOpened?.Invoke(this, EventArgs.Empty);
                RaiseStateChangedLocked();
            }
            catch (Exception exception)
            {
                FailLocked(exception);
                throw;
            }
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_mediaPath is null) return;
            try
            {
                if (_status == PlaybackStatus.Ended) SendCommandLocked("seek", 0d, "absolute+exact");
                _mediaEndedRaised = false;
                SendCommandLocked("set_property", "pause", false);
                _status = PlaybackStatus.Playing;
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
            if (_mediaPath is null) return;
            SendCommandLocked("set_property", "pause", true);
            _status = PlaybackStatus.Paused;
            RaiseStateChangedLocked();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_mediaPath is null) return;
            SendCommandLocked("stop");
            _position = TimeSpan.Zero;
            _duration = null;
            _status = PlaybackStatus.Stopped;
            RaiseStateChangedLocked();
        }
    }

    public void Skip(TimeSpan amount) => Position = Position + amount;

    private void EnsureProcessLocked()
    {
        if (IsProcessAvailableLocked()) return;
        DisposeProcessLocked();
        var mpv = ResolveMpvPath();
        if (mpv.Length == 0)
            throw new InvalidOperationException("Radio Vault needs mpv for audio playback on Linux. Install mpv with your distribution's software manager and try again.");

        _ipcPath = Path.Combine(Path.GetTempPath(), $"radiovault-mpv-{Environment.ProcessId}-{Guid.NewGuid():N}.sock");
        var startInfo = new ProcessStartInfo
        {
            FileName = mpv,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "--idle=yes", "--no-video", "--audio-display=no", "--keep-open=yes", "--no-config",
                     "--terminal=no", "--really-quiet", $"--input-ipc-server={_ipcPath}"
                 })
            startInfo.ArgumentList.Add(argument);

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.Exited += ProcessOnExited;
        if (!_process.Start()) throw new InvalidOperationException("mpv could not be started.");
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        while (!File.Exists(_ipcPath) && !_process.HasExited && DateTime.UtcNow < deadline) Thread.Sleep(25);
        if (!File.Exists(_ipcPath))
            throw new InvalidOperationException("mpv started but its private control socket did not become available.");
    }

    private bool IsProcessAvailableLocked()
        => _process is { HasExited: false } && _ipcPath.Length > 0 && File.Exists(_ipcPath);

    private void SendCommandLocked(params object[] arguments)
        => SendCommandAsync(arguments, CancellationToken.None).GetAwaiter().GetResult();

    private async Task<JsonElement?> SendCommandAsync(object[] arguments, CancellationToken cancellationToken)
    {
        string ipcPath;
        lock (_gate)
        {
            if (!IsProcessAvailableLocked()) throw new InvalidOperationException("The Linux audio player is not running.");
            ipcPath = _ipcPath;
        }
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(ipcPath), timeout.Token).ConfigureAwait(false);
            await using var stream = new NetworkStream(socket, ownsSocket: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { command = arguments })).ConfigureAwait(false);

            for (var attempt = 0; attempt < 20; attempt++)
            {
                var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is null) break;
                using var response = JsonDocument.Parse(line);
                var root = response.RootElement;
                if (!root.TryGetProperty("error", out var error)) continue;
                var errorText = error.GetString() ?? "unknown error";
                if (!string.Equals(errorText, "success", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"mpv rejected a playback command: {errorText}.");
                return root.TryGetProperty("data", out var data) ? data.Clone() : null;
            }
            throw new IOException("mpv did not answer its private playback command.");
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private async Task PollStateAsync()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || _mediaPath is null || !IsProcessAvailableLocked()) return;
            }
            var position = await ReadDoublePropertyAsync("time-pos").ConfigureAwait(false);
            var duration = await ReadDoublePropertyAsync("duration").ConfigureAwait(false);
            var paused = await ReadBoolPropertyAsync("pause").ConfigureAwait(false);
            var ended = await ReadBoolPropertyAsync("eof-reached").ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || _mediaPath is null) return;
                if (position.HasValue) _position = TimeSpan.FromSeconds(Math.Max(0d, position.Value));
                if (duration is > 0d) _duration = TimeSpan.FromSeconds(duration.Value);
                if (ended == true)
                {
                    _status = PlaybackStatus.Ended;
                    if (!_mediaEndedRaised)
                    {
                        _mediaEndedRaised = true;
                        MediaEnded?.Invoke(this, EventArgs.Empty);
                    }
                }
                else if (_status is not PlaybackStatus.Stopped and not PlaybackStatus.Opening)
                    _status = paused == true ? PlaybackStatus.Paused : PlaybackStatus.Playing;
                RaiseStateChangedLocked();
            }
        }
        catch
        {
            // A single missed IPC poll must not interrupt otherwise healthy audio.
        }
    }

    private async Task<double?> ReadDoublePropertyAsync(string name)
    {
        var data = await SendCommandAsync(new object[] { "get_property", name }, CancellationToken.None).ConfigureAwait(false);
        if (!data.HasValue) return null;
        if (data.Value.ValueKind == JsonValueKind.Number && data.Value.TryGetDouble(out var number)) return number;
        return double.TryParse(data.Value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private async Task<bool?> ReadBoolPropertyAsync(string name)
    {
        var data = await SendCommandAsync(new object[] { "get_property", name }, CancellationToken.None).ConfigureAwait(false);
        return data?.ValueKind is JsonValueKind.True ? true : data?.ValueKind is JsonValueKind.False ? false : null;
    }

    private TimeSpan ClampLocked(TimeSpan value)
    {
        if (value < TimeSpan.Zero) return TimeSpan.Zero;
        return _duration.HasValue && value > _duration.Value ? _duration.Value : value;
    }

    private void ProcessOnExited(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _process) || _status == PlaybackStatus.Stopped) return;
            FailLocked(new InvalidOperationException("The Linux mpv audio process stopped unexpectedly."));
        }
    }

    private void FailLocked(Exception exception)
    {
        _status = PlaybackStatus.Failed;
        MediaFailed?.Invoke(this, new PlaybackErrorEventArgs(exception));
        RaiseStateChangedLocked();
    }

    private void RaiseStateChangedLocked()
        => StateChanged?.Invoke(this, new PlaybackEngineSnapshot(
            _status, _status == PlaybackStatus.Playing, _position, _duration, _volume, _speed, _mediaPath));

    private void DisposeProcessLocked()
    {
        _stateTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (_process is not null)
        {
            _process.Exited -= ProcessOnExited;
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch { }
            _process.Dispose();
            _process = null;
        }
        try { if (_ipcPath.Length > 0 && File.Exists(_ipcPath)) File.Delete(_ipcPath); }
        catch { }
        _ipcPath = string.Empty;
    }

    private static string ResolveMpvPath()
    {
        var configured = Environment.GetEnvironmentVariable("RADIOVAULT_MPV_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        foreach (var candidate in new[] { "/usr/bin/mpv", "/usr/local/bin/mpv", "/snap/bin/mpv" })
            if (File.Exists(candidate)) return candidate;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;
            var candidate = Path.Combine(directory, "mpv");
            if (File.Exists(candidate)) return candidate;
        }
        return string.Empty;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LinuxMpvPlaybackEngine));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeProcessLocked();
            _status = PlaybackStatus.Stopped;
        }
        _stateTimer.Dispose();
        _commandGate.Dispose();
    }
}
