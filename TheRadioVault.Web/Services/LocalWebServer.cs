using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Web.Services;

public sealed partial class LocalWebServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly WebHttpRequestReader RequestReader = new(ResolveRequestBodyPolicy);
    private readonly IWebArchiveProvider _archive;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private TcpListener? _listener;
    private TcpListener? _secureListener;
    private int _boundPort;
    private int _boundSecurePort;
    private CancellationTokenSource? _cancellation;
    private Task? _acceptLoop;
    private Task? _secureAcceptLoop;
    private Task? _discoveryLoop;
    private WebServerOptions _options;
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly WebDesktopPairingCoordinator _pairing;
    private readonly WebMutationLedger _mutations;
    private readonly WebPersonalStateDecisionLedger _personalStateDecisions;
    private readonly object _positionedWaveSessionsGate = new();
    private readonly Dictionary<string, PositionedWaveSession> _positionedWaveSessions = new(StringComparer.Ordinal);
    private static readonly TimeSpan PositionedWaveSessionIdleLifetime = TimeSpan.FromMinutes(10);
    private const int PositionedWaveSessionSoftLimit = 8;

    public LocalWebServer(IWebArchiveProvider archive, WebServerOptions options, Action<string>? log = null)
    {
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _pairing = new WebDesktopPairingCoordinator(_options.PairedDesktopClients);
        _mutations = new WebMutationLedger(path: _options.MutationLedgerPath);
        _personalStateDecisions = new WebPersonalStateDecisionLedger(_options.PersonalStateDecisionLedgerPath);
        _log = log;
    }

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }
    public int Port
    {
        get
        {
            lock (_gate) return _boundPort > 0 ? _boundPort : _options.Port;
        }
    }

    public int SecurePort
    {
        get
        {
            lock (_gate) return _boundSecurePort > 0 ? _boundSecurePort : _options.SecurePort;
        }
    }
    public bool IsSecure => _options.SecureAccessEnabled && _options.ServerCertificate is not null;
    public string AccessToken => _options.AccessToken;
    public string RootCertificateThumbprint => _options.RootCertificateThumbprint;
    public bool LanFederationEnabled => _options.LanFederationEnabled && IsSecure;
    public int LanDiscoveryPort => _options.LanDiscoveryPort;
    public int PairedDesktopClientCount => _pairing.Count;
    public IReadOnlyList<WebPairedDesktopClient> PairedDesktopClients => _pairing.Clients;
    public IReadOnlyList<WebDeviceSyncStatus> DeviceSyncStatuses => _mutations.GetDeviceStatuses();
    public WebDesktopPairingSession? CurrentDesktopPairing => _pairing.Current;

    public WebDesktopPairingSession BeginDesktopPairing()
    {
        if (!IsRunning) throw new InvalidOperationException("Start connected access before creating a remote-client pairing code.");
        if (!IsSecure) throw new InvalidOperationException("HTTPS must be enabled before pairing another remote client.");
        if (!_options.LanFederationEnabled) throw new InvalidOperationException("Enable Multi-Device Library Access before creating a pairing code.");

        return _pairing.Begin();
    }

    public void CancelDesktopPairing()
    {
        _pairing.Cancel();
    }

    public bool RevokeDesktopClient(string clientId)
        => _pairing.Revoke(clientId);

    public IReadOnlyList<string> GetAccessUrls()
    {
        var token = Uri.EscapeDataString(_options.AccessToken);
        var scheme = IsSecure ? "https" : "http";
        var port = IsSecure ? SecurePort : Port;
        return GetLanAddresses().Select(address => $"{scheme}://{address}:{port}/?token={token}").ToArray();
    }

    public IReadOnlyList<string> GetSecureSetupUrls()
    {
        if (!IsSecure) return Array.Empty<string>();
        var token = Uri.EscapeDataString(_options.AccessToken);
        return GetLanAddresses().Select(address => $"http://{address}:{Port}/secure-setup?token={token}").ToArray();
    }

    public IReadOnlyList<string> GetBroadcastUrls(long episodeId)
    {
        var token = Uri.EscapeDataString(_options.AccessToken);
        var scheme = IsSecure ? "https" : "http";
        var port = IsSecure ? SecurePort : Port;
        return GetLanAddresses().Select(address => $"{scheme}://{address}:{port}/broadcast/{episodeId}?token={token}").ToArray();
    }

    public void UpdateOptions(WebServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            _options = options;
            _pairing.Replace(options.PairedDesktopClients);
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
            if (_options.SecureAccessEnabled && _options.SecurePort != 0 && _options.SecurePort == _options.Port)
                throw new InvalidOperationException("The HTTP setup port and HTTPS port must be different.");

            try
            {
                _listener = new TcpListener(_options.LoopbackOnly ? IPAddress.Loopback : IPAddress.Any, _options.Port);
                _listener.Start(32);
                _boundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
                if (_options.SecureAccessEnabled)
                {
                    _secureListener = new TcpListener(_options.LoopbackOnly ? IPAddress.Loopback : IPAddress.Any, _options.SecurePort);
                    _secureListener.Start(32);
                    _boundSecurePort = ((IPEndPoint)_secureListener.LocalEndpoint).Port;
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
                        ? $"Started HTTP setup on port {_boundPort}, HTTPS on port {_boundSecurePort}, and LAN server discovery on UDP {_options.LanDiscoveryPort}."
                        : $"Started HTTP setup on port {_boundPort} and HTTPS on port {_boundSecurePort}."
                    : $"Started on LAN port {_boundPort}.");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                IsRunning = false;
                try { _listener?.Stop(); } catch { }
                try { _secureListener?.Stop(); } catch { }
                _listener = null;
                _secureListener = null;
                _boundPort = 0;
                _boundSecurePort = 0;
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
            _boundPort = 0;
            _boundSecurePort = 0;
            _acceptLoop = null;
            _secureAcceptLoop = null;
            _discoveryLoop = null;
        }

        _pairing.Cancel();

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
            SecurePort,
            _options.ServerCertificateThumbprint,
            pairing is not null,
            _pairing.Count,
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

                var requestRead = await RequestReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                if (requestRead.Request is null)
                {
                    if (requestRead.Failure != WebHttpRequestReadFailure.EndOfStream)
                        await WriteRequestReadFailureAsync(stream, requestRead.Failure, cancellationToken).ConfigureAwait(false);
                    return;
                }
                using var request = requestRead.Request;
                var provisional = WebRequestLifecycleResolver.Resolve(
                    request.Method, request.Target, secure, _options.SecureAccessEnabled, authorized: true);
                if (provisional.Kind == WebRequestLifecycleKind.InvalidMethod)
                {
                    await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Only GET, HEAD and selected POST actions are supported.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (provisional.Kind == WebRequestLifecycleKind.MalformedTarget || provisional.Context is null)
                {
                    await WriteTextResponseAsync(stream, 400, "Bad Request", "The HTTP request target is malformed.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var lifecycle = WebRequestLifecycleResolver.Resolve(
                    request.Method,
                    request.Target,
                    secure,
                    _options.SecureAccessEnabled,
                    IsAuthorizedRequest(request, provisional.Context.Query));
                var context = lifecycle.Context!;
                if (lifecycle.Kind == WebRequestLifecycleKind.Pairing)
                {
                    if (!context.IsPost)
                    {
                        await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Remote-client pairing requires POST.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await HandleDesktopPairingAsync(stream, request, secure, cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (lifecycle.Kind == WebRequestLifecycleKind.Unauthorized)
                {
                    await WriteTextResponseAsync(stream, 401, "Unauthorized", "A valid Radio Vault access link or remote-client token is required.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (lifecycle.Kind == WebRequestLifecycleKind.SecureSetup)
                {
                    const string setupHeaders = "Cache-Control: no-store\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'\r\n";
                    var setupHtml = BuildSecureSetupHtml(request);
                    await WriteBytesResponseAsync(stream, 200, "OK", Encoding.UTF8.GetBytes(setupHtml), "text/html; charset=utf-8", context.IsHead, cancellationToken, setupHeaders).ConfigureAwait(false);
                    return;
                }
                if (lifecycle.Kind == WebRequestLifecycleKind.SecureProfile)
                {
                    const string profileHeaders = "Cache-Control: no-store\r\nContent-Disposition: attachment; filename=RadioVault-Secure-Offline-Access.mobileconfig\r\n";
                    await WriteBytesResponseAsync(stream, 200, "OK", _options.MobileConfigurationProfile, "application/x-apple-aspen-config", context.IsHead, cancellationToken, profileHeaders).ConfigureAwait(false);
                    return;
                }
                if (lifecycle.Kind == WebRequestLifecycleKind.SecureRootCertificate)
                {
                    const string certificateHeaders = "Cache-Control: no-store\r\nContent-Disposition: attachment; filename=RadioVault-Local-Root-CA.cer\r\n";
                    await WriteBytesResponseAsync(stream, 200, "OK", _options.RootCertificateDer, "application/x-x509-ca-cert", context.IsHead, cancellationToken, certificateHeaders).ConfigureAwait(false);
                    return;
                }
                if (lifecycle.Kind == WebRequestLifecycleKind.RedirectToSecure)
                {
                    await WriteRedirectAsync(stream, BuildSecureTarget(request), cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (lifecycle.Kind == WebRequestLifecycleKind.WebManifest)
                {
                    const string manifestHeaders = "Cache-Control: no-cache\r\n";
                    await WriteBytesResponseAsync(stream, 200, "OK", Encoding.UTF8.GetBytes(BuildWebManifest()), "application/manifest+json; charset=utf-8", context.IsHead, cancellationToken, manifestHeaders).ConfigureAwait(false);
                    return;
                }
                if (lifecycle.Kind == WebRequestLifecycleKind.AppIcon && TryGetWebAppIcon(context.Path, out var iconBytes))
                {
                    const string iconHeaders = "Cache-Control: public, max-age=86400\r\n";
                    await WriteBytesResponseAsync(stream, 200, "OK", iconBytes, "image/png", context.IsHead, cancellationToken, iconHeaders).ConfigureAwait(false);
                    return;
                }
                if (lifecycle.Kind == WebRequestLifecycleKind.ServiceWorker)
                {
                    const string workerHeaders = "Cache-Control: no-cache\r\nService-Worker-Allowed: /\r\n";
                    await WriteBytesResponseAsync(stream, 200, "OK", Encoding.UTF8.GetBytes(ServiceWorkerJavaScript), "text/javascript; charset=utf-8", context.IsHead, cancellationToken, workerHeaders).ConfigureAwait(false);
                    return;
                }
                if (lifecycle.Kind == WebRequestLifecycleKind.WebShell)
                {
                    var securityHeaders = (secure ? "Cache-Control: no-cache\r\n" : "Cache-Control: no-store\r\n") + "Content-Security-Policy: default-src 'self'; img-src 'self' data:; media-src 'self'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; worker-src 'self'; base-uri 'none'; frame-ancestors 'none'\r\n";
                    await WriteBytesResponseAsync(stream, 200, "OK", Encoding.UTF8.GetBytes(BuildIndexHtml()), "text/html; charset=utf-8", context.IsHead, cancellationToken, securityHeaders).ConfigureAwait(false);
                    return;
                }

                if (await TryHandleAuthorizedRouteAsync(
                        stream,
                        context.Path,
                        context.Query,
                        request,
                        context.Method,
                        cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                await WriteTextResponseAsync(stream, 404, "Not Found", "Not found.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
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

        var decision = _pairing.TryPair(pairing);
        if (decision.Kind != WebPairingDecisionKind.Paired || decision.Client is null)
        {
            var badRequest = decision.Kind is WebPairingDecisionKind.InvalidCode or WebPairingDecisionKind.InvalidIdentity;
            await WriteDesktopPairingResultAsync(
                stream, badRequest ? 400 : 403, badRequest ? "Bad Request" : "Forbidden", false,
                decision.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        try { _options.PairedDesktopClientAdded?.Invoke(decision.Client); }
        catch (Exception ex) { _log?.Invoke($"Could not persist remote client {decision.Client.ClientId}: {ex.Message}"); }

        await WriteDesktopPairingResultAsync(
            stream, 200, "OK", true, decision.Message, decision.Client, cancellationToken).ConfigureAwait(false);
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
                SecurePort,
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
                pairedDesktopClients = _pairing.Count,
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
                WebArchiveDiscoveryProjection.BuildLibrarySummary(episodes, showCount),
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
            var discovery = WebArchiveDiscoveryProjection.Build(episodes, limit, DateTime.Today);
            var bootstrap = new WebAnywhereBootstrap
            {
                Server = BuildServerInfo(),
                Library = discovery.Library,
                Shows = discovery.Shows,
                Years = discovery.Years,
                ContinueListening = discovery.ContinueListening,
                Recent = discovery.Recent,
                Favourites = discovery.Favourites,
                OnThisDay = discovery.OnThisDay,
                Unheard = discovery.Unheard,
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
            new WebServerCapability("radio.live", "Clock-driven Radio Vault Live archive station", "read", true,
                "Listening is separate from Library progress and play history."),
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
        if (await TryWriteDuplicateMutationResponseAsync(stream, request, cancellationToken).ConfigureAwait(false)) return;
        var result = _archive.AddMoment(episodeId, mutation);
        if (result.Changed) MarkMutationProcessed(request);
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

    private async Task HandleMomentsApiAsync(Stream stream, bool headOnly, CancellationToken cancellationToken)
    {
        var moments = _archive.GetMoments();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, moments, count = moments.Count }, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
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
        var receivedAt = DateTimeOffset.UtcNow;
        WebMutationLedger.TryGetMutationId(request, out var mutationId);
        var decision = _personalStateDecisions.TryApply(
            WebConflictDomain.Favourite,
            episodeId,
            mutation.Favourite ? "true" : "false",
            mutation.CapturedAt,
            receivedAt,
            WebMutationLedger.GetClientId(request),
            mutationId,
            () => _archive.SetFavourite(episodeId, mutation.Favourite),
            out var appliedResult);
        if (!decision.Accepted)
        {
            var conflict = new WebMutationResult(
                false,
                decision.Message,
                _archive.GetEpisode(episodeId),
                Conflict: decision.Resolution is WebConflictResolution.RejectStale or WebConflictResolution.RejectClockSkew,
                Resolution: decision.Resolution.ToString());
            var conflictCode = conflict.Conflict ? 409 : 200;
            var conflictBytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result = conflict }, JsonOptions);
            await WriteBytesResponseAsync(stream, conflictCode, conflictCode == 409 ? "Conflict" : "OK", conflictBytes,
                "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
            return;
        }
        var result = appliedResult ?? new WebMutationResult(false, "Broadcast not found.");
        result = result with { Resolution = decision.Resolution.ToString() };
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
        var receivedAt = DateTimeOffset.UtcNow;
        WebMutationLedger.TryGetMutationId(request, out var mutationId);
        var decision = _personalStateDecisions.TryApply(
            WebConflictDomain.ListeningStatus,
            episodeId,
            mutation.Played ? "true" : "false",
            mutation.CapturedAt,
            receivedAt,
            WebMutationLedger.GetClientId(request),
            mutationId,
            () => _archive.SetPlayed(episodeId, mutation.Played),
            out var appliedResult);
        if (!decision.Accepted)
        {
            var conflict = new WebMutationResult(
                false,
                decision.Message,
                _archive.GetEpisode(episodeId),
                Conflict: decision.Resolution is WebConflictResolution.RejectStale or WebConflictResolution.RejectClockSkew,
                Resolution: decision.Resolution.ToString());
            var conflictCode = conflict.Conflict ? 409 : 200;
            var conflictBytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result = conflict }, JsonOptions);
            await WriteBytesResponseAsync(stream, conflictCode, conflictCode == 409 ? "Conflict" : "OK", conflictBytes,
                "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
            return;
        }
        var result = appliedResult ?? new WebMutationResult(false, "Broadcast not found.");
        result = result with { Resolution = decision.Resolution.ToString() };
        var status = result.Changed ? (200, "OK") : (404, "Not Found");
        if (status.Item1 == 200) MarkMutationProcessed(request);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { apiVersion = WebApiRoutes.Version, result }, JsonOptions);
        await WriteBytesResponseAsync(stream, status.Item1, status.Item2, bytes, "application/json; charset=utf-8", false, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private static WebHttpRequestBodyPolicy ResolveRequestBodyPolicy(string requestTarget)
    {
        var isResearchPackUpload = requestTarget.StartsWith(WebApiRoutes.FederationResearchImportPreview, StringComparison.OrdinalIgnoreCase);
        var isWikiPackUpload = requestTarget.StartsWith(WebApiRoutes.FederationWikiImportPreview, StringComparison.OrdinalIgnoreCase)
            || requestTarget.StartsWith(WebApiRoutes.FederationWikiImportApply, StringComparison.OrdinalIgnoreCase);
        var isFullClientPayload = requestTarget.StartsWith(WebApiRoutes.ClientResearch, StringComparison.OrdinalIgnoreCase)
            || requestTarget.StartsWith(WebApiRoutes.ClientTranscripts, StringComparison.OrdinalIgnoreCase)
            || requestTarget.StartsWith(WebApiRoutes.ClientSpeakers, StringComparison.OrdinalIgnoreCase)
            || requestTarget.StartsWith(WebApiRoutes.ClientTranscription, StringComparison.OrdinalIgnoreCase)
            || requestTarget.StartsWith(WebApiRoutes.ClientWiki, StringComparison.OrdinalIgnoreCase);

        var maximumBodyBytes = isResearchPackUpload
            ? WebResearchPackLimits.MaximumPackageBytes
            : isWikiPackUpload ? 512 * 1024 * 1024
            : isFullClientPayload ? 64 * 1024 * 1024
            : 16 * 1024;
        var timeout = isResearchPackUpload || isWikiPackUpload
            ? TimeSpan.FromMinutes(10)
            : isFullClientPayload ? TimeSpan.FromMinutes(2)
            : TimeSpan.FromSeconds(10);
        return new WebHttpRequestBodyPolicy(
            maximumBodyBytes,
            timeout,
            StageToFile: isResearchPackUpload || isWikiPackUpload);
    }

    private static Task WriteRequestReadFailureAsync(
        Stream stream,
        WebHttpRequestReadFailure failure,
        CancellationToken cancellationToken)
    {
        var response = failure switch
        {
            WebHttpRequestReadFailure.TimedOut => (408, "Request Timeout", "The HTTP request timed out."),
            WebHttpRequestReadFailure.HeaderTooLarge => (431, "Request Header Fields Too Large", "The HTTP request headers are too large."),
            WebHttpRequestReadFailure.BodyTooLarge => (413, "Content Too Large", "The HTTP request body is too large."),
            _ => (400, "Bad Request", "The HTTP request is malformed.")
        };
        return WriteTextResponseAsync(
            stream,
            response.Item1,
            response.Item2,
            response.Item3,
            "text/plain; charset=utf-8",
            cancellationToken);
    }

    private static Task WriteJsonAsync<T>(Stream stream, T payload, bool headOnly, CancellationToken cancellationToken)
        => WebHttpResponseWriter.WriteJsonAsync(stream, payload, JsonOptions, headOnly, cancellationToken);

    private static Task WriteTextResponseAsync(Stream stream, int code, string reason, string text, string contentType, CancellationToken cancellationToken)
        => WebHttpResponseWriter.WriteTextAsync(stream, code, reason, text, contentType, cancellationToken);

    private static Task WriteBytesResponseAsync(Stream stream, int code, string reason, byte[] bytes, string contentType, bool headOnly, CancellationToken cancellationToken, string extraHeaders = "")
        => WebHttpResponseWriter.WriteBytesAsync(stream, code, reason, bytes, contentType, headOnly, cancellationToken, extraHeaders);

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
        return $"https://{host}:{SecurePort}{request.Target}";
    }

    private string BuildSecureSetupHtml(HttpRequest request)
    {
        var host = GetRequestHost(request);
        var token = Uri.EscapeDataString(_options.AccessToken);
        var profileUrl = WebUtility.HtmlEncode($"http://{host}:{Port}/secure-profile.mobileconfig?token={token}");
        var certificateUrl = WebUtility.HtmlEncode($"http://{host}:{Port}/secure-root.cer?token={token}");
        var secureUrl = WebUtility.HtmlEncode($"https://{host}:{SecurePort}/?token={token}");
        var thumbprint = WebUtility.HtmlEncode(_options.RootCertificateThumbprint);

        var template = SecureSetupHtml;

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

    private static Task WriteRedirectAsync(Stream stream, string location, CancellationToken cancellationToken)
        => WebHttpResponseWriter.WriteRedirectAsync(stream, location, cancellationToken);

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
        => WebRequestAuthorizer.IsAuthorized(request, query, _options.AccessToken, _pairing.Clients);

    private static string JavaScriptString(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    public void Dispose() => Stop();

    private bool IsProcessedMutation(HttpRequest request)
        => _mutations.Contains(request);

    private void MarkMutationProcessed(HttpRequest request)
        => _mutations.Record(request);

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

    private sealed record FavouriteMutation(bool Favourite, DateTimeOffset? CapturedAt = null);
    private sealed record ListeningStatusMutation(bool Played, DateTimeOffset? CapturedAt = null);
    private sealed record QueueAddMutation(long EpisodeId, bool PlayNext = false);
    private sealed record QueueMoveMutation(int Direction);

}
