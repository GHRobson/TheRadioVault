using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Client.Mobile.Platform;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile;

public sealed class MobileClientSession : IDisposable
{
    private static readonly double[] PlaybackSpeeds = [0.75d, 1d, 1.25d, 1.5d, 1.75d, 2d];
    private static readonly TimeSpan DurableProgressInterval = TimeSpan.FromSeconds(5);
    private readonly MobileServerClient _server;
    private readonly IMobilePlaybackEngine _playback;
    private readonly IMobileNowPlayingService _nowPlaying;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly Timer _syncTimer;
    private MobileMediaProxy? _mediaProxy;
    private IReadOnlyList<WebCanonicalMediaPart> _parts = Array.Empty<WebCanonicalMediaPart>();
    private int _partIndex;
    private double _speed = 1d;
    private long _playbackGeneration;
    private long _logicalPositionMs;
    private long _logicalDurationMs;
    private DateTimeOffset _lastDurableSave = DateTimeOffset.MinValue;
    private bool _explicitSeekPending;
    private bool _incrementPlayCountPending;
    private bool _ownsPlayback;
    private bool _disposed;

    public MobileClientSession(
        MobileServerClient server,
        IMobilePlaybackEngine playback,
        IMobileNowPlayingService nowPlaying)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _nowPlaying = nowPlaying ?? throw new ArgumentNullException(nameof(nowPlaying));
        _playback.StateChanged += PlaybackOnStateChanged;
        _playback.MediaEnded += PlaybackOnMediaEnded;
        _nowPlaying.CommandReceived += NowPlayingOnCommandReceived;
        _syncTimer = new Timer(_ => _ = SynchronizePlaybackAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        IsPaired = _server.IsPaired;
    }

    public event EventHandler? StateChanged;
    public event EventHandler? PlaybackStateChanged;
    public event Action<int>? TabRequested;

    public bool IsBusy { get; private set; }
    public bool IsPaired { get; private set; }
    public string StatusText { get; private set; } = "Pair this iPhone with your Radio Vault Server.";
    public string ServerName => _server.Connection?.ServerDisplayName ?? "No server paired";
    public string ServerAddress => _server.Connection is { } connection
        ? $"https://{connection.ServerAddress}:{connection.SecurePort}"
        : string.Empty;
    public int TotalBroadcasts { get; private set; }
    public int CompletedBroadcasts { get; private set; }
    public int InProgressBroadcasts { get; private set; }
    public IReadOnlyList<MobileBroadcastItem> ContinueListening { get; private set; } = [];
    public IReadOnlyList<MobileBroadcastItem> RecentBroadcasts { get; private set; } = [];
    public IReadOnlyList<MobileBroadcastItem> LibraryBroadcasts { get; private set; } = [];
    public IReadOnlyList<DiscoveredRadioVaultServer> Servers { get; private set; } = [];
    public MobileBroadcastItem? SelectedBroadcast { get; private set; }
    public string NowPlayingTitle { get; private set; } = "Nothing playing";
    public string NowPlayingSubtitle { get; private set; } = "Choose a broadcast from Home or Library";
    public string PlaybackStatus { get; private set; } = "Ready";
    public bool IsPlaying => _playback.Current.IsPlaying;
    public bool CanControlPlayback => _playback.Current.IsOpen && _ownsPlayback;
    public double PlaybackProgress { get; private set; }
    public string PlaybackTime { get; private set; } = "0:00 / 0:00";
    public string SpeedText => $"{_speed:0.##}×";

    public async Task InitializeAsync()
    {
        IsPaired = _server.IsPaired;
        Notify();
        if (!IsPaired)
        {
            TabRequested?.Invoke(3);
            return;
        }

        TabRequested?.Invoke(0);
        await RefreshAsync().ConfigureAwait(false);
    }

