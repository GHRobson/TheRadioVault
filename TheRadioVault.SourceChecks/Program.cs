var checks = new (string Name, Action Run)[]
{
    ("Web playback and queue routes stay behind one server boundary", WebPlaybackAndQueueRoutesStayBehindOneServerBoundary),
    ("Federation administration routes stay behind one server boundary", FederationAdministrationRoutesStayBehindOneServerBoundary),
    ("General Web API dispatch stays declarative and centralised", GeneralWebApiDispatchStaysDeclarativeAndCentralised),
    ("Web assets, client routes and media routes stay behind focused boundaries", WebClientAndMediaBoundariesRemainExtracted),
    ("Desktop Saved and transport controls match native icon parity", DesktopSavedAndTransportControlsMatchNativeParity),
    ("Knowledge imports retain resumable background-job surfaces", KnowledgeImportsRetainResumableBackgroundJobSurfaces),
    ("Web handoff preserves an aligned Safari decoder", WebHandoffPreservesAlignedSafariDecoder),
    ("iPhone broadcast switches replace stale decoders in the tap", IphoneBroadcastSwitchesReplaceStaleDecoderInTap),
    ("iPhone positioned failures preserve canonical gesture fallback", IphonePositionedFailuresPreserveCanonicalGestureFallback),
    ("Repeated iPhone handoffs bypass dormant decoder gating", RepeatedIphoneHandoffsBypassDormantDecoderGating),
    ("Canonical audio ranges are cache-combinable", CanonicalAudioRangesAreCacheCombinable)
};

