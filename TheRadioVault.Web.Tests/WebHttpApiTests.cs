using System.Globalization;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;
using TheRadioVault.Web.Services;
using TheRadioVault.Web.Tests.Fixtures;
using static TheRadioVault.Web.Tests.Fixtures.WebServerFixture;
using static TheRadioVault.Web.Tests.Fixtures.TestSourceTree;
using static TheRadioVault.Web.Tests.TestAssert;

namespace TheRadioVault.Web.Tests;

internal static class WebHttpApiTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases { get; } =
    [
        ("Web API serves versioned broadcast details", WebApiServesVersionedBroadcastDetails),
        ("Full client API preserves native Library and Broadcast Info fields", FullClientApiPreservesNativeLibraryFields),
        ("Full client API serves Research and transcription through the server", FullClientApiServesResearchAndTranscription),
        ("Full client API previews Research pack uploads", FullClientApiPreviewsResearchPackUploads),
        ("Web API exposes Anywhere server identity", WebApiExposesAnywhereServerIdentity),
        ("Web API exposes Anywhere bootstrap", WebApiExposesAnywhereBootstrap),
        ("Web API exposes lightweight federation bootstrap", WebApiExposesLightweightFederationBootstrap),
        ("Web API rejects missing token", WebApiRejectsMissingToken),
        ("Web API accepts paired remote-client header token", WebApiAcceptsPairedDesktopHeaderToken),
        ("Federation bootstrap survives unrelated playback failure", FederationBootstrapSurvivesPlaybackFailure),
        ("Web API accepts chunked JSON request bodies", WebApiAcceptsChunkedJsonRequestBodies),
        ("Web API changes favourite state", WebApiChangesFavouriteState),
        ("Web API changes listening state", WebApiChangesListeningState),
        ("Web API exposes change feed", WebApiExposesChangeFeed),
        ("Federation Library sync exposes a reset and revision", FederationLibrarySyncExposesResetAndRevision),
        ("Federation parity exposes normal application surfaces", FederationParityExposesNormalApplicationSurfaces),
        ("Federation Research workspace exposes server records", FederationResearchWorkspaceExposesServerRecords),
        ("Federation Research undated and coverage routes are authoritative", FederationResearchUndatedAndCoverageRoutesAreAuthoritative),
        ("Web API exposes jobs", WebApiExposesJobs),
        ("Web API requests job cancellation", WebApiRequestsJobCancellation),
        ("Web server stop is idempotent", WebServerStopIsIdempotent),
        ("Web server survives rapid restart generations", WebServerSurvivesRapidRestartGenerations),
        ("Embedded web assets load from the Web assembly", EmbeddedWebAssetsLoadFromAssembly),
        ("Web API sends remote playback commands", WebApiSendsRemotePlaybackCommands),
        ("Web API exposes server playback session", WebApiExposesAuthoritativePlaybackSession),
        ("Web API acknowledges the physical source-stop boundary", WebApiAcknowledgesPhysicalSourceStop),
        ("Paused phone retains playback ownership", PausedPhoneRetainsPlaybackOwnership),
        ("Web API manages queue", WebApiManagesQueue),
        ("Web API preserves Moment identity while editing", WebApiEditsMomentInPlace),
        ("Web API synchronises offline progress", WebApiSynchronisesOfflineProgress),
        ("Web API permits explicit LAN progress rewind", WebApiPermitsExplicitLanProgressRewind),
        ("Secure web options carry certificate material", SecureWebOptionsCarryCertificateMaterial),
        ("Client transcription queue returns the provider job", ClientTranscriptionQueueReturnsJobId)
    ];

static void WebApiServesVersionedBroadcastDetails()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var json = await client.GetStringAsync($"http://127.0.0.1:{port}/api/v1/broadcasts/9?token={Uri.EscapeDataString(token)}");
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Equal("v1", document.RootElement.GetProperty("apiVersion").GetString());
        var broadcast = document.RootElement.GetProperty("broadcast");
        var episode = broadcast.GetProperty("episode");
        Equal(9L, episode.GetProperty("id").GetInt64());
        Equal(9L, broadcast.GetProperty("canonicalBroadcastId").GetInt64());
        Equal("canonical-broadcast", episode.GetProperty("identityKind").GetString());
        Equal("Phoenix test broadcast", episode.GetProperty("title").GetString());
        Equal("A specific test summary.", episode.GetProperty("summary").GetString());
        Equal("WJFK", broadcast.GetProperty("station").GetString());
        Equal("Afternoon", broadcast.GetProperty("slot").GetString());
        Equal(2, broadcast.GetProperty("totalParts").GetInt32());
        Equal("Server archive note.", broadcast.GetProperty("archiveNotes").GetString());
        Equal("Ron Bennington", broadcast.GetProperty("people")[0].GetProperty("name").GetString());
        Equal("Comedy", broadcast.GetProperty("topics")[0].GetString());
        Equal("Test moment", broadcast.GetProperty("moments")[0].GetProperty("title").GetString());
        True(!episode.TryGetProperty("audioPath", out _));
        True(!episode.TryGetProperty("artworkPath", out _));
    });
}

