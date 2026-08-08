using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Contracts;

public interface IConnectedAccessService : IDisposable
{
    ConnectedAccessSnapshot Current { get; }
    event EventHandler<ConnectedAccessSnapshot>? StateChanged;
    Task<IReadOnlyList<ConnectedServerOption>> DiscoverAsync(CancellationToken cancellationToken = default);
    Task PairAsync(string serverInstanceId, string pairingCode, CancellationToken cancellationToken = default);
    Task TestAsync(CancellationToken cancellationToken = default);
    Task ReconnectAsync(CancellationToken cancellationToken = default);
    Task SetStartupModeAsync(bool useRemoteLibrary, CancellationToken cancellationToken = default);
    Task ForgetServerAsync(CancellationToken cancellationToken = default);
    Task RestartAsync(CancellationToken cancellationToken = default);
}