var selectedChecks = args.Length == 0
    ? checks
    : checks.Where(check => args.Any(filter => check.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
if (selectedChecks.Length == 0)
{
    Console.Error.WriteLine("No source checks matched the supplied filters.");
    return 2;
}

var failures = new List<string>();
foreach (var check in selectedChecks)
{
    try
    {
        check.Run();
        Console.WriteLine($"PASS  {check.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{check.Name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL  {check.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{selectedChecks.Length - failures.Count}/{selectedChecks.Length} source checks passed.");
return failures.Count == 0 ? 0 : 1;

static void WebPlaybackAndQueueRoutesStayBehindOneServerBoundary()
{
    var services = Path.Combine(SourceRoot(), "TheRadioVault.Web", "Services");
    var coordinator = File.ReadAllText(Path.Combine(services, "LocalWebServer.cs"));
    var dispatcher = File.ReadAllText(Path.Combine(services, "LocalWebServer.ApiRoutes.cs"));
    var playbackQueue = File.ReadAllText(Path.Combine(services, "LocalWebServer.PlaybackQueue.cs"));

    True(dispatcher.Contains("TryHandlePlaybackQueueRouteAsync(", StringComparison.Ordinal));
    True(!coordinator.Contains("TryHandlePlaybackQueueRouteAsync(", StringComparison.Ordinal));
    True(!coordinator.Contains("private async Task HandlePlayerApiAsync(", StringComparison.Ordinal));
    True(!coordinator.Contains("private async Task HandleQueueApiAsync(", StringComparison.Ordinal));
    True(playbackQueue.Contains("private async Task<bool> TryHandlePlaybackQueueRouteAsync(", StringComparison.Ordinal));

    var routeMarkers = new[]
    {
        "WebApiRoutes.PlayerTransferBegin",
        "WebApiRoutes.PlayerTransferReady",
        "WebApiRoutes.PlayerTransferCommit",
        "WebApiRoutes.PlayerTransferCancel",
        "WebApiRoutes.PlayerTransferSourceStopped",
        "WebApiRoutes.PlayerCommand",
        "WebApiRoutes.PlayerWebProgress",
        "WebApiRoutes.Player,",
        "WebApiRoutes.QueueAdd",
        "WebApiRoutes.QueueClear",
        "TryMatchQueueAction(path, \"remove\"",
        "TryMatchQueueAction(path, \"move\"",
        "WebApiRoutes.Queue,"
    };
    var priorIndex = -1;
    foreach (var marker in routeMarkers)
    {
        var index = playbackQueue.IndexOf(marker, StringComparison.Ordinal);
        True(index > priorIndex, $"Playback/queue route order changed at {marker}.");
        priorIndex = index;
    }
}

static void FederationAdministrationRoutesStayBehindOneServerBoundary()
{
    var services = Path.Combine(SourceRoot(), "TheRadioVault.Web", "Services");
    var coordinator = File.ReadAllText(Path.Combine(services, "LocalWebServer.cs"));
    var dispatcher = File.ReadAllText(Path.Combine(services, "LocalWebServer.ApiRoutes.cs"));
    var federationAdministration = File.ReadAllText(Path.Combine(services, "LocalWebServer.FederationAdministration.cs"));

    const string boundaryCall = "TryHandleFederationAdministrationRouteAsync(";
    var pairingIndex = coordinator.IndexOf("WebApiRoutes.FederationPair", StringComparison.Ordinal);
    var authorizedBoundaryIndex = coordinator.IndexOf("TryHandleAuthorizedRouteAsync(", pairingIndex, StringComparison.Ordinal);
    var boundaryIndex = dispatcher.IndexOf(boundaryCall, StringComparison.Ordinal);
    var clientBoundaryIndex = dispatcher.IndexOf("TryHandleClientRouteAsync(", boundaryIndex, StringComparison.Ordinal);

    True(pairingIndex >= 0);
    True(authorizedBoundaryIndex > pairingIndex);
    True(boundaryIndex >= 0);
    True(clientBoundaryIndex > boundaryIndex);
    var routeWindow = dispatcher[boundaryIndex..clientBoundaryIndex];
    Equal(1, dispatcher.Split(boundaryCall, StringSplitOptions.None).Length - 1);
    Equal(1, coordinator.Split("TryHandleAuthorizedRouteAsync(", StringSplitOptions.None).Length - 1);
    True(federationAdministration.Contains("private async Task<bool> TryHandleFederationAdministrationRouteAsync(", StringComparison.Ordinal));

    var routeMarkers = new[]
    {
        "WebApiRoutes.FederationStatus",
        "WebApiRoutes.FederationBootstrap",
        "WebApiRoutes.FederationLibrarySync",
        "WebApiRoutes.FederationLibraryScan",
        "WebApiRoutes.FederationParity",
        "WebApiRoutes.FederationSettings",
        "WebApiRoutes.FederationPlaybackPreferences",
        "WebApiRoutes.FederationResearchWorkspace",
        "WebApiRoutes.FederationResearchUndated",
        "TryMatchFederationResearchCoverageByShow(path",
        "TryMatchFederationResearchCoverage(path",
        "TryMatchFederationResearchUndatedDate(path",
        "TryMatchFederationResearchWorkspaceRecord(path",
        "WebApiRoutes.FederationResearchImportPreview",
        "WebApiRoutes.FederationResearchImportApply",
        "WebApiRoutes.FederationResearchImportStatus",
        "WebApiRoutes.FederationResearchImportCancel",
        "WebApiRoutes.FederationResearchExport",
        "WebApiRoutes.FederationWikiImportPreview",
        "WebApiRoutes.FederationWikiImportApply",
        "WebApiRoutes.FederationWikiExport"
    };

    var priorIndex = -1;
    foreach (var marker in routeMarkers)
    {
        True(!routeWindow.Contains(marker, StringComparison.Ordinal));
        var markerIndex = federationAdministration.IndexOf(marker, StringComparison.Ordinal);
        True(markerIndex > priorIndex);
        priorIndex = markerIndex;
    }
}

static void GeneralWebApiDispatchStaysDeclarativeAndCentralised()
{
    var services = Path.Combine(SourceRoot(), "TheRadioVault.Web", "Services");
    var coordinator = File.ReadAllText(Path.Combine(services, "LocalWebServer.cs"));
    var dispatcher = File.ReadAllText(Path.Combine(services, "LocalWebServer.ApiRoutes.cs"));
    var resolver = File.ReadAllText(Path.Combine(services, "WebApiRouteResolver.cs"));

    Equal(1, coordinator.Split("TryHandleAuthorizedRouteAsync(", StringSplitOptions.None).Length - 1);
    True(!coordinator.Contains("TryMatchBroadcastAction", StringComparison.Ordinal));
    True(!coordinator.Contains("TryMatchMomentDelete", StringComparison.Ordinal));
    True(!coordinator.Contains("TryMatchMomentUpdate", StringComparison.Ordinal));
    True(!coordinator.Contains("TryMatchJobCancel", StringComparison.Ordinal));
    True(dispatcher.Contains("WebApiRouteResolver.TryResolve(path", StringComparison.Ordinal));
    True(dispatcher.Contains("DispatchGeneralApiRouteAsync(", StringComparison.Ordinal));
    True(dispatcher.Contains("if (!route.Allows(method))", StringComparison.Ordinal));

    var routeKinds = new[]
    {
        "WebApiRouteKind.ServerInfo",
        "WebApiRouteKind.Episodes",
        "WebApiRouteKind.Shows",
        "WebApiRouteKind.Search",
        "WebApiRouteKind.Favourites",
        "WebApiRouteKind.Events",
        "WebApiRouteKind.Jobs",
        "WebApiRouteKind.JobCancel",
        "WebApiRouteKind.OfflineProgress",
        "WebApiRouteKind.FavouriteMutation",
        "WebApiRouteKind.ListeningStatusMutation",
        "WebApiRouteKind.MetadataMutation",
        "WebApiRouteKind.Transcripts",
        "WebApiRouteKind.Transcript",
        "WebApiRouteKind.MomentCreate",
        "WebApiRouteKind.MomentDelete",
        "WebApiRouteKind.MomentUpdate",
        "WebApiRouteKind.BroadcastDetails",
        "WebApiRouteKind.Research",
        "WebApiRouteKind.ArchiveHealth",
        "WebApiRouteKind.Moments"
    };
    foreach (var routeKind in routeKinds)
    {
        True(resolver.Contains(routeKind, StringComparison.Ordinal), $"Resolver is missing {routeKind}.");
        True(dispatcher.Contains("case " + routeKind + ":", StringComparison.Ordinal), $"Dispatcher is missing {routeKind}.");
    }
}

static void WebClientAndMediaBoundariesRemainExtracted()
{
    var root = SourceRoot();
    var web = Path.Combine(root, "TheRadioVault.Web");
    var services = Path.Combine(web, "Services");
    var assets = Path.Combine(web, "Assets");
    var coordinator = File.ReadAllText(Path.Combine(services, "LocalWebServer.cs"));
    var dispatcher = File.ReadAllText(Path.Combine(services, "LocalWebServer.ApiRoutes.cs"));
    var resolver = File.ReadAllText(Path.Combine(services, "WebApiRouteResolver.cs"));
    var clientRoutes = File.ReadAllText(Path.Combine(services, "LocalWebServer.ClientRoutes.cs"));
    var mediaRoutes = File.ReadAllText(Path.Combine(services, "LocalWebServer.Media.cs"));
    var webAssets = File.ReadAllText(Path.Combine(services, "LocalWebServer.WebAssets.cs"));
    var project = File.ReadAllText(Path.Combine(web, "TheRadioVault.Web.csproj"));

    Equal(1, dispatcher.Split("TryHandleClientRouteAsync(", StringSplitOptions.None).Length - 1);
    Equal(1, dispatcher.Split("TryHandleCanonicalMediaRouteAsync(", StringSplitOptions.None).Length - 1);
    Equal(1, dispatcher.Split("TryHandleArtworkAudioRouteAsync(", StringSplitOptions.None).Length - 1);
    var clientBoundaryIndex = dispatcher.IndexOf("TryHandleClientRouteAsync(", StringComparison.Ordinal);
    var generalBoundaryIndex = dispatcher.IndexOf("if (hasGeneralRoute)", clientBoundaryIndex, StringComparison.Ordinal);
    var canonicalMediaBoundaryIndex = dispatcher.IndexOf("TryHandleCanonicalMediaRouteAsync(", StringComparison.Ordinal);
    var playbackBoundaryIndex = dispatcher.IndexOf("TryHandlePlaybackQueueRouteAsync(", canonicalMediaBoundaryIndex, StringComparison.Ordinal);
    var artworkAudioBoundaryIndex = dispatcher.IndexOf("TryHandleArtworkAudioRouteAsync(", playbackBoundaryIndex, StringComparison.Ordinal);
    True(clientBoundaryIndex >= 0 && generalBoundaryIndex > clientBoundaryIndex);
    True(canonicalMediaBoundaryIndex > generalBoundaryIndex && playbackBoundaryIndex > canonicalMediaBoundaryIndex);
    True(artworkAudioBoundaryIndex > playbackBoundaryIndex);
    True(resolver.Contains("WebApiRoutes.Broadcasts", StringComparison.Ordinal));
    True(!coordinator.Contains("private const string WebClientHtml", StringComparison.Ordinal));
    True(!coordinator.Contains("private const string ServiceWorkerJavaScript", StringComparison.Ordinal));
    True(!coordinator.Contains("TryMatchClientOperation(uri.AbsolutePath", StringComparison.Ordinal));
    True(!coordinator.Contains("private async Task HandleArtworkAsync", StringComparison.Ordinal));
    True(!coordinator.Contains("private async Task StreamAudioFileAsync", StringComparison.Ordinal));

    var clientMarkers = new[]
    {
        "WebApiRoutes.Bootstrap",
        "WebApiRoutes.ClientResearch",
        "WebApiRoutes.ClientTranscripts",
        "WebApiRoutes.ClientSpeakers",
        "WebApiRoutes.ClientTranscription",
        "WebApiRoutes.ClientWiki",
        "WebApiRoutes.ClientLibraryOverview",
        "WebApiRoutes.ClientLibraryBrowse",
        "WebApiRoutes.ClientLibraryArchivePeriods",
        "WebApiRoutes.ClientLibrarySearchFacets",
        "WebApiRoutes.ClientLibrarySearchSuggestions",
        "TryMatchClientLibraryBroadcast(path",
        "TryMatchClientBroadcast(path"
    };
    AssertMarkersRemainOrdered(clientRoutes, clientMarkers, "client route");

    var canonicalMediaMarkers = new[]
    {
        "TryMatchCanonicalMediaManifest(path",
        "TryMatchCanonicalMediaStart(path",
        "TryMatchCanonicalMediaPart(path"
    };
    AssertMarkersRemainOrdered(mediaRoutes, canonicalMediaMarkers, "canonical media route");
    var artworkAudioMarkers = new[]
    {
        "path.StartsWith(\"/artwork/\"",
        "path.StartsWith(\"/audio/\""
    };
    AssertMarkersRemainOrdered(mediaRoutes, artworkAudioMarkers, "artwork/audio route");

    foreach (var assetName in new[] { "web-client.html", "service-worker.js", "secure-setup.html" })
    {
        True(File.Exists(Path.Combine(assets, assetName)), $"Missing embedded web asset {assetName}.");
        True(webAssets.Contains($"TheRadioVault.Web.Assets.{assetName}", StringComparison.Ordinal));
    }
    True(project.Contains("<EmbeddedResource Include=\"Assets\\*.html\" />", StringComparison.Ordinal));
    True(project.Contains("<EmbeddedResource Include=\"Assets\\*.js\" />", StringComparison.Ordinal));
    True(File.ReadAllText(Path.Combine(assets, "web-client.html")).Contains("<title>Radio Vault Web</title>", StringComparison.Ordinal));
    True(File.ReadAllText(Path.Combine(assets, "service-worker.js")).Contains("radio-vault-anywhere-shell-v68", StringComparison.Ordinal));
}

static void DesktopSavedAndTransportControlsMatchNativeParity()
{
    var navigation = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "MainWindowViewModel.cs"));
    True(navigation.Contains("new ShellNavigationItemViewModel(\"saved\", \"Saved\"", StringComparison.Ordinal));
    True(!navigation.Contains("new ShellNavigationItemViewModel(\"favourites\", \"Favourites\"", StringComparison.Ordinal));
    True(!navigation.Contains("new ShellNavigationItemViewModel(\"moments\", \"Moments\"", StringComparison.Ordinal));
    True(navigation.Contains("Saved.SelectSectionAsync", StringComparison.Ordinal));

    var savedView = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "SavedView.axaml"));
    True(savedView.Contains("ShowFavouritesCommand", StringComparison.Ordinal));
    True(savedView.Contains("ShowMomentsCommand", StringComparison.Ordinal));
    True(savedView.Contains("RvStatFavouriteBrush", StringComparison.Ordinal));
    True(savedView.Contains("RvMomentBrush", StringComparison.Ordinal));

    var theme = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "App.axaml"));
    True(theme.Contains("SkipBackIconTemplate", StringComparison.Ordinal));
    True(theme.Contains("SkipForwardIconTemplate", StringComparison.Ordinal));
    True(theme.Contains("PrimaryTransportIconTemplate", StringComparison.Ordinal));
    True(theme.Contains("M8,1 L4,4 L8,7", StringComparison.Ordinal));
    True(theme.Contains("M16,1 L20,4 L16,7", StringComparison.Ordinal));
    True(theme.Contains("ShowPlayIcon", StringComparison.Ordinal));
    True(theme.Contains("ShowPauseIcon", StringComparison.Ordinal));

    var shell = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "MainWindow.axaml"));
    True(shell.Contains("SkipBackIconTemplate", StringComparison.Ordinal));
    True(shell.Contains("SkipForwardIconTemplate", StringComparison.Ordinal));
    True(shell.Contains("PrimaryTransportIconTemplate", StringComparison.Ordinal));
    True(!shell.Contains("Text=\"{Binding Playback.PlayPauseGlyph}\"", StringComparison.Ordinal));

    var playback = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "PlaybackViewModel.cs"));
    True(playback.Contains("PlaybackTransferAlignmentToleranceMs = 3_000", StringComparison.Ordinal));
    True(playback.Contains("<= PlaybackTransferAlignmentToleranceMs", StringComparison.Ordinal));
    True(playback.Contains("AlignPreparedDecoderAsync", StringComparison.Ordinal));
    True(playback.Contains("canSeekPreparedDecoder", StringComparison.Ordinal));
    True(playback.Contains("WaitForPreparedDecoderAsync", StringComparison.Ordinal));
    True(playback.Contains("PlaybackTransferDecoderReadyTimeout", StringComparison.Ordinal));
    True(playback.Contains("second stable sample", StringComparison.Ordinal));
    True(playback.Contains("handoffStage = \"commit-transfer\"", StringComparison.Ordinal));
    True(playback.Contains("[\"stage\"] = handoffStage", StringComparison.Ordinal));
    True(playback.Contains("DesktopPlaybackStateMachine _stateMachine", StringComparison.Ordinal));
    True(playback.Contains("RemotePlaybackProgressInterpolator _remoteProgressInterpolator", StringComparison.Ordinal));
    True(!playback.Contains("private bool _transportPending", StringComparison.Ordinal));
    True(!playback.Contains("private long _remoteProjectionPositionMs", StringComparison.Ordinal));

    var stateMachine = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Application", "Services", "DesktopPlaybackStateMachine.cs"));
    True(stateMachine.Contains("public sealed class DesktopPlaybackStateMachine", StringComparison.Ordinal));
    True(stateMachine.Contains("public void BeginTransport", StringComparison.Ordinal));
    True(stateMachine.Contains("public void CompleteTransport", StringComparison.Ordinal));

    var interpolator = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Application", "Services", "RemotePlaybackProgressInterpolator.cs"));
    True(interpolator.Contains("BackwardsSeekThresholdMs = 3_000", StringComparison.Ordinal));
    True(interpolator.Contains("generation == _generation", StringComparison.Ordinal));
    True(interpolator.Contains("projectedPositionMs = Math.Max", StringComparison.Ordinal));

    var transferCoordinator = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Web", "Services", "PlaybackTransferCoordinator.cs"));
    True(transferCoordinator.Contains("CommitToleranceMs = 3_000", StringComparison.Ordinal));
}