static void FullClientApiPreservesNativeLibraryFields()
{
    Equal("/api/v1/client/library/overview", WebApiRoutes.ClientLibraryOverview);
    Equal("/api/v1/client/library/broadcasts/9", WebApiRoutes.ClientLibraryBroadcast(9));
    Equal("/api/v1/client/broadcast-details/9", WebApiRoutes.ClientBroadcast(9));

    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Add("X-RadioVault-Token", token);

        var overview = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"http://127.0.0.1:{port}{WebApiRoutes.ClientLibraryOverview}");
        Equal(1, overview.GetProperty("overview").GetProperty("totalBroadcasts").GetInt32());
        Equal("Ron & Fez", overview.GetProperty("overview").GetProperty("collections")[0].GetProperty("collectionName").GetString());

        var browse = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"http://127.0.0.1:{port}{WebApiRoutes.ClientLibraryBrowse}?filter=ContinueListening&limit=25");
        var summary = browse.GetProperty("result").GetProperty("broadcasts")[0];
        Equal("CANONICAL-9", summary.GetProperty("canonicalKey").GetString());
        Equal(2, summary.GetProperty("segmentCount").GetInt32());

        var details = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"http://127.0.0.1:{port}{WebApiRoutes.ClientBroadcast(9)}");
        var broadcast = details.GetProperty("broadcast");
        Equal("WJFK", broadcast.GetProperty("station").GetString());
        Equal("Ron Bennington", broadcast.GetProperty("hosts").GetString());
        Equal(2, broadcast.GetProperty("physicalFileCount").GetInt32());
    });
}

static void FullClientApiServesResearchAndTranscription()
{
    Equal("/api/v1/client/research/overview", WebApiRoutes.ClientResearchOperation("overview"));
    Equal("/api/v1/client/transcripts/jobs", WebApiRoutes.ClientTranscriptOperation("jobs"));
    Equal("/api/v1/client/transcription/jobs", WebApiRoutes.ClientTranscriptionOperation("jobs"));

    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Add("X-RadioVault-Token", token);

        using var overviewResponse = await client.PostAsJsonAsync(
            $"http://127.0.0.1:{port}{WebApiRoutes.ClientResearchOperation("overview")}", new { });
        overviewResponse.EnsureSuccessStatusCode();
        var overview = await overviewResponse.Content.ReadFromJsonAsync<JsonElement>();
        Equal(1, overview.GetProperty("value").GetProperty("totalResearchRecords").GetInt32());

        using var jobsResponse = await client.PostAsJsonAsync(
            $"http://127.0.0.1:{port}{WebApiRoutes.ClientTranscriptionOperation("jobs")}", new { limit = 25 });
        jobsResponse.EnsureSuccessStatusCode();
        var jobs = await jobsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Equal(0, jobs.GetProperty("value").GetArrayLength());
    });
}

static void FullClientApiPreviewsResearchPackUploads()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Add("X-RadioVault-Token", token);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"http://127.0.0.1:{port}{WebApiRoutes.FederationResearchImportPreview}");
        request.Headers.TryAddWithoutValidation("X-Radio-Vault-File-Name", Uri.EscapeDataString("test-pack.trvpack"));
        request.Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 });
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "application/vnd.theradiovault.research-pack+zip");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        True(body.GetProperty("result").GetProperty("sessionId").GetGuid() != Guid.Empty);
        var preview = body.GetProperty("result").GetProperty("preview");
        Equal("test-pack.trvpack", preview.GetProperty("packageName").GetString());
        Equal(3, preview.GetProperty("totalRecords").GetInt32());
        Equal(1, preview.GetProperty("transcriptCount").GetInt32());
    });
}

static void FederationParityExposesNormalApplicationSurfaces()
{
    var token = "test-token";
    using var server = new LocalWebServer(new TestWebArchiveProvider(), new WebServerOptions
    {
        Port = 0, AccessToken = token, AppVersion = "test", ServerInstanceId = "server-test",
        ServerDisplayName = "Test server", DatabaseSchemaVersion = 45, CapabilityGeneration = 14,
        LanFederationEnabled = true
    });
    server.Start();
    var port = server.Port;
    using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false });
    var payload = client.GetFromJsonAsync<System.Text.Json.JsonElement>(
        $"http://127.0.0.1:{port}{WebApiRoutes.FederationParity}?token={Uri.EscapeDataString(token)}").GetAwaiter().GetResult();
    var parity = payload.GetProperty("parity");
    Equal(14, parity.GetProperty("capabilityGeneration").GetInt32());
    True(!parity.GetProperty("fullParity").GetBoolean());
    var ids = parity.GetProperty("features").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToArray();
    True(ids.Contains("library"));
    True(ids.Contains("research-workspace"));
    True(ids.Contains("diagnostics"));
}

