using TheRadioVault.Application.Abstractions;
using TheRadioVault.Application.Models;
using TheRadioVault.Core.Events;
using TheRadioVault.Core.Playback;
using TheRadioVault.Data.Database;
using TheRadioVault.Presentation.ViewModels;
using TheRadioVault.Services;
using TheRadioVault.Services.Jobs;

namespace TheRadioVault.Desktop.Avalonia.Anywhere;

/// <summary>
/// Hosts the established Radio Vault Web browser/PWA companion from the
/// local Avalonia archive. Native Avalonia federation, desktop pairing, remote
/// caches and shared-device handoff are deliberately not enabled here.
/// </summary>
public sealed class AvaloniaRadioVaultAnywhereService : IRadioVaultAnywhereService
{
    private readonly DatabaseService _legacyDatabase;
    private readonly ApplicationEventBus _events;
    private readonly LivePlaybackStateStore _livePlayback;
    private readonly BackgroundJobQueue _jobs;
    private readonly AvaloniaWebPlaybackController _playbackController;
    private RadioVaultAnywhereSnapshot _current;
    private bool _disposed;

    public AvaloniaRadioVaultAnywhereService(
        SqliteDatabase database,
        PlaybackViewModel playback,
        IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(dispatcher);

        // A previous Alpha build may have left native desktop federation
        // enabled in web-server.json. Reset only that post-1.0 capability while
        // preserving the accepted Anywhere token, ports and certificates.
        var preferences = WebServerPreferences.Load();
        if (preferences.LanFederationEnabled)
        {
            preferences.LanFederationEnabled = false;
            preferences.Save();
        }

        _legacyDatabase = new DatabaseService(database);
        _legacyDatabase.Initialize();
        _events = new ApplicationEventBus();
        _livePlayback = new LivePlaybackStateStore();
        _jobs = new BackgroundJobQueue(2, _events);
        _playbackController = new AvaloniaWebPlaybackController(
            playback,
            dispatcher,
            _livePlayback,
            _events);

        WebServerManager.Initialize(
            _legacyDatabase,
            _events,
            _livePlayback,
            _jobs,
            _playbackController);
        _current = CreateSnapshot();
    }

    public RadioVaultAnywhereSnapshot Current => _current;
    public event EventHandler<RadioVaultAnywhereSnapshot>? StateChanged;

