using System.Collections.ObjectModel;
using Avalonia.Threading;
using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Client.Mobile.Platform;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile.ViewModels;

public sealed class MobileMainViewModel : ObservableObject, IDisposable
{
    private readonly MobileServerClient _server;
    private readonly IMobilePlaybackEngine _playback;
    private MobileMediaProxy? _mediaProxy;
    private IReadOnlyList<WebCanonicalMediaPart> _parts = Array.Empty<WebCanonicalMediaPart>();
    private int _partIndex;
    private bool _isBusy;
    private bool _isPaired;
    private string _statusText = "Pair this iPhone with your Radio Vault Server.";
    private string _searchText = string.Empty;
    private string _pairingCode = string.Empty;
    private DiscoveredRadioVaultServer? _selectedServer;
    private MobileBroadcastItem? _selectedBroadcast;
    private int _selectedTab = 3;
    private int _totalBroadcasts;
    private int _completedBroadcasts;
    private int _inProgressBroadcasts;
    private string _nowPlayingTitle = "Nothing playing";
    private string _nowPlayingSubtitle = "Choose a broadcast from Home or Library";
    private string _playbackStatus = "Ready";
    private string _playPauseText = "Play";
    private double _playbackProgress;
    private string _playbackTime = "0:00 / 0:00";
    private double _speed = 1d;
    private bool _disposed;

    public MobileMainViewModel(MobileServerClient server, IMobilePlaybackEngine playback)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _playback.StateChanged += PlaybackOnStateChanged;
        _playback.MediaEnded += PlaybackOnMediaEnded;

