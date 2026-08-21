using System.Reflection;
using TheRadioVault.Web.Services;
using TheRadioVault.Web.Tests.Fixtures;
using static TheRadioVault.Web.Tests.Fixtures.WebServerFixture;
using static TheRadioVault.Web.Tests.TestAssert;

namespace TheRadioVault.Web.Tests;

internal static class WebShellContractTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases { get; } =
    [
        ("Web player is Radio Vault branded", WebPlayerIsRadioVaultBranded),
        ("Web shell reports configured app version", WebShellReportsConfiguredAppVersion),
        ("Web client uses unified playback ownership", WebClientUsesUnifiedPlaybackOwnership),
        ("Web client includes manual offline downloads", WebClientIncludesManualOfflineDownloads),
        ("Anywhere exposes the server transcription workspace", AnywhereExposesServerTranscriptionWorkspace),
        ("Anywhere shell matches native navigation and player structure", AnywhereShellMatchesNativeStructure),
        ("Anywhere dashboard player and handoff match the native contract", AnywhereDashboardPlayerAndHandoffMatchNativeContract),
        ("Web download button has one click path", WebDownloadButtonHasOneClickPath),
        ("Web client registers secure offline shell", WebClientRegistersSecureOfflineShell),
        ("Web client includes final recovery and accessibility", WebClientIncludesFinalRecoveryAndAccessibility),
        ("Web client keeps Live Radio outside personal playback", WebClientKeepsLiveRadioOutsidePersonalPlayback),
        ("Web client caches downloaded artwork", WebClientCachesDownloadedArtwork),
        ("Web Explore prose follows typed entity links", WebExploreProseFollowsTypedEntityLinks),
    ];

static void WebExploreProseFollowsTypedEntityLinks()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var html = await client.GetStringAsync($"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(token)}");
        True(html.Contains("wikiCurrentPage?.entityLinks", StringComparison.Ordinal));
        True(html.Contains(".toLowerCase().startsWith(\"inline\")", StringComparison.Ordinal));
        True(html.Contains("data-info=", StringComparison.Ordinal));
        True(html.Contains("data-wiki-page=", StringComparison.Ordinal));
        True(html.Contains("data-wiki-entity=", StringComparison.Ordinal));
    });
}

static void WebPlayerIsRadioVaultBranded()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var html = await client.GetStringAsync($"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(token)}");
        True(html.Contains("rvMiniPlayer", StringComparison.Ordinal));
        True(html.Contains("data-section=\"dashboard\"", StringComparison.Ordinal));
        True(html.Contains("data-section=\"library\"", StringComparison.Ordinal));
        True(html.Contains("id=\"playerContextTitle\"", StringComparison.Ordinal));
        True(html.Contains("Now Playing</strong", StringComparison.Ordinal));
        True(html.Contains("class=\"rvMiniPlayer visible idle\"", StringComparison.Ordinal));
        True(html.Contains("miniPlayer.classList.add(\"visible\")", StringComparison.Ordinal));
        True(html.Contains("buildOfflineDashboard", StringComparison.Ordinal));
        True(html.Contains("Offline on this phone", StringComparison.Ordinal));
        True(html.Contains("inactiveOutputIsActive", StringComparison.Ordinal));
        True(html.Contains("thisPhoneOwnsSession", StringComparison.Ordinal));
        True(html.Contains("transferPhone", StringComparison.Ordinal));
        True(html.Contains("playerSeek.disabled = !has || inactive || phoneTransferInProgress", StringComparison.Ordinal));
        True(html.Contains("playerSpeed.disabled = !has || inactive || phoneTransferInProgress", StringComparison.Ordinal));
        True(html.Contains("class=\"rvSkipIcon\"", StringComparison.Ordinal));
        True(html.Contains("M8 1L4 4L8 7", StringComparison.Ordinal));
        True(html.Contains("M16 1L20 4L16 7", StringComparison.Ordinal));
        True(html.Contains("M4 10V7Q4 4 7 4H20M16 1L20 4L16 7M20 14V17Q20 20 17 20H4M8 17L4 20L8 23", StringComparison.Ordinal));
        True(html.Contains("$(\"playerBack\").addEventListener(\"click\", () => skip(-15))", StringComparison.Ordinal));
        True(!html.Contains("Pause playback on PC", StringComparison.Ordinal));
        True(!html.Contains("sendDesktop(\"seek\"", StringComparison.Ordinal));
        True(!html.Contains("sendDesktop(\"speed\"", StringComparison.Ordinal));
        True(!html.Contains("Play on PC", StringComparison.Ordinal));
        True(!html.Contains("Continue on Desktop", StringComparison.Ordinal));
        True(!html.Contains("Continue on Phone", StringComparison.Ordinal));
        True(html.Contains("<audio id=\"audio\" preload=\"metadata\" playsinline>", StringComparison.Ordinal));
        True(!html.Contains("<audio id=\"audio\" controls", StringComparison.Ordinal));
    });
}