static void KnowledgeImportsRetainResumableBackgroundJobSurfaces()
{
    var root = SourceRoot();
    var provider = File.ReadAllText(Path.Combine(root, "TheRadioVault.Infrastructure", "Services", "WebArchiveProvider.RemoteAdministration.cs"));
    True(provider.Contains("StartResearchPackImport", StringComparison.Ordinal));
    True(provider.Contains("BackgroundJobCategory.ResearchImport", StringComparison.Ordinal));
    True(provider.Contains("GetResearchPackImportStatus", StringComparison.Ordinal));
    True(provider.Contains("CreateKnowledgeImportBackup", StringComparison.Ordinal));
    True(provider.Contains("progress: wikiProgress", StringComparison.Ordinal));

    var server = File.ReadAllText(Path.Combine(root, "TheRadioVault.Server", "ViewModels", "ServerSettingsViewModel.cs"));
    True(server.Contains("StartKnowledgeDatabaseImport", StringComparison.Ordinal));
    True(server.Contains("GetKnowledgeDatabaseImportStatus", StringComparison.Ordinal));
    True(server.Contains("CancelKnowledgeDatabaseImport", StringComparison.Ordinal));
    True(server.Contains("ExportKnowledgeDatabaseAsync", StringComparison.Ordinal));
    True(File.Exists(Path.Combine(root, "TheRadioVault.Server", "Services", "ServerKnowledgeFileService.cs")));

    var webServices = Path.Combine(root, "TheRadioVault.Web", "Services");
    var webShell = File.ReadAllText(Path.Combine(root, "TheRadioVault.Web", "Assets", "web-client.html"));
    var administrationRoutes = File.ReadAllText(Path.Combine(webServices, "LocalWebServer.FederationAdministration.cs"));
    True(webShell.Contains("pollResearchPackImport", StringComparison.Ordinal));
    True(webShell.Contains("researchImportProgressCard", StringComparison.Ordinal));
    True(administrationRoutes.Contains("FederationResearchImportStatus", StringComparison.Ordinal));
}