        RefreshCommand = new AsyncCommand(RefreshAsync, () => IsPaired && !IsBusy);
        SearchCommand = new AsyncCommand(SearchAsync, () => IsPaired && !IsBusy);
        DiscoverCommand = new AsyncCommand(DiscoverAsync, () => !IsBusy);
        PairCommand = new AsyncCommand(PairAsync, () => SelectedServer is not null && !IsBusy);
        ForgetCommand = new AsyncCommand(ForgetAsync, () => IsPaired && !IsBusy);
        PlayBroadcastCommand = new AsyncParameterCommand(PlayBroadcastAsync, value => value is MobileBroadcastItem && !IsBusy);
        PlayPauseCommand = new DelegateCommand(TogglePlayPause, () => _playback.Current.IsOpen);
        SkipBackCommand = new DelegateCommand(() => SeekRelative(TimeSpan.FromSeconds(-15)), () => _playback.Current.IsOpen);
        SkipForwardCommand = new DelegateCommand(() => SeekRelative(TimeSpan.FromSeconds(30)), () => _playback.Current.IsOpen);
        CycleSpeedCommand = new DelegateCommand(CycleSpeed, () => _playback.Current.IsOpen);
        ShowHomeCommand = new DelegateCommand(() => SelectedTab = 0);
        ShowLibraryCommand = new DelegateCommand(() => SelectedTab = 1);
        ShowPlayingCommand = new DelegateCommand(() => SelectedTab = 2);
        ShowSettingsCommand = new DelegateCommand(() => SelectedTab = 3);
    }

    public ObservableCollection<MobileBroadcastItem> ContinueListening { get; } = new();
    public ObservableCollection<MobileBroadcastItem> RecentBroadcasts { get; } = new();
    public ObservableCollection<MobileBroadcastItem> LibraryBroadcasts { get; } = new();
    public ObservableCollection<DiscoveredRadioVaultServer> Servers { get; } = new();

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand SearchCommand { get; }
    public AsyncCommand DiscoverCommand { get; }
    public AsyncCommand PairCommand { get; }
    public AsyncCommand ForgetCommand { get; }
    public AsyncParameterCommand PlayBroadcastCommand { get; }
    public DelegateCommand PlayPauseCommand { get; }
    public DelegateCommand SkipBackCommand { get; }
    public DelegateCommand SkipForwardCommand { get; }
    public DelegateCommand CycleSpeedCommand { get; }
    public DelegateCommand ShowHomeCommand { get; }
    public DelegateCommand ShowLibraryCommand { get; }
    public DelegateCommand ShowPlayingCommand { get; }
    public DelegateCommand ShowSettingsCommand { get; }

    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommands(); } }
    public bool IsPaired { get => _isPaired; private set { if (SetProperty(ref _isPaired, value)) { RaisePropertyChanged(nameof(IsNotPaired)); RaiseCommands(); } } }
    public bool IsNotPaired => !IsPaired;
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string ServerName => _server.Connection?.ServerDisplayName ?? "No server paired";
    public string ServerAddress => _server.Connection is { } connection
        ? $"https://{connection.ServerAddress}:{connection.SecurePort}"
        : string.Empty;
    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }
    public string PairingCode { get => _pairingCode; set => SetProperty(ref _pairingCode, value); }
    public DiscoveredRadioVaultServer? SelectedServer
    {
        get => _selectedServer;
        set { if (SetProperty(ref _selectedServer, value)) PairCommand.RaiseCanExecuteChanged(); }
    }
    public MobileBroadcastItem? SelectedBroadcast { get => _selectedBroadcast; set => SetProperty(ref _selectedBroadcast, value); }
    public int TotalBroadcasts { get => _totalBroadcasts; private set => SetProperty(ref _totalBroadcasts, value); }
    public int CompletedBroadcasts { get => _completedBroadcasts; private set => SetProperty(ref _completedBroadcasts, value); }
    public int InProgressBroadcasts { get => _inProgressBroadcasts; private set => SetProperty(ref _inProgressBroadcasts, value); }
    public string TotalBroadcastsText => TotalBroadcasts.ToString("N0");
    public string CompletedBroadcastsText => CompletedBroadcasts.ToString("N0");
    public string InProgressBroadcastsText => InProgressBroadcasts.ToString("N0");
    public bool HasContinueListening => ContinueListening.Count > 0;
    public bool HasRecentBroadcasts => RecentBroadcasts.Count > 0;
    public bool HasLibraryResults => LibraryBroadcasts.Count > 0;
    public string NowPlayingTitle { get => _nowPlayingTitle; private set => SetProperty(ref _nowPlayingTitle, value); }
    public string NowPlayingSubtitle { get => _nowPlayingSubtitle; private set => SetProperty(ref _nowPlayingSubtitle, value); }
    public string PlaybackStatus { get => _playbackStatus; private set => SetProperty(ref _playbackStatus, value); }
    public string PlayPauseText { get => _playPauseText; private set => SetProperty(ref _playPauseText, value); }
    public double PlaybackProgress { get => _playbackProgress; private set => SetProperty(ref _playbackProgress, value); }
    public string PlaybackTime { get => _playbackTime; private set => SetProperty(ref _playbackTime, value); }
    public string SpeedText => $"{_speed:0.##}×";

    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!SetProperty(ref _selectedTab, Math.Clamp(value, 0, 3))) return;
            RaisePropertyChanged(nameof(IsHome));
            RaisePropertyChanged(nameof(IsLibrary));
            RaisePropertyChanged(nameof(IsPlaying));
            RaisePropertyChanged(nameof(IsSettings));
        }
    }
    public bool IsHome => SelectedTab == 0;
    public bool IsLibrary => SelectedTab == 1;
    public bool IsPlaying => SelectedTab == 2;
    public bool IsSettings => SelectedTab == 3;

    public async Task InitializeAsync()
    {
        IsPaired = _server.IsPaired;
        RaisePropertyChanged(nameof(ServerName));
        RaisePropertyChanged(nameof(ServerAddress));
        if (!IsPaired)
        {
            SelectedTab = 3;
            return;
        }

        SelectedTab = 0;
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = $"Connecting to {ServerName}…";
        try
        {
            var bootstrap = await _server.TestConnectionAsync().ConfigureAwait(true);
            var overview = await _server.GetOverviewAsync().ConfigureAwait(true);
            TotalBroadcasts = overview.TotalBroadcasts;
            CompletedBroadcasts = overview.CompletedBroadcasts;
            InProgressBroadcasts = overview.InProgressBroadcasts;
            RaisePropertyChanged(nameof(TotalBroadcastsText));
            RaisePropertyChanged(nameof(CompletedBroadcastsText));
            RaisePropertyChanged(nameof(InProgressBroadcastsText));
            Replace(ContinueListening, overview.ContinueListening);
            Replace(RecentBroadcasts, overview.RecentBroadcasts);
            RaisePropertyChanged(nameof(HasContinueListening));
            RaisePropertyChanged(nameof(HasRecentBroadcasts));
            if (LibraryBroadcasts.Count == 0)
                await LoadLibraryAsync(string.Empty).ConfigureAwait(true);
            StatusText = $"Connected to {bootstrap.Server.DisplayName} · {overview.TotalBroadcasts:N0} broadcasts";
        }
        catch (Exception exception)
        {
            StatusText = "Could not reach the paired server: " + exception.Message;
            SelectedTab = 3;
        }
        finally { IsBusy = false; }
    }

    private async Task SearchAsync()
    {
        IsBusy = true;
        StatusText = string.IsNullOrWhiteSpace(SearchText) ? "Loading the Library…" : $"Searching for “{SearchText.Trim()}”…";
        try
        {
            await LoadLibraryAsync(SearchText).ConfigureAwait(true);
            StatusText = $"{LibraryBroadcasts.Count:N0} broadcast{(LibraryBroadcasts.Count == 1 ? string.Empty : "s")} shown";
        }
        catch (Exception exception) { StatusText = "Search failed: " + exception.Message; }
        finally { IsBusy = false; }
    }

    private async Task LoadLibraryAsync(string search)
    {
        var result = await _server.BrowseAsync(search).ConfigureAwait(true);
        Replace(LibraryBroadcasts, result.Broadcasts);
        RaisePropertyChanged(nameof(HasLibraryResults));
    }

    private async Task DiscoverAsync()
    {
        IsBusy = true;
        StatusText = "Looking for Radio Vault Server on this network…";
        try
        {
            Servers.Clear();
            foreach (var server in await _server.DiscoverAsync().ConfigureAwait(true)) Servers.Add(server);
            SelectedServer = Servers.FirstOrDefault();
            StatusText = Servers.Count == 0
                ? "No servers found. Enable native clients and create a pairing code on Radio Vault Server."
                : $"Found {Servers.Count} server{(Servers.Count == 1 ? string.Empty : "s")}.";
        }
        catch (Exception exception) { StatusText = "Discovery failed: " + exception.Message; }
        finally { IsBusy = false; }
    }

    private async Task PairAsync()
    {
        if (SelectedServer is null) return;
        IsBusy = true;
        StatusText = $"Pairing with {SelectedServer.DisplayName}…";
        try
        {
            await _server.PairAsync(SelectedServer, PairingCode).ConfigureAwait(true);
            IsPaired = true;
            PairingCode = string.Empty;
            RaisePropertyChanged(nameof(ServerName));
            RaisePropertyChanged(nameof(ServerAddress));
            SelectedTab = 0;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception) { StatusText = "Pairing failed: " + exception.Message; }
        finally { IsBusy = false; }
    }

    private Task ForgetAsync()
    {
        _playback.Pause();
        _server.Forget();
        _mediaProxy?.Dispose();
        _mediaProxy = null;
        IsPaired = false;
        Servers.Clear();
        ContinueListening.Clear();
        RecentBroadcasts.Clear();
        LibraryBroadcasts.Clear();
        RaisePropertyChanged(nameof(ServerName));
        RaisePropertyChanged(nameof(ServerAddress));
        SelectedTab = 3;
        StatusText = "Pairing removed from this iPhone.";
        return Task.CompletedTask;
    }

    private async Task PlayBroadcastAsync(object? value)
    {
        if (value is not MobileBroadcastItem broadcast) return;
        IsBusy = true;
        PlaybackStatus = "Preparing secure stream…";
        try
        {
            var manifest = await _server.GetMediaManifestAsync(broadcast.EpisodeId).ConfigureAwait(true);
            if (manifest.Parts.Count == 0) throw new InvalidOperationException("This broadcast has no playable media parts.");
            _parts = manifest.Parts.OrderBy(part => part.PartNumber).ToArray();
            var logicalPosition = Math.Clamp(broadcast.Source.PositionMs, 0, Math.Max(0, manifest.DurationMs));
            _partIndex = Math.Max(0, Array.FindIndex(_parts.ToArray(), part =>
                logicalPosition >= part.LogicalStartMs && logicalPosition < part.LogicalEndMs));
            if (_partIndex < 0) _partIndex = 0;
            SelectedBroadcast = broadcast;
            NowPlayingTitle = broadcast.Title;
            NowPlayingSubtitle = broadcast.Subtitle;
            OpenCurrentPart(play: true, seek: TimeSpan.FromMilliseconds(
                Math.Max(0, logicalPosition - _parts[_partIndex].LogicalStartMs)));
            SelectedTab = 2;
        }
        catch (Exception exception) { PlaybackStatus = "Playback failed: " + exception.Message; }
        finally { IsBusy = false; }
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

    private void TogglePlayPause()
    {
        if (_playback.Current.IsPlaying) _playback.Pause(); else _playback.Play();
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

    private void CycleSpeed()
    {
        var speeds = new[] { 0.75d, 1d, 1.25d, 1.5d, 1.75d, 2d };
        var next = Array.FindIndex(speeds, value => value > _speed + 0.001d);
        _speed = next < 0 ? speeds[0] : speeds[next];
        _playback.SetRate(_speed);
        RaisePropertyChanged(nameof(SpeedText));
    }

    private void PlaybackOnMediaEnded(object? sender, EventArgs eventArgs)
        => Dispatch(() =>
        {
            if (_partIndex + 1 < _parts.Count)
            {
                _partIndex++;
                OpenCurrentPart(play: true);
                return;
            }
            PlaybackStatus = "Finished";
            PlayPauseText = "Play";
        });

    private void PlaybackOnStateChanged(object? sender, MobilePlaybackSnapshot snapshot)
        => Dispatch(() =>
        {
            PlayPauseText = snapshot.IsPlaying ? "Pause" : "Play";
            PlayPauseCommand.RaiseCanExecuteChanged();
            SkipBackCommand.RaiseCanExecuteChanged();
            SkipForwardCommand.RaiseCanExecuteChanged();
            CycleSpeedCommand.RaiseCanExecuteChanged();
            var part = _partIndex >= 0 && _partIndex < _parts.Count ? _parts[_partIndex] : null;
            var logicalMs = (part?.LogicalStartMs ?? 0) + (long)snapshot.Position.TotalMilliseconds;
            var totalMs = _parts.Count == 0 ? (long)(snapshot.Duration?.TotalMilliseconds ?? 0) : _parts.Max(value => value.LogicalEndMs);
            PlaybackProgress = totalMs <= 0 ? 0 : Math.Clamp(logicalMs * 100d / totalMs, 0d, 100d);
            PlaybackTime = $"{FormatTime(TimeSpan.FromMilliseconds(logicalMs))} / {FormatTime(TimeSpan.FromMilliseconds(totalMs))}";
            if (!string.IsNullOrWhiteSpace(snapshot.Error)) PlaybackStatus = "Playback failed: " + snapshot.Error;
        });

    private static void Replace(ObservableCollection<MobileBroadcastItem> target, IEnumerable<WebClientLibraryBroadcastSummary> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(new MobileBroadcastItem(value));
    }

    private static string FormatTime(TimeSpan value)
        => value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");

    private static void Dispatch(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action(); else Dispatcher.UIThread.Post(action);
    }

    private void RaiseCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        SearchCommand.RaiseCanExecuteChanged();
        DiscoverCommand.RaiseCanExecuteChanged();
        PairCommand.RaiseCanExecuteChanged();
        ForgetCommand.RaiseCanExecuteChanged();
    }

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