static void FederationResearchWorkspaceExposesServerRecords()
{
    var token = "test-token";
    using var server = new LocalWebServer(new TestWebArchiveProvider(), new WebServerOptions
    {
        Port = 0, AccessToken = token, AppVersion = "test", ServerInstanceId = "server-test",
        ServerDisplayName = "Test server", DatabaseSchemaVersion = 45, CapabilityGeneration = 14,
        LanFederationEnabled = true
    });
    server.Start();
    var port = server.Port;
    using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false });
    var payload = client.GetFromJsonAsync<System.Text.Json.JsonElement>(
        $"http://127.0.0.1:{port}{WebApiRoutes.FederationResearchWorkspace}?token={Uri.EscapeDataString(token)}").GetAwaiter().GetResult();
    var research = payload.GetProperty("research");
    Equal(1, research.GetProperty("overview").GetProperty("totalResearchRecords").GetInt32());
    Equal(99L, research.GetProperty("records").EnumerateArray().First().GetProperty("id").GetInt64());
    var record = client.GetFromJsonAsync<System.Text.Json.JsonElement>(
        $"http://127.0.0.1:{port}{WebApiRoutes.ResearchWorkspaceRecord(99)}?token={Uri.EscapeDataString(token)}").GetAwaiter().GetResult();
    Equal("Test source", record.GetProperty("record").GetProperty("sources").EnumerateArray().First().GetProperty("title").GetString());
}

static void FederationResearchUndatedAndCoverageRoutesAreAuthoritative()
{
    var token = "test-token";
    using var server = new LocalWebServer(new TestWebArchiveProvider(), new WebServerOptions
    {
        Port = 0, AccessToken = token, AppVersion = "test", ServerInstanceId = "server-test",
        ServerDisplayName = "Test server", DatabaseSchemaVersion = 45, CapabilityGeneration = 14,
        LanFederationEnabled = true
    });
    server.Start();
    var port = server.Port;
    using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false });

    var undated = client.GetFromJsonAsync<System.Text.Json.JsonElement>(
        $"http://127.0.0.1:{port}{WebApiRoutes.FederationResearchUndated}?token={Uri.EscapeDataString(token)}").GetAwaiter().GetResult();
    Equal(0, undated.GetProperty("broadcasts").GetArrayLength());

    var coverage = client.GetFromJsonAsync<System.Text.Json.JsonElement>(
        $"http://127.0.0.1:{port}{WebApiRoutes.ResearchCoverageByShow("Ron & Fez")}?token={Uri.EscapeDataString(token)}").GetAwaiter().GetResult();
    Equal("Ron & Fez", coverage.GetProperty("coverage").GetProperty("showName").GetString());
    Equal(100, coverage.GetProperty("coverage").GetProperty("days").EnumerateArray().First().GetProperty("metadataScore").GetInt32());

    using var request = new StringContent("{\"broadcastDate\":\"2005-05-12\"}", System.Text.Encoding.UTF8, "application/json");
    var response = client.PostAsync(
        $"http://127.0.0.1:{port}{WebApiRoutes.ResearchUndatedDate(9)}?token={Uri.EscapeDataString(token)}", request).GetAwaiter().GetResult();
    True(response.IsSuccessStatusCode);
    var assigned = System.Text.Json.JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement;
    True(assigned.GetProperty("result").GetProperty("updated").GetBoolean());
}

static void WebApiExposesAnywhereServerIdentity()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var payload = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"http://127.0.0.1:{port}{WebApiRoutes.ServerInfo}?token={Uri.EscapeDataString(token)}");
        Equal("v1", payload.GetProperty("apiVersion").GetString());
        var server = payload.GetProperty("server");
        Equal("Test Radio Vault", server.GetProperty("displayName").GetString());
        Equal("test-web-version", server.GetProperty("appVersion").GetString());
        Equal(47, server.GetProperty("databaseSchemaVersion").GetInt32());
        Equal(3, server.GetProperty("capabilityGeneration").GetInt32());
        True(server.GetProperty("capabilities").GetArrayLength() >= 12);
    });
}

static void WebApiExposesAnywhereBootstrap()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var payload = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"http://127.0.0.1:{port}{WebApiRoutes.Bootstrap}?limit=5&token={Uri.EscapeDataString(token)}");
        var bootstrap = payload.GetProperty("bootstrap");
        Equal(1, bootstrap.GetProperty("library").GetProperty("broadcastCount").GetInt32());
        Equal(1, bootstrap.GetProperty("shows").GetArrayLength());
        True(bootstrap.GetProperty("recent").GetArrayLength() > 0);
        True(bootstrap.GetProperty("years").GetArrayLength() > 0);
        True(bootstrap.TryGetProperty("onThisDay", out _));
        Equal(9L, bootstrap.GetProperty("recent")[0].GetProperty("canonicalBroadcastId").GetInt64());
        True(bootstrap.TryGetProperty("playback", out _));
        True(bootstrap.TryGetProperty("queue", out _));
    });
}

static void WebApiExposesLightweightFederationBootstrap()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var payload = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"http://127.0.0.1:{port}{WebApiRoutes.FederationBootstrap}?token={Uri.EscapeDataString(token)}");
        var bootstrap = payload.GetProperty("federationBootstrap");
        Equal(1, bootstrap.GetProperty("library").GetProperty("broadcastCount").GetInt32());
        Equal(1, bootstrap.GetProperty("library").GetProperty("showCount").GetInt32());
        Equal(1, bootstrap.GetProperty("queueCount").GetInt32());
        True(!bootstrap.TryGetProperty("playback", out _));
        True(!bootstrap.TryGetProperty("queue", out _));
    });
}

