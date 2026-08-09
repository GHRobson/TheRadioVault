using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Client.Mobile.Platform;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile;

public sealed class MobileClientSession : IDisposable
{
    private static readonly double[] PlaybackSpeeds = [0.75d, 1d, 1.25d, 1.5d, 1.75d, 2d];
    private readonly MobileServerClient _server;
    private readonly IMobilePlaybackEngine _playback;
    private MobileMediaProxy? _mediaProxy;
    private IReadOnlyList<WebCanonicalMediaPart> _parts = Array.Empty<WebCanonicalMediaPart>();
    private int _partIndex;
    private double _speed = 1d;
    private bool _disposed;

    public MobileClientSession(MobileServerClient server, IMobilePlaybackEngine playback)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _playback.StateChanged += PlaybackOnStateChanged;
        _playback.MediaEnded += PlaybackOnMediaEnded;
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
    public bool CanControlPlayback => _playback.Current.IsOpen;
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
        try
        {
            var manifest = await _server.GetMediaManifestAsync(broadcast.EpisodeId).ConfigureAwait(false);
            if (manifest.Parts.Count == 0)
                throw new InvalidOperationException("This broadcast has no playable media parts.");
            _parts = manifest.Parts.OrderBy(part => part.PartNumber).ToArray();
            var logicalPosition = Math.Clamp(broadcast.Source.PositionMs, 0, Math.Max(0, manifest.DurationMs));
            _partIndex = Math.Max(0, Array.FindIndex(_parts.ToArray(), part =>
                logicalPosition >= part.LogicalStartMs && logicalPosition < part.LogicalEndMs));
            SelectedBroadcast = broadcast;
            NowPlayingTitle = broadcast.Title;
            NowPlayingSubtitle = broadcast.Subtitle;
            OpenCurrentPart(true, TimeSpan.FromMilliseconds(
                Math.Max(0, logicalPosition - _parts[_partIndex].LogicalStartMs)));
            TabRequested?.Invoke(2);
        }
        catch (Exception exception) { PlaybackStatus = "Playback failed: " + exception.Message; }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    public void TogglePlayPause()
    {
        if (!CanControlPlayback) return;
        if (_playback.Current.IsPlaying) _playback.Pause(); else _playback.Play();
    }

    public void SkipBack() => SeekRelative(TimeSpan.FromSeconds(-15));
    public void SkipForward() => SeekRelative(TimeSpan.FromSeconds(30));

    public void CycleSpeed()
    {
        if (!CanControlPlayback) return;
        var next = Array.FindIndex(PlaybackSpeeds, value => value > _speed + 0.001d);
        _speed = next < 0 ? PlaybackSpeeds[0] : PlaybackSpeeds[next];
        _playback.SetRate(_speed);
        NotifyPlayback();
    }

    private void OpenCurrentPart(bool play, TimeSpan? seek = null)
    {
        if (_partIndex < 0 || _partIndex >= _parts.Count || SelectedBroadcast is null) return;
        _mediaProxy ??= new MobileMediaProxy(_server);
        var part = _parts[_partIndex];
        var url = _mediaProxy.Register(WebApiRoutes.MediaPart(SelectedBroadcast.EpisodeId, part.MediaFileId));
        _playback.Open(url);
        _playback.SetRate(_speed);
        if (seek is { } position && position > TimeSpan.Zero) _playback.Seek(position);
        if (play) _playback.Play();
        PlaybackStatus = _parts.Count > 1 ? $"Playing part {_partIndex + 1} of {_parts.Count}" : "Playing";
    }

    private void SeekRelative(TimeSpan amount)
    {
        var current = _playback.Current;
        if (!current.IsOpen) return;
        var target = current.Position + amount;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (current.Duration is { } duration && target > duration) target = duration;
        _playback.Seek(target);
    }

    private void PlaybackOnMediaEnded(object? sender, EventArgs eventArgs)
    {
        if (_partIndex + 1 < _parts.Count)
        {
            _partIndex++;
            OpenCurrentPart(true);
            return;
        }
        PlaybackStatus = "Finished";
        NotifyPlayback();
    }

    private void PlaybackOnStateChanged(object? sender, MobilePlaybackSnapshot snapshot)
    {
        var part = _partIndex >= 0 && _partIndex < _parts.Count ? _parts[_partIndex] : null;
        var logicalMs = (part?.LogicalStartMs ?? 0) + (long)snapshot.Position.TotalMilliseconds;
        var totalMs = _parts.Count == 0
            ? (long)(snapshot.Duration?.TotalMilliseconds ?? 0)
            : _parts.Max(value => value.LogicalEndMs);
        PlaybackProgress = totalMs <= 0 ? 0 : Math.Clamp(logicalMs / (double)totalMs, 0d, 1d);
        PlaybackTime = $"{FormatTime(TimeSpan.FromMilliseconds(logicalMs))} / {FormatTime(TimeSpan.FromMilliseconds(totalMs))}";
        if (!string.IsNullOrWhiteSpace(snapshot.Error)) PlaybackStatus = "Playback failed: " + snapshot.Error;
        NotifyPlayback();
    }

    private void SetBusy(bool busy, string? status = null)
    {
        IsBusy = busy;
        if (status is not null) StatusText = status;
        Notify();
    }

    private void Notify() => StateChanged?.Invoke(this, EventArgs.Empty);
    private void NotifyPlayback() => PlaybackStateChanged?.Invoke(this, EventArgs.Empty);

    private static IReadOnlyList<MobileBroadcastItem> Convert(IEnumerable<WebClientLibraryBroadcastSummary> values)
        => values.Select(value => new MobileBroadcastItem(value)).ToArray();

    private static string FormatTime(TimeSpan value)
        => value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _playback.StateChanged -= PlaybackOnStateChanged;
        _playback.MediaEnded -= PlaybackOnMediaEnded;
        _mediaProxy?.Dispose();
        _playback.Dispose();
        _server.Dispose();
    }
}