static void AssertMarkersRemainOrdered(string source, IReadOnlyList<string> markers, string boundaryName)
{
    var priorIndex = -1;
    foreach (var marker in markers)
    {
        var markerIndex = source.IndexOf(marker, StringComparison.Ordinal);
        True(markerIndex > priorIndex, $"The {boundaryName} order changed at {marker}.");
        priorIndex = markerIndex;
    }
}

static void WebHandoffPreservesAlignedSafariDecoder()
{
    var web = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Web", "Assets", "web-client.html"));
    var functionStart = web.IndexOf("function setLogicalPositionImmediately(positionMs)", StringComparison.Ordinal);
    var functionEnd = web.IndexOf("async function waitForLogicalAlignment", functionStart, StringComparison.Ordinal);
    True(functionStart >= 0 && functionEnd > functionStart);
    var seekFunction = web[functionStart..functionEnd];
    True(seekFunction.Contains("logicalSeekDeadbandMs = 750", StringComparison.Ordinal));
    True(seekFunction.Contains("!audio.ended", StringComparison.Ordinal));
    var deadbandCheck = seekFunction.IndexOf(
        "Math.abs(currentLogicalPositionMs() - targetLogicalMs) <= logicalSeekDeadbandMs",
        StringComparison.Ordinal);
    var physicalSeek = seekFunction.IndexOf("audio.currentTime = localMs / 1000", StringComparison.Ordinal);
    True(deadbandCheck >= 0 && physicalSeek > deadbandCheck);
    True(web.Contains("isIosWebKit = /iPhone|iPad|iPod/i.test(navigator.userAgent)", StringComparison.Ordinal));
    var transferStart = web.IndexOf("async function startLocalEpisodeFromGesture", StringComparison.Ordinal);
    var transferEnd = web.IndexOf("async function loadLocalEpisode", transferStart, StringComparison.Ordinal);
    True(transferStart >= 0 && transferEnd > transferStart);
    var transfer = web[transferStart..transferEnd];
    var gesturePlay = transfer.IndexOf("gesturePrime = audio.play()", StringComparison.Ordinal);
    True(gesturePlay >= 0);
    True(!transfer.Contains("assignCanonicalPartSource(id, freshPartIndex, null)", StringComparison.Ordinal));
    True(transfer.Contains("Reuse", StringComparison.Ordinal));
    True(web.Contains("const dormantPositionMs = isIosWebKit ? 0 : shared.positionMs", StringComparison.Ordinal));
    var prepareStart = web.IndexOf("async function prepareCanonicalAudio", StringComparison.Ordinal);
    var prepareEnd = web.IndexOf("async function hydrateLocalEpisode", prepareStart, StringComparison.Ordinal);
    True(prepareStart >= 0 && prepareEnd > prepareStart);
    var prepare = web[prepareStart..prepareEnd];
    True(web.Contains("currentAudioLogicalBaseMs = gesturePrimedPositionMs", StringComparison.Ordinal));
    True(prepare.Contains("alignmentToleranceMs = currentAudioIsPositioned ? 2500 : 1250", StringComparison.Ordinal));
    var clockProof = prepare.IndexOf("await waitForIosDecoderClock()", StringComparison.Ordinal);
    var seekAfterProof = prepare.IndexOf("setLogicalPositionImmediately(positionMs)", StringComparison.Ordinal);
    True(clockProof >= 0 && seekAfterProof > clockProof);
    True(web.Contains("radio-vault-anywhere-shell-v68", StringComparison.Ordinal));
}