static void WebShellReportsConfiguredAppVersion()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var html = await client.GetStringAsync($"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(token)}");
        True(html.Contains("Version: test-web-version", StringComparison.Ordinal));
        True(!html.Contains("__APP_VERSION__", StringComparison.Ordinal));
    });
}

static void WebClientUsesUnifiedPlaybackOwnership()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var html = await client.GetStringAsync($"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(token)}");
        True(html.Contains("/player/transfer/", StringComparison.Ordinal));
        True(html.Contains("prepareCanonicalAudio", StringComparison.Ordinal));
        True(html.Contains("decoderRunningMuted", StringComparison.Ordinal));
        True(html.Contains("decoderRunningAudibly", StringComparison.Ordinal));
        True(html.Contains("/media-start?positionMs=", StringComparison.Ordinal));
        var directStart = html.IndexOf("assignCanonicalGestureStartSource(id, shared.positionMs)", StringComparison.Ordinal);
        var directPlay = html.IndexOf("gesturePrime = audio.play()", directStart, StringComparison.Ordinal);
        True(directStart >= 0 && directPlay > directStart);
        True(html.Contains("waitForCommittedSourceStop", StringComparison.Ordinal));
        True(html.Contains("assertCommittedPhoneOwnership", StringComparison.Ordinal));
        True(html.Contains("playbackOwnershipMoved", StringComparison.Ordinal));
        True(html.Contains("sourceStopAcknowledgementInFlight", StringComparison.Ordinal));
        True(html.Contains("desiredPlayingOverride = true", StringComparison.Ordinal));
        True(html.Contains("ensurePhoneOutputState", StringComparison.Ordinal));
        True(html.Contains("playerPollInFlight", StringComparison.Ordinal));
        True(html.Contains("changePollInFlight", StringComparison.Ordinal));
        True(html.Contains("currentLogicalPositionMs", StringComparison.Ordinal));
        True(html.Contains("expectedGeneration", StringComparison.Ordinal));
        True(html.Contains("allowRewind", StringComparison.Ordinal));
        True(html.Contains("phoneTransferInProgress", StringComparison.Ordinal));
        True(html.Contains("spinner", StringComparison.Ordinal));
        True(html.Contains("transferPhone", StringComparison.Ordinal));
        True(html.Contains("Move to this device", StringComparison.Ordinal));
        True(html.Contains("/media-manifest", StringComparison.Ordinal));
        True(html.Contains("/media/", StringComparison.Ordinal));
        True(!html.Contains("claim-phone", StringComparison.Ordinal));
        True(!html.Contains("const ownershipAccepted = await syncWebPlayback(true)", StringComparison.Ordinal));
        True(!html.Contains("sendDesktop(\"seek\"", StringComparison.Ordinal));
        True(!html.Contains("sendDesktop(\"speed\"", StringComparison.Ordinal));
        True(!html.Contains("Continue on this device", StringComparison.OrdinalIgnoreCase));
    });
}

static void WebClientIncludesManualOfflineDownloads()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var html = await client.GetStringAsync($"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(token)}");
        True(html.Contains("data-section=\"downloaded\"", StringComparison.Ordinal));
        True(html.Contains("playerDownload", StringComparison.Ordinal));
        True(html.Contains("indexedDB.open", StringComparison.Ordinal));
        True(html.Contains("AbortController", StringComparison.Ordinal));
        True(html.Contains("offline-progress", StringComparison.Ordinal));
        True(!html.Contains("background download", StringComparison.OrdinalIgnoreCase));
    });
}

