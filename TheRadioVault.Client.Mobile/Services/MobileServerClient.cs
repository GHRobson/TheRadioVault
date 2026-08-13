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
    private bool _isReachable;

    public MobileServerClient(IMobileConnectionStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _connection = store.Load();
        if (_connection?.IsConfigured == true) _client = CreatePinnedClient(_connection, includeToken: true);
    }

    public RadioVaultMobileConnection? Connection => _connection;
    public bool IsPaired => _connection?.IsConfigured == true;
    public bool IsReachable => _isReachable;
    public event EventHandler? ConnectivityChanged;
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

    public async Task PairManuallyAsync(
        string serverAddress,
        int securePort,
        string pairingCode,
        CancellationToken cancellationToken = default)
    {
        var code = pairingCode?.Trim() ?? string.Empty;
        if (code.Length != 6 || code.Any(ch => !char.IsDigit(ch)))
            throw new InvalidOperationException("Enter the six-digit code shown in Radio Vault Server settings.");
        if (securePort is < 1024 or > 65535)
            throw new InvalidOperationException("Enter a valid Radio Vault HTTPS port.");

        var input = serverAddress?.Trim() ?? string.Empty;
        if (input.Length == 0) throw new InvalidOperationException("Enter the Radio Vault Server address.");
        if (!input.Contains("://", StringComparison.Ordinal)) input = "https://" + input;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var entered) ||
            !entered.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(entered.Host))
            throw new InvalidOperationException("Enter a valid server address, such as 192.168.1.20.");

        var port = entered.IsDefaultPort ? securePort : entered.Port;
        var baseAddress = new UriBuilder(Uri.UriSchemeHttps, entered.Host, port).Uri;
        string observedThumbprint = string.Empty;
        using var handler = new HttpClientHandler
        {
            // The six-digit code authorizes this one initial request. We record the actual TLS
            // certificate and require the server's signed response to name the same certificate
            // before persisting trust for all subsequent connections.
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                observedThumbprint = NormalizeThumbprint(certificate.GetCertHashString());
                return observedThumbprint.Length >= 32;
            }
        };
        using var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(30)
        };

        var previous = _connection;
        var clientId = Guid.TryParse(previous?.ClientId, out _)
            ? previous!.ClientId
            : Guid.NewGuid().ToString("D");
        var displayName = string.IsNullOrWhiteSpace(previous?.ClientDisplayName)
            ? $"Radio Vault on {Environment.MachineName}"
            : previous!.ClientDisplayName;
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

        var reportedThumbprint = NormalizeThumbprint(result.CertificateThumbprint);
        if (!Guid.TryParse(result.InstanceId, out _) ||
            observedThumbprint.Length < 32 ||
            !string.Equals(observedThumbprint, reportedThumbprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The server identity did not match its secure connection. No credentials were saved.");

        var saved = new RadioVaultMobileConnection(
            clientId,
            displayName,
            result.InstanceId,
            string.IsNullOrWhiteSpace(result.DisplayName) ? entered.Host : result.DisplayName.Trim(),
            entered.Host,
            result.SecurePort is >= 1024 and <= 65535 ? result.SecurePort : port,
            reportedThumbprint,
            result.AccessToken,
            result.CapabilityGeneration,
            result.PairedAt ?? DateTimeOffset.UtcNow);
        if (!saved.IsConfigured) throw new InvalidOperationException("The server returned an incomplete pairing relationship.");
        _store.Save(saved);
        _connection = saved;
        ReplaceClient(CreatePinnedClient(saved, includeToken: true));
    }

    public async Task<WebFederationBootstrap> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var envelope = await GetJsonAsync(
            WebApiRoutes.FederationBootstrap,
            MobileJsonContext.Default.BootstrapEnvelope,
            timeout.Token).ConfigureAwait(false);
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
        int offset = 0,
        int? collectionId = null,
        string filter = "All",
        int? year = null,
        int? month = null,
        bool hideCompleted = false,
        string searchScope = "All",
        bool hasTranscript = false,
        CancellationToken cancellationToken = default)
    {
        var query = "?q=" + Uri.EscapeDataString(searchText?.Trim() ?? string.Empty) +
                    (collectionId is > 0 ? "&collectionId=" + collectionId.Value : string.Empty) +
                    (year is > 0 ? "&year=" + year.Value : string.Empty) +
                    (month is > 0 ? "&month=" + month.Value : string.Empty) +
                    "&filter=" + Uri.EscapeDataString(filter) + "&limit=" + Math.Clamp(limit, 1, 10000) +
                    "&offset=" + Math.Max(0, offset) + "&newestFirst=true&scope=" + Uri.EscapeDataString(searchScope) +
                    "&hasTranscript=" + hasTranscript + "&hideCompleted=" + hideCompleted;
        return (await GetJsonAsync(
            WebApiRoutes.ClientLibraryBrowse + query,
            MobileJsonContext.Default.BrowseEnvelope,
            cancellationToken).ConfigureAwait(false)).Result;
    }

    public async Task<MobileLibrarySync> GetLibrarySyncAsync(
        string sessionId,
        long sequence,
        string revision,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var path = WebApiRoutes.FederationLibrarySync +
                   "?after=" + Math.Max(0, sequence) +
                   "&session=" + Uri.EscapeDataString(sessionId ?? string.Empty) +
                   "&revision=" + Uri.EscapeDataString(revision ?? string.Empty) +
                   "&metadataOnly=true";
        var sync = (await GetJsonAsync(
            path,
            MobileJsonContext.Default.MobileLibrarySyncEnvelope,
            timeout.Token).ConfigureAwait(false)).Sync;
        if (!string.Equals(sync.ServerInstanceId, _connection?.ServerInstanceId, StringComparison.Ordinal))
            throw new InvalidOperationException("The cache refresh was answered by a different Radio Vault Server.");
        return sync;
    }

    public async Task<WebClientLibrarySearchFacets> GetSearchFacetsAsync(CancellationToken cancellationToken = default)
        => (await GetJsonAsync(
            WebApiRoutes.ClientLibrarySearchFacets,
            MobileJsonContext.Default.SearchFacetsEnvelope,
            cancellationToken).ConfigureAwait(false)).Facets;

    public async Task<IReadOnlyList<WebClientLibrarySearchSuggestion>> GetSearchSuggestionsAsync(
        string prefix,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var query = "?prefix=" + Uri.EscapeDataString(prefix.Trim()) + "&limit=" + Math.Clamp(limit, 1, 25);
        return (await GetJsonAsync(
            WebApiRoutes.ClientLibrarySearchSuggestions + query,
            MobileJsonContext.Default.SearchSuggestionsEnvelope,
            cancellationToken).ConfigureAwait(false)).Suggestions;
    }

    public async Task<IReadOnlyList<WebSavedCollectionSummary>> GetSavedCollectionsAsync(
        CancellationToken cancellationToken = default)
        => (await GetJsonAsync(
            WebApiRoutes.ClientSavedCollections,
            MobileJsonContext.Default.SavedCollectionsEnvelope,
            cancellationToken).ConfigureAwait(false)).Collections;

    public async Task<WebSavedCollectionDetails> GetSavedCollectionAsync(
        long collectionId,
        CancellationToken cancellationToken = default)
        => (await GetJsonAsync(
            WebApiRoutes.ClientSavedCollection(collectionId),
            MobileJsonContext.Default.SavedCollectionEnvelope,
            cancellationToken).ConfigureAwait(false)).Collection;

    public async Task<WebSavedCollectionMutationResult> CreateSavedCollectionAsync(
        WebSavedCollectionCreateRequest request,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.ClientSavedCollections,
            request,
            MobileJsonContext.Default.WebSavedCollectionCreateRequest,
            MobileJsonContext.Default.SavedCollectionMutationEnvelope,
            cancellationToken).ConfigureAwait(false)).Result;

    public async Task<WebSavedCollectionMutationResult> UpdateSavedCollectionAsync(
        long collectionId,
        WebSavedCollectionUpdateRequest request,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.ClientSavedCollectionUpdate(collectionId),
            request,
            MobileJsonContext.Default.WebSavedCollectionUpdateRequest,
            MobileJsonContext.Default.SavedCollectionMutationEnvelope,
            cancellationToken).ConfigureAwait(false)).Result;

    public async Task<WebSavedCollectionMutationResult> AddSavedCollectionItemAsync(
        long collectionId,
        WebSavedCollectionItemMutation request,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.ClientSavedCollectionAdd(collectionId),
            request,
            MobileJsonContext.Default.WebSavedCollectionItemMutation,
            MobileJsonContext.Default.SavedCollectionMutationEnvelope,
            cancellationToken).ConfigureAwait(false)).Result;

    public async Task<WebSavedCollectionMutationResult> RemoveSavedCollectionItemAsync(
        long collectionId,
        WebSavedCollectionItemMutation request,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.ClientSavedCollectionRemove(collectionId),
            request,
            MobileJsonContext.Default.WebSavedCollectionItemMutation,
            MobileJsonContext.Default.SavedCollectionMutationEnvelope,
            cancellationToken).ConfigureAwait(false)).Result;

    public async Task<WebSavedCollectionMutationResult> MoveSavedCollectionItemAsync(
        long collectionId,
        WebSavedCollectionItemMutation request,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.ClientSavedCollectionMove(collectionId),
            request,
            MobileJsonContext.Default.WebSavedCollectionItemMutation,
            MobileJsonContext.Default.SavedCollectionMutationEnvelope,
            cancellationToken).ConfigureAwait(false)).Result;

    public async Task<WebSavedCollectionMutationResult> DeleteSavedCollectionAsync(
        long collectionId,
        WebSavedCollectionDeleteRequest request,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.ClientSavedCollectionDelete(collectionId),
            request,
            MobileJsonContext.Default.WebSavedCollectionDeleteRequest,
            MobileJsonContext.Default.SavedCollectionMutationEnvelope,
            cancellationToken).ConfigureAwait(false)).Result;

    public async Task<IReadOnlyList<WebClientLibraryArchivePeriodSummary>> GetArchivePeriodsAsync(
        int? collectionId,
        int? year = null,
        bool hideCompleted = false,
        CancellationToken cancellationToken = default)
    {
        var query = "?hideCompleted=" + hideCompleted +
                    (collectionId is > 0 ? "&collectionId=" + collectionId.Value : string.Empty) +
                    (year is > 0 ? "&year=" + year.Value : string.Empty);
        return (await GetJsonAsync(
            WebApiRoutes.ClientLibraryArchivePeriods + query,
            MobileJsonContext.Default.ArchivePeriodsEnvelope,
            cancellationToken).ConfigureAwait(false)).Periods;
    }

    public async Task<MobileWikiOverview> GetWikiOverviewAsync(CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.ClientWikiOperation("overview"),
            new MobileEmptyMutation(),
            MobileJsonContext.Default.MobileEmptyMutation,
            MobileJsonContext.Default.MobileWikiOverviewEnvelope,
            cancellationToken).ConfigureAwait(false)).Value;

    public async Task<IReadOnlyList<MobileWikiPageSummary>> BrowseWikiAsync(
        string? searchText = null,
        string? pageType = null,
        int limit = 500,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.ClientWikiOperation("browse"),
            new MobileWikiBrowseRequest(
                searchText?.Trim() ?? string.Empty,
                pageType?.Trim() ?? string.Empty,
                string.Empty,
                Math.Clamp(limit, 1, 5000)),
            MobileJsonContext.Default.MobileWikiBrowseRequest,
            MobileJsonContext.Default.MobileWikiBrowseEnvelope,
            cancellationToken).ConfigureAwait(false)).Value;

    public async Task<MobileWikiDashboardHighlights> GetWikiDashboardHighlightsAsync(
        int month,
        int day,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.ClientWikiOperation("dashboard-highlights"),
            new MobileWikiDashboardRequest(month, day),
            MobileJsonContext.Default.MobileWikiDashboardRequest,
            MobileJsonContext.Default.MobileWikiHighlightsEnvelope,
            cancellationToken).ConfigureAwait(false)).Value;

    public async Task<MobileWikiPageDocument?> GetWikiPageAsync(
        Guid pageId,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.ClientWikiOperation("page"),
            new MobileWikiPageRequest(pageId),
            MobileJsonContext.Default.MobileWikiPageRequest,
            MobileJsonContext.Default.MobileWikiPageEnvelope,
            cancellationToken).ConfigureAwait(false)).Value;

    public async Task<MobileWikiImageContent?> GetWikiImageAsync(
        Guid imageId,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.ClientWikiOperation("image"),
            new MobileWikiImageRequest(imageId),
            MobileJsonContext.Default.MobileWikiImageRequest,
            MobileJsonContext.Default.MobileWikiImageEnvelope,
            cancellationToken).ConfigureAwait(false)).Value;

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
        => await SetFavouriteAsync(episodeId, favourite, null, cancellationToken).ConfigureAwait(false);

    public async Task<WebMutationResult> SetFavouriteAsync(
        long episodeId,
        bool favourite,
        string? mutationId,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.Favourite(episodeId),
            new MobileFavouriteMutation(favourite),
            MobileJsonContext.Default.MobileFavouriteMutation,
            MobileJsonContext.Default.MutationEnvelope,
            cancellationToken,
            mutationId).ConfigureAwait(false)).Result;

    public async Task<WebMutationResult> SetListeningStatusAsync(
        long episodeId,
        bool played,
        CancellationToken cancellationToken = default)
        => await SetListeningStatusAsync(episodeId, played, null, cancellationToken).ConfigureAwait(false);

    public async Task<WebMutationResult> SetListeningStatusAsync(
        long episodeId,
        bool played,
        string? mutationId,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.ListeningStatus(episodeId),
            new MobileListeningStatusMutation(played),
            MobileJsonContext.Default.MobileListeningStatusMutation,
            MobileJsonContext.Default.MutationEnvelope,
            cancellationToken,
            mutationId).ConfigureAwait(false)).Result;

    public async Task<WebMomentMutationResult> AddMomentAsync(
        long episodeId,
        WebMomentMutation mutation,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.Moments(episodeId),
            mutation,
            MobileJsonContext.Default.WebMomentMutation,
            MobileJsonContext.Default.MomentMutationEnvelope,
            cancellationToken,
            mutation.ClientMutationId).ConfigureAwait(false)).Result;

    public async Task<IReadOnlyList<WebMomentSummary>> GetMomentsAsync(CancellationToken cancellationToken = default)
        => (await GetJsonAsync(
            WebApiRoutes.MomentsAll,
            MobileJsonContext.Default.MomentsEnvelope,
            cancellationToken).ConfigureAwait(false)).Moments;

    public async Task<MobileKnowledgeOverview> GetKnowledgeOverviewAsync(CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.ClientResearchOperation("overview"),
            new MobileEmptyMutation(),
            MobileJsonContext.Default.MobileEmptyMutation,
            MobileJsonContext.Default.MobileKnowledgeOverviewEnvelope,
            cancellationToken).ConfigureAwait(false)).Value;

    public async Task<IReadOnlyList<MobileKnowledgeCollection>> GetKnowledgeCollectionsAsync(CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.ClientResearchOperation("collections"),
            new MobileEmptyMutation(),
            MobileJsonContext.Default.MobileEmptyMutation,
            MobileJsonContext.Default.MobileKnowledgeCollectionsEnvelope,
            cancellationToken).ConfigureAwait(false)).Value;

    public async Task<IReadOnlyList<MobileKnowledgeDateReview>> GetKnowledgeDateReviewsAsync(CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.ClientResearchOperation("date-reviews"),
            new MobileKnowledgeDateReviewsRequest(),
            MobileJsonContext.Default.MobileKnowledgeDateReviewsRequest,
            MobileJsonContext.Default.MobileKnowledgeDateReviewsEnvelope,
            cancellationToken).ConfigureAwait(false)).Value;

    public async Task<MobileKnowledgeCoverage?> GetKnowledgeCoverageAsync(
        int collectionId,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.ClientResearchOperation("coverage"),
            new MobileKnowledgeCollectionRequest(collectionId),
            MobileJsonContext.Default.MobileKnowledgeCollectionRequest,
            MobileJsonContext.Default.MobileKnowledgeCoverageEnvelope,
            cancellationToken).ConfigureAwait(false)).Value;

    public async Task ResolveKnowledgeDateReviewAsync(
        long researchId,
        int action,
        DateOnly? selectedDate = null,
        CancellationToken cancellationToken = default)
        => _ = await PostJsonAsync(
            WebApiRoutes.ClientResearchOperation("resolve-date-review"),
            new MobileKnowledgeResolveRequest(researchId, action, selectedDate),
            MobileJsonContext.Default.MobileKnowledgeResolveRequest,
            MobileJsonContext.Default.MobileKnowledgeMutationEnvelope,
            cancellationToken).ConfigureAwait(false);

    public async Task<WebQueueMutationResult> AddToQueueAsync(
        long episodeId,
        bool playNext = false,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.QueueAdd,
            new MobileQueueAddMutation(episodeId, playNext),
            MobileJsonContext.Default.MobileQueueAddMutation,
            MobileJsonContext.Default.QueueMutationEnvelope,
            cancellationToken).ConfigureAwait(false)).Result;

    public async Task<IReadOnlyList<WebQueueItem>> GetQueueAsync(CancellationToken cancellationToken = default)
        => (await GetJsonAsync(
            WebApiRoutes.Queue,
            MobileJsonContext.Default.QueueEnvelope,
            cancellationToken).ConfigureAwait(false)).Queue;

    public async Task<WebQueueMutationResult> RemoveQueueItemAsync(
        long queueId,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.QueueRemove(queueId),
            new MobileEmptyMutation(),
            MobileJsonContext.Default.MobileEmptyMutation,
            MobileJsonContext.Default.QueueMutationEnvelope,
            cancellationToken).ConfigureAwait(false)).Result;

    public async Task<WebQueueMutationResult> ClearQueueAsync(CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.QueueClear,
            new MobileEmptyMutation(),
            MobileJsonContext.Default.MobileEmptyMutation,
            MobileJsonContext.Default.QueueMutationEnvelope,
            cancellationToken).ConfigureAwait(false)).Result;

    public async Task<WebQueueMutationResult> MoveQueueItemAsync(
        long queueId,
        int direction,
        CancellationToken cancellationToken = default)
        => (await PostJsonAsync(
            WebApiRoutes.QueueMove(queueId),
            new MobileQueueMoveMutation(Math.Sign(direction)),
            MobileJsonContext.Default.MobileQueueMoveMutation,
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

    public async Task<byte[]> GetArtworkAsync(
        long episodeId,
        CancellationToken cancellationToken = default)
    {
        using var response = await OpenResponseAsync(
            WebApiRoutes.Artwork(episodeId),
            range: null,
            cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (content.Length > 20 * 1024 * 1024)
            throw new InvalidDataException("Broadcast artwork exceeds the 20 MB mobile limit.");
        return content;
    }

    public async Task<HttpResponseMessage> OpenResponseAsync(
        string path,
        string? range,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (!string.IsNullOrWhiteSpace(range)) request.Headers.TryAddWithoutValidation("Range", range);
        HttpResponseMessage response;
        try
        {
            response = await RequiredClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            SetReachable(true);
        }
        catch
        {
            SetReachable(false);
            throw;
        }
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
        SetReachable(false);
    }

    private async Task<T> GetJsonAsync<T>(
        string path,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await RequiredClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            SetReachable(false);
            throw;
        }
        using (response)
        {
            SetReachable(true);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"The Radio Vault Server rejected the request ({(int)response.StatusCode}).",
                    null,
                    response.StatusCode);
            return await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("The Radio Vault Server returned an empty response.");
        }
    }

    private async Task<TResponse> PostJsonAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken,
        string? mutationId = null)
    {
        HttpResponseMessage response;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(request, requestType)
            };
            if (!string.IsNullOrWhiteSpace(mutationId))
                message.Headers.TryAddWithoutValidation("X-Radio-Vault-Mutation-Id", mutationId.Trim());
            response = await RequiredClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            SetReachable(false);
            throw;
        }
        using (response)
        {
            SetReachable(true);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Conflict)
                throw new HttpRequestException(
                    $"The Radio Vault Server rejected the request ({(int)response.StatusCode}).",
                    null,
                    response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync(responseType, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The Radio Vault Server returned an empty response.");
            return result;
        }
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
        if (includeToken && !string.IsNullOrWhiteSpace(connection.ClientId))
            client.DefaultRequestHeaders.Add("X-RadioVault-Client-Id", connection.ClientId);
        return client;
    }

    private void ReplaceClient(HttpClient? replacement)
    {
        var previous = _client;
        _client = replacement;
        previous?.Dispose();
    }

    private void SetReachable(bool reachable)
    {
        if (_isReachable == reachable) return;
        _isReachable = reachable;
        ConnectivityChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static string NormalizeThumbprint(string? value)
        => new((value ?? string.Empty).Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

    public void Dispose() => ReplaceClient(null);

}