static void IphoneBroadcastSwitchesReplaceStaleDecoderInTap()
{
    var web = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Web", "Assets", "web-client.html"));
    var matchStart = web.IndexOf("function decoderMatchesGestureTarget", StringComparison.Ordinal);
    var matchEnd = web.IndexOf("function setLogicalPositionImmediately", matchStart, StringComparison.Ordinal);
    True(matchStart >= 0 && matchEnd > matchStart);
    var match = web[matchStart..matchEnd];
    True(match.Contains("currentAudioEpisodeId", StringComparison.Ordinal));
    True(match.Contains("currentAudioIsPositioned", StringComparison.Ordinal));
    True(match.Contains("currentAudioLogicalPositionMs()", StringComparison.Ordinal));

    var transferStart = web.IndexOf("async function startLocalEpisodeFromGesture", StringComparison.Ordinal);
    var transferEnd = web.IndexOf("async function loadLocalEpisode", transferStart, StringComparison.Ordinal);
    True(transferStart >= 0 && transferEnd > transferStart);
    var transfer = web[transferStart..transferEnd];
    True(transfer.Contains("directAudiblePrime = desiredPlaying && sourceWasUnowned", StringComparison.Ordinal));
    True(transfer.Contains("mustPrimeTargetSourceInGesture = desiredPlaying && isIosWebKit", StringComparison.Ordinal));
    True(transfer.Contains("if (directAudiblePrime || mustPrimeTargetSourceInGesture)", StringComparison.Ordinal));
    True(transfer.Contains("audio.muted = !directAudiblePrime", StringComparison.Ordinal));
    var positionedAssignment = transfer.IndexOf(
        "assignCanonicalGestureStartSource(id, shared.positionMs)", StringComparison.Ordinal);
    var transferAwait = transfer.IndexOf(
        "const beginResult = await beginPhoneTransfer", StringComparison.Ordinal);
    True(positionedAssignment >= 0 && transferAwait > positionedAssignment);

    var prepareStart = web.IndexOf("async function prepareCanonicalAudio", StringComparison.Ordinal);
    var prepareEnd = web.IndexOf("async function hydrateLocalEpisode", prepareStart, StringComparison.Ordinal);
    True(prepareStart >= 0 && prepareEnd > prepareStart);
    var prepare = web[prepareStart..prepareEnd];
    var primedAttach = prepare.IndexOf("Number(gesturePrimedEpisodeId || 0) === id", StringComparison.Ordinal);
    var ordinaryAssignment = prepare.IndexOf("assignCanonicalPartSource(id, partIndex, record)", StringComparison.Ordinal);
    True(primedAttach >= 0 && ordinaryAssignment > primedAttach);
    True(web.Contains("audioEpisodeId: Number(currentAudioEpisodeId || 0)", StringComparison.Ordinal));
}