static void WebApiRejectsMissingToken()
{
    WithWebServer(async (port, _) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/api/v1/broadcasts/9");
        Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    });
}

static void WebApiChangesFavouriteState()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.PostAsJsonAsync($"http://127.0.0.1:{port}/api/v1/broadcasts/9/favourite?token={Uri.EscapeDataString(token)}", new { favourite = false });
        response.EnsureSuccessStatusCode();
        var details = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"http://127.0.0.1:{port}/api/v1/broadcasts/9?token={Uri.EscapeDataString(token)}");
        True(!details.GetProperty("broadcast").GetProperty("episode").GetProperty("favourite").GetBoolean());
    });
}

static void WebApiChangesListeningState()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.PostAsJsonAsync($"http://127.0.0.1:{port}/api/v1/broadcasts/9/listening-status?token={Uri.EscapeDataString(token)}", new { played = true });
        response.EnsureSuccessStatusCode();
        var details = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"http://127.0.0.1:{port}/api/v1/broadcasts/9?token={Uri.EscapeDataString(token)}");
        Equal("Completed", details.GetProperty("broadcast").GetProperty("episode").GetProperty("status").GetString());
    });
}

static void WebApiExposesChangeFeed()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        await client.PostAsJsonAsync($"http://127.0.0.1:{port}/api/v1/broadcasts/9/favourite?token={Uri.EscapeDataString(token)}", new { favourite = false });
        var changes = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"http://127.0.0.1:{port}/api/v1/events?after=0&token={Uri.EscapeDataString(token)}");
        True(changes.GetProperty("count").GetInt32() > 0);
    });
}

static void FederationLibrarySyncExposesResetAndRevision()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var payload = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"http://127.0.0.1:{port}{WebApiRoutes.FederationLibrarySync}?after=0&token={Uri.EscapeDataString(token)}");
        var sync = payload.GetProperty("sync");
        True(sync.GetProperty("resetRequired").GetBoolean());
        True(sync.GetProperty("sessionId").GetString()?.Length > 10);
        True(sync.GetProperty("libraryRevision").GetString()?.Length == 64);
        True(sync.GetProperty("episodes").GetArrayLength() > 0);
        True(sync.TryGetProperty("transcriptSummaries", out _));
        True(sync.TryGetProperty("moments", out _));
        True(sync.TryGetProperty("settingsSnapshot", out var settingsSnapshot) && settingsSnapshot.ValueKind == System.Text.Json.JsonValueKind.Object);

        var session = sync.GetProperty("sessionId").GetString() ?? string.Empty;
        var revision = sync.GetProperty("libraryRevision").GetString() ?? string.Empty;
        var sequence = sync.GetProperty("sequence").GetInt64();
        var unchanged = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"http://127.0.0.1:{port}{WebApiRoutes.FederationLibrarySync}?after={sequence}&session={Uri.EscapeDataString(session)}&revision={Uri.EscapeDataString(revision)}&token={Uri.EscapeDataString(token)}");
        var unchangedSync = unchanged.GetProperty("sync");
        True(unchangedSync.GetProperty("noChanges").GetBoolean());
        Equal(0, unchangedSync.GetProperty("episodes").GetArrayLength());

        using var mutation = await client.PostAsJsonAsync(
            $"http://127.0.0.1:{port}/api/v1/broadcasts/9/favourite?token={Uri.EscapeDataString(token)}",
            new { favourite = false });
        mutation.EnsureSuccessStatusCode();
        var delta = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"http://127.0.0.1:{port}{WebApiRoutes.FederationLibrarySync}?after={sequence}&session={Uri.EscapeDataString(session)}&revision={Uri.EscapeDataString(revision)}&token={Uri.EscapeDataString(token)}");
        var deltaSync = delta.GetProperty("sync");
        True(!deltaSync.GetProperty("resetRequired").GetBoolean());
        True(!deltaSync.GetProperty("noChanges").GetBoolean());
        Equal(1, deltaSync.GetProperty("episodes").GetArrayLength());
        True(!deltaSync.GetProperty("episodes")[0].GetProperty("favourite").GetBoolean());

        var metadataDelta = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"http://127.0.0.1:{port}{WebApiRoutes.FederationLibrarySync}?after={sequence}&session={Uri.EscapeDataString(session)}&revision={Uri.EscapeDataString(revision)}&metadataOnly=true&token={Uri.EscapeDataString(token)}");
        var metadataSync = metadataDelta.GetProperty("sync");
        True(!metadataSync.GetProperty("resetRequired").GetBoolean());
        True(metadataSync.GetProperty("changes").GetArrayLength() > 0);
        Equal(0, metadataSync.GetProperty("episodes").GetArrayLength());
        True(metadataSync.GetProperty("bootstrap").ValueKind == System.Text.Json.JsonValueKind.Null);
    });
}

static void WebApiExposesJobs()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var jobs = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"http://127.0.0.1:{port}/api/v1/jobs?token={Uri.EscapeDataString(token)}");
        Equal(1, jobs.GetProperty("count").GetInt32());
    });
}

static void WebApiRequestsJobCancellation()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.PostAsync($"http://127.0.0.1:{port}/api/v1/jobs/{TestWebArchiveProvider.JobId:D}/cancel?token={Uri.EscapeDataString(token)}", null);
        Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
    });
}

