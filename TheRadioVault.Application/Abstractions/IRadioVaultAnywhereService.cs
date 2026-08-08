using TheRadioVault.Application.Models;

namespace TheRadioVault.Application.Abstractions;

public interface IRadioVaultAnywhereService : IDisposable
{
    RadioVaultAnywhereSnapshot Current { get; }
    event EventHandler<RadioVaultAnywhereSnapshot>? StateChanged;
    Task<RadioVaultAnywhereSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(RadioVaultAnywhereSettings settings, CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task GeneratePairingCodeAsync(CancellationToken cancellationToken = default);
    Task RevokeClientAsync(string clientId, CancellationToken cancellationToken = default);
    Task RegeneratePrivateLinkAsync(CancellationToken cancellationToken = default);
    Task ResetCertificatesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> RunDiagnosticsAsync(CancellationToken cancellationToken = default);
}