static void IphonePositionedFailuresPreserveCanonicalGestureFallback()
{
    var web = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Web", "Assets", "web-client.html"));
    var prepareStart = web.IndexOf("async function prepareCanonicalAudio", StringComparison.Ordinal);
    var prepareEnd = web.IndexOf("async function hydrateLocalEpisode", prepareStart, StringComparison.Ordinal);
    True(prepareStart >= 0 && prepareEnd > prepareStart);
    var prepare = web[prepareStart..prepareEnd];
    True(prepare.Contains(
        "positionedGestureTimeoutMs = isIosWebKit && currentAudioIsPositioned ? 4500 : 12000",
        StringComparison.Ordinal));
    True(prepare.Contains("failedWasPositioned = currentAudioIsPositioned", StringComparison.Ordinal));
    var fallbackAssignment = prepare.IndexOf(
        "assignCanonicalPartSource(id, retryPartIndex, null)", StringComparison.Ordinal);
    var fallbackPlay = prepare.IndexOf("await audio.play()", fallbackAssignment, StringComparison.Ordinal);
    var fallbackSeek = prepare.IndexOf("setLogicalPositionImmediately(positionMs)", StringComparison.Ordinal);
    True(fallbackAssignment >= 0 && fallbackPlay > fallbackAssignment && fallbackSeek > fallbackPlay);

    var transferStart = web.IndexOf("async function startLocalEpisodeFromGesture", StringComparison.Ordinal);
    var transferEnd = web.IndexOf("async function loadLocalEpisode", transferStart, StringComparison.Ordinal);
    True(transferStart >= 0 && transferEnd > transferStart);
    var transfer = web[transferStart..transferEnd];
    var captureSource = transfer.IndexOf("gesturePrimeSource = audio.src", StringComparison.Ordinal);
    var prepareCall = transfer.IndexOf("await prepareCanonicalAudio", StringComparison.Ordinal);
    var replacementCheck = transfer.IndexOf(
        "audio.src !== gesturePrimeSource && audioSourceReady && !audio.error",
        StringComparison.Ordinal);
    var targetPrime = transfer.IndexOf("await primeTargetDecoder", StringComparison.Ordinal);
    True(captureSource >= 0 && prepareCall > captureSource);
    True(replacementCheck > prepareCall && targetPrime > replacementCheck);
    True(transfer.Contains(
        "The failed positioned play promise was replaced by a healthy canonical decoder",
        StringComparison.Ordinal));
    True(web.Contains("radio-vault-anywhere-shell-v68", StringComparison.Ordinal));
}