static void WebApiAcceptsChunkedJsonRequestBodies()
{
    WithWebServer(async (port, token) =>
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port);
        await using var stream = tcp.GetStream();
        var json = "{\"favourite\":false}";
        var first = json[..8];
        var second = json[8..];
        var request = new StringBuilder()
            .Append("POST /api/v1/broadcasts/9/favourite?token=").Append(Uri.EscapeDataString(token)).Append(" HTTP/1.1\r\n")
            .Append("Host: 127.0.0.1\r\n")
            .Append("Content-Type: application/json; charset=utf-8\r\n")
            .Append("Transfer-Encoding: chunked\r\n")
            .Append("Connection: close\r\n\r\n")
            .Append(Encoding.UTF8.GetByteCount(first).ToString("X", CultureInfo.InvariantCulture)).Append("\r\n").Append(first).Append("\r\n")
            .Append(Encoding.UTF8.GetByteCount(second).ToString("X", CultureInfo.InvariantCulture)).Append(";rv=test\r\n").Append(second).Append("\r\n")
            .Append("0\r\n\r\n")
            .ToString();
        var bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();

        using var responseBuffer = new MemoryStream();
        var buffer = new byte[2048];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0) responseBuffer.Write(buffer, 0, read);
        var response = Encoding.UTF8.GetString(responseBuffer.ToArray());
        True(response.StartsWith("HTTP/1.1 200 OK", StringComparison.Ordinal));
        True(response.Contains("\"changed\":true", StringComparison.OrdinalIgnoreCase));
    });
}

static void FederationBootstrapSurvivesPlaybackFailure()
{
    var token = "test-token-" + Guid.NewGuid().ToString("N");
    using var server = new LocalWebServer(new TestWebArchiveProvider(throwPlayback: true), new WebServerOptions
    {
        AppVersion = "test-web-version",
        ServerInstanceId = "11111111-2222-3333-4444-555555555555",
        ServerDisplayName = "Test Radio Vault Server",
        DatabaseSchemaVersion = 45,
        CapabilityGeneration = 8,
        Port = 0,
        AccessToken = token,
        LoopbackOnly = true
    });
    server.Start();
    var port = server.Port;
    try
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var federation = client.GetAsync($"http://127.0.0.1:{port}{WebApiRoutes.FederationBootstrap}?token={Uri.EscapeDataString(token)}").GetAwaiter().GetResult();
        federation.EnsureSuccessStatusCode();
        var full = client.GetAsync($"http://127.0.0.1:{port}{WebApiRoutes.Bootstrap}?token={Uri.EscapeDataString(token)}").GetAwaiter().GetResult();
        Equal(System.Net.HttpStatusCode.InternalServerError, full.StatusCode);
        var error = full.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>().GetAwaiter().GetResult();
        Equal("bootstrap-failed", error.GetProperty("error").GetProperty("code").GetString());
        True(!string.IsNullOrWhiteSpace(error.GetProperty("error").GetProperty("diagnosticId").GetString()));
    }
    finally
    {
        server.Stop();
    }
}

static void WebApiAcceptsPairedDesktopHeaderToken()
{
    var primaryToken = "primary-token-" + Guid.NewGuid().ToString("N");
    var pairedToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    using var server = new LocalWebServer(new TestWebArchiveProvider(), new WebServerOptions
    {
        AppVersion = "test-web-version",
        ServerInstanceId = "11111111-2222-3333-4444-555555555555",
        ServerDisplayName = "Test Radio Vault Server",
        DatabaseSchemaVersion = 45,
        CapabilityGeneration = 8,
        Port = 0,
        AccessToken = primaryToken,
        LoopbackOnly = true,
        PairedDesktopClients = new[]
        {
            new WebPairedDesktopClient(
                "99999999-8888-7777-6666-555555555555",
                "Test client",
                pairedToken,
                DateTimeOffset.UtcNow)
        }
    });
    server.Start();
    var port = server.Port;
    try
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{WebApiRoutes.ServerInfo}");
        request.Headers.TryAddWithoutValidation("X-RadioVault-Token", pairedToken);
        using var response = client.Send(request);
        response.EnsureSuccessStatusCode();
        var body = response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>().GetAwaiter().GetResult();
        Equal("Test Radio Vault Server", body.GetProperty("server").GetProperty("displayName").GetString() ?? string.Empty);
    }
    finally
    {
        server.Stop();
    }
}

static void WebServerStopIsIdempotent()
{
    var token = "test-token-" + Guid.NewGuid().ToString("N");
    using var server = new LocalWebServer(new TestWebArchiveProvider(), new WebServerOptions { AppVersion = "test-web-version", Port = 0, AccessToken = token, LoopbackOnly = true });
    server.Start();
    server.Stop();
    server.Stop();
}

