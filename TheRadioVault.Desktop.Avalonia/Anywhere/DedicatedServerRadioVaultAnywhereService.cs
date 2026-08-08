using TheRadioVault.Application.Abstractions;
using TheRadioVault.Application.Models;
using TheRadioVault.Services;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;
using TheRadioVault.Web.Services;

namespace TheRadioVault.Desktop.Avalonia.Anywhere;

/// <summary>
/// Native-client view of Radio Vault Web. The dedicated server owns the
/// listener, credentials and certificates; this adapter never starts a second
/// web server or edits server preferences behind the server application's back.
/// </summary>
public sealed class DedicatedServerRadioVaultAnywhereService : IRadioVaultAnywhereService
{
    private readonly LoopbackServerClient _server;
    private RadioVaultAnywhereSnapshot _current;
    private bool _disposed;

    public DedicatedServerRadioVaultAnywhereService(LoopbackServerClient server)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _current = CreateSnapshot(
            isRunning: false,
            "Checking the dedicated Radio Vault Server…",
            _server.ServerDisplayName,
            _server.ServerAddress?.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) == true);
    }

    public RadioVaultAnywhereSnapshot Current => _current;
    public event EventHandler<RadioVaultAnywhereSnapshot>? StateChanged;

    public async Task<RadioVaultAnywhereSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        try
        {
            var response = await _server.SendJsonAsync<ServerEnvelope>(
                HttpMethod.Get, WebApiRoutes.ServerInfo, cancellationToken: cancellationToken).ConfigureAwait(false);
            Publish(CreateSnapshot(
                true,
                $"{response.Server.DisplayName} is hosting Radio Vault Web and owns this archive.",
                response.Server.DisplayName,
                response.Server.SecureAccess,
                response.Web?.AccessUrl,
                response.Web?.SecureSetupUrl));
        }
        catch (Exception exception)
        {
            Publish(CreateSnapshot(
                false,
                (_server.IsRemote
                    ? "The active Radio Vault Server could not be reached. "
                    : "Start Radio Vault Server on this computer, then refresh. ") + exception.Message,
                _server.ServerDisplayName,
                _server.ServerAddress?.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) == true));
        }
        return Current;
    }

    public Task SaveAsync(RadioVaultAnywhereSettings settings, CancellationToken cancellationToken = default)
        => ServerOwnsSettings(cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken = default)
        => ServerOwnsSettings(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
        => ServerOwnsSettings(cancellationToken);

    public Task GeneratePairingCodeAsync(CancellationToken cancellationToken = default)
        => ServerOwnsSettings(cancellationToken);

    public Task RevokeClientAsync(string clientId, CancellationToken cancellationToken = default)
        => ServerOwnsSettings(cancellationToken);

    public Task RegeneratePrivateLinkAsync(CancellationToken cancellationToken = default)
        => ServerOwnsSettings(cancellationToken);

    public Task ResetCertificatesAsync(CancellationToken cancellationToken = default)
        => ServerOwnsSettings(cancellationToken);

    public async Task<IReadOnlyList<string>> RunDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        var checks = Current.IsRunning
            ? new[]
            {
                "✓ The dedicated server answered an authenticated request.",
                Current.IsSecure ? "✓ Radio Vault Web is using HTTPS." : "! Radio Vault Web is using HTTP."
            }
            : new[] { "✗ The dedicated Radio Vault Server could not be reached." };
        Publish(Current with { DiagnosticChecks = checks });
        return checks;
    }

    private static Task ServerOwnsSettings(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(new InvalidOperationException(
            "Radio Vault Server owns hosting settings. Open the server's tray icon and choose Open server settings."));
    }

    private RadioVaultAnywhereSnapshot CreateSnapshot(
        bool isRunning,
        string detail,
        string serverDisplayName,
        bool secureAccess,
        string? suppliedAccessUrl = null,
        string? suppliedSetupUrl = null)
    {
        var localPreferences = _server.IsRemote ? null : WebServerPreferences.Load();
        var secure = localPreferences?.SecureAccessEnabled ?? secureAccess;
        var host = _server.IsRemote
            ? _server.ServerAddress?.Host ?? string.Empty
            : LanDiscoveryNetwork.GetPrivateIpv4Interfaces().FirstOrDefault()?.Address.ToString() ?? "127.0.0.1";
        var httpPort = localPreferences?.Port ?? 0;
        var httpsPort = localPreferences?.SecurePort ?? _server.ServerAddress?.Port ?? 0;
        var token = localPreferences is null ? string.Empty : Uri.EscapeDataString(localPreferences.AccessToken);
        var accessPort = secure ? httpsPort : httpPort;
        var accessUrl = _server.IsRemote
            ? suppliedAccessUrl?.Trim() ?? string.Empty
            : isRunning && host.Length > 0 && accessPort > 0 && token.Length > 0
                ? $"{(secure ? "https" : "http")}://{host}:{accessPort}/?token={token}"
                : string.Empty;
        var setupUrl = _server.IsRemote
            ? suppliedSetupUrl?.Trim() ?? string.Empty
            : isRunning && secure && httpPort > 0 && token.Length > 0
                ? $"http://{host}:{httpPort}/secure-setup?token={token}"
                : string.Empty;
        return new RadioVaultAnywhereSnapshot(
            IsRemoteSession: true,
            IsAvailable: true,
            IsRunning: isRunning,
            IsSecure: secure,
            Enabled: localPreferences?.Enabled ?? isRunning,
            StartAutomatically: localPreferences?.StartAutomatically ?? false,
            ServerDisplayName: string.IsNullOrWhiteSpace(serverDisplayName) ? "Radio Vault Server" : serverDisplayName,
            HttpPort: httpPort,
            HttpsPort: httpsPort,
            DiscoveryPort: localPreferences?.LanDiscoveryPort ?? 0,
            AccessUrl: accessUrl,
            SetupUrl: setupUrl,
            StatusText: isRunning ? "Radio Vault Web is hosted by Radio Vault Server." : "Radio Vault Server is not reachable.",
            DetailText: detail,
            PairingCode: string.Empty,
            PairingExpiresAt: null,
            PairedClients: Array.Empty<RadioVaultAnywhereClient>(),
            DiagnosticChecks: Array.Empty<string>());
    }

    private void Publish(RadioVaultAnywhereSnapshot snapshot)
    {
        _current = snapshot;
        StateChanged?.Invoke(this, snapshot);
    }

    private void EnsureAvailable() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose() => _disposed = true;

    private sealed record ServerEnvelope(WebServerInfo Server, WebAccessEnvelope? Web);
    private sealed record WebAccessEnvelope(string ProductName, string AccessUrl, string SecureSetupUrl);
}
