using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Contracts;

public interface IConnectedPlaybackDiagnosticsService
{
    Task<ConnectedPlaybackDiagnosticReport> RunAsync(
        ConnectedPlaybackDiagnosticMode mode,
        string sessionCode,
        IProgress<ConnectedPlaybackDiagnosticProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task ExportAsync(
        ConnectedPlaybackDiagnosticReport report,
        string destinationPath,
        CancellationToken cancellationToken = default);
}
