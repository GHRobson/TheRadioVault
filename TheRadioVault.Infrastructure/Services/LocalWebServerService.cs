using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using TheRadioVault.Core.Events;
using TheRadioVault.Core.Playback;
using TheRadioVault.Services.Jobs;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;
using TheRadioVault.Web.Services;

namespace TheRadioVault.Services;

/// <summary>
/// WPF-shell adapter for the platform-neutral embedded web server.
/// Preferences and diagnostics remain desktop responsibilities while HTTP,
/// routing, querying and streaming live in TheRadioVault.Web.
/// </summary>
public sealed class LocalWebServerService : IDisposable
{
    private readonly LocalWebServer _server;
    private readonly WebArchiveProvider _provider;
    private readonly ScheduledBackupService _scheduledBackups;
    private SecureWebCertificateBundle? _certificates;
    private WebServerPreferences _preferences;

    public LocalWebServerService(
        DatabaseService database,
        WebServerPreferences preferences,
        IApplicationEventBus events,
        ILivePlaybackStateStore livePlayback,
        IBackgroundJobQueue jobs,
        IWebPlaybackController playbackController,
        ServerTranscriptionRuntime? transcription = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _provider = new WebArchiveProvider(database, events, livePlayback, jobs, playbackController, transcription);
        _scheduledBackups = new ScheduledBackupService();
        _certificates = CreateCertificates(_preferences);
        _server = new LocalWebServer(
            _provider,
            ToOptions(_preferences, _certificates),
            message => DiagnosticLog.Write("WebServer", message));
    }

    public bool IsRunning => _server.IsRunning;
    public string? LastError => _server.LastError;
    public int Port => _server.Port;
    public int SecurePort => _server.SecurePort;
    public bool IsSecure => _server.IsSecure;
    public string AccessToken => _server.AccessToken;
    public string RootCertificateThumbprint => _server.RootCertificateThumbprint;
    public string ServerCertificateThumbprint => _certificates?.ServerThumbprint ?? string.Empty;
    public IReadOnlyList<string> CertificateNames => _certificates?.SubjectAlternativeNames ?? Array.Empty<string>();
    public bool LanFederationEnabled => _server.LanFederationEnabled;
    public int LanDiscoveryPort => _server.LanDiscoveryPort;
    public int PairedDesktopClientCount => _server.PairedDesktopClientCount;
    public IReadOnlyList<WebPairedDesktopClient> PairedDesktopClients => _server.PairedDesktopClients;
    public WebDesktopPairingSession? CurrentDesktopPairing => _server.CurrentDesktopPairing;

