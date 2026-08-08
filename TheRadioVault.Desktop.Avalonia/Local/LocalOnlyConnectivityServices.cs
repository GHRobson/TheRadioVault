using System.Text.Json;
using TheRadioVault.Application.Abstractions;
using TheRadioVault.Application.Models;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Desktop.Avalonia.Local;

public sealed class LocalOnlyConnectedAccessService : IConnectedAccessService
{
    private static readonly ConnectedAccessSnapshot Snapshot = new(
        ConnectedAccessState.LocalLibrary,
        IsRemoteSession: false,
        IsLive: false,
        IsCachedReadOnly: false,
        HasSavedServer: false,
        UseRemoteOnStartup: false,
        ServerDisplayName: "Radio Vault Server on this computer",
        ServerAddress: string.Empty,
        SavedServerDisplayName: string.Empty,
        SavedServerAddress: string.Empty,
        StatusText: "Connected locally",
        DetailText: "This client is using the Radio Vault Server on this computer.",
        LastLiveAt: null,
        NextReconnectAt: null,
        BroadcastCount: 0,
        ShowCount: 0,
        CapabilityGeneration: 0,
        CacheSizeBytes: 0,
        LastError: string.Empty);

    public ConnectedAccessSnapshot Current => Snapshot;
    public event EventHandler<ConnectedAccessSnapshot>? StateChanged { add { } remove { } }

    public Task<IReadOnlyList<ConnectedServerOption>> DiscoverAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ConnectedServerOption>>(Array.Empty<ConnectedServerOption>());

    public Task PairAsync(string serverInstanceId, string pairingCode, CancellationToken cancellationToken = default)
        => NotAvailable();
    public Task TestAsync(CancellationToken cancellationToken = default) => NotAvailable();
    public Task ReconnectAsync(CancellationToken cancellationToken = default) => NotAvailable();
    public Task SetStartupModeAsync(bool useRemoteLibrary, CancellationToken cancellationToken = default)
        => useRemoteLibrary ? NotAvailable() : Task.CompletedTask;
    public Task ForgetServerAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RestartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Dispose() { }

    private static Task NotAvailable()
        => Task.FromException(new InvalidOperationException(
            "Native desktop networking is not active in the Radio Vault 1.0 desktop application."));
}

public sealed class LocalOnlyConnectedPlaybackDiagnosticsService : IConnectedPlaybackDiagnosticsService
{
    public Task<ConnectedPlaybackDiagnosticReport> RunAsync(
        ConnectedPlaybackDiagnosticMode mode,
        string sessionCode,
        IProgress<ConnectedPlaybackDiagnosticProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var step = new ConnectedPlaybackDiagnosticStep(
            "local-only",
            "Native desktop networking not active",
            ConnectedPlaybackDiagnosticStatus.Warning,
            "Native connected-playback diagnostics remain postponed; use Radio Vault Web diagnostics for the browser companion.",
            0,
            now);
        progress?.Report(new ConnectedPlaybackDiagnosticProgress(step, step.Message));
        return Task.FromResult(new ConnectedPlaybackDiagnosticReport(
            "RadioVault.ConnectedPlaybackDiagnostic",
            1,
            Guid.NewGuid(),
            sessionCode ?? string.Empty,
            mode,
            "alpha13-local-ux-parity-pass1",
            "LocalOnly",
            Environment.MachineName,
            string.Empty,
            Environment.OSVersion.VersionString,
            Environment.Version.ToString(),
            now,
            now,
            ConnectedPlaybackDiagnosticStatus.Warning,
            step.Message,
            new[] { step },
            Array.Empty<RuntimeDiagnosticEvent>(),
            new Dictionary<string, string> { ["nativeDesktopNetworkLayer"] = "postponed", ["radioVaultAnywhere"] = "available" }));
    }

    public async Task ExportAsync(
        ConnectedPlaybackDiagnosticReport report,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("A destination path is required.", nameof(destinationPath));
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(destinationPath, json, cancellationToken).ConfigureAwait(false);
    }
}
