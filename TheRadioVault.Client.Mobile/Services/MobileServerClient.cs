using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Client.Mobile.Platform;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile;

public sealed class MobileServerClient : IDisposable
{
    private const int DiscoveryPort = 30829;
    private static readonly System.Net.IPAddress DiscoveryAddress = System.Net.IPAddress.Parse("239.255.82.86");
    private readonly IMobileConnectionStore _store;
    private HttpClient? _client;
    private RadioVaultMobileConnection? _connection;

    public MobileServerClient(IMobileConnectionStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _connection = store.Load();
        if (_connection?.IsConfigured == true) _client = CreatePinnedClient(_connection, includeToken: true);
    }

    public RadioVaultMobileConnection? Connection => _connection;
    public bool IsPaired => _connection?.IsConfigured == true;
    public string ClientId => _connection?.ClientId
        ?? throw new InvalidOperationException("Pair this iPhone with a Radio Vault Server first.");
    public string ClientDisplayName => _connection?.ClientDisplayName
        ?? "Radio Vault on iPhone";

    public async Task<IReadOnlyList<DiscoveredRadioVaultServer>> DiscoverAsync(
        TimeSpan? duration = null,
        CancellationToken cancellationToken = default)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true,
            ExclusiveAddressUse = false
        };
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, DiscoveryPort));
        try { udp.JoinMulticastGroup(DiscoveryAddress); } catch (SocketException) { }

        var found = new Dictionary<string, DiscoveredRadioVaultServer>(StringComparer.Ordinal);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(duration ?? TimeSpan.FromSeconds(4));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                var packet = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                var value = JsonSerializer.Deserialize(
                    packet.Buffer, MobileJsonContext.Default.WebLanDiscoveryAnnouncement);
                var thumbprint = NormalizeThumbprint(value?.CertificateThumbprint);
                if (value is null ||
                    !string.Equals(value.Protocol, "radiovault-lan-v1", StringComparison.Ordinal) ||
                    !Guid.TryParse(value.InstanceId, out _) ||
                    value.SecurePort is < 1024 or > 65535 ||
                    thumbprint.Length < 32)
                    continue;

                found[value.InstanceId] = new DiscoveredRadioVaultServer(
                    value.InstanceId,
                    string.IsNullOrWhiteSpace(value.DisplayName) ? "Radio Vault Server" : value.DisplayName.Trim(),
                    packet.RemoteEndPoint.Address.ToString(),
                    value.SecurePort,
                    thumbprint,
                    value.AppVersion,
                    value.PairingAvailable,
                    value.PairedDesktopClients);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { break; }
            catch (JsonException) { }
            catch (SocketException) { break; }
        }

        return found.Values
            .OrderByDescending(server => server.PairingAvailable)
            .ThenBy(server => server.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task PairAsync(
        DiscoveredRadioVaultServer server,
        string pairingCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        var code = pairingCode?.Trim() ?? string.Empty;
        if (code.Length != 6 || code.Any(ch => !char.IsDigit(ch)))
            throw new InvalidOperationException("Enter the six-digit code shown in Radio Vault Server settings.");

        var previous = _connection;
        var clientId = Guid.TryParse(previous?.ClientId, out _)
            ? previous!.ClientId
            : Guid.NewGuid().ToString("D");
        var displayName = string.IsNullOrWhiteSpace(previous?.ClientDisplayName)
            ? $"Radio Vault on {Environment.MachineName}"
            : previous!.ClientDisplayName;
        var pending = new RadioVaultMobileConnection(
            clientId, displayName, server.InstanceId, server.DisplayName, server.Address,
            server.SecurePort, server.CertificateThumbprint, string.Empty, 0, DateTimeOffset.UtcNow);

        using var client = CreatePinnedClient(pending, includeToken: false);
        var request = new WebDesktopPairingRequest(code, clientId, displayName);
        using var response = await client.PostAsJsonAsync(
            WebApiRoutes.FederationPair,
            request,
            MobileJsonContext.Default.WebDesktopPairingRequest,
            cancellationToken).ConfigureAwait(false);
        var envelope = await response.Content.ReadFromJsonAsync(
            MobileJsonContext.Default.PairingEnvelope,
            cancellationToken).ConfigureAwait(false);
        var result = envelope?.Result;
        if (!response.IsSuccessStatusCode || result?.Paired != true)
            throw new InvalidOperationException(result?.Message ?? $"The server rejected pairing ({(int)response.StatusCode}).");
        if (!string.Equals(result.InstanceId, server.InstanceId, StringComparison.Ordinal) ||
            !string.Equals(NormalizeThumbprint(result.CertificateThumbprint), server.CertificateThumbprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The server identity changed during pairing. No credentials were saved.");

        var saved = pending with
        {
            ServerDisplayName = result.DisplayName,
            SecurePort = result.SecurePort,
            CertificateThumbprint = NormalizeThumbprint(result.CertificateThumbprint),
            AccessToken = result.AccessToken,
            CapabilityGeneration = result.CapabilityGeneration,
            PairedAt = result.PairedAt ?? DateTimeOffset.UtcNow
        };
        if (!saved.IsConfigured) throw new InvalidOperationException("The server returned an incomplete pairing relationship.");
        _store.Save(saved);
        _connection = saved;
        ReplaceClient(CreatePinnedClient(saved, includeToken: true));
    }

    public async Task<WebFederationBootstrap> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var envelope = await GetJsonAsync(
            WebApiRoutes.FederationBootstrap,
            MobileJsonContext.Default.BootstrapEnvelope,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(envelope.FederationBootstrap.Server.InstanceId, _connection?.ServerInstanceId, StringComparison.Ordinal))
            throw new InvalidOperationException("The saved address now belongs to a different Radio Vault Server.");
        return envelope.FederationBootstrap;
    }

    public async Task<WebClientLibraryOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        => (await GetJsonAsync(
            WebApiRoutes.ClientLibraryOverview,
            MobileJsonContext.Default.OverviewEnvelope,
            cancellationToken).ConfigureAwait(false)).Overview;

    public async Task<WebClientLibraryBrowseResult> BrowseAsync(
        string? searchText,
        int limit = 100,
        int? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        var query = "?q=" + Uri.EscapeDataString(searchText?.Trim() ?? string.Empty) +
                    (collectionId is > 0 ? "&collectionId=" + collectionId.Value : string.Empty) +
                    "&filter=All&limit=" + Math.Clamp(limit, 1, 250) +
                    "&offset=0&newestFirst=true&scope=All&hasTranscript=false";
        return (await GetJsonAsync(
            WebApiRoutes.ClientLibraryBrowse + query,
            MobileJsonContext.Default.BrowseEnvelope,
            cancellationToken).ConfigureAwait(false)).Result;
    }

    public async Task<WebClientLibraryBroadcastSummary> GetBroadcastSummaryAsync(
        long episodeId,
        CancellationToken cancellationToken = default)
        => (await GetJsonAsync(
            WebApiRoutes.ClientLibraryBroadcast(episodeId),
            MobileJsonContext.Default.BroadcastSummaryEnvelope,
            cancellationToken).ConfigureAwait(false)).Broadcast;

    public async Task<WebClientBroadcastDetails> GetBroadcastDetailsAsync(
        long episodeId,
        CancellationToken cancellationToken = default)
        => (await GetJsonAsync(
            WebApiRoutes.ClientBroadcast(episodeId),
            MobileJsonContext.Default.BroadcastDetailsEnvelope,
            cancellationToken).ConfigureAwait(false)).Broadcast;

    public async Task<WebMutationResult> SetFavouriteAsync(
        long episodeId,
        bool favourite,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.Favourite(episodeId),
            new MobileFavouriteMutation(favourite),
            MobileJsonContext.Default.MobileFavouriteMutation,
            MobileJsonContext.Default.MutationEnvelope,
            cancellationToken).ConfigureAwait(false)).Result;

    public async Task<WebQueueMutationResult> AddToQueueAsync(
        long episodeId,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.QueueAdd,
            new MobileQueueAddMutation(episodeId),
            MobileJsonContext.Default.MobileQueueAddMutation,
            MobileJsonContext.Default.QueueMutationEnvelope,
            cancellationToken).ConfigureAwait(false)).Result;

    public Task<WebCanonicalMediaManifest> GetMediaManifestAsync(
        long episodeId,
        CancellationToken cancellationToken = default)
        => GetJsonAsync(
            WebApiRoutes.MediaManifest(episodeId),
            MobileJsonContext.Default.WebCanonicalMediaManifest,
            cancellationToken);

    public async Task<WebPlaybackSession> GetPlaybackSessionAsync(CancellationToken cancellationToken = default)
        => (await GetJsonAsync(
            WebApiRoutes.Player,
            MobileJsonContext.Default.PlaybackSessionEnvelope,
            cancellationToken).ConfigureAwait(false)).Session;

    public async Task<WebClientPlaybackResult> UpdateLivePlaybackAsync(
        WebClientPlaybackUpdate update,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.PlayerWebProgress,
            update,
            MobileJsonContext.Default.WebClientPlaybackUpdate,
            MobileJsonContext.Default.ClientPlaybackEnvelope,
            cancellationToken).ConfigureAwait(false)).Result;

    public async Task<WebPlaybackTransferResult> BeginPlaybackTransferAsync(
        WebPlaybackTransferBeginRequest request,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.PlayerTransferBegin,
            request,
            MobileJsonContext.Default.WebPlaybackTransferBeginRequest,
            MobileJsonContext.Default.PlaybackTransferEnvelope,
            cancellationToken).ConfigureAwait(false)).Result;

    public async Task<WebPlaybackTransferResult> MarkPlaybackTransferReadyAsync(
        WebPlaybackTransferReadyRequest request,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.PlayerTransferReady,
            request,
            MobileJsonContext.Default.WebPlaybackTransferReadyRequest,
            MobileJsonContext.Default.PlaybackTransferEnvelope,
            cancellationToken).ConfigureAwait(false)).Result;

    public async Task<WebPlaybackTransferResult> CommitPlaybackTransferAsync(
        WebPlaybackTransferCommitRequest request,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.PlayerTransferCommit,
            request,
            MobileJsonContext.Default.WebPlaybackTransferCommitRequest,
            MobileJsonContext.Default.PlaybackTransferEnvelope,
            cancellationToken).ConfigureAwait(false)).Result;

    public async Task CancelPlaybackTransferAsync(
        WebPlaybackTransferCancelRequest request,
        CancellationToken cancellationToken = default)
        => _ = await PostJsonAsync(
            WebApiRoutes.PlayerTransferCancel,
            request,
            MobileJsonContext.Default.WebPlaybackTransferCancelRequest,
            MobileJsonContext.Default.PlaybackTransferEnvelope,
            cancellationToken).ConfigureAwait(false);

    public async Task AcknowledgePlaybackSourceStoppedAsync(
        WebPlaybackTransferSourceStoppedRequest request,
        CancellationToken cancellationToken = default)
        => _ = await PostJsonAsync(
            WebApiRoutes.PlayerTransferSourceStopped,
            request,
            MobileJsonContext.Default.WebPlaybackTransferSourceStoppedRequest,
            MobileJsonContext.Default.PlaybackTransferEnvelope,
            cancellationToken).ConfigureAwait(false);

    public async Task<WebOfflineProgressResult> SaveProgressAsync(
        WebOfflineProgressUpdate update,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.OfflineProgress(update.EpisodeId),
            update,
            MobileJsonContext.Default.WebOfflineProgressUpdate,
            MobileJsonContext.Default.ProgressEnvelope,
            cancellationToken).ConfigureAwait(false)).Result;

    public async Task<HttpResponseMessage> OpenResponseAsync(
        string path,
        string? range,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (!string.IsNullOrWhiteSpace(range)) request.Headers.TryAddWithoutValidation("Range", range);
        var response = await RequiredClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            response.Dispose();
            throw new HttpRequestException($"The Radio Vault Server rejected the media request ({(int)response.StatusCode}).");
        }
        return response;
    }

    public void Forget()
    {
        _store.Delete();
        _connection = null;
        ReplaceClient(null);
    }

    private async Task<T> GetJsonAsync<T>(
        string path,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        => await RequiredClient.GetFromJsonAsync(path, typeInfo, cancellationToken).ConfigureAwait(false)
           ?? throw new InvalidOperationException("The Radio Vault Server returned an empty response.");

    private async Task<TResponse> PostJsonAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
    {
        using var response = await RequiredClient.PostAsJsonAsync(
            path, request, requestType, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync(responseType, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Radio Vault Server returned an empty response.");
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Conflict)
            throw new HttpRequestException($"The Radio Vault Server rejected the request ({(int)response.StatusCode}).");
        return result;
    }

    private HttpClient RequiredClient => _client
        ?? throw new InvalidOperationException("Pair this iPhone with a Radio Vault Server first.");

    private static HttpClient CreatePinnedClient(RadioVaultMobileConnection connection, bool includeToken)
    {
        var expected = NormalizeThumbprint(connection.CertificateThumbprint);
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                string.Equals(NormalizeThumbprint(certificate.GetCertHashString()), expected, StringComparison.Ordinal)
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri($"https://{connection.ServerAddress}:{connection.SecurePort}"),
            Timeout = TimeSpan.FromMinutes(10)
        };
        if (includeToken && !string.IsNullOrWhiteSpace(connection.AccessToken))
            client.DefaultRequestHeaders.Add("X-RadioVault-Token", connection.AccessToken);
        return client;
    }

    private void ReplaceClient(HttpClient? replacement)
    {
        var previous = _client;
        _client = replacement;
        previous?.Dispose();
    }

    internal static string NormalizeThumbprint(string? value)
        => new((value ?? string.Empty).Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

    public void Dispose() => ReplaceClient(null);

}
