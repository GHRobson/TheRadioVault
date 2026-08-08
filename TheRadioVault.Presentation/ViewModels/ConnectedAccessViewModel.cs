using System.Collections.ObjectModel;
using System.Windows.Input;
using TheRadioVault.Application.Abstractions;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class ConnectedAccessViewModel : ObservableObject, IDisposable
{
    private readonly IConnectedAccessService _service;
    private readonly IUiDispatcher? _dispatcher;
    private readonly AsyncCommand _discoverCommand;
    private readonly AsyncCommand _pairCommand;
    private readonly AsyncCommand _testCommand;
    private readonly AsyncCommand _reconnectCommand;
    private readonly AsyncCommand _useServerCommand;
    private readonly AsyncCommand _useLocalCommand;
    private readonly AsyncCommand _forgetCommand;
    private readonly AsyncCommand _restartCommand;
    private ConnectedAccessSnapshot _snapshot;
    private ConnectedServerOption? _selectedServer;
    private string _pairingCode = string.Empty;
    private bool _isBusy;
    private string _operationText = string.Empty;

    public ConnectedAccessViewModel(IConnectedAccessService service, IUiDispatcher? dispatcher = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dispatcher = dispatcher;
        _snapshot = service.Current;
        _service.StateChanged += ServiceOnStateChanged;
        _discoverCommand = new AsyncCommand(DiscoverAsync, () => !IsBusy, SetError);
        _pairCommand = new AsyncCommand(PairAsync, CanPair, SetError);
        _testCommand = new AsyncCommand(TestAsync, () => !IsBusy && Snapshot.HasSavedServer, SetError);
        _reconnectCommand = new AsyncCommand(ReconnectAsync, () => !IsBusy && Snapshot.HasSavedServer, SetError);
        _useServerCommand = new AsyncCommand(() => SetModeAsync(true), () => !IsBusy && Snapshot.HasSavedServer, SetError);
        _useLocalCommand = new AsyncCommand(() => SetModeAsync(false), () => !IsBusy, SetError);
        _forgetCommand = new AsyncCommand(ForgetAsync, () => !IsBusy && Snapshot.HasSavedServer, SetError);
        _restartCommand = new AsyncCommand(() => _service.RestartAsync(), () => !IsBusy, SetError);
    }

    public ObservableCollection<ConnectedServerOption> Servers { get; } = new();
    public ConnectedAccessSnapshot Snapshot { get => _snapshot; private set { if (SetProperty(ref _snapshot, value)) RaiseSnapshotProperties(); } }
    public ConnectedServerOption? SelectedServer { get => _selectedServer; set { if (SetProperty(ref _selectedServer, value)) RaiseCommandState(); } }
    public string PairingCode { get => _pairingCode; set { if (SetProperty(ref _pairingCode, value)) RaiseCommandState(); } }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RaisePropertyChanged(nameof(IsConnectionHealthy));
            RaisePropertyChanged(nameof(IsConnectionPending));
            RaisePropertyChanged(nameof(IsConnectionError));
            RaiseCommandState();
        }
    }
    public string OperationText { get => _operationText; private set => SetProperty(ref _operationText, value); }
    public bool HasServers => Servers.Count > 0;
    public bool RequiresRestart => Snapshot.UseRemoteOnStartup != Snapshot.IsRemoteSession;
    public string RestartNotice => RequiresRestart
        ? Snapshot.UseRemoteOnStartup ? "Restart to open the server Library." : "Restart to return to the local Library."
        : "The selected Library mode is active.";
    public string SessionModeText => Snapshot.IsRemoteSession ? "Connected Access client" : "Authoritative local library";
    public string ConnectionTitle => Snapshot.IsRemoteSession
        ? (string.IsNullOrWhiteSpace(Snapshot.ServerDisplayName) ? "Server Library" : Snapshot.ServerDisplayName)
        : "This computer's Library";
    public string ConnectionStatusText => Snapshot.StatusText;
    public string ConnectionDetailText => Snapshot.DetailText;
    public string ActiveServerName => string.IsNullOrWhiteSpace(Snapshot.ServerDisplayName)
        ? Snapshot.IsRemoteSession ? "Paired Radio Vault Server" : "Radio Vault Server on this computer"
        : Snapshot.ServerDisplayName;
    public string ActiveServerLocation => Snapshot.IsRemoteSession
        ? "Another computer on your network"
        : "This computer";
    public string ActiveServerAddress => string.IsNullOrWhiteSpace(Snapshot.ServerAddress)
        ? "Address unavailable"
        : Snapshot.ServerAddress;
    public string SavedServerName => string.IsNullOrWhiteSpace(Snapshot.SavedServerDisplayName)
        ? "Paired Radio Vault Server"
        : Snapshot.SavedServerDisplayName;
    public string SavedServerAddress => Snapshot.SavedServerAddress;
    public string ActiveConnectionLabel => Snapshot.IsRemoteSession ? "CONNECTED REMOTELY" : "CONNECTED LOCALLY";
    public string ConnectionStateBrush => Snapshot.State switch
    {
        ConnectedAccessState.Live or ConnectedAccessState.LocalLibrary => "#45B67A",
        ConnectedAccessState.Connecting or ConnectedAccessState.Updating or ConnectedAccessState.Discovering
            or ConnectedAccessState.Pairing or ConnectedAccessState.CachedReadOnly => "#D6B84B",
        _ => "#D76262"
    };
    public string ActiveConnectionExplanation => Snapshot.IsRemoteSession
        ? "This client is using a paired Radio Vault Server on another computer."
        : "This client is using the separate Radio Vault Server app on this computer. Find Servers and pairing are not needed.";
    public string StateLabel => Snapshot.StateLabel;
    public string ModeLabel => Snapshot.ModeLabel;
    public string PlaybackLabel => Snapshot.PlaybackLabel;
    public bool IsLive => Snapshot.IsLive;
    public bool IsCachedReadOnly => Snapshot.IsCachedReadOnly;
    public bool IsConnectionHealthy => Snapshot.State is ConnectedAccessState.LocalLibrary or ConnectedAccessState.Live;
    public bool IsConnectionPending => IsBusy || Snapshot.State is ConnectedAccessState.Discovering
        or ConnectedAccessState.Pairing or ConnectedAccessState.Connecting
        or ConnectedAccessState.Updating or ConnectedAccessState.CachedReadOnly;
    public bool IsConnectionError => !IsConnectionHealthy && !IsConnectionPending;
    public bool IsRemoteSession => Snapshot.IsRemoteSession;
    public bool IsLocalSession => !Snapshot.IsRemoteSession;
    public bool HasSavedServer => Snapshot.HasSavedServer;
    public bool UseRemoteOnStartup => Snapshot.UseRemoteOnStartup;
    public string CacheSizeText => Snapshot.CacheSizeText;
    public string CapabilityGenerationText => Snapshot.CapabilityGeneration > 0
        ? $"Generation {Snapshot.CapabilityGeneration} · negotiated with the active server"
        : "Negotiated automatically with the local server";
    public string RemoteLibrarySummary => Snapshot.IsRemoteSession && Snapshot.BroadcastCount > 0
        ? $"{Snapshot.BroadcastCount:N0} broadcasts · {Snapshot.ShowCount:N0} shows"
        : Snapshot.IsRemoteSession ? Snapshot.CacheSizeText : "The server on this computer is authoritative.";
    public bool HasReconnectSchedule => Snapshot.NextReconnectAt.HasValue;
    public string ReconnectScheduleText => Snapshot.NextReconnectAt is { } retry
        ? $"Automatic retry at {retry.ToLocalTime():HH:mm:ss}"
        : string.Empty;

    public ICommand DiscoverCommand => _discoverCommand;
    public ICommand PairCommand => _pairCommand;
    public ICommand TestCommand => _testCommand;
    public ICommand ReconnectCommand => _reconnectCommand;
    public ICommand UseServerCommand => _useServerCommand;
    public ICommand UseLocalCommand => _useLocalCommand;
    public ICommand ForgetCommand => _forgetCommand;
    public ICommand RestartCommand => _restartCommand;

    public Task InitializeAsync()
    {
        Snapshot = _service.Current;
        OperationText = Snapshot.IsCachedReadOnly
            ? "Browsing the encrypted server cache while Radio Vault reconnects."
            : Snapshot.DetailText;
        return Task.CompletedTask;
    }

    private async Task DiscoverAsync()
    {
        IsBusy = true;
        OperationText = "Looking for Radio Vault servers on this network…";
        try
        {
            var servers = await _service.DiscoverAsync().ConfigureAwait(true);
            Servers.Clear();
            foreach (var server in servers) Servers.Add(server);
            SelectedServer = Servers.FirstOrDefault(x => x.PairingAvailable) ?? Servers.FirstOrDefault();
            RaisePropertyChanged(nameof(HasServers));
            OperationText = Servers.Count == 0
                ? "No Radio Vault servers announced themselves. Check that Connected Access and desktop pairing are enabled on the server."
                : $"Found {Servers.Count:N0} Radio Vault server{(Servers.Count == 1 ? string.Empty : "s")}.";
        }
        finally { IsBusy = false; }
    }

    private bool CanPair()
        => !IsBusy && SelectedServer is not null && PairingCode.Trim().Length == 6;

    private async Task PairAsync()
    {
        if (SelectedServer is null) return;
        IsBusy = true;
        OperationText = $"Pairing with {SelectedServer.DisplayName}…";
        try
        {
            await _service.PairAsync(SelectedServer.InstanceId, PairingCode.Trim()).ConfigureAwait(true);
            PairingCode = string.Empty;
            OperationText = "Pairing succeeded. This server is selected automatically; restart Radio Vault Client to use it.";
        }
        finally { IsBusy = false; }
    }

    private async Task TestAsync()
    {
        IsBusy = true;
        OperationText = "Testing the saved certificate-pinned connection…";
        try
        {
            await _service.TestAsync().ConfigureAwait(true);
            OperationText = _service.Current.IsCachedReadOnly
                ? "The live server is unavailable. Previously opened views remain available read-only."
                : "The saved server connection is healthy.";
        }
        finally { IsBusy = false; }
    }

    private async Task ReconnectAsync()
    {
        IsBusy = true;
        OperationText = "Reconnecting to the Radio Vault server…";
        try
        {
            await _service.ReconnectAsync().ConfigureAwait(true);
            OperationText = _service.Current.IsLive
                ? "The server Library is live."
                : _service.Current.StatusText;
        }
        finally { IsBusy = false; }
    }

    private async Task SetModeAsync(bool remote)
    {
        await _service.SetStartupModeAsync(remote).ConfigureAwait(true);
        Snapshot = _service.Current;
        OperationText = remote
            ? "The server Library will open after Radio Vault restarts."
            : "The local Library will open after Radio Vault restarts.";
    }

    private async Task ForgetAsync()
    {
        await _service.ForgetServerAsync().ConfigureAwait(true);
        Servers.Clear();
        SelectedServer = null;
        RaisePropertyChanged(nameof(HasServers));
        OperationText = "The saved server, token and encrypted metadata cache were removed.";
    }

    private void ServiceOnStateChanged(object? sender, ConnectedAccessSnapshot snapshot)
    {
        if (_dispatcher is not null && !_dispatcher.CheckAccess())
        {
            _ = ApplySnapshotOnUiAsync(snapshot);
            return;
        }
        Snapshot = snapshot;
        if (!IsBusy) OperationText = snapshot.DetailText;
    }

    private async Task ApplySnapshotOnUiAsync(ConnectedAccessSnapshot snapshot)
    {
        try
        {
            await _dispatcher!.InvokeAsync(() =>
            {
                Snapshot = snapshot;
                if (!IsBusy) OperationText = snapshot.DetailText;
            }).ConfigureAwait(false);
        }
        catch { }
    }

    private void RaiseSnapshotProperties()
    {
        RaisePropertyChanged(nameof(RequiresRestart));
        RaisePropertyChanged(nameof(RestartNotice));
        RaisePropertyChanged(nameof(SessionModeText));
        RaisePropertyChanged(nameof(ConnectionTitle));
        RaisePropertyChanged(nameof(ConnectionStatusText));
        RaisePropertyChanged(nameof(ConnectionDetailText));
        RaisePropertyChanged(nameof(ActiveServerName));
        RaisePropertyChanged(nameof(ActiveServerLocation));
        RaisePropertyChanged(nameof(ActiveServerAddress));
        RaisePropertyChanged(nameof(SavedServerName));
        RaisePropertyChanged(nameof(SavedServerAddress));
        RaisePropertyChanged(nameof(ActiveConnectionLabel));
        RaisePropertyChanged(nameof(ConnectionStateBrush));
        RaisePropertyChanged(nameof(ActiveConnectionExplanation));
        RaisePropertyChanged(nameof(StateLabel));
        RaisePropertyChanged(nameof(ModeLabel));
        RaisePropertyChanged(nameof(PlaybackLabel));
        RaisePropertyChanged(nameof(IsLive));
        RaisePropertyChanged(nameof(IsCachedReadOnly));
        RaisePropertyChanged(nameof(IsConnectionHealthy));
        RaisePropertyChanged(nameof(IsConnectionPending));
        RaisePropertyChanged(nameof(IsConnectionError));
        RaisePropertyChanged(nameof(IsRemoteSession));
        RaisePropertyChanged(nameof(IsLocalSession));
        RaisePropertyChanged(nameof(HasSavedServer));
        RaisePropertyChanged(nameof(UseRemoteOnStartup));
        RaisePropertyChanged(nameof(CacheSizeText));
        RaisePropertyChanged(nameof(CapabilityGenerationText));
        RaisePropertyChanged(nameof(RemoteLibrarySummary));
        RaisePropertyChanged(nameof(HasReconnectSchedule));
        RaisePropertyChanged(nameof(ReconnectScheduleText));
        RaiseCommandState();
    }

    private void RaiseCommandState()
    {
        _discoverCommand.RaiseCanExecuteChanged();
        _pairCommand.RaiseCanExecuteChanged();
        _testCommand.RaiseCanExecuteChanged();
        _reconnectCommand.RaiseCanExecuteChanged();
        _useServerCommand.RaiseCanExecuteChanged();
        _useLocalCommand.RaiseCanExecuteChanged();
        _forgetCommand.RaiseCanExecuteChanged();
        _restartCommand.RaiseCanExecuteChanged();
    }

    private void SetError(Exception exception)
    {
        OperationText = exception.Message;
        IsBusy = false;
    }

    public void Dispose() => _service.StateChanged -= ServiceOnStateChanged;
}
