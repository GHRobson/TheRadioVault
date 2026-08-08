using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Web.Services;

public sealed partial class LocalWebServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IWebArchiveProvider _archive;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private TcpListener? _listener;
    private TcpListener? _secureListener;
    private CancellationTokenSource? _cancellation;
    private Task? _acceptLoop;
    private Task? _secureAcceptLoop;
    private Task? _discoveryLoop;
    private WebServerOptions _options;
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly ConcurrentDictionary<string, WebPairedDesktopClient> _pairedDesktopClients = new(StringComparer.Ordinal);
    private readonly object _pairingGate = new();
    private string _pairingCode = string.Empty;
    private DateTimeOffset _pairingExpiresAt = DateTimeOffset.MinValue;
    private int _pairingAttemptsRemaining;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _processedMutationIds = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _processedMutationOrder = new();
    private const int ProcessedMutationLimit = 2048;
    private readonly object _positionedWaveSessionsGate = new();
    private readonly Dictionary<string, PositionedWaveSession> _positionedWaveSessions = new(StringComparer.Ordinal);
    private static readonly TimeSpan PositionedWaveSessionIdleLifetime = TimeSpan.FromMinutes(10);
    private const int PositionedWaveSessionSoftLimit = 8;

    public LocalWebServer(IWebArchiveProvider archive, WebServerOptions options, Action<string>? log = null)
    {
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ReplacePairedDesktopClients(_options.PairedDesktopClients);
        _log = log;
    }

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }
    public int Port => _options.Port;
    public int SecurePort => _options.SecurePort;
    public bool IsSecure => _options.SecureAccessEnabled && _options.ServerCertificate is not null;
    public string AccessToken => _options.AccessToken;
    public string RootCertificateThumbprint => _options.RootCertificateThumbprint;
    public bool LanFederationEnabled => _options.LanFederationEnabled && IsSecure;
    public int LanDiscoveryPort => _options.LanDiscoveryPort;
    public int PairedDesktopClientCount => _pairedDesktopClients.Count;
    public IReadOnlyList<WebPairedDesktopClient> PairedDesktopClients => _pairedDesktopClients.Values
        .OrderBy(client => client.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public WebDesktopPairingSession? CurrentDesktopPairing
    {
        get
        {
            lock (_pairingGate)
            {
                return IsPairingActiveUnsafe()
                    ? new WebDesktopPairingSession(_pairingCode, _pairingExpiresAt)
                    : null;
            }
        }
    }

    public WebDesktopPairingSession BeginDesktopPairing()
    {
        if (!IsRunning) throw new InvalidOperationException("Start connected access before creating a remote-client pairing code.");
        if (!IsSecure) throw new InvalidOperationException("HTTPS must be enabled before pairing another remote client.");
        if (!_options.LanFederationEnabled) throw new InvalidOperationException("Enable Multi-Device Library Access before creating a pairing code.");

        lock (_pairingGate)
        {
            _pairingCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
            _pairingExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            _pairingAttemptsRemaining = 10;
            return new WebDesktopPairingSession(_pairingCode, _pairingExpiresAt);
        }
    }

    public void CancelDesktopPairing()
    {
        lock (_pairingGate)
        {
            _pairingCode = string.Empty;
            _pairingExpiresAt = DateTimeOffset.MinValue;
            _pairingAttemptsRemaining = 0;
        }
    }

    public bool RevokeDesktopClient(string clientId)
        => !string.IsNullOrWhiteSpace(clientId) && _pairedDesktopClients.TryRemove(clientId.Trim(), out _);

    public IReadOnlyList<string> GetAccessUrls()
    {
        var token = Uri.EscapeDataString(_options.AccessToken);
        var scheme = IsSecure ? "https" : "http";
        var port = IsSecure ? _options.SecurePort : _options.Port;
        return GetLanAddresses().Select(address => $"{scheme}://{address}:{port}/?token={token}").ToArray();
    }

    public IReadOnlyList<string> GetSecureSetupUrls()
    {
        if (!IsSecure) return Array.Empty<string>();
        var token = Uri.EscapeDataString(_options.AccessToken);
        return GetLanAddresses().Select(address => $"http://{address}:{_options.Port}/secure-setup?token={token}").ToArray();
    }

    public IReadOnlyList<string> GetBroadcastUrls(long episodeId)
    {
        var token = Uri.EscapeDataString(_options.AccessToken);
        var scheme = IsSecure ? "https" : "http";
        var port = IsSecure ? _options.SecurePort : _options.Port;
        return GetLanAddresses().Select(address => $"{scheme}://{address}:{port}/broadcast/{episodeId}?token={token}").ToArray();
    }

    public void UpdateOptions(WebServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            _options = options;
            ReplacePairedDesktopClients(options.PairedDesktopClients);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (IsRunning) return;
            LastError = null;
            if (_options.SecureAccessEnabled && _options.ServerCertificate is null)
                throw new InvalidOperationException("Secure web access is enabled, but no HTTPS certificate is available.");
            if (_options.SecureAccessEnabled && _options.SecurePort == _options.Port)
                throw new InvalidOperationException("The HTTP setup port and HTTPS port must be different.");

            try
            {
                _listener = new TcpListener(_options.LoopbackOnly ? IPAddress.Loopback : IPAddress.Any, _options.Port);
                _listener.Start(32);
                if (_options.SecureAccessEnabled)
                {
                    _secureListener = new TcpListener(_options.LoopbackOnly ? IPAddress.Loopback : IPAddress.Any, _options.SecurePort);
                    _secureListener.Start(32);
                }

                _cancellation = new CancellationTokenSource();
                _startedAt = DateTimeOffset.UtcNow;
                IsRunning = true;
                // Bind every background loop to this exact start generation. A fast
                // stop/start must never let an older cancelled lambda observe the new
                // listener through a mutable field and compete with the fresh loop.
                var startCancellation = _cancellation!;
                var startListener = _listener!;
                var startSecureListener = _secureListener;
                _acceptLoop = Task.Run(() => AcceptLoopAsync(startListener, secure: false, cancellationToken: startCancellation.Token));
                _secureAcceptLoop = startSecureListener is null
                    ? null
                    : Task.Run(() => AcceptLoopAsync(startSecureListener, secure: true, cancellationToken: startCancellation.Token));
                _discoveryLoop = LanFederationEnabled
                    ? Task.Run(() => DiscoveryLoopAsync(startCancellation.Token))
                    : null;
                _log?.Invoke(_options.SecureAccessEnabled
                    ? LanFederationEnabled
                        ? $"Started HTTP setup on port {_options.Port}, HTTPS on port {_options.SecurePort}, and LAN server discovery on UDP {_options.LanDiscoveryPort}."
                        : $"Started HTTP setup on port {_options.Port} and HTTPS on port {_options.SecurePort}."
                    : $"Started on LAN port {_options.Port}.");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                IsRunning = false;
                try { _listener?.Stop(); } catch { }
                try { _secureListener?.Stop(); } catch { }
                _listener = null;
                _secureListener = null;
                _log?.Invoke($"Could not start: {ex}");
                throw;
            }
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        TcpListener? listener;
        TcpListener? secureListener;
        Task? acceptLoop;
        Task? secureAcceptLoop;
        Task? discoveryLoop;
        lock (_gate)
        {
            if (!IsRunning && _listener is null && _secureListener is null && _cancellation is null) return;
            IsRunning = false;
            cancellation = _cancellation;
            listener = _listener;
            secureListener = _secureListener;
            acceptLoop = _acceptLoop;
            secureAcceptLoop = _secureAcceptLoop;
            discoveryLoop = _discoveryLoop;
            _cancellation = null;
            _listener = null;
            _secureListener = null;
            _acceptLoop = null;
            _secureAcceptLoop = null;
            _discoveryLoop = null;
        }

        lock (_pairingGate)
        {
            _pairingCode = string.Empty;
            _pairingExpiresAt = DateTimeOffset.MinValue;
            _pairingAttemptsRemaining = 0;
        }

        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        try { listener?.Stop(); } catch (ObjectDisposedException) { }
        try { secureListener?.Stop(); } catch (ObjectDisposedException) { }

        if (cancellation is not null)
        {
            var loops = new[] { acceptLoop, secureAcceptLoop, discoveryLoop }.Where(x => x is not null).Cast<Task>().ToArray();
            if (loops.Length == 0 || loops.All(x => x.IsCompleted))
            {
                cancellation.Dispose();
            }
            else
            {
                _ = Task.WhenAll(loops).ContinueWith(
                    _ => cancellation.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        DisposePositionedWaveSessions();
        _log?.Invoke("Stopped.");
    }

    private async Task DiscoveryLoopAsync(CancellationToken cancellationToken)
    {
        var multicastAddress = IPAddress.Parse("239.255.82.86");

        while (!cancellationToken.IsCancellationRequested)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(CreateDiscoveryAnnouncement(), JsonOptions);
            var interfaces = LanDiscoveryNetwork.GetPrivateIpv4Interfaces();
            var successfulSends = 0;

            if (interfaces.Count == 0)
            {
                // Keep a default-route fallback for unusual adapters whose
                // subnet mask is unavailable to NetworkInterface.
                try
                {
                    using var fallback = new UdpClient(AddressFamily.InterNetwork)
                    {
                        EnableBroadcast = true,
                        MulticastLoopback = true,
                        Ttl = 1
                    };
                    await fallback.SendAsync(
                        bytes,
                        bytes.Length,
                        new IPEndPoint(multicastAddress, _options.LanDiscoveryPort)).ConfigureAwait(false);
                    await fallback.SendAsync(
                        bytes,
                        bytes.Length,
                        new IPEndPoint(IPAddress.Broadcast, _options.LanDiscoveryPort)).ConfigureAwait(false);
                    successfulSends += 2;
                }
                catch (Exception ex) when (ex is SocketException or InvalidOperationException)
                {
                    _log?.Invoke($"LAN server discovery default-route broadcast failed: {ex.Message}");
                }
            }
            else
            {
                foreach (var network in interfaces)
                {
                    try
                    {
                        using var sender = new UdpClient(new IPEndPoint(network.Address, 0))
                        {
                            EnableBroadcast = true,
                            MulticastLoopback = true,
                            Ttl = 1
                        };
                        try
                        {
                            sender.Client.SetSocketOption(
                                SocketOptionLevel.IP,
                                SocketOptionName.MulticastInterface,
                                network.Address.GetAddressBytes());
                        }
                        catch (SocketException)
                        {
                            // Binding the sender to the interface address is
                            // sufficient on adapters that reject this option.
                        }

                        await sender.SendAsync(
                            bytes,
                            bytes.Length,
                            new IPEndPoint(multicastAddress, _options.LanDiscoveryPort)).ConfigureAwait(false);
                        successfulSends++;

                        await sender.SendAsync(
                            bytes,
                            bytes.Length,
                            new IPEndPoint(network.BroadcastAddress, _options.LanDiscoveryPort)).ConfigureAwait(false);
                        successfulSends++;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (SocketException ex)
                    {
                        _log?.Invoke($"LAN server discovery failed on {network.Name} ({network.Address}): {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"LAN server discovery error on {network.Name} ({network.Address}): {ex.Message}");
                    }
                }
            }

            if (successfulSends == 0)
                _log?.Invoke("LAN server discovery could not send on any private IPv4 network adapter.");

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private WebLanDiscoveryAnnouncement CreateDiscoveryAnnouncement()
    {
        var pairing = CurrentDesktopPairing;
        return new WebLanDiscoveryAnnouncement(
            "radiovault-lan-v1",
            _options.ServerInstanceId,
            string.IsNullOrWhiteSpace(_options.ServerDisplayName) ? "Radio Vault" : _options.ServerDisplayName.Trim(),
            _options.AppVersion,
            WebApiRoutes.Version,
            _options.DatabaseSchemaVersion,
            _options.CapabilityGeneration,
            _options.SecurePort,
            _options.ServerCertificateThumbprint,
            pairing is not null,
            _pairedDesktopClients.Count,
            DateTimeOffset.UtcNow);
    }

    private async Task AcceptLoopAsync(TcpListener listener, bool secure, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, secure, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException) { client?.Dispose(); break; }
            catch (ObjectDisposedException) { client?.Dispose(); break; }
            catch (Exception ex)
            {
                client?.Dispose();
                _log?.Invoke($"Accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, bool secure, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            if (client.Client.RemoteEndPoint is IPEndPoint remote && !IsPrivateOrLoopback(remote.Address)) return;

            Stream stream = client.GetStream();
            SslStream? secureStream = null;
            try
            {
                if (secure)
                {
                    var certificate = _options.ServerCertificate
                        ?? throw new InvalidOperationException("The HTTPS certificate is unavailable.");
                    secureStream = new SslStream(stream, leaveInnerStreamOpen: false);
                    await secureStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificateContext = _options.ServerCertificateContext
                            ?? SslStreamCertificateContext.Create(certificate, null, offline: true),
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
                    }, cancellationToken).ConfigureAwait(false);
                    stream = secureStream;
                }

                var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                if (request is null) return;
                var isGet = string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase);
                var isHead = string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
                var isPost = string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase);
                if (!isGet && !isHead && !isPost)
                {
                    await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Only GET, HEAD and selected POST actions are supported.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var uri = new Uri((secure ? "https" : "http") + "://radiovault.local" + request.Target);
                var query = ParseQuery(uri.Query);

                if (uri.AbsolutePath.Equals(WebApiRoutes.FederationPair, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Remote-client pairing requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleDesktopPairingAsync(stream, request, secure, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!IsAuthorizedRequest(request, query))
                {
                    await WriteTextResponseAsync(stream, 401, "Unauthorized", "A valid Radio Vault access link or remote-client token is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (_options.SecureAccessEnabled && !secure)
                {
                    if ((isGet || isHead) && uri.AbsolutePath.Equals("/secure-setup", StringComparison.OrdinalIgnoreCase))
                    {
                        const string setupHeaders = "Cache-Control: no-store\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'\r\n";
                        var setupHtml = BuildSecureSetupHtml(request);
                        await WriteBytesResponseAsync(stream, 200, "OK", Encoding.UTF8.GetBytes(setupHtml), "text/html; charset=utf-8", isHead, cancellationToken, setupHeaders).ConfigureAwait(false);
                        return;
                    }
                    if ((isGet || isHead) && uri.AbsolutePath.Equals("/secure-profile.mobileconfig", StringComparison.OrdinalIgnoreCase))
                    {
                        const string profileHeaders = "Cache-Control: no-store\r\nContent-Disposition: attachment; filename=RadioVault-Secure-Offline-Access.mobileconfig\r\n";
                        await WriteBytesResponseAsync(stream, 200, "OK", _options.MobileConfigurationProfile, "application/x-apple-aspen-config", isHead, cancellationToken, profileHeaders).ConfigureAwait(false);
                        return;
                    }
                    if ((isGet || isHead) && uri.AbsolutePath.Equals("/secure-root.cer", StringComparison.OrdinalIgnoreCase))
                    {
                        const string certificateHeaders = "Cache-Control: no-store\r\nContent-Disposition: attachment; filename=RadioVault-Local-Root-CA.cer\r\n";
                        await WriteBytesResponseAsync(stream, 200, "OK", _options.RootCertificateDer, "application/x-x509-ca-cert", isHead, cancellationToken, certificateHeaders).ConfigureAwait(false);
                        return;
                    }

                    await WriteRedirectAsync(stream, BuildSecureTarget(request), cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (secure && (isGet || isHead) && uri.AbsolutePath.Equals("/manifest.webmanifest", StringComparison.OrdinalIgnoreCase))
                {
                    const string manifestHeaders = "Cache-Control: no-cache\r\n";
                    await WriteBytesResponseAsync(stream, 200, "OK", Encoding.UTF8.GetBytes(BuildWebManifest()), "application/manifest+json; charset=utf-8", isHead, cancellationToken, manifestHeaders).ConfigureAwait(false);
                    return;
                }

                // The shell itself is also allowed when secure access is deliberately
                // disabled (for example, a same-PC browser). Its brand artwork must
                // therefore remain available on that authenticated HTTP surface too.
                if ((isGet || isHead) && TryGetWebAppIcon(uri.AbsolutePath, out var iconBytes))
                {
                    const string iconHeaders = "Cache-Control: public, max-age=86400\r\n";
                    await WriteBytesResponseAsync(stream, 200, "OK", iconBytes, "image/png", isHead, cancellationToken, iconHeaders).ConfigureAwait(false);
                    return;
                }

                if (secure && (isGet || isHead) && uri.AbsolutePath.Equals("/service-worker.js", StringComparison.OrdinalIgnoreCase))
                {
                    const string workerHeaders = "Cache-Control: no-cache\r\nService-Worker-Allowed: /\r\n";
                    await WriteBytesResponseAsync(stream, 200, "OK", Encoding.UTF8.GetBytes(ServiceWorkerJavaScript), "text/javascript; charset=utf-8", isHead, cancellationToken, workerHeaders).ConfigureAwait(false);
                    return;
                }

                if ((isGet || isHead) && (uri.AbsolutePath == "/" || uri.AbsolutePath.Equals("/index.html", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.StartsWith("/broadcast/", StringComparison.OrdinalIgnoreCase)))
                {
                    var securityHeaders = (secure ? "Cache-Control: no-cache\r\n" : "Cache-Control: no-store\r\n") + "Content-Security-Policy: default-src 'self'; img-src 'self' data:; media-src 'self'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; worker-src 'self'; base-uri 'none'; frame-ancestors 'none'\r\n";
                    await WriteBytesResponseAsync(stream, 200, "OK", Encoding.UTF8.GetBytes(BuildIndexHtml()), "text/html; charset=utf-8", request.Method == "HEAD", cancellationToken, securityHeaders).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.ServerInfo, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleServerInfoApiAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.FederationStatus, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleFederationStatusAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.FederationBootstrap, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleFederationBootstrapAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.FederationLibrarySync, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isGet && !isHead)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Library synchronization supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleFederationLibrarySyncAsync(stream, query, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.FederationLibraryScan, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isGet && !isHead && !isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Library scanning supports GET, HEAD and POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleFederationLibraryScanAsync(stream, request, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.FederationParity, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isGet && !isHead)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Remote-client parity supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleFederationParityAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.FederationSettings, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isGet && !isHead)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Server settings support GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleFederationSettingsAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.FederationPlaybackPreferences, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isGet && !isHead && !isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Playback preferences support GET, HEAD and POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleFederationPlaybackPreferencesAsync(stream, request, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.FederationResearchWorkspace, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isGet && !isHead)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Research workspace browsing supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleFederationResearchWorkspaceAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.FederationResearchUndated, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isGet && !isHead)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Undated Research browsing supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleFederationResearchUndatedAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchFederationResearchCoverageByShow(uri.AbsolutePath, out var researchCoverageShow))
                {
                    if (!isGet && !isHead)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Research coverage supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleFederationResearchCoverageByShowAsync(stream, researchCoverageShow, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchFederationResearchCoverage(uri.AbsolutePath, out var researchCoverageCollectionId))
                {
                    if (!isGet && !isHead)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Research coverage supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleFederationResearchCoverageAsync(stream, researchCoverageCollectionId, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchFederationResearchUndatedDate(uri.AbsolutePath, out var undatedEpisodeId))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Manual broadcast dating requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleFederationResearchUndatedDateAsync(stream, request, undatedEpisodeId, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchFederationResearchWorkspaceRecord(uri.AbsolutePath, out var researchWorkspaceRecordId))
                {
                    if (!isGet && !isHead)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Research record browsing supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleFederationResearchWorkspaceRecordAsync(stream, researchWorkspaceRecordId, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.FederationResearchImportPreview, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Research pack analysis requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleFederationResearchImportPreviewAsync(stream, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.FederationResearchImportApply, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Research pack import requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleFederationResearchImportApplyAsync(stream, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.FederationResearchImportStatus, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Research import status requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleFederationResearchImportStatusAsync(stream, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.FederationResearchImportCancel, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Research pack cancellation requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleFederationResearchImportCancelAsync(stream, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.FederationResearchExport, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Research pack export requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleFederationResearchExportAsync(stream, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.FederationWikiImportPreview, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Wiki pack analysis requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleFederationWikiImportPreviewAsync(stream, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.FederationWikiImportApply, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Wiki pack import requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleFederationWikiImportApplyAsync(stream, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.FederationWikiExport, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Wiki pack export requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleFederationWikiExportAsync(stream, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.Bootstrap, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleBootstrapApiAsync(stream, query, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchClientOperation(uri.AbsolutePath, WebApiRoutes.ClientResearch, out var researchOperation))
                {
                    await HandleClientOperationAsync(stream, request, researchOperation, _archive.ExecuteClientResearchAsync, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchClientOperation(uri.AbsolutePath, WebApiRoutes.ClientTranscripts, out var transcriptOperation))
                {
                    await HandleClientOperationAsync(stream, request, transcriptOperation, _archive.ExecuteClientTranscriptAsync, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchClientOperation(uri.AbsolutePath, WebApiRoutes.ClientSpeakers, out var speakerOperation))
                {
                    await HandleClientOperationAsync(stream, request, speakerOperation, _archive.ExecuteClientSpeakerAsync, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchClientOperation(uri.AbsolutePath, WebApiRoutes.ClientTranscription, out var transcriptionOperation))
                {
                    await HandleClientOperationAsync(stream, request, transcriptionOperation, _archive.ExecuteClientTranscriptionAsync, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchClientOperation(uri.AbsolutePath, WebApiRoutes.ClientWiki, out var wikiOperation))
                {
                    await HandleClientOperationAsync(stream, request, wikiOperation, _archive.ExecuteClientWikiAsync, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.ClientLibraryOverview, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleClientLibraryOverviewAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.ClientLibraryBrowse, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleClientLibraryBrowseAsync(stream, query, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.ClientLibraryArchivePeriods, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleClientLibraryArchivePeriodsAsync(stream, query, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.ClientLibrarySearchFacets, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleClientLibrarySearchFacetsAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.ClientLibrarySearchSuggestions, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleClientLibrarySearchSuggestionsAsync(stream, query, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchClientLibraryBroadcast(uri.AbsolutePath, out var clientLibraryEpisodeId))
                {
                    await HandleClientLibraryBroadcastAsync(stream, clientLibraryEpisodeId, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchClientBroadcast(uri.AbsolutePath, out var clientDetailsEpisodeId))
                {
                    await HandleClientBroadcastDetailsAsync(stream, clientDetailsEpisodeId, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals("/api/episodes", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.Equals(WebApiRoutes.Broadcasts, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleEpisodesApiAsync(stream, query, request.Method == "HEAD", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals("/api/shows", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.Equals(WebApiRoutes.Shows, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleShowsApiAsync(stream, request.Method == "HEAD", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.Search, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleEpisodesApiAsync(stream, query, request.Method == "HEAD", cancellationToken, forceSearchView: true).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.Favourites, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleEpisodesApiAsync(stream, query, isHead, cancellationToken, forcedView: "favorites").ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.Events, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleEventsApiAsync(stream, query, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.Jobs, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleJobsApiAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchJobCancel(uri.AbsolutePath, out var cancelJobId))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleJobCancellationAsync(stream, cancelJobId, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchBroadcastAction(uri.AbsolutePath, "offline-progress", out var offlineProgressEpisodeId))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Offline progress synchronisation requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleOfflineProgressAsync(stream, request, offlineProgressEpisodeId, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchBroadcastAction(uri.AbsolutePath, "favourite", out var favouriteEpisodeId))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleFavouriteMutationAsync(stream, favouriteEpisodeId, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchBroadcastAction(uri.AbsolutePath, "listening-status", out var statusEpisodeId))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleListeningStatusMutationAsync(stream, statusEpisodeId, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchBroadcastAction(uri.AbsolutePath, "metadata", out var metadataEpisodeId))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Metadata updates require POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleBroadcastMetadataMutationAsync(stream, metadataEpisodeId, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.Transcripts, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isGet && !isHead)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Transcript browsing supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleTranscriptsApiAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchBroadcastAction(uri.AbsolutePath, "transcript", out var transcriptEpisodeId))
                {
                    if (!isGet && !isHead)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Transcript access supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleTranscriptApiAsync(stream, transcriptEpisodeId, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchBroadcastAction(uri.AbsolutePath, "moments", out var momentsEpisodeId))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Moment creation requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleMomentCreateAsync(stream, momentsEpisodeId, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchMomentDelete(uri.AbsolutePath, out var momentEpisodeId, out var momentId))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Moment deletion requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleMomentDeleteAsync(stream, momentEpisodeId, momentId, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchMomentUpdate(uri.AbsolutePath, out var updateMomentId))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Moment editing requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleMomentUpdateAsync(stream, updateMomentId, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchCanonicalMediaManifest(uri.AbsolutePath, out var manifestEpisodeId))
                {
                    await HandleCanonicalMediaManifestAsync(stream, manifestEpisodeId, query, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchCanonicalMediaStart(uri.AbsolutePath, out var startEpisodeId))
                {
                    await HandleCanonicalMediaStartAsync(stream, request, startEpisodeId, query, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchCanonicalMediaPart(uri.AbsolutePath, out var mediaEpisodeId, out var mediaFileId))
                {
                    await HandleCanonicalMediaPartAsync(stream, request, mediaEpisodeId, mediaFileId, query, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.StartsWith(WebApiRoutes.Broadcasts + "/", StringComparison.OrdinalIgnoreCase) && long.TryParse(uri.AbsolutePath[(WebApiRoutes.Broadcasts.Length + 1)..], out var detailsEpisodeId))
                {
                    await HandleBroadcastDetailsApiAsync(stream, detailsEpisodeId, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.StartsWith(WebApiRoutes.Root + "/research/", StringComparison.OrdinalIgnoreCase) && long.TryParse(uri.AbsolutePath[(WebApiRoutes.Root.Length + "/research/".Length)..], out var researchEpisodeId))
                {
                    await HandleResearchApiAsync(stream, researchEpisodeId, request.Method == "HEAD", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.ArchiveHealth, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleArchiveHealthApiAsync(stream, request.Method == "HEAD", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.PlayerTransferBegin, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandlePlaybackTransferBeginAsync(stream, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.PlayerTransferReady, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandlePlaybackTransferReadyAsync(stream, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.PlayerTransferCommit, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandlePlaybackTransferCommitAsync(stream, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.PlayerTransferCancel, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandlePlaybackTransferCancelAsync(stream, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.PlayerTransferSourceStopped, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandlePlaybackTransferSourceStoppedAsync(stream, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.PlayerCommand, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandlePlaybackCommandAsync(stream, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.PlayerWebProgress, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleWebPlaybackUpdateAsync(stream, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.Player, StringComparison.OrdinalIgnoreCase))
                {
                    await HandlePlayerApiAsync(stream, request.Method == "HEAD", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.QueueAdd, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleQueueAddAsync(stream, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.QueueClear, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleQueueClearAsync(stream, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchQueueAction(uri.AbsolutePath, "remove", out var removeQueueId))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleQueueRemoveAsync(stream, removeQueueId, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (TryMatchQueueAction(uri.AbsolutePath, "move", out var moveQueueId))
                {
                    if (!isPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Use POST for this action.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleQueueMoveAsync(stream, moveQueueId, request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.Queue, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleQueueApiAsync(stream, request.Method == "HEAD", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.Equals(WebApiRoutes.MomentsAll, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isGet && !isHead)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Moment browsing supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleMomentsApiAsync(stream, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.StartsWith("/artwork/", StringComparison.OrdinalIgnoreCase) && long.TryParse(uri.AbsolutePath[9..], out var artworkEpisodeId))
                {
                    await HandleArtworkAsync(stream, request.Method == "HEAD", artworkEpisodeId, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (uri.AbsolutePath.StartsWith("/audio/", StringComparison.OrdinalIgnoreCase) && long.TryParse(uri.AbsolutePath[7..], out var episodeId))
                {
                    await HandleAudioAsync(stream, request, episodeId, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await WriteTextResponseAsync(stream, 404, "Not Found", "Not found.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Request error: {ex}");
                try { await WriteTextResponseAsync(stream, 500, "Internal Server Error", "The request could not be completed.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false); } catch { }
            }
        }
    }

    private async Task HandleServerInfoApiAsync(Stream stream, bool headOnly, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            apiVersion = WebApiRoutes.Version,
            server = BuildServerInfo(),
            web = new
            {
                productName = "Radio Vault Web",
                accessUrl = GetAccessUrls().FirstOrDefault() ?? string.Empty,
                secureSetupUrl = GetSecureSetupUrls().FirstOrDefault() ?? string.Empty
            }
        }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleDesktopPairingAsync(Stream stream, HttpRequest request, bool secure, CancellationToken cancellationToken)
    {
        if (!secure || !IsSecure)
        {
            await WriteTextResponseAsync(stream, 426, "Upgrade Required", "Remote-client pairing is available only through the Radio Vault HTTPS listener.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!_options.LanFederationEnabled)
        {
            await WriteTextResponseAsync(stream, 403, "Forbidden", "Multi-Device Library Access is not enabled on this Radio Vault server.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!TryDeserialize<WebDesktopPairingRequest>(request.Body, out var pairing) || pairing is null)
        {
            await WriteDesktopPairingResultAsync(
                stream, 400, "Bad Request", false,
                "The pairing request body was empty or invalid. Update both Radio Vault computers to the same build and try again.",
                null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var code = pairing.Code?.Trim() ?? string.Empty;
        if (code.Length != 6 || code.Any(ch => !char.IsDigit(ch)))
        {
            await WriteDesktopPairingResultAsync(
                stream, 400, "Bad Request", false,
                "The pairing code must contain exactly six digits.",
                null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var clientId = NormalizeDesktopClientId(pairing.ClientId);
        var displayName = NormalizeDesktopDisplayName(pairing.DisplayName);
        if (clientId.Length == 0 || displayName.Length == 0)
        {
            await WriteDesktopPairingResultAsync(
                stream, 400, "Bad Request", false,
                "The remote-client identity is invalid. Restart Radio Vault on the remote client and generate a fresh pairing code.",
                null, cancellationToken).ConfigureAwait(false);
            return;
        }

        WebPairedDesktopClient? pairedClient = null;
        var message = "The pairing code is invalid or has expired.";
        lock (_pairingGate)
        {
            if (IsPairingActiveUnsafe() && _pairingAttemptsRemaining > 0 && FixedTimeEquals(code, _pairingCode))
            {
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                pairedClient = new WebPairedDesktopClient(clientId, displayName, token, DateTimeOffset.UtcNow);
                _pairedDesktopClients[clientId] = pairedClient;
                _pairingCode = string.Empty;
                _pairingExpiresAt = DateTimeOffset.MinValue;
                _pairingAttemptsRemaining = 0;
                message = "This remote client is now trusted by the Radio Vault server.";
            }
            else
            {
                _pairingAttemptsRemaining = Math.Max(0, _pairingAttemptsRemaining - 1);
                if (_pairingAttemptsRemaining == 0)
                {
                    _pairingCode = string.Empty;
                    _pairingExpiresAt = DateTimeOffset.MinValue;
                }
            }
        }

        if (pairedClient is null)
        {
            await WriteDesktopPairingResultAsync(
                stream, 403, "Forbidden", false, message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        try { _options.PairedDesktopClientAdded?.Invoke(pairedClient); }
        catch (Exception ex) { _log?.Invoke($"Could not persist remote client {pairedClient.ClientId}: {ex.Message}"); }

        await WriteDesktopPairingResultAsync(
            stream, 200, "OK", true, message, pairedClient, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteDesktopPairingResultAsync(
        Stream stream,
        int statusCode,
        string reason,
        bool paired,
        string message,
        WebPairedDesktopClient? client,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            apiVersion = WebApiRoutes.Version,
            result = new WebDesktopPairingResult(
                paired,
                message,
                _options.ServerInstanceId,
                _options.ServerDisplayName,
                paired && client is not null ? client.Token : string.Empty,
                _options.ServerCertificateThumbprint,
                _options.SecurePort,
                _options.CapabilityGeneration,
                paired ? client?.PairedAt : null)
        }, JsonOptions);
        await WriteBytesResponseAsync(
            stream, statusCode, reason, bytes, "application/json; charset=utf-8", false, cancellationToken,
            "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleFederationStatusAsync(Stream stream, bool headOnly, CancellationToken cancellationToken)
    {
        var pairing = CurrentDesktopPairing;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            apiVersion = WebApiRoutes.Version,
            federation = new
            {
                role = "server",
                enabled = LanFederationEnabled,
                discoveryProtocol = "radiovault-lan-v1",
                discoveryPort = _options.LanDiscoveryPort,
                pairedDesktopClients = _pairedDesktopClients.Count,
                pairingAvailable = pairing is not null,
                pairingExpiresAt = pairing?.ExpiresAt,
                server = BuildServerInfo()
            }
        }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleFederationBootstrapAsync(Stream stream, bool headOnly, CancellationToken cancellationToken)
    {
        var diagnosticId = Guid.NewGuid().ToString("N")[..8];
        try
        {
            var episodes = _archive.GetEpisodes();
            var showCount = WebEpisodeQuery.GetShows(episodes).Count;
            var queueCount = 0;
            try
            {
                queueCount = _archive.GetQueue().Take(200).Count();
            }
            catch (Exception queueError)
            {
                // Queue contents are not required to establish desktop trust.
                // Record the failure but keep the bootstrap usable.
                _log?.Invoke($"Federation bootstrap {diagnosticId}: queue count unavailable: {queueError.Message}");
            }

            var snapshot = new WebFederationBootstrap(
                BuildServerInfo(),
                BuildLibrarySummary(episodes, showCount),
                queueCount,
                DateTimeOffset.UtcNow);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                apiVersion = WebApiRoutes.Version,
                federationBootstrap = snapshot
            }, JsonOptions);
            await WriteBytesResponseAsync(
                stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken,
                "Cache-Control: no-store\r\n").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Federation bootstrap {diagnosticId} failed: {ex}");
            await WriteApiErrorAsync(
                stream,
                500,
                "Internal Server Error",
                "federation-bootstrap-failed",
                "The server library could not prepare its remote-client bootstrap. Restart Radio Vault on the server and try Test saved connection again.",
                diagnosticId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleBootstrapApiAsync(Stream stream, IReadOnlyDictionary<string, string> query, bool headOnly, CancellationToken cancellationToken)
    {
        var diagnosticId = Guid.NewGuid().ToString("N")[..8];
        try
        {
            var limit = query.TryGetValue("limit", out var rawLimit) && int.TryParse(rawLimit, out var parsedLimit)
                ? Math.Clamp(parsedLimit, 1, 50)
                : 12;
            var episodes = _archive.GetEpisodes();
            var shows = WebEpisodeQuery.GetShows(episodes)
                .Select(x => new WebShowSummary(x.Show, x.Count))
                .ToArray();
            var bootstrap = new WebAnywhereBootstrap
            {
                Server = BuildServerInfo(),
                Library = BuildLibrarySummary(episodes, shows.Length),
                Shows = shows,
                Years = WebEpisodeQuery.GetYears(episodes),
                ContinueListening = WebEpisodeQuery.Apply(episodes, "continue", string.Empty, string.Empty, limit, DateTime.Today),
                Recent = WebEpisodeQuery.Apply(episodes, "recent", string.Empty, string.Empty, limit, DateTime.Today),
                Favourites = WebEpisodeQuery.Apply(episodes, "favorites", string.Empty, string.Empty, limit, DateTime.Today),
                OnThisDay = WebEpisodeQuery.Apply(episodes, "onthisday", string.Empty, string.Empty, limit, DateTime.Today),
                Unheard = WebEpisodeQuery.Apply(episodes, "recent", string.Empty, string.Empty, null, null, null, "unplayed", limit, DateTime.Today),
                Playback = _archive.GetPlaybackSession(),
                Queue = _archive.GetQueue().Take(200).ToArray(),
                GeneratedAt = DateTimeOffset.UtcNow
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                apiVersion = WebApiRoutes.Version,
                bootstrap
            }, JsonOptions);
            await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Anywhere bootstrap {diagnosticId} failed: {ex}");
            await WriteApiErrorAsync(
                stream,
                500,
                "Internal Server Error",
                "bootstrap-failed",
                "Radio Vault could not prepare the remote-client bootstrap.",
                diagnosticId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteApiErrorAsync(
        Stream stream,
        int statusCode,
        string reason,
        string code,
        string message,
        string diagnosticId,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            apiVersion = WebApiRoutes.Version,
            error = new
            {
                code,
                message,
                diagnosticId
            }
        }, JsonOptions);
        await WriteBytesResponseAsync(
            stream, statusCode, reason, bytes, "application/json; charset=utf-8", false, cancellationToken,
            "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private WebServerInfo BuildServerInfo()
    {
        var instanceId = Guid.TryParse(_options.ServerInstanceId, out var parsed)
            ? parsed.ToString("D")
            : _options.ServerInstanceId.Trim();
        return new WebServerInfo(
            instanceId,
            string.IsNullOrWhiteSpace(_options.ServerDisplayName) ? "Radio Vault" : _options.ServerDisplayName.Trim(),
            _options.AppVersion,
            WebApiRoutes.Version,
            _options.DatabaseSchemaVersion,
            _options.CapabilityGeneration,
            IsSecure,
            _startedAt,
            DateTimeOffset.UtcNow,
            GetServerCapabilities());
    }

    private IReadOnlyList<WebServerCapability> GetServerCapabilities()
        => new[]
        {
            new WebServerCapability("library.read", "Browse canonical library", "read", true),
            new WebServerCapability("library.search", "Search broadcasts, people and topics", "read", true),
            new WebServerCapability("broadcast.details", "Broadcast details and research", "read", true),
            new WebServerCapability("moments.write", "Create and delete Moments", "read-write", true),
            new WebServerCapability("transcripts.read", "Read timed transcripts", "read", true),
            new WebServerCapability("media.canonical", "Canonical multipart streaming", "read", true),
            new WebServerCapability("playback.shared", "Shared Radio Vault app and phone playback session", "write", true),
            new WebServerCapability("playback.handoff", "Visible server, desktop-client and phone playback handoff", "read-write", true),
            new WebServerCapability("playback.transactional-handoff", "Prepare, verify and atomically commit playback moves", "read-write", true,
                "The source remains authoritative until the target decoder is aligned and ready."),
            new WebServerCapability("progress.sync", "Online and offline listening-progress synchronisation", "write", true),
            new WebServerCapability("favourites.write", "Favourite updates", "write", true),
            new WebServerCapability("queue.write", "Shared queue management", "write", true),
            new WebServerCapability("offline.manual", "Manual PWA downloads", "client", IsSecure, IsSecure ? string.Empty : "Secure Web access is required for installable offline use."),
            new WebServerCapability("events.poll", "Incremental change feed", "read", true),
            new WebServerCapability("jobs.read", "Background task status and cancellation", "write", true),
            new WebServerCapability("archive.health", "Archive Health summary", "read", true),
            new WebServerCapability("library.scan", "Manual and automatic authoritative Library scanning", "read-write", true),
            new WebServerCapability("library.facets", "Server-side show, year, month, date and listening-status filters", "read", true),
            new WebServerCapability("library.pagination", "Bounded canonical-library pages with total counts", "read", true),
            new WebServerCapability("mutations.idempotent", "Retry-safe offline mutation delivery", "write", true),
            new WebServerCapability("offline.repair", "Device download cache audit and repair", "client", true),
            new WebServerCapability("client.diagnostics", "Privacy-safe browser diagnostics, export and reconnect history", "client", true),
            new WebServerCapability("client.recovery", "Safe application-shell update and repair without deleting downloaded media", "client", true),
            new WebServerCapability(
                "lan.desktop-client",
                "Trusted remote-client bootstrap and application-service access",
                "read-write",
                LanFederationEnabled,
                LanFederationEnabled
                    ? "Remote clients can discover, pair and bootstrap from this server."
                    : "Enable HTTPS and Multi-Device Library Access to accept paired remote clients."),
            new WebServerCapability(
                "lan.full-shell",
                "Remote-client application shell",
                "read-write",
                LanFederationEnabled,
                LanFederationEnabled
                    ? "Remote clients can use this server as their main Radio Vault library without opening the local database."
                    : "Enable HTTPS and Multi-Device Library Access to provide the remote-client shell."),
            new WebServerCapability(
                "lan.remote-playback",
                "Certificate-pinned canonical playback for remote clients",
                "read-write",
                LanFederationEnabled,
                LanFederationEnabled
                    ? "Remote clients can stream canonical multipart media and synchronise deliberate seeks."
                    : "Enable Multi-Device Library Access to stream to remote clients."),
            new WebServerCapability(
                "lan.write-through",
                "Server favourites, listening status, queue and progress",
                "write",
                LanFederationEnabled,
                LanFederationEnabled
                    ? "Remote client mutations are written to this server library."
                    : "Enable Multi-Device Library Access to accept remote-client mutations."),
            new WebServerCapability(
                "lan.research-packs",
                "Server research pack import and export",
                "read-write",
                LanFederationEnabled,
                LanFederationEnabled
                    ? "Remote clients can preview, import and export research packs through the normal Research workspace."
                    : "Enable Multi-Device Library Access to use remote research packs."),
            new WebServerCapability(
                "lan.settings-parity",
                "Server archive and playback settings",
                "read-write",
                LanFederationEnabled,
                LanFederationEnabled
                    ? "Remote clients can read server archive state and synchronize playback preferences."
                    : "Enable Multi-Device Library Access to expose server settings."),
            new WebServerCapability(
                "lan.cache-sync",
                "Persistent remote-client library cache and incremental synchronization",
                "read",
                LanFederationEnabled,
                LanFederationEnabled
                    ? "Remote clients can launch from an encrypted metadata cache and apply bounded server deltas."
                    : "Enable Multi-Device Library Access to synchronize remote-client caches."),
            new WebServerCapability(
                "lan.parity-audit",
                "Remote-client parity contract and diagnostics",
                "read",
                LanFederationEnabled,
                LanFederationEnabled
                    ? "Remote clients can verify every normal application surface against this server and export a parity diagnostic snapshot."
                    : "Enable Multi-Device Library Access to expose the remote-client parity contract."),
            new WebServerCapability(
                "lan.research-workspace",
                "Remote-client Research workspace",
                "read",
                LanFederationEnabled,
                LanFederationEnabled
                    ? "Remote clients can browse the server Research workspace without opening their local database."
                    : "Enable Multi-Device Library Access to expose the server Research workspace."),
            new WebServerCapability(
                "lan.discovery",
                "Private-network server discovery",
                "discovery",
                LanFederationEnabled,
                LanFederationEnabled ? $"UDP discovery is active on port {_options.LanDiscoveryPort}." : "Discovery is disabled.")
        };

    private static WebLibrarySummary BuildLibrarySummary(IReadOnlyList<WebEpisode> episodes, int showCount)
        => new(
            episodes.Count,
            showCount,
            episodes.Count(x => x.Favourite),
            episodes.Count(x => x.PositionMs > 0 && !x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)),
            episodes.Count(x => x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)),
            episodes.Select(x => x.AirDate).Max(),
            episodes.Select(x => x.LastPlayedAt).Max());

    private async Task HandleEpisodesApiAsync(Stream stream, IReadOnlyDictionary<string, string> query, bool headOnly, CancellationToken cancellationToken, bool forceSearchView = false, string? forcedView = null)
    {
        var search = query.TryGetValue("q", out var q) ? q.Trim() : string.Empty;
        var show = query.TryGetValue("show", out var collection) ? collection.Trim() : string.Empty;
        var view = forcedView ?? (query.TryGetValue("view", out var requestedView) ? requestedView.Trim().ToLowerInvariant() : "recent");
        if (forceSearchView) view = "recent";
        var limit = query.TryGetValue("limit", out var rawLimit) && int.TryParse(rawLimit, out var parsedLimit) ? Math.Clamp(parsedLimit, 1, 150) : 80;
        var offset = query.TryGetValue("offset", out var rawOffset) && int.TryParse(rawOffset, out var parsedOffset) ? Math.Max(0, parsedOffset) : 0;
        int? year = query.TryGetValue("year", out var rawYear) && int.TryParse(rawYear, out var parsedYear) ? parsedYear : null;
        int? month = query.TryGetValue("month", out var rawMonth) && int.TryParse(rawMonth, out var parsedMonth) ? parsedMonth : null;
        DateTime? exactDate = query.TryGetValue("date", out var rawDate) && DateTime.TryParseExact(rawDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate) ? parsedDate : null;
        var status = query.TryGetValue("status", out var requestedStatus) ? requestedStatus.Trim() : string.Empty;

        var page = WebEpisodeQuery.ApplyPage(_archive.GetEpisodes(), view, search, show, year, month, exactDate, status, offset, limit, DateTime.Today);
        var payload = page.Episodes.Select(x => new
        {
            id = x.Id,
            canonicalBroadcastId = x.CanonicalBroadcastId,
            identityKind = x.IdentityKind,
            show = x.Show,
            title = x.Title,
            date = x.AirDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            summary = x.Summary,
            peopleSearchText = x.PeopleSearchText,
            topicSearchText = x.TopicSearchText,
            durationMs = x.DurationMs,
            positionMs = x.PositionMs,
            status = x.Status,
            favourite = x.Favourite,
            progressPercent = x.ProgressPercent,
            lastPlayedAt = x.LastPlayedAt,
            dateAdded = x.DateAdded,
            hasArtwork = !string.IsNullOrWhiteSpace(x.ArtworkPath)
        }).ToArray();

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            apiVersion = WebApiRoutes.Version,
            episodes = payload,
            returned = payload.Length,
            total = page.Total,
            offset = page.Offset,
            limit = page.Limit,
            hasMore = page.HasMore,
            view,
            show,
            filters = new
            {
                year,
                month,
                date = exactDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                status,
                search
            }
        }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleShowsApiAsync(Stream stream, bool headOnly, CancellationToken cancellationToken)
    {
        var shows = WebEpisodeQuery.GetShows(_archive.GetEpisodes())
            .Select(x => new { name = x.Show, count = x.Count })
            .ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, shows }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleBroadcastDetailsApiAsync(Stream stream, long episodeId, bool headOnly, CancellationToken cancellationToken)
    {
        var details = _archive.GetBroadcastDetails(episodeId);
        if (details is null)
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "Broadcast not found.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, broadcast = details }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleResearchApiAsync(Stream stream, long episodeId, bool headOnly, CancellationToken cancellationToken)
    {
        var details = _archive.GetBroadcastDetails(episodeId);
        if (details is null)
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "Broadcast not found.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            apiVersion = WebApiRoutes.Version,
            episodeId,
            summary = details.Episode.Summary,
            people = details.People,
            topics = details.Topics,
            catalogueFields = details.CatalogueFields,
            moments = details.Moments,
            research = details.Research
        }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleBroadcastMetadataMutationAsync(Stream stream, long episodeId, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out WebBroadcastMetadataMutation? mutation) || mutation is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "Broadcast metadata is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (await TryWriteDuplicateMutationResponseAsync(stream, request, cancellationToken).ConfigureAwait(false)) return;
        var result = _archive.UpdateBroadcastMetadata(episodeId, mutation);
        if (result.Changed) MarkMutationProcessed(request);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, result.Changed ? 200 : 404, result.Changed ? "OK" : "Not Found", bytes,
            "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleTranscriptsApiAsync(Stream stream, bool headOnly, CancellationToken cancellationToken)
    {
        var transcripts = _archive.GetTranscripts();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, transcripts, count = transcripts.Count }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: private, no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleTranscriptApiAsync(Stream stream, long episodeId, bool headOnly, CancellationToken cancellationToken)
    {
        var transcript = _archive.GetTranscript(episodeId);
        if (transcript is null)
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "No transcript is available for this broadcast.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, transcript }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: private, no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleMomentCreateAsync(Stream stream, long episodeId, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out WebMomentMutation? mutation) || mutation is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A Moment position and title are required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        var result = _archive.AddMoment(episodeId, mutation);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, result.Changed ? 200 : 404, result.Changed ? "OK" : "Not Found", bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleMomentDeleteAsync(Stream stream, long episodeId, long momentId, CancellationToken cancellationToken)
    {
        var result = _archive.DeleteMoment(episodeId, momentId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, result.Changed ? 200 : 404, result.Changed ? "OK" : "Not Found", bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleArchiveHealthApiAsync(Stream stream, bool headOnly, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, archiveHealth = _archive.GetArchiveHealth() }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandlePlayerApiAsync(Stream stream, bool headOnly, CancellationToken cancellationToken)
    {
        var session = _archive.GetPlaybackSession();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            apiVersion = WebApiRoutes.Version,
            player = session.Player,
            desktop = session.Desktop,
            web = session.Phone,
            session
        }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandlePlaybackTransferBeginAsync(Stream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out WebPlaybackTransferBeginRequest? transfer) || transfer is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON playback transfer request is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        await WritePlaybackTransferResultAsync(stream, _archive.BeginPlaybackTransfer(transfer), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandlePlaybackTransferReadyAsync(Stream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out WebPlaybackTransferReadyRequest? transfer) || transfer is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON playback readiness report is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        await WritePlaybackTransferResultAsync(stream, _archive.MarkPlaybackTransferReady(transfer), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandlePlaybackTransferCommitAsync(Stream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out WebPlaybackTransferCommitRequest? transfer) || transfer is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON playback commit request is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        await WritePlaybackTransferResultAsync(stream, _archive.CommitPlaybackTransfer(transfer), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandlePlaybackTransferCancelAsync(Stream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out WebPlaybackTransferCancelRequest? transfer) || transfer is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON playback cancellation request is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        await WritePlaybackTransferResultAsync(stream, _archive.CancelPlaybackTransfer(transfer), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandlePlaybackTransferSourceStoppedAsync(Stream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out WebPlaybackTransferSourceStoppedRequest? acknowledgement) || acknowledgement is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON playback source-stop acknowledgement is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        await WritePlaybackTransferResultAsync(stream,
            _archive.AcknowledgePlaybackTransferSourceStopped(acknowledgement), cancellationToken).ConfigureAwait(false);
    }

    private async Task WritePlaybackTransferResultAsync(Stream stream, WebPlaybackTransferResult result, CancellationToken cancellationToken)
    {
        var code = result.Conflict ? 409 : 200;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, code, result.Conflict ? "Conflict" : "OK", bytes,
            "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleMomentUpdateAsync(Stream stream, long momentId, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out WebMomentEditMutation? mutation) || mutation is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A Moment title and notes are required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (await TryWriteDuplicateMutationResponseAsync(stream, request, cancellationToken).ConfigureAwait(false)) return;
        var result = _archive.UpdateMoment(momentId, mutation);
        if (result.Changed) MarkMutationProcessed(request);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, result.Changed ? 200 : 404, result.Changed ? "OK" : "Not Found", bytes,
            "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandlePlaybackCommandAsync(Stream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out WebPlaybackCommand? command) || command is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON playback command is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        var result = _archive.ExecutePlaybackCommand(command);
        var code = result.Conflict ? 409 : 200;
        var reason = result.Conflict ? "Conflict" : "OK";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, code, reason, bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleWebPlaybackUpdateAsync(Stream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out WebClientPlaybackUpdate? update) || update is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON phone playback update is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        var result = _archive.UpdateWebPlayback(update);
        var code = result.Conflict ? 409 : 200;
        var reason = result.Conflict ? "Conflict" : "OK";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, code, reason, bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleOfflineProgressAsync(Stream stream, HttpRequest request, long episodeId, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out WebOfflineProgressUpdate? update) || update is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON offline progress update is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (update.EpisodeId != episodeId) update = update with { EpisodeId = episodeId };
        if (await TryWriteDuplicateMutationResponseAsync(stream, request, cancellationToken).ConfigureAwait(false)) return;
        var result = _archive.SyncOfflineProgress(update);
        var code = result.Conflict ? 409 : result.Changed || result.Episode is not null ? 200 : 404;
        if (code == 200) MarkMutationProcessed(request);
        var reason = code == 409 ? "Conflict" : code == 200 ? "OK" : "Not Found";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, code, reason, bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleQueueApiAsync(Stream stream, bool headOnly, CancellationToken cancellationToken)
    {
        var queue = _archive.GetQueue();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, queue, count = queue.Count }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleMomentsApiAsync(Stream stream, bool headOnly, CancellationToken cancellationToken)
    {
        var moments = _archive.GetMoments();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, moments, count = moments.Count }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleQueueAddAsync(Stream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out QueueAddMutation? mutation) || mutation is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON episodeId is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (await TryWriteDuplicateMutationResponseAsync(stream, request, cancellationToken).ConfigureAwait(false)) return;
        var result = _archive.AddToQueue(mutation.EpisodeId, mutation.PlayNext);
        var code = result.Changed ? 200 : 404;
        if (code == 200) MarkMutationProcessed(request);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, code, result.Changed ? "OK" : "Not Found", bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleQueueRemoveAsync(Stream stream, long queueId, CancellationToken cancellationToken)
    {
        var result = _archive.RemoveFromQueue(queueId);
        var code = result.Changed ? 200 : 404;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, code, result.Changed ? "OK" : "Not Found", bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleQueueClearAsync(Stream stream, CancellationToken cancellationToken)
    {
        var result = _archive.ClearQueue();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleQueueMoveAsync(Stream stream, long queueId, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out QueueMoveMutation? mutation) || mutation is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON direction is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        var result = _archive.MoveQueueItem(queueId, mutation.Direction);
        var code = result.Changed ? 200 : 409;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, code, result.Changed ? "OK" : "Conflict", bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleEventsApiAsync(Stream stream, IReadOnlyDictionary<string, string> query, bool headOnly, CancellationToken cancellationToken)
    {
        var after = query.TryGetValue("after", out var rawAfter) && long.TryParse(rawAfter, out var parsedAfter) ? Math.Max(0, parsedAfter) : 0;
        var limit = query.TryGetValue("limit", out var rawLimit) && int.TryParse(rawLimit, out var parsedLimit) ? Math.Clamp(parsedLimit, 1, 200) : 100;
        var changes = _archive.GetChanges(after, limit);
        var sequence = changes.Count == 0 ? after : changes.Max(x => x.Sequence);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, sequence, changes, count = changes.Count }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleJobsApiAsync(Stream stream, bool headOnly, CancellationToken cancellationToken)
    {
        var jobs = _archive.GetJobs();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, jobs, count = jobs.Count }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleJobCancellationAsync(Stream stream, Guid jobId, CancellationToken cancellationToken)
    {
        var result = _archive.CancelJob(jobId);
        var code = result.Changed ? 202 : 409;
        var reason = result.Changed ? "Accepted" : "Conflict";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, code, reason, bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleFavouriteMutationAsync(Stream stream, long episodeId, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out FavouriteMutation? mutation) || mutation is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON favourite value is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await TryWriteDuplicateMutationResponseAsync(stream, request, cancellationToken).ConfigureAwait(false)) return;
        var result = _archive.SetFavourite(episodeId, mutation.Favourite);
        var status = result.Changed ? (200, "OK") : (404, "Not Found");
        if (status.Item1 == 200) MarkMutationProcessed(request);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, status.Item1, status.Item2, bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleListeningStatusMutationAsync(Stream stream, long episodeId, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(request.Body, out ListeningStatusMutation? mutation) || mutation is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "A JSON played value is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await TryWriteDuplicateMutationResponseAsync(stream, request, cancellationToken).ConfigureAwait(false)) return;
        var result = _archive.SetPlayed(episodeId, mutation.Played);
        var status = result.Changed ? (200, "OK") : (404, "Not Found");
        if (status.Item1 == 200) MarkMutationProcessed(request);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, status.Item1, status.Item2, bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleArtworkAsync(Stream stream, bool headOnly, long episodeId, CancellationToken cancellationToken)
    {
        var episode = _archive.GetEpisode(episodeId);
        if (episode is null || string.IsNullOrWhiteSpace(episode.ArtworkPath) || !File.Exists(episode.ArtworkPath))
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "Artwork is not available.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        var bytes = await File.ReadAllBytesAsync(episode.ArtworkPath, cancellationToken).ConfigureAwait(false);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, GetImageMime(Path.GetExtension(episode.ArtworkPath)), headOnly, cancellationToken, "Cache-Control: private, max-age=3600\r\n").ConfigureAwait(false);
    }

    private async Task StreamAudioFileAsync(Stream stream, HttpRequest request, string audioPath, string logIdentity, CancellationToken cancellationToken)
    {
        if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && !request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Audio streaming supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        var file = new FileInfo(audioPath);
        var length = file.Length;
        if (length <= 0) { await WriteTextResponseAsync(stream, 416, "Range Not Satisfiable", "The recording is empty.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false); return; }
        var lastModifiedUtc = file.LastWriteTimeUtc;
        var etag = $"\"rv-{length:x}-{lastModifiedUtc.Ticks:x}\"";
        var lastModified = lastModifiedUtc.ToString("R", CultureInfo.InvariantCulture);
        long start=0,end=length-1; var partial=false;
        var requestedRange=request.Headers.TryGetValue("range",out var rawRange)?rawRange.Trim():string.Empty;
        var ifRange=request.Headers.TryGetValue("if-range",out var rawIfRange)?rawIfRange.Trim():string.Empty;
        var rangeValidatorMatches=string.IsNullOrEmpty(ifRange) ||
            string.Equals(ifRange,etag,StringComparison.Ordinal) ||
            (DateTimeOffset.TryParse(ifRange,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var ifRangeDate) &&
             lastModifiedUtc <= ifRangeDate.UtcDateTime.AddSeconds(1));
        if (!string.IsNullOrEmpty(requestedRange) && rangeValidatorMatches)
        {
            if (!TryParseRange(requestedRange,length,out start,out end))
            {
                var invalid=$"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{length}\r\nAccept-Ranges: bytes\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(invalid),cancellationToken).ConfigureAwait(false); return;
            }
            partial=true;
        }
        var contentLength=end-start+1; var status=partial?"206 Partial Content":"200 OK";
        var header=new StringBuilder().Append("HTTP/1.1 ").Append(status).Append("\r\nContent-Type: ").Append(GetAudioMime(file.Extension))
            .Append("\r\nContent-Length: ").Append(contentLength)
            .Append("\r\nAccept-Ranges: bytes")
            .Append("\r\nETag: ").Append(etag)
            .Append("\r\nLast-Modified: ").Append(lastModified)
            .Append("\r\nCache-Control: private, max-age=300, no-transform")
            .Append("\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n");
        if(partial) header.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(length).Append("\r\n");
        header.Append("\r\n");
        var userAgent = request.Headers.TryGetValue("user-agent", out var rawUserAgent) ? rawUserAgent : string.Empty;
        _log?.Invoke($"{logIdentity}: {request.Method} range='{requestedRange}', if-range='{ifRange}' => {status}, bytes {start}-{end}/{length}, validator={etag}, agent='{userAgent}'.");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()),cancellationToken).ConfigureAwait(false);
        if(request.Method.Equals("HEAD",StringComparison.OrdinalIgnoreCase)) return;
        await using var input=new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,256*1024,FileOptions.Asynchronous|FileOptions.RandomAccess);
        input.Seek(start,SeekOrigin.Begin); var buffer=new byte[256*1024]; var remaining=contentLength;
        try { while(remaining>0&&!cancellationToken.IsCancellationRequested){var read=await input.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,remaining)),cancellationToken).ConfigureAwait(false);if(read<=0)break;await stream.WriteAsync(buffer.AsMemory(0,read),cancellationToken).ConfigureAwait(false);remaining-=read;} await stream.FlushAsync(cancellationToken).ConfigureAwait(false);if(remaining>0)_log?.Invoke($"{logIdentity}: response ended {remaining} bytes early for range '{requestedRange}'.");} catch(IOException ex){_log?.Invoke($"{logIdentity}: client disconnected after {contentLength-remaining} of {contentLength} bytes for range '{requestedRange}' ({ex.Message}).");}
    }

    private async Task HandleCanonicalMediaManifestAsync(Stream stream, long episodeId, IReadOnlyDictionary<string, string> query, bool headOnly, CancellationToken cancellationToken)
    {
        query.TryGetValue("recording", out var recordingKey);
        var manifest = _archive.GetCanonicalMediaManifest(episodeId, recordingKey);
        if (manifest is null)
        {
            _log?.Invoke($"Canonical media manifest {episodeId}: no complete plan is currently available for recording '{recordingKey ?? "preferred"}'.");
            await WriteTextResponseAsync(stream, 404, "Not Found", "No complete canonical media manifest is available.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleCanonicalMediaPartAsync(Stream stream, HttpRequest request, long episodeId, long mediaFileId, IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
    {
        query.TryGetValue("recording", out var recordingKey);
        var part = _archive.GetCanonicalMediaPart(episodeId, mediaFileId, recordingKey);
        if (part is null || string.IsNullOrWhiteSpace(part.AudioPath) || !File.Exists(part.AudioPath))
        {
            // A library scan or cloud-backed path can briefly change while the
            // browser moves its decoder. Re-resolve once before returning a 404.
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            part = _archive.GetCanonicalMediaPart(episodeId, mediaFileId, recordingKey);
        }
        if (part is null || string.IsNullOrWhiteSpace(part.AudioPath) || !File.Exists(part.AudioPath))
        {
            var reason = part is null
                ? "the requested part was not in the current canonical plan"
                : string.IsNullOrWhiteSpace(part.AudioPath)
                    ? "the media path was empty"
                    : "the indexed media path was unavailable";
            _log?.Invoke($"Canonical media {episodeId}/{mediaFileId}: 404 because {reason}; recording '{recordingKey ?? "preferred"}'.");
            await WriteTextResponseAsync(stream, 404, "Not Found", "The canonical media part is not available.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        await StreamAudioFileAsync(stream, request, part.AudioPath, $"Canonical media {episodeId}/{mediaFileId}", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleCanonicalMediaStartAsync(Stream stream, HttpRequest request, long episodeId, IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
    {
        query.TryGetValue("recording", out var recordingKey);
        var requestedPositionMs = query.TryGetValue("positionMs", out var rawPosition) && long.TryParse(rawPosition, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPosition)
            ? Math.Max(0, parsedPosition)
            : 0;
        var manifest = _archive.GetCanonicalMediaManifest(episodeId, recordingKey);
        var part = manifest?.Parts.FirstOrDefault(candidate =>
            requestedPositionMs >= candidate.LogicalStartMs &&
            (requestedPositionMs < candidate.LogicalEndMs || ReferenceEquals(candidate, manifest.Parts[^1])))
            ?? manifest?.Parts.LastOrDefault();
        if (part is null || string.IsNullOrWhiteSpace(part.AudioPath) || !File.Exists(part.AudioPath))
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "The canonical starting media is not available.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        var localPositionMs = Math.Max(0, requestedPositionMs - part.LogicalStartMs);
        var forcePositioned = query.TryGetValue("positioned", out var positionedValue) &&
            positionedValue.Equals("1", StringComparison.OrdinalIgnoreCase);
        if (forcePositioned || localPositionMs > 0)
        {
            var streamSession = query.TryGetValue("streamSession", out var requestedSession) &&
                !string.IsNullOrWhiteSpace(requestedSession)
                    ? requestedSession.Trim()
                    : $"fallback-{episodeId}-{part.MediaFileId}-{localPositionMs}";
            await StreamPositionedWaveAsync(
                stream,
                request,
                part.AudioPath,
                localPositionMs,
                streamSession,
                $"Canonical positioned media start {episodeId}/{part.MediaFileId}",
                cancellationToken).ConfigureAwait(false);
            return;
        }
        await StreamAudioFileAsync(stream, request, part.AudioPath, $"Canonical media start {episodeId}/{part.MediaFileId}", cancellationToken).ConfigureAwait(false);
    }

    private async Task StreamPositionedWaveAsync(
        Stream stream,
        HttpRequest request,
        string audioPath,
        long positionMs,
        string streamSession,
        string logIdentity,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Positioned web playback requires Windows Media Foundation.");
        if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            !request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Audio streaming supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        var positioned = GetOrCreatePositionedWaveSession(streamSession, audioPath, positionMs);
        var virtualLength = positioned.VirtualLength;

        long start = 0, end = virtualLength - 1;
        var partial = false;
        var requestedRange = request.Headers.TryGetValue("range", out var rawRange) ? rawRange.Trim() : string.Empty;
        if (!string.IsNullOrWhiteSpace(requestedRange))
        {
            if (!TryParseRange(requestedRange, virtualLength, out start, out end))
            {
                var invalid = $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{virtualLength}\r\nAccept-Ranges: bytes\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(invalid), cancellationToken).ConfigureAwait(false);
                return;
            }
            partial = true;
        }

        var contentLength = end - start + 1;
        var etag = positioned.ETag;
        var status = partial ? "206 Partial Content" : "200 OK";
        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status)
            .Append("\r\nContent-Type: audio/wav")
            .Append("\r\nContent-Length: ").Append(contentLength)
            .Append("\r\nAccept-Ranges: bytes")
            .Append("\r\nETag: ").Append(etag)
            .Append("\r\nCache-Control: private, max-age=300, no-transform")
            .Append("\r\nX-Content-Type-Options: nosniff")
            .Append("\r\nConnection: close\r\n");
        if (partial) header.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(virtualLength).Append("\r\n");
        header.Append("\r\n");
        _log?.Invoke($"{logIdentity}: {request.Method} positioned at {positionMs} ms, range='{requestedRange}' => {status}, virtual bytes {start}-{end}/{virtualLength}.");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()), cancellationToken).ConfigureAwait(false);
        if (request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var written = await positioned.WriteRangeAsync(stream, start, contentLength, cancellationToken).ConfigureAwait(false);
            if (written < contentLength)
                _log?.Invoke($"{logIdentity}: positioned response ended {contentLength - written} bytes early for range '{requestedRange}'.");
        }
        catch (IOException ex)
        {
            _log?.Invoke($"{logIdentity}: positioned client disconnected during range '{requestedRange}' ({ex.Message}).");
        }
    }

    private PositionedWaveSession GetOrCreatePositionedWaveSession(
        string streamSession,
        string audioPath,
        long positionMs)
    {
        var identityBytes = Encoding.UTF8.GetBytes($"{streamSession}\n{audioPath}\n{positionMs}");
        var key = Convert.ToHexString(SHA256.HashData(identityBytes));
        var now = DateTimeOffset.UtcNow;
        lock (_positionedWaveSessionsGate)
        {
            foreach (var stale in _positionedWaveSessions
                         .Where(pair => now - pair.Value.LastAccessUtc >= PositionedWaveSessionIdleLifetime)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                if (_positionedWaveSessions[stale].TryDispose())
                    _positionedWaveSessions.Remove(stale);
            }

            if (_positionedWaveSessions.TryGetValue(key, out var existing))
            {
                if (existing.MatchesCurrentFile()) return existing;
                if (!existing.TryDispose()) return existing;
                _positionedWaveSessions.Remove(key);
            }

            if (_positionedWaveSessions.Count >= PositionedWaveSessionSoftLimit)
            {
                foreach (var oldest in _positionedWaveSessions
                             .OrderBy(pair => pair.Value.LastAccessUtc)
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    if (!_positionedWaveSessions[oldest].TryDispose()) continue;
                    _positionedWaveSessions.Remove(oldest);
                    if (_positionedWaveSessions.Count < PositionedWaveSessionSoftLimit) break;
                }
            }

            var created = new PositionedWaveSession(audioPath, positionMs);
            _positionedWaveSessions[key] = created;
            return created;
        }
    }

    private void DisposePositionedWaveSessions()
    {
        PositionedWaveSession[] sessions;
        lock (_positionedWaveSessionsGate)
        {
            sessions = _positionedWaveSessions.Values.ToArray();
            _positionedWaveSessions.Clear();
        }
        foreach (var session in sessions) session.Dispose();
    }

    private sealed class PositionedWaveSession : IDisposable
    {
        private readonly string _audioPath;
        private readonly long _fileLength;
        private readonly DateTime _lastWriteTimeUtc;
        private readonly TimeSpan _requestedTime;
        private readonly SemaphoreSlim _access = new(1, 1);
        private WaveStream _reader;
        private long _decodedStart;
        private long _dataCursor;
        private bool _disposed;

        public PositionedWaveSession(string audioPath, long positionMs)
        {
            _audioPath = audioPath;
            var file = new FileInfo(audioPath);
            _fileLength = file.Length;
            _lastWriteTimeUtc = file.LastWriteTimeUtc;
            _reader = OpenPositionedAudioReader(audioPath);
            WaveFormat = _reader.WaveFormat;
            if (WaveFormat.Encoding != WaveFormatEncoding.Pcm || WaveFormat.BitsPerSample is not (8 or 16 or 24 or 32))
                throw new InvalidDataException($"The decoded format {WaveFormat.Encoding}/{WaveFormat.BitsPerSample}-bit cannot be represented as a standard PCM wave stream.");

            _requestedTime = TimeSpan.FromMilliseconds(Math.Clamp(
                positionMs,
                0,
                Math.Max(0, _reader.TotalTime.TotalMilliseconds)));
            PositionReaderAtStart();
            var availableData = Math.Max(0, _reader.Length - _decodedStart);
            availableData -= availableData % BlockAlign;
            DataLength = Math.Min(availableData, (long)uint.MaxValue - 64);
            DataLength -= DataLength % BlockAlign;
            WaveHeader = CreatePcmWaveHeader(WaveFormat, DataLength);
            VirtualLength = WaveHeader.LongLength + DataLength;
            ETag = $"\"rv-positioned-{_fileLength:x}-{_lastWriteTimeUtc.Ticks:x}-{positionMs:x}\"";
            LastAccessUtc = DateTimeOffset.UtcNow;
        }

        public WaveFormat WaveFormat { get; }
        public int BlockAlign => Math.Max(1, WaveFormat.BlockAlign);
        public long DataLength { get; }
        public byte[] WaveHeader { get; }
        public long VirtualLength { get; }
        public string ETag { get; }
        public DateTimeOffset LastAccessUtc { get; private set; }

        public bool MatchesCurrentFile()
        {
            var file = new FileInfo(_audioPath);
            return file.Exists && file.Length == _fileLength && file.LastWriteTimeUtc == _lastWriteTimeUtc;
        }

        public async Task<long> WriteRangeAsync(
            Stream output,
            long virtualStart,
            long count,
            CancellationToken cancellationToken)
        {
            await _access.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                LastAccessUtc = DateTimeOffset.UtcNow;
                var remaining = Math.Max(0, count);
                var cursor = Math.Max(0, virtualStart);
                long written = 0;
                if (cursor < WaveHeader.LongLength)
                {
                    var headerCount = (int)Math.Min(remaining, WaveHeader.LongLength - cursor);
                    await output.WriteAsync(WaveHeader.AsMemory((int)cursor, headerCount), cancellationToken).ConfigureAwait(false);
                    cursor += headerCount;
                    remaining -= headerCount;
                    written += headerCount;
                }
                if (remaining <= 0) return written;

                var targetDataOffset = Math.Max(0, cursor - WaveHeader.LongLength);
                if (targetDataOffset < _dataCursor) PositionReaderAtStart();
                var buffer = new byte[256 * 1024];
                while (_dataCursor < targetDataOffset)
                {
                    var discardCount = (int)Math.Min(buffer.Length, targetDataOffset - _dataCursor);
                    var discarded = _reader.Read(buffer, 0, discardCount);
                    if (discarded <= 0) return written;
                    _dataCursor += discarded;
                }

                while (remaining > 0 && !cancellationToken.IsCancellationRequested)
                {
                    var read = _reader.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    _dataCursor += read;
                    remaining -= read;
                    written += read;
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                LastAccessUtc = DateTimeOffset.UtcNow;
                return written;
            }
            finally
            {
                _access.Release();
            }
        }

        private void PositionReaderAtStart()
        {
            _reader.CurrentTime = _requestedTime;
            _decodedStart = _reader.Position - _reader.Position % BlockAlign;
            _dataCursor = 0;
        }

        public bool TryDispose()
        {
            if (!_access.Wait(0)) return false;
            try
            {
                DisposeCore();
                return true;
            }
            finally
            {
                _access.Release();
            }
        }

        public void Dispose()
        {
            _access.Wait();
            try { DisposeCore(); }
            finally
            {
                _access.Release();
                _access.Dispose();
            }
        }

        private void DisposeCore()
        {
            if (_disposed) return;
            _disposed = true;
            _reader.Dispose();
        }
    }

    private static WaveStream OpenPositionedAudioReader(string audioPath)
    {
        if (Path.GetExtension(audioPath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            return new WaveFileReader(audioPath);
        return new MediaFoundationReader(
            audioPath,
            new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = false });
    }

    private static byte[] CreatePcmWaveHeader(WaveFormat format, long dataLength)
    {
        using var output = new MemoryStream(44);
        using (var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write((uint)(36 + dataLength));
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16u);
            writer.Write((ushort)1);
            writer.Write((ushort)format.Channels);
            writer.Write((uint)format.SampleRate);
            writer.Write((uint)format.AverageBytesPerSecond);
            writer.Write((ushort)format.BlockAlign);
            writer.Write((ushort)format.BitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write((uint)dataLength);
        }
        return output.ToArray();
    }

    private async Task HandleAudioAsync(Stream stream, HttpRequest request, long episodeId, CancellationToken cancellationToken)
    {
        if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && !request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Audio streaming supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        var episode = _archive.GetEpisode(episodeId);
        if (episode is null || string.IsNullOrWhiteSpace(episode.AudioPath) || !File.Exists(episode.AudioPath))
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "The recording is not currently available on this computer.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        var file = new FileInfo(episode.AudioPath);
        var length = file.Length;
        if (length <= 0)
        {
            await WriteTextResponseAsync(stream, 416, "Range Not Satisfiable", "The recording is empty.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        long start = 0;
        long end = length - 1;
        var partial = false;
        var requestedRange = request.Headers.TryGetValue("range", out var rawRange) ? rawRange.Trim() : string.Empty;
        if (!string.IsNullOrEmpty(requestedRange))
        {
            if (!TryParseRange(requestedRange, length, out start, out end))
            {
                var invalidHeader = new StringBuilder()
                    .Append("HTTP/1.1 416 Range Not Satisfiable\r\n")
                    .Append("Content-Range: bytes */").Append(length).Append("\r\n")
                    .Append("Accept-Ranges: bytes\r\n")
                    .Append("Content-Length: 0\r\n")
                    .Append("Cache-Control: no-store, no-cache, must-revalidate\r\n")
                    .Append("Pragma: no-cache\r\n")
                    .Append("Expires: 0\r\n")
                    .Append("Vary: Range\r\n")
                    .Append("Connection: close\r\n\r\n");
                _log?.Invoke($"Audio {episodeId}: rejected range '{requestedRange}' for {length} bytes.");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(invalidHeader.ToString()), cancellationToken).ConfigureAwait(false);
                return;
            }
            partial = true;
        }

        var contentLength = end - start + 1;
        var mime = GetAudioMime(file.Extension);
        var status = partial ? "206 Partial Content" : "200 OK";
        var safeName = Uri.EscapeDataString(file.Name);
        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append("\r\n")
            .Append("Content-Type: ").Append(mime).Append("\r\n")
            .Append("Content-Length: ").Append(contentLength).Append("\r\n")
            .Append("Accept-Ranges: bytes\r\n")
            .Append("Content-Encoding: identity\r\n")
            .Append("Content-Disposition: inline; filename*=UTF-8''").Append(safeName).Append("\r\n")
            .Append("Cache-Control: no-store, no-cache, must-revalidate\r\n")
            .Append("Pragma: no-cache\r\n")
            .Append("Expires: 0\r\n")
            .Append("Vary: Range\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Connection: close\r\n");
        if (partial) header.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(length).Append("\r\n");
        header.Append("\r\n");

        var userAgent = request.Headers.TryGetValue("user-agent", out var rawUserAgent) ? rawUserAgent : string.Empty;
        _log?.Invoke($"Audio {episodeId}: {request.Method} range='{requestedRange}' => {status}, bytes {start}-{end}/{length}, length {contentLength}, agent='{userAgent}'.");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()), cancellationToken).ConfigureAwait(false);
        if (request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) return;

        await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 256 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        input.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[256 * 1024];
        var remaining = contentLength;
        try
        {
            while (remaining > 0 && !cancellationToken.IsCancellationRequested)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (remaining > 0)
                _log?.Invoke($"Audio {episodeId}: response ended {remaining} bytes early for requested range '{requestedRange}'.");
        }
        catch (IOException ex)
        {
            _log?.Invoke($"Audio {episodeId}: client disconnected during range '{requestedRange}' after {contentLength - remaining} of {contentLength} bytes ({ex.Message}).");
        }
    }

    private static bool TryMatchCanonicalMediaManifest(string path, out long episodeId)
    {
        episodeId = 0;
        var suffix = "/media-manifest";
        if (!path.StartsWith(WebApiRoutes.Broadcasts + "/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var value = path[(WebApiRoutes.Broadcasts.Length + 1)..^suffix.Length];
        return long.TryParse(value, out episodeId);
    }

    private static bool TryMatchCanonicalMediaPart(string path, out long episodeId, out long mediaFileId)
    {
        episodeId = 0; mediaFileId = 0;
        var prefix = WebApiRoutes.Broadcasts + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = path[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return rest.Length == 3 && rest[1].Equals("media", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(rest[0], out episodeId) && long.TryParse(rest[2], out mediaFileId);
    }

    private static bool TryMatchCanonicalMediaStart(string path, out long episodeId)
    {
        episodeId = 0;
        var suffix = "/media-start";
        if (!path.StartsWith(WebApiRoutes.Broadcasts + "/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var value = path[(WebApiRoutes.Broadcasts.Length + 1)..^suffix.Length];
        return long.TryParse(value, out episodeId);
    }

    private static async Task<HttpRequest?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var input = new MemoryStream();
        var buffer = new byte[2048];
        var headerEnd = -1;

        while (input.Length < 32 * 1024 && headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (read <= 0) return null;
            input.Write(buffer, 0, read);
            headerEnd = FindHeaderEnd(input.GetBuffer(), (int)input.Length);
        }
        if (headerEnd < 0) return null;

        var all = input.ToArray();
        var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var first = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var requestTarget = first[1];
        var isResearchPackUpload = requestTarget.StartsWith(WebApiRoutes.FederationResearchImportPreview, StringComparison.OrdinalIgnoreCase);
        var isWikiPackUpload = requestTarget.StartsWith(WebApiRoutes.FederationWikiImportPreview, StringComparison.OrdinalIgnoreCase)
            || requestTarget.StartsWith(WebApiRoutes.FederationWikiImportApply, StringComparison.OrdinalIgnoreCase);
        var isFullClientPayload = requestTarget.StartsWith(WebApiRoutes.ClientResearch, StringComparison.OrdinalIgnoreCase)
            || requestTarget.StartsWith(WebApiRoutes.ClientTranscripts, StringComparison.OrdinalIgnoreCase)
            || requestTarget.StartsWith(WebApiRoutes.ClientSpeakers, StringComparison.OrdinalIgnoreCase)
            || requestTarget.StartsWith(WebApiRoutes.ClientTranscription, StringComparison.OrdinalIgnoreCase)
            || requestTarget.StartsWith(WebApiRoutes.ClientWiki, StringComparison.OrdinalIgnoreCase);
        var isLargePayload = isResearchPackUpload || isWikiPackUpload || isFullClientPayload;
        var maximumBodyBytes = isResearchPackUpload
            ? WebResearchPackLimits.MaximumPackageBytes
            : isWikiPackUpload ? 512 * 1024 * 1024
            : isFullClientPayload ? 64 * 1024 * 1024 : 16 * 1024;
        timeout.CancelAfter(isResearchPackUpload || isWikiPackUpload
            ? TimeSpan.FromMinutes(10)
            : isFullClientPayload ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(10));
        var bodyOffset = headerEnd + 4;
        var alreadyRead = Math.Max(0, all.Length - bodyOffset);
        var initialBody = alreadyRead > 0
            ? all.AsMemory(bodyOffset, alreadyRead)
            : ReadOnlyMemory<byte>.Empty;

        byte[] requestBody;
        var isChunked = headers.TryGetValue("transfer-encoding", out var transferEncoding) &&
                        transferEncoding.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Any(value => value.Equals("chunked", StringComparison.OrdinalIgnoreCase));
        if (isChunked)
        {
            var chunkedBody = await ReadChunkedBodyAsync(stream, initialBody, maximumBodyBytes, timeout.Token).ConfigureAwait(false);
            if (chunkedBody is null) return null;
            requestBody = chunkedBody;
        }
        else
        {
            var contentLength = headers.TryGetValue("content-length", out var rawLength) && int.TryParse(rawLength, out var parsedLength)
                ? parsedLength
                : 0;
            if (contentLength < 0 || contentLength > maximumBodyBytes) return null;

            using var body = new MemoryStream(contentLength);
            if (alreadyRead > 0) body.Write(all, bodyOffset, Math.Min(alreadyRead, contentLength));
            while (body.Length < contentLength)
            {
                var remaining = contentLength - (int)body.Length;
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), timeout.Token).ConfigureAwait(false);
                if (read <= 0) return null;
                body.Write(buffer, 0, read);
            }
            requestBody = body.ToArray();
        }

        return new HttpRequest(first[0], first[1], headers, requestBody);
    }


    private static async Task<byte[]?> ReadChunkedBodyAsync(
        Stream stream,
        ReadOnlyMemory<byte> initialBody,
        int maximumBodyBytes,
        CancellationToken cancellationToken)
    {
        var reader = new PrefixedBodyReader(stream, initialBody);
        using var output = new MemoryStream();

        while (true)
        {
            var sizeLine = await reader.ReadAsciiLineAsync(128, cancellationToken).ConfigureAwait(false);
            if (sizeLine is null) return null;
            var extension = sizeLine.IndexOf(';');
            var sizeText = (extension >= 0 ? sizeLine[..extension] : sizeLine).Trim();
            if (!int.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var chunkSize) || chunkSize < 0)
                return null;

            if (chunkSize == 0)
            {
                while (true)
                {
                    var trailer = await reader.ReadAsciiLineAsync(2048, cancellationToken).ConfigureAwait(false);
                    if (trailer is null) return null;
                    if (trailer.Length == 0) return output.ToArray();
                }
            }

            if (output.Length + chunkSize > maximumBodyBytes) return null;
            var chunk = new byte[chunkSize];
            if (!await reader.ReadExactlyAsync(chunk, cancellationToken).ConfigureAwait(false)) return null;
            output.Write(chunk, 0, chunk.Length);

            var carriageReturn = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            var lineFeed = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (carriageReturn != '\r' || lineFeed != '\n') return null;
        }
    }

    private sealed class PrefixedBodyReader
    {
        private readonly Stream _stream;
        private readonly byte[] _prefix;
        private int _prefixOffset;
        private readonly byte[] _networkBuffer = new byte[2048];
        private int _networkOffset;
        private int _networkCount;

        public PrefixedBodyReader(Stream stream, ReadOnlyMemory<byte> prefix)
        {
            _stream = stream;
            _prefix = prefix.ToArray();
        }

        public async Task<int> ReadByteAsync(CancellationToken cancellationToken)
        {
            if (_prefixOffset < _prefix.Length) return _prefix[_prefixOffset++];
            if (_networkOffset >= _networkCount)
            {
                _networkCount = await _stream.ReadAsync(_networkBuffer, cancellationToken).ConfigureAwait(false);
                _networkOffset = 0;
                if (_networkCount <= 0) return -1;
            }
            return _networkBuffer[_networkOffset++];
        }

        public async Task<bool> ReadExactlyAsync(byte[] destination, CancellationToken cancellationToken)
        {
            for (var index = 0; index < destination.Length; index++)
            {
                var value = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (value < 0) return false;
                destination[index] = (byte)value;
            }
            return true;
        }

        public async Task<string?> ReadAsciiLineAsync(int maximumBytes, CancellationToken cancellationToken)
        {
            using var line = new MemoryStream();
            while (line.Length <= maximumBytes)
            {
                var value = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (value < 0) return null;
                if (value == '\r')
                {
                    var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                    if (next != '\n') return null;
                    return Encoding.ASCII.GetString(line.ToArray());
                }
                line.WriteByte((byte)value);
            }
            return null;
        }
    }

    private static int FindHeaderEnd(byte[] bytes, int length)
    {
        for (var i = 0; i <= length - 4; i++)
        {
            if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10) return i;
        }
        return -1;
    }

    private static async Task WriteJsonAsync<T>(Stream stream, T payload, bool headOnly, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await WriteBytesResponseAsync(
            stream,
            200,
            "OK",
            bytes,
            "application/json; charset=utf-8",
            headOnly,
            cancellationToken,
            "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private static async Task WriteTextResponseAsync(Stream stream, int code, string reason, string text, string contentType, CancellationToken cancellationToken)
        => await WriteBytesResponseAsync(stream, code, reason, Encoding.UTF8.GetBytes(text), contentType, false, cancellationToken).ConfigureAwait(false);

    private static async Task WriteBytesResponseAsync(Stream stream, int code, string reason, byte[] bytes, string contentType, bool headOnly, CancellationToken cancellationToken, string extraHeaders = "")
    {
        var header = $"HTTP/1.1 {code} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n{extraHeaders}Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
        if (!headOnly) await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private string BuildIndexHtml()
        => WebClientHtml
            .Replace("__TOKEN__", JavaScriptString(_options.AccessToken), StringComparison.Ordinal)
            .Replace("__APP_VERSION__", JavaScriptString(_options.AppVersion), StringComparison.Ordinal);

    private string BuildWebManifest()
    {
        var token = Uri.EscapeDataString(_options.AccessToken);
        var version = Uri.EscapeDataString(_options.AppVersion);
        return $$"""
        {
          "name": "Radio Vault Web",
          "short_name": "Radio Vault Web",
          "description": "Browse and listen to your Radio Vault archive in a browser.",
          "id": "/?token={{token}}",
          "start_url": "/?token={{token}}",
          "scope": "/",
          "display": "standalone",
          "background_color": "#101010",
          "theme_color": "#f2c94c",
          "icons": [
            { "src": "/app-icon-192.png?token={{token}}&v={{version}}", "sizes": "192x192", "type": "image/png", "purpose": "any" },
            { "src": "/app-icon-512.png?token={{token}}&v={{version}}", "sizes": "512x512", "type": "image/png", "purpose": "any" },
            { "src": "/app-icon-maskable-512.png?token={{token}}&v={{version}}", "sizes": "512x512", "type": "image/png", "purpose": "maskable" }
          ]
        }
        """;
    }

    private static bool TryGetWebAppIcon(string path, out byte[] bytes)
    {
        var resourceName = path.ToLowerInvariant() switch
        {
            "/app-icon-180.png" => "TheRadioVault.Web.Assets.app-icon-180.png",
            "/app-icon-192.png" => "TheRadioVault.Web.Assets.app-icon-192.png",
            "/app-icon-512.png" => "TheRadioVault.Web.Assets.app-icon-512.png",
            "/app-icon-maskable-512.png" => "TheRadioVault.Web.Assets.app-icon-maskable-512.png",
            "/app-logo-512.png" => "TheRadioVault.Web.Assets.app-logo-512.png",
            _ => string.Empty
        };
        if (resourceName.Length == 0)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
        using var stream = typeof(LocalWebServer).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        bytes = buffer.ToArray();
        return true;
    }

    private string BuildSecureTarget(HttpRequest request)
    {
        var host = GetRequestHost(request);
        return $"https://{host}:{_options.SecurePort}{request.Target}";
    }

    private string BuildSecureSetupHtml(HttpRequest request)
    {
        var host = GetRequestHost(request);
        var token = Uri.EscapeDataString(_options.AccessToken);
        var profileUrl = WebUtility.HtmlEncode($"http://{host}:{_options.Port}/secure-profile.mobileconfig?token={token}");
        var certificateUrl = WebUtility.HtmlEncode($"http://{host}:{_options.Port}/secure-root.cer?token={token}");
        var secureUrl = WebUtility.HtmlEncode($"https://{host}:{_options.SecurePort}/?token={token}");
        var thumbprint = WebUtility.HtmlEncode(_options.RootCertificateThumbprint);

        const string template = """
<!doctype html>
<html>
<head>
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>Radio Vault secure setup</title>
  <style>
    :root { color-scheme: dark; }
    * { box-sizing: border-box; }
    body { margin: 0; background: #101010; color: #f4f4f4; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; }
    main { max-width: 700px; margin: auto; padding: 24px 16px 50px; }
    .logo { width: 48px; height: 48px; display: grid; place-items: center; border: 2px solid #f2c94c; border-radius: 12px; color: #f2c94c; font-weight: 900; }
    h1 { font-size: 28px; margin: 16px 0 8px; }
    p, li { color: #bbb; line-height: 1.5; }
    .card { margin-top: 16px; padding: 17px; background: #1d1d1d; border: 1px solid #3a3a3a; border-radius: 14px; }
    .step { color: #f2c94c; font-weight: 800; }
    a.button { display: block; text-align: center; margin-top: 12px; padding: 13px 16px; background: #f2c94c; color: #111; text-decoration: none; border-radius: 10px; font-weight: 800; }
    a.secondary { background: #2a2a2a; color: #f4f4f4; border: 1px solid #444; }
    code { word-break: break-all; color: #ffe27c; font-size: 11px; }
    .warning { padding: 12px; border-left: 3px solid #f2c94c; background: #262216; }
  </style>
</head>
<body>
  <main>
    <div class="logo">RV</div>
    <h1>Secure offline access</h1>
    <p>This one-time setup lets Safari trust Radio Vault's private HTTPS server. Afterwards, downloaded broadcasts can open from a cold offline launch when the computer is unreachable.</p>
    <div class="card">
      <div class="step">1 · Install the profile</div>
      <p>Tap below, allow the profile download, then open <strong>Settings → Profile Downloaded</strong> and install <strong>Radio Vault Secure Offline Access</strong>.</p>
      <a class="button" href="__PROFILE_URL__">Download iPhone profile</a>
      <a class="button secondary" href="__CERTIFICATE_URL__">Download certificate only</a>
    </div>
    <div class="card">
      <div class="step">2 · Enable full trust</div>
      <p>Open <strong>Settings → General → About → Certificate Trust Settings</strong>, then enable full trust for <strong>Radio Vault Local Root CA</strong>.</p>
      <div class="warning">The root private key remains on the Radio Vault computer. The phone receives only the public certificate used to verify this local server.</div>
    </div>
    <div class="card">
      <div class="step">3 · Test secure access</div>
      <p>Return here and tap the button below. Once Radio Vault opens over HTTPS, Safari will install its offline application shell automatically.</p>
      <a class="button" href="__SECURE_URL__">Open Radio Vault securely</a>
      <p>Root certificate fingerprint:<br><code>__THUMBPRINT__</code></p>
    </div>
  </main>
</body>
</html>
""";

        return template
            .Replace("__PROFILE_URL__", profileUrl, StringComparison.Ordinal)
            .Replace("__CERTIFICATE_URL__", certificateUrl, StringComparison.Ordinal)
            .Replace("__SECURE_URL__", secureUrl, StringComparison.Ordinal)
            .Replace("__THUMBPRINT__", thumbprint, StringComparison.Ordinal);
    }

    private static string GetRequestHost(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Host", out var host) || string.IsNullOrWhiteSpace(host)) return "127.0.0.1";
        host = host.Trim();
        if (host.StartsWith("[", StringComparison.Ordinal))
        {
            var closing = host.IndexOf(']');
            return closing > 0 ? host[1..closing] : "127.0.0.1";
        }
        var colon = host.LastIndexOf(':');
        return colon > 0 ? host[..colon] : host;
    }

    private static async Task WriteRedirectAsync(Stream stream, string location, CancellationToken cancellationToken)
    {
        var safeLocation = location.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
        var header = $"HTTP/1.1 302 Found\r\nLocation: {safeLocation}\r\nContent-Length: 0\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            result[Uri.UnescapeDataString(parts[0].Replace('+', ' '))] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
        }
        return result;
    }

    private static bool TryParseRange(string value, long fileLength, out long start, out long end)
    {
        start = 0; end = fileLength - 1;
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
        var range = value[6..].Split(',', 2)[0].Trim();
        var parts = range.Split('-', 2);
        if (parts.Length != 2) return false;
        if (string.IsNullOrWhiteSpace(parts[0]))
        {
            if (!long.TryParse(parts[1], out var suffix) || suffix <= 0) return false;
            suffix = Math.Min(suffix, fileLength);
            start = fileLength - suffix;
            return true;
        }
        if (!long.TryParse(parts[0], out start) || start < 0 || start >= fileLength) return false;
        if (!string.IsNullOrWhiteSpace(parts[1]) && long.TryParse(parts[1], out var parsedEnd)) end = Math.Min(parsedEnd, fileLength - 1);
        return end >= start;
    }

    private static string GetAudioMime(string extension) => extension.ToLowerInvariant() switch
    {
        ".m4a" or ".mp4" => "audio/mp4",
        ".aac" => "audio/aac",
        ".wav" => "audio/wav",
        ".flac" => "audio/flac",
        ".ogg" or ".opus" => "audio/ogg",
        _ => "audio/mpeg"
    };

    private static string GetImageMime(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg"
    };

    private static bool IsPrivateOrLoopback(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 127 || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 169 && bytes[1] == 254);
    }

    private static IEnumerable<string> GetLanAddresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses
                .Select(address => new
                {
                    Address = address.Address,
                    Priority = network.NetworkInterfaceType switch
                    {
                        NetworkInterfaceType.Wireless80211 => 0,
                        NetworkInterfaceType.Ethernet => 1,
                        _ => 2
                    }
                }))
            .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivateOrLoopback(x.Address))
            .Select(x => new
            {
                Address = x.Address.ToString(),
                Priority = x.Priority + (x.Address.GetAddressBytes()[0] == 169 ? 10 : 0)
            })
            .GroupBy(x => x.Address, StringComparer.Ordinal)
            .Select(x => x.OrderBy(y => y.Priority).First())
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Address, StringComparer.Ordinal)
            .Select(x => x.Address);
    }

    private bool IsAuthorizedRequest(HttpRequest request, IReadOnlyDictionary<string, string> query)
    {
        if (query.TryGetValue("token", out var queryToken) && FixedTimeEquals(queryToken, _options.AccessToken))
            return true;

        string? headerToken = null;
        if (request.Headers.TryGetValue("X-RadioVault-Token", out var directToken))
            headerToken = directToken.Trim();
        else if (request.Headers.TryGetValue("Authorization", out var authorization) &&
                 authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            headerToken = authorization[7..].Trim();

        if (string.IsNullOrWhiteSpace(headerToken)) return false;
        if (FixedTimeEquals(headerToken, _options.AccessToken)) return true;
        return _pairedDesktopClients.Values.Any(client => FixedTimeEquals(headerToken, client.Token));
    }

    private void ReplacePairedDesktopClients(IEnumerable<WebPairedDesktopClient> clients)
    {
        _pairedDesktopClients.Clear();
        foreach (var client in clients ?? Array.Empty<WebPairedDesktopClient>())
        {
            var clientId = NormalizeDesktopClientId(client.ClientId);
            var displayName = NormalizeDesktopDisplayName(client.DisplayName);
            var token = client.Token?.Trim() ?? string.Empty;
            if (clientId.Length == 0 || displayName.Length == 0 || token.Length < 32) continue;
            _pairedDesktopClients[clientId] = client with
            {
                ClientId = clientId,
                DisplayName = displayName,
                Token = token
            };
        }
    }

    private bool IsPairingActiveUnsafe()
    {
        if (string.IsNullOrWhiteSpace(_pairingCode) || _pairingAttemptsRemaining <= 0) return false;
        if (_pairingExpiresAt > DateTimeOffset.UtcNow) return true;
        _pairingCode = string.Empty;
        _pairingExpiresAt = DateTimeOffset.MinValue;
        _pairingAttemptsRemaining = 0;
        return false;
    }

    private static string NormalizeDesktopClientId(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length is < 8 or > 128) return string.Empty;
        return trimmed.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')
            ? trimmed
            : string.Empty;
    }

    private static string NormalizeDesktopDisplayName(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > 80) return string.Empty;
        return new string(trimmed.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string JavaScriptString(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    public void Dispose() => Stop();

    private static bool TryMatchJobCancel(string path, out Guid jobId)
    {
        jobId = Guid.Empty;
        var prefix = WebApiRoutes.Jobs + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var tail = path[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return tail.Length == 2 && tail[1].Equals("cancel", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(tail[0], out jobId);
    }

    private static bool TryMatchQueueAction(string path, string action, out long queueId)
    {
        queueId = 0;
        var prefix = WebApiRoutes.Queue + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var tail = path[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return tail.Length == 2 && tail[1].Equals(action, StringComparison.OrdinalIgnoreCase) && long.TryParse(tail[0], out queueId);
    }

    private static bool TryMatchMomentDelete(string path, out long episodeId, out long momentId)
    {
        episodeId = 0; momentId = 0;
        var prefix = WebApiRoutes.Broadcasts + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var parts = path[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 && long.TryParse(parts[0], out episodeId) &&
               parts[1].Equals("moments", StringComparison.OrdinalIgnoreCase) && long.TryParse(parts[2], out momentId);
    }

    private static bool TryMatchMomentUpdate(string path, out long momentId)
    {
        momentId = 0;
        var prefix = WebApiRoutes.MomentsAll + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var parts = path[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && long.TryParse(parts[0], out momentId) &&
               parts[1].Equals("update", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryMatchBroadcastAction(string path, string action, out long episodeId)
    {
        episodeId = 0;
        var prefix = WebApiRoutes.Broadcasts + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var tail = path[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return tail.Length == 2 && tail[1].Equals(action, StringComparison.OrdinalIgnoreCase) && long.TryParse(tail[0], out episodeId);
    }

    private static bool TryGetMutationId(HttpRequest request, out string mutationId)
    {
        mutationId = string.Empty;
        if (!request.Headers.TryGetValue("X-Radio-Vault-Mutation-Id", out var raw)) return false;
        var value = raw.Trim();
        if (value.Length is < 8 or > 128) return false;
        if (value.Any(ch => !char.IsLetterOrDigit(ch) && ch != '-' && ch != '_' && ch != '.' && ch != ':')) return false;
        mutationId = value;
        return true;
    }

    private bool IsProcessedMutation(HttpRequest request)
        => TryGetMutationId(request, out var mutationId) && _processedMutationIds.ContainsKey(mutationId);

    private void MarkMutationProcessed(HttpRequest request)
    {
        if (!TryGetMutationId(request, out var mutationId)) return;
        if (!_processedMutationIds.TryAdd(mutationId, DateTimeOffset.UtcNow)) return;
        _processedMutationOrder.Enqueue(mutationId);
        while (_processedMutationIds.Count > ProcessedMutationLimit && _processedMutationOrder.TryDequeue(out var oldest))
            _processedMutationIds.TryRemove(oldest, out _);
    }

    private async Task<bool> TryWriteDuplicateMutationResponseAsync(Stream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!IsProcessedMutation(request)) return false;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            apiVersion = WebApiRoutes.Version,
            result = new
            {
                changed = false,
                duplicate = true,
                message = "This change was already applied by Radio Vault."
            }
        }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
        return true;
    }

    private static bool TryDeserialize<T>(byte[] body, out T? value)
    {
        value = default;
        if (body.Length == 0) return false;
        try
        {
            value = JsonSerializer.Deserialize<T>(body, JsonOptions);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record HttpRequest(string Method, string Target, Dictionary<string, string> Headers, byte[] Body);
    private sealed record FavouriteMutation(bool Favourite);
    private sealed record ListeningStatusMutation(bool Played);
    private sealed record QueueAddMutation(long EpisodeId, bool PlayNext = false);
    private sealed record QueueMoveMutation(int Direction);

    private const string ServiceWorkerJavaScript = """
const CACHE_NAME='radio-vault-anywhere-shell-v67';
const AUDIO_CACHE='radio-vault-anywhere-audio-v1';
const ARTWORK_CACHE='radio-vault-anywhere-artwork-v1';
const SHELL_KEY='/__radio_vault_offline_shell__';
self.addEventListener('install',event=>{self.skipWaiting()});
self.addEventListener('activate',event=>{event.waitUntil((async()=>{for(const key of await caches.keys())if(key!==CACHE_NAME&&key.startsWith('radio-vault-anywhere-shell-'))await caches.delete(key);await self.clients.claim()})())});
self.addEventListener('message',event=>{const type=event.data?.type;if(type==='SKIP_WAITING'){self.skipWaiting();return}if(type!=='CACHE_SHELL'||!event.data.url)return;event.waitUntil((async()=>{try{const response=await fetch(event.data.url,{cache:'no-store'});if(response.ok){const cache=await caches.open(CACHE_NAME);await cache.put(SHELL_KEY,response.clone())}}catch{}})())});
async function offlineAudioResponse(request){const cache=await caches.open(AUDIO_CACHE),full=await cache.match(new URL(request.url).pathname);if(!full)return new Response('Downloaded audio was not found.',{status:404});const range=request.headers.get('range');if(!range)return full;const blob=await full.blob(),match=/bytes=(\d*)-(\d*)/.exec(range);if(!match)return new Response(null,{status:416,headers:{'Content-Range':'bytes */'+blob.size}});let start=match[1]?Number(match[1]):0,end=match[2]?Number(match[2]):blob.size-1;if(!match[1]&&match[2]){const suffix=Number(match[2]);start=Math.max(0,blob.size-suffix);end=blob.size-1}if(!Number.isFinite(start)||!Number.isFinite(end)||start<0||start>=blob.size||end<start)return new Response(null,{status:416,headers:{'Content-Range':'bytes */'+blob.size}});end=Math.min(end,blob.size-1);const part=blob.slice(start,end+1,full.headers.get('Content-Type')||blob.type||'audio/mpeg');return new Response(part,{status:206,headers:{'Content-Type':part.type||'audio/mpeg','Content-Length':String(part.size),'Content-Range':`bytes ${start}-${end}/${blob.size}`,'Accept-Ranges':'bytes','Cache-Control':'no-store'}})}
async function offlineArtworkResponse(request){const cache=await caches.open(ARTWORK_CACHE),response=await cache.match(new URL(request.url).pathname);return response||new Response('Downloaded artwork was not found.',{status:404,headers:{'Cache-Control':'no-store'}})}
self.addEventListener('fetch',event=>{const request=event.request;if(request.method!=='GET')return;const url=new URL(request.url);if(url.origin===self.location.origin&&url.pathname.startsWith('/__offline_audio__/')){event.respondWith(offlineAudioResponse(request));return}if(url.origin===self.location.origin&&url.pathname.startsWith('/__offline_artwork__/')){event.respondWith(offlineArtworkResponse(request));return}if(request.mode==='navigate'){event.respondWith((async()=>{const cache=await caches.open(CACHE_NAME);try{const controller=new AbortController(),timer=setTimeout(()=>controller.abort(),4500);let response;try{response=await fetch(request,{cache:'no-store',signal:controller.signal})}finally{clearTimeout(timer)}if(response&&response.ok){const copy=response.clone();const contentType=(copy.headers.get('Content-Type')||'').toLowerCase();if(contentType.includes('text/html')){const text=await copy.text();if(text&&text.includes('Radio Vault'))await cache.put(SHELL_KEY,new Response(text,{status:200,headers:{'Content-Type':'text/html; charset=utf-8','Cache-Control':'no-store'}}))}return response}}catch{}const cached=await cache.match(SHELL_KEY);if(cached)return cached;return new Response('<!doctype html><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="theme-color" content="#101010"><body style="margin:0;background:#101010;color:#f4f4f4;font-family:-apple-system,BlinkMacSystemFont,sans-serif;padding:calc(env(safe-area-inset-top) + 28px) 24px"><h1 style="color:#f2c94c">Radio Vault</h1><p>The offline interface could not be restored.</p><p>Reconnect to Radio Vault once, reload this page, then keep the secure bookmark for offline use.</p><button onclick="location.reload()" style="padding:12px 16px;border:0;border-radius:10px;background:#f2c94c;color:#111;font-weight:700">Try again</button></body>',{status:503,headers:{'Content-Type':'text/html; charset=utf-8','Cache-Control':'no-store'}})})())}});
""";

    private const string WebClientHtml = """
<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <meta
      name="viewport"
      content="width=device-width,initial-scale=1,viewport-fit=cover"
    />
    <meta name="theme-color" content="#f2c94c" />
    <meta name="application-name" content="Radio Vault Web" />
    <meta name="apple-mobile-web-app-title" content="Radio Vault Web" />
    <link rel="manifest" href="/manifest.webmanifest?token=__TOKEN__&v=__APP_VERSION__" />
    <link rel="apple-touch-icon" sizes="180x180" href="/app-icon-180.png?token=__TOKEN__&v=__APP_VERSION__" />
    <link rel="icon" type="image/png" sizes="192x192" href="/app-icon-192.png?token=__TOKEN__&v=__APP_VERSION__" />
    <meta name="apple-mobile-web-app-capable" content="yes" />
    <meta
      name="apple-mobile-web-app-status-bar-style"
      content="black-translucent"
    />
    <title>Radio Vault Web</title>
    <style>
      :root {
        color-scheme: dark;
        --bg: #101010;
        --panel: #1d1d1d;
        --panel2: #272727;
        --panel3: #303030;
        --line: #3a3a3a;
        --text: #f4f4f4;
        --muted: #aaa;
        --accent: #f2c94c;
        --accent2: #ffe27c;
        --transcript: #43c7bd;
        --transcript-soft: rgba(67, 199, 189, 0.13);
        --danger: #ff8f8f;
        --shadow: 0 18px 55px rgba(0, 0, 0, 0.45);
      }
      * {
        box-sizing: border-box;
      }
      html,
      body {
        min-height: 100%;
      }
      body {
        margin: 0;
        background:
          radial-gradient(circle at 80% -10%, #2b2617 0, transparent 32%),
          var(--bg);
        color: var(--text);
        font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      }
      button,
      select,
      input {
        font: inherit;
      }
      button {
        touch-action: manipulation;
      }
      header {
        position: sticky;
        top: 0;
        z-index: 5;
        background: rgba(16, 16, 16, 0.94);
        backdrop-filter: blur(18px);
        padding: max(14px, env(safe-area-inset-top)) 14px 10px;
        border-bottom: 1px solid var(--line);
      }
      .brand {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 12px;
      }
      .brandMark {
        display: flex;
        align-items: center;
        gap: 10px;
      }
      .menuToggle,
      .menuScrim {
        display: none;
      }
      .menuToggle {
        width: 44px;
        height: 44px;
        min-width: 44px;
        padding: 0;
        border: 1px solid var(--line);
        border-radius: 10px;
        background: var(--panel2);
        color: var(--text);
        place-items: center;
      }
      .menuToggleGlyph {
        position: relative;
        width: 20px;
        height: 16px;
        display: block;
      }
      .menuToggleGlyph span {
        position: absolute;
        left: 0;
        width: 20px;
        height: 2px;
        border-radius: 2px;
        background: currentColor;
        transform-origin: center;
        transition: transform var(--motion-fast, 160ms) ease, opacity var(--motion-fast, 160ms) ease;
      }
      .menuToggleGlyph span:nth-child(1) { top: 0; }
      .menuToggleGlyph span:nth-child(2) { top: 7px; }
      .menuToggleGlyph span:nth-child(3) { top: 14px; }
      body.menuOpen .menuToggleGlyph span:nth-child(1) { transform: translateY(7px) rotate(45deg); }
      body.menuOpen .menuToggleGlyph span:nth-child(2) { opacity: 0; }
      body.menuOpen .menuToggleGlyph span:nth-child(3) { transform: translateY(-7px) rotate(-45deg); }
      .vaultLogo {
        width: 30px;
        height: 30px;
        display: block;
        object-fit: contain;
        filter: drop-shadow(0 3px 5px rgba(0,0,0,.28));
      }
      .brand h1 {
        font-size: 21px;
        margin: 0 0 10px;
      }
      .stateRow {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 10px;
      }
      .mini {
        padding: 6px 9px;
        font-size: 11px;
        background: transparent;
        color: var(--muted);
        border: 1px solid var(--line);
        border-radius: 8px;
      }
      .api {
        font-size: 10px;
        color: var(--muted);
        border: 1px solid var(--line);
        border-radius: 999px;
        padding: 4px 7px;
      }
      .state {
        font-size: 11px;
        color: var(--muted);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .state.live {
        color: var(--accent);
      }
      .primaryNav {
        display: grid;
        grid-template-columns: repeat(5, minmax(0, 1fr));
        gap: 6px;
        margin-top: 12px;
      }
      .primaryTab {
        min-width: 0;
        border: 1px solid transparent;
        background: transparent;
        color: var(--muted);
        border-radius: 10px;
        padding: 9px 5px;
        font-size: 12px;
        font-weight: 720;
      }
      .primaryTab.active {
        background: var(--panel2);
        color: var(--text);
        border-color: #4a432d;
        box-shadow: inset 0 -2px 0 var(--accent);
      }
      .libraryTools {
        margin-top: 10px;
      }
      .libraryViews {
        display: flex;
        gap: 7px;
        overflow-x: auto;
        padding: 0 0 9px;
        scrollbar-width: none;
      }
      .libraryViews::-webkit-scrollbar {
        display: none;
      }
      .viewChip {
        white-space: nowrap;
        border: 1px solid var(--line);
        background: var(--panel);
        color: var(--muted);
        border-radius: 999px;
        padding: 7px 11px;
        font-size: 12px;
        font-weight: 680;
      }
      .viewChip.active {
        background: var(--accent);
        color: #111;
        border-color: var(--accent);
      }
      .viewIntro {
        margin: 2px 2px 12px;
      }
      .viewIntro h2 {
        margin: 0;
        font-size: 25px;
        line-height: 1.1;
      }
      .viewIntro p {
        margin: 6px 0 0;
        color: var(--muted);
        font-size: 13px;
      }
      .dashboardHero {
        padding: 18px;
        margin-bottom: 14px;
        border: 1px solid #5d5024;
        border-radius: 17px;
        background: linear-gradient(145deg, #2b2618, #191919 68%);
        box-shadow: var(--shadow);
      }
      .dashboardHero .eyebrow {
        color: var(--accent);
        font-size: 12px;
        font-weight: 800;
        letter-spacing: 0.06em;
        text-transform: uppercase;
      }
      .dashboardHero h3 {
        margin: 7px 0 6px;
        font-size: 24px;
      }
      .dashboardHero p {
        margin: 0;
        color: var(--muted);
        line-height: 1.45;
      }
      .dashboardStats {
        display: grid;
        grid-template-columns: repeat(3, 1fr);
        gap: 8px;
        margin-top: 14px;
      }
      .dashboardStat {
        padding: 10px;
        border-radius: 11px;
        background: rgba(0, 0, 0, 0.22);
        border: 1px solid rgba(255, 255, 255, 0.08);
      }
      .dashboardStat strong {
        display: block;
        font-size: 18px;
        color: var(--accent);
      }
      .dashboardStat span {
        color: var(--muted);
        font-size: 11px;
      }
      .dashboardSection {
        margin-top: 18px;
      }
      .dashboardSectionHead {
        display: flex;
        align-items: end;
        justify-content: space-between;
        gap: 12px;
        margin: 0 2px 9px;
      }
      .dashboardSectionHead h3 {
        margin: 0;
        font-size: 18px;
      }
      .dashboardSectionHead button {
        border: 0;
        background: transparent;
        color: var(--accent);
        padding: 4px 0;
        font-weight: 720;
        font-size: 12px;
      }
      .dashboardGrid {
        display: grid;
        gap: 9px;
      }
      .dashboardEpisode {
        display: grid;
        grid-template-columns: 62px minmax(0, 1fr) auto;
        gap: 11px;
        align-items: center;
        padding: 10px;
        border: 1px solid var(--line);
        border-radius: 13px;
        background: linear-gradient(145deg, var(--panel), #191919);
      }
      .dashboardEpisode img,
      .dashboardEpisode .artPlaceholder {
        display: block;
        width: 62px;
        height: 62px;
        border-radius: 9px;
        object-fit: cover;
        background: var(--panel3);
      }
      .dashboardEpisodeText {
        min-width: 0;
      }
      .dashboardEpisodeTitle {
        margin-top: 3px;
        font-size: 14px;
        font-weight: 740;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .dashboardEpisodeMeta {
        margin-top: 4px;
        color: var(--muted);
        font-size: 11px;
      }
      .dashboardPlay {
        width: 40px;
        height: 40px;
        padding: 0;
        border: 0;
        border-radius: 50%;
        background: var(--accent);
        color: #111;
        font-weight: 900;
      }
      select,
      input[type="search"],
      input[type="date"] {
        width: 100%;
        font-size: 16px;
        padding: 12px 14px;
        border-radius: 10px;
        border: 1px solid var(--line);
        background: var(--panel);
        color: var(--text);
      }
      .filters {
        display: grid;
        grid-template-columns: 1fr;
        gap: 8px;
      }
      main {
        padding: 12px 12px calc(106px + env(safe-area-inset-bottom));
        max-width: 920px;
        margin: auto;
      }
      .count {
        font-size: 12px;
        color: var(--muted);
        margin: 2px 2px 10px;
      }
      .episode {
        content-visibility: auto;
        contain-intrinsic-size: 190px;
        padding: 14px;
        margin: 0 0 10px;
        background: linear-gradient(145deg, var(--panel), #191919);
        border: 1px solid var(--line);
        border-radius: 14px;
        overflow: hidden;
      }
      .cover {
        width: 72px;
        height: 72px;
        border-radius: 10px;
        object-fit: cover;
        float: right;
        margin: 0 0 8px 12px;
        background: #292929;
      }
      .show {
        color: var(--accent);
        font-weight: 750;
        font-size: 13px;
      }
      .title {
        font-size: 17px;
        font-weight: 700;
        margin: 4px 0;
      }
      .meta,
      .summary {
        color: var(--muted);
        font-size: 13px;
      }
      .summary {
        margin-top: 7px;
        display: -webkit-box;
        -webkit-line-clamp: 2;
        -webkit-box-orient: vertical;
        overflow: hidden;
      }
      .progress {
        height: 5px;
        background: #333;
        border-radius: 4px;
        margin-top: 10px;
        overflow: hidden;
      }
      .progress span {
        display: block;
        height: 100%;
        background: linear-gradient(90deg, var(--accent), var(--accent2));
      }
      .actions {
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
        margin-top: 11px;
      }
      .actions button,
      .primary {
        padding: 9px 14px;
        border: 0;
        border-radius: 9px;
        background: var(--accent);
        color: #111;
        font-weight: 780;
      }
      .actions .secondary,
      .secondary {
        background: var(--panel2);
        color: var(--text);
        border: 1px solid var(--line);
      }
      .actions .ghost {
        background: transparent;
        color: var(--muted);
        border: 1px solid var(--line);
      }
      .empty {
        text-align: center;
        color: var(--muted);
        padding: 55px 20px;
      }
      .detail {
        position: fixed;
        inset: 0;
        z-index: 8;
        background: var(--bg);
        overflow: auto;
        padding-bottom: calc(106px + env(safe-area-inset-bottom));
        display: none;
      }
      .detail.open {
        display: block;
      }
      .detailHeader {
        position: sticky;
        top: 0;
        z-index: 2;
        display: flex;
        align-items: center;
        gap: 10px;
        padding: max(12px, env(safe-area-inset-top)) 14px 12px;
        background: rgba(16, 16, 16, 0.96);
        border-bottom: 1px solid var(--line);
      }
      .back {
        border: 1px solid var(--line);
        background: var(--panel);
        color: var(--text);
        padding: 8px 11px;
        border-radius: 9px;
      }
      .detailBody {
        max-width: 760px;
        margin: auto;
        padding: 18px 14px;
      }
      .hero {
        display: grid;
        grid-template-columns: 112px 1fr;
        gap: 16px;
        align-items: start;
      }
      .hero img {
        width: 112px;
        height: 112px;
        border-radius: 12px;
        object-fit: cover;
        background: var(--panel2);
      }
      .hero h2 {
        font-size: 23px;
        line-height: 1.15;
        margin: 4px 0 7px;
      }
      .section {
        margin-top: 16px;
        padding: 16px;
        background: var(--panel);
        border: 1px solid var(--line);
        border-radius: 12px;
      }
      .section h3 {
        font-size: 16px;
        margin: 0 0 10px;
      }
      .chips {
        display: flex;
        gap: 7px;
        flex-wrap: wrap;
      }
      .chip {
        background: var(--panel2);
        border: 1px solid var(--line);
        border-radius: 999px;
        padding: 6px 9px;
        font-size: 12px;
      }
      .moment {
        padding: 9px 0;
        border-bottom: 1px solid var(--line);
      }
      .moment:last-child {
        border-bottom: 0;
      }
      .moment button {
        border: 0;
        background: none;
        color: var(--accent);
        font-weight: 700;
        padding: 0;
      }
      .healthGrid {
        display: grid;
        grid-template-columns: repeat(2, 1fr);
        gap: 10px;
      }
      .healthCard {
        background: var(--panel);
        border: 1px solid var(--line);
        border-radius: 12px;
        padding: 15px;
      }
      .healthScore {
        font-size: 30px;
        font-weight: 750;
        color: var(--accent);
      }
      .queueItem {
        display: flex;
        gap: 12px;
        align-items: center;
        padding: 12px;
        background: var(--panel);
        border: 1px solid var(--line);
        border-radius: 12px;
        margin-bottom: 9px;
      }
      .queuePos {
        font-size: 22px;
        color: var(--accent);
        min-width: 30px;
      }
      .muted {
        color: var(--muted);
      }
      .downloadTray {
        position: sticky;
        top: 0;
        z-index: 6;
        margin: 0 auto 10px;
        max-width: 920px;
        padding: 11px 13px;
        background: #211f18;
        border: 1px solid #66551e;
        border-radius: 12px;
        box-shadow: var(--shadow);
      }
      .downloadTray[hidden] {
        display: none;
      }
      .downloadHead {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 10px;
      }
      .downloadHead strong {
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .downloadTrack {
        height: 7px;
        background: #3b382d;
        border-radius: 7px;
        overflow: hidden;
        margin-top: 9px;
      }
      .downloadTrack span {
        display: block;
        height: 100%;
        width: 0;
        background: linear-gradient(90deg, var(--accent), var(--accent2));
        transition: width 0.12s linear;
      }
      .downloadMeta {
        display: flex;
        justify-content: space-between;
        gap: 10px;
        color: var(--muted);
        font-size: 11px;
        margin-top: 6px;
      }
      .downloadBadge {
        display: inline-flex;
        align-items: center;
        gap: 5px;
        margin-left: 7px;
        padding: 3px 7px;
        border-radius: 999px;
        background: #302b18;
        border: 1px solid #66551e;
        color: var(--accent);
        font-size: 10px;
        font-weight: 750;
      }
      .offlineState {
        color: var(--accent);
      }
      body.offlineOnly .api {
        opacity: 0.45;
      }
      body.offlineOnly .filters {
        opacity: 0.92;
      }
      .dangerButton {
        border-color: #704040 !important;
        color: #ffcaca !important;
      }
      .storageNote {
        font-size: 11px;
        color: var(--muted);
        margin-top: 8px;
      }
      .offlineLibrary {
        display: grid;
        gap: 10px;
        margin-bottom: 12px;
      }
      .offlineSummary {
        padding: 14px;
        background: linear-gradient(145deg, #242116, #191919);
        border: 1px solid #66551e;
        border-radius: 14px;
      }
      .offlineSummaryRow {
        display: flex;
        justify-content: space-between;
        gap: 10px;
        align-items: center;
      }
      .offlineSummary strong {
        color: var(--accent);
      }
      .offlineTools {
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
        margin-top: 10px;
      }
      .offlineTools select,
      .offlineTools button {
        background: var(--panel2);
        color: var(--text);
        border: 1px solid var(--line);
        border-radius: 9px;
        padding: 9px 11px;
      }
      .sourcePill {
        display: inline-flex;
        align-items: center;
        border-radius: 999px;
        padding: 4px 8px;
        margin-top: 7px;
        font-size: 11px;
        font-weight: 750;
        background: #302b18;
        color: var(--accent);
        border: 1px solid #66551e;
      }
      .bootGuard {
        position: fixed;
        z-index: 30;
        inset: 0;
        display: grid;
        place-items: center;
        padding: 24px;
        background: #101010;
        color: var(--text);
      }
      .bootGuard[hidden] {
        display: none;
      }
      .bootCard {
        width: min(100%, 430px);
        padding: 22px;
        border: 1px solid var(--line);
        border-radius: 16px;
        background: linear-gradient(145deg, var(--panel), #171717);
        box-shadow: var(--shadow);
        text-align: center;
      }
      .bootLogo {
        width: 52px;
        height: 52px;
        margin: 0 auto 14px;
        display: block;
        object-fit: contain;
        filter: drop-shadow(0 7px 12px rgba(0,0,0,.35));
      }
      .bootCard h2 {
        margin: 0 0 8px;
      }
      .bootCard p {
        margin: 0;
        color: var(--muted);
        line-height: 1.45;
      }
      .bootActions {
        display: flex;
        justify-content: center;
        gap: 9px;
        flex-wrap: wrap;
        margin-top: 16px;
      }
      .bootActions button {
        padding: 10px 14px;
        border-radius: 9px;
        border: 1px solid var(--line);
        background: var(--panel2);
        color: var(--text);
        font-weight: 700;
      }
      .bootActions .primary {
        background: var(--accent);
        color: #111;
        border-color: var(--accent);
      }
      /* Radio Vault custom player: the audio element remains hidden for reliable iOS streaming. */
      audio {
        display: none;
      }
      .rvMiniPlayer {
        position: fixed;
        z-index: 10;
        left: 0;
        right: 0;
        bottom: 0;
        display: grid;
        grid-template-columns: 48px minmax(0, 1fr) 48px;
        gap: 10px;
        align-items: center;
        min-height: calc(68px + env(safe-area-inset-bottom));
        padding: 9px max(12px, env(safe-area-inset-right))
          calc(9px + env(safe-area-inset-bottom))
          max(12px, env(safe-area-inset-left));
        background: #1d1d1d;
        border: 0;
        border-top: 1px solid #5b4e24;
        border-radius: 0;
        box-shadow: 0 -12px 36px rgba(0, 0, 0, 0.42);
        opacity: 1;
        pointer-events: auto;
      }
      .rvMiniPlayer.visible {
        opacity: 1;
      }
      .rvMiniPlayer.idle .rvMiniArt {
        visibility: visible !important;
        object-fit: contain;
        padding: 10px;
        border: 1px solid #5b4e24;
        background: #171717;
      }
      .rvMiniPlayer.idle .rvMiniText {
        cursor: default;
      }
      .rvMiniPlayer.idle .roundButton {
        opacity: 0.48;
      }
      .rvMiniPlayer .roundButton:disabled {
        cursor: default;
      }
      .rvMiniArt {
        width: 48px;
        height: 48px;
        border-radius: 9px;
        object-fit: cover;
        background: var(--panel3);
      }
      .rvMiniText {
        min-width: 0;
        border: 0;
        background: transparent;
        color: inherit;
        text-align: left;
        padding: 0;
      }
      .rvMiniShow {
        font-size: 11px;
        color: var(--accent);
        font-weight: 750;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .rvMiniTitle {
        font-size: 14px;
        font-weight: 720;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
        margin-top: 3px;
      }
      .rvIcon {
        width: 28px;
        height: 28px;
        display: block;
        overflow: visible;
        margin: auto;
        flex: 0 0 auto;
      }
      .rvIcon.outline {
        fill: none;
        stroke: currentColor;
        stroke-width: 1.7;
        stroke-linecap: round;
        stroke-linejoin: round;
      }
      .rvSpinner {
        animation: rvSpin 0.8s linear infinite;
        transform-origin: 50% 50%;
      }
      @keyframes rvSpin { to { transform: rotate(360deg); } }
      .deviceStateIcon {
        width: 14px;
        height: 14px;
        vertical-align: -2px;
        margin-right: 5px;
        color: var(--accent);
      }
      .roundButton {
        width: 46px;
        height: 46px;
        border-radius: 50%;
        border: 0;
        background: var(--accent);
        color: #fff;
        font-size: 20px;
        font-weight: 900;
        display: grid;
        place-items: center;
        padding: 0;
        line-height: 1;
      }
      .roundButton .rvIcon {
        width: 27px;
        height: 27px;
      }
      .rvFullPlayer {
        position: fixed;
        z-index: 12;
        inset: 0;
        background:
          radial-gradient(circle at 50% 8%, #413719 0, transparent 32%), #111;
        overflow: auto;
        padding: max(12px, env(safe-area-inset-top)) 18px
          max(26px, env(safe-area-inset-bottom));
        transform: translateY(100%);
        transition: 0.28s ease;
      }
      .rvFullPlayer.open {
        transform: none;
      }
      .playerTop {
        display: flex;
        align-items: center;
        justify-content: space-between;
      }
      .playerTop button {
        border: 0;
        background: var(--panel);
        color: var(--text);
        border-radius: 10px;
        padding: 9px 12px;
      }
      .playerArtwork {
        display: block;
        width: min(74vw, 340px);
        aspect-ratio: 1;
        margin: 18px auto;
        border-radius: 22px;
        object-fit: cover;
        background: linear-gradient(145deg, #292929, #191919);
        box-shadow: 0 22px 60px rgba(0, 0, 0, 0.48);
      }
      .playerIdentity {
        text-align: center;
        max-width: 560px;
        margin: auto;
      }
      .playerIdentity .show {
        font-size: 14px;
      }
      .playerIdentity h2 {
        font-size: 24px;
        line-height: 1.2;
        margin: 7px 0 5px;
      }
      .playerIdentity .state {
        font-size: 12px;
      }
      .seekWrap {
        max-width: 620px;
        margin: 25px auto 0;
      }
      .seek {
        width: 100%;
        appearance: none;
        height: 7px;
        border-radius: 7px;
        background: linear-gradient(
          90deg,
          var(--accent) var(--seek, 0%),
          #393939 var(--seek, 0%)
        );
        outline: none;
      }
      .seek::-webkit-slider-thumb {
        appearance: none;
        width: 20px;
        height: 20px;
        border-radius: 50%;
        background: var(--accent);
        border: 3px solid #111;
        box-shadow: 0 0 0 1px var(--accent);
      }
      .timeRow {
        display: flex;
        justify-content: space-between;
        color: var(--muted);
        font-size: 12px;
        margin-top: 7px;
      }
      .transport {
        display: flex;
        justify-content: center;
        align-items: center;
        gap: 24px;
        margin: 18px auto;
      }
      .transport button {
        border: 0;
        background: transparent;
        color: var(--text);
        font-weight: 800;
        min-width: 52px;
        height: 52px;
        border-radius: 50%;
      }
      .transport .mainPlay {
        width: 70px;
        height: 70px;
        background: var(--accent);
        color: #fff;
        font-size: 28px;
        display: grid;
        place-items: center;
        padding: 0;
        line-height: 1;
      }
      .transport .mainPlay .rvIcon {
        width: 38px;
        height: 38px;
      }
      .playerOptions {
        display: flex;
        justify-content: center;
        gap: 10px;
        flex-wrap: wrap;
        max-width: 620px;
        margin: 12px auto;
      }
      .playerOptions button,
      .playerOptions select {
        border: 1px solid var(--line);
        background: var(--panel);
        color: var(--text);
        padding: 10px 12px;
        border-radius: 10px;
      }
      .playerNotice {
        max-width: 620px;
        margin: 12px auto;
        text-align: center;
        color: var(--muted);
        font-size: 12px;
        min-height: 18px;
      }
      /* The inactive output stays fully synchronized but exposes only the
         custom centre transfer control. */
      .rvMiniPlayer.inactiveOutput {
        background: linear-gradient(90deg, #211f18, #1d1d1d 46%);
        border-top-color: var(--accent);
      }
      .rvMiniPlayer.inactiveOutput .rvMiniShow {
        color: #ffe289;
      }
      .rvFullPlayer.inactiveOutput .playerArtwork {
        box-shadow: 0 22px 64px rgba(242, 201, 76, 0.18);
      }
      .rvFullPlayer.inactiveOutput .transport button:not(.mainPlay),
      .rvFullPlayer.inactiveOutput .playerOptions select,
      .rvFullPlayer.inactiveOutput .playerOptions button {
        opacity: 0.34;
      }
      .rvFullPlayer.inactiveOutput .mainPlay,
      .rvMiniPlayer.inactiveOutput .roundButton {
        box-shadow: 0 0 0 1px rgba(247, 201, 72, 0.2), 0 8px 24px rgba(247, 201, 72, 0.16);
      }
      .rvFullPlayer.inactiveOutput .seek {
        opacity: 0.48;
      }
      .rvFullPlayer button:disabled,
      .rvFullPlayer select:disabled,
      .rvFullPlayer input:disabled {
        cursor: default;
      }
      .toast {
        position: fixed;
        z-index: 20;
        left: 20px;
        right: 20px;
        bottom: calc(92px + env(safe-area-inset-bottom));
        max-width: 520px;
        margin: auto;
        background: #2d2d2d;
        border: 1px solid var(--line);
        border-radius: 12px;
        padding: 12px 14px;
        box-shadow: var(--shadow);
        transform: translateY(30px);
        opacity: 0;
        pointer-events: none;
        transition: 0.2s;
      }
      .toast.show {
        transform: none;
        opacity: 1;
      }
      .toast.error {
        border-color: #7a3f3f;
        color: #ffd0d0;
      }
      .transferDiagnostic {
        position: fixed;
        z-index: 40;
        inset: max(14px, env(safe-area-inset-top)) 14px calc(14px + env(safe-area-inset-bottom));
        display: none;
        flex-direction: column;
        max-width: 680px;
        margin: auto;
        background: #171717;
        border: 1px solid #6f4747;
        border-radius: 18px;
        box-shadow: 0 24px 80px rgba(0,0,0,.72);
        overflow: hidden;
      }
      .transferDiagnostic.open { display: flex; }
      .transferDiagnosticHeader {
        display: flex; align-items: center; justify-content: space-between; gap: 12px;
        padding: 15px 16px; border-bottom: 1px solid var(--line); background: #211b1b;
      }
      .transferDiagnosticHeader strong { color: #ffd0d0; }
      .transferDiagnosticHeader button, .transferDiagnosticActions button {
        border: 1px solid var(--line); background: #292929; color: var(--text);
        border-radius: 10px; padding: 9px 12px; font: inherit;
      }
      .transferDiagnosticBody {
        flex: 1; overflow: auto; padding: 14px 16px;
        font: 12px/1.45 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
        white-space: pre-wrap; overflow-wrap: anywhere; color: #eee;
      }
      .transferDiagnosticActions {
        display: flex; gap: 10px; padding: 12px 16px; border-top: 1px solid var(--line);
      }
      .transferDiagnosticActions button:first-child { background: var(--accent); color: #111; border-color: transparent; font-weight: 750; }
      /* Native-feeling interaction polish. Kept CSS-only so the proven runtime remains untouched. */
      :root { --motion-fast: 160ms; --motion: 230ms; --motion-slow: 320ms; --spring: cubic-bezier(0.22, 1, 0.36, 1); }
      body { -webkit-tap-highlight-color: transparent; overscroll-behavior-y: none; }
      button, .episode, .dashboardEpisode, .viewChip, .primaryTab { transition: transform var(--motion-fast) var(--spring), background-color var(--motion-fast) ease, border-color var(--motion-fast) ease, opacity var(--motion-fast) ease, box-shadow var(--motion-fast) ease; }
      button:active, .episode:active, .dashboardEpisode:active { transform: scale(0.975); }
      header { box-shadow: 0 10px 28px rgba(0,0,0,.22); }
      .primaryNav { padding: 3px; border-radius: 13px; background: rgba(255,255,255,.035); border: 1px solid rgba(255,255,255,.045); }
      .primaryTab.active { transform: translateY(-1px); box-shadow: inset 0 -2px 0 var(--accent), 0 7px 18px rgba(0,0,0,.2); }
      .viewChip.active { box-shadow: 0 7px 20px rgba(242,201,76,.14); }
      .viewIntro { animation: rvTitleIn var(--motion-slow) var(--spring) both; }
      #list > * { animation: rvCardIn var(--motion-slow) var(--spring) both; }
      #list > *:nth-child(2) { animation-delay: 25ms; }
      #list > *:nth-child(3) { animation-delay: 50ms; }
      #list > *:nth-child(4) { animation-delay: 75ms; }
      #list > *:nth-child(5) { animation-delay: 100ms; }
      #list > *:nth-child(n+6) { animation-delay: 120ms; }
      .episode, .dashboardEpisode { box-shadow: 0 8px 28px rgba(0,0,0,.16); }
      .episode:hover, .dashboardEpisode:hover { border-color: #4b4635; box-shadow: 0 13px 34px rgba(0,0,0,.25); }
      .cover, .dashboardEpisode img, .dashboardEpisode .artPlaceholder { box-shadow: 0 8px 22px rgba(0,0,0,.28); }
      .detail { display: block; visibility: hidden; pointer-events: none; opacity: 0; transform: translateX(10%); transition: transform var(--motion-slow) var(--spring), opacity var(--motion) ease, visibility 0s linear var(--motion-slow); }
      .detail.open { display: block; visibility: visible; pointer-events: auto; opacity: 1; transform: translateX(0); transition-delay: 0s; }
      .rvMiniPlayer { transition: box-shadow var(--motion) ease, background-color var(--motion) ease; }
      .rvMiniPlayer:not(.idle) { box-shadow: 0 -16px 44px rgba(0,0,0,.52); }
      .rvMiniArt { transition: transform var(--motion) var(--spring), opacity var(--motion-fast) ease; }
      .rvMiniPlayer:active .rvMiniArt { transform: scale(.94); }
      .rvFullPlayer { opacity: 0; transform: translateY(100%) scale(.985); transition: transform var(--motion-slow) var(--spring), opacity var(--motion) ease; }
      .rvFullPlayer.open { opacity: 1; transform: translateY(0) scale(1); }
      .playerArtwork { transition: transform var(--motion-slow) var(--spring), box-shadow var(--motion) ease; }
      .rvFullPlayer.open .playerArtwork { animation: rvArtworkIn 420ms var(--spring) both; }
      .skeletonList { display: grid; gap: 10px; }
      .skeletonCard { min-height: 132px; border-radius: 14px; border: 1px solid var(--line); background: linear-gradient(145deg, var(--panel), #191919); padding: 14px; overflow: hidden; }
      .skeletonLine, .skeletonSquare, .skeletonPill { position: relative; overflow: hidden; background: #2b2b2b; }
      .skeletonLine::after, .skeletonSquare::after, .skeletonPill::after { content: ""; position: absolute; inset: 0; transform: translateX(-100%); background: linear-gradient(90deg, transparent, rgba(255,255,255,.075), transparent); animation: rvShimmer 1.2s infinite; }
      .skeletonSquare { width: 72px; height: 72px; border-radius: 10px; float: right; margin-left: 12px; }
      .skeletonLine { height: 12px; border-radius: 7px; margin: 8px 0; width: 72%; }
      .skeletonLine.short { width: 42%; }
      .skeletonLine.long { width: 88%; }
      .skeletonPill { width: 88px; height: 30px; border-radius: 999px; margin-top: 15px; }
      @keyframes rvCardIn { from { opacity: 0; transform: translateY(12px) scale(.992); } to { opacity: 1; transform: none; } }
      @keyframes rvTitleIn { from { opacity: 0; transform: translateY(8px); } to { opacity: 1; transform: none; } }
      @keyframes rvArtworkIn { from { opacity: .25; transform: translateY(20px) scale(.94); } to { opacity: 1; transform: none; } }
      @keyframes rvShimmer { to { transform: translateX(100%); } }
      @media (prefers-reduced-motion: reduce) { *, *::before, *::after { scroll-behavior: auto !important; animation-duration: .01ms !important; animation-iteration-count: 1 !important; transition-duration: .01ms !important; } }

      .skipLink { position: fixed; z-index: 150; left: 12px; top: 8px; transform: translateY(-160%); padding: 10px 13px; border-radius: 9px; background: var(--accent); color: #111; font-weight: 800; text-decoration: none; transition: transform var(--motion-fast) ease; }
      .skipLink:focus { transform: translateY(0); }
      :where(button, input, select, a, [tabindex]):focus-visible { outline: 3px solid var(--accent2); outline-offset: 3px; }
      .appUpdateBanner { position: fixed; z-index: 75; left: 12px; right: 12px; top: calc(max(12px, env(safe-area-inset-top)) + 112px); display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 12px 13px; border: 1px solid #6a5b24; border-radius: 13px; background: #292414; box-shadow: var(--shadow); }
      .appUpdateBanner[hidden] { display: none; }
      .appUpdateBanner strong { color: var(--accent); display: block; }
      .appUpdateBanner .muted { margin-top: 2px; font-size: 11px; }
      .appUpdateActions { display: flex; gap: 7px; flex: 0 0 auto; }
      .appUpdateActions button { border: 1px solid var(--line); border-radius: 8px; padding: 7px 10px; background: var(--panel2); color: var(--text); }
      .appUpdateActions .primary { background: var(--accent); color: #111; border-color: var(--accent); font-weight: 800; }
      .stateActions { display: flex; align-items: center; gap: 7px; }
      .filterActions { display: flex; justify-content: space-between; align-items: center; gap: 10px; margin-top: 8px; color: var(--muted); font-size: 11px; }
      .filterActions button { flex: 0 0 auto; }
      .connectionBanner { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin: 0 0 12px; padding: 11px 13px; border: 1px solid #66551e; border-radius: 12px; background: #242116; color: var(--muted); font-size: 12px; }
      .connectionBanner strong { color: var(--accent); }
      .connectionBanner button { border: 1px solid var(--line); background: var(--panel2); color: var(--text); border-radius: 8px; padding: 7px 10px; }
      .sourceBoundary { display: inline-flex; align-items: center; gap: 6px; padding: 4px 8px; border: 1px solid var(--line); border-radius: 999px; color: var(--muted); font-size: 10px; font-weight: 760; }
      .sourceBoundary.server { border-color: #4b4635; color: var(--accent); }
      .sourceBoundary.device { border-color: #66551e; color: var(--accent2); }
      .reconnectPulse { display: inline-block; width: 7px; height: 7px; border-radius: 50%; background: currentColor; box-shadow: 0 0 0 0 rgba(242,201,76,.5); animation: rvPulse 1.4s infinite; }
      @keyframes rvPulse { 70% { box-shadow: 0 0 0 8px rgba(242,201,76,0); } 100% { box-shadow: 0 0 0 0 rgba(242,201,76,0); } }
      @media (min-width: 700px) {
        .filters {
          grid-template-columns: minmax(180px, 1.2fr) repeat(4, minmax(118px, .75fr));
        }
        .filterSearch { grid-column: 1 / -1; }
        .healthGrid {
          grid-template-columns: repeat(5, 1fr);
        }
        .dashboardGrid {
          grid-template-columns: repeat(2, minmax(0, 1fr));
        }
        .primaryTab {
          font-size: 13px;
        }
        .playerArtwork {
          width: 300px;
        }
      }

      @media (min-width: 1080px) {
        header { position: fixed; inset: 0 auto 0 0; width: 270px; padding: 28px 18px; border-right: 1px solid var(--line); border-bottom: 0; overflow-y: auto; }
        .brand, .stateRow { align-items: flex-start; }
        .brand { flex-direction: column; }
        .brand h1 { margin-bottom: 0; }
        .stateRow { flex-direction: column; margin-top: 18px; }
        .stateActions { width: 100%; }
        .stateActions button { flex: 1; }
        .primaryNav { grid-template-columns: 1fr; margin-top: 22px; }
        .primaryTab { text-align: left; padding: 12px 13px; font-size: 14px; }
        .primaryTab.active { box-shadow: inset 3px 0 0 var(--accent); }
        .libraryTools { margin-top: 20px; }
        .libraryViews { flex-wrap: wrap; overflow: visible; }
        .filters { grid-template-columns: 1fr; }
        .filterSearch { grid-column: auto; }
        .filterActions { align-items: flex-start; flex-direction: column; }
        main { max-width: 1120px; margin-left: 290px; padding-top: 28px; }
        .rvMiniPlayer { left: 290px; }
        .appUpdateBanner { left: 302px; top: 20px; right: 20px; }
        .dashboardGrid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
      }

      .syncStatusButton { width: 28px; height: 28px; min-width: 28px; padding: 0; border: 0; border-radius: 50%; display: inline-grid; place-items: center; box-shadow: 0 0 0 1px rgba(255,255,255,.12) inset; }
      .syncStatusButton svg { width: 15px; height: 15px; stroke: white; stroke-width: 2.4; fill: none; stroke-linecap: round; stroke-linejoin: round; }
      .syncStatusButton.synced { background: #2e9d5b; }
      .syncStatusButton.syncing { background: #d88416; }
      .syncStatusButton.offline { background: #c84646; }
      .syncStatusButton.attention { background: #c76b22; }
      .syncStatusButton.syncing svg { animation: rvSpin 1s linear infinite; }
      @keyframes rvSpin { to { transform: rotate(360deg); } }
      .compactLibrarySearch { display:flex; gap:8px; align-items:center; }
      .compactLibrarySearch input { flex:1; min-width:0; }
      .filterToggle { white-space:nowrap; position:relative; }
      .filterCount { display:none; min-width:18px; height:18px; padding:0 5px; border-radius:999px; background:var(--accent); color:#111; font-size:10px; font-weight:800; align-items:center; justify-content:center; margin-left:4px; }
      .filterCount.active { display:inline-flex; }
      .advancedFilters[hidden] { display:none !important; }
      .syncSheet { position:fixed; z-index:80; right:12px; top:70px; width:min(340px,calc(100vw - 24px)); background:var(--panel); border:1px solid var(--line); border-radius:16px; padding:14px; box-shadow:0 20px 60px rgba(0,0,0,.45); }
      .syncSheet[hidden] { display:none; }
      .syncSheet h3 { margin:0 0 6px; }
      .syncSheetActions { display:flex; gap:8px; margin-top:12px; justify-content:flex-end; }
      .storageMeter { height:8px; background:rgba(255,255,255,.08); border-radius:999px; overflow:hidden; margin:8px 0; }
      .storageMeter span { display:block; height:100%; background:var(--accent); }
      .downloadStatePill { display:inline-flex; align-items:center; gap:5px; border-radius:999px; padding:3px 8px; font-size:10px; background:rgba(255,255,255,.08); margin-left:6px; }
      .downloadBadge.repair { background:rgba(210,132,22,.22); color:#ffd18a; }
      .primaryTab { display:flex; align-items:center; gap:9px; }
      .primaryTab .navIcon { width:18px; height:18px; flex:0 0 18px; color:var(--muted); }
      .primaryTab.active .navIcon { color:var(--accent); }
      .primaryTab.transcriptsTab.active { color:var(--transcript); box-shadow:inset 0 -2px 0 var(--transcript),0 7px 18px rgba(0,0,0,.2); }
      .primaryTab.transcriptsTab.active .navIcon { color:var(--transcript); }
      body[data-view="transcripts"] .viewIntro h2 { color:var(--transcript); }
      .transcriptWorkspace { display:grid; gap:14px; }
      .transcriptOverview { display:grid; grid-template-columns:repeat(3,minmax(0,1fr)); gap:10px; }
      .transcriptMetric { padding:14px; border:1px solid var(--line); border-radius:12px; background:linear-gradient(145deg,var(--panel),#191f1f); }
      .transcriptMetric strong { display:block; color:var(--transcript); font-size:23px; }
      .transcriptMetric span { color:var(--muted); font-size:11px; }
      .transcriptStatus { display:flex; align-items:center; justify-content:space-between; gap:12px; padding:14px; border:1px solid rgba(67,199,189,.3); border-radius:13px; background:var(--transcript-soft); }
      .transcriptStatus .statusCopy { min-width:0; }
      .transcriptStatus strong { display:block; color:var(--transcript); }
      .transcriptStatus .actions { margin:0; flex:0 0 auto; }
      .transcriptTabs { display:flex; gap:6px; overflow:auto; padding:4px; border:1px solid var(--line); border-radius:12px; background:rgba(255,255,255,.025); }
      .transcriptTabs { scrollbar-width:none; }
      .transcriptTabs::-webkit-scrollbar { display:none; }
      .transcriptTabs button { flex:1; min-width:max-content; padding:9px 12px; border:0; border-radius:8px; background:transparent; color:var(--muted); }
      .transcriptTabs button.active { color:#071817; background:var(--transcript); font-weight:800; }
      .transcriptSearch { width:100%; padding:12px 13px; border:1px solid var(--line); border-radius:10px; background:var(--panel); color:var(--text); }
      .transcriptBrowser { display:grid; grid-template-columns:minmax(260px,.82fr) minmax(0,1.6fr); gap:14px; min-height:470px; }
      .transcriptList,.transcriptViewer,.transcriptRuns { min-width:0; border:1px solid var(--line); border-radius:14px; background:var(--panel); overflow:hidden; }
      .transcriptList { max-height:640px; overflow-y:auto; padding:8px; }
      .transcriptRow { width:100%; display:block; text-align:left; margin:0 0 7px; padding:12px; border:1px solid transparent; border-radius:10px; background:var(--panel2); color:var(--text); }
      .transcriptRow:last-child { margin-bottom:0; }
      .transcriptRow:hover,.transcriptRow.active { border-color:var(--transcript); background:var(--transcript-soft); }
      .transcriptRow .show { color:var(--transcript); }
      .transcriptRow .title { margin:3px 0; font-weight:750; }
      .transcriptViewer { display:flex; flex-direction:column; }
      .transcriptViewerHead { display:flex; align-items:flex-start; justify-content:space-between; gap:12px; padding:14px 15px; border-bottom:1px solid var(--line); }
      .transcriptViewerHead h3 { margin:0 0 4px; }
      .transcriptViewerHead .actions { margin:0; flex-wrap:nowrap; }
      .transcriptSegments { padding:8px 12px 16px; max-height:560px; overflow:auto; }
      .transcriptSegment { display:grid; grid-template-columns:62px minmax(84px,130px) minmax(0,1fr); gap:10px; padding:11px 4px; border-bottom:1px solid var(--line); }
      .transcriptSegment:last-child { border-bottom:0; }
      .transcriptTime { border:0; padding:0; background:none; color:var(--transcript); font-weight:800; text-align:left; }
      .transcriptSpeaker { color:var(--muted); font-size:12px; font-weight:700; }
      .transcriptText { line-height:1.45; overflow-wrap:anywhere; }
      .transcriptRuns { padding:10px; }
      .transcriptRun { padding:13px; margin-bottom:8px; border:1px solid var(--line); border-radius:11px; background:var(--panel2); }
      .transcriptRun:last-child { margin-bottom:0; }
      .runHead,.runMeta { display:flex; align-items:center; justify-content:space-between; gap:10px; }
      .runState { color:var(--transcript); font-weight:800; }
      .runMeta { margin-top:5px; color:var(--muted); font-size:11px; align-items:flex-start; }
      .runProgress { height:5px; margin:10px 0; border-radius:999px; overflow:hidden; background:#141414; }
      .runProgress span { display:block; height:100%; background:var(--transcript); }
      .runActions { display:flex; flex-wrap:wrap; gap:6px; margin-top:10px; }
      .runActions button,.transcriptViewerHead button,.transcriptStatus button { border:1px solid var(--line); border-radius:8px; padding:7px 9px; background:var(--panel3); color:var(--text); }
      .runActions button.primary,.transcriptStatus button.primary { border-color:var(--transcript); background:var(--transcript); color:#071817; font-weight:800; }
      .transcriptEmpty { display:grid; place-items:center; min-height:300px; padding:30px; text-align:center; color:var(--muted); }
      .transcriptQueueHint { margin-top:8px; font-size:11px; color:var(--muted); }
      .workspaceTabs { display:flex; gap:6px; overflow:auto; padding:4px; border:1px solid var(--line); border-radius:12px; background:rgba(255,255,255,.025); scrollbar-width:none; }
      .workspaceTabs::-webkit-scrollbar { display:none; }
      .workspaceTabs button { flex:1; min-width:max-content; padding:9px 12px; border:0; border-radius:8px; background:transparent; color:var(--muted); }
      .workspaceTabs button.active { color:#130f20; background:var(--research); font-weight:800; }
      .researchWorkspace,.settingsWorkspace,.momentsWorkspace { display:grid; gap:14px; }
      .researchMetrics,.settingsMetrics { display:grid; grid-template-columns:repeat(4,minmax(0,1fr)); gap:10px; }
      .researchMetric,.settingsMetric { min-width:0; padding:14px; border:1px solid var(--line); border-radius:12px; background:linear-gradient(145deg,var(--panel),#1d1a26); }
      .researchMetric strong,.settingsMetric strong { display:block; color:var(--research); font-size:23px; overflow-wrap:anywhere; }
      .researchMetric span,.settingsMetric span { color:var(--muted); font-size:11px; }
      .researchToolbar,.settingsToolbar { display:flex; flex-wrap:wrap; gap:9px; align-items:center; justify-content:space-between; }
      .researchToolbar input,.researchToolbar select,.settingsField input,.settingsField select { min-width:0; padding:10px 11px; border:1px solid var(--line); border-radius:9px; background:var(--panel); color:var(--text); }
      .researchToolbar input[type="search"] { flex:1; min-width:220px; }
      .researchRecords,.momentCards,.settingsSections { display:grid; gap:9px; }
      .researchRecord,.momentCard,.settingsCard { padding:14px; border:1px solid var(--line); border-radius:12px; background:var(--panel); }
      .researchRecord { width:100%; color:var(--text); text-align:left; }
      .researchRecord:hover { border-color:var(--research); background:rgba(180,154,242,.07); }
      .researchRecordHead,.momentCardHead,.settingsCardHead { display:flex; justify-content:space-between; gap:12px; align-items:flex-start; }
      .researchRecord h3,.momentCard h3,.settingsCard h3 { margin:0 0 4px; font-size:15px; }
      .researchBadges { display:flex; flex-wrap:wrap; gap:5px; margin-top:9px; }
      .researchBadge { padding:4px 7px; border-radius:999px; background:rgba(180,154,242,.12); color:#cfbef8; font-size:10px; }
      .researchBadge.attention { background:rgba(231,162,76,.13); color:#f3bd75; }
      .researchCoverageGrid { display:grid; grid-template-columns:repeat(auto-fill,minmax(42px,1fr)); gap:5px; }
      .coverageDay { min-height:42px; padding:6px; border:1px solid var(--line); border-radius:7px; background:var(--panel); color:var(--muted); font-size:10px; text-align:left; }
      .coverageDay.audio { border-color:#3d715e; color:#a8dec6; }
      .coverageDay.research { box-shadow:inset 0 -3px 0 var(--research); }
      .coverageDay.missing { border-color:#865f3c; color:#e7b47c; }
      .packDrop { display:grid; gap:10px; padding:18px; border:1px dashed rgba(180,154,242,.5); border-radius:13px; background:rgba(180,154,242,.045); }
      .packPreview { display:grid; grid-template-columns:repeat(3,minmax(0,1fr)); gap:8px; }
      .packPreview div { padding:10px; border-radius:9px; background:var(--panel2); }
      .packPreview strong { display:block; color:var(--research); font-size:18px; }
      .settingsCard { display:grid; gap:12px; }
      .settingsFields { display:grid; grid-template-columns:repeat(3,minmax(0,1fr)); gap:10px; }
      .settingsField { display:grid; gap:5px; color:var(--muted); font-size:11px; }
      .capabilityGrid { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:7px; }
      .capability { padding:10px; border:1px solid var(--line); border-radius:9px; background:var(--panel2); }
      .capability.available strong { color:#8ed5b3; }
      body[data-view="research"] .viewIntro h2 { color:var(--research); }
      body[data-view="moments"] .viewIntro h2 { color:var(--moment); }
      body[data-view="settings"] .viewIntro h2 { color:var(--settings); }
      .primaryTab.researchTab.active { border-color:var(--research); background:rgba(180,154,242,.08); }
      .primaryTab.momentTab.active { border-color:var(--moment); background:rgba(231,162,76,.07); }
      .primaryTab.settingsTab.active { border-color:var(--settings); background:rgba(169,176,184,.07); }
      @media (max-width: 700px) {
        .stateRow { gap:6px; }
        #serverState { display:none; }
        #webDiagnostics { display:none; }
        .libraryTools { margin-top:10px; }
        .libraryViews { margin-bottom:8px; }
        .filters { display:block; }
        .advancedFilters { margin-top:8px; grid-template-columns:1fr 1fr; }
        .advancedFilters select, .advancedFilters input { min-width:0; width:100%; }
        .filterActions { margin-top:8px; }
        .appUpdateBanner { align-items: flex-start; top: calc(max(12px, env(safe-area-inset-top)) + 154px); }
        .appUpdateActions { flex-direction: column; }
        .transcriptOverview { grid-template-columns:1fr; }
        .transcriptBrowser { grid-template-columns:1fr; }
        .transcriptList { max-height:300px; }
        .transcriptStatus { align-items:flex-start; flex-direction:column; }
        .transcriptSegment { grid-template-columns:55px minmax(0,1fr); }
        .transcriptText { grid-column:1 / -1; }
        .researchMetrics,.settingsMetrics,.settingsFields,.capabilityGrid,.packPreview { grid-template-columns:1fr; }
        .researchRecordHead,.momentCardHead,.settingsCardHead { flex-direction:column; }
      }
      @media (min-width:1080px) {
        .primaryTab.transcriptsTab.active { box-shadow:inset 3px 0 0 var(--transcript); }
      }

      /* Alpha 8 native-shell parity: shared palette, navigation rail, page header and player. */
      :root {
        --bg:#111317; --shell:#15181d; --panel:#1c2026; --panel2:#23282f; --panel3:#2b3139; --line:#363d46;
        --text:#f5f6f8; --muted:#afb5bd; --border-strong:#4b5561; --search:#78b6d8; --favourite:#f08db7; --moment:#e8a45e;
        --research:#b49af2; --wiki:#8fa9ff; --wiki-soft:rgba(143,169,255,.10); --settings:#a9b0b8; --progress-blue:#67aee6;
      }
      body { background:var(--bg); }
      .episode,.dashboardEpisode,.skeletonCard { background:var(--panel); }
      .dashboardHero { background:linear-gradient(140deg,#282719,var(--panel) 72%); }
      .brand h1 { letter-spacing:.025em; }
      .primaryNav { background:transparent; border:0; padding:0; }
      .primaryTab { position:relative; width:100%; justify-content:flex-start; color:var(--text); font-weight:650; }
      .primaryTab .navIcon { stroke:currentColor; }
      .primaryTab.searchTab .navIcon { color:var(--search); }
      .primaryTab.favouriteTab .navIcon { color:var(--favourite); }
      .primaryTab.momentTab .navIcon { color:var(--moment); }
      .primaryTab.transcriptsTab .navIcon { color:var(--transcript); }
      .primaryTab.nowPlayingTab .navIcon { color:var(--progress-blue); }
      .primaryTab.researchTab .navIcon { color:var(--research); }
      .primaryTab.wikiTab .navIcon { color:var(--wiki); }
      .primaryTab.settingsTab .navIcon { color:var(--settings); }
      .primaryTab.active { color:var(--text); border-color:var(--accent); background:rgba(242,201,76,.035); box-shadow:none; }
      .primaryTab.transcriptsTab.active { color:var(--text); border-color:var(--transcript); background:var(--transcript-soft); box-shadow:none; }
      .primaryTab.wikiTab.active { color:var(--text); border-color:var(--wiki); background:var(--wiki-soft); box-shadow:none; }
      .navChevron { margin-left:auto; color:var(--muted); font-size:13px; }
      .navSoon { margin-left:auto; color:var(--muted); font-size:8px; letter-spacing:.08em; text-transform:uppercase; opacity:.72; }
      .navDivider { display:flex; align-items:center; gap:8px; margin:10px 8px 5px; color:#737e89; font-size:8px; font-weight:800; letter-spacing:.12em; }
      .navDivider::after { content:""; height:1px; flex:1; background:var(--line); }
      .sidebarShows { display:grid; gap:1px; margin:0 0 5px 28px; }
      .sidebarShow { width:100%; display:grid; grid-template-columns:10px minmax(0,1fr); gap:6px; align-items:center; padding:6px 8px; border:0; border-radius:7px; background:transparent; color:#cbd1d7; text-align:left; font-size:11px; font-weight:620; }
      .sidebarShow::before { content:"•"; color:var(--accent); }
      .sidebarShow:hover,.sidebarShow.active { background:var(--panel2); color:var(--text); }
      .rvMiniIdentity { min-width:0; display:grid; grid-template-columns:64px minmax(0,1fr); gap:12px; align-items:center; padding:0; border:0; background:transparent; color:var(--text); text-align:left; }
      .rvMiniIdentity .rvMiniText { min-width:0; display:flex; flex-direction:column; gap:3px; }
      .rvMiniTransport { min-width:0; display:grid; gap:6px; align-content:center; }
      .rvMiniButtons { display:flex; justify-content:center; align-items:center; gap:8px; }
      .miniSkip { width:44px; height:44px; border:1px solid var(--line); border-radius:10px; background:var(--panel2); color:var(--muted); font-size:12px; font-weight:750; }
      .rvMiniTimeline { display:grid; grid-template-columns:46px minmax(80px,1fr) 46px; gap:9px; align-items:center; color:var(--muted); font-size:9px; }
      .rvMiniTimeline span:first-child { text-align:right; }
      .rvMiniTimeline .seek { width:100%; }
      .rvMiniUtilities { display:flex; justify-content:flex-end; align-items:center; gap:7px; }
      .rvMiniUtilities button { min-width:42px; height:38px; padding:0 9px; border:0; border-radius:9px; background:transparent; color:var(--text); font-weight:700; }
      .rvMiniUtilities button:hover { background:var(--panel2); }
      .miniInfoGlyph { display:inline-grid; place-items:center; width:20px; height:20px; border:1.7px solid currentColor; border-radius:50%; font-size:12px; font-weight:800; }
      .rvMiniPlayer.idle .rvMiniIdentity { pointer-events:none; }
      .viewIntro { background:var(--bg); }
      .viewIntro h2 { font-size:25px; font-weight:760; }
      .srOnly {
        position:absolute !important;
        width:1px !important;
        height:1px !important;
        padding:0 !important;
        margin:-1px !important;
        overflow:hidden !important;
        clip:rect(0,0,0,0) !important;
        white-space:nowrap !important;
        border:0 !important;
      }
      body[data-view="wiki"] .viewIntro h2 { color:var(--wiki); }
      .chip,.wikiEntityChip { appearance:none; cursor:pointer; font:inherit; color:inherit; }
      .chip:hover,.chip:focus-visible,.wikiEntityChip:hover,.wikiEntityChip:focus-visible { border-color:var(--wiki); color:#cbd5ff; background:var(--wiki-soft); outline:none; }
      .wikiDashboard { display:grid; gap:15px; }
      .wikiHero { padding:20px; border:1px solid rgba(143,169,255,.34); border-radius:17px; background:linear-gradient(145deg,rgba(143,169,255,.12),var(--panel) 64%); box-shadow:var(--shadow); }
      .wikiHero h3 { margin:5px 0 7px; font-size:24px; }
      .wikiHero p { margin:0; max-width:760px; color:var(--muted); line-height:1.55; }
      .wikiMetrics { display:grid; grid-template-columns:repeat(4,minmax(0,1fr)); gap:8px; margin-top:16px; }
      .wikiMetric { padding:10px; border:1px solid rgba(255,255,255,.07); border-radius:10px; background:rgba(0,0,0,.18); }
      .wikiMetric strong { display:block; color:var(--wiki); font-size:19px; }
      .wikiMetric span { color:var(--muted); font-size:10px; }
      .wikiSearch { display:grid; grid-template-columns:minmax(0,1fr) auto; gap:8px; }
      .wikiSearch input { min-width:0; padding:11px 13px; border:1px solid var(--line); border-radius:10px; background:var(--panel); color:var(--text); }
      .wikiPageGrid { display:grid; grid-template-columns:repeat(auto-fill,minmax(230px,1fr)); gap:10px; }
      .wikiPageCard { width:100%; min-height:142px; padding:14px; border:1px solid var(--line); border-radius:12px; background:var(--panel); color:var(--text); text-align:left; }
      .wikiPageCard:hover { border-color:var(--wiki); background:var(--wiki-soft); }
      .wikiPageCard h3 { margin:7px 0 6px; font-size:16px; }
      .wikiPageCard p { margin:0; color:var(--muted); font-size:11px; line-height:1.45; }
      .wikiPageMeta { display:flex; justify-content:space-between; gap:7px; color:var(--muted); font-size:9px; }
      .wikiReader { display:grid; grid-template-columns:minmax(0,1fr) 230px; gap:16px; align-items:start; }
      .wikiArticle,.wikiContents { border:1px solid var(--line); border-radius:14px; background:var(--panel); }
      .wikiArticle { padding:clamp(16px,3vw,30px); }
      .wikiArticleHeader { padding-bottom:17px; border-bottom:1px solid var(--line); }
      .wikiArticleHeader h2 { margin:7px 0; color:var(--text); font-size:clamp(25px,4vw,38px); }
      .wikiArticleHeader p { color:var(--muted); line-height:1.55; }
      .wikiMarkdown { line-height:1.7; color:#e7e9ed; }
      .wikiMarkdown h2,.wikiMarkdown h3,.wikiMarkdown h4 { scroll-margin-top:24px; color:var(--text); }
      .wikiMarkdown h2 { margin-top:28px; padding-bottom:7px; border-bottom:1px solid var(--line); }
      .wikiMarkdown a,.wikiLink { color:#b9c7ff; text-decoration:none; }
      .wikiMarkdown a:hover,.wikiLink:hover { text-decoration:underline; }
      .wikiMarkdown blockquote { margin-left:0; padding-left:14px; border-left:3px solid var(--wiki); color:var(--muted); }
      .wikiContents { position:sticky; top:18px; padding:14px; }
      .wikiContents h3 { margin:0 0 9px; font-size:13px; }
      .wikiContents a { display:block; padding:5px 0; color:var(--muted); font-size:11px; text-decoration:none; }
      .wikiGallery { display:grid; grid-template-columns:repeat(auto-fit,minmax(180px,1fr)); gap:10px; margin:18px 0; }
      .wikiFigure { margin:0; overflow:hidden; border:1px solid var(--line); border-radius:11px; background:var(--panel2); }
      .wikiFigure img { display:block; width:100%; max-height:340px; object-fit:contain; background:#0d0f12; }
      .wikiFigure figcaption { padding:9px 10px; color:var(--muted); font-size:10px; line-height:1.4; }
      .wikiTimeline { display:grid; gap:9px; margin-top:18px; }
      .wikiTimelineEvent { padding:12px; border-left:3px solid var(--wiki); border-radius:0 10px 10px 0; background:var(--panel2); }
      .wikiTimelineEvent h4 { margin:2px 0 5px; }
      .wikiSources { margin-top:23px; padding-top:15px; border-top:1px solid var(--line); }
      .wikiSources li { margin-bottom:8px; color:var(--muted); font-size:11px; }
      .wikiNav { display:grid; grid-template-columns:auto auto auto auto minmax(120px,1fr) auto; gap:7px; align-items:center; margin-bottom:12px; }
      .wikiNav input { min-width:0; padding:10px 12px; border:1px solid var(--line); border-radius:9px; background:var(--panel); color:var(--text); }
      .wikiBrowseFilters { display:flex; flex-wrap:wrap; gap:10px; margin-bottom:14px; }
      .wikiBrowseFilters label { display:grid; gap:5px; min-width:170px; color:var(--muted); font-size:.78rem; font-weight:700; }
      .wikiBrowseFilters select { padding:9px 11px; border:1px solid var(--line); border-radius:9px; background:var(--panel); color:var(--text); }
      .wikiInfobox { display:grid; grid-template-columns:repeat(4,minmax(0,1fr)); gap:8px; margin:14px 0; }
      .wikiInfoCell { padding:10px; border:1px solid var(--line); border-radius:9px; background:var(--panel2); }
      .wikiInfoCell strong { display:block; color:var(--wiki); font-size:11px; }
      .wikiRelated { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:10px; margin:20px 0; }
      .wikiRelated section { padding:13px; border:1px solid var(--line); border-radius:11px; background:var(--panel2); }
      .wikiRelated h3 { margin:0 0 8px; }
      .wikiMissing { padding:12px; margin:18px 0; border:1px solid #f0b85a; border-radius:10px; background:rgba(240,184,90,.08); }
      .wikiEraRow { display:flex; flex-wrap:wrap; gap:7px; }
      .wikiTimelineExplorer { display:grid; gap:14px; }
      .wikiTimelineControls { padding:18px; border:1px solid rgba(143,169,255,.35); border-radius:14px; background:linear-gradient(145deg,var(--wiki-soft),var(--panel)); }
      .wikiTimelineControls select,.wikiTimelineControls input[type="range"] { width:100%; }
      .wikiTimelineCards { display:grid; gap:0; max-width:920px; margin:0 auto; padding:3px 1px 28px; scroll-behavior:smooth; }
      .wikiTimelineCard { position:relative; min-height:190px; margin-left:54px; padding:18px 18px 20px 24px; border:1px solid var(--line); border-left:2px solid var(--wiki); border-radius:0 13px 13px 0; background:var(--panel); }
      .wikiTimelineCard::before { content:""; position:absolute; left:-9px; top:28px; width:14px; height:14px; border:2px solid var(--background); border-radius:50%; background:var(--wiki); }
      .wikiTimelineCard .year { color:var(--wiki); font-size:27px; font-weight:800; }
      @media (max-width:760px) { .wikiMetrics,.wikiInfobox { grid-template-columns:repeat(2,1fr); } .wikiReader,.wikiRelated { grid-template-columns:1fr; } .wikiContents { position:static; order:-1; } .wikiNav { grid-template-columns:auto auto 1fr auto; } .wikiNav .wide { display:none; } .wikiNav input { grid-column:1/4; grid-row:2; } .wikiNav .searchButton { grid-column:4; grid-row:2; } .wikiArticle { padding:16px; } }
      #libraryResults { display:grid; gap:0; }
      .episode {
        position:relative;
        z-index:0;
        display:grid;
        grid-template-columns:38px 62px minmax(0,1fr) 76px auto;
        gap:8px;
        align-items:center;
        min-height:62px;
        margin:0 0 5px;
        padding:7px 9px;
        overflow:visible;
        content-visibility:visible;
        contain:none;
        background:var(--panel2);
        border:1px solid var(--line);
        border-radius:9px;
        box-shadow:none;
      }
      .episode:hover {
        background:var(--panel3);
        border-color:var(--border-strong);
        box-shadow:none;
      }
      .episode:focus-within,
      .episode:has(.libraryOverflow[open]) { z-index:26; }
      .libraryPrimaryAction,
      .libraryIconButton,
      .libraryOverflow summary {
        width:34px;
        min-width:34px;
        height:34px;
        min-height:34px;
        padding:0;
        display:grid;
        place-items:center;
        border:0;
        border-radius:8px;
        background:transparent;
        color:var(--muted);
        cursor:pointer;
      }
      .libraryPrimaryAction {
        width:36px;
        min-width:36px;
        height:36px;
        min-height:36px;
        background:var(--accent);
        color:#18140a;
      }
      .libraryPrimaryAction:hover { background:var(--accent2); }
      .libraryIconButton:hover,
      .libraryOverflow summary:hover,
      .libraryOverflow[open] summary { background:var(--panel3); color:var(--text); }
      .libraryActionGlyph { width:18px; height:18px; display:block; fill:none; stroke:currentColor; stroke-width:1.8; stroke-linecap:round; stroke-linejoin:round; }
      .libraryPrimaryAction .libraryActionGlyph { width:19px; height:19px; }
      .libraryPrimaryAction .playGlyph { fill:currentColor; stroke:none; }
      .libraryDate { min-width:0; text-align:center; color:var(--muted); font-size:9px; font-weight:700; line-height:1.15; text-transform:uppercase; }
      .libraryDate strong { display:block; color:var(--text); font-size:13px; line-height:1.2; }
      .libraryRowCopy { min-width:0; padding:2px 4px; border:0; border-radius:7px; background:transparent; color:var(--text); text-align:left; }
      .libraryRowCopy:hover { background:rgba(255,255,255,.035); }
      .libraryRowShow { color:var(--accent); font-size:10px; font-weight:720; overflow:hidden; white-space:nowrap; text-overflow:ellipsis; }
      .libraryRowTitle { margin-top:2px; font-size:13px; font-weight:700; overflow:hidden; white-space:nowrap; text-overflow:ellipsis; }
      .libraryRowMeta { margin-top:3px; color:var(--muted); font-size:9px; overflow:hidden; white-space:nowrap; text-overflow:ellipsis; }
      .libraryProgressColumn { display:grid; gap:5px; min-width:0; color:var(--muted); font-size:9px; text-align:right; }
      .libraryProgressTrack { height:3px; overflow:hidden; border-radius:999px; background:#343a42; }
      .libraryProgressTrack span { display:block; height:100%; border-radius:inherit; background:var(--progress-blue); }
      .libraryRowActions { display:flex; align-items:center; justify-content:flex-end; gap:2px; }
      .libraryFavouriteAction { color:var(--favourite); }
      .libraryFavouriteAction.favourite { background:rgba(240,141,183,.09); }
      .libraryFavouriteAction.favourite .libraryActionGlyph { fill:currentColor; }
      .libraryDownloadAction.dangerButton { background:rgba(248,113,130,.07); }
      .libraryOverflow { position:relative; }
      .libraryOverflow summary { list-style:none; }
      .libraryOverflow summary::-webkit-details-marker { display:none; }
      .libraryOverflow summary:focus-visible { outline:3px solid var(--accent2); outline-offset:3px; }
      .libraryOverflowMenu {
        position:absolute;
        top:calc(100% + 6px);
        right:0;
        z-index:30;
        width:218px;
        display:grid;
        gap:2px;
        padding:6px;
        border:1px solid var(--line);
        border-radius:10px;
        background:var(--shell);
        box-shadow:0 18px 48px rgba(0,0,0,.48);
      }
      .libraryOverflowMenu button {
        width:100%;
        min-height:38px;
        padding:8px 10px;
        border:0;
        border-radius:7px;
        background:transparent;
        color:var(--text);
        text-align:left;
        font-size:12px;
      }
      .libraryOverflowMenu button:hover { background:var(--panel2); }
      @media (max-width:700px) {
        #libraryResults { gap:0; }
        .episode { grid-template-columns:42px minmax(0,1fr) auto; gap:6px; min-height:66px; padding:8px; }
        .libraryDate,.libraryProgressColumn { display:none; }
        .libraryPrimaryAction { grid-column:1; }
        .libraryRowCopy { grid-column:2; }
        .libraryRowActions { grid-column:3; }
        .libraryRowTitle { font-size:12px; }
        .libraryRowMeta { max-width:44vw; }
      }
      @media (hover:none) {
        .libraryPrimaryAction,
        .libraryIconButton,
        .libraryOverflow summary { width:42px; min-width:42px; height:42px; min-height:42px; }
      }
      /* Dashboard parity: follow the native client's listening-first composition. */
      .nativeDashboard { display:grid; gap:20px; padding:0 0 8px; }
      .nativeDashboardTop { display:grid; grid-template-columns:minmax(0,5fr) minmax(300px,3fr); gap:16px; }
      .nativeDashboardSide { display:grid; gap:16px; align-content:start; }
      .nativeCard { min-width:0; border:1px solid var(--line); border-radius:12px; background:var(--panel); }
      .nativeCardRaised { background:var(--panel2); box-shadow:0 10px 30px rgba(0,0,0,.18); }
      .dashboardContinue { min-height:292px; padding:24px; display:grid; grid-template-rows:auto 1fr auto; gap:18px; }
      .dashboardContinueHead h3,.nativeSectionTitle { margin:0; font-size:16px; font-weight:760; }
      .dashboardContinueHead p { margin:3px 0 0; color:var(--muted); font-size:11px; }
      .dashboardContinueBody { display:grid; grid-template-columns:minmax(0,1fr) 140px; gap:24px; align-items:center; }
      .dashboardContinueShow { font-size:27px; line-height:1.08; font-weight:800; }
      .dashboardContinueDate { margin-top:8px; color:var(--muted); font-size:13px; }
      .dashboardContinueTitle { margin-top:8px; font-size:17px; font-weight:650; font-style:italic; line-height:1.25; }
      .dashboardContinueArt { width:140px; height:140px; border:1px solid var(--line); border-radius:12px; object-fit:cover; background:var(--accent-soft); }
      .dashboardContinueFoot { display:grid; gap:12px; }
      .dashboardProgressRow { display:grid; grid-template-columns:minmax(0,1fr) auto; gap:14px; align-items:center; color:var(--muted); font-size:11px; }
      .dashboardProgress { height:4px; border-radius:999px; overflow:hidden; background:#333840; }
      .dashboardProgress span { display:block; height:100%; border-radius:inherit; background:var(--accent); }
      .dashboardResume { width:max-content; padding:10px 22px; border:1px solid var(--accent); border-radius:9px; background:var(--accent); color:#111; font-weight:800; }
      .dashboardSurprise { min-height:102px; padding:20px; display:grid; grid-template-columns:minmax(0,1fr) auto; gap:18px; align-items:center; }
      .dashboardSurprise h3 { margin:0; font-size:18px; }
      .dashboardSurprise p { margin:4px 0 0; color:var(--muted); font-size:11px; }
      .dashboardSurprise button { padding:10px 20px; border:1px solid var(--accent); border-radius:9px; background:var(--accent); color:#111; font-weight:800; }
      .nativeDashboardStats { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:12px; }
      .nativeDashboardStat { min-height:82px; padding:15px; display:grid; grid-template-columns:40px minmax(0,1fr); gap:11px; align-items:center; }
      .nativeDashboardStat svg { width:30px; height:30px; fill:none; stroke-width:1.9; stroke-linecap:round; stroke-linejoin:round; }
      .nativeDashboardStat.broadcasts svg { stroke:#68b9e8; }
      .nativeDashboardStat.progressing svg { stroke:#d69b54; }
      .nativeDashboardStat.completed svg { stroke:#66c48b; }
      .nativeDashboardStat.favourites svg { stroke:#e685ad; }
      .nativeDashboardStat span { display:block; font-size:12px; font-weight:700; }
      .nativeDashboardStat strong { display:block; margin-top:1px; font-size:24px; }
      .nativeDashboardPair { display:grid; grid-template-columns:1.08fr 1fr; gap:16px; }
      .nativeDashboardPanel { min-width:0; display:grid; align-content:start; gap:10px; }
      .nativeDashboardPanelBody { min-height:250px; padding:12px; }
      .nativeDashboardRows { display:grid; gap:7px; }
      .nativeDashboardRow { min-width:0; display:grid; grid-template-columns:40px minmax(0,1fr) 104px; gap:10px; align-items:center; padding:8px; border:1px solid var(--line); border-radius:9px; background:var(--panel2); }
      .nativeDashboardRow.simple { grid-template-columns:68px minmax(0,1fr) 40px; padding:10px; }
      .nativeDashboardRow button { min-width:0; }
      .dashboardRowPlay { width:36px; height:36px; padding:0; border:0; border-radius:9px; background:var(--accent); color:#111; font-size:13px; font-weight:900; }
      .dashboardRowText { padding:0; border:0; background:transparent; color:var(--text); text-align:left; overflow:hidden; }
      .dashboardRowTitle { font-size:12px; font-weight:700; overflow:hidden; white-space:nowrap; text-overflow:ellipsis; }
      .dashboardRowMeta { margin-top:2px; color:var(--muted); font-size:10px; overflow:hidden; white-space:nowrap; text-overflow:ellipsis; }
      .dashboardRowProgress { display:grid; gap:4px; color:var(--muted); font-size:9px; text-align:right; }
      .dashboardDateTile { text-align:center; font-size:9px; font-weight:700; color:var(--muted); text-transform:uppercase; }
      .dashboardDateTile strong { display:block; color:var(--text); font-size:14px; }
      .dashboardOnThisDay { min-height:250px; padding:18px; display:grid; grid-template-rows:1fr auto; gap:12px; }
      .dashboardOnThisDayMain { display:grid; grid-template-columns:118px minmax(0,1fr); gap:18px; }
      .dashboardOnThisDayArt { width:118px; height:118px; border:1px solid var(--line); border-radius:12px; object-fit:cover; background:var(--accent-soft); }
      .dashboardOnThisDayTitle { font-size:17px; font-weight:700; line-height:1.2; }
      .dashboardOnThisDay .chips { margin-top:10px; }
      .dashboardDots { display:flex; justify-content:center; gap:7px; }
      .dashboardDot { width:8px; height:8px; min-width:8px; padding:0; border:0; border-radius:50%; background:#555b63; }
      .dashboardDot.active { background:var(--accent); transform:scale(1.22); }
      .nativeDashboardEmpty { min-height:100%; display:grid; place-items:center; padding:24px; color:var(--muted); text-align:center; }
      .rvMiniIdentity { border-radius:9px; }
      .rvMiniIdentity:hover { background:var(--panel2); }
      .rvMiniUtilities { display:grid; grid-template-rows:auto auto; gap:9px; align-content:center; }
      .rvMiniActions { display:flex; justify-content:flex-end; align-items:center; gap:7px; }
      .rvMiniActions button svg { width:20px; height:20px; display:block; }
      .rvMiniUtilities .miniFavourite { color:var(--favourite); }
      .rvMiniUtilities .miniMoment { color:#e49a54; }
      .rvMiniVolume { display:grid; grid-template-columns:24px minmax(80px,1fr); gap:10px; align-items:center; padding:0 2px; }
      .rvMiniVolume svg { width:18px; height:18px; fill:none; stroke:var(--muted); stroke-width:1.7; stroke-linecap:round; stroke-linejoin:round; }
      .rvMiniVolume input { width:100%; accent-color:var(--accent); }
      @media (max-width:900px) {
        .nativeDashboardTop,.nativeDashboardPair { grid-template-columns:1fr; }
        .nativeDashboardPair { gap:20px; }
      }
      @media (max-width:600px) {
        .nativeDashboard { gap:16px; }
        .dashboardContinue { min-height:0; padding:16px; }
        .dashboardContinueBody { grid-template-columns:minmax(0,1fr) 86px; gap:14px; }
        .dashboardContinueArt { width:86px; height:86px; }
        .dashboardContinueShow { font-size:21px; }
        .dashboardSurprise { grid-template-columns:1fr; }
        .dashboardSurprise button { width:max-content; }
        .nativeDashboardStats { grid-template-columns:1fr 1fr; gap:8px; }
        .nativeDashboardStat { grid-template-columns:30px minmax(0,1fr); gap:8px; padding:11px; }
        .nativeDashboardStat svg { width:24px; height:24px; }
        .nativeDashboardStat strong { font-size:20px; }
        .dashboardOnThisDayMain { grid-template-columns:82px minmax(0,1fr); gap:12px; }
        .dashboardOnThisDayArt { width:82px; height:82px; }
        .nativeDashboardRow { grid-template-columns:36px minmax(0,1fr) 76px; }
        .nativeDashboardRow.simple { grid-template-columns:52px minmax(0,1fr) 36px; }
      }
      @media (min-width:1080px) {
        header { inset:0 auto 110px 0; width:224px; padding:18px 16px 14px; background:var(--shell); display:flex; flex-direction:column; overflow:hidden; }
        .brand { order:1; display:block; flex:0 0 auto; padding:6px 8px 0; }
        .brandMark { display:flex; }
        .vaultLogo { width:34px; height:34px; }
        .brand h1 { margin:0; font-size:14px; }
        .api { display:block; width:max-content; margin-top:2px; padding:0; border:0; font-size:10px; }
        .primaryNav { order:2; display:block; flex:1; min-height:0; margin:24px 0 0; overflow-y:auto; scrollbar-width:none; }
        .primaryNav::-webkit-scrollbar { display:none; }
        .primaryTab { min-height:42px; margin:0 0 3px; padding:9px 10px; border:1px solid transparent; border-radius:10px; font-size:13px; }
        .primaryTab.active,.primaryTab.transcriptsTab.active { box-shadow:none; }
        .stateRow { order:3; flex:0 0 auto; width:100%; display:grid; grid-template-columns:minmax(0,1fr) auto; gap:8px; margin:10px 0 0; padding:10px; border:1px solid var(--line); border-radius:9px; background:var(--panel); }
        .stateActions { grid-column:1 / -1; width:100%; }
        .stateActions button { flex:1; }
        main { max-width:none; margin:0 0 0 224px; padding:0 0 134px; }
        .viewIntro { margin:0; padding:18px 28px 17px; border-bottom:1px solid var(--line); min-height:82px; }
        .viewIntro p { margin-top:4px; }
        .libraryTools { max-width:1180px; margin:0 auto; padding:16px 28px 0; }
        #count { max-width:1120px; margin:18px auto 10px; padding:0 10px; }
        #list { max-width:1120px; margin:0 auto; padding:0 10px; }
        .downloadTray { margin-top:14px; }
        .rvMiniPlayer { left:0; min-height:110px; height:110px; grid-template-columns:330px minmax(300px,1fr) 370px; gap:22px; padding:12px 18px 18px; background:var(--shell); border-top:1px solid var(--line); box-shadow:none; }
        .rvMiniArt { width:64px; height:64px; border-radius:11px; }
        .rvMiniTitle { order:1; font-size:13px; margin:0; }
        .rvMiniShow { order:2; font-size:10px; color:var(--muted); }
        .rvMiniPlayer .roundButton { width:46px; height:46px; }
        .appUpdateBanner { left:244px; }
        .detail { left:224px; bottom:110px; padding-bottom:0; }
      }
      @media (max-width:1079px) {
        header { padding-bottom:8px; }
        body.menuOpen { overflow:hidden; }
        body.menuOpen header { z-index:36; }
        .brand { align-items:center; gap:8px; }
        .brandMark { min-width:0; }
        .brand h1 { min-width:0; margin:0; overflow:hidden; font-size:17px; white-space:nowrap; text-overflow:ellipsis; }
        .vaultLogo { width:28px; height:28px; }
        .api { margin-left:auto; }
        .menuToggle { display:grid; flex:0 0 auto; }
        .menuScrim {
          position:fixed;
          inset:0;
          z-index:35;
          display:block;
          border:0;
          background:rgba(5,7,9,.7);
          backdrop-filter:blur(2px);
        }
        .menuScrim[hidden] { display:none; }
        .stateRow { margin-top:8px; }
        .primaryNav {
          position:fixed;
          top:calc(max(14px, env(safe-area-inset-top)) + 76px);
          bottom:auto;
          left:0;
          z-index:1;
          width:min(324px,calc(100vw - 42px));
          height:calc(100vh - (max(14px, env(safe-area-inset-top)) + 76px));
          height:calc(100dvh - (max(14px, env(safe-area-inset-top)) + 76px));
          display:block;
          margin:0;
          padding:14px 12px calc(20px + env(safe-area-inset-bottom));
          overflow-x:hidden;
          overflow-y:auto;
          border:0;
          border-right:1px solid var(--line);
          border-radius:0 14px 0 0;
          background:var(--shell);
          box-shadow:20px 0 54px rgba(0,0,0,.46);
          visibility:hidden;
          opacity:0;
          pointer-events:none;
          transform:translateX(-104%);
          transition:transform var(--motion) var(--spring),opacity var(--motion-fast) ease,visibility 0s linear var(--motion);
          scrollbar-width:none;
        }
        body.menuOpen .primaryNav {
          visibility:visible;
          opacity:1;
          pointer-events:auto;
          transform:translateX(0);
          transition-delay:0s;
        }
        .primaryNav::-webkit-scrollbar { display:none; }
        .primaryTab { width:100%; min-width:0; min-height:44px; margin:0 0 3px; padding:9px 10px; }
        .primaryTab span:not(.navSoon) { display:inline; }
        .primaryTab .navIcon { width:20px; height:20px; }
        .navLibraryGroup { display:block; }
        .sidebarShows { display:grid; }
        .navDivider { display:flex; }
        .navSoon { display:inline; }
        .libraryTools { padding:10px 0 0; }
        .rvMiniPlayer { grid-template-columns:minmax(0,1fr) auto; }
        .rvMiniIdentity { grid-template-columns:48px minmax(0,1fr); gap:10px; }
        .rvMiniTransport { display:block; }
        .rvMiniButtons .miniSkip,.rvMiniTimeline,.rvMiniUtilities { display:none; }
      }
      @media (max-width:600px) {
        .api { display:none; }
      }
    </style>
  </head>
  <body>
    <a class="skipLink" href="#mainContent">Skip to content</a>
    <div id="bootGuard" class="bootGuard">
      <div class="bootCard">
        <img class="bootLogo" src="/app-logo-512.png?token=__TOKEN__&v=__APP_VERSION__" alt="" />
        <h2 id="bootTitle">Opening Radio Vault Web</h2>
        <p id="bootMessage">
          Restoring your downloaded library and checking the Radio Vault server…
        </p>
        <div id="bootActions" class="bootActions" hidden>
          <button id="bootDownloads">Open Downloads</button
          ><button id="bootRetry" class="primary">Try again</button>
        </div>
      </div>
    </div>
    <header>
      <div class="brand">
        <div class="brandMark">
          <img class="vaultLogo" src="/app-logo-512.png?token=__TOKEN__&v=__APP_VERSION__" alt="Radio Vault" />
          <h1>THE RADIO VAULT</h1>
        </div>
        <span class="api">Broadcast archive &middot; Web</span>
        <button id="menuToggle" class="menuToggle" type="button" aria-controls="primaryNav" aria-expanded="false" aria-label="Open main menu">
          <span class="menuToggleGlyph" aria-hidden="true"><span></span><span></span><span></span></span>
        </button>
      </div>
      <div class="stateRow">
        <div id="serverState" class="state">Connected locally</div>
        <button id="syncStatus" class="syncStatusButton synced" type="button" aria-label="Synced" title="Synced"><svg viewBox="0 0 24 24"><path d="M5 12.5l4 4L19 7"/></svg></button>
        <div class="stateActions">
          <button id="webDiagnostics" class="mini" type="button">Diagnostics</button>
          <button id="cancelJob" class="mini" hidden>Cancel task</button>
        </div>
      </div>
      <nav id="primaryNav" class="primaryNav" aria-label="Radio Vault views">
        <button class="primaryTab active" data-section="dashboard" data-nav-key="dashboard"><svg class="navIcon" viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M4 12 12 5l8 7M6.5 10.5V20h11v-9.5M10 20v-6h4v6" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"/></svg><span>Dashboard</span></button>
        <button class="primaryTab searchTab" data-nav-search data-nav-key="search"><svg class="navIcon" viewBox="0 0 24 24" fill="none" aria-hidden="true"><circle cx="10.5" cy="10.5" r="6.5" stroke="currentColor" stroke-width="1.9"/><path d="m15.2 15.2 4.8 4.8" stroke="currentColor" stroke-width="1.9" stroke-linecap="round"/></svg><span>Search</span></button>
        <div class="navLibraryGroup">
          <button class="primaryTab" data-section="library" data-nav-key="library"><svg class="navIcon" viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M5 3h14v18H5zM9 8h6M9 12h6M9 16h6" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"/></svg><span>Library</span><span class="navChevron">&#8964;</span></button>
          <div id="sidebarShows" class="sidebarShows" aria-label="Library shows"></div>
        </div>
        <button class="primaryTab favouriteTab" data-nav-favourites data-nav-key="favourites"><svg class="navIcon" viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="m12 20-6.8-6.2C2 10.8 3.7 6 7.5 6c1.9 0 3.5 1.1 4.5 2.7C13 7.1 14.6 6 16.5 6c3.8 0 5.5 4.8 2.3 7.8z" stroke="currentColor" stroke-width="1.9" stroke-linejoin="round"/></svg><span>Favourites</span></button>
        <button class="primaryTab momentTab" data-section="moments" data-nav-key="moments"><svg class="navIcon" viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M6 3h12v18l-6-4.3L6 21z" stroke="currentColor" stroke-width="1.9" stroke-linejoin="round"/></svg><span>Moments</span></button>
        <button class="primaryTab researchTab" data-section="research" data-nav-key="research"><svg class="navIcon" viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M5 3h10l4 4v13H5zM15 3v4h4M8 11h8M8 15h6" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"/></svg><span>Knowledge</span></button>
        <button class="primaryTab wikiTab" data-section="wiki" data-nav-key="wiki"><svg class="navIcon" viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M4 5.5A3.5 3.5 0 0 1 7.5 2H12v18H7.5A3.5 3.5 0 0 0 4 23zM20 5.5A3.5 3.5 0 0 0 16.5 2H12v18h4.5A3.5 3.5 0 0 1 20 23z" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"/></svg><span>Explore</span></button>
        <div class="navDivider"><span>ON THIS DEVICE</span></div>
        <button class="primaryTab" data-section="downloaded" data-nav-key="downloaded"><svg class="navIcon" viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M12 3v12m0 0 4-4m-4 4-4-4M5 20h14" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"/></svg><span>Downloads</span></button>
        <button class="primaryTab" data-section="queue" data-nav-key="queue"><svg class="navIcon" viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M4 7h12M4 12h12M4 17h8M19 14v6m0 0 3-3m-3 3-3-3" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"/></svg><span>Queue</span></button>
        <button class="primaryTab" data-section="health" data-nav-key="health"><svg class="navIcon" viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M4 19V9m5 10V5m5 14v-7m5 7V3" stroke="currentColor" stroke-width="1.9" stroke-linecap="round"/></svg><span>Archive Health</span></button>
        <button class="primaryTab settingsTab" data-section="settings" data-nav-key="settings"><svg class="navIcon" viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M4 7h16M4 12h16M4 17h16M8 4v6m8-1v6m-6-1v6" stroke="currentColor" stroke-width="1.9" stroke-linecap="round"/></svg><span>Settings</span></button>
        <button class="primaryTab nowPlayingTab" data-nav-player><svg class="navIcon" viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="m8 5 10 7-10 7z" stroke="currentColor" stroke-width="1.9" stroke-linejoin="round"/></svg><span>Now Playing</span></button>
      </nav>
      <div id="libraryTools" class="libraryTools" hidden>
        <div id="libraryViewChips" class="libraryViews">
          <button class="viewChip active" data-library-view="library">
            All broadcasts
          </button>
          <button class="viewChip" data-library-view="continue">
            Continue
          </button>
          <button class="viewChip" data-library-view="favorites">
            Favourites
          </button>
          <button class="viewChip" data-library-view="onthisday">
            On this day
          </button>
        </div>
        <div class="filters">
          <div class="compactLibrarySearch">
            <input id="search" class="filterSearch" type="search" placeholder="Search broadcasts, people or topics" autocomplete="off" />
            <button id="filterToggle" class="mini filterToggle" type="button" aria-expanded="false">Filters <span id="filterCount" class="filterCount">0</span></button>
          </div>
          <div id="advancedFilters" class="advancedFilters" hidden>
            <select id="show" aria-label="Show"><option value="">All shows</option></select>
            <select id="year" aria-label="Broadcast year"><option value="">All years</option></select>
            <select id="month" aria-label="Broadcast month"><option value="">All months</option><option value="1">January</option><option value="2">February</option><option value="3">March</option><option value="4">April</option><option value="5">May</option><option value="6">June</option><option value="7">July</option><option value="8">August</option><option value="9">September</option><option value="10">October</option><option value="11">November</option><option value="12">December</option></select>
            <input id="date" type="date" aria-label="Exact broadcast date" />
            <select id="status" aria-label="Listening status"><option value="">Any listening status</option><option value="unplayed">Not started</option><option value="inprogress">In progress</option><option value="completed">Listened</option></select>
          </div>
        </div>
        <div class="filterActions">
          <span id="filterSummary">Complete canonical library</span>
          <button id="clearFilters" class="mini" type="button">Clear filters</button>
        </div>
      </div>
    </header>
    <button id="menuScrim" class="menuScrim" type="button" aria-label="Close main menu" hidden></button>
    <section id="syncSheet" class="syncSheet" hidden role="dialog" aria-labelledby="syncSheetTitle" aria-live="polite">
      <h3 id="syncSheetTitle">Synced</h3>
      <div id="syncSheetBody" class="muted">All changes are safely stored on the Radio Vault server.</div>
      <div class="syncSheetActions"><button id="syncRetryFailed" class="mini" type="button" hidden>Retry failed</button><button id="syncDiscardFailed" class="mini" type="button" hidden>Discard failed</button><button id="syncNow" class="mini" type="button">Sync now</button><button id="syncSheetClose" class="mini" type="button">Close</button></div>
    </section>
    <section id="appUpdateBanner" class="appUpdateBanner" hidden role="status" aria-live="polite">
      <div><strong>Radio Vault update ready</strong><div class="muted">Reload the interface to use the newest app shell. Downloads and pending changes are preserved.</div></div>
      <div class="appUpdateActions"><button id="appUpdateLater" type="button">Later</button><button id="appUpdateReload" class="primary" type="button">Reload</button></div>
    </section>
    <main id="mainContent" tabindex="-1">
      <div id="downloadTray" class="downloadTray" hidden>
        <div class="downloadHead">
          <strong id="downloadTitle">Downloading broadcast</strong
          ><button id="downloadCancel" class="mini">Cancel</button>
        </div>
        <div class="downloadTrack"><span id="downloadFill"></span></div>
        <div class="downloadMeta">
          <span id="downloadText">Preparing…</span
          ><span id="downloadSize"></span>
        </div>
      </div>
      <div id="viewIntro" class="viewIntro">
        <h2 id="viewTitle">Dashboard</h2>
        <p id="viewDescription">
          Continue listening and rediscover your archive.
        </p>
      </div>
      <div id="count" class="count"></div>
      <div id="list"><div class="empty">Loading your archive…</div></div>
    </main>
    <div id="miniPlayer" class="rvMiniPlayer visible idle">
      <button id="miniExpand" class="rvMiniIdentity" type="button">
        <img id="miniArt" class="rvMiniArt" alt="" />
        <span class="rvMiniText"><span id="miniTitle" class="rvMiniTitle">Choose a broadcast</span><span id="miniShow" class="rvMiniShow">Choose a broadcast from the Library</span></span>
      </button>
      <div class="rvMiniTransport">
        <div class="rvMiniButtons"><button id="miniBack" class="miniSkip" type="button">-15</button><button id="miniPlay" class="roundButton" aria-label="Play"></button><button id="miniForward" class="miniSkip" type="button">+30</button></div>
        <div class="rvMiniTimeline"><span id="miniElapsed">0:00</span><input id="miniSeek" class="seek" type="range" min="0" max="1" value="0" step="1" aria-label="Playback position"/><span id="miniDuration">0:00</span></div>
      </div>
      <div class="rvMiniUtilities">
        <div class="rvMiniActions">
          <button id="miniFavourite" class="miniFavourite" type="button" aria-label="Favourite"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="m12 20-6.8-6.2C2 10.8 3.7 6 7.5 6c1.9 0 3.5 1.1 4.5 2.7C13 7.1 14.6 6 16.5 6c3.8 0 5.5 4.8 2.3 7.8z" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linejoin="round"/></svg></button>
          <button id="miniMoment" class="miniMoment" type="button" aria-label="Save a Moment"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 3h12v18l-6-4.3L6 21z" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linejoin="round"/></svg></button>
          <button id="miniInfo" type="button" aria-label="Broadcast information"><span class="miniInfoGlyph">i</span></button>
          <button id="miniSpeed" type="button" aria-label="Change playback speed">1x</button>
          <button id="miniMore" type="button" aria-label="More playback actions">&bull;&bull;&bull;</button>
        </div>
        <label class="rvMiniVolume"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 9h3.5L11 5.5v13L6.5 15H3zM14 9c1.8 1.5 1.8 4.5 0 6m2.5-8.5c3.5 3 3.5 8 0 11"/></svg><input id="miniVolume" type="range" min="0" max="1" value="1" step="0.05" aria-label="Volume"/></label>
      </div>
    </div>
    <div id="fullPlayer" class="rvFullPlayer" role="dialog" aria-modal="true" aria-label="Now Playing" tabindex="-1">
      <div class="playerTop">
        <button id="playerClose">↓ Back</button
        ><strong id="playerContextTitle">Now Playing</strong
        ><button id="playerInfo">Info</button>
      </div>
      <img id="playerArt" class="playerArtwork" alt="" />
      <div class="playerIdentity">
        <div id="playerShow" class="show">Radio Vault</div>
        <h2 id="playerTitle">Choose a broadcast</h2>
        <div id="playerDeviceState" class="state">Ready</div>
      </div>
      <div class="seekWrap">
        <input
          id="playerSeek"
          class="seek"
          type="range"
          min="0"
          max="1"
          value="0"
          step="1"
        />
        <div class="timeRow">
          <span id="playerElapsed">0:00</span
          ><span id="playerRemaining">-0:00</span>
        </div>
      </div>
      <div class="transport">
        <button id="playerBack">−30</button
        ><button id="playerPlay" class="mainPlay" aria-label="Play"></button
        ><button id="playerForward">+30</button>
      </div>
      <div class="playerOptions">
        <select id="playerSpeed" aria-label="Playback speed">
          <option value="0.75">0.75×</option>
          <option value="1" selected>1×</option>
          <option value="1.1">1.1×</option>
          <option value="1.2">1.2×</option>
          <option value="1.25">1.25×</option>
          <option value="1.3">1.3×</option>
          <option value="1.5">1.5×</option>
          <option value="1.75">1.75×</option>
          <option value="2">2×</option></select
        ><button id="playerFavourite">♡ Favourite</button
        ><button id="playerListened">Mark listened</button
        ><button id="playerDownload">Download</button
        ><button id="playerNext">Next in queue</button>
      </div>
      <div id="playerNotice" class="playerNotice"></div>
    </div>
    <audio id="audio" preload="metadata" playsinline></audio>
    <div id="toast" class="toast" role="status" aria-live="polite" aria-atomic="true"></div>
    <section id="transferDiagnostic" class="transferDiagnostic" aria-modal="true" role="dialog" aria-label="Phone playback diagnostic" tabindex="-1">
      <div class="transferDiagnosticHeader"><strong>Phone playback diagnostic</strong><button id="transferDiagnosticClose">Close</button></div>
      <pre id="transferDiagnosticBody" class="transferDiagnosticBody"></pre>
      <div class="transferDiagnosticActions"><button id="transferDiagnosticCopy">Copy report</button><button id="transferDiagnosticRetry">Retry playback</button></div>
    </section>
    <section id="webDiagnostic" class="transferDiagnostic" aria-modal="true" role="dialog" aria-label="Radio Vault Web diagnostics" tabindex="-1">
      <div class="transferDiagnosticHeader"><strong>Radio Vault Web diagnostics</strong><button id="webDiagnosticClose">Close</button></div>
      <pre id="webDiagnosticBody" class="transferDiagnosticBody"></pre>
      <div class="transferDiagnosticActions"><button id="webDiagnosticCopy">Copy report</button><button id="webDiagnosticExport">Export report</button><button id="webDiagnosticClear">Clear history</button><button id="webDiagnosticReconnect">Check connection</button><button id="webDiagnosticRepair">Repair app shell</button></div>
    </section>
    <div id="detail" class="detail" role="dialog" aria-modal="true" aria-labelledby="detailHeading" tabindex="-1">
      <div class="detailHeader">
        <button id="detailBack" class="back">← Back</button
        ><strong id="detailHeading">Broadcast Info</strong>
      </div>
      <div id="detailBody" class="detailBody">
        <div class="empty">Loading broadcast…</div>
      </div>
    </div>
    <script>

      const token = "__TOKEN__",
        api = "/api/v1",
        shellCacheName = "radio-vault-anywhere-shell-v67";
      const $ = (id) => document.getElementById(id),
        mainContent = $("mainContent"),
        list = $("list"),
        count = $("count"),
        viewTitle = $("viewTitle"),
        viewDescription = $("viewDescription"),
        libraryTools = $("libraryTools"),
        libraryViewChips = $("libraryViewChips"),
        primaryNav = $("primaryNav"),
        menuToggle = $("menuToggle"),
        menuScrim = $("menuScrim"),
        search = $("search"),
        show = $("show"),
        year = $("year"),
        month = $("month"),
        exactDate = $("date"),
        statusFilter = $("status"),
        filterSummary = $("filterSummary"),
        audio = $("audio"),
        serverState = $("serverState"),
        syncStatus = $("syncStatus"),
        syncSheet = $("syncSheet"),
        syncSheetTitle = $("syncSheetTitle"),
        syncSheetBody = $("syncSheetBody"),
        syncRetryFailed = $("syncRetryFailed"),
        syncDiscardFailed = $("syncDiscardFailed"),
        syncNow = $("syncNow"),
        syncSheetClose = $("syncSheetClose"),
        filterToggle = $("filterToggle"),
        filterCount = $("filterCount"),
        advancedFilters = $("advancedFilters"),
        cancelJob = $("cancelJob"),
        detail = $("detail"),
        detailBody = $("detailBody"),
        miniPlayer = $("miniPlayer"),
        miniArt = $("miniArt"),
        miniShow = $("miniShow"),
        miniTitle = $("miniTitle"),
        miniPlay = $("miniPlay"),
        miniSeek = $("miniSeek"),
        miniElapsed = $("miniElapsed"),
        miniDuration = $("miniDuration"),
        miniSpeed = $("miniSpeed"),
        miniFavourite = $("miniFavourite"),
        miniMoment = $("miniMoment"),
        miniVolume = $("miniVolume"),
        fullPlayer = $("fullPlayer"),
        playerArt = $("playerArt"),
        playerContextTitle = $("playerContextTitle"),
        playerShow = $("playerShow"),
        playerTitle = $("playerTitle"),
        playerDeviceState = $("playerDeviceState"),
        playerSeek = $("playerSeek"),
        playerElapsed = $("playerElapsed"),
        playerRemaining = $("playerRemaining"),
        playerPlay = $("playerPlay"),
        playerSpeed = $("playerSpeed"),
        playerNotice = $("playerNotice"),
        playerDownload = $("playerDownload"),
        downloadTray = $("downloadTray"),
        downloadTitle = $("downloadTitle"),
        downloadFill = $("downloadFill"),
        downloadText = $("downloadText"),
        downloadSize = $("downloadSize"),
        downloadCancel = $("downloadCancel"),
        toast = $("toast"),
        transferDiagnostic = $("transferDiagnostic"),
        transferDiagnosticBody = $("transferDiagnosticBody"),
        transferDiagnosticClose = $("transferDiagnosticClose"),
        transferDiagnosticCopy = $("transferDiagnosticCopy"),
        transferDiagnosticRetry = $("transferDiagnosticRetry"),
        webDiagnostic = $("webDiagnostic"),
        webDiagnosticBody = $("webDiagnosticBody"),
        webDiagnosticClose = $("webDiagnosticClose"),
        webDiagnosticCopy = $("webDiagnosticCopy"),
        webDiagnosticExport = $("webDiagnosticExport"),
        webDiagnosticClear = $("webDiagnosticClear"),
        webDiagnosticReconnect = $("webDiagnosticReconnect"),
        webDiagnosticRepair = $("webDiagnosticRepair"),
        appUpdateBanner = $("appUpdateBanner"),
        appUpdateLater = $("appUpdateLater"),
        appUpdateReload = $("appUpdateReload");
      const mobileMenuMedia = window.matchMedia("(max-width: 1079px)");
      // The native app keeps filters in the page workspace, not inside its navigation rail.
      $("viewIntro").after(libraryTools);
      for (const [selector, label] of [
        ['[data-nav-key="dashboard"]', "Dashboard"], ['[data-nav-search]', "Search"],
        ['[data-nav-key="library"]', "Library"], ['[data-nav-favourites]', "Favourites"],
        ['[data-nav-key="moments"]', "Moments"],
        ['[data-nav-player]', "Now Playing"], ['[data-nav-key="research"]', "Research"], ['[data-nav-key="wiki"]', "Wiki"],
        ['[data-nav-key="downloaded"]', "Downloads"], ['[data-nav-key="queue"]', "Queue"],
        ['[data-nav-key="health"]', "Archive Health"], ['[data-nav-key="settings"]', "Settings"],
      ]) document.querySelector(selector)?.setAttribute("aria-label", label);
      let timer,
        menuReturnFocus = null,
        view = "dashboard",
        navMode = "dashboard",
        libraryView = "library",
        archiveBroadcastCount = 0,
        archiveShowCount = 0,
        archiveFavouriteCount = 0,
        archiveContinueCount = 0,
        archiveCompletedCount = 0,
        dashboardSnapshot = null,
        dashboardOnThisDayIndex = 0,
        activeViewLoad = null,
        viewLoadGeneration = 0,
        resumeAt = 0,
        resumeEpisodeId = 0,
        resumeAttempts = 0,
        lastSequence = 0,
        currentDetailId = null,
        currentDetails = null,
        activeJobId = null,
        transcriptSection = "library",
        transcriptQuery = "",
        transcriptSummaries = [],
        transcriptionJobs = [],
        transcriptionBatches = [],
        transcriptionStatus = null,
        transcriptionSettings = null,
        researchSnapshot = null,
        researchSection = "overview",
        researchQuery = "",
        researchUndated = [],
        researchCoverage = null,
        researchCoverageShow = "",
        researchPackPreview = null,
        researchImportJob = null,
        wikiOverview = null,
        wikiPages = [],
        wikiCurrentPage = null,
        wikiImages = [],
        wikiQuery = "",
        wikiPageType = "",
        wikiPageStatus = "",
        wikiNavigation = null,
        wikiHighlights = null,
        wikiTimelineShows = [],
        wikiTimelinePage = null,
        wikiTimelineYear = 0,
        wikiTopicCleanup = null,
        wikiHistory = [],
        wikiHistoryIndex = -1,
        settingsSnapshot = null,
        federationSnapshot = null,
        paritySnapshot = null,
        selectedTranscript = null,
        selectedTranscriptSummary = null,
        selectedBatchId = "",
        selectedBatchItems = [],
        desktopState = {
          episodeId: null,
          isPlaying: false,
          positionMs: 0,
          durationMs: 0,
          revision: 0,
          speed: 1,
        },
        desktopStateReceivedAt = 0,
        webState = {
          episodeId: null,
          isPlaying: false,
          controllerClientId: "",
          revision: 0,
        },
        webStateReceivedAt = 0,
        sessionState = {
          player: null,
          ownerDevice: "None",
          ownerClientId: "",
          generation: 0,
          devices: [],
        },
        isIosWebKit = /iPhone|iPad|iPod/i.test(navigator.userAgent),
        playbackDeviceName = /iPhone/i.test(navigator.userAgent)
          ? "iPhone"
          : /iPad/i.test(navigator.userAgent)
            ? "iPad"
            : /Android/i.test(navigator.userAgent)
              ? "Android phone"
              : "Web player",
        playbackDeviceKind = /iPhone|iPad|Android/i.test(navigator.userAgent)
          ? "Phone"
          : "Browser",
        playerStateEpoch = 0,
        playerStateRequestId = 0,
        lastAppliedPlayerStateRequestId = 0,
        localEpisode = null,
        localDetails = null,
        localManifest = null,
        localManifestPartIndex = -1,
        localManifestRecord = null,
        lastWebHeartbeat = 0,
        lastLocalSave = 0,
        lastDurableProgressSave = 0,
        seeking = false,
        toastTimer,
        localTakeoverPending = false,
        suppressNextPauseSync = false,
        lastOwnershipNoticeAt = 0,
        serverReachable = true,
        serverInfo = null,
        lastReachabilityCheck = 0,
        reachabilityProbe = null,
        activeDownload = null,
        currentAudioObjectUrl = "",
        currentAudioSource = "stream",
        audioSourceReady = false,
        currentAudioEpisodeId = 0,
        currentAudioLogicalBaseMs = 0,
        currentAudioIsPositioned = false,
        gesturePrimedEpisodeId = 0,
        gesturePrimedPositionMs = 0,
        playErrorShown = false,
        downloadSort = "newest",
        downloadStatusFilter = "all",
        artworkRepairPromise = null,
        downloadAuditPromise = null,
        downloadRepairCount = 0,
        transferTrace = [],
        transferTraceStartedAt = 0,
        transferTraceEpisodeId = 0,
        transferTracePositionMs = 0,
        phoneTransferInProgress = false,
        activePhoneTransfer = null,
        phoneTransferSequence = 0,
        sourceStopAcknowledgementInFlight = false,
        playerPollInFlight = false,
        changePollInFlight = false,
        dormantPreparationPromise = null,
        dormantPreparationEpisodeId = 0,
        dormantDecoderReadyEpisodeId = 0,
        dormantDecoderReadyAt = 0,
        dormantDecoderReadyPositionMs = 0,
        canonicalPartChangeInProgress = false,
        bootstrapState = null,
        bootstrapLoadedAt = 0,
        bootstrapDashboardPending = false,
        bootstrapQueuePending = false,
        reconnecting = false,
        lastConnectedView = "dashboard",
        lastConnectedLibraryView = "library",
        reconnectRefreshPromise = null,
        syncInProgress = false,
        syncRetryTimer = null,
        pendingJournalCount = 0,
        blockedSyncCount = 0,
        libraryPageSize = 80,
        libraryLoadedCount = 0,
        libraryTotalCount = 0,
        libraryPageKey = "",
        lastOverlayFocus = null,
        serviceWorkerControllerChanged = false;
      const downloadedRecords = new Map(),
        downloadedArtworkUrls = new Map();
      const clientId = (() => {
        let id = localStorage.getItem("radioVaultClientId");
        if (!id) {
          id = (
            crypto.randomUUID
              ? crypto.randomUUID()
              : "rv-" + Date.now() + "-" + Math.random().toString(36).slice(2)
          ).replace(/[^a-zA-Z0-9_-]/g, "");
          localStorage.setItem("radioVaultClientId", id);
        }
        return id;
      })();
      const rvIcons = {
        play: `<svg class="rvIcon" viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M8 5l11 7-11 7z"/></svg>`,
        pause: `<svg class="rvIcon" viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M7 5h4v14H7zM14 5h4v14h-4z"/></svg>`,
        transferPhone: `<svg class="rvIcon outline" viewBox="0 0 24 24" aria-hidden="true"><path d="M10 3h7a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2h-7a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zM11 18h5M3 10.5h10M9 6.5l4 4-4 4"/></svg>`,
        spinner: `<svg class="rvIcon outline rvSpinner" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3a9 9 0 1 1-8.3 5.5"/></svg>`,
        devicePhone: `<svg class="rvIcon outline deviceStateIcon" viewBox="0 0 24 24" aria-hidden="true"><path d="M8 3h8a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zM10 18h4"/></svg>`,
        deviceDesktop: `<svg class="rvIcon outline deviceStateIcon" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 4h17v13H4zM9 21h7M12.5 17v4"/></svg>`,
      };
      function setButtonIcon(button, icon, label) {
        button.innerHTML = rvIcons[icon] || "";
        button.setAttribute("aria-label", label);
        button.title = label;
      }
      const esc = (s) =>
        String(s ?? "").replace(
          /[&<>"']/g,
          (c) =>
            ({
              "&": "&amp;",
              "<": "&lt;",
              ">": "&gt;",
              '"': "&quot;",
              "'": "&#39;",
            })[c],
        );
      function loadingSkeleton(rows = 4) {
        return `<div class="skeletonList" aria-label="Loading">${Array.from({ length: rows }, () => `<div class="skeletonCard"><div class="skeletonSquare"></div><div class="skeletonLine short"></div><div class="skeletonLine long"></div><div class="skeletonLine"></div><div class="skeletonPill"></div></div>`).join("")}</div>`;
      }
      const auth = (u) =>
        u +
        (u.includes("?") ? "&" : "?") +
        "token=" +
        encodeURIComponent(token);
      const fmtMs = (ms) => {
        const n = Math.max(0, Math.round((ms || 0) / 1000)),
          h = Math.floor(n / 3600),
          m = Math.floor((n % 3600) / 60),
          s = n % 60;
        return h
          ? `${h}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}`
          : `${m}:${String(s).padStart(2, "0")}`;
      };
      const fmtBytes = (n) => {
        n = Number(n || 0);
        if (n < 1024) return n + " B";
        if (n < 1048576) return (n / 1024).toFixed(1) + " KB";
        if (n < 1073741824) return (n / 1048576).toFixed(1) + " MB";
        return (n / 1073741824).toFixed(2) + " GB";
      };
      const bootGuard = $("bootGuard"),
        bootTitle = $("bootTitle"),
        bootMessage = $("bootMessage"),
        bootActions = $("bootActions");
      function setBootStatus(message) {
        if (bootMessage) bootMessage.textContent = message;
      }
      function finishBoot() {
        if (bootGuard) bootGuard.hidden = true;
      }
      function showBootFailure(error) {
        console.error("Radio Vault startup failed.", error);
        if (!bootGuard) return;
        bootGuard.hidden = false;
        bootTitle.textContent = "Radio Vault could not finish opening";
        bootMessage.textContent =
          "Your downloads are still stored on this phone. You can retry the interface or open the Offline Library directly.";
        bootActions.hidden = false;
      }
      window.addEventListener("error", (event) => {
        if (!document.body.dataset.booted)
          showBootFailure(
            event.error || new Error(event.message || "Startup error"),
          );
      });
      window.addEventListener("unhandledrejection", (event) => {
        if (!document.body.dataset.booted) {
          event.preventDefault();
          showBootFailure(event.reason || new Error("Startup task failed"));
        }
      });
      function notify(message, error = false) {
        clearTimeout(toastTimer);
        toast.textContent = message;
        toast.className = "toast show" + (error ? " error" : "");
        toastTimer = setTimeout(() => (toast.className = "toast"), 3200);
      }
      function rememberOverlayFocus() {
        const active = document.activeElement;
        if (active instanceof HTMLElement && active !== document.body) lastOverlayFocus = active;
      }
      function focusOverlay(element, preferred = null) {
        rememberOverlayFocus();
        requestAnimationFrame(() => (preferred || element)?.focus?.());
      }
      function restoreOverlayFocus() {
        const target = lastOverlayFocus;
        lastOverlayFocus = null;
        requestAnimationFrame(() => target?.isConnected && target.focus?.());
      }
      function setMenuOpen(open, restoreFocus = true) {
        const mobile = mobileMenuMedia.matches,
          wasOpen = document.body.classList.contains("menuOpen");
        open = Boolean(open && mobile);
        if (open && !wasOpen)
          menuReturnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : menuToggle;
        document.body.classList.toggle("menuOpen", open);
        menuToggle.setAttribute("aria-expanded", open ? "true" : "false");
        menuToggle.setAttribute("aria-label", open ? "Close main menu" : "Open main menu");
        menuScrim.hidden = !open;
        primaryNav.inert = mobile && !open;
        mainContent.inert = open;
        miniPlayer.inert = open;
        if (mobile) primaryNav.setAttribute("aria-hidden", open ? "false" : "true");
        else primaryNav.removeAttribute("aria-hidden");
        if (open) {
          requestAnimationFrame(() =>
            primaryNav.querySelector(".primaryTab.active:not(:disabled), .primaryTab:not(:disabled)")?.focus(),
          );
        } else if (wasOpen) {
          const target = menuReturnFocus;
          menuReturnFocus = null;
          if (restoreFocus)
            requestAnimationFrame(() => target?.isConnected && target.focus?.());
        }
      }
      function handleMenuViewportChange() {
        setMenuOpen(false, false);
      }
      if (mobileMenuMedia.addEventListener)
        mobileMenuMedia.addEventListener("change", handleMenuViewportChange);
      else mobileMenuMedia.addListener(handleMenuViewportChange);
      setMenuOpen(false, false);
      function showAppUpdateBanner() {
        if (!appUpdateBanner) return;
        appUpdateBanner.hidden = false;
        recordDiagnostic("app-shell", "A newer Radio Vault app shell is ready");
      }
      async function reloadWithCurrentShell() {
        appUpdateReload.disabled = true;
        try {
          const registrations = "serviceWorker" in navigator ? await navigator.serviceWorker.getRegistrations() : [];
          for (const registration of registrations) {
            registration.waiting?.postMessage({ type: "SKIP_WAITING" });
            await registration.update().catch(() => null);
          }
          location.reload();
        } catch {
          location.reload();
        }
      }
      async function repairAppShell() {
        if (!confirm("Repair the Radio Vault app shell? Downloaded audio, artwork, listening progress and pending sync changes will be preserved.")) return;
        webDiagnosticRepair.disabled = true;
        try {
          if ("caches" in window) {
            const keys = await caches.keys();
            await Promise.all(keys.filter((key) => key.startsWith("radio-vault-anywhere-shell-")).map((key) => caches.delete(key)));
          }
          if ("serviceWorker" in navigator) {
            const registrations = await navigator.serviceWorker.getRegistrations();
            await Promise.all(registrations.map((registration) => registration.unregister()));
          }
          recordDiagnostic("app-shell", "Application shell reset without deleting device downloads");
          location.reload();
        } catch (error) {
          webDiagnosticRepair.disabled = false;
          recordDiagnostic("app-shell", "Application shell reset failed", { message: String(error?.message || error) });
          notify("The app shell could not be repaired.", true);
        }
      }
      const diagnosticStorageKey = "radioVaultAnywhereDiagnosticsV1",
        navigationStorageKey = "radioVaultAnywhereNavigationV1";
      let diagnosticEvents = (() => {
        try {
          const value = JSON.parse(localStorage.getItem(diagnosticStorageKey) || "[]");
          return Array.isArray(value) ? value.slice(-80) : [];
        } catch {
          return [];
        }
      })();
      function safeDiagnosticDetails(details) {
        if (!details || typeof details !== "object") return details || null;
        const result = {};
        for (const [key, value] of Object.entries(details)) {
          if (/token|url|title|summary|people|topic/i.test(key)) continue;
          if (typeof value === "string") result[key] = value.slice(0, 160);
          else if (typeof value === "number" || typeof value === "boolean" || value == null) result[key] = value;
        }
        return result;
      }
      function recordDiagnostic(kind, message, details = null) {
        const entry = {
          at: new Date().toISOString(),
          kind: String(kind || "event").slice(0, 40),
          message: String(message || "").slice(0, 240),
          details: safeDiagnosticDetails(details),
        };
        diagnosticEvents.push(entry);
        diagnosticEvents = diagnosticEvents.slice(-80);
        try { localStorage.setItem(diagnosticStorageKey, JSON.stringify(diagnosticEvents)); } catch {}
        console.info("[Radio Vault Web]", entry);
      }
      function diagnosticReport() {
        const server = serverInfo || {};
        const lines = [
          "Radio Vault Web diagnostics",
          "Version: __APP_VERSION__",
          `Generated: ${new Date().toISOString()}`,
          `Browser online: ${navigator.onLine}`,
          `Server reachable: ${serverReachable}`,
          `Server: ${String(server.displayName || "Unknown")}`,
          `Server version: ${String(server.appVersion || "Unknown")}`,
          `API: ${String(server.apiVersion || "v1")}`,
          `Schema: ${Number(server.databaseSchemaVersion || 0)}`,
          `View: ${view} / ${libraryView}`,
          `Downloads stored: ${downloadedRecords.size}`,
          `Downloads needing repair: ${downloadRepairCount}`,
          `Pending sync changes: ${pendingJournalCount}`,
          `Blocked sync changes: ${blockedSyncCount}`,
          `Library page: ${libraryLoadedCount} of ${libraryTotalCount}`,
          `Playback source: ${currentAudioSource}`,
          `Shared owner: ${ownerDevice()}`,
          `Service worker: ${navigator.serviceWorker?.controller ? "controlling" : "not controlling"}`,
          `Shell cache: ${shellCacheName}`,
          `Viewport: ${window.innerWidth} × ${window.innerHeight}`,
          "",
          "Recent privacy-safe events:",
        ];
        for (const entry of diagnosticEvents.slice(-40)) {
          lines.push(`${entry.at} [${entry.kind}] ${entry.message}`);
          if (entry.details) lines.push(JSON.stringify(entry.details));
        }
        return lines.join("\n");
      }
      function structuredDiagnosticReport() {
        const server = serverInfo || {};
        const navigation = {
          view, libraryView,
          activeFilters: [show.value, year.value, month.value, exactDate.value, statusFilter.value, search.value].filter(Boolean).length,
        };
        const timings = performance.getEntriesByType("navigation").slice(-1).map((entry) => ({
          type: entry.type,
          domContentLoadedMs: Math.round(entry.domContentLoadedEventEnd),
          loadMs: Math.round(entry.loadEventEnd),
          transferBytes: Number(entry.transferSize || 0),
        }));
        return {
          format: "radio-vault-anywhere-diagnostic",
          formatVersion: 2,
          generatedUtc: new Date().toISOString(),
          appVersion: "__APP_VERSION__",
          connectivity: { browserOnline: navigator.onLine, serverReachable, reconnecting },
          server: {
            displayName: String(server.displayName || "Unknown"),
            appVersion: String(server.appVersion || "Unknown"),
            apiVersion: String(server.apiVersion || "v1"),
            databaseSchemaVersion: Number(server.databaseSchemaVersion || 0),
            capabilityGeneration: Number(server.capabilityGeneration || 0),
          },
          client: {
            standalone: Boolean(window.matchMedia?.("(display-mode: standalone)")?.matches || navigator.standalone),
            language: String(navigator.language || ""),
            reducedMotion: Boolean(window.matchMedia?.("(prefers-reduced-motion: reduce)")?.matches),
            serviceWorkerControlled: Boolean(navigator.serviceWorker?.controller),
            shellCacheName,
            viewport: { width: window.innerWidth, height: window.innerHeight, pixelRatio: Number(window.devicePixelRatio || 1) },
            navigation,
            downloadsStored: downloadedRecords.size,
            downloadsNeedingRepair: Number(downloadRepairCount || 0),
            pendingMutations: Number(pendingJournalCount || 0),
            blockedMutations: Number(blockedSyncCount || 0),
            libraryPage: { loaded: Number(libraryLoadedCount || 0), total: Number(libraryTotalCount || 0), pageSize: Number(libraryPageSize || 0) },
            playbackSource: String(currentAudioSource || "none"),
            sharedOwner: String(ownerDevice() || "none"),
          },
          performance: timings,
          events: diagnosticEvents.slice(-80),
        };
      }
      function exportDiagnosticReport() {
        const payload = JSON.stringify(structuredDiagnosticReport(), null, 2);
        const blob = new Blob([payload], { type: "application/json" });
        const href = URL.createObjectURL(blob);
        const anchor = document.createElement("a");
        anchor.href = href;
        anchor.download = `RadioVault-Web-Diagnostics-${new Date().toISOString().replace(/[:.]/g, "-")}.json`;
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
        setTimeout(() => URL.revokeObjectURL(href), 1000);
        recordDiagnostic("diagnostics", "Privacy-safe diagnostic report exported");
        notify("Radio Vault Web diagnostic report exported");
      }
      function openWebDiagnostics() {
        webDiagnosticBody.textContent = diagnosticReport();
        webDiagnostic.classList.add("open");
        focusOverlay(webDiagnostic, webDiagnosticClose);
      }
      function saveNavigationState() {
        const state = {
          view,
          libraryView,
          show: show.value,
          year: year.value,
          month: month.value,
          date: exactDate.value,
          status: statusFilter.value,
          search: search.value,
        };
        try { localStorage.setItem(navigationStorageKey, JSON.stringify(state)); } catch {}
      }
      function restoreNavigationState() {
        try {
          const state = JSON.parse(localStorage.getItem(navigationStorageKey) || "null");
          if (!state || typeof state !== "object") return;
          if (["dashboard", "library", "moments", "transcripts", "research", "downloaded", "queue", "health", "settings"].includes(state.view)) view = state.view;
          if (["library", "continue", "favorites", "onthisday"].includes(state.libraryView)) libraryView = state.libraryView;
          search.value = String(state.search || "").slice(0, 200);
          year.dataset.restoreValue = String(state.year || "");
          month.value = String(state.month || "");
          exactDate.value = String(state.date || "");
          statusFilter.value = String(state.status || "");
          show.dataset.restoreValue = String(state.show || "");
          lastConnectedView = view;
          lastConnectedLibraryView = libraryView;
        } catch {}
      }
      function transferSnapshot(extra = {}) {
        const mediaError = audio.error;
        return {
          owner: ownerDevice(),
          sessionGeneration: Number(sessionState?.generation || 0),
          sessionEpisodeId: Number(sessionState?.player?.episodeId || 0),
          sessionPlaying: !!sessionState?.player?.isPlaying,
          sessionPositionMs: Math.round(Number(projectedSessionState()?.positionMs || 0)),
          webController: String(webState?.controllerClientId || ""),
          localClient: clientId,
          localEpisodeId: Number(localEpisode?.id || 0),
          audioPaused: !!audio.paused,
          audioEnded: !!audio.ended,
          audioMuted: !!audio.muted,
          audioReadyState: Number(audio.readyState || 0),
          audioNetworkState: Number(audio.networkState || 0),
          audioCurrentTime: Number.isFinite(audio.currentTime) ? Number(audio.currentTime.toFixed(3)) : null,
          audioDuration: Number.isFinite(audio.duration) ? Number(audio.duration.toFixed(3)) : null,
          audioEpisodeId: Number(currentAudioEpisodeId || 0),
          audioLogicalBaseMs: Math.round(Number(currentAudioLogicalBaseMs || 0)),
          audioLogicalPositionMs: Math.round(currentAudioLogicalPositionMs()),
          audioPositioned: !!currentAudioIsPositioned,
          audioSource: currentAudioSource,
          mediaErrorCode: mediaError?.code || 0,
          mediaErrorMessage: mediaError?.message || "",
          online: navigator.onLine,
          visibility: document.visibilityState,
          ...extra,
        };
      }
      function beginTransferTrace(id, positionMs) {
        transferTraceStartedAt = performance.now();
        transferTraceEpisodeId = Number(id || 0);
        transferTracePositionMs = Math.round(Number(positionMs || 0));
        transferTrace = [];
        traceTransfer("gesture", "Transfer gesture received", transferSnapshot({ requestedPositionMs: transferTracePositionMs }));
      }
      function traceTransfer(stage, message, details = null) {
        if (!transferTraceStartedAt) return;
        const elapsedMs = Math.round(performance.now() - transferTraceStartedAt);
        const entry = { elapsedMs, stage, message, details };
        transferTrace.push(entry);
        console.info("[Radio Vault transfer]", entry);
      }
      function transferReport(error = null) {
        const lines = [
          "Radio Vault phone playback diagnostic",
          "Version: __APP_VERSION__",
          `Timestamp: ${new Date().toISOString()}`,
          `User agent: ${navigator.userAgent}`,
          `Episode: ${transferTraceEpisodeId}`,
          `Requested position: ${transferTracePositionMs} ms`,
          error ? `Final error: ${error.name || "Error"}: ${error.message || String(error)}` : "Final error: none",
          "",
        ];
        for (const entry of transferTrace) {
          lines.push(`+${entry.elapsedMs}ms [${entry.stage}] ${entry.message}`);
          if (entry.details) lines.push(JSON.stringify(entry.details, null, 2));
        }
        return lines.join("\n");
      }
      function showTransferDiagnostic(error) {
        traceTransfer("failure", "Transfer failed", transferSnapshot({ errorName: error?.name || "Error", errorMessage: error?.message || String(error || "Unknown error") }));
        transferDiagnosticBody.textContent = transferReport(error);
        transferDiagnostic.classList.add("open");
        focusOverlay(transferDiagnostic, transferDiagnosticClose);
      }
      for (const eventName of ["loadstart", "loadedmetadata", "loadeddata", "canplay", "canplaythrough", "play", "playing", "pause", "waiting", "stalled", "suspend", "seeking", "seeked", "ended", "emptied", "abort", "error"]) {
        audio.addEventListener(eventName, () => traceTransfer("media-event", eventName, transferSnapshot()));
      }
      transferDiagnosticClose.onclick = () => { transferDiagnostic.classList.remove("open"); restoreOverlayFocus(); };
      transferDiagnosticCopy.onclick = async () => {
        const report = transferDiagnosticBody.textContent || "";
        try {
          await navigator.clipboard.writeText(report);
          notify("Diagnostic report copied");
        } catch {
          transferDiagnosticBody.focus();
          window.getSelection()?.selectAllChildren(transferDiagnosticBody);
          notify("Select and copy the diagnostic report");
        }
      };
      transferDiagnosticRetry.onclick = async () => {
        transferDiagnostic.classList.remove("open");
        if (transferTraceEpisodeId) await startLocalEpisodeFromGesture(transferTraceEpisodeId, transferTracePositionMs);
      };
      $("webDiagnostics").addEventListener("click", openWebDiagnostics);
      webDiagnosticClose.addEventListener("click", () => { webDiagnostic.classList.remove("open"); restoreOverlayFocus(); });
      webDiagnosticExport.addEventListener("click", exportDiagnosticReport);
      webDiagnosticRepair.addEventListener("click", repairAppShell);
      webDiagnosticCopy.addEventListener("click", async () => {
        const report = diagnosticReport();
        try {
          await navigator.clipboard.writeText(report);
          notify("Radio Vault Web diagnostic report copied");
        } catch {
          webDiagnosticBody.focus();
          window.getSelection()?.selectAllChildren(webDiagnosticBody);
          notify("Select and copy the diagnostic report");
        }
      });
      webDiagnosticClear.addEventListener("click", () => {
        diagnosticEvents = [];
        try { localStorage.removeItem(diagnosticStorageKey); } catch {}
        recordDiagnostic("diagnostics", "Diagnostic history cleared");
        webDiagnosticBody.textContent = diagnosticReport();
      });
      webDiagnosticReconnect.addEventListener("click", async () => {
        reconnecting = true;
        applyConnectivityUi();
        const connected = await probeServer(true);
        if (connected) {
          await loadAnywhereBootstrap(true).catch(() => null);
          await load();
          notify(`Connected to ${connectedLabel()}`);
        } else notify("Radio Vault is still unreachable.", true);
        webDiagnosticBody.textContent = diagnosticReport();
      });
      async function enableOfflineShell() {
        if (location.protocol !== "https:" || !("serviceWorker" in navigator))
          return false;
        try {
          const hadController = Boolean(navigator.serviceWorker.controller);
          const registration = await navigator.serviceWorker.register(
            auth("/service-worker.js"),
            { scope: "/" },
          );
          const watchWorker = (worker) => worker?.addEventListener("statechange", () => {
            if (worker.state === "installed" && hadController) showAppUpdateBanner();
          });
          watchWorker(registration.installing);
          registration.addEventListener("updatefound", () => watchWorker(registration.installing));
          if (registration.waiting && hadController) showAppUpdateBanner();
          navigator.serviceWorker.addEventListener("controllerchange", () => {
            if (!hadController || serviceWorkerControllerChanged) return;
            serviceWorkerControllerChanged = true;
            showAppUpdateBanner();
          });
          await navigator.serviceWorker.ready;
          const worker = registration.active || registration.waiting || registration.installing;
          worker?.postMessage({ type: "CACHE_SHELL", url: location.href });
          return true;
        } catch (e) {
          console.warn("Radio Vault offline shell could not be enabled.", e);
          return false;
        }
      }
      async function request(path, options = {}) {
        const started = performance.now(),
          timeoutMs = Math.max(1000, Number(options.timeoutMs || 12000)),
          fetchOptions = { ...options };
        delete fetchOptions.timeoutMs;
        const controller = fetchOptions.signal ? null : new AbortController(),
          timeout = controller ? setTimeout(() => controller.abort(), timeoutMs) : null;
        if (controller) fetchOptions.signal = controller.signal;
        try {
          const r = await fetch(auth(api + path), {
            cache: "no-store",
            ...fetchOptions,
          });
          let data = null;
          try {
            data = await r.json();
          } catch {}
          if (!r.ok) {
            const message =
              data?.result?.message ||
              data?.message ||
              "Radio Vault could not complete that action.";
            const err = new Error(message);
            err.status = r.status;
            err.data = data;
            throw err;
          }
          const elapsedMs = Math.round(performance.now() - started);
          if (elapsedMs >= 1800)
            recordDiagnostic("slow-request", path.split("?")[0], { elapsedMs, status: r.status });
          return data;
        } catch (error) {
          if (error?.name === "AbortError") {
            const timeoutError = new Error("Radio Vault did not respond in time.");
            timeoutError.name = "TimeoutError";
            throw timeoutError;
          }
          recordDiagnostic("request-failed", path.split("?")[0], {
            status: Number(error?.status || 0),
            online: navigator.onLine,
            elapsedMs: Math.round(performance.now() - started),
          });
          throw error;
        } finally {
          if (timeout) clearTimeout(timeout);
        }
      }
      async function clientPost(service, operation, payload = {}) {
        const response = await request(`/client/${service}/${operation}`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(payload),
          timeoutMs: operation === "install-recommended" ? 15 * 60 * 1000 : 20000,
        });
        return response?.value;
      }
      function applyPlaybackSessionSnapshot(session) {
        if (!session) return;
        desktopState = session.desktop || desktopState;
        desktopStateReceivedAt = Date.now();
        webState = session.phone || session.web || webState;
        webStateReceivedAt = Date.now();
        sessionState = session;
      }
      function populateBootstrapFacets(bootstrap) {
        const previousShow = show.value || show.dataset.restoreValue || "";
        const previousYear = year.value || year.dataset.restoreValue || "";
        show.innerHTML = '<option value="">All shows</option>' +
          (bootstrap.shows || []).map((item) => `<option value="${esc(item.name)}">${esc(item.name)} (${Number(item.count || 0).toLocaleString()})</option>`).join("");
        year.innerHTML = '<option value="">All years</option>' +
          (bootstrap.years || []).map((value) => `<option value="${Number(value)}">${Number(value)}</option>`).join("");
        if ([...show.options].some((option) => option.value === previousShow)) show.value = previousShow;
        if ([...year.options].some((option) => option.value === previousYear)) year.value = previousYear;
        delete show.dataset.restoreValue;
        delete year.dataset.restoreValue;
        renderSidebarShows(bootstrap.shows || []);
      }
      function renderSidebarShows(items) {
        const host = $("sidebarShows");
        if (!host) return;
        host.innerHTML = (items || []).slice(0, 12).map((item) => `<button class="sidebarShow" data-nav-show="${esc(item.name || "")}" title="${esc(item.name || "")}"><span>${esc(item.name || "Unknown show")}</span></button>`).join("");
      }
      function applyBootstrap(bootstrap) {
        if (!bootstrap) throw new Error("The server returned an empty bootstrap payload.");
        bootstrapState = bootstrap;
        bootstrapLoadedAt = Date.now();
        bootstrapDashboardPending = true;
        bootstrapQueuePending = true;
        serverInfo = bootstrap.server || serverInfo;
        archiveBroadcastCount = Number(bootstrap.library?.broadcastCount || 0);
        archiveShowCount = Number(bootstrap.library?.showCount || 0);
        archiveFavouriteCount = Number(bootstrap.library?.favouriteCount || 0);
        archiveContinueCount = Number(bootstrap.library?.continueListeningCount || 0);
        archiveCompletedCount = Number(bootstrap.library?.completedCount || 0);
        populateBootstrapFacets(bootstrap);
        applyPlaybackSessionSnapshot(bootstrap.playback);
        serverReachable = true;
        applyConnectivityUi();
        recordDiagnostic("bootstrap", "Server startup snapshot loaded", {
          broadcasts: archiveBroadcastCount,
          shows: archiveShowCount,
          queue: Number(bootstrap.queue?.length || 0),
          elapsedAgeMs: 0,
        });
      }
      async function loadAnywhereBootstrap(force = false) {
        if (!force && bootstrapState && Date.now() - bootstrapLoadedAt < 30000) return bootstrapState;
        const payload = await request("/bootstrap?limit=12");
        applyBootstrap(payload.bootstrap);
        return bootstrapState;
      }
      async function refreshAfterReconnect() {
        if (reconnectRefreshPromise) return reconnectRefreshPromise;
        reconnectRefreshPromise = (async () => {
          reconnecting = true;
          applyConnectivityUi();
          await loadAnywhereBootstrap(true);
          serverReachable = true;
          reconnecting = false;
          applyConnectivityUi();
          try {
            await syncAllPending();
          } catch (error) {
            recordDiagnostic("sync-storage", "Pending device changes could not be inspected after reconnect");
          }
          await loadAnywhereBootstrap(true);
          await load();
          loadPlayerState();
          recordDiagnostic("reconnect", "Current Radio Vault Web workspace refreshed", { view, downloads: downloadedRecords.size });
        })().catch((error) => {
          serverReachable = false;
          reconnecting = false;
          applyConnectivityUi();
          recordDiagnostic("reconnect-failed", "Workspace refresh after reconnect failed", { online: navigator.onLine });
          throw error;
        }).finally(() => reconnectRefreshPromise = null);
        return reconnectRefreshPromise;
      }
      const offlineDbName = "RadioVaultAnywhere",
        offlineDbVersion = 2;
      function openOfflineDb() {
        return new Promise((resolve, reject) => {
          const r = indexedDB.open(offlineDbName, offlineDbVersion);
          r.onupgradeneeded = () => {
            const db = r.result;
            if (!db.objectStoreNames.contains("downloads"))
              db.createObjectStore("downloads", { keyPath: "episodeId" });
            if (!db.objectStoreNames.contains("progress"))
              db.createObjectStore("progress", { keyPath: "episodeId" });
            if (!db.objectStoreNames.contains("journal"))
              db.createObjectStore("journal", { keyPath: "id" });
          };
          r.onsuccess = () => resolve(r.result);
          r.onerror = () =>
            reject(
              r.error || new Error("Offline storage could not be opened."),
            );
        });
      }
      async function dbGet(store, key) {
        const db = await openOfflineDb();
        return new Promise((resolve, reject) => {
          const tx = db.transaction(store, "readonly"),
            r = tx.objectStore(store).get(key);
          r.onsuccess = () => resolve(r.result || null);
          r.onerror = () => reject(r.error);
          tx.oncomplete = () => db.close();
          tx.onabort = () => {
            db.close();
            reject(tx.error);
          };
        });
      }
      async function dbAll(store) {
        const db = await openOfflineDb();
        return new Promise((resolve, reject) => {
          const tx = db.transaction(store, "readonly"),
            r = tx.objectStore(store).getAll();
          r.onsuccess = () => resolve(r.result || []);
          r.onerror = () => reject(r.error);
          tx.oncomplete = () => db.close();
          tx.onabort = () => {
            db.close();
            reject(tx.error);
          };
        });
      }
      async function dbPut(store, value) {
        const db = await openOfflineDb();
        return new Promise((resolve, reject) => {
          const tx = db.transaction(store, "readwrite");
          tx.objectStore(store).put(value);
          tx.oncomplete = () => {
            db.close();
            resolve(value);
          };
          tx.onerror = () => {
            db.close();
            reject(tx.error);
          };
          tx.onabort = () => {
            db.close();
            reject(tx.error);
          };
        });
      }
      async function dbDelete(store, key) {
        const db = await openOfflineDb();
        return new Promise((resolve, reject) => {
          const tx = db.transaction(store, "readwrite");
          tx.objectStore(store).delete(key);
          tx.oncomplete = () => {
            db.close();
            resolve();
          };
          tx.onerror = () => {
            db.close();
            reject(tx.error);
          };
          tx.onabort = () => {
            db.close();
            reject(tx.error);
          };
        });
      }
      function newMutationId(prefix = "m") {
        return prefix + ":" + (crypto.randomUUID ? crypto.randomUUID() : Date.now() + "-" + Math.random().toString(36).slice(2));
      }
      function isRetryableSyncStatus(status) {
        status = Number(status || 0);
        return status === 0 || status === 408 || status === 425 || status === 429 || status >= 500;
      }
      function syncBackoffDelay(attempts) {
        return Math.min(300000, 2000 * Math.pow(2, Math.min(7, Math.max(0, Number(attempts || 1) - 1))));
      }
      async function journalMutation(kind, endpoint, payload, dedupeKey, mutationId = null) {
        const existing = (await dbAll("journal")).filter(x => x.dedupeKey === dedupeKey);
        for (const item of existing) await dbDelete("journal", item.id);
        const item = {
          id: mutationId || newMutationId("journal"), kind, endpoint, payload, dedupeKey,
          createdAt: Date.now(), attempts: 0, blocked: false, nextAttemptAt: 0, lastError: "",
        };
        await dbPut("journal", item);
        await refreshSyncStatus();
        return item;
      }
      async function mutateOrJournal(kind, endpoint, payload, dedupeKey) {
        const mutationId = newMutationId(kind);
        if (serverReachable) {
          try {
            return await request(endpoint, {
              method:"POST",
              headers:{"Content-Type":"application/json","X-Radio-Vault-Mutation-Id":mutationId},
              body: JSON.stringify(payload || {}),
            });
          } catch (e) {
            if (!isRetryableSyncStatus(e.status)) throw e;
            if (!e.status) { serverReachable = false; applyConnectivityUi(); }
          }
        }
        await journalMutation(kind, endpoint, payload || {}, dedupeKey, mutationId);
        notify("Saved on this device. It will sync when Radio Vault reconnects.");
        return { queued:true };
      }
      async function syncMutationJournal() {
        if (!serverReachable || syncInProgress) return;
        syncInProgress = true; await refreshSyncStatus();
        try {
          const now = Date.now();
          const items=(await dbAll("journal")).sort((a,b)=>a.createdAt-b.createdAt);
          for (const item of items) {
            if (item.blocked || Number(item.nextAttemptAt || 0) > now) continue;
            try {
              await request(item.endpoint,{
                method:"POST",
                headers:{"Content-Type":"application/json","X-Radio-Vault-Mutation-Id":item.id},
                body:JSON.stringify(item.payload||{}),
              });
              await dbDelete("journal",item.id);
            } catch(e) {
              item.attempts=(item.attempts||0)+1;
              item.lastAttemptAt=Date.now();
              item.lastError=e.status?"HTTP "+String(e.status):"Server unavailable";
              item.blocked=!isRetryableSyncStatus(e.status);
              item.nextAttemptAt=item.blocked?0:Date.now()+syncBackoffDelay(item.attempts);
              await dbPut("journal",item);
              if(!e.status){serverReachable=false;applyConnectivityUi();break;}
            }
          }
        } finally { syncInProgress=false; await refreshSyncStatus(); }
      }
      async function syncAllPending() {
        if (!serverReachable) return;
        await syncPendingProgress();
        await syncMutationJournal();
      }
      function scheduleSyncRetry(records) {
        clearTimeout(syncRetryTimer);
        syncRetryTimer = null;
        if (!serverReachable || syncInProgress) return;
        const now = Date.now();
        const next = records
          .filter(item => !item.blocked && Number(item.nextAttemptAt || 0) > now)
          .map(item => Number(item.nextAttemptAt))
          .sort((a,b)=>a-b)[0];
        if (!next) return;
        syncRetryTimer = setTimeout(() => syncAllPending().catch(() => {}), Math.max(500, next - now));
      }
      async function resetBlockedSyncRecords() {
        for (const item of await dbAll("journal")) {
          if (!item.blocked) continue;
          item.blocked=false; item.nextAttemptAt=0; item.lastError="";
          await dbPut("journal",item);
        }
        for (const progress of await dbAll("progress")) {
          if (!progress.pending || !progress.blocked) continue;
          progress.blocked=false; progress.nextAttemptAt=0; progress.lastError="";
          await dbPut("progress",progress);
        }
        await refreshSyncStatus();
        await syncAllPending();
      }
      async function discardBlockedSyncRecords() {
        for (const item of await dbAll("journal")) if (item.blocked) await dbDelete("journal",item.id);
        for (const progress of await dbAll("progress")) {
          if (!progress.pending || !progress.blocked) continue;
          progress.pending=false; progress.blocked=false; progress.nextAttemptAt=0; progress.lastError="Discarded on this device";
          await dbPut("progress",progress);
          try { localStorage.setItem(progressKey(progress.episodeId),JSON.stringify(progress)); } catch {}
        }
        await refreshSyncStatus();
      }
      async function refreshSyncStatus() {
        const journals=await dbAll("journal"), progress=(await dbAll("progress")).filter(x=>x.pending);
        pendingJournalCount=journals.length+progress.length;
        blockedSyncCount=[...journals,...progress].filter(x=>x.blocked).length;
        let mode= !serverReachable ? "offline" : syncInProgress ? "syncing" : pendingJournalCount ? "attention" : "synced";
        syncStatus.className="syncStatusButton "+mode;
        const waitingText = blockedSyncCount
          ? `${blockedSyncCount} change${blockedSyncCount===1?"":"s"} could not be applied automatically. Retry or discard them from this sheet.`
          : `${pendingJournalCount} change${pendingJournalCount===1?"":"s"} waiting to sync.`;
        const config={synced:["Synced",`All changes are stored on ${connectedLabel()}.`,"M5 12.5l4 4L19 7"],syncing:["Syncing",`${pendingJournalCount} change${pendingJournalCount===1?"":"s"} being sent to Radio Vault.`,"M20 7h-5V2M4 17h5v5M19 12a7 7 0 0 0-12-5M5 12a7 7 0 0 0 12 5"],offline:["Offline",`${pendingJournalCount} change${pendingJournalCount===1?"":"s"} waiting. Downloaded broadcasts remain available.`,"M4 4l16 16M8.5 8.5A5 5 0 0 1 17 12M5 12a9 9 0 0 1 .8-3.7M12 17h.01"],attention:["Needs attention",waitingText,"M12 8v5M12 17h.01M10.3 3.6L2.4 18a2 2 0 0 0 1.8 3h15.6a2 2 0 0 0 1.8-3L13.7 3.6a2 2 0 0 0-3.4 0z"]}[mode];
        syncStatus.innerHTML=`<svg viewBox="0 0 24 24"><path d="${config[2]}"/></svg>`;
        syncStatus.setAttribute("aria-label",config[0]); syncStatus.title=config[0]; syncSheetTitle.textContent=config[0]; syncSheetBody.textContent=config[1];
        syncRetryFailed.hidden=blockedSyncCount===0;
        syncDiscardFailed.hidden=blockedSyncCount===0;
        scheduleSyncRetry([...journals,...progress]);
      }
      function formatBytes(bytes) {
        const n = Number(bytes || 0);
        if (n < 1024) return n + " B";
        if (n < 1048576) return (n / 1024).toFixed(1) + " MB";
        return (n / 1048576).toFixed(n > 104857600 ? 0 : 1) + " MB";
      }
      function revokeOfflineUrls() {
        downloadedArtworkUrls.clear();
      }
      function offlineAudioPath(id) {
        return "/__offline_audio__/" + Number(id);
      }
      function offlineArtworkPath(id) {
        return "/__offline_artwork__/" + Number(id);
      }
      async function cacheOfflineAudio(id, blob, mimeType) {
        if (!("caches" in window)) return false;
        const cache = await caches.open("radio-vault-anywhere-audio-v1");
        await cache.put(
          offlineAudioPath(id),
          new Response(blob, {
            headers: {
              "Content-Type": mimeType || blob.type || "audio/mpeg",
              "Content-Length": String(blob.size),
              "Accept-Ranges": "bytes",
            },
          }),
        );
        return true;
      }
      async function removeOfflineAudio(id) {
        if ("caches" in window) {
          const cache = await caches.open("radio-vault-anywhere-audio-v1");
          await cache.delete(offlineAudioPath(id));
        }
      }
      async function ensureOfflineAudioCached(record) {
        if (!record?.audioBlob || !("caches" in window)) return false;
        const cached = await caches.match(offlineAudioPath(record.episodeId));
        if (cached) return true;
        return cacheOfflineAudio(
          record.episodeId,
          record.audioBlob,
          record.mimeType,
        );
      }
      async function cacheOfflineArtwork(id, blob) {
        if (!blob || !blob.size || !("caches" in window)) return false;
        const mime =
            blob.type && blob.type.startsWith("image/")
              ? blob.type
              : "image/jpeg",
          cache = await caches.open("radio-vault-anywhere-artwork-v1");
        await cache.put(
          offlineArtworkPath(id),
          new Response(blob, {
            headers: {
              "Content-Type": mime,
              "Content-Length": String(blob.size),
              "Cache-Control": "public, max-age=31536000, immutable",
            },
          }),
        );
        return true;
      }
      async function removeOfflineArtwork(id) {
        if ("caches" in window) {
          const cache = await caches.open("radio-vault-anywhere-artwork-v1");
          await cache.delete(offlineArtworkPath(id));
        }
      }
      async function ensureOfflineArtworkCached(record) {
        if (
          !record?.artworkBlob ||
          !record.artworkBlob.size ||
          !("caches" in window)
        )
          return false;
        const cached = await caches.match(offlineArtworkPath(record.episodeId));
        if (cached) return true;
        return cacheOfflineArtwork(record.episodeId, record.artworkBlob);
      }
      async function auditDownloadedStorage(notifyResult = false) {
        if (downloadAuditPromise) return downloadAuditPromise;
        downloadAuditPromise = (async () => {
          let checked=0, repaired=0, needsRepair=0;
          const audioCache = "caches" in window ? await caches.open("radio-vault-anywhere-audio-v1") : null;
          for (const [id, record] of downloadedRecords) {
            checked++;
            let changed=false;
            const blobSize=Number(record.audioBlob?.size || 0);
            if (!blobSize) {
              if (record.repairState !== "missing-audio") { record.repairState="missing-audio"; changed=true; }
              needsRepair++;
            } else {
              if (Number(record.size || 0) !== blobSize) { record.size=blobSize; changed=true; }
              if (record.repairState) { record.repairState=""; changed=true; }
              if (audioCache) {
                const cached=await audioCache.match(offlineAudioPath(id));
                const cachedLength=Number(cached?.headers.get("Content-Length") || 0);
                if (!cached || cachedLength !== blobSize) {
                  await cacheOfflineAudio(id,record.audioBlob,record.mimeType);
                  repaired++;
                }
              }
            }
            record.lastCheckedAt=Date.now();
            if (changed) await dbPut("downloads",record);
            downloadedRecords.set(id,record);
          }
          downloadRepairCount=needsRepair;
          updateDownloadButtons();
          recordDiagnostic("offline-storage", "Downloaded storage audit completed", { checked, repaired, needsRepair });
          if (notifyResult) notify(needsRepair ? `${needsRepair} download${needsRepair===1?"":"s"} need repair.` : repaired ? `${repaired} download cache entr${repaired===1?"y":"ies"} repaired.` : "All downloads are healthy.", needsRepair>0);
          return {checked,repaired,needsRepair};
        })().finally(()=>downloadAuditPromise=null);
        return downloadAuditPromise;
      }
      async function refreshDownloadedIndex() {
        revokeOfflineUrls();
        downloadedRecords.clear();
        for (const record of await dbAll("downloads")) {
          const id = Number(record.episodeId);
          downloadedRecords.set(id, record);
          ensureOfflineAudioCached(record).catch(() => {});
          if (record.artworkBlob?.size) {
            downloadedArtworkUrls.set(id, offlineArtworkPath(id));
            ensureOfflineArtworkCached(record).catch(() => {});
          }
        }
        renderPlayer();
        refreshSyncStatus().catch(()=>{});
      }
      function progressKey(episodeId) {
        return "radioVaultProgress:" + Number(episodeId);
      }
      function localProgressFallback(episodeId) {
        try {
          return JSON.parse(
            localStorage.getItem(progressKey(episodeId)) || "null",
          );
        } catch {
          return null;
        }
      }
      async function getLocalProgress(episodeId) {
        const indexed = await dbGet("progress", Number(episodeId)),
          fallback = localProgressFallback(episodeId);
        if (fallback && (!indexed || fallback.updatedAt > indexed.updatedAt)) {
          await dbPut("progress", fallback);
          return fallback;
        }
        return indexed;
      }
      async function saveLocalProgress(completed = false, options = {}) {
        if (!localEpisode || phoneTransferInProgress) return null;
        const positionMs = currentLogicalPositionMs(),
          durationMs = canonicalDurationMs(),
          allowRewind = options.allowRewind ?? (serverReachable && thisPhoneOwnsSession()),
          record = {
            episodeId: Number(localEpisode.id),
            positionMs,
            durationMs,
            speed: Number(audio.playbackRate || 1),
            completed:
              !!completed ||
              (durationMs > 0 && positionMs >= Math.max(0, durationMs - 5000)),
            updatedAt: Date.now(),
            pending: true,
            mutationId: newMutationId("progress"),
            attempts: 0,
            blocked: false,
            nextAttemptAt: 0,
            lastError: "",
            allowRewind: !!allowRewind,
            expectedGeneration: allowRewind ? Number(sessionState?.generation || 0) : 0,
            explicitSeek: !!options.explicitSeek,
          };
        try {
          localStorage.setItem(
            progressKey(record.episodeId),
            JSON.stringify(record),
          );
        } catch {}
        await dbPut("progress", record);
        lastLocalSave = Date.now();
        lastDurableProgressSave = lastLocalSave;
        return record;
      }
      async function syncPendingProgress() {
        if (!serverReachable || syncInProgress) { await refreshSyncStatus(); return; }
        syncInProgress = true; await refreshSyncStatus();
        try {
          const now=Date.now();
          const pending = (await dbAll("progress"))
            .filter((x) => x.pending)
            .sort((a, b) => a.updatedAt - b.updatedAt);
          for (const progress of pending) {
            if (progress.blocked || Number(progress.nextAttemptAt || 0) > now) continue;
            progress.mutationId = progress.mutationId || newMutationId("progress");
            try {
              const d = await request(
                "/broadcasts/" + progress.episodeId + "/offline-progress",
                {
                  method: "POST",
                  headers: { "Content-Type": "application/json", "X-Radio-Vault-Mutation-Id": progress.mutationId },
                  body: JSON.stringify({
                    clientId,
                    episodeId: progress.episodeId,
                    positionMs: progress.positionMs,
                    durationMs: progress.durationMs,
                    completed: progress.completed,
                    speed: progress.speed,
                    capturedAt: new Date(progress.updatedAt).toISOString(),
                    allowRewind: !!progress.allowRewind,
                    expectedGeneration: Number(progress.expectedGeneration || 0),
                    explicitSeek: !!progress.explicitSeek,
                  }),
                },
              );
              progress.pending = false;
              progress.blocked = false;
              progress.nextAttemptAt = 0;
              progress.lastError = "";
              progress.syncedAt = Date.now();
              try { localStorage.setItem(progressKey(progress.episodeId),JSON.stringify(progress)); } catch {}
              await dbPut("progress", progress);
              if (localEpisode?.id === progress.episodeId && d.result?.episode)
                localEpisode = { ...localEpisode, ...d.result.episode };
            } catch (e) {
              // A generation/owner conflict means this progress record belongs to
              // an output that has already lost playback. It is permanently stale,
              // not retryable: discarding it prevents an old Safari zero or delayed
              // pause callback from resurfacing after a later handoff.
              if (Number(e.status || 0) === 409 && progress.allowRewind) {
                progress.pending = false;
                progress.blocked = false;
                progress.nextAttemptAt = 0;
                progress.discardedAt = Date.now();
                progress.lastError = "Discarded after playback ownership changed";
                await dbPut("progress", progress);
                try { localStorage.setItem(progressKey(progress.episodeId),JSON.stringify(progress)); } catch {}
                loadPlayerState().catch(() => {});
                continue;
              }
              progress.attempts=(progress.attempts||0)+1;
              progress.lastAttemptAt=Date.now();
              progress.lastError=e.status?"HTTP "+String(e.status):"Server unavailable";
              progress.blocked=!isRetryableSyncStatus(e.status);
              progress.nextAttemptAt=progress.blocked?0:Date.now()+syncBackoffDelay(progress.attempts);
              await dbPut("progress",progress);
              try { localStorage.setItem(progressKey(progress.episodeId),JSON.stringify(progress)); } catch {}
              if (!e.status) { serverReachable=false; applyConnectivityUi(); break; }
            }
          }
        } finally { syncInProgress = false; await refreshSyncStatus(); }
      }
      function episodeArt(e) {
        return (
          downloadedArtworkUrls.get(Number(e?.id)) ||
          (e?.hasArtwork ? auth("/artwork/" + e.id) : "")
        );
      }
      function updateViewChrome() {
        const descriptions = {
          dashboard: [
            "Dashboard",
            "Continue listening and rediscover your archive.",
          ],
          library: ["Library", "Browse, filter and search every broadcast."],
          transcripts: [
            "Transcription studio",
            "Create and edit transcripts within the Knowledge workspace.",
          ],
          moments: ["Moments", "Return to the exact parts of broadcasts you wanted to remember."],
          research: ["Knowledge", "Review archive knowledge, coverage, sources and portable knowledge databases."],
          wiki: ["Explore", "Explore the people, shows, topics and history held in your archive."],
          downloaded: [
            "Downloads",
            "Broadcasts stored on this phone for offline listening.",
          ],
          queue: ["Queue", "Choose what Radio Vault should play next."],
          health: [
            "Archive Health",
            "A quick assessment of collection, metadata and research quality.",
          ],
          settings: ["Settings", "Server, archive, playback and connected-client settings."],
        };
        let [title, description] = descriptions[view] || descriptions.dashboard;
        if (view === "library" && libraryView === "favorites") {
          title = "Favourites";
          description = "Your saved broadcasts in one place.";
        } else if (view === "library" && navMode === "search") {
          title = "Search";
          description = "Find broadcasts, people, topics and transcript wording.";
        }
        document.body.dataset.view = view;
        viewTitle.textContent = title;
        viewDescription.textContent = description;
        const activeNavKey = view === "transcripts" ? "research" : view === "library"
          ? libraryView === "favorites" ? "favourites" : navMode === "search" ? "search" : "library"
          : view;
        document
          .querySelectorAll(".primaryTab")
          .forEach((button) =>
            button.classList.toggle("active", button.dataset.navKey === activeNavKey),
          );
        document.querySelectorAll(".sidebarShow").forEach((button) =>
          button.classList.toggle("active", view === "library" && button.dataset.navShow === show.value));
        libraryTools.hidden = view !== "library" && view !== "downloaded";
        libraryViewChips.hidden = view !== "library";
        year.hidden = view === "downloaded";
        month.hidden = view === "downloaded";
        exactDate.hidden = view === "downloaded";
        statusFilter.hidden = view === "downloaded";
        const applied = [];
        if (show.value) applied.push(show.value);
        if (year.value) applied.push(year.value);
        if (month.value) applied.push(month.options[month.selectedIndex]?.text || month.value);
        if (exactDate.value) applied.push(exactDate.value);
        if (statusFilter.value) applied.push(statusFilter.options[statusFilter.selectedIndex]?.text || statusFilter.value);
        if (search.value.trim()) applied.push(`“${search.value.trim()}”`);
        const activeFilterCount = [show.value, year.value, month.value, exactDate.value, statusFilter.value, search.value.trim()].filter(Boolean).length;
        filterCount.textContent = String(activeFilterCount);
        filterCount.classList.toggle("active", activeFilterCount > 0);
        filterToggle.setAttribute("aria-label", activeFilterCount ? `Filters, ${activeFilterCount} active` : "Filters");
        filterSummary.textContent = view === "downloaded"
          ? "Stored locally on this device"
          : applied.length ? applied.join(" · ") : "Complete canonical library";
        document
          .querySelectorAll(".viewChip")
          .forEach((button) =>
            button.classList.toggle(
              "active",
              button.dataset.libraryView === libraryView,
            ),
          );
      }
      function setPrimaryView(next, nextLibraryView = null) {
        activeViewLoad?.abort();
        viewLoadGeneration++;
        if (!serverReachable && next !== "downloaded" && next !== "dashboard") {
          notify("That view needs the Radio Vault server. Your current screen has been preserved.", true);
          return;
        }
        view = next;
        if (nextLibraryView) libraryView = nextLibraryView;
        if (next !== "library") navMode = next;
        if (serverReachable) {
          lastConnectedView = view;
          lastConnectedLibraryView = libraryView;
        }
        saveNavigationState();
        updateViewChrome();
        load();
      }
      function beginViewLoad() {
        activeViewLoad?.abort();
        activeViewLoad = new AbortController();
        return {
          generation: ++viewLoadGeneration,
          signal: activeViewLoad.signal,
        };
      }
      function isCurrentViewLoad(generation) {
        return generation === viewLoadGeneration;
      }
      function connectedLabel() {
        const name = String(serverInfo?.displayName || "Radio Vault").trim();
        const version = String(serverInfo?.appVersion || "").trim();
        return version ? `${name} · ${version}` : name;
      }
      function applyConnectivityUi() {
        const offline = !serverReachable;
        document.body.classList.toggle("offlineOnly", offline);
        document.querySelectorAll(".primaryTab").forEach((button) => {
          const usable = !offline || button.dataset.section === "dashboard" || button.dataset.section === "downloaded";
          button.hidden = false;
          button.disabled = !usable;
          button.setAttribute("aria-disabled", usable ? "false" : "true");
        });
        show.disabled = false;
        show.hidden = false;
        search.placeholder = offline
          ? "Search downloaded broadcasts"
          : "Search broadcasts, people or topics";
        serverState.textContent = offline
          ? downloadedRecords.size
            ? "Offline · downloaded broadcasts available"
            : "Offline · no downloaded broadcasts"
          : reconnecting
            ? `Reconnecting to ${connectedLabel()}…`
            : serverState.textContent.startsWith("Offline") || serverState.textContent === "Connected locally" || serverState.textContent.startsWith("Reconnecting")
              ? connectedLabel()
              : serverState.textContent;
        serverState.title = offline
          ? "The Radio Vault server is currently unreachable."
          : serverInfo
            ? `${serverInfo.displayName} · API ${serverInfo.apiVersion} · schema ${serverInfo.databaseSchemaVersion} · ${serverInfo.capabilities?.filter((x) => x.available).length || 0} capabilities`
            : "Connected to the Radio Vault server.";
        serverState.classList.toggle("offlineState", offline || reconnecting);
        if (!offline) {
          lastConnectedView = view;
          lastConnectedLibraryView = libraryView;
        }
        updateViewChrome();
        refreshSyncStatus().catch(()=>{});
      }
      function connectionBoundaryBanner() {
        if (serverReachable) return '<div class="connectionBanner"><span><strong>Server</strong> · Complete canonical library from ' + esc(connectedLabel()) + '</span><span class="sourceBoundary server">Server library</span></div>';
        return '<div class="connectionBanner"><span><strong>Offline on this device</strong> · The previous screen is preserved, but only downloaded broadcasts can be opened until Radio Vault reconnects.</span><button data-open-section="downloaded">Open Downloads</button></div>';
      }
      async function probeServer(force = false) {
        const now = Date.now();
        if (!force && now - lastReachabilityCheck < 1500)
          return serverReachable;
        if (reachabilityProbe) return reachabilityProbe;
        lastReachabilityCheck = now;
        reachabilityProbe = (async () => {
          const wasReachable = serverReachable,
            controller = new AbortController(),
            timeout = setTimeout(() => controller.abort(), 2500);
          try {
            const response = await fetch(auth(api + "/server-info"), {
              cache: "no-store",
              signal: controller.signal,
            });
            serverReachable = response.ok;
            if (response.ok) {
              const payload = await response.json();
              serverInfo = payload.server || serverInfo;
            }
          } catch {
            serverReachable = false;
          } finally {
            clearTimeout(timeout);
            reachabilityProbe = null;
            reconnecting = false;
            if (wasReachable !== serverReachable) {
              recordDiagnostic("connectivity", serverReachable ? "Server reconnected" : "Server became unreachable", { online: navigator.onLine });
              if (!wasReachable && serverReachable && document.body.dataset.booted)
                setTimeout(() => refreshAfterReconnect().catch(() => {}), 0);
            }
            applyConnectivityUi();
          }
          return serverReachable;
        })();
        return reachabilityProbe;
      }
      function downloadState(id) {
        id = Number(id);
        if (activeDownload?.episodeId === id) return "active";
        const record=downloadedRecords.get(id);
        if (record?.repairState) return "repair";
        return record ? "downloaded" : "none";
      }
      function downloadActionLabel(state) {
        return state === "active" ? "Cancel download"
          : state === "repair" ? "Repair download"
          : state === "downloaded" ? "Remove download"
          : "Download";
      }
      function downloadActionIcon(state) {
        if (state === "active")
          return '<svg class="libraryActionGlyph" viewBox="0 0 24 24" aria-hidden="true"><path d="M6 6l12 12M18 6 6 18"/></svg>';
        if (state === "repair")
          return '<svg class="libraryActionGlyph" viewBox="0 0 24 24" aria-hidden="true"><path d="M19 8V4m0 0h-4m4 0-3.2 3.2A7 7 0 1 0 19 14"/></svg>';
        if (state === "downloaded")
          return '<svg class="libraryActionGlyph" viewBox="0 0 24 24" aria-hidden="true"><path d="M5 7h14M9 7V4h6v3m-8 0 1 13h8l1-13M10 11v5m4-5v5"/></svg>';
        return '<svg class="libraryActionGlyph" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3v12m0 0 4-4m-4 4-4-4M5 20h14"/></svg>';
      }
      function applyDownloadButtonState(button, state) {
        const label = button.classList.contains("libraryPrimaryAction") && state === "repair"
          ? "Repair to listen"
          : downloadActionLabel(state);
        button.dataset.downloadState = state;
        if (button.classList.contains("libraryCompactDownload"))
          button.innerHTML = downloadActionIcon(state) + `<span class="srOnly" data-download-label>${esc(label)}</span>`;
        else if (button.classList.contains("dashboardPlay"))
          button.textContent = state === "repair" ? "↻" : label;
        else
          button.textContent = label;
        button.setAttribute("aria-label", label);
        button.title = label;
        button.classList.toggle("dangerButton", state === "downloaded");
      }
      function updateDownloadButtons() {
        document.querySelectorAll("[data-download]").forEach((b) => {
          const id = Number(b.dataset.download), state = downloadState(id);
          applyDownloadButtonState(b, state);
        });
        const activeId = localEpisode?.id;
        if (playerDownload && activeId) {
          playerDownload.dataset.download = String(activeId);
          const state = downloadState(activeId);
          applyDownloadButtonState(playerDownload, state);
        } else if (playerDownload) {
          delete playerDownload.dataset.download;
          delete playerDownload.dataset.downloadState;
          playerDownload.textContent = "Download";
          playerDownload.setAttribute("aria-label", "Download");
          playerDownload.title = "Download";
          playerDownload.classList.remove("dangerButton");
        }
      }
      function showDownloadProgress(title, received, total) {
        downloadTray.hidden = false;
        downloadTitle.textContent = title || "Downloading broadcast";
        const percent = total > 0 ? Math.min(100, (received * 100) / total) : 0;
        downloadFill.style.width = percent + "%";
        downloadText.textContent =
          total > 0 ? Math.round(percent) + "%" : "Downloading…";
        downloadSize.textContent =
          total > 0
            ? formatBytes(received) + " of " + formatBytes(total)
            : formatBytes(received);
        updateDownloadButtons();
      }
      function hideDownloadProgress() {
        downloadTray.hidden = true;
        downloadFill.style.width = "0%";
        downloadText.textContent = "Preparing…";
        downloadSize.textContent = "";
        updateDownloadButtons();
      }
      async function cacheArtwork(episodeId) {
        try {
          const r = await fetch(auth("/artwork/" + episodeId), {
            cache: "no-store",
          });
          if (!r.ok) return null;
          const contentType = (r.headers.get("content-type") || "")
            .split(";")[0]
            .trim()
            .toLowerCase();
          if (!contentType.startsWith("image/")) return null;
          const blob = await r.blob();
          return blob.size ? new Blob([blob], { type: contentType }) : null;
        } catch {
          return null;
        }
      }
      async function repairDownloadedArtwork() {
        if (!serverReachable) return false;
        if (artworkRepairPromise) return artworkRepairPromise;
        artworkRepairPromise = (async () => {
          let changed = false;
          for (const [id, record] of downloadedRecords) {
            if (record.artworkBlob?.size) {
              downloadedArtworkUrls.set(id, offlineArtworkPath(id));
              await ensureOfflineArtworkCached(record).catch(() => false);
              continue;
            }
            const artworkBlob = await cacheArtwork(id);
            if (!artworkBlob) continue;
            record.artworkBlob = artworkBlob;
            record.artworkMimeType = artworkBlob.type;
            await dbPut("downloads", record);
            await cacheOfflineArtwork(id, artworkBlob);
            downloadedArtworkUrls.set(id, offlineArtworkPath(id));
            changed = true;
          }
          if (changed) {
            renderPlayer();
            if (view === "downloaded") await loadDownloaded();
          }
          return changed;
        })().finally(() => (artworkRepairPromise = null));
        return artworkRepairPromise;
      }
      async function startDownload(episodeId) {
        episodeId = Number(episodeId);
        if (activeDownload) {
          if (activeDownload.episodeId === episodeId) {
            activeDownload.controller.abort();
            return;
          }
          notify("Finish or cancel the current download first.", true);
          return;
        }
        if (downloadedRecords.has(episodeId)) {
          const existing=downloadedRecords.get(episodeId), repairing=!!existing?.repairState;
          if (!repairing && !confirm("Remove this downloaded broadcast from this phone?")) return;
          if (repairing && !serverReachable) { notify("Reconnect to Radio Vault before repairing this download.",true); return; }
          if (!repairing) {
            await dbDelete("downloads", episodeId);
            await removeOfflineAudio(episodeId);
            await removeOfflineArtwork(episodeId);
            downloadedRecords.delete(episodeId);
            downloadedArtworkUrls.delete(episodeId);
            updateDownloadButtons();
            if (view === "downloaded") loadDownloaded();
            notify("Download removed from this phone");
            return;
          }
          // Keep the repair record until a complete replacement has been stored.
          // A failed or cancelled re-download must not make the broadcast disappear.
          await removeOfflineAudio(episodeId);
          notify("Repairing download…");
        }
        const controller = new AbortController();
        activeDownload = { episodeId, controller };
        try {
          if (navigator.storage?.persist)
            await navigator.storage.persist().catch(() => false);
          const details = await getDetails(episodeId),
            episode = details.episode;
          showDownloadProgress(episode.title, 0, 0);
          const response = await fetch(auth("/audio/" + episodeId), {
            signal: controller.signal,
            cache: "no-store",
          });
          if (!response.ok)
            throw new Error("The broadcast could not be downloaded.");
          const total = Number(response.headers.get("content-length") || 0);
          if (total && navigator.storage?.estimate) {
            const estimate = await navigator.storage.estimate();
            if (
              estimate.quota &&
              estimate.usage + total > estimate.quota * 0.95
            )
              throw new Error(
                "There is not enough browser storage for this broadcast.",
              );
          }
          const chunks = [];
          let received = 0;
          if (response.body?.getReader) {
            const reader = response.body.getReader();
            while (true) {
              const { done, value } = await reader.read();
              if (done) break;
              chunks.push(value);
              received += value.byteLength;
              showDownloadProgress(episode.title, received, total);
            }
          } else {
            const blob = await response.blob();
            chunks.push(blob);
            received = blob.size;
            showDownloadProgress(episode.title, received, total || received);
          }
          const audioBlob = new Blob(chunks, {
              type: response.headers.get("content-type") || "audio/mpeg",
            }),
            artworkBlob = await cacheArtwork(episodeId),
            record = {
              episodeId,
              episode,
              details,
              audioBlob,
              artworkBlob,
              artworkMimeType: artworkBlob?.type || "",
              size: audioBlob.size,
              mimeType: audioBlob.type,
              downloadedAt: Date.now(),
              repairState: "",
              lastCheckedAt: Date.now(),
            };
          await dbPut("downloads", record);
          await cacheOfflineAudio(episodeId, audioBlob, audioBlob.type);
          if (artworkBlob) {
            await cacheOfflineArtwork(episodeId, artworkBlob);
            downloadedArtworkUrls.set(episodeId, offlineArtworkPath(episodeId));
          }
          downloadedRecords.set(episodeId, record);
          notify("Broadcast downloaded for offline listening");
          if (view === "downloaded") loadDownloaded();
        } catch (e) {
          if (e.name === "AbortError") notify("Download cancelled");
          else notify(e.message || "Download failed.", true);
        } finally {
          activeDownload = null;
          hideDownloadProgress();
          if (currentDetailId) openDetails(currentDetailId, false);
          renderPlayer();
        }
      }
      async function loadDownloaded() {
        view = "downloaded";
        updateViewChrome();
        list.innerHTML = loadingSkeleton(4);
        try {
          await refreshDownloadedIndex();
          await auditDownloadedStorage();
          const progressById = new Map(
            (await dbAll("progress")).map((x) => [Number(x.episodeId), x]),
          );
          let records = [...downloadedRecords.values()];
          const term = search.value.trim().toLowerCase(),
            showName = show.value.trim().toLowerCase();
          records = records.filter(
            (r) =>
              (!showName ||
                String(r.episode.show).toLowerCase() === showName) &&
              (!term ||
                [
                  r.episode.show,
                  r.episode.title,
                  r.episode.summary,
                  r.episode.peopleSearchText,
                  r.episode.topicSearchText,
                ].some((x) =>
                  String(x || "")
                    .toLowerCase()
                    .includes(term),
                )),
          );
          records.sort((a, b) =>
            downloadSort === "oldest"
              ? a.downloadedAt - b.downloadedAt
              : downloadSort === "largest"
                ? b.size - a.size
                : downloadSort === "smallest"
                  ? a.size - b.size
                  : downloadSort === "lastplayed"
                    ? Number(
                        progressById.get(Number(b.episodeId))?.updatedAt || 0,
                      ) -
                      Number(
                        progressById.get(Number(a.episodeId))?.updatedAt || 0,
                      )
                    : downloadSort === "alphabetical"
                      ? String(a.episode.title || "").localeCompare(String(b.episode.title || ""))
                      : downloadSort === "broadcastdate"
                        ? String(b.episode.airDate || "").localeCompare(String(a.episode.airDate || ""))
                        : b.downloadedAt - a.downloadedAt,
          );
          let episodes = records.map((r) => {
            const p = progressById.get(Number(r.episodeId));
            return {
              ...r.episode,
              date: r.episode.airDate
                ? String(r.episode.airDate).slice(0, 10)
                : "",
              positionMs: p?.positionMs ?? r.episode.positionMs,
              durationMs: p?.durationMs || r.episode.durationMs,
              status: p?.completed
                ? "Completed"
                : p?.positionMs > 0
                  ? "In Progress"
                  : r.episode.status,
              progressPercent:
                (p?.durationMs || r.episode.durationMs) > 0
                  ? Math.min(
                      100,
                      Math.round(
                        ((p?.positionMs ?? r.episode.positionMs) * 100) /
                          (p?.durationMs || r.episode.durationMs),
                      ),
                    )
                  : 0,
              downloaded: true,
              downloadSize: r.size,
              downloadedAt: r.downloadedAt,
            };
          });
          if (downloadStatusFilter !== "all") {
            episodes = episodes.filter((episode) =>
              downloadStatusFilter === "inprogress"
                ? episode.status === "In Progress"
                : downloadStatusFilter === "completed"
                  ? episode.status === "Completed"
                  : episode.status !== "In Progress" && episode.status !== "Completed",
            );
          }
          const totalBytes = records.reduce(
              (n, r) => n + Number(r.size || 0),
              0,
            ),
            estimate = navigator.storage?.estimate
              ? await navigator.storage.estimate().catch(() => null)
              : null,
            free = estimate?.quota
              ? Math.max(0, estimate.quota - (estimate.usage || 0))
              : null;
          count.textContent =
            episodes.length +
            " downloaded broadcast" +
            (episodes.length === 1 ? "" : "s");
          const summary = `<div class="offlineLibrary"><div class="offlineSummary"><div class="offlineSummaryRow"><div><strong>Offline Library</strong><div class="muted">${episodes.length} broadcasts · ${fmtBytes(totalBytes)} stored${free == null ? "" : " · " + fmtBytes(free) + " available"}${downloadRepairCount ? " · "+downloadRepairCount+" need repair" : " · all checked"}</div></div><span class="sourcePill">Stored on this phone</span></div><div class="offlineTools"><select id="downloadStatusFilter" aria-label="Listening status"><option value="all">All downloaded</option><option value="unplayed">Not started</option><option value="inprogress">In progress</option><option value="completed">Completed</option></select><select id="downloadSort" aria-label="Sort downloaded broadcasts"><option value="newest">Newest download</option><option value="oldest">Oldest download</option><option value="broadcastdate">Broadcast date</option><option value="lastplayed">Last played</option><option value="alphabetical">Alphabetical</option><option value="largest">Largest first</option><option value="smallest">Smallest first</option></select><button id="checkDownloads">Check downloads</button><button id="deleteAllDownloads" ${downloadedRecords.size ? "" : "disabled"}>Delete all downloads</button></div></div></div>`;
          list.innerHTML =
            summary +
            (episodes.length
              ? episodes.map(episodeCard).join("")
              : '<div class="empty">No broadcasts have been downloaded to this phone.</div>');
          const statusFilter = $("downloadStatusFilter");
          if (statusFilter) {
            statusFilter.value = downloadStatusFilter;
            statusFilter.addEventListener("change", () => {
              downloadStatusFilter = statusFilter.value;
              loadDownloaded();
            });
          }
          const sort = $("downloadSort");
          if (sort) {
            sort.value = downloadSort;
            sort.addEventListener("change", () => {
              downloadSort = sort.value;
              loadDownloaded();
            });
          }
          const checkDownloads = $("checkDownloads");
          if (checkDownloads) checkDownloads.addEventListener("click", async()=>{checkDownloads.disabled=true;try{await auditDownloadedStorage(true);await loadDownloaded();}finally{checkDownloads.disabled=false;}});
          const clear = $("deleteAllDownloads");
          if (clear)
            clear.addEventListener("click", async () => {
              if (
                !confirm("Delete every downloaded broadcast from this phone?")
              )
                return;
              for (const id of [...downloadedRecords.keys()]) {
                await dbDelete("downloads", id);
                await removeOfflineAudio(id);
                await removeOfflineArtwork(id);
              }
              downloadedRecords.clear();
              revokeOfflineUrls();
              await loadDownloaded();
              renderPlayer();
              notify("All phone downloads were removed");
            });
          updateDownloadButtons();
        } catch (e) {
          console.error(e);
          list.innerHTML =
            '<div class="empty">Downloaded broadcasts could not be read from this phone.</div>';
        }
      }

      function canonicalId(e) {
        return Number(e?.canonicalBroadcastId || e?.id || 0);
      }
      function episodeCard(e) {
        const id = canonicalId(e), state = downloadState(id), repair = state === "repair",
          downloaded = Boolean(e.downloaded || downloadedRecords.has(id)),
          dateValue = String(e.date || (e.airDate ? String(e.airDate).slice(0, 10) : "")),
          parsedDate = dateValue ? new Date(/^\d{4}-\d{2}-\d{2}$/.test(dateValue) ? dateValue + "T12:00:00" : dateValue) : null,
          validDate = parsedDate && !Number.isNaN(parsedDate.valueOf()),
          dateDay = validDate ? parsedDate.toLocaleDateString(undefined, { day:"2-digit" }) : "--",
          dateRest = validDate ? parsedDate.toLocaleDateString(undefined, { month:"short", year:"numeric" }) : "Date unknown",
          progressPercent = Math.max(0, Math.min(100, Number(e.progressPercent || 0))),
          durationMinutes = Math.round(Number(e.durationMs || 0) / 60000),
          statusText = String(e.status || (progressPercent > 0 ? "In progress" : "Not started")),
          sourceText = repair ? "Download needs repair"
            : downloaded ? (e.downloadSize ? `Offline · ${fmtBytes(e.downloadSize)}` : "Offline on this device")
            : "Server library",
          metadata = [dateValue, durationMinutes > 0 ? `${durationMinutes} min` : "", statusText, sourceText].filter(Boolean).join(" · "),
          title = String(e.title || "Broadcast"), showName = String(e.show || "Radio Vault"),
          playLabel = repair ? "Repair to listen" : progressPercent > 0 ? "Resume" : "Play",
          favouriteLabel = e.favourite ? "Remove from favourites" : "Add to favourites",
          compactDownload = `<button type="button" class="libraryIconButton libraryDownloadAction libraryCompactDownload${state === "downloaded" ? " dangerButton" : ""}" data-download="${id}" data-download-state="${state}" aria-label="${esc(downloadActionLabel(state))}" title="${esc(downloadActionLabel(state))}">${downloadActionIcon(state)}<span class="srOnly" data-download-label>${esc(downloadActionLabel(state))}</span></button>`,
          primaryAction = repair
            ? `<button type="button" class="libraryPrimaryAction libraryCompactDownload" data-download="${id}" data-download-state="repair" aria-label="${playLabel}" title="${playLabel}">${downloadActionIcon("repair")}<span class="srOnly" data-download-label>${playLabel}</span></button>`
            : `<button type="button" class="libraryPrimaryAction" data-play="${id}" data-position="${Number(e.positionMs || 0)}" aria-label="${playLabel} ${esc(title)}" title="${playLabel}"><svg class="libraryActionGlyph playGlyph" viewBox="0 0 24 24" aria-hidden="true"><path d="m8 5 11 7-11 7z"/></svg></button>`;
        return `<article class="episode" data-canonical-broadcast-id="${id}">${primaryAction}<div class="libraryDate" aria-label="${esc(dateValue || "Date unknown")}"><strong>${esc(dateDay)}</strong>${esc(dateRest)}</div><button type="button" class="libraryRowCopy" data-info="${id}" aria-label="Open broadcast information for ${esc(title)}"><div class="libraryRowShow">${esc(showName)}</div><div class="libraryRowTitle">${esc(title)}</div><div class="libraryRowMeta">${esc(metadata)}</div></button><div class="libraryProgressColumn"><span>${progressPercent > 0 ? `${Math.round(progressPercent)}% listened` : esc(statusText)}</span><div class="libraryProgressTrack"><span style="width:${progressPercent}%"></span></div></div><div class="libraryRowActions">${compactDownload}<button type="button" class="libraryIconButton libraryFavouriteAction${e.favourite ? " favourite" : ""}" data-favourite="${id}" data-value="${!e.favourite}" aria-label="${favouriteLabel}" title="${favouriteLabel}"><svg class="libraryActionGlyph" viewBox="0 0 24 24" aria-hidden="true"><path fill="${e.favourite ? "currentColor" : "none"}" d="m12 20-6.8-6.2C2 10.8 3.7 6 7.5 6c1.9 0 3.5 1.1 4.5 2.7C13 7.1 14.6 6 16.5 6c3.8 0 5.5 4.8 2.3 7.8z"/></svg></button><details class="libraryOverflow"><summary aria-label="More actions for ${esc(title)}" title="More actions"><svg class="libraryActionGlyph" viewBox="0 0 24 24" aria-hidden="true"><circle cx="5" cy="12" r="1" fill="currentColor" stroke="none"/><circle cx="12" cy="12" r="1" fill="currentColor" stroke="none"/><circle cx="19" cy="12" r="1" fill="currentColor" stroke="none"/></svg></summary><div class="libraryOverflowMenu"><button type="button" data-info="${id}">Broadcast information</button><button type="button" data-queue="${id}">Add to queue</button><button type="button" data-played="${id}" data-value="${e.status !== "Completed"}">${e.status === "Completed" ? "Mark as unlistened" : "Mark as listened"}</button></div></details></div></article>`;
      }
      function dashboardEpisodeCard(e) {
        const id = canonicalId(e), art = episodeArt(e), repair=downloadState(id)==="repair";
        return `<article class="dashboardEpisode" data-canonical-broadcast-id="${id}"><button class="rvMiniText" data-info="${id}" aria-label="Open broadcast info">${art ? `<img src="${art}" alt="">` : '<span class="artPlaceholder"></span>'}</button><button class="rvMiniText dashboardEpisodeText" data-info="${id}"><div class="show">${esc(e.show)}</div><div class="dashboardEpisodeTitle">${esc(e.title)}</div><div class="dashboardEpisodeMeta">${esc(e.date || (e.airDate ? String(e.airDate).slice(0, 10) : ""))}${repair ? " · needs repair" : e.progressPercent > 0 ? ` · ${e.progressPercent}% listened` : ""}</div></button><button class="dashboardPlay" ${repair ? `data-download="${id}"` : `data-play="${id}" data-position="${e.positionMs || 0}"`} aria-label="${repair ? "Repair download" : e.progressPercent > 0 ? "Resume" : "Play"}">${repair ? "↻" : "▶"}</button></article>`;
      }
      function dashboardSection(title, libraryTarget, episodes, emptyText, offlineTarget = false) {
        const target = offlineTarget
          ? `data-open-section="${libraryTarget}"`
          : `data-open-library-view="${libraryTarget}"`;
        return `<section class="dashboardSection"><div class="dashboardSectionHead"><h3>${esc(title)}</h3><button ${target}>View all</button></div><div class="dashboardGrid">${episodes.length ? episodes.map(dashboardEpisodeCard).join("") : `<div class="empty" style="padding:24px 12px">${esc(emptyText)}</div>`}</div></section>`;
      }
      async function buildOfflineDashboard() {
        await refreshDownloadedIndex();
        const progressById = new Map(
          (await dbAll("progress")).map((x) => [Number(x.episodeId), x]),
        );
        const episodes = [...downloadedRecords.values()].map((r) => {
          const p = progressById.get(Number(r.episodeId));
          const durationMs = Number(p?.durationMs || r.episode.durationMs || 0);
          const positionMs = Number(p?.positionMs ?? r.episode.positionMs ?? 0);
          const progressPercent = durationMs > 0
            ? Math.min(100, Math.round((positionMs * 100) / durationMs))
            : 0;
          return {
            ...r.episode,
            date: r.episode.airDate ? String(r.episode.airDate).slice(0, 10) : "",
            positionMs,
            durationMs,
            progressPercent,
            status: p?.completed ? "Completed" : positionMs > 0 ? "In Progress" : r.episode.status,
            downloaded: true,
            downloadSize: Number(r.size || 0),
            downloadedAt: Number(r.downloadedAt || 0),
            lastPlayedAt: Number(p?.updatedAt || 0),
            repairState: r.repairState || "",
          };
        });
        const totalBytes = episodes.reduce((n, e) => n + e.downloadSize, 0);
        const continuing = episodes
          .filter((e) => e.positionMs > 0 && e.progressPercent < 95 && e.status !== "Completed")
          .sort((a, b) => b.lastPlayedAt - a.lastPlayedAt)
          .slice(0, 6);
        const ready = episodes
          .filter((e) => e.positionMs <= 0 && e.status !== "Completed")
          .sort((a, b) => b.downloadedAt - a.downloadedAt)
          .slice(0, 6);
        const favourites = episodes
          .filter((e) => e.favourite)
          .sort((a, b) => b.downloadedAt - a.downloadedAt)
          .slice(0, 4);
        const recent = [...episodes]
          .sort((a, b) => b.downloadedAt - a.downloadedAt)
          .slice(0, 4);
        count.textContent = `${episodes.length} broadcast${episodes.length === 1 ? "" : "s"} available offline`;
        const hero = `<section class="dashboardHero"><div class="eyebrow">Offline on this phone</div><h3>Your downloaded Radio Vault</h3><p>Everything here is built from broadcasts and listening progress stored on this device. Progress will synchronise when Radio Vault reconnects.</p><div class="dashboardStats"><div class="dashboardStat"><strong>${episodes.length.toLocaleString()}</strong><span>downloads</span></div><div class="dashboardStat"><strong>${fmtBytes(totalBytes)}</strong><span>stored</span></div><div class="dashboardStat"><strong>${continuing.length.toLocaleString()}</strong><span>in progress</span></div></div><div class="actions"><button data-open-section="downloaded">Open Downloads</button></div></section>`;
        list.innerHTML =
          hero +
          dashboardSection("Continue offline", "downloaded", continuing, "Nothing downloaded is currently in progress.", true) +
          dashboardSection("Ready to listen", "downloaded", ready, "Download an unplayed broadcast while connected and it will appear here.", true) +
          dashboardSection("Favourites on this phone", "downloaded", favourites, "No downloaded favourites are stored on this phone.", true) +
          dashboardSection("Recently downloaded", "downloaded", recent, "No broadcasts have been downloaded to this phone.", true);
        updateDownloadButtons();
      }
      function dashboardPercent(e) {
        if (Number.isFinite(Number(e?.progressPercent))) return Math.max(0, Math.min(100, Number(e.progressPercent)));
        const duration = Number(e?.durationMs || 0), position = Number(e?.positionMs || 0);
        return duration > 0 ? Math.max(0, Math.min(100, Math.round(position * 100 / duration))) : 0;
      }
      function dashboardArt(e, className) {
        const art = episodeArt(e);
        return art ? `<img class="${className}" src="${art}" alt="">` : `<span class="${className}" aria-hidden="true"></span>`;
      }
      function dashboardNativeRow(e, simple = false) {
        const id = canonicalId(e), percent = dashboardPercent(e), date = e?.date || (e?.airDate ? String(e.airDate).slice(0, 10) : ""),
          parsed = date ? new Date(date + "T12:00:00") : null,
          day = parsed && !Number.isNaN(parsed.valueOf()) ? parsed.toLocaleDateString(undefined, { day:"2-digit" }) : "--",
          rest = parsed && !Number.isNaN(parsed.valueOf()) ? parsed.toLocaleDateString(undefined, { month:"short", year:"numeric" }) : "Date unknown";
        if (simple) return `<article class="nativeDashboardRow simple" data-canonical-broadcast-id="${id}"><div class="dashboardDateTile"><strong>${esc(day)}</strong>${esc(rest)}</div><button class="dashboardRowText" data-info="${id}"><div class="dashboardRowTitle">${esc(e?.title || e?.show || "Broadcast")}</div><div class="dashboardRowMeta">${esc(e?.show || "Radio Vault")}</div></button><button class="dashboardRowPlay" data-play="${id}" data-position="${Number(e?.positionMs || 0)}" aria-label="Play ${esc(e?.title || "broadcast")}">&#9654;</button></article>`;
        return `<article class="nativeDashboardRow" data-canonical-broadcast-id="${id}"><button class="dashboardRowPlay" data-play="${id}" data-position="${Number(e?.positionMs || 0)}" aria-label="Resume ${esc(e?.title || "broadcast")}">&#9654;</button><button class="dashboardRowText" data-info="${id}"><div class="dashboardRowTitle">${esc(e?.title || e?.show || "Broadcast")}</div><div class="dashboardRowMeta">${esc(e?.show || "Radio Vault")}</div></button><div class="dashboardRowProgress"><span>${percent}% listened</span><div class="dashboardProgress"><span style="width:${percent}%"></span></div></div></article>`;
      }
      function dashboardTagList(value) {
        return String(value || "").split(/[;,|]/).map((item) => item.trim()).filter(Boolean).slice(0, 4);
      }
      function dashboardOnThisDayCard(episodes) {
        episodes = episodes || [];
        if (!episodes.length) return '<div class="nativeCard nativeDashboardPanelBody"><div class="nativeDashboardEmpty">No broadcasts aired on this date.</div></div>';
        dashboardOnThisDayIndex = Math.max(0, Math.min(dashboardOnThisDayIndex, episodes.length - 1));
        const e = episodes[dashboardOnThisDayIndex], id = canonicalId(e), people = dashboardTagList(e.peopleSearchText), topics = dashboardTagList(e.topicSearchText), date = e.airDate ? String(e.airDate).slice(0, 10) : "Date unknown";
        return `<article class="nativeCard nativeCardRaised dashboardOnThisDay"><div class="dashboardOnThisDayMain">${dashboardArt(e,"dashboardOnThisDayArt")}<div><div style="display:grid;grid-template-columns:minmax(0,1fr) 40px;gap:10px"><div><div class="dashboardOnThisDayTitle">${esc(e.title || e.show || "Broadcast")}</div><div class="dashboardRowMeta">${esc(e.show || "Radio Vault")}</div><div class="dashboardRowMeta">${esc(date)}</div></div><button class="dashboardRowPlay" data-play="${id}" data-position="${Number(e.positionMs || 0)}" aria-label="Play ${esc(e.title || "broadcast")}">&#9654;</button></div>${people.length ? `<div class="eyebrow" style="margin-top:10px">People</div><div class="chips">${people.map((x)=>`<span class="chip">${esc(x)}</span>`).join("")}</div>` : ""}${topics.length ? `<div class="eyebrow" style="margin-top:8px">Topics</div><div class="chips">${topics.map((x)=>`<span class="chip">${esc(x)}</span>`).join("")}</div>` : ""}</div></div><div class="dashboardDots">${episodes.map((_,index)=>`<button class="dashboardDot ${index===dashboardOnThisDayIndex?"active":""}" data-dashboard-on-this-day="${index}" aria-label="Show broadcast ${index+1}"></button>`).join("")}</div></article>`;
      }
      function renderNativeConnectedDashboard(continuing, recent, favourites, onThisDay, unheard) {
        continuing = continuing || []; recent = recent || []; favourites = favourites || []; onThisDay = onThisDay || []; unheard = unheard || [];
        dashboardSnapshot = { continuing, recent, favourites, onThisDay, unheard };
        count.textContent = "";
        const featured = continuing[0] || null, featuredPercent = dashboardPercent(featured),
          surprisePool = unheard.length ? unheard : recent.filter((e) => Number(e.positionMs || 0) <= 0 && String(e.status || "").toLowerCase() !== "completed"),
          surprise = surprisePool.length ? surprisePool[Math.floor(Math.random() * surprisePool.length)] : null,
          continueCard = featured ? `<article class="nativeCard nativeCardRaised dashboardContinue"><div class="dashboardContinueHead"><h3>Continue listening</h3><p>Pick up exactly where you left off.</p></div><div class="dashboardContinueBody"><div><div class="dashboardContinueShow">${esc(featured.show || "Radio Vault")}</div><div class="dashboardContinueDate">${esc(featured.airDate ? String(featured.airDate).slice(0,10) : "Date unknown")}</div><div class="dashboardContinueTitle">${esc(featured.title || "Broadcast")}</div></div>${dashboardArt(featured,"dashboardContinueArt")}</div><div class="dashboardContinueFoot"><div class="dashboardProgressRow"><div class="dashboardProgress"><span style="width:${featuredPercent}%"></span></div><span>${featuredPercent}% listened</span></div><button class="dashboardResume" data-play="${canonicalId(featured)}" data-position="${Number(featured.positionMs || 0)}">Resume</button></div></article>` : '<article class="nativeCard nativeCardRaised dashboardContinue"><div class="nativeDashboardEmpty"><div><strong>Nothing waiting to resume</strong><div style="margin-top:6px">Choose something from the Library or let Radio Vault pick for you.</div></div></div></article>',
          stats = `<div class="nativeDashboardStats"><article class="nativeCard nativeDashboardStat broadcasts"><svg viewBox="0 0 24 24"><path d="M5 3h14v18H5zM9 8h6M9 12h6M9 16h6"/></svg><div><span>Broadcasts</span><strong>${archiveBroadcastCount.toLocaleString()}</strong></div></article><article class="nativeCard nativeDashboardStat progressing"><svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l4 2"/></svg><div><span>In progress</span><strong>${archiveContinueCount.toLocaleString()}</strong></div></article><article class="nativeCard nativeDashboardStat completed"><svg viewBox="0 0 24 24"><path d="m3 13 6 6L21 6"/></svg><div><span>Completed</span><strong>${archiveCompletedCount.toLocaleString()}</strong></div></article><article class="nativeCard nativeDashboardStat favourites"><svg viewBox="0 0 24 24"><path d="m12 20-6.8-6.2C2 10.8 3.7 6 7.5 6c1.9 0 3.5 1.1 4.5 2.7C13 7.1 14.6 6 16.5 6c3.8 0 5.5 4.8 2.3 7.8z"/></svg><div><span>Favourites</span><strong>${archiveFavouriteCount.toLocaleString()}</strong></div></article></div>`,
          surpriseCard = `<article class="nativeCard dashboardSurprise"><div><h3>Not sure what to play?</h3><p>Choose a random unheard broadcast.</p></div><button ${surprise ? `data-play="${canonicalId(surprise)}" data-position="0"` : "disabled"}>Surprise me</button></article>`,
          upNext = continuing.slice(1,5),
          upNextPanel = `<section class="nativeDashboardPanel"><h3 class="nativeSectionTitle">Up next</h3><div class="nativeCard nativeDashboardPanelBody">${upNext.length ? `<div class="nativeDashboardRows">${upNext.map((e)=>dashboardNativeRow(e)).join("")}</div>` : '<div class="nativeDashboardEmpty">Nothing else waiting to resume.</div>'}</div></section>`,
          onThisDayPanel = `<section class="nativeDashboardPanel"><h3 class="nativeSectionTitle">On this day</h3>${dashboardOnThisDayCard(onThisDay)}</section>`,
          recentPanel = `<section class="nativeDashboardPanel"><h3 class="nativeSectionTitle">Recently added</h3>${recent.length ? `<div class="nativeDashboardRows">${recent.slice(0,5).map((e)=>dashboardNativeRow(e,true)).join("")}</div>` : '<div class="nativeCard nativeDashboardEmpty">No recently added broadcasts.</div>'}</section>`,
          unheardPanel = `<section class="nativeDashboardPanel"><h3 class="nativeSectionTitle">Unheard broadcasts</h3>${unheard.length ? `<div class="nativeDashboardRows">${unheard.slice(0,5).map((e)=>dashboardNativeRow(e,true)).join("")}</div>` : '<div class="nativeCard nativeDashboardEmpty">You have heard everything in the Library.</div>'}</section>`;
        list.innerHTML = connectionBoundaryBanner() + `<div class="nativeDashboard"><div class="nativeDashboardTop">${continueCard}<div class="nativeDashboardSide">${surpriseCard}${stats}</div></div><div class="nativeDashboardPair">${upNextPanel}${onThisDayPanel}</div><div class="nativeDashboardPair">${recentPanel}${unheardPanel}</div></div>`;
        updateDownloadButtons();
      }
      function renderConnectedDashboard(continuing, recent, favourites, onThisDay, unheard) {
        return renderNativeConnectedDashboard(continuing, recent, favourites, onThisDay, unheard);
        count.textContent = `${archiveBroadcastCount.toLocaleString()} canonical broadcasts on ${serverInfo?.displayName || "Radio Vault"}`;
        const hero = `<section class="dashboardHero"><div class="eyebrow">Server · ${esc(serverInfo?.displayName || "Radio Vault")}</div><h3>Pick up where you left off</h3><p>The Dashboard, shared playback and queue were loaded from one bounded Radio Vault Web startup snapshot.</p><div class="dashboardStats"><div class="dashboardStat"><strong>${archiveBroadcastCount.toLocaleString()}</strong><span>broadcasts</span></div><div class="dashboardStat"><strong>${archiveShowCount.toLocaleString()}</strong><span>shows</span></div><div class="dashboardStat"><strong>${downloadedRecords.size.toLocaleString()}</strong><span>on this device</span></div></div><div class="actions"><button data-open-library-view="library">Open Library</button><button class="secondary" data-open-section="downloaded">Downloads</button></div></section>`;
        list.innerHTML =
          connectionBoundaryBanner() +
          hero +
          dashboardSection("Continue listening", "continue", continuing || [], "Nothing is currently in progress.") +
          dashboardSection("Recently added", "library", recent || [], "No recently added broadcasts.") +
          dashboardSection("Favourites", "favorites", favourites || [], "No favourites yet.") +
          dashboardSection("On this day", "onthisday", onThisDay || [], "No broadcasts from this date.");
        updateDownloadButtons();
      }
      async function loadDashboard() {
        updateViewChrome();
        const operation = beginViewLoad();
        count.textContent = "";
        try {
          if (!serverReachable) {
            await buildOfflineDashboard();
            return;
          }
          if (bootstrapDashboardPending && bootstrapState && !show.value) {
            bootstrapDashboardPending = false;
            renderConnectedDashboard(
              bootstrapState.continueListening,
              bootstrapState.recent,
              bootstrapState.favourites,
              bootstrapState.onThisDay,
              bootstrapState.unheard,
            );
            return;
          }
          list.innerHTML = loadingSkeleton(3);
          const showName = encodeURIComponent(show.value),
            paths = [
              `/broadcasts?limit=6&view=continue&show=${showName}&q=`,
              `/broadcasts?limit=6&view=recent&show=${showName}&q=`,
              `/broadcasts?limit=4&view=favorites&show=${showName}&q=`,
              `/broadcasts?limit=4&view=onthisday&show=${showName}&q=`,
              `/broadcasts?limit=5&view=recent&status=unplayed&show=${showName}&q=`,
            ],
            [continuing, recent, favourites, onThisDay, unheard] = await Promise.all(
              paths.map((path) => request(path, { signal: operation.signal })),
            );
          if (!isCurrentViewLoad(operation.generation)) return;
          serverReachable = true;
          applyConnectivityUi();
          renderConnectedDashboard(continuing.episodes, recent.episodes, favourites.episodes, onThisDay.episodes, unheard.episodes);
        } catch (error) {
          if (error.name === "AbortError") return;
          serverReachable = false;
          recordDiagnostic("dashboard", "Connected Dashboard fell back to device storage", { online: navigator.onLine });
          applyConnectivityUi();
          try {
            await buildOfflineDashboard();
          } catch (offlineError) {
            console.error(offlineError);
            list.innerHTML = connectionBoundaryBanner() +
              '<div class="empty">Radio Vault could not load the offline Dashboard.</div>';
          }
        }
      }
      function currentLibraryPageKey() {
        return JSON.stringify([libraryView,show.value,year.value,month.value,exactDate.value,statusFilter.value,search.value.trim()]);
      }
      async function loadLibrary(append = false) {
        updateViewChrome();
        const operation = beginViewLoad(), q = search.value.trim(), path = q ? "/search" : "/broadcasts", key=currentLibraryPageKey();
        if (!append || key !== libraryPageKey) {
          append=false; libraryPageKey=key; libraryLoadedCount=0; libraryTotalCount=0;
          list.innerHTML = loadingSkeleton(5);
        }
        const offset=append?libraryLoadedCount:0, started=performance.now();
        try {
          const d = await request(
            path + "?limit="+libraryPageSize+"&offset="+offset+"&view=" + encodeURIComponent(libraryView) +
              "&show=" + encodeURIComponent(show.value) + "&year=" + encodeURIComponent(year.value) +
              "&month=" + encodeURIComponent(month.value) + "&date=" + encodeURIComponent(exactDate.value) +
              "&status=" + encodeURIComponent(statusFilter.value) + "&q=" + encodeURIComponent(q),
            { signal: operation.signal },
          );
          if (!isCurrentViewLoad(operation.generation)) return;
          serverReachable = true; applyConnectivityUi();
          libraryTotalCount=Number(d.total ?? d.returned ?? 0);
          const cards=(d.episodes||[]).map(episodeCard).join("");
          if (!append) {
            list.innerHTML = connectionBoundaryBanner() + `<div id="libraryResults">${cards || '<div class="empty">Nothing matches these Library filters.</div>'}</div><div class="actions" id="libraryMoreHost"></div>`;
            libraryLoadedCount=Number(d.returned||0);
          } else {
            const host=$("libraryResults");
            host?.querySelector(".empty")?.remove();
            host?.insertAdjacentHTML("beforeend",cards);
            libraryLoadedCount+=Number(d.returned||0);
          }
          const moreHost=$("libraryMoreHost");
          if (moreHost) moreHost.innerHTML=d.hasMore?'<button id="loadMoreLibrary" type="button">Load more broadcasts</button>':"";
          $("loadMoreLibrary")?.addEventListener("click",()=>loadLibrary(true));
          count.textContent = `${libraryLoadedCount.toLocaleString()} of ${libraryTotalCount.toLocaleString()} canonical broadcast${libraryTotalCount===1?"":"s"} shown`;
          updateDownloadButtons();
          recordDiagnostic("library-page", append?"Additional Library page rendered":"Initial Library page rendered", {returned:Number(d.returned||0),total:libraryTotalCount,elapsedMs:Math.round(performance.now()-started)});
        } catch (error) {
          if (error.name === "AbortError") return;
          serverReachable = false; applyConnectivityUi();
          recordDiagnostic("library", "Library view preserved while server is unavailable", { downloaded: downloadedRecords.size });
          if (!append) list.innerHTML = connectionBoundaryBanner() + '<div class="empty">The complete Library is temporarily unavailable. Your filters and navigation have been preserved.</div>';
          else notify("More broadcasts could not be loaded. Your current results were preserved.",true);
        }
      }
      function wikiPageCard(page) {
        return `<button class="wikiPageCard" data-wiki-page="${esc(page.pageId)}"><div class="wikiPageMeta"><span>${esc(page.pageType || "Page")}</span><span>${Number(page.citationCount || 0)} sources</span></div><h3>${esc(page.title || "Untitled page")}</h3><p>${esc(page.summary || "Open this page to explore the archive.")}</p></button>`;
      }
      function wikiFindPage(target) {
        const query = String(target || "").replace(/^wiki:/i, "").trim(), slug = query.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
        return wikiPages.find((page) => String(page.title || "").toLowerCase() === query.toLowerCase() || String(page.slug || "").toLowerCase() === slug) || null;
      }
      function wikiRemember(entry) {
        const current = wikiHistory[wikiHistoryIndex];
        if (current && current.kind === entry.kind && String(current.pageId || "") === String(entry.pageId || "")) return;
        wikiHistory = wikiHistory.slice(0, wikiHistoryIndex + 1); wikiHistory.push(entry); wikiHistoryIndex = wikiHistory.length - 1;
      }
      function wikiNavBar() {
        const suggestions = wikiPages.slice(0, 500).map((page) => `<option value="${esc(page.title || "")}"></option>`).join("");
        return `<nav class="wikiNav"><button id="wikiBack" class="secondary" ${wikiHistoryIndex <= 0 ? "disabled" : ""} aria-label="Back">&larr;</button><button id="wikiForward" class="secondary" ${wikiHistoryIndex >= wikiHistory.length - 1 ? "disabled" : ""} aria-label="Forward">&rarr;</button><button id="wikiHome" class="ghost wide">Home</button><button id="wikiTimelineExplorer" class="ghost wide">Timeline</button><input id="wikiSearchInput" type="search" list="wikiSearchSuggestions" value="${esc(wikiQuery || "")}" placeholder="Search Explore" autocomplete="off"><datalist id="wikiSearchSuggestions">${suggestions}</datalist><button id="wikiSearchButton" class="searchButton">Search</button></nav>`;
      }
      function renderWikiBrowse(pages, query = "") {
        wikiCurrentPage = null;
        wikiImages = [];
        const shown = pages || [];
        count.textContent = `${shown.length.toLocaleString()} Explore page${shown.length === 1 ? "" : "s"}${query ? ` matching "${query}"` : ""}`;
        const types = [...new Set(wikiPages.map((page) => String(page.pageType || "")).filter(Boolean))].sort().map((value) => `<option value="${esc(value)}" ${wikiPageType === value ? "selected" : ""}>${esc(value)}</option>`).join("");
        const statuses = [...new Set(wikiPages.map((page) => String(page.status || "")).filter(Boolean))].sort().map((value) => `<option value="${esc(value)}" ${wikiPageStatus === value ? "selected" : ""}>${esc(value)}</option>`).join("");
        list.innerHTML = connectionBoundaryBanner() + wikiNavBar() + `<div class="wikiDashboard"><div class="wikiBrowseFilters"><label>Page type<select id="wikiPageTypeFilter"><option value="">All types</option>${types}</select></label><label>Status<select id="wikiPageStatusFilter"><option value="">All statuses</option>${statuses}</select></label></div>${shown.length ? `<div class="wikiPageGrid">${shown.map(wikiPageCard).join("")}</div>` : `<div class="empty">No Explore page matches ${query ? `"${esc(query)}"` : "these filters"}. A starter page can be created in the desktop client.</div>`}</div>`;
      }
      function renderWikiDashboard() {
        const overview = wikiOverview || {}, published = wikiPages.filter((page) => String(page.status || "").toLowerCase() === "published"), featured = (published.length ? published : wikiPages).slice(0, 8);
        const shows = wikiPages.filter((page) => page.pageType === "Show").slice(0, 8), people = wikiPages.filter((page) => page.pageType === "Person").slice(0, 8), topics = wikiPages.filter((page) => page.pageType === "Topic").slice(0, 8);
        const recent = wikiPages.slice().sort((a,b) => String(b.updatedAt || "").localeCompare(String(a.updatedAt || ""))).slice(0, 6), onThisDay = wikiHighlights?.onThisDay || [], eras = wikiHighlights?.eras || [];
        count.textContent = `${Number(overview.pageCount || wikiPages.length).toLocaleString()} Explore pages`;
        list.innerHTML = connectionBoundaryBanner() + wikiNavBar() + `<div class="wikiDashboard"><section class="wikiHero"><div class="eyebrow">THE STORY OF YOUR ARCHIVE</div><h3>Explore Radio Vault</h3><p>Follow programmes, people, eras and recurring stories, then open cited broadcasts without interrupting playback.</p><div class="wikiMetrics"><div class="wikiMetric"><strong>${Number(overview.pageCount || 0).toLocaleString()}</strong><span>pages</span></div><div class="wikiMetric"><strong>${Number(overview.sourceCount || 0).toLocaleString()}</strong><span>sources</span></div><div class="wikiMetric"><strong>${Number(overview.imageCount || 0).toLocaleString()}</strong><span>images</span></div><div class="wikiMetric"><strong>${Number(overview.timelineEventCount || 0).toLocaleString()}</strong><span>timeline events</span></div></div></section>${wikiTopicCleanup ? `<section class="wikiMissing"><div class="dashboardSectionHead"><div><h3>Canonical topics</h3><p>${esc(wikiTopicCleanup.summary || "Topic naming is being consolidated across the archive.")}</p></div>${Number(wikiTopicCleanup.automaticCount || 0) ? '<button id="wikiAutoTopics">Consolidate safe matches</button>' : ''}</div></section>` : ""}${featured.length ? `<section><div class="dashboardSectionHead"><div><h3>Featured starting points</h3><p>Developed pages from across the archive.</p></div><button id="wikiBrowseAll" class="secondary">Browse all</button></div><div class="wikiPageGrid">${featured.map(wikiPageCard).join("")}</div></section>` : `<div class="empty">Explore is ready for its first pages.</div>`}${eras.length ? `<section><h3>Explore by era</h3><div class="wikiEraRow">${eras.map((era) => `<button class="secondary" data-wiki-era="${Number(era.startYear)}"><strong>${Number(era.startYear)}s</strong><br><small>${Number(era.eventCount)} events</small></button>`).join("")}</div></section>` : ""}${onThisDay.length ? `<section><h3>On this date</h3><div class="wikiPageGrid">${onThisDay.slice(0,6).map((item) => `<button class="wikiPageCard" data-wiki-page="${esc(item.page.pageId)}"><div class="wikiPageMeta"><span>${esc(item.event.yearText || "History")}</span><span>${esc(item.page.title)}</span></div><h3>${esc(item.event.title)}</h3><p>${esc(item.event.summary || "Open the timeline event")}</p></button>`).join("")}</div></section>` : ""}${shows.length ? `<section><h3>Shows</h3><div class="wikiPageGrid">${shows.map(wikiPageCard).join("")}</div></section>` : ""}${people.length ? `<section><h3>People</h3><div class="wikiPageGrid">${people.map(wikiPageCard).join("")}</div></section>` : ""}${topics.length ? `<section><h3>Topics and stories</h3><div class="wikiPageGrid">${topics.map(wikiPageCard).join("")}</div></section>` : ""}${recent.length ? `<section><h3>Recently changed</h3><div class="wikiPageGrid">${recent.map(wikiPageCard).join("")}</div></section>` : ""}</div>`;
      }
      function wikiInline(text) {
        let raw = String(text || ""), tokens = [];
        const token = (html) => { const index = tokens.push(html) - 1; return `\uE000${index}\uE001`; };
        const linked = (target, label) => { const page = wikiFindPage(target), missing = page ? "" : " missing"; return token(`<button class="wikiLink ghost${missing}" data-wiki-entity="${esc(target.trim())}">${esc(label || target)}${page ? "" : " ?"}</button>`); };
        raw = raw.replace(/\[\[([^\]|]+)(?:\|([^\]]+))?\]\]/g, (_, target, label) => linked(target, label));
        raw = raw.replace(/\[([^\]]+)\]\((wiki:[^)]+)\)/g, (_, label, target) => linked(target.slice(5), label));
        raw = raw.replace(/\[([^\]]+)\]\((https?:\/\/[^)]+)\)/g, (_, label, url) => token(`<a href="${esc(url)}" target="_blank" rel="noreferrer">${esc(label)}</a>`));
        wikiPages.slice().sort((a,b) => String(b.title || "").length - String(a.title || "").length).forEach((page) => {
          const title = String(page.title || "").trim(); if (title.length < 3 || wikiCurrentPage && String(page.pageId) === String(wikiCurrentPage.pageId)) return;
          const escaped = title.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), expression = new RegExp(`(^|[^A-Za-z0-9])(${escaped})(?=$|[^A-Za-z0-9])`, "gi");
          raw = raw.replace(expression, (_, before, label) => `${before}${token(`<button class="wikiLink ghost" data-wiki-page="${esc(page.pageId)}">${esc(label)}</button>`)}`);
        });
        let value = esc(raw).replace(/\uE000(\d+)\uE001/g, (_, index) => tokens[Number(index)] || "");
        value = value.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>").replace(/\*([^*]+)\*/g, "<em>$1</em>");
        value = value.replace(/(^|\s)\[(\d+)\]/g, '$1<sup>[$2]</sup>');
        return value;
      }
      function wikiAnchor(value, index) {
        const base = String(value || "section").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
        return `wiki-${base || "section"}-${index}`;
      }
      function renderWikiMarkdown(markdown) {
        const lines = String(markdown || "").replace(/\r/g, "").split("\n"), headings = [], html = [];
        let paragraph = [], listType = "";
        const flushParagraph = () => { if (paragraph.length) { html.push(`<p>${wikiInline(paragraph.join(" "))}</p>`); paragraph = []; } };
        const closeList = () => { if (listType) { html.push(`</${listType}>`); listType = ""; } };
        lines.forEach((raw) => {
          const line = raw.trim();
          const heading = /^(#{1,4})\s+(.+)$/.exec(line);
          const bullet = /^[-*]\s+(.+)$/.exec(line), numbered = /^\d+[.)]\s+(.+)$/.exec(line);
          if (heading) {
            flushParagraph(); closeList();
            const level = Math.min(4, heading[1].length + 1), title = heading[2].replace(/[*_]/g, ""), anchor = wikiAnchor(title, headings.length);
            headings.push({ level, title, anchor }); html.push(`<h${level} id="${anchor}">${wikiInline(heading[2])}</h${level}>`); return;
          }
          if (bullet || numbered) {
            flushParagraph(); const wanted = bullet ? "ul" : "ol";
            if (listType !== wanted) { closeList(); listType = wanted; html.push(`<${wanted}>`); }
            html.push(`<li>${wikiInline((bullet || numbered)[1])}</li>`); return;
          }
          closeList();
          if (!line) { flushParagraph(); return; }
          if (line.startsWith("> ")) { flushParagraph(); html.push(`<blockquote>${wikiInline(line.slice(2))}</blockquote>`); return; }
          if (/^---+$/.test(line)) { flushParagraph(); html.push("<hr>"); return; }
          paragraph.push(line);
        });
        flushParagraph(); closeList();
        return { html: html.join(""), headings };
      }
      function wikiDate(value) {
        if (!value) return "Date not recorded";
        const parsed = new Date(value); return Number.isNaN(parsed.getTime()) ? esc(value) : parsed.toLocaleDateString(undefined, { year:"numeric", month:"short", day:"numeric" });
      }
      function renderWikiPage() {
        const page = wikiCurrentPage;
        if (!page) return renderWikiDashboard();
        const rendered = renderWikiMarkdown(page.bodyMarkdown), imageById = new Map(wikiImages.map((item) => [String(item.imageId), item]));
        const gallery = (page.images || []).map((link) => {
          const record = link.image || {}, content = imageById.get(String(link.imageId));
          if (!content?.content) return "";
          const date = record.capturedDate || record.representativeFrom || "";
          return `<figure class="wikiFigure"><img src="data:${esc(content.mediaType || record.mediaType || "image/jpeg")};base64,${content.content}" alt="${esc(record.altText || record.caption || "Wiki image")}"><figcaption>${esc(record.caption || record.originalFileName || "Archive image")}${date ? `<br>${wikiDate(date)}` : ""}${record.dateNotes ? ` - ${esc(record.dateNotes)}` : ""}</figcaption></figure>`;
        }).filter(Boolean).join("");
        const timeline = (page.timeline || []).map((event) => `<article class="wikiTimelineEvent"><div class="eyebrow">${esc(event.dateDisplay || (event.startDate ? String(event.startDate).slice(0,10) : "Timeline"))}</div><h4>${esc(event.title || "Event")}</h4>${event.summary ? `<p class="muted">${esc(event.summary)}</p>` : ""}${(event.broadcasts || []).length ? `<div class="actions">${event.broadcasts.map((link) => `<button data-info="${Number(link.episodeId)}">${esc(link.label || "Open broadcast information")}</button>`).join("")}</div>` : ""}</article>`).join("");
        const sources = (page.citations || []).slice().sort((a,b) => Number(a.ordinal || 0)-Number(b.ordinal || 0)).map((citation) => {
          const source = citation.source || {}, title = source.title || citation.note || "Archive source";
          return `<li id="wiki-source-${Number(citation.ordinal || 0)}"><strong>[${Number(citation.ordinal || 0)}]</strong> ${source.url ? `<a href="${esc(source.url)}" target="_blank" rel="noreferrer">${esc(title)}</a>` : esc(title)}${source.author ? ` by ${esc(source.author)}` : ""}${source.publisher ? ` - ${esc(source.publisher)}` : ""}${source.publishedDate ? ` (${wikiDate(source.publishedDate)})` : ""}${source.locator ? `<br><small>${esc(source.locator)}</small>` : ""}${citation.note ? `<br>${esc(citation.note)}` : ""}${citation.quotedText ? `<blockquote>${esc(citation.quotedText)}</blockquote>` : ""}${source.episodeId ? ` <button class="wikiLink ghost" data-info="${Number(source.episodeId)}">Open cited broadcast</button>` : ""}</li>`;
        }).join("");
        const contents = rendered.headings.length ? `<aside class="wikiContents"><h3>Contents</h3>${rendered.headings.map((heading) => `<a href="#${heading.anchor}">${esc(heading.title)}</a>`).join("")}</aside>` : "";
        const nav = wikiNavigation || {}, related = nav.relatedPages || [], backlinks = nav.backlinks || [], missing = nav.missingLinks || [];
        const relatedHtml = related.length || backlinks.length ? `<div class="wikiRelated">${related.length ? `<section><h3>Related pages</h3><div class="chips">${related.map((item) => `<button class="chip" data-wiki-page="${esc(item.pageId)}">${esc(item.title)}</button>`).join("")}</div></section>` : ""}${backlinks.length ? `<section><h3>Pages that mention this</h3><div class="chips">${backlinks.map((item) => `<button class="chip" data-wiki-page="${esc(item.pageId)}">${esc(item.title)}</button>`).join("")}</div></section>` : ""}</div>` : "";
        const missingHtml = missing.length ? `<div class="wikiMissing"><strong>Pages still needed</strong><p class="muted">These links do not have a matching page yet.</p><div class="chips">${missing.map((item) => `<button class="chip" data-wiki-entity="${esc(item.target)}">${esc(item.label || item.target)} ?</button>`).join("")}</div></div>` : "";
        const aliases = (page.aliases || []).length ? page.aliases.join(", ") : "None recorded", years = (page.timeline || []).map((item) => item.startDate ? Number(String(item.startDate).slice(0,4)) : 0).filter(Boolean);
        const range = years.length ? (Math.min(...years) === Math.max(...years) ? String(Math.min(...years)) : `${Math.min(...years)}-${Math.max(...years)}`) : "No dated events";
        const infobox = `<div class="wikiInfobox"><div class="wikiInfoCell"><strong>TYPE</strong>${esc(page.pageType || "Page")}</div><div class="wikiInfoCell"><strong>ALIASES</strong>${esc(aliases)}</div><div class="wikiInfoCell"><strong>EVIDENCE</strong>${Number(page.citations?.length || 0)} sources | ${Number(page.images?.length || 0)} images</div><div class="wikiInfoCell"><strong>TIMELINE</strong>${esc(range)}</div></div>`;
        count.textContent = `${page.pageType || "Wiki page"} | ${Number(page.citations?.length || 0)} sources | revision ${Number(page.revision || 0)}`;
        list.innerHTML = connectionBoundaryBanner() + wikiNavBar() + `<div class="wikiReader"><article class="wikiArticle"><header class="wikiArticleHeader"><div class="eyebrow">${esc(page.pageType || "Wiki")}</div><h2>${esc(page.title || "Untitled page")}</h2>${page.summary ? `<p>${esc(page.summary)}</p>` : ""}<div class="wikiPageMeta"><span>${esc(page.status || "Draft")}</span><span>Updated ${wikiDate(page.updatedAt)}</span></div></header>${infobox}${gallery ? `<div class="wikiGallery">${gallery}</div>` : ""}<div class="wikiMarkdown">${rendered.html || '<p class="muted">This page has no article text yet.</p>'}</div>${missingHtml}${relatedHtml}${timeline ? `<section><h2>Timeline</h2><div class="wikiTimeline">${timeline}</div></section>` : ""}${sources ? `<section class="wikiSources"><h2>References</h2><ol>${sources}</ol></section>` : ""}</article>${contents}</div>`;
      }
      async function openWikiPage(pageId, remember = true) {
        if (!pageId) return;
        list.innerHTML = loadingSkeleton(4); count.textContent = "Opening Explore page...";
        const page = await clientPost("wiki", "page", { pageId });
        if (!page) throw new Error("That Explore page could not be found.");
        wikiCurrentPage = page;
        wikiQuery = "";
        wikiNavigation = await clientPost("wiki", "navigation", { pageId }).catch(() => null);
        wikiImages = (await Promise.all((page.images || []).map((link) => clientPost("wiki", "image", { imageId:link.imageId }).catch(() => null)))).filter(Boolean);
        if (remember) wikiRemember({ kind:"page", pageId });
        renderWikiPage();
      }
      async function openWikiEntity(entity) {
        const query = String(entity || "").replace(/^wiki:/i, "").trim();
        if (!query) return;
        if (view !== "wiki") { view = "wiki"; navMode = "wiki"; updateViewChrome(); }
        const matches = await clientPost("wiki", "browse", { search:query, pageType:"", status:"", limit:100 }) || [];
        const normalized = query.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
        const exact = matches.find((page) => String(page.title || "").toLowerCase() === query.toLowerCase() || String(page.slug || "").toLowerCase() === normalized);
        if (exact) return openWikiPage(exact.pageId);
        wikiQuery = query; wikiPageType = ""; wikiPageStatus = "";
        wikiRemember({ kind:"browse", query, pageType:"", status:"" });
        renderWikiBrowse(matches, query);
      }
      function showWikiDashboard(remember = true) {
        wikiQuery = ""; wikiCurrentPage = null; wikiNavigation = null;
        if (remember) wikiRemember({ kind:"dashboard" });
        renderWikiDashboard();
      }
      async function showWikiTimelineExplorer(showId = "", year = 0, remember = true) {
        if (!wikiTimelineShows.length) wikiTimelineShows = await clientPost("wiki", "timeline-shows", {}) || [];
        const chosen = wikiTimelineShows.find((item) => String(item.page?.pageId) === String(showId)) || wikiTimelineShows[0];
        if (!chosen) { list.innerHTML = connectionBoundaryBanner() + wikiNavBar() + '<div class="empty">Add dated events to a Show page to begin exploring its timeline.</div>'; return; }
        wikiTimelinePage = await clientPost("wiki", "page", { pageId:chosen.page.pageId });
        const events = (wikiTimelinePage?.timeline || []).slice().sort((a,b) => String(a.startDate || "9999").localeCompare(String(b.startDate || "9999")));
        if (remember) wikiRemember({ kind:"timeline", pageId:chosen.page.pageId, year:0 });
        count.textContent = `${chosen.page.title} | ${events.length} timeline events`;
        const options = wikiTimelineShows.map((item) => `<option value="${esc(item.page.pageId)}" ${String(item.page.pageId) === String(chosen.page.pageId) ? "selected" : ""}>${esc(item.page.title)} - ${esc(item.summary || "")}</option>`).join("");
        const cards = events.map((event) => `<article class="wikiTimelineCard"><div class="year">${esc(event.yearText || (event.startDate ? String(event.startDate).slice(0,4) : "Undated"))}</div><div class="eyebrow">${esc(event.dateDisplay || "Timeline")}</div><h3>${esc(event.title || "Event")}</h3><p class="muted">${esc(event.summary || "")}</p>${(event.broadcasts || []).map((link) => `<button data-info="${Number(link.episodeId)}">${esc(link.label || "Open broadcast information")}</button>`).join("")}<button class="ghost" data-wiki-page="${esc(chosen.page.pageId)}">Open full article</button></article>`).join("");
        list.innerHTML = connectionBoundaryBanner() + wikiNavBar() + `<div class="wikiTimelineExplorer"><section class="wikiTimelineControls"><div class="eyebrow">INTERACTIVE HISTORY</div><h2>Timeline Explorer</h2><p class="muted">Choose a show, then scroll smoothly down through its complete history. Broadcast and Moment links open at the exact preserved point.</p><select id="wikiTimelineShowSelect">${options}</select></section><div class="wikiTimelineCards">${cards || '<div class="empty">This show has no dated timeline events yet.</div>'}</div></div>`;
      }
      async function navigateWikiHistory(direction) {
        const next = wikiHistoryIndex + direction; if (next < 0 || next >= wikiHistory.length) return;
        wikiHistoryIndex = next; const entry = wikiHistory[wikiHistoryIndex];
        if (entry.kind === "page") return openWikiPage(entry.pageId, false);
        if (entry.kind === "timeline") return showWikiTimelineExplorer(entry.pageId, entry.year, false);
        if (entry.kind === "browse") { wikiQuery = entry.query || ""; wikiPageType = entry.pageType || ""; wikiPageStatus = entry.status || ""; const pages = await clientPost("wiki", "browse", { search:wikiQuery, pageType:wikiPageType, status:wikiPageStatus, limit:500 }) || []; return renderWikiBrowse(pages, wikiQuery); }
        showWikiDashboard(false);
      }
      async function loadWiki(force = false) {
        list.innerHTML = loadingSkeleton(5); count.textContent = "Loading Explore...";
        try {
          if (force || !wikiOverview) wikiOverview = await clientPost("wiki", "overview", {});
          if (force || !wikiPages.length) wikiPages = await clientPost("wiki", "browse", { search:"", pageType:"", status:"", limit:5000 }) || [];
          const today = new Date();
          if (force || !wikiHighlights) wikiHighlights = await clientPost("wiki", "dashboard-highlights", { month:today.getMonth()+1, day:today.getDate() }).catch(() => ({ onThisDay:[], eras:[] }));
          if (force || !wikiTimelineShows.length) wikiTimelineShows = await clientPost("wiki", "timeline-shows", {}).catch(() => []);
          wikiTopicCleanup = await clientPost("wiki", "topic-audit", {}).catch(() => null);
          wikiHistory = []; wikiHistoryIndex = -1; showWikiDashboard(true);
        } catch (error) { list.innerHTML = connectionBoundaryBanner() + `<div class="empty">${esc(error.message || "Explore could not be loaded.")}</div>`; }
      }
      async function runWikiSearch() {
        const input = document.getElementById("wikiSearchInput"), query = String(input?.value || "").trim();
        wikiPageType = String(document.getElementById("wikiPageTypeFilter")?.value || wikiPageType || "");
        wikiPageStatus = String(document.getElementById("wikiPageStatusFilter")?.value || wikiPageStatus || "");
        if (!query && !wikiPageType && !wikiPageStatus) { showWikiDashboard(true); return; }
        const matches = await clientPost("wiki", "browse", { search:query, pageType:wikiPageType, status:wikiPageStatus, limit:500 }) || [];
        wikiQuery = query;
        wikiRemember({ kind:"browse", query, pageType:wikiPageType, status:wikiPageStatus });
        renderWikiBrowse(matches, query);
      }
      async function load() {
        updateViewChrome();
        if (view === "dashboard") return loadDashboard();
        if (view === "library") return loadLibrary();
        if (view === "moments") return loadMoments();
        if (view === "transcripts") return loadTranscripts();
        if (view === "research") return loadResearch();
        if (view === "wiki") return loadWiki();
        if (view === "queue") return loadQueue();
        if (view === "health") return loadHealth();
        if (view === "settings") return loadSettings();
        return loadDownloaded();
      }
      async function loadShows() {
        const records = [...downloadedRecords.values()],
          groups = new Map(),
          years = new Set(),
          previousShow = show.value || show.dataset.restoreValue || "",
          previousYear = year.value || year.dataset.restoreValue || "";
        for (const record of records) {
          const name = String(record.episode.show || "").trim();
          if (name) groups.set(name, (groups.get(name) || 0) + 1);
          const value = Number(String(record.episode.airDate || "").slice(0, 4));
          if (value >= 1900) years.add(value);
        }
        archiveShowCount = groups.size;
        if (!archiveBroadcastCount) archiveBroadcastCount = records.length;
        renderSidebarShows([...groups.entries()].sort((a, b) => a[0].localeCompare(b[0])).map(([name, count]) => ({ name, count })));
        show.innerHTML = '<option value="">All shows</option>' + [...groups.entries()]
          .sort((a, b) => a[0].localeCompare(b[0]))
          .map((item) => `<option value="${esc(item[0])}">${esc(item[0])} (${item[1]} downloaded)</option>`)
          .join("");
        year.innerHTML = '<option value="">All years</option>' + [...years]
          .sort((a, b) => b - a)
          .map((value) => `<option value="${value}">${value}</option>`)
          .join("");
        if ([...show.options].some((option) => option.value === previousShow)) show.value = previousShow;
        if ([...year.options].some((option) => option.value === previousYear)) year.value = previousYear;
        delete show.dataset.restoreValue;
        delete year.dataset.restoreValue;
      }
      function renderMoments(items) {
        items = items || [];
        count.textContent = `${items.length.toLocaleString()} saved Moment${items.length === 1 ? "" : "s"}`;
        list.innerHTML = connectionBoundaryBanner() + `<div class="momentsWorkspace">${items.length ? `<div class="momentCards">${items.map((item) => `<article class="momentCard"><div class="momentCardHead"><div><div class="show">${esc(item.show || "Unknown show")}</div><h3>${esc(item.title || "Moment")}</h3><div class="muted">${esc(item.episodeTitle || "Broadcast")} · ${esc(item.airDate ? String(item.airDate).slice(0,10) : "Date unknown")} · ${fmtMs(item.positionMs || 0)}</div></div><span class="sourcePill">${esc(item.createdAt ? new Date(item.createdAt).toLocaleDateString() : "Saved")}</span></div>${item.notes ? `<p class="muted">${esc(item.notes)}</p>` : ""}<div class="actions"><button data-play="${Number(item.episodeId)}" data-seek="${Math.floor(Number(item.positionMs || 0) / 1000)}">Play Moment</button><button class="secondary" data-info="${Number(item.episodeId)}">Broadcast info</button><button class="secondary" data-edit-moment="${Number(item.id)}" data-moment-title="${esc(item.title || "Moment")}" data-moment-notes="${esc(item.notes || "")}">Edit</button><button class="ghost" data-delete-moment="${Number(item.id)}" data-moment-episode="${Number(item.episodeId)}">Delete</button></div></article>`).join("")}</div>` : '<div class="empty">No Moments have been saved yet. Use the bookmark button while listening to remember an exact point.</div>'}</div>`;
      }
      async function loadMoments() {
        search.value = "";
        list.innerHTML = loadingSkeleton(4);
        try {
          const data = await request("/moments");
          renderMoments(data.moments || []);
        } catch (error) {
          list.innerHTML = connectionBoundaryBanner() + `<div class="empty">Moments could not be loaded.<div class="muted" style="margin-top:7px">${esc(error.message || "Try again when the server is connected.")}</div></div>`;
        }
      }
      function researchTabs() {
        const tabs = [["overview","Overview"],["records","Knowledge records"],["undated","Undated broadcasts"],["coverage","Coverage"],["imports","Import history"],["sources","Sources"],["packs","Knowledge database"],["transcription","Transcription studio"]];
        return `<div class="workspaceTabs">${tabs.map(([key,label]) => `<button class="${researchSection === key ? "active" : ""}" data-research-section="${key}">${label}</button>`).join("")}</div>`;
      }
      function researchMetric(value, label) {
        return `<div class="researchMetric"><strong>${Number(value || 0).toLocaleString()}</strong><span>${esc(label)}</span></div>`;
      }
      function researchImportProgressCard(job) {
        if (!job) return "";
        const percent = Math.max(0, Math.min(100, Number(job.percent || 0))),
          countLabel = Number(job.total || 0) > 0 ? `${Number(job.current || 0).toLocaleString()} of ${Number(job.total || 0).toLocaleString()}` : "",
          failed = String(job.state || "").toLowerCase() === "failed";
        return `<section class="settingsCard"><div class="settingsCardHead"><div><h3>${failed ? "Knowledge import failed" : "Importing Knowledge Database"}</h3><div class="muted">${esc(job.error || job.message || "Preparing the server-owned import job…")}</div></div><strong>${percent.toFixed(0)}%</strong></div><div class="runProgress"><span style="width:${percent}%"></span></div><div class="runMeta"><span>${esc(countLabel)}</span><span>${esc(job.state || "Pending")}</span></div>${job.canCancel ? '<div class="actions"><button class="secondary" id="cancelResearchPack">Cancel import</button></div>' : ""}</section>`;
      }
      async function pollResearchPackImport(sessionId, initialJob) {
        researchImportJob = initialJob;
        renderResearch();
        while (["queued","running","pending"].includes(String(researchImportJob?.state || "").toLowerCase())) {
          await new Promise((resolve) => setTimeout(resolve, 750));
          const response = await request("/federation/research-packs/import/status", { method:"POST", headers:{"Content-Type":"application/json"}, body:JSON.stringify({sessionId}), timeoutMs:20000 });
          researchImportJob = response.result;
          renderResearch();
        }
        const finalJob = researchImportJob, state = String(finalJob?.state || "").toLowerCase();
        if (state === "completed") {
          const result = finalJob.result || {};
          notify(`Knowledge Database imported · ${Number(result.updated || 0)} updated · ${Number(result.researchRecordsStored || 0)} Knowledge records`);
          researchPackPreview = null; researchImportJob = null; researchSnapshot = null; researchSection = "imports"; await loadResearch();
          return;
        }
        if (state === "cancelled") {
          notify("Knowledge Database import cancelled; no partial changes were kept.");
          researchPackPreview = null; researchImportJob = null; renderResearch();
          return;
        }
        researchPackPreview = null;
        renderResearch();
        throw new Error(finalJob?.error || finalJob?.message || "The Knowledge Database could not be imported.");
      }
      function researchRecordCard(record) {
        return `<button class="researchRecord" data-research-record="${Number(record.id)}"><div class="researchRecordHead"><div><div class="show">${esc(record.show || "Unknown show")}</div><h3>${esc(record.headline || record.broadcastId || "Research record")}</h3><div class="muted">${esc(record.broadcastDate ? String(record.broadcastDate).slice(0,10) : "Date unknown")} · ${esc(record.existenceStatus || "Unknown status")} · ${Number(record.confidence || 0)}% confidence</div></div><span class="sourcePill">${Number(record.sourceCount || 0)} sources</span></div>${record.summary ? `<p class="muted">${esc(record.summary)}</p>` : ""}<div class="researchBadges">${record.needsReview ? '<span class="researchBadge attention">Needs review</span>' : '<span class="researchBadge">Reviewed</span>'}${record.conflictCount ? `<span class="researchBadge attention">${Number(record.conflictCount)} conflicts</span>` : ""}${record.pendingDecisionCount ? `<span class="researchBadge attention">${Number(record.pendingDecisionCount)} decisions</span>` : ""}<span class="researchBadge">${Number(record.peopleCount || 0)} people</span><span class="researchBadge">${Number(record.topicCount || 0)} topics</span></div></button>`;
      }
      function renderResearch() {
        const snapshot = researchSnapshot || {}, overview = snapshot.overview || {}, records = snapshot.records || [];
        count.textContent = `${Number(overview.totalResearchRecords || records.length).toLocaleString()} research records`;
        let content = "";
        if (researchSection === "overview") {
          const attention = records.filter((record) => record.needsReview || record.conflictCount || record.pendingDecisionCount).slice(0,12);
          content = `<div class="researchMetrics">${researchMetric(overview.totalResearchRecords,"research records")}${researchMetric(overview.attachedRecords,"attached to audio")}${researchMetric(overview.needsReview,"need review")}${researchMetric(overview.conflictedRecords,"with conflicts")}</div><section class="settingsCard"><div class="settingsCardHead"><div><h3>Research attention</h3><div class="muted">The highest-priority records from the authoritative server workspace.</div></div><button data-research-section="records">Browse all records</button></div><div class="researchRecords">${attention.map(researchRecordCard).join("") || '<div class="empty">No research records currently need attention.</div>'}</div></section>`;
        } else if (researchSection === "records") {
          const query = researchQuery.trim().toLowerCase(), filtered = records.filter((record) => !query || [record.show,record.headline,record.summary,record.broadcastId,record.existenceStatus].some((value) => String(value || "").toLowerCase().includes(query)));
          content = `<div class="researchToolbar"><input id="researchSearch" type="search" value="${esc(researchQuery)}" placeholder="Search research records" autocomplete="off"><span class="muted">${filtered.length.toLocaleString()} shown</span></div><div class="researchRecords">${filtered.map(researchRecordCard).join("") || '<div class="empty">No research records match this search.</div>'}</div>`;
        } else if (researchSection === "undated") {
          content = `<div class="settingsCard"><div class="settingsCardHead"><div><h3>Undated broadcasts</h3><div class="muted">Assign only dates supported by filename or external research evidence.</div></div><span class="sourcePill">${researchUndated.length.toLocaleString()} unresolved</span></div><div class="researchRecords">${researchUndated.map((item) => `<article class="researchRecord"><div class="researchRecordHead"><div><div class="show">${esc(item.showName)}</div><h3>${esc(item.title || item.preferredFilename)}</h3><div class="muted">${esc(item.dateConfidence || "Unknown confidence")} · ${Number(item.fileCount || 0)} files</div></div></div>${item.parserEvidence ? `<p class="muted">${esc(item.parserEvidence)}</p>` : ""}<div class="actions"><input type="date" id="research-date-${Number(item.episodeId)}" value="${esc(item.proposedDate ? String(item.proposedDate).slice(0,10) : "")}" aria-label="Broadcast date"><button data-assign-research-date="${Number(item.episodeId)}">Assign date</button><button class="secondary" data-info="${Number(item.episodeId)}">Broadcast info</button></div></article>`).join("") || '<div class="empty">Every canonical broadcast currently has a date.</div>'}</div></div>`;
        } else if (researchSection === "coverage") {
          const shows = bootstrapState?.shows || [], coverage = researchCoverage, days = coverage?.days || [];
          content = `<div class="researchToolbar"><select id="researchCoverageShow"><option value="">Choose a show</option>${shows.map((item) => `<option value="${esc(item.name)}" ${item.name === researchCoverageShow ? "selected" : ""}>${esc(item.name)}</option>`).join("")}</select><span class="muted">Audio, research and known missing days from the server timeline.</span></div>${coverage ? `<section class="settingsCard"><div class="settingsCardHead"><div><h3>${esc(coverage.showName)}</h3><div class="muted">${esc(String(coverage.firstDate).slice(0,10))} to ${esc(String(coverage.lastDate).slice(0,10))} · ${days.length.toLocaleString()} dated days</div></div><div class="researchBadges"><span class="researchBadge">Audio</span><span class="researchBadge">Research underline</span><span class="researchBadge attention">Known missing</span></div></div><div class="researchCoverageGrid">${days.map((day) => `<button class="coverageDay ${day.hasAudio ? "audio" : ""} ${day.hasResearch ? "research" : ""} ${day.isKnownMissing ? "missing" : ""}" ${day.representativeEpisodeId ? `data-info="${Number(day.representativeEpisodeId)}"` : "disabled"} title="${esc(String(day.date).slice(0,10) + " · " + (day.missingFields || ""))}">${esc(String(day.date).slice(5,10))}</button>`).join("")}</div></section>` : '<div class="empty">Choose a show to view its Research coverage.</div>'}`;
        } else if (researchSection === "imports") {
          const imports = snapshot.imports || [];
          content = `<div class="researchRecords">${imports.map((item) => `<article class="researchRecord"><div class="researchRecordHead"><div><h3>${esc(item.packageName)}</h3><div class="muted">${esc(item.importedAt ? new Date(item.importedAt).toLocaleString() : "Import")} · ${esc(item.status || "Completed")}</div></div><span class="sourcePill">${Number(item.importedCount || 0)} records</span></div><div class="researchBadges"><span class="researchBadge">${Number(item.matchedCount || 0)} matched</span><span class="researchBadge">${Number(item.fieldsApplied || 0)} applied</span><span class="researchBadge">${Number(item.fieldsMerged || 0)} merged</span><span class="researchBadge">${Number(item.manualFieldsProtected || 0)} manual fields protected</span>${item.conflictCount ? `<span class="researchBadge attention">${Number(item.conflictCount)} conflicts</span>` : ""}</div></article>`).join("") || '<div class="empty">No deep research packs have been imported yet.</div>'}</div>`;
        } else if (researchSection === "sources") {
          const sources = snapshot.sources || [];
          content = `<div class="researchRecords">${sources.map((item) => `<article class="researchRecord"><div class="researchRecordHead"><div><h3>${esc(item.publisher || item.domain || "Research source")}</h3><div class="muted">${esc(item.domain || "")} · ${esc(item.sourceType || "Source")}</div></div><strong>${Number(item.averageConfidence || 0)}%</strong></div><div class="researchBadges"><span class="researchBadge">${Number(item.sourceCount || 0)} sources</span><span class="researchBadge">${Number(item.broadcastCount || 0)} broadcasts</span></div></article>`).join("") || '<div class="empty">No research sources have been indexed.</div>'}</div>`;
        } else {
          const preview = researchPackPreview, p = preview?.preview;
          content = `${researchImportProgressCard(researchImportJob)}<div class="packDrop"><div><h3>Import an Archive Knowledge Database</h3><div class="muted">One inspectable SQLite database carries Knowledge records, Explore pages, citations, images, timelines, transcripts and their stable archive links.</div></div><input id="researchPackFile" type="file" accept=".trvknowledge,application/vnd.radiovault.knowledge+sqlite3,application/x-sqlite3" ${researchImportJob ? "disabled" : ""}><div class="actions"><button id="previewResearchPack" ${researchImportJob ? "disabled" : ""}>Analyse selected database</button>${preview && !researchImportJob ? '<button class="secondary" id="cancelResearchPack">Cancel preview</button>' : ""}</div>${p && !researchImportJob ? `<div class="packPreview">${researchMetric(p.totalRecords,"knowledge records")}${researchMetric(p.wikiPageCount,"Explore pages")}${researchMetric(p.wikiImageCount,"images")}${researchMetric(p.transcriptCount,"transcripts")}${researchMetric(p.fieldsExpectedToApply,"fields to apply")}${researchMetric(p.fieldsExpectedToMerge,"fields to merge")}</div><div class="actions"><button id="applyResearchPack">Import this database</button></div>` : ""}</div><section class="settingsCard"><div><h3>Export the Archive Knowledge Database</h3><div class="muted">Every export includes all shows and all years, plus Explore content, images, timelines, citations, missing-broadcast knowledge and matching transcripts.</div></div><div class="actions"><button id="exportResearchPack">Export complete knowledge database</button></div></section>`;
        }
        list.innerHTML = connectionBoundaryBanner() + `<div class="researchWorkspace">${researchTabs()}${content}</div>`;
        const field = document.getElementById("researchSearch");
        if (field) field.addEventListener("input", () => { researchQuery = field.value; renderResearch(); document.getElementById("researchSearch")?.focus(); });
        const coverageShow = document.getElementById("researchCoverageShow");
        if (coverageShow) coverageShow.addEventListener("change", async () => {
          researchCoverageShow = coverageShow.value;
          researchCoverage = null;
          await loadResearch();
        });
      }
      async function loadResearch(force = false) {
        search.value = "";
        if (force) researchSnapshot = null;
        if (!researchSnapshot) list.innerHTML = loadingSkeleton(5);
        try {
          if (!researchSnapshot) researchSnapshot = (await request("/federation/research-workspace")).research;
          if (researchSection === "undated") researchUndated = (await request("/federation/research-workspace/undated")).broadcasts || [];
          if (researchSection === "coverage" && researchCoverageShow) researchCoverage = (await request("/federation/research-workspace/coverage/show/" + encodeURIComponent(researchCoverageShow))).coverage;
          renderResearch();
        } catch (error) {
          list.innerHTML = connectionBoundaryBanner() + `<div class="empty">The Knowledge workspace could not be loaded.<div class="muted" style="margin-top:7px">${esc(error.message || "Try again when the server is connected.")}</div></div>`;
        }
      }
      async function openResearchRecord(id) {
        const data = await request("/federation/research-workspace/" + Number(id)), details = data.record, record = details.record;
        detail.classList.add("open");
        focusOverlay(detail, $("detailBack"));
        detailBody.innerHTML = `<div class="hero"><div><div class="show">${esc(record.show)}</div><h2>${esc(record.headline || record.broadcastId)}</h2><div class="meta">${esc(record.broadcastDate ? String(record.broadcastDate).slice(0,10) : "Date unknown")} · ${Number(record.confidence || 0)}% confidence · ${esc(record.existenceStatus)}</div><div class="actions">${record.episodeId ? `<button data-info="${Number(record.episodeId)}">Open broadcast</button>` : ""}<button class="secondary" data-research-review="${Number(record.id)}" data-value="${!record.needsReview}">${record.needsReview ? "Mark reviewed" : "Flag for review"}</button></div></div></div>${record.summary ? `<div class="section"><h3>Summary</h3><div class="muted">${esc(record.summary)}</div></div>` : ""}${[...(details.hosts||[]),...(details.guests||[]),...(details.callers||[]),...(details.mentionedPeople||[])].length ? `<div class="section"><h3>People</h3><div class="chips">${[...(details.hosts||[]),...(details.guests||[]),...(details.callers||[]),...(details.mentionedPeople||[])].map((name) => `<span class="chip">${esc(name)}</span>`).join("")}</div></div>` : ""}${(details.topics||[]).length ? `<div class="section"><h3>Topics</h3><div class="chips">${details.topics.map((topic) => `<span class="chip">${esc(topic)}</span>`).join("")}</div></div>` : ""}<div class="section"><h3>Sources</h3>${(details.sources||[]).map((source) => `<div class="moment"><strong>${source.url ? `<a href="${esc(source.url)}" target="_blank" rel="noreferrer">${esc(source.title || source.publisher || source.url)}</a>` : esc(source.title || source.publisher)}</strong><div class="muted">${esc(source.publisher)} · ${Number(source.confidence || 0)}% confidence</div></div>`).join("") || '<div class="muted">No sources attached.</div>'}</div>${(details.conflicts||[]).length ? `<div class="section"><h3>Conflicts</h3>${details.conflicts.map((conflict) => `<div class="moment"><strong>${esc(conflict.fieldName)}</strong><div class="muted">Existing: ${esc(conflict.existingValue)} · Incoming: ${esc(conflict.incomingValue)} · ${esc(conflict.resolution)}</div></div>`).join("")}</div>` : ""}`;
      }
      async function previewResearchPackFile(file) {
        const response = await fetch(auth(api + "/federation/research-packs/import/preview"), { method:"POST", headers:{"Content-Type":"application/octet-stream","X-Radio-Vault-File-Name":encodeURIComponent(file.name)}, body:await file.arrayBuffer(), cache:"no-store" });
        const data = await response.json().catch(() => null);
        if (!response.ok) throw new Error(data?.error?.message || data?.message || "The research pack could not be analysed.");
        researchPackPreview = data.result;
        researchImportJob = null;
        renderResearch();
      }
      async function exportResearchPack() {
        const response = await fetch(auth(api + "/federation/research-packs/export"), { method:"POST", headers:{"Content-Type":"application/json"}, body:JSON.stringify({}), cache:"no-store" });
        if (!response.ok) { const data = await response.json().catch(() => null); throw new Error(data?.error?.message || "The research pack could not be exported."); }
        const blob = await response.blob(), disposition = response.headers.get("Content-Disposition") || "", matched = disposition.match(/filename\*=UTF-8''([^;]+)/i), fileName = matched ? decodeURIComponent(matched[1]) : "RadioVault-Archive.trvknowledge", link = document.createElement("a");
        link.href = URL.createObjectURL(blob); link.download = fileName; link.click(); setTimeout(() => URL.revokeObjectURL(link.href),1000);
        notify(`Knowledge database exported with ${response.headers.get("X-Radio-Vault-Transcript-Count") || 0} transcripts and ${response.headers.get("X-Radio-Vault-Wiki-Page-Count") || 0} Wiki pages`);
      }
      function renderSettings() {
        const settings = settingsSnapshot || {}, storage = settings.storage || {}, preservation = settings.preservation || {}, playback = settings.playback || {}, federation = federationSnapshot || {}, parity = paritySnapshot || {}, features = parity.features || serverInfo?.capabilities || [];
        count.textContent = serverInfo ? `Connected to ${connectedLabel()}` : "Server settings";
        list.innerHTML = connectionBoundaryBanner() + `<div class="settingsWorkspace"><div class="settingsMetrics"><div class="settingsMetric"><strong>${Number(storage.totalFiles || 0).toLocaleString()}</strong><span>archive files</span></div><div class="settingsMetric"><strong>${fmtBytes(storage.logicalBytes || 0)}</strong><span>logical archive size</span></div><div class="settingsMetric"><strong>${Number(settings.researchAttention || 0).toLocaleString()}</strong><span>research items needing attention</span></div><div class="settingsMetric"><strong>${Number(parity.availableCount || features.filter((item) => item.available).length).toLocaleString()}/${Number(parity.totalCount || features.length).toLocaleString()}</strong><span>remote-client features available</span></div></div><section class="settingsCard"><div class="settingsCardHead"><div><h3>Connected server</h3><div class="muted">${esc(serverInfo?.displayName || "Radio Vault Server")} · ${esc(serverInfo?.appVersion || "")} · API ${esc(serverInfo?.apiVersion || "v1")} · schema ${Number(serverInfo?.databaseSchemaVersion || 0)}</div></div><span class="sourcePill">${serverInfo?.secureAccess ? "Secure connection" : "Local connection"}</span></div><div class="actions"><button id="settingsReconnect">Reconnect and refresh</button><button class="secondary" id="settingsDiagnostics">Open diagnostics</button></div></section><section class="settingsCard"><div><h3>Playback controls</h3><div class="muted">These preferences are authoritative on the server and shared by every client.</div></div><div class="settingsFields"><label class="settingsField">Skip back (seconds)<input id="settingsSkipBack" type="number" min="1" max="120" value="${Number(playback.skipBackSeconds || 15)}"></label><label class="settingsField">Skip forward (seconds)<input id="settingsSkipForward" type="number" min="1" max="120" value="${Number(playback.skipForwardSeconds || 30)}"></label><label class="settingsField">Completion threshold (seconds)<input id="settingsCompletion" type="number" min="1" max="600" value="${Number(playback.completionThresholdSeconds || 60)}"></label></div><div class="actions"><button id="savePlaybackPreferences">Save playback settings</button></div></section><section class="settingsCard"><div class="settingsCardHead"><div><h3>Archive and preservation</h3><div class="muted">Database check: ${esc(settings.databaseQuickCheck || "unknown")} · latest backup ${esc(settings.latestBackupAt ? new Date(settings.latestBackupAt).toLocaleString() : "not available")}</div></div><button id="runLibraryScan">Scan Library now</button></div><div class="settingsMetrics"><div class="settingsMetric"><strong>${Number(storage.availableOffline || 0).toLocaleString()}</strong><span>locally available</span></div><div class="settingsMetric"><strong>${Number(storage.cloudOnly || 0).toLocaleString()}</strong><span>cloud-only</span></div><div class="settingsMetric"><strong>${Number(storage.missing || 0).toLocaleString()}</strong><span>missing files</span></div><div class="settingsMetric"><strong>${Number(preservation.fullHashes || 0).toLocaleString()}</strong><span>full preservation hashes</span></div></div><div class="researchRecords">${(settings.archiveFolders||[]).map((folder) => `<div class="researchRecord"><strong>${esc(folder.collectionName)}</strong><div class="muted">${esc(folder.path)} · ${folder.recursive ? "recursive" : "single folder"} · last scan ${esc(folder.lastScanAt ? new Date(folder.lastScanAt).toLocaleString() : "never")}</div></div>`).join("") || '<div class="muted">No archive folders are configured.</div>'}</div></section><section class="settingsCard"><div class="settingsCardHead"><div><h3>Remote clients and parity</h3><div class="muted">${federation.enabled ? `${Number(federation.pairedDesktopClients || 0)} paired desktop clients · discovery port ${Number(federation.discoveryPort || 0)}` : "Multi-Device Library Access is disabled in the server settings app."}</div></div><span class="sourcePill">Generation ${Number(serverInfo?.capabilityGeneration || parity.capabilityGeneration || 0)}</span></div><div class="capabilityGrid">${features.map((feature) => `<div class="capability ${feature.available ? "available" : ""}"><strong>${feature.available ? "Available" : "Unavailable"} · ${esc(feature.name)}</strong><div class="muted">${esc(feature.access || "read")} ${feature.notes ? "· "+esc(feature.notes) : ""}</div></div>`).join("")}</div></section></div>`;
      }
      async function loadSettings() {
        search.value = "";
        list.innerHTML = loadingSkeleton(5);
        try {
          const [settingsData,federationData,parityData] = await Promise.all([request("/federation/settings"),request("/federation/status"),request("/federation/parity")]);
          settingsSnapshot = settingsData.settings; federationSnapshot = federationData.federation; paritySnapshot = parityData.parity; renderSettings();
        } catch (error) {
          list.innerHTML = connectionBoundaryBanner() + `<div class="empty">Server settings could not be loaded.<div class="muted" style="margin-top:7px">${esc(error.message || "Try reconnecting to the server.")}</div></div>`;
        }
      }
      function renderQueue(queue) {
        queue = queue || [];
        count.textContent = queue.length + " queued broadcast" + (queue.length === 1 ? "" : "s");
        list.innerHTML = connectionBoundaryBanner() + (queue.length
          ? queue.map((item) => {
              const id = canonicalId(item.episode);
              return `<div class="queueItem" data-canonical-broadcast-id="${id}"><div class="queuePos">${item.position + 1}</div><div style="flex:1"><div class="show">${esc(item.episode.show)}</div><div class="title">${esc(item.episode.title)}</div><div class="actions"><button data-play="${id}" data-position="${item.episode.positionMs || 0}">Play here</button><button class="ghost" data-queue-remove="${item.queueId}">Remove</button><button class="ghost" data-queue-move="${item.queueId}" data-direction="-1">↑</button><button class="ghost" data-queue-move="${item.queueId}" data-direction="1">↓</button></div></div></div>`;
            }).join("")
          : '<div class="empty">The queue is empty.</div>');
        if (queue.length)
          list.insertAdjacentHTML("beforeend", '<div class="actions" style="justify-content:center"><button class="ghost" id="clearQueue">Clear queue</button></div>');
      }
      async function loadQueue() {
        search.value = "";
        if (bootstrapQueuePending && bootstrapState) {
          bootstrapQueuePending = false;
          renderQueue(bootstrapState.queue);
          return;
        }
        list.innerHTML = loadingSkeleton(3);
        try {
          const d = await request("/queue");
          renderQueue(d.queue);
        } catch {
          list.innerHTML = connectionBoundaryBanner() +
            '<div class="empty">The shared queue is unavailable while the server is offline.</div>';
        }
      }
      async function loadHealth() {
        search.value = "";
        list.innerHTML = '<div class="empty">Calculating Archive Health…</div>';
        try {
          const d = await request("/archive-health"),
            h = d.archiveHealth;
          count.textContent = "Current archive assessment";
          list.innerHTML = `<div class="healthGrid">${[
            ["Overall", h.overallScore],
            ["Collection", h.collectionScore],
            ["Metadata", h.metadataScore],
            ["Research", h.researchScore],
            ["Preservation", h.preservationScore],
          ]
            .map(
              (x) =>
                `<div class="healthCard"><div class="muted">${x[0]}</div><div class="healthScore">${x[1]}%</div></div>`,
            )
            .join(
              "",
            )}</div><div class="section"><h3>Needs attention</h3><div>${h.actionableIssues} actionable issues</div><div class="muted" style="margin-top:7px">${h.missingBroadcasts} broadcasts to find · ${h.researchNeedsReview} research records to review · ${h.pendingReconciliation} pending matches</div></div>`;
        } catch {
          list.innerHTML =
            '<div class="empty">Archive Health could not be loaded.</div>';
        }
      }
      function transcriptStateLabel(value) {
        if (typeof value === "string") return value;
        return ["Queued", "Running", "Completed", "Failed", "Cancelled", "Interrupted"][Number(value)] || "Unknown";
      }
      function batchStateLabel(value) {
        if (typeof value === "string") return value;
        return ["Queued", "Running", "Paused", "Completed", "Completed with errors", "Cancelled", "Interrupted"][Number(value)] || "Unknown";
      }
      function transcriptStatusLabel(value) {
        if (typeof value === "string") return value;
        return ["Draft", "Complete", "Failed"][Number(value)] || "Unknown";
      }
      function formatTranscriptDate(value) {
        if (!value) return "Unknown date";
        const date = new Date(value);
        return Number.isNaN(date.getTime()) ? esc(String(value).slice(0, 10)) : date.toLocaleDateString(undefined, { day:"2-digit", month:"short", year:"numeric" });
      }
      function transcriptSummaryForEpisode(episodeId) {
        return transcriptSummaries.find((item) => Number(item.episodeId) === Number(episodeId));
      }
      function renderTranscriptionStatus() {
        const ready = !!transcriptionStatus?.isAvailable;
        const diarization = !!transcriptionStatus?.diarizationAvailable;
        const title = ready ? "Server transcription is ready" : "Transcription setup is needed";
        const detail = ready
          ? `${esc(transcriptionStatus.engineId || "Whisper")} · ${esc(transcriptionStatus.modelId || "default model")} · ${diarization ? "multi-speaker diarization ready" : "speaker diarization unavailable"}`
          : esc(transcriptionStatus?.availabilityMessage || "Install the recommended worker, speech model, voice activity detector and speaker models on the server.");
        return `<div class="transcriptStatus"><div class="statusCopy"><strong>${title}</strong><div class="muted">${detail}</div></div>${ready ? "" : '<div class="actions"><button class="primary" data-transcription-install>Install recommended setup</button></div>'}</div>`;
      }
      function renderTranscriptMetrics() {
        const active = transcriptionJobs.filter((job) => [0, 1, "Queued", "Running"].includes(job.state)).length;
        const speakers = transcriptSummaries.reduce((total, item) => total + Number(item.speakerCount || 0), 0);
        return `<div class="transcriptOverview"><div class="transcriptMetric"><strong>${transcriptSummaries.length.toLocaleString()}</strong><span>saved transcripts</span></div><div class="transcriptMetric"><strong>${active.toLocaleString()}</strong><span>active transcription jobs</span></div><div class="transcriptMetric"><strong>${speakers.toLocaleString()}</strong><span>speaker labels</span></div></div>`;
      }
      function renderTranscriptLibrary() {
        const query = transcriptQuery.trim().toLowerCase();
        const matches = transcriptSummaries.filter((item) => !query || [item.show, item.episodeTitle, item.airDateDisplay].some((value) => String(value || "").toLowerCase().includes(query)));
        const rows = matches.map((item) => `<button class="transcriptRow ${Number(selectedTranscriptSummary?.episodeId) === Number(item.episodeId) ? "active" : ""}" data-open-transcript="${Number(item.episodeId)}"><div class="show">${esc(item.show || "Unknown show")}</div><div class="title">${esc(item.episodeTitle || `Broadcast ${item.episodeId}`)}</div><div class="meta">${formatTranscriptDate(item.airDate)} · ${Number(item.wordCount || 0).toLocaleString()} words</div><div class="meta">${Number(item.speakerCount || 0) ? `${Number(item.identifiedSpeakerCount || 0)}/${Number(item.speakerCount || 0)} speakers identified` : "No speaker labels"}</div></button>`).join("");
        const viewer = selectedTranscript ? renderTranscriptViewer() : '<div class="transcriptViewer"><div class="transcriptEmpty"><div><strong>Select a transcript</strong><div class="transcriptQueueHint">Choose a saved broadcast to read its timed transcript.</div></div></div></div>';
        return `<input id="transcriptSearch" class="transcriptSearch" type="search" placeholder="Search transcript titles and shows" value="${esc(query)}" autocomplete="off"><div class="transcriptBrowser"><div class="transcriptList">${rows || '<div class="transcriptEmpty">No transcripts match this search.</div>'}</div>${viewer}</div>`;
      }
      function renderTranscriptViewer() {
        const transcript = selectedTranscript,
          summary = selectedTranscriptSummary || transcriptSummaryForEpisode(transcript.episodeId) || {},
          segments = transcript.segments || [];
        return `<div class="transcriptViewer"><div class="transcriptViewerHead"><div><h3>${esc(summary.episodeTitle || `Broadcast ${transcript.episodeId}`)}</h3><div class="meta">${esc(summary.show || "")} ${summary.show ? "·" : ""} ${transcriptStatusLabel(transcript.status)} · ${Number(transcript.wordCount || 0).toLocaleString()} words</div></div><div class="actions"><button data-transcript-export="txt">TXT</button><button data-transcript-export="srt">SRT</button><button data-transcript-export="vtt">VTT</button></div></div><div class="transcriptSegments">${segments.map((segment) => `<div class="transcriptSegment"><button class="transcriptTime" data-play="${Number(transcript.episodeId)}" data-seek="${Math.floor(Number(segment.startMs || 0) / 1000)}">${fmtMs(Number(segment.startMs || 0))}</button><div class="transcriptSpeaker">${esc(segment.assignedPersonName || segment.speaker || segment.speakerKey || "Speaker")}</div><div class="transcriptText">${esc(segment.text || "")}</div></div>`).join("") || `<div class="transcriptEmpty">${esc(transcript.fullText || "No timed segments are available.")}</div>`}</div></div>`;
      }
      function renderJobRuns() {
        const jobs = transcriptionJobs || [];
        return `<div class="transcriptRuns">${jobs.map((job) => {
          const state = job.stateDisplay || transcriptStateLabel(job.state),
            summary = transcriptSummaryForEpisode(job.episodeId),
            progress = Math.max(0, Math.min(100, Number(job.progressPercent || 0)));
          return `<div class="transcriptRun"><div class="runHead"><div><div class="runState">${esc(state)}</div><strong>${esc(summary?.episodeTitle || `Broadcast ${job.episodeId}`)}</strong></div><span>${progress.toFixed(0)}%</span></div><div class="runProgress"><span style="width:${progress}%"></span></div><div class="runMeta"><span>${esc(job.rangeDisplay || "Full broadcast")} · ${esc(job.modelId || "default model")}</span><span>${esc(job.requestedDisplay || "")}</span></div><div class="muted" style="margin-top:6px">${esc(job.error || job.message || "")}</div><div class="runActions">${job.canPause ? `<button data-transcription-action="pause" data-job-id="${job.jobId}">Pause</button>` : ""}${job.canResume ? `<button class="primary" data-transcription-action="resume" data-job-id="${job.jobId}">Resume</button>` : ""}${job.canRetry ? `<button data-transcription-action="retry" data-job-id="${job.jobId}">Retry</button>` : ""}${job.canCancel ? `<button data-transcription-action="cancel" data-job-id="${job.jobId}">Cancel</button>` : ""}${summary ? `<button data-open-transcript="${Number(job.episodeId)}">View transcript</button>` : ""}</div></div>`;
        }).join("") || '<div class="transcriptEmpty">No transcription jobs have run yet.</div>'}</div>`;
      }
      function renderBatchRuns() {
        return `<div class="transcriptRuns">${transcriptionBatches.map((batch) => {
          const selected = String(batch.batchId) === String(selectedBatchId),
            progress = Math.max(0, Math.min(100, Number(batch.progressPercent || 0)));
          return `<div class="transcriptRun"><div class="runHead"><div><div class="runState">${esc(batchStateLabel(batch.state))}</div><strong>${esc(batch.name || "Transcription batch")}</strong></div><span>${progress.toFixed(0)}%</span></div><div class="runProgress"><span style="width:${progress}%"></span></div><div class="runMeta"><span>${Number(batch.completedCount || 0)} complete · ${Number(batch.pendingCount || 0) + Number(batch.runningCount || 0)} remaining</span><span>${Number(batch.failedCount || 0)} failed</span></div><div class="runActions"><button data-open-batch="${batch.batchId}">${selected ? "Hide broadcasts" : "Show broadcasts"}</button>${batch.canPause ? `<button data-batch-action="pause" data-batch-id="${batch.batchId}">Pause</button>` : ""}${batch.canResume ? `<button class="primary" data-batch-action="resume" data-batch-id="${batch.batchId}">Resume</button>` : ""}${batch.canRetryFailed ? `<button data-batch-action="retry" data-batch-id="${batch.batchId}">Retry failed</button>` : ""}${batch.canCancel ? `<button data-batch-action="cancel" data-batch-id="${batch.batchId}">Cancel</button>` : ""}</div>${selected ? `<div style="margin-top:10px">${selectedBatchItems.map((item) => `<div class="moment"><strong>${esc(item.title || `Broadcast ${item.episodeId}`)}</strong><div class="muted">${esc(item.show || "")} · ${esc(item.progressDisplay || String(item.state))}${item.error ? ` · ${esc(item.error)}` : ""}</div></div>`).join("") || '<div class="muted">Loading batch broadcasts…</div>'}</div>` : ""}</div>`;
        }).join("") || '<div class="transcriptEmpty">No batch runs have been created yet.</div>'}</div>`;
      }
      function renderTranscripts() {
        count.textContent = `${transcriptSummaries.length.toLocaleString()} saved transcript${transcriptSummaries.length === 1 ? "" : "s"}`;
        const tabs = `<div class="transcriptTabs"><button class="${transcriptSection === "library" ? "active" : ""}" data-transcript-section="library">Transcript library</button><button class="${transcriptSection === "jobs" ? "active" : ""}" data-transcript-section="jobs">Individual jobs</button><button class="${transcriptSection === "batches" ? "active" : ""}" data-transcript-section="batches">Batch runs</button></div>`;
        const content = transcriptSection === "jobs" ? renderJobRuns() : transcriptSection === "batches" ? renderBatchRuns() : renderTranscriptLibrary();
        researchSection = "transcription";
        list.innerHTML = connectionBoundaryBanner() + `<div class="researchWorkspace">${researchTabs()}<div class="transcriptWorkspace">${renderTranscriptionStatus()}${renderTranscriptMetrics()}${tabs}${content}</div></div>`;
        const field = document.getElementById("transcriptSearch");
        if (field) field.addEventListener("input", () => {
          transcriptQuery = field.value;
          const position = field.selectionStart;
          renderTranscripts();
          const replacement = document.getElementById("transcriptSearch");
          replacement?.focus();
          replacement?.setSelectionRange(position, position);
        });
      }
      async function loadTranscripts(preserveSelection = true) {
        updateViewChrome();
        if (!preserveSelection) {
          selectedTranscript = null;
          selectedTranscriptSummary = null;
        }
        if (!transcriptSummaries.length && !transcriptionJobs.length) list.innerHTML = loadingSkeleton(4);
        try {
          const [summaries, jobs, batches, statusValue, settingsValue] = await Promise.all([
            clientPost("transcripts", "summaries"),
            clientPost("transcription", "jobs", { limit: 100 }),
            clientPost("transcription", "batches", { limit: 50 }),
            clientPost("transcription", "status"),
            clientPost("transcription", "settings"),
          ]);
          transcriptSummaries = summaries || [];
          transcriptionJobs = jobs || [];
          transcriptionBatches = batches || [];
          transcriptionStatus = statusValue;
          transcriptionSettings = settingsValue;
          renderTranscripts();
        } catch (error) {
          list.innerHTML = connectionBoundaryBanner() + `<div class="empty">The server transcription workspace could not be loaded.<div class="muted" style="margin-top:7px">${esc(error.message || "Try again when the server is connected.")}</div></div>`;
        }
      }
      async function openTranscript(episodeId) {
        selectedTranscriptSummary = transcriptSummaryForEpisode(episodeId) || null;
        selectedTranscript = await clientPost("transcripts", "get", { episodeId:Number(episodeId) });
        transcriptSection = "library";
        renderTranscripts();
      }
      function transcriptTimestamp(milliseconds, separator = ",") {
        const total = Math.max(0, Number(milliseconds || 0)), hours = Math.floor(total / 3600000), minutes = Math.floor(total % 3600000 / 60000), seconds = Math.floor(total % 60000 / 1000), millis = Math.floor(total % 1000);
        return `${String(hours).padStart(2,"0")}:${String(minutes).padStart(2,"0")}:${String(seconds).padStart(2,"0")}${separator}${String(millis).padStart(3,"0")}`;
      }
      function exportTranscript(format) {
        if (!selectedTranscript) return;
        const segments = selectedTranscript.segments || [];
        let content = "", mime = "text/plain", extension = format;
        if (format === "txt") content = selectedTranscript.fullText || segments.map((segment) => `${segment.assignedPersonName || segment.speaker || segment.speakerKey || "Speaker"}: ${segment.text || ""}`).join("\n\n");
        if (format === "srt") content = segments.map((segment, index) => `${index + 1}\n${transcriptTimestamp(segment.startMs)} --> ${transcriptTimestamp(segment.endMs)}\n${segment.assignedPersonName || segment.speaker || segment.speakerKey || "Speaker"}: ${segment.text || ""}\n`).join("\n");
        if (format === "vtt") { mime = "text/vtt"; content = "WEBVTT\n\n" + segments.map((segment) => `${transcriptTimestamp(segment.startMs, ".")} --> ${transcriptTimestamp(segment.endMs, ".")}\n${segment.assignedPersonName || segment.speaker || segment.speakerKey || "Speaker"}: ${segment.text || ""}\n`).join("\n"); }
        const title = (selectedTranscriptSummary?.episodeTitle || `broadcast-${selectedTranscript.episodeId}`).replace(/[^a-z0-9]+/gi, "-").replace(/^-|-$/g, "").toLowerCase();
        const link = document.createElement("a");
        link.href = URL.createObjectURL(new Blob([content], { type:`${mime};charset=utf-8` }));
        link.download = `${title || "transcript"}.${extension}`;
        link.click();
        setTimeout(() => URL.revokeObjectURL(link.href), 1000);
      }
      async function queueWebTranscription(episodeId, sample = false) {
        const existing = await clientPost("transcripts", "summary", { episodeId:Number(episodeId) });
        const replace = !!existing;
        if (replace && !confirm(`Replace the existing transcript with a new ${sample ? "five-minute sample" : "full transcription"}?`)) return;
        if (!transcriptionSettings) transcriptionSettings = await clientPost("transcription", "settings");
        await clientPost("transcription", "queue", {
          episodeId:Number(episodeId),
          options:{
            language:"",
            modelId:"",
            startMs:0,
            durationMs:sample ? 300000 : null,
            enableSpeakerDiarization:!!transcriptionSettings?.enableMultiSpeakerDiarization,
            useVoiceActivityDetection:!!transcriptionSettings?.useVoiceActivityDetection,
            replaceExistingTranscript:replace,
          },
        });
        notify(sample ? "Five-minute transcription queued" : "Full transcription queued");
        closeDetails();
        transcriptSection = "jobs";
        setPrimaryView("transcripts");
      }
      async function getDetails(id) {
        id = Number(id);
        try {
          const d = await request("/broadcasts/" + id);
          serverReachable = true;
          return d.broadcast;
        } catch (e) {
          const record =
            downloadedRecords.get(id) || (await dbGet("downloads", id));
          if (record?.details) return record.details;
          throw e;
        }
      }
      async function openDetails(id, push = true) {
        id = Number(id);
        currentDetailId = id;
        const detailWasOpen = detail.classList.contains("open");
        detail.classList.add("open");
        if (!detailWasOpen) focusOverlay(detail, $("detailBack"));
        detailBody.innerHTML = '<div class="empty">Loading canonical broadcast…</div>';
        if (push)
          history.pushState({ canonicalBroadcastId: id }, "", auth("/broadcast/" + id));
        try {
          const b = await getDetails(id),
            e = b.episode,
            broadcastId = Number(b.canonicalBroadcastId || e.canonicalBroadcastId || e.id || id),
            people = b.people || [],
            topics = b.topics || [],
            catalogueFields = b.catalogueFields || [],
            moments = b.moments || [],
            research = b.research,
            sourceLabel = serverReachable
              ? '<span class="sourceBoundary server">Server</span>'
              : '<span class="sourceBoundary device">Downloaded copy</span>';
          currentDetails = b;
          currentDetailId = broadcastId;
          detailBody.innerHTML = `<div class="hero">${episodeArt(e) ? `<img src="${episodeArt(e)}" alt="">` : '<div style="width:112px;height:112px;background:var(--panel);border-radius:12px"></div>'}<div><div class="show">${esc(e.show)}</div><h2>${esc(e.title)}</h2><div class="meta">${esc(e.airDate ? String(e.airDate).slice(0, 10) : "Date unknown")} · ${Math.round((e.durationMs || 0) / 60000)} min · ${sourceLabel}</div><div class="actions"><button data-play="${broadcastId}" data-position="${e.positionMs || 0}">${e.progressPercent > 0 ? "Resume" : "Listen"}</button><button class="ghost" data-queue="${broadcastId}">Add to queue</button><button class="secondary" data-favourite="${broadcastId}" data-value="${!e.favourite}">${e.favourite ? "Unfavourite" : "Favourite"}</button><button class="secondary" data-played="${broadcastId}" data-value="${e.status !== "Completed"}">${e.status === "Completed" ? "Mark unlistened" : "Mark listened"}</button><button class="secondary" data-download="${broadcastId}">${downloadState(broadcastId) === "downloaded" ? "Remove download" : downloadState(broadcastId) === "active" ? "Cancel download" : "Download"}</button><button class="secondary" data-add-moment="${broadcastId}">Add Moment</button><button class="secondary" data-load-transcript="${broadcastId}">Transcript</button></div><div class="storageNote">Canonical broadcast ${broadcastId}. Downloads remain on this device and use the same assembled timeline as the server stream.</div></div></div>${e.summary ? `<div class="section"><h3>Summary</h3><div class="muted">${esc(e.summary)}</div></div>` : ""}${catalogueFields.length ? `<div class="section"><h3>Programme details</h3>${catalogueFields.map((field) => `<div class="moment"><strong>${esc(field.label)}</strong><div class="muted">${esc(field.value)}</div></div>`).join("")}</div>` : ""}${people.length ? `<div class="section"><h3>People</h3><div class="chips">${people.map((p) => `<span class="chip">${esc(p.name)} · ${esc(p.role)}</span>`).join("")}</div></div>` : ""}${topics.length ? `<div class="section"><h3>Topics</h3><div class="chips">${topics.map((t) => `<span class="chip">${esc(t)}</span>`).join("")}</div></div>` : ""}${moments.length ? `<div class="section"><h3>Moments</h3>${moments.map((m) => `<div class="moment"><button data-play="${broadcastId}" data-seek="${m.positionSeconds}">${fmtMs(m.positionMs)}</button> <strong>${esc(m.title)}</strong>${m.notes ? `<div class="muted">${esc(m.notes)}</div>` : ""}</div>`).join("")}</div>` : ""}${research ? `<div class="section"><h3>Research</h3><div>${research.confidence}% confidence · ${esc(research.researchState)}</div><div class="muted" style="margin-top:6px">${research.sources.length} sources${research.needsReview ? " · needs review" : ""}${research.conflictCount ? " · " + research.conflictCount + " conflicts" : ""}</div>${research.sources.length ? `<div style="margin-top:10px">${research.sources.map((source) => `<div class="moment">${source.url ? `<a style="color:var(--accent)" href="${esc(source.url)}" target="_blank" rel="noreferrer">${esc(source.title || source.url)}</a>` : esc(source.title)}</div>`).join("")}</div>` : ""}</div>` : ""}${b.archiveNotes || b.personalNotes ? `<div class="section"><h3>Notes</h3><div class="muted">${esc([b.archiveNotes, b.personalNotes].filter(Boolean).join("\n\n"))}</div></div>` : ""}<div id="transcriptHost"></div>`;
          detailBody.querySelector(".hero .actions")?.insertAdjacentHTML("beforeend", `<button class="secondary" data-edit-metadata="${broadcastId}">Edit metadata</button><button class="secondary" data-transcribe-full="${broadcastId}">Transcribe full broadcast</button><button class="secondary" data-transcribe-sample="${broadcastId}">Five-minute sample</button>`);
          updateDownloadButtons();
        } catch (error) {
          recordDiagnostic("broadcast-details", "Canonical Broadcast Info could not be loaded", { canonicalBroadcastId: id, online: navigator.onLine });
          detailBody.innerHTML = connectionBoundaryBanner() +
            '<div class="empty">Broadcast Info could not be loaded from the server or this device.</div>';
        }
      }
      function canonicalDurationMs() {
        return Math.max(
          0,
          Number(localManifest?.durationMs || 0),
          Number(localEpisode?.durationMs || 0),
        );
      }
      function currentManifestPart() {
        return localManifest?.parts?.[localManifestPartIndex] || null;
      }
      function currentAudioLogicalPositionMs() {
        const localMs = Number.isFinite(audio.currentTime)
          ? Math.max(0, Math.round(audio.currentTime * 1000))
          : 0;
        return Math.max(0, Number(currentAudioLogicalBaseMs || 0) + localMs);
      }
      function currentLogicalPositionMs() {
        if (!localEpisode) return 0;
        const logical = currentAudioLogicalPositionMs(),
          duration = canonicalDurationMs();
        return duration > 0 ? Math.min(duration, logical) : logical;
      }
      function manifestPartIndexForPosition(positionMs) {
        const parts = Array.isArray(localManifest?.parts) ? localManifest.parts : [];
        if (!parts.length) return -1;
        const target = Math.max(0, Number(positionMs || 0));
        const found = parts.findIndex((part, index) =>
          target >= Number(part.logicalStartMs || 0) &&
          (target < Number(part.logicalEndMs || 0) || index === parts.length - 1));
        return found >= 0 ? found : parts.length - 1;
      }
      function logicalPositionWithinPart(positionMs, part) {
        const partDuration = Math.max(
          0,
          Number(part?.logicalEndMs || 0) - Number(part?.logicalStartMs || 0),
        );
        return Math.max(
          0,
          Math.min(
            partDuration > 0 ? Math.max(0, partDuration - 250) : Number.MAX_SAFE_INTEGER,
            Number(positionMs || 0) - Number(part?.logicalStartMs || 0),
          ),
        );
      }
      function revokeCurrentAudioObjectUrl() {
        if (!currentAudioObjectUrl) return;
        URL.revokeObjectURL(currentAudioObjectUrl);
        currentAudioObjectUrl = "";
      }
      function waitForAudioReady(timeoutMs = 12000) {
        if (audio.readyState >= 2 && Number.isFinite(audio.duration) && audio.duration > 0)
          return Promise.resolve();
        return new Promise((resolve, reject) => {
          let settled = false;
          const finish = (error) => {
            if (settled) return;
            settled = true;
            clearTimeout(timeout);
            audio.removeEventListener("loadedmetadata", ready);
            audio.removeEventListener("canplay", ready);
            audio.removeEventListener("error", failed);
            error ? reject(error) : resolve();
          };
          const ready = () => {
            if (audio.readyState >= 1 && Number.isFinite(audio.duration) && audio.duration > 0)
              finish();
          };
          const failed = () => finish(new Error("The target audio could not be opened."));
          const timeout = setTimeout(
            () => finish(new Error("The target audio did not become ready in time.")),
            timeoutMs,
          );
          audio.addEventListener("loadedmetadata", ready);
          audio.addEventListener("canplay", ready);
          audio.addEventListener("error", failed);
          ready();
        });
      }
      async function waitForIosDecoderClock(timeoutMs = 1800) {
        if (!isIosWebKit || audio.paused) return true;
        const startedAt = Number.isFinite(audio.currentTime) ? audio.currentTime : 0,
          deadline = Date.now() + Math.max(500, Number(timeoutMs || 1800));
        while (Date.now() < deadline) {
          await new Promise((resolve) => setTimeout(resolve, 75));
          if (audio.error || audio.ended || !Number.isFinite(audio.duration) || audio.duration <= 0)
            return false;
          if (!audio.paused && Number(audio.currentTime || 0) >= startedAt + 0.08)
            return true;
        }
        return false;
      }
      async function loadCanonicalManifest(id, record = null) {
        id = Number(id);
        if (record) {
          const durationMs = Math.max(
            0,
            Number(record?.details?.episode?.durationMs || 0),
            Number(record?.episode?.durationMs || 0),
            Number(localEpisode?.durationMs || 0),
          );
          return {
            episodeId: id,
            recordingKey: "offline",
            durationMs,
            parts: [{
              partNumber: 1,
              partTotal: 1,
              logicalStartMs: 0,
              logicalEndMs: durationMs,
              mediaFileId: 0,
            }],
          };
        }
        const manifest = await request("/broadcasts/" + id + "/media-manifest", {
          timeoutMs: 12000,
        });
        if (!Array.isArray(manifest?.parts) || !manifest.parts.length)
          throw new Error("Radio Vault did not return a playable canonical media plan.");
        manifest.parts.sort(
          (left, right) => Number(left.logicalStartMs || 0) - Number(right.logicalStartMs || 0),
        );
        return manifest;
      }
      function assignCanonicalPartSource(id, partIndex, record = null) {
        const part = localManifest?.parts?.[partIndex];
        if (!part) throw new Error("The requested canonical media part is unavailable.");
        audioSourceReady = false;
        gesturePrimedEpisodeId = 0;
        gesturePrimedPositionMs = 0;
        currentAudioEpisodeId = Number(id);
        currentAudioLogicalBaseMs = Number(part.logicalStartMs || 0);
        currentAudioIsPositioned = false;
        playErrorShown = false;
        revokeCurrentAudioObjectUrl();
        suppressNextPauseSync = true;
        if (record) {
          if (navigator.serviceWorker?.controller) {
            audio.src = offlineAudioPath(id);
            currentAudioSource = "download";
          } else if (record.audioBlob) {
            currentAudioObjectUrl = URL.createObjectURL(record.audioBlob);
            audio.src = currentAudioObjectUrl;
            currentAudioSource = "download";
          } else {
            throw new Error("The downloaded audio data is missing.");
          }
        } else {
          const recording = localManifest?.recordingKey
            ? "?recording=" + encodeURIComponent(localManifest.recordingKey)
            : "?";
          audio.src = auth(
            api + "/broadcasts/" + Number(id) + "/media/" + Number(part.mediaFileId) +
            recording + "&streamSession=" + Date.now() + "-" + Math.random().toString(16).slice(2),
          );
          currentAudioSource = "stream";
        }
        localManifestPartIndex = partIndex;
        localManifestRecord = record;
        audio.load();
      }
      function assignCanonicalGestureStartSource(id, positionMs) {
        audioSourceReady = false;
        playErrorShown = false;
        revokeCurrentAudioObjectUrl();
        suppressNextPauseSync = true;
        gesturePrimedEpisodeId = Number(id);
        gesturePrimedPositionMs = Math.max(0, Number(positionMs || 0));
        currentAudioEpisodeId = Number(id);
        // The media-start representation's byte zero is this canonical point.
        // Store that truth immediately, before any manifest or transfer response,
        // so asynchronous attachment cannot lose the positioned timeline.
        currentAudioLogicalBaseMs = gesturePrimedPositionMs;
        currentAudioIsPositioned = isIosWebKit || gesturePrimedPositionMs > 0;
        audio.src = auth(
          api + "/broadcasts/" + Number(id) + "/media-start?positionMs=" +
          Math.round(gesturePrimedPositionMs) + "&positioned=" + (isIosWebKit ? "1" : "0") +
          "&streamSession=" + Date.now() + "-" +
          Math.random().toString(16).slice(2),
        );
        currentAudioSource = "stream";
        audio.load();
      }
      function decoderMatchesGestureTarget(id, positionMs) {
        if (
          Number(currentAudioEpisodeId || 0) !== Number(id) ||
          !audioSourceReady ||
          !audio.src ||
          audio.error ||
          audio.ended
        ) return false;
        if (!isIosWebKit) return true;
        return currentAudioIsPositioned &&
          Math.abs(currentAudioLogicalPositionMs() - Number(positionMs || 0)) <= 2500;
      }
      function setLogicalPositionImmediately(positionMs) {
        const partIndex = manifestPartIndexForPosition(positionMs);
        if (partIndex < 0 || partIndex !== localManifestPartIndex || audio.readyState < 1)
          return false;
        const localMs = Math.max(0, Number(positionMs || 0) - Number(currentAudioLogicalBaseMs || 0)),
          targetLogicalMs = Math.max(0, Number(currentAudioLogicalBaseMs || 0) + localMs),
          logicalSeekDeadbandMs = 750;
        // iPhone Safari can reinterpret the duration of a long MP3 after a tiny
        // redundant range seek and immediately mark an otherwise healthy decoder
        // as ended. The dormant handoff decoder is kept close to the shared
        // playhead already, so preserve it when it is within commit tolerance.
        if (
          !audio.ended &&
          Math.abs(currentLogicalPositionMs() - targetLogicalMs) <= logicalSeekDeadbandMs
        ) return true;
        try {
          audio.currentTime = localMs / 1000;
          return true;
        } catch {
          return false;
        }
      }
      async function waitForLogicalAlignment(positionMs, toleranceMs = 1250) {
        const expectedPartIndex = manifestPartIndexForPosition(positionMs);
        if (expectedPartIndex !== localManifestPartIndex) return false;
        for (let attempt = 0; attempt < 8; attempt++) {
          // Once WebKit has collapsed a range-backed decoder, further seeks only
          // generate misleading seeked events against the tiny probe resource.
          // Stop immediately so the caller can keep ownership at the source.
          if (
            isIosWebKit &&
            (audio.error || audio.ended || !Number.isFinite(audio.duration) || audio.duration < 1)
          ) return false;
          const difference = Math.abs(currentLogicalPositionMs() - Number(positionMs || 0));
          if (difference <= toleranceMs) return true;
          setLogicalPositionImmediately(positionMs);
          await new Promise((resolve) => setTimeout(resolve, attempt === 0 ? 80 : 100));
        }
        return Math.abs(currentLogicalPositionMs() - Number(positionMs || 0)) <= toleranceMs;
      }
      async function prepareCanonicalAudio(id, positionMs, record = null) {
        id = Number(id);
        const positionedGestureTimeoutMs = isIosWebKit && currentAudioIsPositioned ? 4500 : 12000;
        const manifestChanged = Number(localManifest?.episodeId || 0) !== id ||
          (!!record !== !!localManifestRecord);
        if (manifestChanged) {
          localManifest = await loadCanonicalManifest(id, record);
          localManifestPartIndex = -1;
          localManifestRecord = record;
        }
        const partIndex = manifestPartIndexForPosition(positionMs);
        if (partIndex < 0) throw new Error("No canonical media part covers the requested playhead.");
        if (
          !record &&
          Number(gesturePrimedEpisodeId || 0) === id &&
          manifestPartIndexForPosition(gesturePrimedPositionMs) === partIndex &&
          audio.src
        ) {
          // The actual canonical part was opened synchronously in the Library
          // tap. Attach the later manifest to that decoder without replacing the
          // blessed media resource and losing Safari's audible-play permission.
          localManifestPartIndex = partIndex;
          localManifestRecord = null;
        }
        try {
          if (partIndex !== localManifestPartIndex || !audio.src) {
            assignCanonicalPartSource(id, partIndex, record);
            await waitForAudioReady();
          } else if (!audioSourceReady) {
            await waitForAudioReady(positionedGestureTimeoutMs);
          }
        } catch (error) {
          if (record || currentAudioSource !== "stream") throw error;

          const failedSource = audio.src,
            failedWasPositioned = currentAudioIsPositioned;
          traceTransfer("fallback", "The positioned iPhone stream failed; opening canonical media", {
            failedWasPositioned,
            errorName: error?.name || "Error",
            errorMessage: error?.message || String(error),
          });

          // The server may have restarted or refreshed its canonical media
          // projection while this page retained an older in-memory manifest.
          // Refresh the plan and give the decoder one new, cache-busted source.
          localManifest = await loadCanonicalManifest(id, null);
          localManifestPartIndex = -1;
          localManifestRecord = null;
          const retryPartIndex = manifestPartIndexForPosition(positionMs);
          if (retryPartIndex < 0)
            throw new Error("No refreshed canonical media part covers the requested playhead.");
          await new Promise((resolve) => setTimeout(resolve, 175));
          assignCanonicalPartSource(id, retryPartIndex, null);
          await waitForAudioReady();
          if (isIosWebKit && failedWasPositioned && audio.paused) {
            // The original play() was invoked synchronously in the tap. Reusing
            // that same media element preserves Safari's user activation even
            // though replacing the failed source rejects the old play promise.
            // Start the healthy full-file decoder before seeking it, otherwise
            // WebKit can collapse the duration at the requested resume point.
            audio.muted = true;
            try {
              await audio.play();
            } catch (fallbackPlayError) {
              traceTransfer("fallback", "Safari rejected the canonical media fallback", {
                failedSource,
                fallbackSource: audio.src,
                errorName: fallbackPlayError?.name || "Error",
                errorMessage: fallbackPlayError?.message || String(fallbackPlayError),
              });
              throw new Error("Safari could not start the canonical audio fallback. Tap the move control again.");
            }
            traceTransfer("fallback", "Canonical media fallback is running", {
              failedSource,
              fallbackSource: audio.src,
              decoderDurationMs: Math.round(Number(audio.duration || 0) * 1000),
            });
          }
        }
        const requestedPart = currentManifestPart(),
          requestedLocalMs = logicalPositionWithinPart(positionMs, requestedPart);
        if (
          isIosWebKit &&
          !audio.paused &&
          requestedLocalMs > 1000 &&
          !currentAudioIsPositioned
        ) {
          traceTransfer("align", "Waiting for the fresh iPhone decoder clock before seeking", {
            targetPositionMs: Math.round(Number(positionMs || 0)),
            decoderCurrentTimeMs: Math.round(Number(audio.currentTime || 0) * 1000),
            decoderDurationMs: Math.round(Number(audio.duration || 0) * 1000),
          });
          if (!(await waitForIosDecoderClock()))
            throw new Error("The iPhone decoder did not begin consuming the fresh audio stream.");
          traceTransfer("align", "Fresh iPhone decoder clock is running; applying the shared playhead", {
            targetPositionMs: Math.round(Number(positionMs || 0)),
            decoderCurrentTimeMs: Math.round(Number(audio.currentTime || 0) * 1000),
            decoderDurationMs: Math.round(Number(audio.duration || 0) * 1000),
          });
        }
        setLogicalPositionImmediately(positionMs);
        const alignmentToleranceMs = currentAudioIsPositioned ? 2500 : 1250;
        if (!(await waitForLogicalAlignment(positionMs, alignmentToleranceMs)))
          throw new Error("The target decoder could not align to the shared playhead.");
        audioSourceReady = true;
        return currentLogicalPositionMs();
      }
      async function hydrateLocalEpisode(id, positionMs, record, authoritativePosition = false, revealPlayer = true) {
        const progress = await getLocalProgress(id);
        localDetails = record?.details || (await getDetails(id));
        localEpisode = {
          ...localDetails.episode,
          id: Number(localDetails.episode?.canonicalBroadcastId || localDetails.episode?.id || id),
          positionMs: authoritativePosition
            ? Math.max(0, Number(positionMs || 0))
            : progress?.positionMs ?? localDetails.episode.positionMs,
          status: progress?.completed
            ? "Completed"
            : progress?.positionMs > 0
              ? "In Progress"
              : localDetails.episode.status,
        };
        if (revealPlayer) showFullPlayer();
        else renderPlayer();
        setMediaSession();
      }
      function synchronousResumeMs(id, positionMs = 0, record = null) {
        let local = null;
        try {
          local = JSON.parse(localStorage.getItem(progressKey(id)) || "null");
        } catch {}
        return Math.max(
          0,
          Number(positionMs || 0),
          Number(record?.episode?.positionMs || 0),
          Number(record?.progress?.positionMs || 0),
          Number(local?.positionMs || 0),
        );
      }
      function sharedStartPoint(id, requestedPositionMs = 0, desiredPlayingOverride = null) {
        const requested = Math.max(0, Number(requestedPositionMs || 0)),
          hasPlayingOverride = typeof desiredPlayingOverride === "boolean";
        if (serverReachable && Number(sessionState?.player?.episodeId || 0) === Number(id)) {
          const shared = projectedSessionState();
          const sharedPosition = Math.max(0, Number(shared.positionMs || 0));
          return {
            positionMs: sharedPosition > 0 ? sharedPosition : requested,
            durationMs: Math.max(0, Number(shared.durationMs || 0)),
            speed: Number(shared.speed || 1),
            desiredPlaying: hasPlayingOverride ? desiredPlayingOverride : !!shared.isPlaying,
            remote: shared,
          };
        }
        return {
          positionMs: requested,
          durationMs: 0,
          speed: 1,
          desiredPlaying: hasPlayingOverride ? desiredPlayingOverride : true,
          remote: null,
        };
      }
      function applyTransferResponse(result) {
        if (result?.session) applyPlaybackSessionSnapshot(result.session);
        renderPlayer();
        return result?.transfer || null;
      }
      async function transferRequest(stage, body) {
        const response = await request("/player/transfer/" + stage, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body),
          timeoutMs: stage === "commit" ? 10000 : 15000,
        });
        if (!response?.result?.changed && stage !== "cancel")
          throw new Error(response?.result?.message || "The playback move was not accepted.");
        return response.result;
      }
      async function beginPhoneTransfer(body) {
        let lastError = null;
        for (let attempt = 0; attempt < 2; attempt++) {
          try {
            return await transferRequest("begin", body);
          } catch (error) {
            lastError = error;
            if (attempt === 0)
              await new Promise((resolve) => setTimeout(resolve, 200));
          }
        }
        throw lastError || new Error("The server did not confirm playback preparation.");
      }
      async function recoverCommittedPhoneTransfer(transfer) {
        try {
          const player = await request("/player", { timeoutMs: 2500 }),
            session = player?.session || null,
            receipt = session?.committedTransfer || null;
          if (session) applyPlaybackSessionSnapshot(session);
          if (
            receipt &&
            String(receipt.transferId || "") === String(transfer?.transferId || "") &&
            String(receipt.targetClientId || "") === clientId &&
            String(session?.ownerClientId || "") === clientId &&
            Number(receipt.generation || 0) === Number(session?.generation || 0)
          ) {
            return {
              changed: true,
              conflict: false,
              message: "Playback move was already committed.",
              transfer,
              session,
            };
          }
        } catch {}
        return null;
      }
      async function commitPhoneTransfer(transfer, body) {
        let lastError = null;
        for (let attempt = 0; attempt < 3; attempt++) {
          try {
            return await transferRequest("commit", body);
          } catch (error) {
            lastError = error;
            const recovered = await recoverCommittedPhoneTransfer(transfer);
            if (recovered) return recovered;
            if (attempt < 2)
              await new Promise((resolve) => setTimeout(resolve, 250));
          }
        }
        const error = lastError || new Error(
          "The server did not confirm whether the playback move committed.",
        );
        error.commitOutcomeUnknown = true;
        throw error;
      }
      async function cancelPhoneTransfer(transfer, reason) {
        if (!transfer?.transferId) return null;
        try {
          const result = await transferRequest("cancel", {
            clientId,
            transferId: transfer.transferId,
            reason: String(reason || "Target preparation failed"),
          });
          applyTransferResponse(result);
          return result;
        } catch {
          return null;
        }
      }
      async function acknowledgeCommittedSourceStop(session) {
        const receipt = session?.committedTransfer;
        if (
          !receipt ||
          receipt.sourceStopAcknowledged ||
          !receipt.sourceWasPlaying ||
          String(receipt.sourceClientId || "") !== clientId ||
          String(receipt.targetClientId || "") === clientId ||
          sourceStopAcknowledgementInFlight
        ) return false;

        sourceStopAcknowledgementInFlight = true;
        try {
          // Stop the physical browser decoder before acknowledging. Suppress the
          // ordinary pause heartbeat because the committed transfer already saved
          // the protected boundary and this phone is no longer the owner.
          if (!audio.paused) {
            suppressNextPauseSync = true;
            audio.pause();
          }
          const result = await transferRequest("source-stopped", {
            clientId,
            transferId: receipt.transferId,
            generation: Number(receipt.generation || 0),
          });
          applyTransferResponse(result);
          return true;
        } catch (error) {
          recordDiagnostic("source-stop-ack", "The previous phone output stopped but its acknowledgement will retry", {
            message: error?.message || String(error || "Unknown error"),
          });
          return false;
        } finally {
          sourceStopAcknowledgementInFlight = false;
        }
      }
      function playbackOwnershipMovedError(session) {
        const owner = String(session?.ownerDevice || "another device");
        const error = new Error(`Playback moved again to ${owner} before this phone could start.`);
        error.playbackOwnershipMoved = true;
        return error;
      }
      function assertCommittedPhoneOwnership(generation, session = sessionState) {
        if (
          Number(session?.generation || 0) !== Number(generation || 0) ||
          String(session?.ownerClientId || "") !== clientId
        ) throw playbackOwnershipMovedError(session);
        return true;
      }
      async function readAndConfirmCommittedPhoneOwnership(generation) {
        const player = await request("/player", { timeoutMs: 2500 });
        const latest = player?.session || null;
        if (latest) applyPlaybackSessionSnapshot(latest);
        assertCommittedPhoneOwnership(generation, latest);
        return latest;
      }
      async function waitForCommittedSourceStop(session, generation, timeoutMs = 3000) {
        assertCommittedPhoneOwnership(generation, session);
        let receipt = session?.committedTransfer;
        if (
          !receipt ||
          receipt.sourceStopAcknowledged ||
          !receipt.sourceWasPlaying ||
          !receipt.sourceClientId ||
          String(receipt.sourceClientId) === clientId
        ) {
          await readAndConfirmCommittedPhoneOwnership(generation);
          return true;
        }

        const transferId = String(receipt.transferId || ""),
          deadline = Date.now() + Math.max(500, Number(timeoutMs || 3000));
        while (Date.now() < deadline) {
          await new Promise((resolve) => setTimeout(resolve, 125));
          try {
            const latest = await readAndConfirmCommittedPhoneOwnership(generation);
            receipt = latest?.committedTransfer;
            if (
              receipt &&
              String(receipt.transferId || "") === transferId &&
              Number(receipt.generation || 0) === Number(generation || 0) &&
              receipt.sourceStopAcknowledged
            ) return true;
          } catch (error) {
            if (error?.playbackOwnershipMoved) throw error;
          }
        }

        // A disconnected previous source is why this wait is bounded, but the
        // target must still prove that a newer transfer has not superseded it.
        await readAndConfirmCommittedPhoneOwnership(generation);
        return false;
      }
      async function primeTargetDecoder(desiredPlaying, alreadyRunningAudibly = false) {
        if (!desiredPlaying) {
          if (!audio.paused) {
            suppressNextPauseSync = true;
            audio.pause();
          }
          return true;
        }
        if (!alreadyRunningAudibly) audio.muted = true;
        await audio.play();
        await new Promise((resolve) => setTimeout(resolve, 120));
        if (audio.paused || audio.ended || audio.readyState < 2)
          throw new Error("The browser could not prove that the target decoder was running.");
        return true;
      }
      async function ensurePhoneOutputState(desiredPlaying) {
        audio.muted = false;
        if (!desiredPlaying) {
          if (!audio.paused) {
            suppressNextPauseSync = true;
            audio.pause();
          }
          return true;
        }

        // Safari can resolve the muted preparation play promise before the
        // audible media pipeline has fully transitioned. Retry Play across a
        // short bounded window and verify the decoder is genuinely running after
        // unmute; a committed handoff must never finish as silent ownership.
        let lastError = null;
        for (let attempt = 0; attempt < 5; attempt++) {
          try {
            if (audio.paused) await audio.play();
          } catch (error) {
            lastError = error;
          }
          await new Promise((resolve) => setTimeout(resolve, 160));
          if (!audio.paused && !audio.ended && audio.readyState >= 2 && !audio.error)
            return true;
        }
        throw new Error(lastError?.message || "The phone decoder did not start after playback ownership moved.");
      }
      function dormantDecoderReadyFor(id) {
        return Number(id || 0) > 0 &&
          Number(dormantDecoderReadyEpisodeId || 0) === Number(id) &&
          Number(localManifest?.episodeId || 0) === Number(id) &&
          audioSourceReady;
      }
      async function startLocalEpisodeFromGesture(id, positionMs = 0, desiredPlayingOverride = true) {
        id = Number(id);
        if (activePhoneTransfer) {
          notify("Playback is already being prepared on this device.");
          return false;
        }
        const operationId = ++phoneTransferSequence,
          shared = sharedStartPoint(id, positionMs, desiredPlayingOverride),
          desiredPlaying = !!shared.desiredPlaying,
          sourceWasUnowned = !ownerClientId() &&
            Number(sessionState?.player?.episodeId || 0) <= 0,
          directAudiblePrime = desiredPlaying && sourceWasUnowned,
          targetDecoderMatches = decoderMatchesGestureTarget(id, shared.positionMs),
          mustPrimeTargetSourceInGesture = desiredPlaying && isIosWebKit &&
            !targetDecoderMatches;
        activePhoneTransfer = { operationId, transfer: null, committed: false };
        phoneTransferInProgress = true;
        localTakeoverPending = true;
        beginTransferTrace(id, positionMs);
        renderPlayer();
        let transfer = null,
          record = null,
          transferCommitted = false,
          committedGeneration = 0;
        try {
          traceTransfer("begin", "Beginning transactional playback move", transferSnapshot({
            sourceOwner: ownerDevice(),
            protectedPositionMs: Math.round(shared.positionMs || 0),
          }));

          // Invoke play() synchronously inside the tap whenever the shared
          // dormant decoder is available. This preserves Safari's user-gesture
          // permission across the later network/manifest work. The outcome is
          // retained rather than swallowed so a denied gesture cannot be mistaken
          // for a decoder that is safe to commit.
          let gesturePrime = null,
            gesturePrimeSource = "";
          if (directAudiblePrime || mustPrimeTargetSourceInGesture) {
            // A broadcast switch must replace the decoder inside the tap, before
            // any transfer or manifest await. Otherwise Safari briefly replays the
            // previous broadcast and the later full-file seek can collapse the new
            // decoder's duration to the requested resume point. Keep the fresh
            // positioned decoder muted while another source session exists.
            traceTransfer("prime", "Opening the target broadcast in the playback gesture", {
              targetEpisodeId: id,
              targetPositionMs: Math.round(Number(shared.positionMs || 0)),
              previousAudioEpisodeId: Number(currentAudioEpisodeId || 0),
              targetDecoderMatches,
              audible: directAudiblePrime,
            });
            assignCanonicalGestureStartSource(id, shared.positionMs);
            gesturePrimeSource = audio.src;
            audio.muted = !directAudiblePrime;
            gesturePrime = audio.play().then(
              () => ({ ok: true, error: null }),
              (error) => ({ ok: false, error }),
            );
          } else if (desiredPlaying && audioSourceReady && audio.src) {
            if (Number(localManifest?.episodeId || 0) === id) {
              if (!isIosWebKit || localManifestRecord) {
                setLogicalPositionImmediately(shared.positionMs);
              }
              // Reuse is safe only when the ready decoder already belongs to the
              // requested broadcast. iPhone mismatches were replaced above with
              // the server-positioned WAV representation.
            }
            audio.muted = true;
            gesturePrimeSource = audio.src;
            gesturePrime = audio.play().then(
              () => ({ ok: true, error: null }),
              (error) => ({ ok: false, error }),
            );
          }

          const beginResult = await beginPhoneTransfer({
            clientId,
            deviceName: playbackDeviceName,
            deviceKind: playbackDeviceKind,
            episodeId: id,
            positionMs: Math.round(shared.positionMs || 0),
            durationMs: Math.round(shared.durationMs || 0),
            speed: Number(shared.speed || 1),
            desiredPlaying,
          });
          transfer = applyTransferResponse(beginResult);
          activePhoneTransfer.transfer = transfer;
          if (!transfer) throw new Error("The server did not create a playback transfer ticket.");
          traceTransfer("ticket", "Server playback ticket received", transferSnapshot({
            requestedPositionMs: Math.round(shared.positionMs || 0),
            protectedPositionMs: Math.round(Number(transfer.protectedPositionMs || 0)),
            commitPositionMs: Math.round(Number(transfer.commitPositionMs || 0)),
          }));

          // A connected transactional move always uses the server's canonical
          // manifest and media parts. A legacy single-blob phone download may cover
          // only one recording from a multipart broadcast, so it must never be used
          // as the decoder proof for a cross-device ownership commit.
          record = null;
          if (dormantPreparationPromise) {
            traceTransfer("align", "Waiting for the existing dormant decoder preparation", transferSnapshot());
            await dormantPreparationPromise.catch(() => {});
          }
          await hydrateLocalEpisode(id, transfer.protectedPositionMs, record, true);
          await prepareCanonicalAudio(id, transfer.protectedPositionMs, record);
          audio.playbackRate = Math.max(0.5, Math.min(3, Number(transfer.speed || shared.speed || 1)));
          if (gesturePrime) {
            const gestureResult = await gesturePrime;
            if (!gestureResult.ok) {
              const fallbackReplacedPrimedSource = !!gesturePrimeSource &&
                audio.src !== gesturePrimeSource && audioSourceReady && !audio.error;
              traceTransfer(
                fallbackReplacedPrimedSource ? "fallback" : "permission",
                fallbackReplacedPrimedSource
                  ? "The failed positioned play promise was replaced by a healthy canonical decoder"
                  : "Safari rejected the playback gesture",
                {
                  gesturePrimeSource,
                  currentSource: audio.src,
                  errorName: gestureResult.error?.name || "Error",
                  errorMessage: gestureResult.error?.message || String(gestureResult.error),
                },
              );
              if (!fallbackReplacedPrimedSource)
                throw new Error("Safari did not allow this device to start audio. Wait for the move control to finish preparing, then tap it again.");
            }
          }
          await primeTargetDecoder(!!transfer.desiredPlaying, directAudiblePrime);

          let preparedPositionMs = currentLogicalPositionMs(),
            alignedForCommit = false;
          for (let alignmentPass = 0; alignmentPass < 4; alignmentPass++) {
            // Priming can advance or re-seek the media element. Measure the
            // actual decoder position after it has settled, immediately before
            // asking the server for a fresh source boundary.
            await primeTargetDecoder(!!transfer.desiredPlaying, directAudiblePrime);
            preparedPositionMs = currentLogicalPositionMs();
            const readyResult = await transferRequest("ready", {
              clientId,
              transferId: transfer.transferId,
              preparedPositionMs,
              preparedDurationMs: canonicalDurationMs(),
              decoderReady: audioSourceReady,
              desiredPlaying: !!transfer.desiredPlaying,
              overrideDesiredPlaying: false,
              speed: Number(audio.playbackRate || transfer.speed || 1),
            });
            transfer = applyTransferResponse(readyResult);
            activePhoneTransfer.transfer = transfer;
            if (!transfer?.isReady)
              throw new Error("The server did not confirm that the target decoder was ready.");
            audio.playbackRate = Math.max(0.5, Math.min(3, Number(transfer.speed || 1)));
            preparedPositionMs = currentLogicalPositionMs();
            const difference = Math.abs(
              preparedPositionMs - Number(transfer.commitPositionMs || 0),
            );
            const commitAlignmentToleranceMs = directAudiblePrime ? 2500 : 750;
            if (difference <= commitAlignmentToleranceMs) {
              alignedForCommit = true;
              break;
            }

            traceTransfer("align", "Aligning target to refreshed source playhead", {
              pass: alignmentPass + 1,
              targetPositionMs: preparedPositionMs,
              sourcePositionMs: Number(transfer.commitPositionMs || 0),
            });
            await prepareCanonicalAudio(id, transfer.commitPositionMs, record);
            preparedPositionMs = currentLogicalPositionMs();
          }
          if (!alignedForCommit)
            throw new Error("The phone decoder could not stay aligned with the source device.");

          const commitResult = await commitPhoneTransfer(transfer, {
            clientId,
            transferId: transfer.transferId,
            readyRevision: Number(transfer.readyRevision || 0),
            preparedPositionMs,
            decoderRunningMuted: !transfer.desiredPlaying || (!audio.paused && audio.muted),
            decoderRunningAudibly: !!transfer.desiredPlaying && directAudiblePrime &&
              !audio.paused && !audio.muted,
          });
          transfer = applyTransferResponse(commitResult);
          transferCommitted = true;
          committedGeneration = Number(commitResult.session?.generation || 0);
          assertCommittedPhoneOwnership(committedGeneration, commitResult.session);
          activePhoneTransfer.committed = true;
          playerStateEpoch++;
          webState = commitResult.session?.phone || commitResult.session?.player || webState;
          webStateReceivedAt = Date.now();

          phoneTransferInProgress = false;
          localTakeoverPending = false;
          dormantDecoderReadyEpisodeId = 0;
          dormantDecoderReadyAt = 0;
          dormantDecoderReadyPositionMs = 0;
          let sourceStopConfirmed = true;
          if (transfer?.desiredPlaying) {
            sourceStopConfirmed = await waitForCommittedSourceStop(
              commitResult.session,
              committedGeneration,
              3000,
            );
          } else {
            await readAndConfirmCommittedPhoneOwnership(committedGeneration);
          }
          // Ownership is checked once more at the exact sound boundary. A rapid
          // phone→laptop/server move must never let this older target unmute.
          assertCommittedPhoneOwnership(committedGeneration);
          await ensurePhoneOutputState(!!transfer?.desiredPlaying);
          await syncWebPlayback(false, false);
          await saveLocalProgress(false, { allowRewind: true });
          await syncPendingProgress();
          activePhoneTransfer = null;
          gesturePrimedEpisodeId = 0;
          gesturePrimedPositionMs = 0;
          traceTransfer("success", "Transactional playback move committed", transferSnapshot({
            sourceStopConfirmed,
          }));
          transferTraceStartedAt = 0;
          renderPlayer();
          if (!sourceStopConfirmed)
            notify("Playback moved, but the previous device did not confirm that it stopped before the safety timeout.", true);
          return true;
        } catch (error) {
          const committed = transferCommitted || !!activePhoneTransfer?.committed;
          if (transfer && !committed && !error?.commitOutcomeUnknown)
            await cancelPhoneTransfer(transfer, error?.message);
          phoneTransferInProgress = false;
          localTakeoverPending = false;
          activePhoneTransfer = null;
          gesturePrimedEpisodeId = 0;
          gesturePrimedPositionMs = 0;

          if (committed) {
            const superseded = !!error?.playbackOwnershipMoved ||
              (committedGeneration > 0 && !(
                Number(sessionState?.generation || 0) === committedGeneration &&
                String(sessionState?.ownerClientId || "") === clientId
              ));
            if (superseded) {
              // Commit was valid, but a newer generation has already moved output
              // elsewhere. This older target must remain silent.
              audio.muted = false;
              if (!audio.paused) {
                suppressNextPauseSync = true;
                audio.pause();
              }
              renderPlayer();
              showTransferDiagnostic(error);
              notify(error?.message || "Playback moved again before this phone could start.", true);
              return false;
            }

            // Commit is the irreversible ownership boundary. A later durable-save,
            // diagnostics or rendering failure must never stop the newly authoritative
            // decoder and leave silence everywhere while this generation is still ours.
            try {
              await ensurePhoneOutputState(!!transfer?.desiredPlaying);
            } catch {
              // Ownership already committed. Keep the decoder unmuted and let the
              // next direct user gesture retry without rolling the session back.
              audio.muted = false;
            }
            renderPlayer();
            showTransferDiagnostic(error);
            notify("Playback moved successfully. Shared status will retry automatically.", true);
            return true;
          }

          audio.muted = false;
          if (!audio.paused) {
            suppressNextPauseSync = true;
            audio.pause();
          }
          renderPlayer();
          showTransferDiagnostic(error);
          notify("Playback move cancelled. The original device was left unchanged.", true);
          return false;
        }
      }
      async function loadLocalEpisode(id, positionMs = 0, autoplay = true) {
        id = Number(id);
        try {
          if (autoplay && serverReachable && !thisPhoneOwnsSession())
            return await startLocalEpisodeFromGesture(id, positionMs);

          if (localEpisode && Number(localEpisode.id) !== id) {
            await saveLocalProgress(false);
            await syncPendingProgress();
          }
          const record = downloadedRecords.get(id) || (await dbGet("downloads", id)),
            shared = sharedStartPoint(id, positionMs),
            initialResume = shared.remote
              ? Math.max(0, Number(shared.positionMs || 0))
              : synchronousResumeMs(id, shared.positionMs, record);
          await hydrateLocalEpisode(id, initialResume, record, !!shared.remote);
          await prepareCanonicalAudio(id, initialResume, record);
          audio.playbackRate = Math.max(0.5, Math.min(3, Number(shared.speed || 1)));
          if (autoplay) {
            await audio.play();
            if (serverReachable && thisPhoneOwnsSession()) {
              await syncWebPlayback(false, false);
              await saveLocalProgress(false, { allowRewind: true });
            }
          }
          renderPlayer();
          return true;
        } catch (error) {
          notify(error?.message || "This broadcast could not be opened.", true);
          return false;
        }
      }
      async function prepareDormantPhoneDecoder(shared) {
        const id = Number(shared?.episodeId || 0);
        // Safari requires decoder replacement and playback to remain inside the
        // listener's tap. A speculative paused load can repeatedly fail after
        // ownership moves away, which used to leave the Move control disabled
        // behind an endless preparation spinner. The transactional tap path
        // already creates and proves the positioned decoder safely on iOS.
        if (!id || isIosWebKit || thisPhoneOwnsSession() || phoneTransferInProgress) return;
        // Once the canonical part is loaded, keep its dormant playhead aligned with
        // a synchronous seek. This avoids starting a new asynchronous preparation
        // on every 500 ms session poll and leaves the Move control steadily ready.
        if (
          dormantDecoderReadyFor(id) &&
          (isIosWebKit || setLogicalPositionImmediately(shared.positionMs))
        ) {
          dormantDecoderReadyAt = Date.now();
          dormantDecoderReadyPositionMs = currentLogicalPositionMs();
          audio.playbackRate = Math.max(0.5, Math.min(3, Number(shared.speed || 1)));
          return;
        }
        // Only one decoder-preparation operation may manipulate the shared audio
        // element at once. If ownership changes mid-load, the next 500 ms poll will
        // prepare the new source after this operation has safely completed.
        if (dormantPreparationPromise) return dormantPreparationPromise;
        dormantPreparationEpisodeId = id;
        if (Number(dormantDecoderReadyEpisodeId || 0) !== id) {
          dormantDecoderReadyEpisodeId = 0;
          dormantDecoderReadyAt = 0;
          dormantDecoderReadyPositionMs = 0;
          renderPlayer();
        }
        dormantPreparationPromise = (async () => {
          try {
            // Dormant handoff preparation is also canonical while connected.
            // Offline blobs remain available for explicit offline playback, but they
            // are not proof that the full canonical multipart timeline is ready.
            const record = null;
            await hydrateLocalEpisode(id, shared.positionMs, record, true, false);
            // On iOS the dormant decoder is only a warm manifest/source permit.
            // Repeated seeks while paused are unsafe for long range-backed MP3s;
            // the direct Move tap refreshes and aligns a new running decoder.
            const dormantPositionMs = isIosWebKit ? 0 : shared.positionMs;
            await prepareCanonicalAudio(id, dormantPositionMs, record);
            audio.playbackRate = Math.max(0.5, Math.min(3, Number(shared.speed || 1)));
            if (phoneTransferInProgress) return;
            if (!audio.paused) {
              suppressNextPauseSync = true;
              audio.pause();
            }
            const currentShared = projectedSessionState();
            if (!thisPhoneOwnsSession() && Number(currentShared?.episodeId || 0) === id) {
              dormantDecoderReadyEpisodeId = id;
              dormantDecoderReadyAt = Date.now();
              dormantDecoderReadyPositionMs = currentLogicalPositionMs();
            }
          } catch (error) {
            if (Number(dormantDecoderReadyEpisodeId || 0) === id) {
              dormantDecoderReadyEpisodeId = 0;
              dormantDecoderReadyAt = 0;
              dormantDecoderReadyPositionMs = 0;
            }
            recordDiagnostic("handoff-preload", "Dormant target preparation failed", {
              episodeId: id,
              message: error?.message || String(error),
            });
          }
        })().finally(() => {
          dormantPreparationPromise = null;
          dormantPreparationEpisodeId = 0;
          renderPlayer();
        });
        return dormantPreparationPromise;
      }

      audio.addEventListener("loadedmetadata", () => {
        audioSourceReady = true;
        renderPlayer();
      });
      audio.addEventListener("canplay", () => {
        audioSourceReady = true;
        renderPlayer();
      });
      audio.addEventListener("error", () => {
        audioSourceReady = false;
        if (phoneTransferInProgress) return;
        notify(
          currentAudioSource === "download"
            ? "This downloaded file could not be opened. Remove it and download it again."
            : "The canonical audio stream could not be opened.",
          true,
        );
      });
      audio.addEventListener("play", () => {
        renderPlayer();
        if (!phoneTransferInProgress && thisPhoneOwnsSession()) {
          saveLocalProgress(false, { allowRewind: true });
          syncWebPlayback(false, false);
        }
        setMediaSession();
      });
      audio.addEventListener("pause", () => {
        const suppressServerSync = suppressNextPauseSync;
        suppressNextPauseSync = false;
        renderPlayer();
        if (!phoneTransferInProgress && thisPhoneOwnsSession()) {
          saveLocalProgress(false, { allowRewind: true });
          if (!suppressServerSync) syncWebPlayback(false, false);
          syncPendingProgress();
        }
      });
      audio.addEventListener("timeupdate", () => {
        if (!seeking) renderPlayer();
        const now = Date.now();
        if (
          !phoneTransferInProgress &&
          thisPhoneOwnsSession() &&
          !audio.paused &&
          now - lastWebHeartbeat >= 1000
        ) {
          lastWebHeartbeat = now;
          syncWebPlayback(false, false);
        }
        if (
          !phoneTransferInProgress &&
          now - lastDurableProgressSave >= 5000
        ) {
          saveLocalProgress(false, { allowRewind: thisPhoneOwnsSession() })
            .then(() => syncPendingProgress())
            .catch(() => {});
        }
      });
      audio.addEventListener("ratechange", () => {
        renderPlayer();
        if (phoneTransferInProgress || !thisPhoneOwnsSession()) return;
        saveLocalProgress(false, { allowRewind: true });
        syncWebPlayback(false, false);
      });
      audio.addEventListener("ended", async () => {
        if (canonicalPartChangeInProgress || phoneTransferInProgress) return;
        const parts = Array.isArray(localManifest?.parts) ? localManifest.parts : [];
        if (thisPhoneOwnsSession() && localManifestPartIndex >= 0 && localManifestPartIndex < parts.length - 1) {
          canonicalPartChangeInProgress = true;
          try {
            const next = parts[localManifestPartIndex + 1];
            await prepareCanonicalAudio(localEpisode.id, Number(next.logicalStartMs || 0), localManifestRecord);
            await audio.play();
            await syncWebPlayback(false, false);
          } catch (error) {
            notify(error?.message || "The next recording part could not be opened.", true);
          } finally {
            canonicalPartChangeInProgress = false;
          }
          return;
        }
        renderPlayer();
        if (thisPhoneOwnsSession()) {
          await syncWebPlayback(true, false);
          await saveLocalProgress(true, { allowRewind: true });
          await syncPendingProgress();
        }
      });
      async function syncWebPlayback(completed = false, explicitSeek = false) {
        if (
          !localEpisode ||
          phoneTransferInProgress ||
          !serverReachable ||
          !thisPhoneOwnsSession()
        ) return false;
        try {
          const response = await request("/player/web-progress", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              clientId,
              episodeId: Number(localEpisode.id),
              positionMs: currentLogicalPositionMs(),
              durationMs: canonicalDurationMs(),
              isPlaying: !audio.paused,
              speed: audio.playbackRate || 1,
              completed: !!completed,
              force: false,
              deviceName: playbackDeviceName,
              deviceKind: playbackDeviceKind,
              expectedGeneration: Number(sessionState?.generation || 0),
              explicitSeek: !!explicitSeek,
            }),
          });
          serverReachable = true;
          webState = response.result.player;
          webStateReceivedAt = Date.now();
          sessionState = {
            ...(sessionState || {}),
            player: webState,
            phone: webState,
            desktop: desktopState,
            ownerDevice: playbackDeviceName,
            ownerClientId: clientId,
          };
          renderPlayer();
          return true;
        } catch (error) {
          if (!error.status) {
            serverReachable = false;
            applyConnectivityUi();
            return false;
          }
          if (error.status === 409) {
            if (!audio.paused) {
              suppressNextPauseSync = true;
              audio.pause();
            }
            notify(error.message, true);
            loadPlayerState().catch(() => {});
          }
          return false;
        }
      }
      async function sendDesktop(command, extra = {}, force = false) {
        try {
          const d = await request("/player/command", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              command,
              clientId,
              episodeId:
                extra.episodeId || sessionState?.player?.episodeId || desktopState.episodeId || null,
              expectedRevision: desktopState.revision || 0,
              force,
              deviceName: playbackDeviceName,
              deviceKind: playbackDeviceKind,
              ...extra,
            }),
          });
          desktopState = d.result.player;
          desktopStateReceivedAt = Date.now();
          renderPlayer();
          return d.result;
        } catch (e) {
          if (e.data?.result?.player) {
            desktopState = e.data.result.player;
            desktopStateReceivedAt = Date.now();
          }
          renderPlayer();
          notify(e.message, true);
          throw e;
        }
      }
      async function playNextQueued() {
        try {
          const d = await request("/queue"),
            item = d.queue?.[0];
          if (!item) {
            notify("The queue is empty.", true);
            return;
          }
          if (localEpisode) await syncWebPlayback();
          if (await loadLocalEpisode(item.episode.id, 0, true)) {
            await request("/queue/" + item.queueId + "/remove", {
              method: "POST",
            });
            notify("Playing next queued broadcast");
          }
        } catch (e) {
          notify(e.message, true);
        }
      }
      function ownerDevice() {
        return String(sessionState?.ownerDevice || "None");
      }
      function ownerClientId() {
        return String(sessionState?.ownerClientId || "");
      }
      function serverOwnsSession() {
        return !ownerClientId() || /^(server|desktop)$/i.test(ownerDevice());
      }
      function thisPhoneOwnsSession() {
        return !!ownerClientId() && ownerClientId() === clientId;
      }
      function ownerDeviceRecord() {
        const devices = Array.isArray(sessionState?.devices) ? sessionState.devices : [];
        return devices.find((device) => String(device?.deviceId || "") === ownerClientId()) || null;
      }
      function ownerDeviceKind() {
        if (serverOwnsSession()) return "Server";
        return String(ownerDeviceRecord()?.kind || "Phone");
      }
      function ownerLocationText() {
        if (serverOwnsSession()) return "Radio Vault server";
        if (thisPhoneOwnsSession()) return `this ${playbackDeviceName}`;
        return ownerDevice() && ownerDevice() !== "None" ? ownerDevice() : "another device";
      }
      function inactiveOutputIsActive() {
        // During an explicit PC-to-phone transfer the server may briefly still
        // report Desktop as owner while Safari is starting its muted decoder
        // and the first authoritative phone heartbeat is in flight. Treat this
        // short claim window as locally active so a routine player refresh
        // cannot pause the decoder before ownership is accepted.
        if (localTakeoverPending) return false;
        return (
          serverReachable &&
          !!sessionState?.player?.episodeId &&
          !thisPhoneOwnsSession()
        );
      }
      function projectedPlaybackState(base, receivedAt, device) {
        base = base || {};
        const durationMs = Math.max(0, Number(base.durationMs || 0)),
          speed = Math.max(0.5, Math.min(3, Number(base.speed || 1))),
          elapsedMs =
            base.isPlaying && receivedAt
              ? Math.max(0, Date.now() - receivedAt) * speed
              : 0,
          positionMs = Math.max(
            0,
            Math.min(
              durationMs || Number.MAX_SAFE_INTEGER,
              Number(base.positionMs || 0) + elapsedMs,
            ),
          );
        return {
          episodeId: Number(base.episodeId || 0) || null,
          show: base.show || "",
          title: base.title || "",
          positionMs,
          durationMs,
          isPlaying: !!base.isPlaying,
          status: base.status || (base.isPlaying ? "Playing" : "Paused"),
          speed,
          device,
        };
      }
      function projectedDesktopState() {
        return projectedPlaybackState(desktopState, desktopStateReceivedAt, "Server");
      }
      function projectedPhoneState() {
        return projectedPlaybackState(webState, webStateReceivedAt, ownerDevice());
      }
      function projectedSessionState() {
        return serverOwnsSession()
          ? projectedDesktopState()
          : projectedPhoneState();
      }
      function activeState() {
        if (inactiveOutputIsActive()) return projectedSessionState();
        if (thisPhoneOwnsSession() && !localEpisode) return projectedPhoneState();
        return {
          episodeId: localEpisode?.id || null,
          show: localEpisode?.show || "",
          title: localEpisode?.title || "",
          positionMs: currentLogicalPositionMs(),
          durationMs: canonicalDurationMs(),
          isPlaying: !!localEpisode && !audio.paused,
          status: localEpisode?.status || "Idle",
          speed: audio.playbackRate || 1,
          device: "Phone",
        };
      }
      function setPlayerPresenceControls(inactive, has, episodeId) {
        const preparingDormantTarget = !isIosWebKit && inactive && has &&
          (!dormantDecoderReadyFor(episodeId) ||
           (dormantPreparationPromise && Number(dormantPreparationEpisodeId || 0) === Number(episodeId || 0)));
        playerPlay.disabled = !has || phoneTransferInProgress || preparingDormantTarget;
        $("playerBack").disabled = !has || inactive || phoneTransferInProgress;
        $("playerForward").disabled = !has || inactive || phoneTransferInProgress;
        playerSeek.disabled = !has || inactive || phoneTransferInProgress;
        playerSpeed.disabled = !has || inactive || phoneTransferInProgress;
        $("playerFavourite").disabled = !localEpisode || inactive;
        $("playerListened").disabled = !localEpisode || inactive;
        playerDownload.disabled = !localEpisode || inactive;
        $("playerNext").disabled = !has || inactive;
      }
      function renderPlayer() {
        const s = activeState(),
          has = !!s.episodeId,
          inactive = inactiveOutputIsActive(),
          owner = ownerDevice(),
          percent = s.durationMs
            ? Math.max(0, Math.min(100, (s.positionMs * 100) / s.durationMs))
            : 0,
          art = has
            ? inactive
              ? serverReachable
                ? auth("/artwork/" + s.episodeId)
                : ""
              : downloadedArtworkUrls.get(Number(s.episodeId)) ||
                (serverReachable ? auth("/artwork/" + s.episodeId) : "")
            : "";
        miniPlayer.classList.add("visible");
        miniPlayer.classList.toggle("idle", !has);
        miniPlayer.classList.toggle("inactiveOutput", inactive);
        fullPlayer.classList.toggle("inactiveOutput", inactive);
        miniArt.src = art || "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 64 64'%3E%3Crect width='64' height='64' rx='14' fill='%23171717'/%3E%3Crect x='5' y='5' width='54' height='54' rx='11' fill='none' stroke='%23f7c948' stroke-width='4'/%3E%3Ctext x='32' y='39' text-anchor='middle' font-family='Arial,sans-serif' font-size='20' font-weight='700' fill='%23f7c948'%3ERV%3C/text%3E%3C/svg%3E";
        miniArt.style.visibility = "visible";
        const preparingDormantTarget = !isIosWebKit && inactive && has &&
          (!dormantDecoderReadyFor(s.episodeId) ||
           (dormantPreparationPromise && Number(dormantPreparationEpisodeId || 0) === Number(s.episodeId || 0)));
        miniPlay.disabled = !has || phoneTransferInProgress || preparingDormantTarget;
        if (phoneTransferInProgress) setButtonIcon(miniPlay, "spinner", "Preparing playback");
        else if (preparingDormantTarget) setButtonIcon(miniPlay, "spinner", "Preparing this device");
        else if (inactive) setButtonIcon(miniPlay, "transferPhone", "Move to this device");
        else setButtonIcon(miniPlay, s.isPlaying ? "pause" : "play", s.isPlaying ? "Pause" : "Play");
        miniShow.textContent = inactive
          ? `${ownerLocationText()} · ${s.show || "Radio Vault"}`
          : s.show || "Radio Vault";
        miniTitle.textContent = s.title || "Choose a broadcast";
        miniSeek.max = Math.max(1, s.durationMs || 1);
        if (!seeking) miniSeek.value = Math.max(0, s.positionMs || 0);
        miniSeek.style.setProperty("--seek", percent + "%");
        miniElapsed.textContent = fmtMs(s.positionMs || 0);
        miniDuration.textContent = fmtMs(s.durationMs || 0);
        miniSpeed.textContent = `${Number(s.speed || 1).toLocaleString(undefined, { maximumFractionDigits:2 })}\u00d7`;
        $("miniBack").disabled = !has || inactive || phoneTransferInProgress;
        $("miniForward").disabled = !has || inactive || phoneTransferInProgress;
        miniSeek.disabled = !has || inactive || phoneTransferInProgress;
        miniSpeed.disabled = !localEpisode || inactive || phoneTransferInProgress;
        $("miniInfo").disabled = !has;
        miniFavourite.disabled = !localEpisode || inactive || phoneTransferInProgress;
        miniMoment.disabled = !localEpisode || inactive || phoneTransferInProgress;
        if (has) {
          miniFavourite.dataset.favourite = String(s.episodeId);
          miniFavourite.dataset.value = String(!localEpisode?.favourite);
          miniMoment.dataset.addMoment = String(s.episodeId);
        } else {
          delete miniFavourite.dataset.favourite;
          delete miniFavourite.dataset.value;
          delete miniMoment.dataset.addMoment;
        }
        miniFavourite.querySelector("path")?.setAttribute("fill", localEpisode?.favourite ? "currentColor" : "none");
        miniFavourite.setAttribute("aria-label", localEpisode?.favourite ? "Remove from favourites" : "Add to favourites");
        miniVolume.value = String(audio.volume);
        playerArt.src = art;
        playerArt.style.visibility = has ? "visible" : "hidden";
        playerContextTitle.textContent = "Now Playing";
        playerShow.textContent = s.show || "Radio Vault";
        playerTitle.textContent = s.title || "Choose a broadcast";
        const ownerKind = ownerDeviceKind().toLowerCase();
        const stateIcon = serverOwnsSession() || ownerKind.includes("desktop")
          ? rvIcons.deviceDesktop
          : rvIcons.devicePhone;
        const stateLocation = ownerLocationText();
        playerDeviceState.innerHTML = has
          ? `${stateIcon}${s.isPlaying ? "Playing" : "Paused"} on ${stateLocation} · ${Number(s.speed || 1).toLocaleString(undefined, { maximumFractionDigits: 2 })}×`
          : "Ready";
        if (phoneTransferInProgress) setButtonIcon(playerPlay, "spinner", "Preparing playback");
        else if (preparingDormantTarget) setButtonIcon(playerPlay, "spinner", "Preparing this device");
        else if (inactive) setButtonIcon(playerPlay, "transferPhone", "Move to this device");
        else setButtonIcon(playerPlay, s.isPlaying ? "pause" : "play", s.isPlaying ? "Pause" : "Play");
        playerSeek.max = Math.max(1, s.durationMs || 1);
        if (!seeking) playerSeek.value = Math.max(0, s.positionMs || 0);
        playerSeek.style.setProperty("--seek", percent + "%");
        playerElapsed.textContent = fmtMs(s.positionMs);
        playerRemaining.textContent =
          "-" + fmtMs(Math.max(0, (s.durationMs || 0) - (s.positionMs || 0)));
        playerSpeed.value = String(Number(s.speed || 1));
        $("playerInfo").disabled = !has;
        $("playerFavourite").textContent = localEpisode?.favourite
          ? "♥ Unfavourite"
          : "♡ Favourite";
        $("playerListened").textContent =
          localEpisode?.status === "Completed"
            ? "Mark unlistened"
            : "Mark listened";
        playerNotice.textContent = phoneTransferInProgress
          ? "Preparing and verifying this device while the current output remains unchanged…"
          : preparingDormantTarget
            ? `Preparing the canonical audio on this device. Playback remains unchanged on ${stateLocation}.`
          : inactive
            ? `Playback is ${s.isPlaying ? "playing" : "paused"} on ${stateLocation}. Choose Move to this device to move it here; other transport controls stay locked.`
          : currentAudioSource === "download"
            ? serverReachable
              ? "Playing the downloaded copy stored on this phone."
              : "Playing offline. Listening progress will sync when Radio Vault reconnects."
            : has
              ? `This ${playbackDeviceName} owns the shared playback session.`
              : "Choose a broadcast to begin.";
        updateDownloadButtons();
        setPlayerPresenceControls(inactive, has, s.episodeId);
        applyMediaSessionOwnership(inactive);
      }
      function showFullPlayer() {
        const wasOpen = fullPlayer.classList.contains("open");
        fullPlayer.classList.add("open");
        renderPlayer();
        if (!wasOpen) focusOverlay(fullPlayer, $("playerClose"));
      }
      function hideFullPlayer() {
        fullPlayer.classList.remove("open");
        restoreOverlayFocus();
      }
      async function togglePlayback() {
        if (inactiveOutputIsActive()) {
          const remote = projectedSessionState();
          if (!remote.episodeId) return;
          const moved = await startLocalEpisodeFromGesture(
            remote.episodeId,
            remote.positionMs,
          );
          if (moved) notify("Playback moved to this device");
          return;
        }
        if (!localEpisode && thisPhoneOwnsSession()) {
          const shared = projectedPhoneState();
          if (shared.episodeId)
            await startLocalEpisodeFromGesture(shared.episodeId, shared.positionMs);
          return;
        }
        if (!localEpisode) return;
        if (audio.paused) {
          try {
            await audio.play();
            playErrorShown = false;
          } catch (e) {
            if (!playErrorShown) {
              playErrorShown = true;
              notify(
                currentAudioSource === "download"
                  ? "Safari could not start the downloaded file. Try removing and downloading it again."
                  : "Tap Play to start audio.",
                true,
              );
            }
          }
        } else audio.pause();
      }
      async function seekLogical(positionMs, explicitSeek = true) {
        if (inactiveOutputIsActive() || !localEpisode || phoneTransferInProgress) return;
        const target = Math.max(0, Math.min(canonicalDurationMs() || Number.MAX_SAFE_INTEGER, Number(positionMs || 0))),
          wasPlaying = !audio.paused;
        const positionedLogicalStart = Number(currentAudioLogicalBaseMs || 0);
        let positionedGesturePrime = null;
        if (
          isIosWebKit &&
          !localManifestRecord &&
          currentAudioIsPositioned &&
          target < positionedLogicalStart - 250
        ) {
          // A positioned stream deliberately begins at its resume point. If the
          // listener seeks earlier, open another positioned representation in
          // this seek gesture rather than asking Safari to rewind before byte 0.
          assignCanonicalGestureStartSource(localEpisode.id, target);
          if (wasPlaying) positionedGesturePrime = audio.play();
        }
        await prepareCanonicalAudio(localEpisode.id, target, localManifestRecord);
        if (positionedGesturePrime) await positionedGesturePrime;
        if (wasPlaying && audio.paused) await audio.play();
        renderPlayer();
        await syncWebPlayback(false, explicitSeek);
        await saveLocalProgress(false, {
          allowRewind: thisPhoneOwnsSession(),
          explicitSeek: !!explicitSeek,
        });
        await syncPendingProgress();
      }
      async function skip(seconds) {
        if (inactiveOutputIsActive() || !localEpisode) return;
        await seekLogical(currentLogicalPositionMs() + Number(seconds || 0) * 1000, true);
      }
      function setMediaSession() {
        if (!("mediaSession" in navigator) || !localEpisode) return;
        try {
          navigator.mediaSession.metadata = new MediaMetadata({
            title: localEpisode.title,
            artist: localEpisode.show,
            album: "Radio Vault",
            artwork: episodeArt(localEpisode)
              ? [{ src: episodeArt(localEpisode), sizes: "512x512" }]
              : [],
          });
          navigator.mediaSession.playbackState = audio.paused ? "paused" : "playing";
          navigator.mediaSession.setActionHandler("play", () => audio.play());
          navigator.mediaSession.setActionHandler("pause", () => audio.pause());
          navigator.mediaSession.setActionHandler("seekbackward", (d) =>
            skip(-(d.seekOffset || 15)),
          );
          navigator.mediaSession.setActionHandler("seekforward", (d) =>
            skip(d.seekOffset || 30),
          );
          navigator.mediaSession.setActionHandler("seekto", (d) => {
            if (d.seekTime != null && !inactiveOutputIsActive())
              seekLogical(Number(d.seekTime) * 1000, true);
          });
        } catch {}
      }
      function applyMediaSessionOwnership(inactive) {
        if (!("mediaSession" in navigator)) return;
        try {
          if (inactive) {
            navigator.mediaSession.playbackState = "none";
            for (const action of [
              "play",
              "pause",
              "seekbackward",
              "seekforward",
              "seekto",
              "nexttrack",
              "previoustrack",
            ]) {
              try { navigator.mediaSession.setActionHandler(action, null); } catch {}
            }
          } else if (localEpisode) {
            setMediaSession();
          }
        } catch {}
      }
      async function loadPlayerState() {
        const requestEpoch = playerStateEpoch;
        const requestId = ++playerStateRequestId;
        try {
          const [pd, jd] = await Promise.all([
            request("/player"),
            request("/jobs"),
          ]);
          // A transfer can complete while an older player request is in flight.
          // Discard that pre-transfer response rather than letting it restore
          // the previous owner for a single frame and pause the new output.
          if (requestEpoch !== playerStateEpoch || requestId < lastAppliedPlayerStateRequestId)
            return;
          lastAppliedPlayerStateRequestId = requestId;
          desktopState = pd.desktop || desktopState;
          desktopStateReceivedAt = Date.now();
          webState = pd.web || webState;
          webStateReceivedAt = Date.now();
          sessionState = pd.session || {
            player: pd.player || pd.desktop || desktopState,
            ownerDevice: pd.web?.controllerClientId ? (pd.web?.device || "Phone") : "Server",
            ownerClientId: pd.web?.controllerClientId || "",
            generation: 0,
          };
          if (await acknowledgeCommittedSourceStop(sessionState)) {
            // The acknowledgement response carries the newest session, but retain
            // this fallback if a legacy response omitted it.
            sessionState = sessionState || pd.session;
          }

          // Ownership is authoritative even while paused. If another output
          // takes the session, stop this phone without reporting the dormant
          // decoder as progress. The committed transaction already persisted the
          // outgoing source boundary. Then quietly prepare the canonical target so
          // the next user gesture can begin playback immediately.
          const inactive = inactiveOutputIsActive();
          if (inactive) {
            const shared = projectedSessionState(),
              stoppedAudiblePhone = !audio.paused;
            if (stoppedAudiblePhone) {
              suppressNextPauseSync = true;
              audio.pause();
            }
            if (!isIosWebKit && shared?.episodeId && !phoneTransferInProgress)
              prepareDormantPhoneDecoder(shared).catch(() => {});
            if (stoppedAudiblePhone) {
              const noticeNow = Date.now();
              if (noticeNow - lastOwnershipNoticeAt > 3000) {
                lastOwnershipNoticeAt = noticeNow;
                notify(
                  serverOwnsSession()
                    ? "Playback moved to the Radio Vault server"
                    : `Playback moved to ${ownerLocationText()}`,
                );
              }
            }
          }

          const active = jd.jobs.filter(
              (j) => j.state === "Running" || j.state === "Queued",
            ),
            primary = active.find((j) => j.state === "Running") || active[0];
          activeJobId =
            (
              active.find((j) => j.state === "Running" && j.canCancel) ||
              active.find((j) => j.canCancel)
            )?.jobId || null;
          cancelJob.hidden = !activeJobId;
          const shared = projectedSessionState();
          serverState.textContent = active.length
            ? `${active.length} background task${active.length === 1 ? "" : "s"} · ${primary.name}${primary.percent == null ? "" : " " + Math.round(primary.percent) + "%"}`
            : shared?.episodeId
              ? `${shared.isPlaying ? "Playing" : "Paused"} on ${ownerLocationText()} · ${shared.show || shared.title || "Radio Vault"}`
              : connectedLabel();
          serverState.classList.toggle(
            "live",
            active.length > 0 || !!shared?.isPlaying,
          );
          serverReachable = true;
          applyConnectivityUi();
          renderPlayer();
          repairDownloadedArtwork().catch(() => {});
          try {
            await syncAllPending();
          } catch (error) {
            recordDiagnostic("sync-storage", "Pending device changes could not be inspected during live refresh");
          }
        } catch {
          serverReachable = false;
          applyConnectivityUi();
          activeJobId = null;
          cancelJob.hidden = true;
          serverState.textContent = downloadedRecords.size
            ? "Offline · downloaded broadcasts available"
            : "Waiting for Radio Vault…";
          serverState.classList.remove("live");
          renderPlayer();
        }
      }
      async function pollChanges() {
        try {
          const d = await request(
            "/events?after=" + lastSequence + "&limit=100",
          );
          lastSequence = Math.max(lastSequence, d.sequence || 0);
          if (!d.changes.length) return;
          await loadPlayerState();
          const episodeChanges = d.changes.filter((c) => c.episodeId);
          if (
            d.changes.some((c) =>
              [
                "library",
                "research",
                "metadata",
                "favourite",
                "listening-status",
                "queue",
                "transcription",
                "transcription-control",
              ].includes(c.kind),
            )
          )
            load();
          if (
            currentDetailId &&
            episodeChanges.some((c) => c.episodeId === currentDetailId)
          )
            openDetails(currentDetailId, false);
        } catch {}
      }
      function closeDetails(back = true) {
        currentDetailId = null;
        currentDetails = null;
        detail.classList.remove("open");
        restoreOverlayFocus();
        if (back && location.pathname.startsWith("/broadcast/"))
          history.pushState({}, "", auth("/"));
      }
      $("detailBack").addEventListener("click", () => closeDetails());
      window.addEventListener("popstate", () => {
        const m = location.pathname.match(/^\/broadcast\/(\d+)/);
        m ? openDetails(Number(m[1]), false) : closeDetails(false);
      });
      document.body.addEventListener("keydown", async (event) => {
        if (event.key === "Enter" && event.target?.id === "wikiSearchInput") {
          event.preventDefault(); await runWikiSearch(); return;
        }
        if ((event.key === "Enter" || event.key === " ") && event.target?.classList?.contains("chip")) {
          event.preventDefault(); await openWikiEntity(String(event.target.textContent || "").split(/\s+\u00b7\s+/)[0].trim());
        }
      });
      search.addEventListener("input", () => {
        clearTimeout(timer);
        timer = setTimeout(() => {
          if (view !== "library" && view !== "downloaded") view = "library";
          if (view === "library") navMode = search.value.trim() ? "search" : "library";
          saveNavigationState();
          updateViewChrome();
          load();
        }, 220);
      });
      function libraryFilterChanged() {
        if (view !== "library" && view !== "downloaded") view = "library";
        if (view === "library" && !search.value.trim()) navMode = "library";
        bootstrapDashboardPending = false;
        saveNavigationState();
        updateViewChrome();
        load();
      }
      for (const control of [show, year, month, exactDate, statusFilter])
        control.addEventListener("change", libraryFilterChanged);
      $("clearFilters").addEventListener("click", () => {
        show.value = "";
        year.value = "";
        month.value = "";
        exactDate.value = "";
        statusFilter.value = "";
        search.value = "";
        libraryView = "library";
        libraryFilterChanged();
      });
      menuToggle.addEventListener("click", () =>
        setMenuOpen(!document.body.classList.contains("menuOpen")),
      );
      menuScrim.addEventListener("click", () => setMenuOpen(false));
      primaryNav.addEventListener("click", (e) => {
        if (!e.target.closest("button")) return;
        setMenuOpen(false);
        const button = e.target.closest("[data-section]");
        if (!button) return;
        navMode = button.dataset.navKey || button.dataset.section;
        if (button.dataset.section === "library") libraryView = "library";
        setPrimaryView(button.dataset.section);
      });
      libraryViewChips.addEventListener("click", (e) => {
        const button = e.target.closest("[data-library-view]");
        if (!button) return;
        libraryView = button.dataset.libraryView;
        view = "library";
        navMode = libraryView === "favorites" ? "favourites" : "library";
        lastConnectedView = view;
        lastConnectedLibraryView = libraryView;
        saveNavigationState();
        updateViewChrome();
        loadLibrary();
      });
      document.body.addEventListener("click", async (e) => {
        e.target.closest(".libraryOverflowMenu button")?.closest("details")?.removeAttribute("open");
        const wikiPageButton = e.target.closest("[data-wiki-page]");
        if (wikiPageButton) { await openWikiPage(wikiPageButton.dataset.wikiPage); return; }
        const wikiEntity = e.target.closest("[data-wiki-entity], .chip");
        if (wikiEntity) {
          const entity = String(wikiEntity.dataset.wikiEntity || wikiEntity.textContent || "").split(/\s+\u00b7\s+/)[0].trim();
          if (detail.classList.contains("open")) closeDetails(false);
          await openWikiEntity(entity);
          return;
        }
        if (e.target.id === "wikiSearchButton") { await runWikiSearch(); return; }
        if (e.target.id === "wikiBack") { await navigateWikiHistory(-1); return; }
        if (e.target.id === "wikiForward") { await navigateWikiHistory(1); return; }
        if (e.target.id === "wikiHome") { showWikiDashboard(true); return; }
        if (e.target.id === "wikiTimelineExplorer") { await showWikiTimelineExplorer(); return; }
        if (e.target.id === "wikiAutoTopics") { const result = await clientPost("wiki", "topic-auto-cleanup", {}); notify(result?.summary || "Safe topic cleanup complete."); await loadWiki(true); return; }
        if (e.target.id === "wikiBrowseAll") { wikiQuery = ""; wikiPageType = ""; wikiPageStatus = ""; wikiRemember({ kind:"browse", query:"", pageType:"", status:"" }); renderWikiBrowse(wikiPages, ""); return; }
        const wikiEra = e.target.closest("[data-wiki-era]");
        if (wikiEra) { await showWikiTimelineExplorer("", Number(wikiEra.dataset.wikiEra || 0)); return; }
        const dashboardDot = e.target.closest("[data-dashboard-on-this-day]");
        if (dashboardDot && dashboardSnapshot) {
          dashboardOnThisDayIndex = Number(dashboardDot.dataset.dashboardOnThisDay || 0);
          renderNativeConnectedDashboard(dashboardSnapshot.continuing, dashboardSnapshot.recent, dashboardSnapshot.favourites, dashboardSnapshot.onThisDay, dashboardSnapshot.unheard);
          return;
        }
        const navSearch = e.target.closest("[data-nav-search]");
        if (navSearch) {
          navMode = "search";
          libraryView = "library";
          setPrimaryView("library");
          setTimeout(() => search.focus(), 0);
          return;
        }
        const navFavourite = e.target.closest("[data-nav-favourites]");
        if (navFavourite) {
          navMode = "favourites";
          setPrimaryView("library", "favorites");
          return;
        }
        const navShow = e.target.closest("[data-nav-show]");
        if (navShow) {
          navMode = "library";
          libraryView = "library";
          show.value = navShow.dataset.navShow;
          setPrimaryView("library");
          return;
        }
        const navPlayer = e.target.closest("[data-nav-player]");
        if (navPlayer) {
          showFullPlayer();
          return;
        }
        const openSection = e.target.closest("[data-open-section]");
        if (openSection) {
          setPrimaryView(openSection.dataset.openSection);
          return;
        }
        const openLibrary = e.target.closest("[data-open-library-view]");
        if (openLibrary) {
          setPrimaryView("library", openLibrary.dataset.openLibraryView);
          return;
        }
        const researchTab = e.target.closest("[data-research-section]");
        if (researchTab) {
          researchSection = researchTab.dataset.researchSection;
          if (researchSection === "transcription") {
            view = "transcripts";
            navMode = "research";
            saveNavigationState();
            updateViewChrome();
            await loadTranscripts();
            return;
          }
          if (view === "transcripts") {
            view = "research";
            navMode = "research";
            saveNavigationState();
            updateViewChrome();
          }
          await loadResearch();
          return;
        }
        const researchRecordButton = e.target.closest("[data-research-record]");
        if (researchRecordButton) {
          researchRecordButton.disabled = true;
          try { await openResearchRecord(Number(researchRecordButton.dataset.researchRecord)); }
          catch (error) { notify(error.message || "The research record could not be opened.", true); }
          finally { researchRecordButton.disabled = false; }
          return;
        }
        const researchReview = e.target.closest("[data-research-review]");
        if (researchReview) {
          researchReview.disabled = true;
          try {
            await clientPost("research", "set-review", { researchId:Number(researchReview.dataset.researchReview), needsReview:researchReview.dataset.value === "true" });
            notify(researchReview.dataset.value === "true" ? "Research flagged for review" : "Research marked reviewed");
            closeDetails(false);
            await loadResearch(true);
          } catch (error) { notify(error.message || "The research review state could not be saved.", true); }
          finally { researchReview.disabled = false; }
          return;
        }
        const assignResearchDate = e.target.closest("[data-assign-research-date]");
        if (assignResearchDate) {
          const episodeId = Number(assignResearchDate.dataset.assignResearchDate), value = document.getElementById(`research-date-${episodeId}`)?.value;
          if (!value) { notify("Choose a supported broadcast date first.", true); return; }
          assignResearchDate.disabled = true;
          try {
            await request(`/federation/research-workspace/undated/${episodeId}/date`, { method:"POST", headers:{"Content-Type":"application/json"}, body:JSON.stringify({broadcastDate:value}) });
            notify("Broadcast date assigned");
            researchSnapshot = null;
            await loadResearch();
          } catch (error) { notify(error.message || "The broadcast date could not be assigned.", true); }
          finally { assignResearchDate.disabled = false; }
          return;
        }
        if (e.target.id === "previewResearchPack") {
          const file = document.getElementById("researchPackFile")?.files?.[0];
          if (!file) { notify("Choose a deep research pack first.", true); return; }
          e.target.disabled = true;
          try { await previewResearchPackFile(file); notify("Research pack analysis ready"); }
          catch (error) { notify(error.message || "The research pack could not be analysed.", true); }
          finally { e.target.disabled = false; }
          return;
        }
        if (e.target.id === "applyResearchPack") {
          if (!researchPackPreview?.sessionId) return;
          e.target.disabled = true;
          try {
            const response = await request("/federation/research-packs/import/apply", { method:"POST", headers:{"Content-Type":"application/json"}, body:JSON.stringify({sessionId:researchPackPreview.sessionId}), timeoutMs:120000 });
            await pollResearchPackImport(researchPackPreview.sessionId, response.result);
          } catch (error) { notify(error.message || "The research pack could not be imported.", true); }
          finally { e.target.disabled = false; }
          return;
        }
        if (e.target.id === "cancelResearchPack") {
          if (researchPackPreview?.sessionId) await request("/federation/research-packs/import/cancel", { method:"POST", headers:{"Content-Type":"application/json"}, body:JSON.stringify({sessionId:researchPackPreview.sessionId}) }).catch(() => null);
          if (!researchImportJob) researchPackPreview = null;
          renderResearch(); return;
        }
        if (e.target.id === "exportResearchPack") {
          e.target.disabled = true;
          try { await exportResearchPack(); }
          catch (error) { notify(error.message || "The research pack could not be exported.", true); }
          finally { e.target.disabled = false; }
          return;
        }
        if (e.target.id === "savePlaybackPreferences") {
          e.target.disabled = true;
          try {
            const response = await request("/federation/playback-preferences", { method:"POST", headers:{"Content-Type":"application/json"}, body:JSON.stringify({skipBackSeconds:Number(document.getElementById("settingsSkipBack")?.value || 15),skipForwardSeconds:Number(document.getElementById("settingsSkipForward")?.value || 30),completionThresholdSeconds:Number(document.getElementById("settingsCompletion")?.value || 60),updatedAt:new Date().toISOString()}) });
            settingsSnapshot = { ...(settingsSnapshot || {}), playback:response.playbackPreferences };
            notify("Playback settings saved for every client"); renderSettings();
          } catch (error) { notify(error.message || "Playback settings could not be saved.", true); }
          finally { e.target.disabled = false; }
          return;
        }
        if (e.target.id === "runLibraryScan") {
          e.target.disabled = true; e.target.textContent = "Scanning…";
          try { const response = await request("/federation/library-scan", { method:"POST", headers:{"Content-Type":"application/json"}, body:JSON.stringify({trigger:"anywhere-settings"}), timeoutMs:120000 }); notify(response.libraryScan?.message || "Library scan completed"); await loadAnywhereBootstrap(true); await loadSettings(); }
          catch (error) { notify(error.message || "The server Library scan could not be completed.", true); }
          finally { e.target.disabled = false; }
          return;
        }
        if (e.target.id === "settingsReconnect") {
          e.target.disabled = true;
          try { const connected = await probeServer(true); if (!connected) throw new Error("The server is still unreachable."); await refreshAfterReconnect(); notify(`Reconnected to ${connectedLabel()}`); }
          catch (error) { notify(error.message || "Radio Vault could not reconnect.", true); }
          finally { e.target.disabled = false; }
          return;
        }
        if (e.target.id === "settingsDiagnostics") { openWebDiagnostics(); return; }
        const editMetadata = e.target.closest("[data-edit-metadata]");
        if (editMetadata) {
          const details = currentDetails, episode = details?.episode || {};
          if (!details) return;
          const peopleFor = (role) => (details.people || []).filter((person) => String(person.role || "").toLowerCase().includes(role)).map((person) => person.name).join(", ");
          const title = prompt("Broadcast title", episode.title || ""); if (title === null) return;
          const description = prompt("Description or summary", episode.summary || ""); if (description === null) return;
          const notes = prompt("Personal notes", details.personalNotes || ""); if (notes === null) return;
          const edition = prompt("Edition or variant", ""); if (edition === null) return;
          const hosts = prompt("Hosts (comma-separated)", peopleFor("host")); if (hosts === null) return;
          const guests = prompt("Guests (comma-separated)", peopleFor("guest")); if (guests === null) return;
          const callers = prompt("Callers (comma-separated)", peopleFor("caller")); if (callers === null) return;
          const mentionedPeople = prompt("Other people mentioned (comma-separated)", peopleFor("mention")); if (mentionedPeople === null) return;
          const tags = prompt("Topics or tags (comma-separated)", (details.topics || []).join(", ")); if (tags === null) return;
          editMetadata.disabled = true;
          try {
            await request(`/broadcasts/${Number(editMetadata.dataset.editMetadata)}/metadata`, { method:"POST", headers:{"Content-Type":"application/json"}, body:JSON.stringify({title,description,notes,edition,hosts,guests,callers,mentionedPeople,tags}) });
            notify("Broadcast metadata saved on the server");
            await openDetails(Number(editMetadata.dataset.editMetadata), false);
          } catch (error) { notify(error.message || "Broadcast metadata could not be saved.", true); }
          finally { editMetadata.disabled = false; }
          return;
        }
        const editMoment = e.target.closest("[data-edit-moment]");
        if (editMoment) {
          const title = prompt("Moment title", editMoment.dataset.momentTitle || "Moment");
          if (title === null) return;
          const notes = prompt("Notes (optional)", editMoment.dataset.momentNotes || "");
          if (notes === null) return;
          await request(`/moments/${Number(editMoment.dataset.editMoment)}/update`, { method:"POST", headers:{"Content-Type":"application/json"}, body:JSON.stringify({title,notes,clientMutationId:crypto.randomUUID ? crypto.randomUUID() : String(Date.now())}) });
          notify("Moment updated"); await loadMoments(); return;
        }
        const deleteMoment = e.target.closest("[data-delete-moment]");
        if (deleteMoment) {
          if (!confirm("Delete this Moment?")) return;
          await request(`/broadcasts/${Number(deleteMoment.dataset.momentEpisode)}/moments/${Number(deleteMoment.dataset.deleteMoment)}`, { method:"POST" });
          notify("Moment deleted"); await loadMoments(); return;
        }
        const transcriptTab = e.target.closest("[data-transcript-section]");
        if (transcriptTab) {
          transcriptSection = transcriptTab.dataset.transcriptSection;
          renderTranscripts();
          return;
        }
        const openTranscriptButton = e.target.closest("[data-open-transcript]");
        if (openTranscriptButton) {
          openTranscriptButton.disabled = true;
          try { await openTranscript(Number(openTranscriptButton.dataset.openTranscript)); }
          catch (error) { notify(error.message || "The transcript could not be opened.", true); }
          finally { openTranscriptButton.disabled = false; }
          return;
        }
        const transcriptExport = e.target.closest("[data-transcript-export]");
        if (transcriptExport) {
          exportTranscript(transcriptExport.dataset.transcriptExport);
          return;
        }
        const installTranscription = e.target.closest("[data-transcription-install]");
        if (installTranscription) {
          installTranscription.disabled = true;
          installTranscription.textContent = "Installing…";
          try {
            await clientPost("transcription", "install-recommended", { modelId:"base.en" });
            notify("Server transcription setup installed");
            await loadTranscripts();
          } catch (error) { notify(error.message || "Transcription setup could not be installed.", true); }
          finally { installTranscription.disabled = false; }
          return;
        }
        const transcriptionAction = e.target.closest("[data-transcription-action]");
        if (transcriptionAction) {
          transcriptionAction.disabled = true;
          try {
            await clientPost("transcription", transcriptionAction.dataset.transcriptionAction, { jobId:transcriptionAction.dataset.jobId });
            notify(`Transcription ${transcriptionAction.dataset.transcriptionAction} requested`);
            await loadTranscripts();
          } catch (error) { notify(error.message || "The transcription job could not be changed.", true); }
          finally { transcriptionAction.disabled = false; }
          return;
        }
        const batchAction = e.target.closest("[data-batch-action]");
        if (batchAction) {
          batchAction.disabled = true;
          try {
            await clientPost("transcription", `batch-${batchAction.dataset.batchAction}`, { batchId:batchAction.dataset.batchId });
            notify(`Batch ${batchAction.dataset.batchAction} requested`);
            await loadTranscripts();
          } catch (error) { notify(error.message || "The batch could not be changed.", true); }
          finally { batchAction.disabled = false; }
          return;
        }
        const openBatch = e.target.closest("[data-open-batch]");
        if (openBatch) {
          if (String(selectedBatchId) === String(openBatch.dataset.openBatch)) {
            selectedBatchId = "";
            selectedBatchItems = [];
          } else {
            selectedBatchId = openBatch.dataset.openBatch;
            selectedBatchItems = await clientPost("transcription", "batch-items", { batchId:selectedBatchId }) || [];
          }
          renderTranscripts();
          return;
        }
        const transcribeFull = e.target.closest("[data-transcribe-full]");
        if (transcribeFull) {
          transcribeFull.disabled = true;
          try { await queueWebTranscription(Number(transcribeFull.dataset.transcribeFull), false); }
          catch (error) { notify(error.message || "The transcription could not be queued.", true); }
          finally { transcribeFull.disabled = false; }
          return;
        }
        const transcribeSample = e.target.closest("[data-transcribe-sample]");
        if (transcribeSample) {
          transcribeSample.disabled = true;
          try { await queueWebTranscription(Number(transcribeSample.dataset.transcribeSample), true); }
          catch (error) { notify(error.message || "The sample could not be queued.", true); }
          finally { transcribeSample.disabled = false; }
          return;
        }
        const dl = e.target.closest("[data-download]");
        if (dl) {
          await startDownload(dl.dataset.download);
          return;
        }
        const p = e.target.closest("[data-play]");
        if (p) {
          await startLocalEpisodeFromGesture(
            p.dataset.play,
            Number(p.dataset.seek || 0) * 1000 ||
              Number(p.dataset.position || 0),
          );
          return;
        }
        const i = e.target.closest("[data-info]");
        if (i) {
          openDetails(Number(i.dataset.info));
          return;
        }
        const q = e.target.closest("[data-queue]");
        if (q) {
          await mutateOrJournal("queue-add", "/queue/add", { episodeId:Number(q.dataset.queue), playNext:false }, "queue:"+q.dataset.queue);
          notify("Added to queue");
          if (view === "queue") loadQueue();
          return;
        }
        const qr = e.target.closest("[data-queue-remove]");
        if (qr) {
          await request("/queue/" + qr.dataset.queueRemove + "/remove", {
            method: "POST",
          });
          loadQueue();
          return;
        }
        const qm = e.target.closest("[data-queue-move]");
        if (qm) {
          await request("/queue/" + qm.dataset.queueMove + "/move", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ direction: Number(qm.dataset.direction) }),
          });
          loadQueue();
          return;
        }
        if (e.target.id === "clearQueue") {
          await request("/queue/clear", { method: "POST" });
          loadQueue();
          return;
        }
        const addMoment = e.target.closest("[data-add-moment]");
        if (addMoment) {
          const state = activeState();
          const positionMs = state && Number(state.episodeId) === Number(addMoment.dataset.addMoment) ? Number(state.positionMs || 0) : 0;
          const title = prompt("Moment title", "Moment");
          if (title === null) return;
          const notes = prompt("Notes (optional)", "") || "";
          await request("/broadcasts/"+addMoment.dataset.addMoment+"/moments", {method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({positionMs,title,notes,clientMutationId:crypto.randomUUID ? crypto.randomUUID() : String(Date.now())})});
          notify("Moment saved");
          if (addMoment.id !== "miniMoment") await openDetails(Number(addMoment.dataset.addMoment), false);
          return;
        }
        const loadTranscript = e.target.closest("[data-load-transcript]");
        if (loadTranscript) {
          const host = document.getElementById("transcriptHost");
          if (!host) return;
          host.innerHTML = '<div class="section"><h3>Transcript</h3><div class="muted">Loading transcript…</div></div>';
          try {
            const data = await request("/broadcasts/"+loadTranscript.dataset.loadTranscript+"/transcript");
            const t = data.transcript;
            host.innerHTML = `<div class="section"><h3>Transcript</h3><div class="meta">${esc(t.status)} · ${t.wordCount || 0} words${t.language ? " · "+esc(t.language) : ""}</div><div style="margin-top:10px">${(t.segments||[]).map(seg=>`<div class="moment"><button data-play="${t.canonicalBroadcastId}" data-seek="${Math.floor((seg.startMs||0)/1000)}">${fmtMs(seg.startMs||0)}</button> <strong>${esc(seg.speaker || "Speaker")}</strong><div class="muted">${esc(seg.text)}</div></div>`).join("") || '<div class="muted">No timed segments are available.</div>'}</div></div>`;
          } catch (err) { host.innerHTML = '<div class="section"><h3>Transcript</h3><div class="muted">No transcript is available for this broadcast.</div></div>'; }
          return;
        }
        const f = e.target.closest("[data-favourite]");
        if (f) {
          f.disabled = true;
          try {
            await mutateOrJournal("favourite", "/broadcasts/"+f.dataset.favourite+"/favourite", {favourite:f.dataset.value==="true"}, "favourite:"+f.dataset.favourite);
            if (localEpisode && Number(localEpisode.id) === Number(f.dataset.favourite)) localEpisode.favourite = f.dataset.value === "true";
            await load();
            if (currentDetailId) await openDetails(currentDetailId, false);
          } finally {
            f.disabled = false;
          }
          return;
        }
        const st = e.target.closest("[data-played]");
        if (st) {
          st.disabled = true;
          try {
            await mutateOrJournal("listening-status", "/broadcasts/"+st.dataset.played+"/listening-status", {played:st.dataset.value==="true"}, "status:"+st.dataset.played);
            await load();
            if (currentDetailId) await openDetails(currentDetailId, false);
          } finally {
            st.disabled = false;
          }
        }
      });
      document.body.addEventListener("input", (event) => {
        if (event.target?.id === "wikiTimelineRange") {
          const label = document.getElementById("wikiTimelineYearLabel");
          if (label) label.textContent = `Around ${Number(event.target.value || 0)}`;
        }
      });
      document.body.addEventListener("change", async (event) => {
        if (event.target?.id === "wikiPageTypeFilter" || event.target?.id === "wikiPageStatusFilter") {
          await runWikiSearch(); return;
        }
        if (event.target?.id === "wikiTimelineShowSelect") {
          await showWikiTimelineExplorer(event.target.value, 0, true); return;
        }
        if (event.target?.id === "wikiTimelineRange") {
          const showId = document.getElementById("wikiTimelineShowSelect")?.value || "";
          await showWikiTimelineExplorer(showId, Number(event.target.value || 0), false);
          if (wikiHistory[wikiHistoryIndex]?.kind === "timeline") wikiHistory[wikiHistoryIndex].year = Number(event.target.value || 0);
        }
      });
      filterToggle.addEventListener("click", () => { const open=advancedFilters.hidden; advancedFilters.hidden=!open; filterToggle.setAttribute("aria-expanded",open?"true":"false"); });
      syncStatus.addEventListener("click",()=>{const opening=syncSheet.hidden;syncSheet.hidden=!syncSheet.hidden;if(opening)focusOverlay(syncSheet,syncSheetClose);else restoreOverlayFocus();refreshSyncStatus().catch(()=>{});});
      syncSheetClose.addEventListener("click",()=>{syncSheet.hidden=true;restoreOverlayFocus();});
      appUpdateLater.addEventListener("click",()=>{appUpdateBanner.hidden=true;});
      appUpdateReload.addEventListener("click",reloadWithCurrentShell);
      syncRetryFailed.addEventListener("click",async()=>{syncRetryFailed.disabled=true;try{await resetBlockedSyncRecords();}finally{syncRetryFailed.disabled=false;}});
      syncDiscardFailed.addEventListener("click",async()=>{if(!confirm("Discard the failed sync changes? Device playback progress will remain available locally, but these changes will not be sent to the server."))return;await discardBlockedSyncRecords();});
      syncNow.addEventListener("click",async()=>{await probeServer(true); if(serverReachable)await syncAllPending(); });
      document.addEventListener("click", (event) => {
        document.querySelectorAll(".libraryOverflow[open]").forEach((menu) => {
          if (!menu.contains(event.target)) menu.removeAttribute("open");
        });
      });
      document.addEventListener("keydown", (event) => {
        if (document.body.classList.contains("menuOpen") && event.key === "Tab") {
          const focusable = [menuToggle, ...primaryNav.querySelectorAll("button:not([disabled])")]
            .filter((item) => !item.hidden && item.offsetParent !== null);
          if (!focusable.length) return;
          const first = focusable[0], last = focusable[focusable.length - 1];
          if (event.shiftKey && document.activeElement === first) {
            last.focus(); event.preventDefault(); return;
          }
          if (!event.shiftKey && document.activeElement === last) {
            first.focus(); event.preventDefault(); return;
          }
          if (!focusable.includes(document.activeElement)) {
            first.focus(); event.preventDefault(); return;
          }
        }
        if (event.key !== "Escape") return;
        if (webDiagnostic.classList.contains("open")) { webDiagnostic.classList.remove("open"); restoreOverlayFocus(); event.preventDefault(); return; }
        if (transferDiagnostic.classList.contains("open")) { transferDiagnostic.classList.remove("open"); restoreOverlayFocus(); event.preventDefault(); return; }
        if (!syncSheet.hidden) { syncSheet.hidden = true; restoreOverlayFocus(); event.preventDefault(); return; }
        if (document.body.classList.contains("menuOpen")) { setMenuOpen(false); event.preventDefault(); return; }
        const openLibraryMenu = document.querySelector(".libraryOverflow[open]");
        if (openLibraryMenu) { openLibraryMenu.removeAttribute("open"); openLibraryMenu.querySelector("summary")?.focus(); event.preventDefault(); return; }
        if (detail.classList.contains("open")) { closeDetails(); event.preventDefault(); return; }
        if (fullPlayer.classList.contains("open")) { hideFullPlayer(); event.preventDefault(); return; }
      });
      $("miniExpand").addEventListener("click", showFullPlayer);
      miniPlay.addEventListener("click", togglePlayback);
      $("miniBack").addEventListener("click", () => skip(-15));
      $("miniForward").addEventListener("click", () => skip(30));
      $("miniMore").addEventListener("click", showFullPlayer);
      $("miniInfo").addEventListener("click", () => {
        const state = activeState();
        if (state.episodeId) openDetails(state.episodeId);
      });
      miniSeek.addEventListener("input", () => {
        seeking = true;
        const value = Number(miniSeek.value), max = Number(miniSeek.max);
        miniElapsed.textContent = fmtMs(value);
        miniSeek.style.setProperty("--seek", (max ? value * 100 / max : 0) + "%");
      });
      miniSeek.addEventListener("change", async () => {
        const value = Number(miniSeek.value);
        try {
          if (!inactiveOutputIsActive() && localEpisode) await seekLogical(value, true);
        } finally {
          seeking = false;
          renderPlayer();
        }
      });
      miniSpeed.addEventListener("click", () => {
        if (inactiveOutputIsActive() || !localEpisode) return;
        const options = [...playerSpeed.options], current = options.findIndex((option) => option.value === playerSpeed.value), next = options[(current + 1) % options.length];
        playerSpeed.value = next.value;
        playerSpeed.dispatchEvent(new Event("change"));
        renderPlayer();
      });
      // localStorage returns null when a user has never adjusted the volume.
      // Number(null) is zero, which previously made every first launch silent.
      const savedVolumeValue = localStorage.getItem("radioVaultVolume");
      const savedVolume = savedVolumeValue === null ? 1 : Number(savedVolumeValue);
      if (Number.isFinite(savedVolume)) audio.volume = Math.max(0, Math.min(1, savedVolume));
      miniVolume.value = String(audio.volume);
      miniVolume.addEventListener("input", () => {
        audio.volume = Math.max(0, Math.min(1, Number(miniVolume.value || 0)));
        localStorage.setItem("radioVaultVolume", String(audio.volume));
      });
      $("playerClose").addEventListener("click", hideFullPlayer);
      playerPlay.addEventListener("click", togglePlayback);
      $("playerBack").addEventListener("click", () => skip(-30));
      $("playerForward").addEventListener("click", () => skip(30));
      $("playerInfo").addEventListener("click", () => {
        const s = activeState();
        if (s.episodeId) {
          hideFullPlayer();
          openDetails(s.episodeId);
        }
      });
      playerSeek.addEventListener("input", () => {
        seeking = true;
        const value = Number(playerSeek.value);
        playerElapsed.textContent = fmtMs(value);
        const max = Number(playerSeek.max);
        playerRemaining.textContent = "-" + fmtMs(Math.max(0, max - value));
        playerSeek.style.setProperty(
          "--seek",
          (max ? (value * 100) / max : 0) + "%",
        );
      });
      playerSeek.addEventListener("change", async () => {
        const value = Number(playerSeek.value);
        try {
          if (!inactiveOutputIsActive() && localEpisode)
            await seekLogical(value, true);
        } finally {
          seeking = false;
          renderPlayer();
        }
      });
      playerSpeed.addEventListener("change", async () => {
        if (inactiveOutputIsActive() || !localEpisode) return;
        const speed = Number(playerSpeed.value);
        audio.playbackRate = speed;
        syncWebPlayback(false, false);
      });
      $("playerFavourite").addEventListener("click", async () => {
        if (!localEpisode) return;
        const d = await mutateOrJournal("favourite", "/broadcasts/"+localEpisode.id+"/favourite", {favourite:!localEpisode.favourite}, "favourite:"+localEpisode.id);
        localEpisode = d.result?.episode || {...localEpisode,favourite:!localEpisode.favourite};
        renderPlayer();
        load();
      });
      $("playerListened").addEventListener("click", async () => {
        if (!localEpisode) return;
        const played = localEpisode.status !== "Completed",
          d = await mutateOrJournal("listening-status", "/broadcasts/"+localEpisode.id+"/listening-status", {played}, "status:"+localEpisode.id);
        localEpisode = d.result?.episode || {...localEpisode,status:played?"Completed":"Not started"};
        renderPlayer();
        load();
      });
      downloadCancel.addEventListener("click", () =>
        activeDownload?.controller.abort(),
      );
      $("playerNext").addEventListener("click", playNextQueued);
      cancelJob.addEventListener("click", async () => {
        if (!activeJobId) return;
        cancelJob.disabled = true;
        try {
          await fetch(auth(api + "/jobs/" + activeJobId + "/cancel"), {
            method: "POST",
            cache: "no-store",
          });
          await loadPlayerState();
        } finally {
          cancelJob.disabled = false;
        }
      });
      document.addEventListener("visibilitychange", async () => {
        if (document.hidden) {
          saveLocalProgress();
          syncWebPlayback();
          return;
        }
        // Laptop sleep and mobile tab suspension can outlive both the playback
        // lease and an in-flight poll. Revalidate server/session state before
        // allowing the restored page to issue transport or progress commands.
        reconnecting = true;
        applyConnectivityUi();
        const connected = await probeServer(true);
        if (connected) {
          await loadPlayerState().catch(() => null);
          if (localEpisode && thisPhoneOwnsSession()) {
            lastWebHeartbeat = 0;
            await syncWebPlayback(false, false);
          }
          await syncAllPending().catch(() => null);
        }
      });
      window.addEventListener("online", async () => {
        reconnecting = true;
        recordDiagnostic("connectivity", "Browser network connection returned", { view, downloads: downloadedRecords.size });
        applyConnectivityUi();
        await probeServer(true);
        if (serverReachable) {
          await refreshAfterReconnect().catch(() => null);
          if (serverReachable) notify(`Reconnected to ${connectedLabel()}`);
        }
      });
      window.addEventListener("offline", async () => {
        serverReachable = false;
        reconnecting = false;
        recordDiagnostic("connectivity", "Browser entered offline mode", { view, downloads: downloadedRecords.size });
        applyConnectivityUi();
        renderPlayer();
        if (view === "dashboard") await buildOfflineDashboard().catch(() => {});
        else if (view === "downloaded") await loadDownloaded();
        else if (!list.querySelector(".connectionBanner"))
          list.insertAdjacentHTML("afterbegin", connectionBoundaryBanner());
      });
      window.addEventListener("pagehide", () => {
        saveLocalProgress();
        if (serverReachable && localEpisode)
          fetch(auth(api + "/player/web-progress"), {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              clientId,
              episodeId: localEpisode.id,
              positionMs: Math.round(currentLogicalPositionMs()),
              durationMs: Math.round(canonicalDurationMs()),
              isPlaying: !audio.paused,
              speed: audio.playbackRate || 1,
              completed: false,
              force: false,
              deviceName: playbackDeviceName,
              deviceKind: playbackDeviceKind,
              expectedGeneration: Number(sessionState?.generation || 0),
              explicitSeek: false,
            }),
            keepalive: true,
          }).catch(() => {});
      });
      async function bootApplication() {
        try {
          restoreNavigationState();
          setBootStatus("Restoring downloads stored on this device…");
          try {
            await refreshDownloadedIndex();
            await auditDownloadedStorage();
          } catch (error) {
            console.warn("Offline library index could not be restored.", error);
            recordDiagnostic("offline-storage", "Downloaded library index could not be restored");
          }
          setBootStatus("Loading the server Radio Vault Web snapshot…");
          try {
            await loadAnywhereBootstrap(true);
            serverReachable = true;
            setBootStatus(`Connected to ${connectedLabel()}…`);
          } catch (error) {
            serverReachable = false;
            recordDiagnostic("bootstrap", "Server startup snapshot unavailable; using device storage", { online: navigator.onLine });
            try { await loadShows(); } catch {}
          }
          applyConnectivityUi();
          if (serverReachable) {
            try {
              await syncAllPending();
              await loadAnywhereBootstrap(true);
            } catch (error) {
              recordDiagnostic("sync-storage", "Pending device changes could not be inspected during startup");
            }
          }
          const deep = location.pathname.match(/^\/broadcast\/(\d+)/);
          if (deep) {
            try {
              await openDetails(Number(deep[1]), false);
            } catch (error) {
              console.warn("Deep-linked canonical broadcast could not be opened.", error);
            }
          }
          setBootStatus(
            serverReachable
              ? "Opening your Radio Vault Web workspace…"
              : "Opening your Offline Library…",
          );
          if (!serverReachable && view !== "dashboard" && view !== "downloaded") {
            lastConnectedView = view;
            lastConnectedLibraryView = libraryView;
            view = "dashboard";
          }
          try {
            await load();
          } catch (error) {
            console.warn("Primary view failed; falling back to downloads.", error);
            serverReachable = false;
            applyConnectivityUi();
            await loadDownloaded();
          }
          renderPlayer();
          document.body.dataset.booted = "true";
          finishBoot();
          recordDiagnostic("startup", "Radio Vault Web opened", { connected: serverReachable, view, downloads: downloadedRecords.size });
          enableOfflineShell().catch((error) =>
            console.warn("Offline shell refresh failed.", error),
          );
          if (serverReachable)
            loadPlayerState().catch((error) => console.warn("Live player refresh failed.", error));
        } catch (error) {
          recordDiagnostic("startup-failed", "Radio Vault Web could not finish opening", { online: navigator.onLine });
          showBootFailure(error);
        }
      }
      $("bootRetry").addEventListener("click", () => location.reload());
      $("bootDownloads").addEventListener("click", async () => {
        try {
          serverReachable = false;
          view = "downloaded";
          applyConnectivityUi();
          await loadDownloaded();
          document.body.dataset.booted = "true";
          finishBoot();
        } catch (error) {
          showBootFailure(error);
        }
      });
      bootApplication();
      setInterval(async () => {
        if (playerPollInFlight) return;
        playerPollInFlight = true;
        try {
          if (serverReachable || await probeServer()) await loadPlayerState();
        } finally {
          playerPollInFlight = false;
        }
      }, 1000);
      setInterval(async () => {
        if (changePollInFlight || !serverReachable) return;
        changePollInFlight = true;
        try {
          await pollChanges();
          if (view === "transcripts" && transcriptSection !== "library")
            await loadTranscripts();
        } finally {
          changePollInFlight = false;
        }
      }, 3000);
      setInterval(() => {
        if (inactiveOutputIsActive()) renderPlayer();
      }, 250);
      setInterval(() => {
        const now = Date.now(),
          ownsPhoneSession = !!localEpisode && thisPhoneOwnsSession(),
          heartbeatInterval = audio.paused ? 5000 : 1000;
        // A paused browser is still the selected output. Keep its presence fresh
        // so the server does not mistake an intentional pause for a closed tab.
        if (ownsPhoneSession && now - lastWebHeartbeat >= heartbeatInterval) {
          lastWebHeartbeat = now;
          syncWebPlayback();
        } else if (serverReachable) {
          syncPendingProgress();
        }
      }, 1000);
    
    </script>
  </body>
</html>
""";
}