static void WebServerSurvivesRapidRestartGenerations()
{
    var token = "restart-token-" + Guid.NewGuid().ToString("N");
    using var server = new LocalWebServer(new TestWebArchiveProvider(), new WebServerOptions
    {
        AppVersion = "restart-test-version",
        Port = 0,
        AccessToken = token,
        LoopbackOnly = true
    });

    for (var generation = 0; generation < 8; generation++)
    {
        server.Start();
        var port = server.Port;
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
        var html = client.GetStringAsync(
            $"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(token)}").GetAwaiter().GetResult();
        True(html.Contains("THE RADIO VAULT", StringComparison.Ordinal));
        server.Stop();
    }

    var source = ReadWebServerSourceBundle();
    True(source.Contains("var startListener = _listener!;", StringComparison.Ordinal));
    True(source.Contains("AcceptLoopAsync(startListener", StringComparison.Ordinal));
    True(!source.Contains("AcceptLoopAsync(_listener!", StringComparison.Ordinal));
}

static void EmbeddedWebAssetsLoadFromAssembly()
{
    var serverType = typeof(LocalWebServer);
    var resources = serverType.Assembly.GetManifestResourceNames();
    foreach (var resourceName in new[]
             {
                 "TheRadioVault.Web.Assets.web-client.html",
                 "TheRadioVault.Web.Assets.service-worker.js",
                 "TheRadioVault.Web.Assets.secure-setup.html"
             })
    {
        True(resources.Contains(resourceName, StringComparer.Ordinal), $"Missing assembly resource {resourceName}.");
    }

    var flags = BindingFlags.NonPublic | BindingFlags.Static;
    var webClient = serverType.GetProperty("WebClientHtml", flags)?.GetValue(null) as string;
    var serviceWorker = serverType.GetProperty("ServiceWorkerJavaScript", flags)?.GetValue(null) as string;
    var secureSetup = serverType.GetProperty("SecureSetupHtml", flags)?.GetValue(null) as string;
    True(webClient?.Contains("<title>Radio Vault Web</title>", StringComparison.Ordinal) == true);
    True(serviceWorker?.Contains("radio-vault-anywhere-shell-v67", StringComparison.Ordinal) == true);
    True(secureSetup?.Contains("<title>Radio Vault secure setup</title>", StringComparison.Ordinal) == true);
}

static void WebApiSendsRemotePlaybackCommands()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.PostAsJsonAsync(
            $"http://127.0.0.1:{port}/api/v1/player/command?token={Uri.EscapeDataString(token)}",
            new { command = "pause", clientId = "test-client-0001", expectedRevision = 100L });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        True(body.GetProperty("result").GetProperty("changed").GetBoolean());
    });
}

static void WebApiExposesAuthoritativePlaybackSession()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var body = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"http://127.0.0.1:{port}/api/v1/player?token={Uri.EscapeDataString(token)}");
        var session = body.GetProperty("session");
        Equal("Server", session.GetProperty("ownerDevice").GetString());
        Equal("Server", session.GetProperty("player").GetProperty("device").GetString());
        True(session.TryGetProperty("generation", out _));
    });
}

static void WebApiAcknowledgesPhysicalSourceStop()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        string Route(string path)
            => $"http://127.0.0.1:{port}/api/v1{path}?token={Uri.EscapeDataString(token)}";

        using var begin = await client.PostAsJsonAsync(Route("/player/transfer/begin"), new
        {
            clientId = "phone-owner-01",
            deviceName = "Phone",
            deviceKind = "Phone",
            episodeId = 9,
            positionMs = 120_000L,
            durationMs = 3_600_000L,
            speed = 1d,
            desiredPlaying = true
        });
        begin.EnsureSuccessStatusCode();
        var beginBody = await begin.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var beginTransfer = beginBody.GetProperty("result").GetProperty("transfer");
        var transferId = beginTransfer.GetProperty("transferId").GetGuid();

        using var ready = await client.PostAsJsonAsync(Route("/player/transfer/ready"), new
        {
            clientId = "phone-owner-01",
            transferId,
            preparedPositionMs = beginTransfer.GetProperty("protectedPositionMs").GetInt64(),
            preparedDurationMs = 3_600_000L,
            decoderReady = true,
            desiredPlaying = true,
            overrideDesiredPlaying = false,
            speed = 1d
        });
        ready.EnsureSuccessStatusCode();
        var readyBody = await ready.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var readyTransfer = readyBody.GetProperty("result").GetProperty("transfer");

        using var commit = await client.PostAsJsonAsync(Route("/player/transfer/commit"), new
        {
            clientId = "phone-owner-01",
            transferId,
            readyRevision = readyTransfer.GetProperty("readyRevision").GetInt64(),
            preparedPositionMs = readyTransfer.GetProperty("commitPositionMs").GetInt64(),
            decoderRunningMuted = true
        });
        commit.EnsureSuccessStatusCode();
        var commitBody = await commit.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var receipt = commitBody.GetProperty("result").GetProperty("session").GetProperty("committedTransfer");
        True(!receipt.GetProperty("sourceStopAcknowledged").GetBoolean());
        var generation = receipt.GetProperty("generation").GetInt64();

        using var acknowledge = await client.PostAsJsonAsync(Route("/player/transfer/source-stopped"), new
        {
            clientId = "server",
            transferId,
            generation
        });
        acknowledge.EnsureSuccessStatusCode();
        var acknowledgeBody = await acknowledge.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        True(acknowledgeBody.GetProperty("result").GetProperty("session")
            .GetProperty("committedTransfer").GetProperty("sourceStopAcknowledged").GetBoolean());
    });
}

