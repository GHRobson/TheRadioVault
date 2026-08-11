using TheRadioVault.Client.Mobile.Models;

namespace TheRadioVault.Client.Mobile.Pairing;

/// <summary>
/// Owns discovery and pairing state while the session façade retains global
/// busy presentation, navigation and post-pair Library loading.
/// </summary>
internal sealed class MobilePairingCoordinator
{
    private readonly IMobilePairingTransport _transport;

    public MobilePairingCoordinator(IMobilePairingTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public bool IsPaired => _transport.IsPaired;
    public string ServerName => _transport.ServerName;
    public IReadOnlyList<DiscoveredRadioVaultServer> Servers { get; private set; } = [];

    public async Task<MobilePairingOperationResult> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            Servers = await _transport.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            return new MobilePairingOperationResult(
                true,
                Servers.Count == 0
                    ? "No servers found. Enable native clients and create a pairing code on Radio Vault Server."
                    : $"Found {Servers.Count} server{(Servers.Count == 1 ? string.Empty : "s")}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new MobilePairingOperationResult(false, "Discovery failed: " + exception.Message);
        }
    }

    public async Task<MobilePairingOperationResult> PairAsync(
        DiscoveredRadioVaultServer server,
        string pairingCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        try
        {
            await _transport.PairAsync(server, pairingCode, cancellationToken).ConfigureAwait(false);
            return new MobilePairingOperationResult(
                true,
                $"Paired with {ServerName}. Loading your library…");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new MobilePairingOperationResult(false, "Pairing failed: " + exception.Message);
        }
    }

    public async Task<MobilePairingOperationResult> PairManuallyAsync(
        string serverAddress,
        int securePort,
        string pairingCode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _transport
                .PairManuallyAsync(serverAddress, securePort, pairingCode, cancellationToken)
                .ConfigureAwait(false);
            return new MobilePairingOperationResult(
                true,
                $"Paired with {ServerName}. Loading your library…");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new MobilePairingOperationResult(false, "Manual pairing failed: " + exception.Message);
        }
    }

    public void Forget()
    {
        _transport.Forget();
        Servers = [];
    }
}

internal sealed record MobilePairingOperationResult(bool Succeeded, string Status);

internal interface IMobilePairingTransport
{
    bool IsPaired { get; }
    string ServerName { get; }
    Task<IReadOnlyList<DiscoveredRadioVaultServer>> DiscoverAsync(
        CancellationToken cancellationToken = default);
    Task PairAsync(
        DiscoveredRadioVaultServer server,
        string pairingCode,
        CancellationToken cancellationToken = default);
    Task PairManuallyAsync(
        string serverAddress,
        int securePort,
        string pairingCode,
        CancellationToken cancellationToken = default);
    void Forget();
}

internal sealed class MobilePairingTransport(MobileServerClient server) : IMobilePairingTransport
{
    private readonly MobileServerClient _server = server ?? throw new ArgumentNullException(nameof(server));

    public bool IsPaired => _server.IsPaired;
    public string ServerName => _server.Connection?.ServerDisplayName ?? "No server paired";

    public Task<IReadOnlyList<DiscoveredRadioVaultServer>> DiscoverAsync(
        CancellationToken cancellationToken = default)
        => _server.DiscoverAsync(cancellationToken: cancellationToken);

    public Task PairAsync(
        DiscoveredRadioVaultServer server,
        string pairingCode,
        CancellationToken cancellationToken = default)
        => _server.PairAsync(server, pairingCode, cancellationToken);

    public Task PairManuallyAsync(
        string serverAddress,
        int securePort,
        string pairingCode,
        CancellationToken cancellationToken = default)
        => _server.PairManuallyAsync(serverAddress, securePort, pairingCode, cancellationToken);

    public void Forget() => _server.Forget();
}