static void RepeatedIphoneHandoffsBypassDormantDecoderGating()
{
    var web = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Web", "Assets", "web-client.html"));

    var dormantStart = web.IndexOf("async function prepareDormantPhoneDecoder", StringComparison.Ordinal);
    var dormantEnd = web.IndexOf("audio.addEventListener(\"loadedmetadata\"", dormantStart, StringComparison.Ordinal);
    True(dormantStart >= 0 && dormantEnd > dormantStart);
    var dormant = web[dormantStart..dormantEnd];
    True(dormant.Contains(
        "if (!id || isIosWebKit || thisPhoneOwnsSession() || phoneTransferInProgress) return;",
        StringComparison.Ordinal));

    True(web.Contains(
        "if (!isIosWebKit && shared?.episodeId && !phoneTransferInProgress)",
        StringComparison.Ordinal));
    Equal(2, web.Split(
        "const preparingDormantTarget = !isIosWebKit && inactive && has &&",
        StringSplitOptions.None).Length - 1);

    var transferStart = web.IndexOf("async function startLocalEpisodeFromGesture", StringComparison.Ordinal);
    var transferEnd = web.IndexOf("async function loadLocalEpisode", transferStart, StringComparison.Ordinal);
    True(transferStart >= 0 && transferEnd > transferStart);
    var transfer = web[transferStart..transferEnd];
    True(transfer.Contains(
        "mustPrimeTargetSourceInGesture = desiredPlaying && isIosWebKit",
        StringComparison.Ordinal));
    True(transfer.Contains("assignCanonicalGestureStartSource(id, shared.positionMs)", StringComparison.Ordinal));
    True(web.Contains("radio-vault-anywhere-shell-v68", StringComparison.Ordinal));
}