static void AnywhereExposesServerTranscriptionWorkspace()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var html = await client.GetStringAsync($"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(token)}");
        True(!html.Contains("data-section=\"transcripts\"", StringComparison.Ordinal));
        True(html.Contains("[\"transcription\",\"Transcription studio\"]", StringComparison.Ordinal));
        True(html.Contains("--transcript: #43c7bd", StringComparison.Ordinal));
        True(html.Contains("clientPost(\"transcription\", \"jobs\"", StringComparison.Ordinal));
        True(html.Contains("data-transcription-action=\"pause\"", StringComparison.Ordinal));
        True(html.Contains("data-transcription-action=\"resume\"", StringComparison.Ordinal));
        True(html.Contains("data-transcription-action=\"cancel\"", StringComparison.Ordinal));
        True(html.Contains("data-transcription-action=\"retry\"", StringComparison.Ordinal));
        True(html.Contains("data-transcript-export=\"txt\"", StringComparison.Ordinal));
        True(html.Contains("data-transcript-export=\"srt\"", StringComparison.Ordinal));
        True(html.Contains("data-transcript-export=\"vtt\"", StringComparison.Ordinal));
        True(html.Contains("data-transcribe-full", StringComparison.Ordinal));
        True(html.Contains("data-transcribe-sample", StringComparison.Ordinal));
    });
}

static void AnywhereShellMatchesNativeStructure()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var html = await client.GetStringAsync($"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(token)}");
        True(html.Contains("THE RADIO VAULT", StringComparison.Ordinal));
        True(html.Contains("data-nav-search", StringComparison.Ordinal));
        True(html.Contains("data-nav-favourites", StringComparison.Ordinal));
        True(html.Contains("data-section=\"moments\"", StringComparison.Ordinal));
        True(html.Contains("data-section=\"research\"", StringComparison.Ordinal));
        True(html.Contains("data-section=\"settings\"", StringComparison.Ordinal));
        True(!html.Contains("data-nav-upcoming", StringComparison.Ordinal));
        True(!html.Contains(".sidebarShows,.navDivider,.navSoon,.momentTab,.researchTab,.settingsTab", StringComparison.Ordinal));
        True(!html.Contains("â€¢", StringComparison.Ordinal));
        True(!html.Contains("Â·", StringComparison.Ordinal));
        True(!html.Contains("â€¦", StringComparison.Ordinal));
        True(html.Contains("Knowledge database", StringComparison.Ordinal));
        True(html.Contains("Export complete knowledge database", StringComparison.Ordinal));
        True(html.Contains("Export broadcasts without dates", StringComparison.Ordinal));
        True(html.Contains("Export broadcasts missing topics or summaries", StringComparison.Ordinal));
        True(html.Contains("loadMoments", StringComparison.Ordinal));
        True(html.Contains("loadResearch", StringComparison.Ordinal));
        True(html.Contains("loadSettings", StringComparison.Ordinal));
        True(html.Contains("data-edit-metadata", StringComparison.Ordinal));
        True(html.Contains("id=\"sidebarShows\"", StringComparison.Ordinal));
        True(html.Contains("width:224px", StringComparison.Ordinal));
        True(html.Contains("height:110px", StringComparison.Ordinal));
        True(html.Contains("id=\"miniSeek\"", StringComparison.Ordinal));
        True(html.Contains("id=\"miniBack\"", StringComparison.Ordinal));
        True(html.Contains("id=\"miniForward\"", StringComparison.Ordinal));
        True(html.Contains("id=\"miniInfo\"", StringComparison.Ordinal));
        True(html.Contains("id=\"miniSpeed\"", StringComparison.Ordinal));
        True(html.Contains("after(libraryTools)", StringComparison.Ordinal));
        True(html.Contains("setAttribute(\"aria-label\"", StringComparison.Ordinal));
    });
}