static async Task<long> ClaimPhoneViaTransaction(
    HttpClient client,
    int port,
    string token,
    string clientId,
    long positionMs,
    bool desiredPlaying)
{
    string Route(string path) => $"http://127.0.0.1:{port}/api/v1{path}?token={Uri.EscapeDataString(token)}";
    using var begin = await client.PostAsJsonAsync(Route("/player/transfer/begin"), new
    {
        clientId,
        deviceName = "Phone",
        deviceKind = "Phone",
        episodeId = 9,
        positionMs,
        durationMs = 3_600_000L,
        speed = 1d,
        desiredPlaying
    });
    begin.EnsureSuccessStatusCode();
    var beginBody = await begin.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
    var beginTransfer = beginBody.GetProperty("result").GetProperty("transfer");
    var transferId = beginTransfer.GetProperty("transferId").GetGuid();
    var protectedPosition = beginTransfer.GetProperty("protectedPositionMs").GetInt64();

    using var ready = await client.PostAsJsonAsync(Route("/player/transfer/ready"), new
    {
        clientId,
        transferId,
        preparedPositionMs = protectedPosition,
        preparedDurationMs = 3_600_000L,
        decoderReady = true,
        desiredPlaying,
        overrideDesiredPlaying = true,
        speed = 1d
    });
    ready.EnsureSuccessStatusCode();
    var readyBody = await ready.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
    var readyTransfer = readyBody.GetProperty("result").GetProperty("transfer");
    var commitPosition = readyTransfer.GetProperty("commitPositionMs").GetInt64();
    var readyRevision = readyTransfer.GetProperty("readyRevision").GetInt64();

    using var commit = await client.PostAsJsonAsync(Route("/player/transfer/commit"), new
    {
        clientId,
        transferId,
        readyRevision,
        preparedPositionMs = commitPosition,
        decoderRunningMuted = desiredPlaying
    });
    commit.EnsureSuccessStatusCode();
    var commitBody = await commit.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
    return commitBody.GetProperty("result").GetProperty("session").GetProperty("generation").GetInt64();
}

static void PausedPhoneRetainsPlaybackOwnership()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        // Routes already contain query parameters through auth in the real shell;
        // these tests add the token to each API path directly.
        string Route(string path) => $"http://127.0.0.1:{port}/api/v1{path}?token={Uri.EscapeDataString(token)}";

        using var begin = await client.PostAsJsonAsync(Route("/player/transfer/begin"), new
        {
            clientId = "phone-owner-01",
            deviceName = "Phone",
            deviceKind = "Phone",
            episodeId = 9,
            positionMs = 120_000L,
            durationMs = 3_600_000L,
            speed = 1.25d,
            desiredPlaying = false
        });
        begin.EnsureSuccessStatusCode();
        var beginBody = await begin.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var beginTransfer = beginBody.GetProperty("result").GetProperty("transfer");
        var transferId = beginTransfer.GetProperty("transferId").GetGuid();
        var protectedPosition = beginTransfer.GetProperty("protectedPositionMs").GetInt64();
        using var ready = await client.PostAsJsonAsync(Route("/player/transfer/ready"), new
        {
            clientId = "phone-owner-01",
            transferId,
            preparedPositionMs = protectedPosition,
            preparedDurationMs = 3_600_000L,
            decoderReady = true,
            desiredPlaying = false,
            overrideDesiredPlaying = true,
            speed = 1.25d
        });
        ready.EnsureSuccessStatusCode();
        var readyBody = await ready.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var readyTransfer = readyBody.GetProperty("result").GetProperty("transfer");
        using var commit = await client.PostAsJsonAsync(Route("/player/transfer/commit"), new
        {
            clientId = "phone-owner-01",
            transferId,
            readyRevision = readyTransfer.GetProperty("readyRevision").GetInt64(),
            preparedPositionMs = readyTransfer.GetProperty("commitPositionMs").GetInt64(),
            decoderRunningMuted = false
        });
        commit.EnsureSuccessStatusCode();
        var commitBody = await commit.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var generation = commitBody.GetProperty("result").GetProperty("session").GetProperty("generation").GetInt64();

        var endpoint = Route("/player/web-progress");
        using var pause = await client.PostAsJsonAsync(endpoint, new
        {
            clientId = "phone-owner-01",
            episodeId = 9,
            positionMs = 120_000L,
            durationMs = 3_600_000L,
            isPlaying = false,
            speed = 1.25d,
            completed = false,
            expectedGeneration = generation
        });
        pause.EnsureSuccessStatusCode();

        var body = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(Route("/player"));
        var session = body.GetProperty("session");
        Equal("Phone", session.GetProperty("ownerDevice").GetString());
        Equal("phone-owner-01", session.GetProperty("ownerClientId").GetString());
        True(!session.GetProperty("player").GetProperty("isPlaying").GetBoolean());
        Equal(120_000L, session.GetProperty("player").GetProperty("positionMs").GetInt64());

        using var resume = await client.PostAsJsonAsync(endpoint, new
        {
            clientId = "phone-owner-01",
            episodeId = 9,
            positionMs = 121_000L,
            durationMs = 3_600_000L,
            isPlaying = true,
            speed = 1.25d,
            completed = false,
            expectedGeneration = generation
        });
        resume.EnsureSuccessStatusCode();
    });
}