    public async Task RefreshAsync()
    {
        if (!IsPaired || IsBusy) return;
        SetBusy(true, $"Connecting to {ServerName}…");
        try
        {
            var bootstrap = await _server.TestConnectionAsync().ConfigureAwait(false);
            var overview = await _server.GetOverviewAsync().ConfigureAwait(false);
            TotalBroadcasts = overview.TotalBroadcasts;
            CompletedBroadcasts = overview.CompletedBroadcasts;
            InProgressBroadcasts = overview.InProgressBroadcasts;
            ContinueListening = Convert(overview.ContinueListening);
            RecentBroadcasts = Convert(overview.RecentBroadcasts);
            if (LibraryBroadcasts.Count == 0)
                LibraryBroadcasts = Convert((await _server.BrowseAsync(string.Empty).ConfigureAwait(false)).Broadcasts);
            StatusText = $"Connected to {bootstrap.Server.DisplayName} · {overview.TotalBroadcasts:N0} broadcasts";
        }
        catch (Exception exception)
        {
            StatusText = "Could not reach the paired server: " + exception.Message;
            TabRequested?.Invoke(3);
        }
        finally { SetBusy(false); }
    }

    public async Task SearchAsync(string searchText)
    {
        if (!IsPaired || IsBusy) return;
        var search = searchText?.Trim() ?? string.Empty;
        SetBusy(true, search.Length == 0 ? "Loading the Library…" : $"Searching for “{search}”…");
        try
        {
            LibraryBroadcasts = Convert((await _server.BrowseAsync(search).ConfigureAwait(false)).Broadcasts);
            StatusText = $"{LibraryBroadcasts.Count:N0} broadcast{(LibraryBroadcasts.Count == 1 ? string.Empty : "s")} shown";
        }
        catch (Exception exception) { StatusText = "Search failed: " + exception.Message; }
        finally { SetBusy(false); }
    }

    public async Task DiscoverAsync()
    {
        if (IsBusy) return;
        SetBusy(true, "Looking for Radio Vault Server on this network…");
        try
        {
            Servers = await _server.DiscoverAsync().ConfigureAwait(false);
            StatusText = Servers.Count == 0
                ? "No servers found. Enable native clients and create a pairing code on Radio Vault Server."
                : $"Found {Servers.Count} server{(Servers.Count == 1 ? string.Empty : "s")}.";
        }
        catch (Exception exception) { StatusText = "Discovery failed: " + exception.Message; }
        finally { SetBusy(false); }
    }

    public async Task PairAsync(DiscoveredRadioVaultServer server, string pairingCode)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (IsBusy) return;
        SetBusy(true, $"Pairing with {server.DisplayName}…");
        try
        {
            await _server.PairAsync(server, pairingCode).ConfigureAwait(false);
            IsPaired = true;
            StatusText = $"Paired with {ServerName}. Loading your library…";
            Notify();
            TabRequested?.Invoke(0);
        }
        catch (Exception exception)
        {
            StatusText = "Pairing failed: " + exception.Message;
            SetBusy(false);
            return;
        }