static void CanonicalAudioRangesAreCacheCombinable()
{
    var web = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Web", "Services", "LocalWebServer.Media.cs"));
    var streamStart = web.IndexOf("private async Task StreamAudioFileAsync", StringComparison.Ordinal);
    var streamEnd = web.IndexOf("private async Task HandleCanonicalMediaManifestAsync", streamStart, StringComparison.Ordinal);
    True(streamStart >= 0 && streamEnd > streamStart);
    var stream = web[streamStart..streamEnd];
    True(stream.Contains("ETag: ", StringComparison.Ordinal));
    True(stream.Contains("Last-Modified: ", StringComparison.Ordinal));
    True(stream.Contains("Cache-Control: private, max-age=300, no-transform", StringComparison.Ordinal));
    True(stream.Contains("if-range", StringComparison.Ordinal));
    True(!stream.Contains("Vary: Range", StringComparison.Ordinal));
    True(!stream.Contains("Content-Encoding: identity", StringComparison.Ordinal));
    True(!stream.Contains("Cache-Control: no-store", StringComparison.Ordinal));
}

static string SourceRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "TheRadioVault.sln")))
            return directory.FullName;
        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("Could not locate the Radio Vault source root from the source-check output directory.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}

static void True(bool condition, string message = "Expected true, got false.")
{
    if (!condition) throw new InvalidOperationException(message);
}