static void AnywhereDashboardPlayerAndHandoffMatchNativeContract()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var html = await client.GetStringAsync($"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(token)}");
        True(html.Contains("nativeDashboardTop", StringComparison.Ordinal));
        True(html.Contains("dashboardContinue", StringComparison.Ordinal));
        True(html.Contains("Unheard broadcasts", StringComparison.Ordinal));
        True(html.Contains("bootstrapState.unheard", StringComparison.Ordinal));
        True(html.Contains("grid-template-columns:330px minmax(300px,1fr) 370px", StringComparison.Ordinal));
        True(html.Contains("id=\"miniFavourite\"", StringComparison.Ordinal));
        True(html.Contains("id=\"miniMoment\"", StringComparison.Ordinal));
        True(html.Contains("id=\"miniVolume\"", StringComparison.Ordinal));
        True(html.Contains("savedVolumeValue === null ? 1", StringComparison.Ordinal));
        True(html.Contains("heartbeatInterval = audio.paused ? 5000 : 1000", StringComparison.Ordinal));
        True(html.Contains("Waiting for the existing dormant decoder preparation", StringComparison.Ordinal));
        True(html.Contains("Priming can advance or re-seek the media element", StringComparison.Ordinal));
        True(html.Contains("if (phoneTransferInProgress) return;", StringComparison.Ordinal));
    });
}

static void WebDownloadButtonHasOneClickPath()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var html = await client.GetStringAsync($"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(token)}");
        True(html.Contains("e.target.closest(\"[data-download]\")", StringComparison.Ordinal));
        True(!html.Contains("playerDownload.addEventListener('click'", StringComparison.Ordinal));
    });
}

static void WebClientRegistersSecureOfflineShell()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var html = await client.GetStringAsync($"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(token)}");
        True(html.Contains("navigator.serviceWorker.register", StringComparison.Ordinal));
        True(html.Contains("CACHE_SHELL", StringComparison.Ordinal));
        True(html.Contains("service-worker.js", StringComparison.Ordinal));
    });
}

static void WebClientIncludesFinalRecoveryAndAccessibility()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var html = await client.GetStringAsync($"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(token)}");
        True(html.Contains("Skip to content", StringComparison.Ordinal));
        True(html.Contains("id=\"mainContent\"", StringComparison.Ordinal));
        True(html.Contains("appUpdateBanner", StringComparison.Ordinal));
        True(html.Contains("repairAppShell", StringComparison.Ordinal));
        True(html.Contains("Downloaded audio, artwork, listening progress and pending sync changes will be preserved", StringComparison.Ordinal));
        True(html.Contains("aria-live=\"polite\"", StringComparison.Ordinal));
        True(html.Contains("radio-vault-anywhere-shell-v68", StringComparison.Ordinal));
        True(html.Contains("radio-vault-anywhere-audio-v1", StringComparison.Ordinal));
        True(html.Contains("radio-vault-anywhere-artwork-v1", StringComparison.Ordinal));
    });
}

static void WebClientCachesDownloadedArtwork()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var html = await client.GetStringAsync($"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(token)}");
        True(html.Contains("/__offline_artwork__/", StringComparison.Ordinal));
        True(html.Contains("radio-vault-anywhere-artwork-v1", StringComparison.Ordinal));
        True(html.Contains("repairDownloadedArtwork", StringComparison.Ordinal));
    });
}

static void WebClientKeepsLiveRadioOutsidePersonalPlayback()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var html = await client.GetStringAsync($"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(token)}");
        True(html.Contains("data-section=\"live-radio\"", StringComparison.Ordinal));
        True(html.Contains("<audio id=\"liveAudio\"", StringComparison.Ordinal));
        True(html.Contains("/client/live-radio", StringComparison.Ordinal));
        True(html.Contains("liveRadioTunedIn", StringComparison.Ordinal));
        True(html.Contains("Save This Moment", StringComparison.Ordinal));
        True(html.Contains("never changes played status, progress, Continue Listening, play counts, your queue or playback handoff", StringComparison.Ordinal));
        True(html.Contains("setActionHandler(action, null)", StringComparison.Ordinal));
        True(html.Contains("Saved from Radio Vault Live", StringComparison.Ordinal));
    });
}

}
