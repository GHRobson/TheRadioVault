using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;
using TheRadioVault.Web.Services;

namespace TheRadioVault.Services;

public sealed class NativeConnectedAccessService : IConnectedAccessService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly System.Net.IPAddress DiscoveryAddress = System.Net.IPAddress.Parse("239.255.82.86");
    private readonly NativeServerConnectionPreferences _preferences;
    private readonly bool _isRemoteSession;
    private readonly LoopbackServerClient? _runtimeConnection;
    private readonly ConcurrentDictionary<string, DiscoveredServer> _discovered = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _monitorCancellation = new();
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private Task? _monitorTask;
    private ConnectedAccessSnapshot _current;
    private int _consecutiveMonitorFailures;
    private bool _disposed;

    public NativeConnectedAccessService(
        NativeServerConnectionPreferences? preferences = null,
        bool isRemoteSession = false,
        LoopbackServerClient? runtimeConnection = null)
    {
        _preferences = preferences ?? NativeServerConnectionPreferences.Load();
        _isRemoteSession = isRemoteSession;
        _runtimeConnection = runtimeConnection;
        var hasActiveConnection = _runtimeConnection?.IsAvailable == true;
        var initialState = _isRemoteSession
            ? _preferences.HasSavedServer ? ConnectedAccessState.Connecting : ConnectedAccessState.Disconnected
            : hasActiveConnection ? ConnectedAccessState.LocalLibrary : ConnectedAccessState.Unavailable;
        _current = CreateSnapshot(
            initialState,
            initialState switch
            {
                ConnectedAccessState.Connecting => $"Connecting to {_preferences.ServerDisplayName}",
                ConnectedAccessState.Disconnected => "Pair with a Radio Vault Server",
                ConnectedAccessState.Unavailable => "Radio Vault Server is not running",
                _ => $"Connected to {LocalServerDisplayName}"
            },
            initialState switch
            {
                ConnectedAccessState.Connecting => "The native client is using the paired server for Library, playback and workspaces.",
                ConnectedAccessState.Disconnected => "Find the Radio Vault Server on your network, then enter its six-digit pairing code.",
                ConnectedAccessState.Unavailable => "Start Radio Vault Server on this computer or pair with a server on your network.",
                _ => "This client is using the separate Radio Vault Server app running on this computer. No discovery or pairing is needed."
            });
        if (_isRemoteSession && _preferences.HasSavedServer)
            _monitorTask = MonitorRemoteConnectionAsync(_monitorCancellation.Token);
    }

    public ConnectedAccessSnapshot Current => _current;
    public event EventHandler<ConnectedAccessSnapshot>? StateChanged;

    public async Task<IReadOnlyList<ConnectedServerOption>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        Publish(CreateSnapshot(ConnectedAccessState.Discovering, "Finding servers", "Listening for secure Radio Vault Server announcements on this network..."));
        var servers = await DiscoverCoreAsync(30829, TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
        _discovered.Clear();
        foreach (var server in servers) _discovered[server.InstanceId] = server;
        Publish(CreateSnapshot(
            _preferences.HasSavedServer || _isRemoteSession
                ? ConnectedAccessState.Disconnected
                : _runtimeConnection?.IsAvailable == true
                    ? ConnectedAccessState.LocalLibrary
                    : ConnectedAccessState.Unavailable,
            servers.Count == 0 ? "No servers found" : $"Found {servers.Count} server{(servers.Count == 1 ? string.Empty : "s")}",
            servers.Count == 0
                ? "Enable remote native clients in Radio Vault Server settings, then create a pairing code."
                : "Choose the server showing a one-time pairing code."));
        return servers.Select(server => new ConnectedServerOption(
            server.InstanceId, server.DisplayName, server.Address, server.SecurePort,
            server.AppVersion, server.PairingAvailable, server.PairedDesktopClients)).ToArray();
    }

    public async Task PairAsync(string serverInstanceId, string pairingCode, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        if (!_discovered.TryGetValue(serverInstanceId, out var server))
            throw new InvalidOperationException("Find servers again and choose the server that is displaying the pairing code.");
        var code = pairingCode?.Trim() ?? string.Empty;
        if (code.Length != 6 || code.Any(ch => !char.IsDigit(ch)))
            throw new InvalidOperationException("Enter the six-digit code shown in Radio Vault Server settings.");

        Publish(CreateSnapshot(ConnectedAccessState.Pairing, "Pairing", $"Establishing a certificate-pinned connection to {server.DisplayName}..."));
        using var client = CreatePinnedClient(server.Address, server.SecurePort, server.CertificateThumbprint, accessToken: null);
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(
            new WebDesktopPairingRequest(code, _preferences.ClientId, _preferences.ClientDisplayName), JsonOptions);
        using var content = new ByteArrayContent(requestBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var response = await client.PostAsync(WebApiRoutes.FederationPair, content, cancellationToken).ConfigureAwait(false);
        var envelope = await response.Content.ReadFromJsonAsync<PairingEnvelope>(JsonOptions, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || envelope?.Result is null || !envelope.Result.Paired)
            throw new InvalidOperationException(envelope?.Result?.Message ?? $"The server rejected pairing ({(int)response.StatusCode}).");
        var result = envelope.Result;
        if (!string.Equals(result.InstanceId, server.InstanceId, StringComparison.Ordinal) ||
            !string.Equals(NativeServerConnectionPreferences.NormalizeThumbprint(result.CertificateThumbprint), server.CertificateThumbprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The server identity changed during pairing. No credentials were saved.");

        _preferences.ServerInstanceId = result.InstanceId;
        _preferences.ServerDisplayName = result.DisplayName;
        _preferences.ServerAddress = server.Address;
        _preferences.SecurePort = result.SecurePort;
        _preferences.CertificateThumbprint = NativeServerConnectionPreferences.NormalizeThumbprint(result.CertificateThumbprint);
        _preferences.AccessToken = result.AccessToken;
        _preferences.CapabilityGeneration = result.CapabilityGeneration;
        _preferences.PairedAt = result.PairedAt ?? DateTimeOffset.UtcNow;
        _preferences.LastConnectedAt = DateTimeOffset.UtcNow;
        _preferences.UseRemoteOnStartup = true;
        _preferences.LibrarySyncSessionId = string.Empty;
        _preferences.LibrarySyncSequence = 0;
        _preferences.LibrarySyncRevision = string.Empty;
        _preferences.LibraryCacheSynchronizedAt = null;
        _preferences.Save();
        Publish(CreateSnapshot(
            ConnectedAccessState.Live,
            "Pairing complete",
            $"{result.DisplayName} trusts this client and is selected for the next launch. Restart Radio Vault Client to open its Library."));
    }

    public async Task TestAsync(CancellationToken cancellationToken = default)
    {
        EnsureSavedServer();
        Publish(CreateSnapshot(ConnectedAccessState.Connecting, "Testing connection", $"Contacting {_preferences.ServerDisplayName}..."));
        await _probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bootstrap = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            PublishHealthyConnection(bootstrap);
        }
        catch (Exception exception) when (
            _isRemoteSession &&
            (_runtimeConnection?.CacheSizeBytes ?? 0) > 0 &&
            !cancellationToken.IsCancellationRequested &&
            exception is HttpRequestException or TaskCanceledException)
        {
            Publish(CreateSnapshot(
                ConnectedAccessState.CachedReadOnly,
                "Server temporarily unavailable",
                "Previously opened server views remain available read-only. Radio Vault is reconnecting automatically.",
                nextReconnectAt: DateTimeOffset.UtcNow.AddSeconds(4),
                lastError: exception.Message));
            return;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    public Task ReconnectAsync(CancellationToken cancellationToken = default) => TestAsync(cancellationToken);

    public Task SetStartupModeAsync(bool useRemoteLibrary, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (useRemoteLibrary) EnsureSavedServer();
        _preferences.UseRemoteOnStartup = useRemoteLibrary;
        _preferences.Save();
        Publish(CreateSnapshot(
            _isRemoteSession ? ConnectedAccessState.Live : ConnectedAccessState.LocalLibrary,
            useRemoteLibrary ? "Paired server selected" : "This computer's server selected",
            useRemoteLibrary
                ? $"Radio Vault will use {_preferences.ServerDisplayName} after the app restarts."
                : "Radio Vault will use the server on this computer after the app restarts."));
        return Task.CompletedTask;
    }

    public Task ForgetServerAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _preferences.Forget();
        Publish(CreateSnapshot(ConnectedAccessState.LocalLibrary, "Pairing removed", "The remote server token and certificate pin were removed from this client."));
        return Task.CompletedTask;
    }

    public Task RestartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private async Task<WebFederationBootstrap> ProbeAsync(CancellationToken cancellationToken)
    {
        using var client = CreatePinnedClient(
            _preferences.ServerAddress, _preferences.SecurePort, _preferences.CertificateThumbprint, _preferences.AccessToken);
        var envelope = await client.GetFromJsonAsync<BootstrapEnvelope>(
            WebApiRoutes.FederationBootstrap, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The paired server returned an empty connection test.");
        if (!string.Equals(envelope.FederationBootstrap.Server.InstanceId, _preferences.ServerInstanceId, StringComparison.Ordinal))
            throw new InvalidOperationException("The saved server address now belongs to a different Radio Vault Server.");
        return envelope.FederationBootstrap;
    }

    private async Task MonitorRemoteConnectionAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.Zero;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                await _probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var bootstrap = await ProbeAsync(cancellationToken).ConfigureAwait(false);
                    _consecutiveMonitorFailures = 0;
                    PublishHealthyConnection(bootstrap);
                    delay = TimeSpan.FromSeconds(15);
                }
                finally
                {
                    _probeGate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                _consecutiveMonitorFailures++;
                var retrySeconds = Math.Min(30, 2 << Math.Min(_consecutiveMonitorFailures - 1, 3));
                delay = TimeSpan.FromSeconds(retrySeconds);
                var cached = (_runtimeConnection?.CacheSizeBytes ?? 0) > 0;
                Publish(CreateSnapshot(
                    cached ? ConnectedAccessState.CachedReadOnly : ConnectedAccessState.Disconnected,
                    cached ? "Server unavailable — cached Library" : "Reconnecting to server",
                    cached
                        ? "Previously opened Library views remain available read-only while the live connection recovers."
                        : "The client is keeping its saved server identity and will reconnect automatically.",
                    nextReconnectAt: DateTimeOffset.UtcNow.Add(delay),
                    lastError: exception.Message));
            }
            catch (Exception exception)
            {
                _consecutiveMonitorFailures++;
                delay = TimeSpan.FromSeconds(30);
                Publish(CreateSnapshot(
                    ConnectedAccessState.Unavailable,
                    "Server identity check failed",
                    "Radio Vault will not replace the saved server identity. Review the connection in Settings.",
                    nextReconnectAt: DateTimeOffset.UtcNow.Add(delay),
                    lastError: exception.Message));
            }
        }
    }

    private void PublishHealthyConnection(WebFederationBootstrap bootstrap)
    {
        // A successful independent probe proves the server is live. Clear
        // short-lived responses once when recovering from an outage, but retain
        // them during steady 15-second health probes. Previously every successful
        // probe defeated the bounded memory cache and made remote tabs needlessly
        // return to the network.
        var recovered = !_current.IsLive;
        _runtimeConnection?.MarkServerLive(invalidateMemoryCache: recovered);
        _preferences.LastConnectedAt = DateTimeOffset.UtcNow;
        _preferences.CapabilityGeneration = bootstrap.Server.CapabilityGeneration;
        _preferences.Save();
        Publish(CreateSnapshot(
            ConnectedAccessState.Live,
            "Connection healthy",
            $"{bootstrap.Library.BroadcastCount:N0} broadcasts and {bootstrap.Library.ShowCount:N0} shows are live from {bootstrap.Server.DisplayName}.",
            broadcastCount: bootstrap.Library.BroadcastCount,
            showCount: bootstrap.Library.ShowCount));
    }

    private static async Task<IReadOnlyList<DiscoveredServer>> DiscoverCoreAsync(
        int port, TimeSpan duration, CancellationToken cancellationToken)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true, ExclusiveAddressUse = false };
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, port));
        var joined = 0;
        foreach (var network in LanDiscoveryNetwork.GetPrivateIpv4Interfaces())
        {
            try { client.JoinMulticastGroup(DiscoveryAddress, network.Address); joined++; } catch (SocketException) { }
        }
        if (joined == 0) try { client.JoinMulticastGroup(DiscoveryAddress); } catch (SocketException) { }

        var found = new Dictionary<string, DiscoveredServer>(StringComparer.Ordinal);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(duration);
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                var packet = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                var announcement = JsonSerializer.Deserialize<WebLanDiscoveryAnnouncement>(packet.Buffer, JsonOptions);
                if (announcement is null ||
                    !string.Equals(announcement.Protocol, "radiovault-lan-v1", StringComparison.Ordinal) ||
                    !Guid.TryParse(announcement.InstanceId, out _) || announcement.SecurePort is < 1024 or > 65535 ||
                    NativeServerConnectionPreferences.NormalizeThumbprint(announcement.CertificateThumbprint).Length < 32)
                    continue;
                found[announcement.InstanceId] = new DiscoveredServer(
                    announcement.InstanceId,
                    string.IsNullOrWhiteSpace(announcement.DisplayName) ? "Radio Vault Server" : announcement.DisplayName.Trim(),
                    packet.RemoteEndPoint.Address.ToString(), announcement.SecurePort,
                    NativeServerConnectionPreferences.NormalizeThumbprint(announcement.CertificateThumbprint),
                    announcement.AppVersion, announcement.PairingAvailable, announcement.PairedDesktopClients);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { break; }
            catch (JsonException) { }
            catch (SocketException) { break; }
        }
        return found.Values.OrderByDescending(server => server.PairingAvailable)
            .ThenBy(server => server.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static HttpClient CreatePinnedClient(string address, int port, string thumbprint, string? accessToken)
    {
        var expected = NativeServerConnectionPreferences.NormalizeThumbprint(thumbprint);
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null && string.Equals(
                    NativeServerConnectionPreferences.NormalizeThumbprint(certificate.GetCertHashString()), expected, StringComparison.Ordinal)
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri($"https://{address}:{port}"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        if (!string.IsNullOrWhiteSpace(accessToken)) client.DefaultRequestHeaders.Add("X-RadioVault-Token", accessToken);
        return client;
    }

    private ConnectedAccessSnapshot CreateSnapshot(
        ConnectedAccessState state,
        string status,
        string detail,
        int broadcastCount = 0,
        int showCount = 0,
        DateTimeOffset? nextReconnectAt = null,
        string lastError = "")
        => new(
            state, IsRemoteSession: _isRemoteSession, IsLive: state == ConnectedAccessState.Live,
            IsCachedReadOnly: state == ConnectedAccessState.CachedReadOnly, HasSavedServer: _preferences.HasSavedServer,
            UseRemoteOnStartup: _preferences.UseRemoteOnStartup,
            ServerDisplayName: _isRemoteSession ? _preferences.ServerDisplayName : LocalServerDisplayName,
            ServerAddress: _runtimeConnection?.ServerAddress?.ToString() ?? string.Empty,
            SavedServerDisplayName: _preferences.ServerDisplayName,
            SavedServerAddress: _preferences.HasSavedServer
                ? $"https://{_preferences.ServerAddress}:{_preferences.SecurePort}/"
                : string.Empty,
            StatusText: status, DetailText: detail, LastLiveAt: _preferences.LastConnectedAt,
            NextReconnectAt: nextReconnectAt, BroadcastCount: broadcastCount, ShowCount: showCount,
            CapabilityGeneration: _isRemoteSession ? _preferences.CapabilityGeneration : 0,
            CacheSizeBytes: _runtimeConnection?.CacheSizeBytes ?? 0, LastError: lastError);

    private string LocalServerDisplayName => string.IsNullOrWhiteSpace(_runtimeConnection?.ServerDisplayName)
        ? "Radio Vault Server on this computer"
        : _runtimeConnection.ServerDisplayName;

    private void Publish(ConnectedAccessSnapshot snapshot)
    {
        _current = snapshot;
        StateChanged?.Invoke(this, snapshot);
    }

    private void EnsureSavedServer()
    {
        EnsureAvailable();
        if (!_preferences.HasSavedServer) throw new InvalidOperationException("Pair this client with a Radio Vault Server first.");
    }

    private void EnsureAvailable() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _monitorCancellation.Cancel();
        _monitorCancellation.Dispose();
    }

    private sealed record DiscoveredServer(
        string InstanceId, string DisplayName, string Address, int SecurePort,
        string CertificateThumbprint, string AppVersion, bool PairingAvailable, int PairedDesktopClients);
    private sealed record PairingEnvelope(WebDesktopPairingResult Result);
    private sealed record BootstrapEnvelope(WebFederationBootstrap FederationBootstrap);
}