    public Task<RadioVaultAnywhereSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        cancellationToken.ThrowIfCancellationRequested();
        Publish(CreateSnapshot());
        return Task.FromResult(Current);
    }

    public Task SaveAsync(RadioVaultAnywhereSettings settings, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePorts(settings.HttpPort, settings.HttpsPort);

        var preferences = WebServerPreferences.Load();
        preferences.Enabled = settings.Enabled;
        preferences.StartAutomatically = settings.StartAutomatically;
        preferences.ServerDisplayName = NormaliseName(settings.ServerDisplayName);
        preferences.Port = settings.HttpPort;
        preferences.SecureAccessEnabled = settings.SecureAccessEnabled;
        preferences.SecurePort = settings.HttpsPort;
        preferences.LanFederationEnabled = false;
        WebServerManager.Apply(preferences, settings.Enabled);
        Publish(CreateSnapshot("Radio Vault Web settings saved."));
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        cancellationToken.ThrowIfCancellationRequested();
        var preferences = WebServerPreferences.Load();
        preferences.Enabled = true;
        preferences.LanFederationEnabled = false;
        WebServerManager.Apply(preferences, shouldRun: true);
        Publish(CreateSnapshot("Radio Vault Web is running."));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        cancellationToken.ThrowIfCancellationRequested();
        var preferences = WebServerPreferences.Load();
        preferences.Enabled = false;
        preferences.LanFederationEnabled = false;
        WebServerManager.Apply(preferences, shouldRun: false);
        Publish(CreateSnapshot("Radio Vault Web is stopped."));
        return Task.CompletedTask;
    }

    public Task GeneratePairingCodeAsync(CancellationToken cancellationToken = default)
        => Task.FromException(new InvalidOperationException(
            "Native Radio Vault desktop pairing begins with the dedicated server/client phase after Radio Vault 0.33."));

    public Task RevokeClientAsync(string clientId, CancellationToken cancellationToken = default)
        => Task.FromException(new InvalidOperationException(
            "Native Radio Vault desktop pairing begins with the dedicated server/client phase after Radio Vault 0.33."));

    public Task RegeneratePrivateLinkAsync(CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        cancellationToken.ThrowIfCancellationRequested();
        var preferences = WebServerPreferences.Load();
        preferences.RegenerateToken();
        preferences.LanFederationEnabled = false;
        preferences.Save();
        WebServerManager.Apply(preferences, WebServerManager.Server?.IsRunning == true);
        Publish(CreateSnapshot("A new private Radio Vault Web link was created."));
        return Task.CompletedTask;
    }

    public Task ResetCertificatesAsync(CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        cancellationToken.ThrowIfCancellationRequested();
        WebServerManager.ResetSecureCertificates();
        Publish(CreateSnapshot("Secure certificates were regenerated."));
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<string>> RunDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        cancellationToken.ThrowIfCancellationRequested();
        var server = WebServerManager.Server;
        IReadOnlyList<string> checks;
        if (server is null || !server.IsRunning)
        {
            checks = new[] { "Radio Vault Web is not running." };
        }
        else if (!server.IsSecure)
        {
            checks = new[]
            {
                "HTTP listener is running.",
                "HTTPS is disabled; enable secure access for iPhone/PWA installation and trusted local playback."
            };
        }
        else
        {
            var result = await server.RunSecureDiagnosticsAsync().ConfigureAwait(false);
            checks = result.Checks;
        }

        Publish(CreateSnapshot(
            checks.All(item => !item.StartsWith("✗", StringComparison.Ordinal))
                ? "Radio Vault Web diagnostics completed."
                : "Radio Vault Web diagnostics found an issue.",
            checks));
        return checks;
    }

    private RadioVaultAnywhereSnapshot CreateSnapshot(
        string? overrideStatus = null,
        IReadOnlyList<string>? diagnosticChecks = null)
    {
        var preferences = WebServerPreferences.Load();
        var server = WebServerManager.Server;
        var running = server?.IsRunning == true;
        var accessUrl = running
            ? server!.GetAccessUrls().FirstOrDefault() ?? string.Empty
            : string.Empty;
        var setupUrl = running && server!.IsSecure
            ? server.GetSecureSetupUrls().FirstOrDefault() ?? string.Empty
            : string.Empty;

        var status = overrideStatus ?? (running
            ? server!.IsSecure
                ? "Radio Vault Web is available securely on this network."
                : "Radio Vault Web is available over HTTP."
            : "Radio Vault Web is stopped.");
        var detail = server?.LastError;
        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = running
                ? $"{preferences.ServerDisplayName} · {(server!.IsSecure ? "HTTPS" : "HTTP")} · browser/PWA companion only."
                : "Start hosting to use Radio Vault from a phone, tablet or web browser on this network.";
        }

        return new RadioVaultAnywhereSnapshot(
            IsRemoteSession: false,
            IsAvailable: true,
            IsRunning: running,
            IsSecure: server?.IsSecure == true,
            Enabled: preferences.Enabled,
            StartAutomatically: preferences.StartAutomatically,
            ServerDisplayName: preferences.ServerDisplayName,
            HttpPort: preferences.Port,
            HttpsPort: preferences.SecurePort,
            DiscoveryPort: preferences.LanDiscoveryPort,
            AccessUrl: accessUrl,
            SetupUrl: setupUrl,
            StatusText: status,
            DetailText: detail,
            PairingCode: string.Empty,
            PairingExpiresAt: null,
            PairedClients: Array.Empty<RadioVaultAnywhereClient>(),
            DiagnosticChecks: diagnosticChecks ?? Array.Empty<string>());
    }

    private void Publish(RadioVaultAnywhereSnapshot snapshot)
    {
        _current = snapshot;
        StateChanged?.Invoke(this, snapshot);
    }

    private void EnsureAvailable()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AvaloniaRadioVaultAnywhereService));
    }

    private static string NormaliseName(string value)
    {
        var name = string.IsNullOrWhiteSpace(value)
            ? $"Radio Vault on {Environment.MachineName}"
            : value.Trim();
        return name.Length <= 80 ? name : name[..80];
    }

    private static void ValidatePorts(int http, int https)
    {
        foreach (var port in new[] { http, https })
        {
            if (port is < 1024 or > 65535)
                throw new InvalidOperationException("Ports must be between 1024 and 65535.");
        }
        if (http == https)
            throw new InvalidOperationException("HTTP and HTTPS must use different ports.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WebServerManager.Stop();
        _playbackController.Dispose();
        _jobs.Dispose();
    }
}