    public WebDesktopPairingSession BeginDesktopPairing() => _server.BeginDesktopPairing();
    public void CancelDesktopPairing() => _server.CancelDesktopPairing();
    public bool RevokeDesktopClient(string clientId) => _server.RevokeDesktopClient(clientId);
    public IReadOnlyList<string> GetAccessUrls() => _server.GetAccessUrls();
    public IReadOnlyList<string> GetSecureSetupUrls() => _server.GetSecureSetupUrls();
    public IReadOnlyList<string> GetBroadcastUrls(long episodeId) => _server.GetBroadcastUrls(episodeId);
    public WebPlaybackSession GetPlaybackSession() => _provider.GetPlaybackSession();
    public WebPlaybackTransferResult BeginPlaybackTransfer(WebPlaybackTransferBeginRequest request)
        => _provider.BeginPlaybackTransfer(request);
    public WebPlaybackTransferResult MarkPlaybackTransferReady(WebPlaybackTransferReadyRequest request)
        => _provider.MarkPlaybackTransferReady(request);
    public WebPlaybackTransferResult CommitPlaybackTransfer(WebPlaybackTransferCommitRequest request)
        => _provider.CommitPlaybackTransfer(request);
    public WebPlaybackTransferResult CancelPlaybackTransfer(WebPlaybackTransferCancelRequest request)
        => _provider.CancelPlaybackTransfer(request);
    public WebPlaybackTransferResult AcknowledgePlaybackTransferSourceStopped(WebPlaybackTransferSourceStoppedRequest request)
        => _provider.AcknowledgePlaybackTransferSourceStopped(request);
    public WebPlaybackSession ClaimServerPlayback(long episodeId, long positionMs, long durationMs, double speed, bool isPlaying)
        => _provider.ClaimServerPlayback(episodeId, positionMs, durationMs, speed, isPlaying);
    public bool ConfirmServerPlaybackOwnership() => _provider.ConfirmServerPlaybackOwnership();
    public WebLibraryScanSnapshot GetLibraryScanStatus() => _provider.GetLibraryScanStatus();
    public IReadOnlyList<WebArchiveFolderSnapshot> GetLibraryFolders()
        => _provider.GetAuthoritativeSettings().ArchiveFolders;
    public Task<WebLibraryScanSnapshot> RunLibraryScanAsync(string trigger, CancellationToken cancellationToken = default)
        => _provider.RunLibraryScanAsync(trigger, cancellationToken);
    public Task<WebResearchPackPreviewResponse> PreviewResearchPackAsync(
        byte[] bytes,
        string sourceName,
        CancellationToken cancellationToken = default)
    {
        var stream = new MemoryStream(bytes, writable: false);
        return PreviewAsync(stream, sourceName, cancellationToken);

        async Task<WebResearchPackPreviewResponse> PreviewAsync(
            Stream packageStream,
            string name,
            CancellationToken token)
        {
            await using var owned = packageStream;
            return await _provider.PreviewResearchPackAsync(packageStream, name, token).ConfigureAwait(false);
        }
    }
    public Task<WebResearchPackPreviewResponse> PreviewResearchPackFileAsync(
        string filePath,
        string sourceName,
        CancellationToken cancellationToken = default)
        => _provider.PreviewResearchPackFileAsync(filePath, sourceName, cancellationToken);
    public WebResearchPackImportJob StartResearchPackImport(Guid sessionId)
        => _provider.StartResearchPackImport(sessionId);
    public WebResearchPackImportJob GetResearchPackImportStatus(Guid sessionId)
        => _provider.GetResearchPackImportStatus(sessionId);
    public bool CancelResearchPackImport(Guid sessionId)
        => _provider.CancelResearchPackImport(sessionId);
    public Task<WebResearchPackExportPayload> ExportResearchPackAsync(CancellationToken cancellationToken = default)
        => _provider.ExportResearchPackAsync(cancellationToken);

    public Task<WebLibraryScanSnapshot> RunAutomaticLibraryScanIfDueAsync(
        TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        var status = _provider.GetLibraryScanStatus();
        if (status.IsRunning) return Task.FromResult(status);

        var folders = _provider.GetAuthoritativeSettings().ArchiveFolders;
        if (folders.Count == 0) return Task.FromResult(status);
        var now = DateTimeOffset.UtcNow;
        var due = folders.Any(folder => !folder.LastScanAt.HasValue ||
            now - new DateTimeOffset(DateTime.SpecifyKind(folder.LastScanAt.Value, DateTimeKind.Local)) >= interval);
        if (!due) return Task.FromResult(status);
        return _provider.RunLibraryScanAsync("automatic-hourly", cancellationToken);
    }

