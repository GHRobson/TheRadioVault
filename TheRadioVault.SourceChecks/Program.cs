var checks = new (string Name, Action Run)[]
{
    ("Web playback and queue routes stay behind one server boundary", WebPlaybackAndQueueRoutesStayBehindOneServerBoundary),
    ("Federation administration routes stay behind one server boundary", FederationAdministrationRoutesStayBehindOneServerBoundary),
    ("Desktop Saved and transport controls match native icon parity", DesktopSavedAndTransportControlsMatchNativeParity),
    ("Knowledge imports retain resumable background-job surfaces", KnowledgeImportsRetainResumableBackgroundJobSurfaces)
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
    var dispatcher = File.ReadAllText(Path.Combine(services, "LocalWebServer.cs"));
    var playbackQueue = File.ReadAllText(Path.Combine(services, "LocalWebServer.PlaybackQueue.cs"));

    True(dispatcher.Contains("TryHandlePlaybackQueueRouteAsync(", StringComparison.Ordinal));
    True(!dispatcher.Contains("private async Task HandlePlayerApiAsync(", StringComparison.Ordinal));
    True(!dispatcher.Contains("private async Task HandleQueueApiAsync(", StringComparison.Ordinal));
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
    var dispatcher = File.ReadAllText(Path.Combine(services, "LocalWebServer.cs"));
    var federationAdministration = File.ReadAllText(Path.Combine(services, "LocalWebServer.FederationAdministration.cs"));

    const string boundaryCall = "TryHandleFederationAdministrationRouteAsync(";
    var pairingIndex = dispatcher.IndexOf("WebApiRoutes.FederationPair", StringComparison.Ordinal);
    var boundaryIndex = dispatcher.IndexOf(boundaryCall, StringComparison.Ordinal);
    var clientBootstrapIndex = dispatcher.IndexOf("WebApiRoutes.Bootstrap", boundaryIndex, StringComparison.Ordinal);

    True(pairingIndex >= 0);
    True(boundaryIndex > pairingIndex);
    True(clientBootstrapIndex > boundaryIndex);
    var routeWindow = dispatcher[pairingIndex..clientBootstrapIndex];
    Equal(1, dispatcher.Split(boundaryCall, StringSplitOptions.None).Length - 1);
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
    var webShell = File.ReadAllText(Path.Combine(webServices, "LocalWebServer.cs"));
    var administrationRoutes = File.ReadAllText(Path.Combine(webServices, "LocalWebServer.FederationAdministration.cs"));
    True(webShell.Contains("pollResearchPackImport", StringComparison.Ordinal));
    True(webShell.Contains("researchImportProgressCard", StringComparison.Ordinal));
    True(administrationRoutes.Contains("FederationResearchImportStatus", StringComparison.Ordinal));
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