        SetBusy(false);
        await RefreshAsync().ConfigureAwait(false);
    }

    public void Forget()
    {
        _playback.Pause();
        _ownsPlayback = false;
        _nowPlaying.Clear();
        _server.Forget();
        _mediaProxy?.Dispose();
        _mediaProxy = null;
        IsPaired = false;
        Servers = [];
        ContinueListening = [];
        RecentBroadcasts = [];
        LibraryBroadcasts = [];
        TotalBroadcasts = 0;
        CompletedBroadcasts = 0;
        InProgressBroadcasts = 0;
        StatusText = "Pairing removed from this iPhone.";
        Notify();
        TabRequested?.Invoke(3);
    }

    public async Task PlayAsync(MobileBroadcastItem broadcast)
    {
        ArgumentNullException.ThrowIfNull(broadcast);
        if (!IsPaired || IsBusy) return;
        IsBusy = true;
        PlaybackStatus = "Preparing secure stream…";
        Notify();
        WebPlaybackTransferTicket? transfer = null;
        var transferCommitted = false;
        try
        {
            await FlushPlaybackAsync().ConfigureAwait(false);
            _ownsPlayback = false;
            var manifest = await _server.GetMediaManifestAsync(broadcast.EpisodeId).ConfigureAwait(false);
            if (manifest.Parts.Count == 0)
                throw new InvalidOperationException("This broadcast has no playable media parts.");

            _parts = manifest.Parts.OrderBy(part => part.PartNumber).ToArray();
            _logicalDurationMs = Math.Max(
                Math.Max(0, manifest.DurationMs),
                _parts.Max(part => Math.Max(0, part.LogicalEndMs)));
            var logicalPosition = Math.Clamp(broadcast.Source.PositionMs, 0, _logicalDurationMs);
            var shared = await _server.GetPlaybackSessionAsync().ConfigureAwait(false);
            _playbackGeneration = Math.Max(0, shared.Generation);
            var anotherDeviceOwnsPlayback = HasActivePlayback(shared) && !IsOwnedByThisDevice(shared);
            var desiredPlaying = true;

            if (anotherDeviceOwnsPlayback)
            {
                if (shared.Player.EpisodeId == broadcast.EpisodeId)
                {
                    logicalPosition = ProjectPosition(shared.Player);
                    _speed = Math.Clamp(shared.Player.Speed, 0.5d, 3d);
                    desiredPlaying = shared.Player.IsPlaying;
                }
                PlaybackStatus = $"Preparing while {OwnerName(shared)} keeps playing…";
                NotifyPlayback();
                var begin = await _server.BeginPlaybackTransferAsync(new WebPlaybackTransferBeginRequest(
                    _server.ClientId,
                    broadcast.EpisodeId,
                    logicalPosition,
                    _logicalDurationMs,
                    _speed,
                    desiredPlaying,
                    _server.ClientDisplayName,
                    "iOSClient")).ConfigureAwait(false);
                transfer = RequireTransfer(begin);
                logicalPosition = transfer.ProtectedPositionMs;
                _speed = transfer.Speed;
                desiredPlaying = transfer.DesiredPlaying;
            }

            SelectedBroadcast = broadcast;
            NowPlayingTitle = broadcast.Title;
            NowPlayingSubtitle = broadcast.Subtitle;
            _incrementPlayCountPending = true;
            OpenLogicalPosition(logicalPosition, desiredPlaying, muted: transfer is not null);
            TabRequested?.Invoke(2);

            if (transfer is not null)
            {
                transfer = await AlignTransferAsync(transfer, desiredPlaying).ConfigureAwait(false);
                var committed = await _server.CommitPlaybackTransferAsync(new WebPlaybackTransferCommitRequest(
                    _server.ClientId,
                    transfer.TransferId,
                    transfer.ReadyRevision,
                    CaptureLogicalPosition(),
                    DecoderRunningMuted: true)).ConfigureAwait(false);
                ThrowIfConflict(committed.Conflict, committed.Message);
                transferCommitted = true;
                _playbackGeneration = Math.Max(0, committed.Session.Generation);
                await WaitForSourceStopAsync(committed.Session).ConfigureAwait(false);
                var ownership = await _server.GetPlaybackSessionAsync().ConfigureAwait(false);
                if (!IsOwnedByThisDevice(ownership))
                    throw new InvalidOperationException("Playback moved again before this iPhone became audible.");
                _playbackGeneration = Math.Max(0, ownership.Generation);
                _ownsPlayback = true;
                _playback.SetMuted(false);
                if (transfer.DesiredPlaying && !_playback.Current.IsPlaying) _playback.Play();
                if (!transfer.DesiredPlaying && _playback.Current.IsPlaying) _playback.Pause();
            }
            else
            {
                var update = await ReportLivePlaybackAsync(force: !IsOwnedByThisDevice(shared)).ConfigureAwait(false);
                ThrowIfConflict(update.Conflict, update.Message);
                var claimed = await _server.GetPlaybackSessionAsync().ConfigureAwait(false);
                _playbackGeneration = Math.Max(0, claimed.Generation);
                _ownsPlayback = IsOwnedByThisDevice(claimed);
                if (!_ownsPlayback) throw new InvalidOperationException("Another device owns playback.");
            }

            PlaybackStatus = IsPlaying ? $"Playing on {_server.ClientDisplayName}" : $"Paused on {_server.ClientDisplayName}";
            await SaveDurableProgressAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (transfer is not null && !transferCommitted)
            {
                try
                {
                    await _server.CancelPlaybackTransferAsync(new WebPlaybackTransferCancelRequest(
                        _server.ClientId, transfer.TransferId, exception.Message)).ConfigureAwait(false);
                }
                catch { }
            }
            _playback.Pause();
            _playback.SetMuted(false);
            _ownsPlayback = false;
            PlaybackStatus = "Playback failed: " + exception.Message;
        }
        finally
        {
            IsBusy = false;
            Notify();
            NotifyPlayback();
        }
    }

    public void TogglePlayPause()
    {
        if (!CanControlPlayback) return;
        if (_playback.Current.IsPlaying) _playback.Pause(); else _playback.Play();
        PlaybackStatus = _playback.Current.IsPlaying ? "Playing" : "Paused";
        _ = FlushPlaybackAsync();
    }

    public void SkipBack() => SeekRelative(TimeSpan.FromSeconds(-15));
    public void SkipForward() => SeekRelative(TimeSpan.FromSeconds(30));

    public void CycleSpeed()
    {
        if (!CanControlPlayback) return;
        var next = Array.FindIndex(PlaybackSpeeds, value => value > _speed + 0.001d);
        _speed = next < 0 ? PlaybackSpeeds[0] : PlaybackSpeeds[next];
        _playback.SetRate(_speed);
        PlaybackStatus = $"Playback speed {SpeedText}";
        NotifyPlayback();
        _ = FlushPlaybackAsync();
    }

    public Task FlushPlaybackAsync()
        => SynchronizePlaybackAsync(forceDurable: true, allowWhileBusy: true);

    private async Task<WebPlaybackTransferTicket> AlignTransferAsync(
        WebPlaybackTransferTicket transfer,
        bool desiredPlaying)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            var ready = await _server.MarkPlaybackTransferReadyAsync(new WebPlaybackTransferReadyRequest(
                _server.ClientId,
                transfer.TransferId,
                CaptureLogicalPosition(),
                _logicalDurationMs,
                DecoderReady: true,
                DesiredPlaying: desiredPlaying,
                OverrideDesiredPlaying: false,
                _speed,
                _server.ClientDisplayName,
                "iOSClient")).ConfigureAwait(false);
            ThrowIfConflict(ready.Conflict, ready.Message);
            transfer = RequireTransfer(ready);
            desiredPlaying = transfer.DesiredPlaying;
            _speed = transfer.Speed;
            _playback.SetRate(_speed);
            if (desiredPlaying && !_playback.Current.IsPlaying) _playback.Play();
            if (!desiredPlaying && _playback.Current.IsPlaying) _playback.Pause();
            if (Math.Abs(CaptureLogicalPosition() - transfer.CommitPositionMs) <= 750) return transfer;
            PlaybackStatus = "Aligning with the latest shared playhead…";
            OpenLogicalPosition(transfer.CommitPositionMs, desiredPlaying, muted: true);
        }
        throw new InvalidOperationException("The iPhone could not stay aligned with the source device.");
    }

    private async Task WaitForSourceStopAsync(WebPlaybackSession committed)
    {
        if (committed.CommittedTransfer is not { SourceWasPlaying: true, SourceStopAcknowledged: false } receipt)
            return;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250).ConfigureAwait(false);
            var latest = await _server.GetPlaybackSessionAsync().ConfigureAwait(false);
            if (latest.Generation != committed.Generation || !IsOwnedByThisDevice(latest))
                throw new InvalidOperationException("Playback moved again during handoff.");
            if (latest.CommittedTransfer?.TransferId == receipt.TransferId &&
                latest.CommittedTransfer.SourceStopAcknowledged) return;
        }
        PlaybackStatus = "Playback moved; the previous device did not confirm that it stopped.";
    }

    private void OpenLogicalPosition(long logicalPositionMs, bool play, bool muted)
    {
        if (_parts.Count == 0 || SelectedBroadcast is null) return;
        logicalPositionMs = Math.Clamp(logicalPositionMs, 0, Math.Max(0, _logicalDurationMs));
        var index = Array.FindIndex(_parts.ToArray(), part =>
            logicalPositionMs >= part.LogicalStartMs && logicalPositionMs < part.LogicalEndMs);
        _partIndex = index >= 0 ? index : _parts.Count - 1;
        var part = _parts[_partIndex];
        _mediaProxy ??= new MobileMediaProxy(_server);
        var url = _mediaProxy.Register(WebApiRoutes.MediaPart(SelectedBroadcast.EpisodeId, part.MediaFileId));
        _playback.SetMuted(muted);
        _playback.Open(url);
        _playback.SetRate(_speed);
        var localPosition = TimeSpan.FromMilliseconds(Math.Max(0, logicalPositionMs - part.LogicalStartMs));
        if (localPosition > TimeSpan.Zero) _playback.Seek(localPosition);
        if (play) _playback.Play(); else _playback.Pause();
        _logicalPositionMs = logicalPositionMs;
        PlaybackStatus = _parts.Count > 1 ? $"Playing part {_partIndex + 1} of {_parts.Count}" : "Playing";
    }

    private void SeekRelative(TimeSpan amount)
        => SeekLogical(CaptureLogicalPosition() + (long)amount.TotalMilliseconds);

    private void SeekLogical(long logicalPositionMs)
    {
        if (!CanControlPlayback || SelectedBroadcast is null) return;
        logicalPositionMs = Math.Clamp(logicalPositionMs, 0, Math.Max(0, _logicalDurationMs));
        var targetPart = Array.FindIndex(_parts.ToArray(), part =>
            logicalPositionMs >= part.LogicalStartMs && logicalPositionMs < part.LogicalEndMs);
        if (targetPart < 0) targetPart = _parts.Count - 1;
        var shouldPlay = _playback.Current.IsPlaying;
        if (targetPart != _partIndex)
        {
            OpenLogicalPosition(logicalPositionMs, shouldPlay, muted: false);
        }
        else
        {
            _playback.Seek(TimeSpan.FromMilliseconds(
                Math.Max(0, logicalPositionMs - _parts[_partIndex].LogicalStartMs)));
        }
        _logicalPositionMs = logicalPositionMs;
        _explicitSeekPending = true;
        NotifyPlayback();
        _ = FlushPlaybackAsync();
    }

    private async Task SynchronizePlaybackAsync(bool forceDurable = false, bool allowWhileBusy = false)
    {
        if (_disposed || !IsPaired || SelectedBroadcast is null || !_playback.Current.IsOpen) return;
        if (!await _syncGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var session = await _server.GetPlaybackSessionAsync().ConfigureAwait(false);
            _playbackGeneration = Math.Max(0, session.Generation);
            if (await StopForCommittedTransferAsync(session).ConfigureAwait(false)) return;
            if (IsBusy && !allowWhileBusy) return;
            if (HasActivePlayback(session) && !IsOwnedByThisDevice(session))
            {
                _ownsPlayback = false;
                if (_playback.Current.IsPlaying) _playback.Pause();
                PlaybackStatus = $"Playback moved to {OwnerName(session)}";
                NotifyPlayback();
                return;
            }

            var live = await ReportLivePlaybackAsync(force: !HasActivePlayback(session)).ConfigureAwait(false);
            if (live.Conflict)
            {
                _playback.Pause();
                PlaybackStatus = live.Message;
                NotifyPlayback();
                return;
            }
            _ownsPlayback = true;

            var now = DateTimeOffset.UtcNow;
            if (forceDurable || now - _lastDurableSave >= DurableProgressInterval)
                await SaveDurableProgressAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"[iOS playback sync] {exception}");
        }
        finally { _syncGate.Release(); }
    }

    private async Task<bool> StopForCommittedTransferAsync(WebPlaybackSession session)
    {
        var receipt = session.CommittedTransfer;
        if (receipt is null || !receipt.SourceWasPlaying || receipt.SourceStopAcknowledged ||
            !string.Equals(receipt.SourceClientId, _server.ClientId, StringComparison.Ordinal) ||
            string.Equals(receipt.TargetClientId, _server.ClientId, StringComparison.Ordinal))
            return false;

        _playback.Pause();
        _playback.SetMuted(false);
        _ownsPlayback = false;
        await _server.AcknowledgePlaybackSourceStoppedAsync(new WebPlaybackTransferSourceStoppedRequest(
            _server.ClientId, receipt.TransferId, receipt.Generation)).ConfigureAwait(false);
        PlaybackStatus = $"Playback moved to {receipt.TargetDeviceName}";
        NotifyPlayback();
        return true;
    }

    private async Task<WebClientPlaybackResult> ReportLivePlaybackAsync(bool force)
    {
        var broadcast = SelectedBroadcast ?? throw new InvalidOperationException("No broadcast is loaded.");
        return await _server.UpdateLivePlaybackAsync(new WebClientPlaybackUpdate(
            _server.ClientId,
            broadcast.EpisodeId,
            CaptureLogicalPosition(),
            _logicalDurationMs,
            _playback.Current.IsPlaying,
            _speed,
            Completed: IsCompleted(),
            Force: force,
            DeviceName: _server.ClientDisplayName,
            DeviceKind: "iOSClient",
            ExpectedGeneration: _playbackGeneration,
            ExplicitSeek: _explicitSeekPending)).ConfigureAwait(false);
    }

    private async Task SaveDurableProgressAsync()
    {
        var broadcast = SelectedBroadcast;
        if (broadcast is null) return;
        var explicitSeek = _explicitSeekPending;
        var incrementPlayCount = _incrementPlayCountPending;
        var result = await _server.SaveProgressAsync(new WebOfflineProgressUpdate(
            _server.ClientId,
            broadcast.EpisodeId,
            CaptureLogicalPosition(),
            _logicalDurationMs,
            Completed: IsCompleted(),
            Speed: _speed,
            CapturedAt: DateTimeOffset.UtcNow,
            AllowRewind: true,
            ExpectedGeneration: _playbackGeneration,
            ExplicitSeek: explicitSeek,
            IncrementPlayCount: incrementPlayCount)).ConfigureAwait(false);
        if (result.Conflict) throw new InvalidOperationException(result.Message);
        _explicitSeekPending = false;
        _incrementPlayCountPending = incrementPlayCount && !result.Changed;
        _lastDurableSave = DateTimeOffset.UtcNow;
    }

    private long CaptureLogicalPosition()
    {
        if (_partIndex < 0 || _partIndex >= _parts.Count) return Math.Max(0, _logicalPositionMs);
        var observed = _parts[_partIndex].LogicalStartMs + (long)_playback.Current.Position.TotalMilliseconds;
        _logicalPositionMs = Math.Clamp(observed, 0, Math.Max(0, _logicalDurationMs));
        return _logicalPositionMs;
    }

    private bool IsCompleted()
        => _logicalDurationMs > 0 && CaptureLogicalPosition() >= Math.Max(0, _logicalDurationMs - 5_000);

    private static bool HasActivePlayback(WebPlaybackSession session)
        => session.Player.EpisodeId is > 0;

    private bool IsOwnedByThisDevice(WebPlaybackSession session)
        => string.Equals(session.OwnerClientId, _server.ClientId, StringComparison.Ordinal);

    private static string OwnerName(WebPlaybackSession session)
        => string.IsNullOrWhiteSpace(session.Player.Device) ? session.OwnerDevice : session.Player.Device;

    private static long ProjectPosition(WebPlaybackState state)
    {
        var position = Math.Max(0, state.PositionMs);
        if (state.IsPlaying && state.UpdatedAt is { } updated)
        {
            var elapsed = (DateTimeOffset.UtcNow - updated).TotalMilliseconds;
            if (elapsed > 0 && elapsed <= 15_000)
                position += (long)Math.Round(elapsed * Math.Clamp(state.Speed, 0.5d, 3d));
        }
        return state.DurationMs > 0 ? Math.Clamp(position, 0, state.DurationMs) : position;
    }

    private static WebPlaybackTransferTicket RequireTransfer(WebPlaybackTransferResult result)
    {
        ThrowIfConflict(result.Conflict, result.Message);
        return result.Transfer ?? throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(result.Message) ? "The server did not create a playback move." : result.Message);
    }

    private static void ThrowIfConflict(bool conflict, string message)
    {
        if (conflict)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message) ? "Playback changed on another Radio Vault device." : message);
    }

    private void PlaybackOnMediaEnded(object? sender, EventArgs eventArgs)
    {
        if (_partIndex + 1 < _parts.Count)
        {
            _partIndex++;
            OpenLogicalPosition(_parts[_partIndex].LogicalStartMs, play: true, muted: false);
            return;
        }
        _logicalPositionMs = _logicalDurationMs;
        PlaybackStatus = "Finished";
        NotifyPlayback();
        _ = FlushPlaybackAsync();
    }

    private void PlaybackOnStateChanged(object? sender, MobilePlaybackSnapshot snapshot)
    {
        var logicalMs = CaptureLogicalPosition();
        PlaybackProgress = _logicalDurationMs <= 0 ? 0 : Math.Clamp(logicalMs / (double)_logicalDurationMs, 0d, 1d);
        PlaybackTime = $"{FormatTime(TimeSpan.FromMilliseconds(logicalMs))} / {FormatTime(TimeSpan.FromMilliseconds(_logicalDurationMs))}";
        if (!string.IsNullOrWhiteSpace(snapshot.Error)) PlaybackStatus = "Playback failed: " + snapshot.Error;
        NotifyPlayback();
    }

    private void NowPlayingOnCommandReceived(object? sender, MobileRemoteCommand command)
    {
        switch (command.Kind)
        {
            case MobileRemoteCommandKind.Play:
                if (CanControlPlayback && !_playback.Current.IsPlaying) _playback.Play();
                _ = FlushPlaybackAsync();
                break;
            case MobileRemoteCommandKind.Pause:
                if (CanControlPlayback && _playback.Current.IsPlaying) _playback.Pause();
                _ = FlushPlaybackAsync();
                break;
            case MobileRemoteCommandKind.TogglePlayPause:
                TogglePlayPause();
                break;
            case MobileRemoteCommandKind.SkipBack:
                SkipBack();
                break;
            case MobileRemoteCommandKind.SkipForward:
                SkipForward();
                break;
            case MobileRemoteCommandKind.Seek when command.Position is { } position:
                SeekLogical((long)position.TotalMilliseconds);
                break;
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        IsBusy = busy;
        if (status is not null) StatusText = status;
        Notify();
    }

    private void Notify() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void NotifyPlayback()
    {
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        _nowPlaying.Update(new MobileNowPlayingSnapshot(
            NowPlayingTitle,
            NowPlayingSubtitle,
            TimeSpan.FromMilliseconds(Math.Max(0, _logicalPositionMs)),
            TimeSpan.FromMilliseconds(Math.Max(0, _logicalDurationMs)),
            _speed,
            _playback.Current.IsPlaying,
            SelectedBroadcast is not null && _playback.Current.IsOpen && _ownsPlayback));
    }

    private static IReadOnlyList<MobileBroadcastItem> Convert(IEnumerable<WebClientLibraryBroadcastSummary> values)
        => values.Select(value => new MobileBroadcastItem(value)).ToArray();

    private static string FormatTime(TimeSpan value)
        => value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _syncTimer.Dispose();
        _playback.StateChanged -= PlaybackOnStateChanged;
        _playback.MediaEnded -= PlaybackOnMediaEnded;
        _nowPlaying.CommandReceived -= NowPlayingOnCommandReceived;
        _mediaProxy?.Dispose();
        _nowPlaying.Dispose();
        _playback.Dispose();
        _server.Dispose();
        _syncGate.Dispose();
    }
}