    public void UpdatePreferences(WebServerPreferences preferences)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        var replacement = CreateCertificates(_preferences);
        _certificates?.Dispose();
        _certificates = replacement;
        _server.UpdateOptions(ToOptions(_preferences, _certificates));
    }


    public async Task<SecureCertificateValidationResult> RunSecureDiagnosticsAsync()
    {
        if (_certificates is null || !_server.IsRunning || !_server.IsSecure)
            return new SecureCertificateValidationResult(false, new[] { "✗ Secure Web access is not running" });

        var baseValidation = SecureWebCertificateService.ValidateCertificate(
            _certificates.ServerCertificate, _certificates.RootCertificate, _certificates.SubjectAlternativeNames);
        var checks = baseValidation.Checks.ToList();
        var valid = baseValidation.IsValid;

        foreach (var host in _server.GetAccessUrls().Select(x => new Uri(x).Host).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var client = new TcpClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(host, _server.SecurePort, timeout.Token).ConfigureAwait(false);
                using var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
                    certificate is not null && string.Equals(
                        new X509Certificate2(certificate).Thumbprint,
                        _certificates.ServerThumbprint,
                        StringComparison.OrdinalIgnoreCase));
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, timeout.Token).ConfigureAwait(false);
                var remote = ssl.RemoteCertificate is null ? string.Empty : new X509Certificate2(ssl.RemoteCertificate).Thumbprint;
                var matches = string.Equals(remote, _certificates.ServerThumbprint, StringComparison.OrdinalIgnoreCase);
                checks.Add((matches ? "✓ " : "✗ ") + $"TLS listener for {host} presents the expected certificate");
                valid &= matches;
            }
            catch (Exception ex)
            {
                checks.Add($"✗ TLS handshake for {host} failed: {ex.Message}");
                valid = false;
            }
        }

        return new SecureCertificateValidationResult(valid, checks);
    }

    public void Start()
    {
        _server.Start();
        _scheduledBackups.Start();
    }

    public void Stop()
    {
        _scheduledBackups.Stop();
        _server.Stop();
    }

    public void Dispose()
    {
        _server.Dispose();
        _scheduledBackups.Dispose();
        _certificates?.Dispose();
        _certificates = null;
        _provider.Dispose();
    }

    private static SecureWebCertificateBundle? CreateCertificates(WebServerPreferences preferences)
        => preferences.SecureAccessEnabled
            ? SecureWebCertificateService.EnsureCertificates(preferences.CertificatePassword)
            : null;

    private WebServerOptions ToOptions(WebServerPreferences preferences, SecureWebCertificateBundle? certificates)
        => new()
        {
            AppVersion = AppVersionService.Version,
            ServerInstanceId = preferences.ServerInstanceId,
            ServerDisplayName = preferences.ServerDisplayName,
            DatabaseSchemaVersion = TheRadioVault.Data.Database.SqliteDatabase.CurrentSchemaVersion,
            CapabilityGeneration = 40,
            Port = preferences.Port,
            AccessToken = preferences.AccessToken,
            SecureAccessEnabled = preferences.SecureAccessEnabled,
            SecurePort = preferences.SecurePort,
            ServerCertificate = certificates?.ServerCertificate,
            ServerCertificateContext = certificates is null ? null : SslStreamCertificateContext.Create(
                certificates.ServerCertificate,
                new X509Certificate2Collection(certificates.RootCertificate),
                offline: true),
            ServerCertificateThumbprint = certificates?.ServerThumbprint ?? string.Empty,
            RootCertificateDer = certificates?.RootCertificateDer ?? Array.Empty<byte>(),
            MobileConfigurationProfile = certificates?.MobileConfigurationProfile ?? Array.Empty<byte>(),
            RootCertificateThumbprint = certificates?.RootThumbprint ?? string.Empty,
            LanFederationEnabled = preferences.LanFederationEnabled,
            LanDiscoveryPort = preferences.LanDiscoveryPort,
            PairedDesktopClients = preferences.LanFederationEnabled
                ? preferences.PairedDesktopClients
                    .Select(x => x.ToContract())
                    .ToArray()
                : Array.Empty<WebPairedDesktopClient>(),
            PairedDesktopClientAdded = preferences.LanFederationEnabled
                ? (Action<WebPairedDesktopClient>)preferences.AddOrUpdatePairedDesktopClient
                : null,
            MutationLedgerPath = Path.Combine(AppPaths.DataDirectory, "server-mutation-acknowledgements.json"),
            ScheduledBackupStatus = () => _scheduledBackups.Status
        };
}