static void WebApiManagesQueue()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var add = await client.PostAsJsonAsync(
            $"http://127.0.0.1:{port}/api/v1/queue/add?token={Uri.EscapeDataString(token)}",
            new { episodeId = 9, playNext = false });
        add.EnsureSuccessStatusCode();
        var queue = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"http://127.0.0.1:{port}/api/v1/queue?token={Uri.EscapeDataString(token)}");
        True(queue.GetProperty("count").GetInt32() >= 1);
        using var clear = await client.PostAsync($"http://127.0.0.1:{port}/api/v1/queue/clear?token={Uri.EscapeDataString(token)}", null);
        clear.EnsureSuccessStatusCode();
    });
}

static void WebApiEditsMomentInPlace()
{
    Equal("/api/v1/moments/1/update", WebApiRoutes.MomentUpdate(1));
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.PostAsJsonAsync(
            $"http://127.0.0.1:{port}{WebApiRoutes.MomentUpdate(1)}?token={Uri.EscapeDataString(token)}",
            new WebMomentEditMutation("Edited title", "Edited notes"));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        True(body.GetProperty("result").GetProperty("changed").GetBoolean());
        Equal("Updated", body.GetProperty("result").GetProperty("message").GetString());
    });
}

static void WebApiSynchronisesOfflineProgress()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.PostAsJsonAsync(
            $"http://127.0.0.1:{port}/api/v1/broadcasts/9/offline-progress?token={Uri.EscapeDataString(token)}",
            new
            {
                clientId = "offline-client-01",
                episodeId = 9,
                positionMs = 240_000L,
                durationMs = 3_600_000L,
                completed = false,
                speed = 1.25d
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        True(body.GetProperty("result").GetProperty("changed").GetBoolean());
        Equal(240_000L, body.GetProperty("result").GetProperty("episode").GetProperty("positionMs").GetInt64());

        using var stale = await client.PostAsJsonAsync(
            $"http://127.0.0.1:{port}/api/v1/broadcasts/9/offline-progress?token={Uri.EscapeDataString(token)}",
            new
            {
                clientId = "offline-client-01",
                episodeId = 9,
                positionMs = 180_000L,
                durationMs = 3_600_000L,
                completed = false,
                speed = 1d
            });
        stale.EnsureSuccessStatusCode();
        var staleBody = await stale.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        True(!staleBody.GetProperty("result").GetProperty("changed").GetBoolean());
        Equal(240_000L, staleBody.GetProperty("result").GetProperty("episode").GetProperty("positionMs").GetInt64());
    });
}

static void WebApiPermitsExplicitLanProgressRewind()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var generation = await ClaimPhoneViaTransaction(
            client, port, token, "lan-desktop-client-01", 120_000L, true);
        var url = $"http://127.0.0.1:{port}/api/v1/broadcasts/9/offline-progress?token={Uri.EscapeDataString(token)}";
        using (var forward = await client.PostAsJsonAsync(url, new
               {
                   clientId = "lan-desktop-client-01",
                   episodeId = 9,
                   positionMs = 300_000L,
                   durationMs = 3_600_000L,
                   completed = false,
                   speed = 1d,
                   allowRewind = true,
                   expectedGeneration = generation
               }))
        {
            forward.EnsureSuccessStatusCode();
        }

        using var rewind = await client.PostAsJsonAsync(url, new
        {
            clientId = "lan-desktop-client-01",
            episodeId = 9,
            positionMs = 180_000L,
            durationMs = 3_600_000L,
            completed = false,
            speed = 1d,
            allowRewind = true,
            expectedGeneration = generation,
            explicitSeek = true
        });
        rewind.EnsureSuccessStatusCode();
        var body = await rewind.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        True(body.GetProperty("result").GetProperty("changed").GetBoolean());
        Equal(180_000L, body.GetProperty("result").GetProperty("episode").GetProperty("positionMs").GetInt64());
    });
}

static void SecureWebOptionsCarryCertificateMaterial()
{
    var options = new WebServerOptions
    {
        AppVersion = "0.26.0",
        Port = 8765,
        SecureAccessEnabled = true,
        SecurePort = 8766,
        AccessToken = "secure-test-token-000000000000",
        RootCertificateDer = new byte[] { 1, 2, 3 },
        MobileConfigurationProfile = new byte[] { 4, 5, 6 },
        RootCertificateThumbprint = "ABCDEF"
    };
    Equal("0.26.0", options.AppVersion);
    True(options.SecureAccessEnabled);
    Equal(8766, options.SecurePort);
    Equal(3, options.RootCertificateDer.Length);
    Equal("ABCDEF", options.RootCertificateThumbprint);
}


static void ClientTranscriptionQueueReturnsJobId()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        client.DefaultRequestHeaders.Add("X-RadioVault-Token", token);
        using var response = await client.PostAsJsonAsync(
            $"http://127.0.0.1:{port}{WebApiRoutes.ClientTranscriptionOperation("queue")}",
            new { episodeId = 9L, options = new { } });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Equal(TestWebArchiveProvider.JobId.ToString("D"), payload.GetProperty("value").GetString());
    });
}
}
