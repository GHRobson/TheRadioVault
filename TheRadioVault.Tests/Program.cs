using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using System.Reflection;
using TheRadioVault.Application.Abstractions;
using TheRadioVault.Application.Composition;
using TheRadioVault.Application.Models;
using TheRadioVault.Application.Services;
using TheRadioVault.Core.Domain;
using TheRadioVault.Core.Events;
using TheRadioVault.Core.Playback;
using TheRadioVault.Data.Database;
using TheRadioVault.Core.Services;
using TheRadioVault.Core.LibraryTruth;
using TheRadioVault.Research.Models;
using TheRadioVault.Research.Services;
using TheRadioVault.Web.Models;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Services;
using TheRadioVault.Services.Jobs;
using TheRadioVault.Services.Models;
using TheRadioVault.Services.Services;
using TheRadioVault.Transcription.Models;
using TheRadioVault.Transcription.Services;
using TheRadioVault.Presentation.Navigation;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Models;
using TheRadioVault.Services;

var tests = new (string Name, Action Run)[]
{
    ("Stable broadcast identity", () => Equal("RON-FEZ-2005-05-12-P2", BroadcastIdentityService.CreateStableId("Ron & Fez", new DateOnly(2005, 5, 12), 2))),
    ("Unknown-date identity", () => Equal("BENNINGTON-UNKNOWN", BroadcastIdentityService.CreateStableId("Bennington", null))),
    ("Collection aliases", () => Equal("Ron & Fez", MetadataNormalizer.NormalizeCollection("ron and fez"))),
    ("New show aliases", NewShowAliasesNormalize),
    ("Parser recognises new show types", ParserRecognisesNewShowTypes),
    ("Library Truth recognises new show types", LibraryTruthRecognisesNewShowTypes),
    ("Show projections combine legacy collection aliases", ShowProjectionsCombineLegacyCollectionAliases),
    ("Library search finds transcript speech", LibrarySearchFindsTranscriptSpeech),
    ("Avalonia sidebar hides empty show sections", AvaloniaSidebarHidesEmptyShowSections),
    ("Parser accepts catalogue-style interview filenames", ParserAcceptsCatalogueStyleInterviewFilenames),
    ("Folder assignment drives catalogue parser", FolderAssignmentDrivesCatalogueParser),
    ("Catalogue shows use complete list view", CatalogueShowsUseCompleteListView),
    ("Canonical promotion accepts undated catalogue items", CanonicalPromotionAcceptsUndatedCatalogueItems),
    ("Catalogue research fields flow through desktop and Anywhere", CatalogueResearchFieldsFlowThroughDesktopAndAnywhere),
    ("Catalogue dates preserve partial clues without invention", CatalogueDatesPreservePartialCluesWithoutInvention),
    ("Catalogue dates require visible Research decisions", CatalogueDatesRequireVisibleResearchDecisions),
    ("Date review applies to every first-class show", DateReviewAppliesToEveryFirstClassShow),
    ("Library mini panel hides deep catalogue fields", LibraryMiniPanelHidesDeepCatalogueFields),
    ("Sidebar activity replaces page-wide loading bars", SidebarActivityReplacesPageWideLoadingBars),
    ("Playback percentage clamps", () => Equal(100d, PlaybackProgressService.CalculatePercent(1100, 1000))),
    ("Playback persistence rejects transient zero resets", () =>
    {
        Equal(321_000L, PlaybackPersistencePolicy.ResolvePosition(0, 321_000, allowPositionReset: false));
        Equal(0L, PlaybackPersistencePolicy.ResolvePosition(0, 321_000, allowPositionReset: true));
        Equal(120_000L, PlaybackPersistencePolicy.ResolvePosition(120_000, 321_000, allowPositionReset: false));
    }),
    ("Canonical personal state writes roll back atomically", CanonicalPersonalStateWritesRollBackAtomically),
    ("Archive health presentation prioritises unavailable files", () =>
    {
        var presentation = ArchiveHealthPresentationPolicy.Create(0, 0, 2, 4);
        Equal("Archive needs attention", presentation.Headline);
        True(presentation.NeedsAttention);
    }),
    ("Archive health presentation keeps research decisions non-critical", () =>
    {
        var presentation = ArchiveHealthPresentationPolicy.Create(0, 0, 0, 4);
        Equal("Archive healthy", presentation.Headline);
        True(!presentation.NeedsAttention);
    }),
    ("Backup age presentation is plain language", () =>
    {
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        Equal("Yesterday", ArchiveHealthPresentationPolicy.FormatBackupAge(now.AddHours(-30), now));
        Equal("Overdue · 45 days ago", ArchiveHealthPresentationPolicy.FormatBackupAge(now.AddDays(-45), now));
    }),
    ("Scheduled backups are verified and report their next run", ScheduledBackupsAreVerified),
    ("RSS archive inbox baselines, encrypts and deduplicates private feeds", RssArchiveInboxIsSafeAndIncremental),
    ("Backup restore rehearsal validates a disposable clean-server restore", BackupRestoreRehearsalValidatesCleanRestore),
    ("Server diagnostics redact secrets paths and client identities", ServerDiagnosticsRedactPrivateState),
    ("Media consolidation rehearses without moving and commits without deleting", MediaConsolidationIsVerifiedAndNonDestructive),
    ("Media consolidation blocks changed sources and conflicting destinations", MediaConsolidationBlocksChangedFiles),
    ("Media consolidation holds alternates whose runtime cannot be ranked", MediaConsolidationHoldsUnknownRuntimeAlternates),
    ("Media consolidation requires a complete current inventory snapshot", MediaConsolidationRequiresCompleteInventory),
    ("Archive reconciliation exposes a server-owned read-only facade", ArchiveReconciliationIsServerOwnedAndReadOnly),
    ("Scanner content identity rejects weak partial-hash collisions", ScannerContentIdentityRejectsWeakCollisions),
    ("Archive content identity is path and machine independent when verified", ArchiveContentIdentityIsStable),
    ("Moment deduplication keeps one near-identical save", () =>
    {
        True(MomentDeduplicationPolicy.IsEquivalent("BENNINGTON-2026-06-22", 4_498_945, "The old slave hanging tree", "", "BENNINGTON-2026-06-22", 4_498_000, "The old slave hanging tree", ""));
        True(!MomentDeduplicationPolicy.IsEquivalent("BENNINGTON-2026-06-22", 4_498_945, "The old slave hanging tree", "", "BENNINGTON-2026-06-22", 4_503_000, "The old slave hanging tree", ""));
        True(!MomentDeduplicationPolicy.IsEquivalent("BENNINGTON-2026-06-22", 4_498_945, "The old slave hanging tree", "first note", "BENNINGTON-2026-06-22", 4_498_000, "The old slave hanging tree", "different note"));
    }),
    ("Moments service repairs canonical duplicates idempotently", MomentsServiceRepairsCanonicalDuplicates),
    ("Resume action", () => Equal("Resume", PlaybackProgressService.GetDashboardAction(1000, 10000))),
    ("Completion final window", () => True(PlaybackProgressService.IsCompletionThresholdReached(58 * 60_000, 60 * 60_000))),
    ("Unplayed status", () => Equal(ListeningStatus.Unplayed, PlaybackProgressService.GetStatus(0, 10000))),
    ("Record stable ID", () => Equal("BENNINGTON-2016-05-17", new BroadcastIdentity("Bennington", new DateOnly(2016, 5, 17)).StableId)),
    ("Archive entity links preserve identity across clients", ArchiveEntityLinksPreserveIdentity),
    ("Research audit catches show guest", ResearchAuditCatchesShowGuest),
    ("Research audit marks safe repairs", ResearchAuditMarksSafeRepairs),
    ("Research audit marks duplicate source repair", ResearchAuditMarksDuplicateSourceRepair),
    ("Research audit marks generic headline repair", ResearchAuditMarksGenericHeadlineRepair),
    ("Research audit aggregates repeated summaries", ResearchAuditAggregatesRepeatedSummaries),
    ("Research audit offers direct decision cards", ResearchAuditOffersDirectDecisionCards),
    ("Avalonia shell navigation is toolkit neutral", () =>
    {
        var navigation = new ShellNavigationService();
        navigation.NavigateAsync(NavigationRequest.To("dashboard")).GetAwaiter().GetResult();
        navigation.NavigateAsync(NavigationRequest.To("library")).GetAwaiter().GetResult();
        Equal("library", navigation.CurrentRoute);
        True(navigation.TryGoBackAsync().GetAwaiter().GetResult());
        Equal("dashboard", navigation.CurrentRoute);
    }),
    ("Avalonia Settings events return to the UI dispatcher", AvaloniaSettingsEventsUseUiDispatcher),
    ("Avalonia Settings explains server ownership", AvaloniaSettingsExplainsServerOwnership),
    ("Mac client remains usable before server pairing", MacClientRemainsUsableBeforeServerPairing),
    ("Alpha 12 completes server ownership and status UX", Alpha12CompletesServerOwnershipAndStatusUx),
    ("Alpha 13 completes remote client monitoring and server installer", Alpha13CompletesRemoteClientMonitoringAndServerInstaller),
    ("Alpha 13 Buildfix 1 restores folders listening actions and dual installers", Alpha13Buildfix1RestoresFoldersListeningActionsAndDualInstallers),
    ("Alpha 14 renames Web and restores phone connection controls", Alpha14RenamesWebAndRestoresPhoneConnectionControls),
    ("Alpha 15 restores server folder assignment and native audio quality", Alpha15RestoresServerFolderAssignmentAndNativeAudioQuality),
    ("Alpha 16 improves remote responsiveness and playback ownership", Alpha16ImprovesRemoteResponsivenessAndPlaybackOwnership),
    ("Alpha 17 bounds large-library web handoffs", Alpha17BoundsLargeLibraryWebHandoffs),
    ("Direct episode lookup returns only the requested broadcast", DirectEpisodeLookupReturnsOnlyRequestedBroadcast),
    ("Alpha 18 hardens connected-client reliability", Alpha18HardensConnectedClientReliability),
    ("Native handoff preserves the Windows volume session", NativeHandoffPreservesWindowsVolumeSession),
    ("Mac Client uses native AVFoundation and existing server contracts", MacClientUsesNativeAvFoundationAndExistingServerContracts),
    ("macOS and Linux packages preserve the shared client-server boundary", MacAndLinuxPackagesPreserveSharedClientServerBoundary),
    ("Product versions remain consistent", ProductVersionsRemainConsistent),
    ("iOS Client preserves native platform and server boundaries", IosClientPreservesNativePlatformAndServerBoundaries),
    ("Alpha 19 uses a truthful cache-first startup", Alpha19UsesTruthfulCacheFirstStartup),
    ("Alpha 20 hardens release truth and installer payloads", Alpha20HardensReleaseTruthAndInstallerPayloads),
    ("RC1 freezes recovery and upgrade preservation", Rc1FreezesRecoveryAndUpgradePreservation),
    ("RC1 buildfix restores visible Research pack import", Rc1BuildfixRestoresVisibleResearchPackImport),
    ("RC1 buildfix 4 unifies client UI and native downloads", Rc1Buildfix4UnifiesClientUiAndNativeDownloads),
    ("Alpha 0.35 begins the Wiki without breaking stable upgrades", Alpha035BeginsWikiWithoutBreakingStableUpgrades),
    ("Native downloads persist, audit and prepare local media", NativeDownloadsPersistAuditAndPrepareLocalMedia),
    ("Native download policies expire and trim local media safely", NativeDownloadPoliciesExpireAndTrimSafely),
    ("Connected views refresh bounded stale data", ConnectedViewsRefreshBoundedStaleData),
    ("Avalonia incomplete progress overrun clamps to 99", () =>
    {
        var item = new TheRadioVault.Services.Models.LibraryBroadcastSummary(
            "KEY", 1, "BROADCAST-1", 1, "Test", null, DateTimeOffset.UtcNow, "", "Title", null, false, false, true,
            1200, 1000, null, null, 1, 1, 1, false, "");
        Equal(99, item.ProgressPercent);
    }),
    ("Avalonia completed broadcasts always display 100 percent", () =>
    {
        var item = new TheRadioVault.Services.Models.LibraryBroadcastSummary(
            "KEY", 1, "BROADCAST-1", 1, "Test", null, DateTimeOffset.UtcNow, "", "Title", null, false, true, false,
            58_000, 60_000, null, null, 1, 1, 1, false, "");
        Equal(100, item.ProgressPercent);
    }),
    ("Avalonia incomplete broadcasts never display 100 percent", () =>
    {
        var item = new TheRadioVault.Services.Models.LibraryBroadcastSummary(
            "KEY", 1, "BROADCAST-1", 1, "Test", null, DateTimeOffset.UtcNow, "", "Title", null, false, false, true,
            59_900, 60_000, null, null, 1, 1, 1, false, "");
        Equal(99, item.ProgressPercent);
    }),
    ("Avalonia live progress matches canonical aliases", () =>
    {
        var item = new TheRadioVault.Services.Models.LibraryBroadcastSummary(
            "CANONICAL-KEY", 41, "BROADCAST-1", 1, "Test", null, DateTimeOffset.UtcNow, "", "Title", null, false, false, true,
            10_000, 60_000, null, null, 1, 1, 1, false, "");
        var live = new TheRadioVault.Presentation.ViewModels.PlaybackLiveProgress(
            99, "CANONICAL-KEY", 30_000, 60_000, false, true, true, DateTimeOffset.UtcNow);
        True(live.Matches(item));
        Equal(50, live.ProgressPercent);
    }),
    ("Desktop playback state machine preserves pending transport intent", DesktopPlaybackStateMachinePreservesPendingIntent),
    ("Desktop remote progress interpolation removes heartbeat wiggle", DesktopRemoteProgressInterpolationRemovesHeartbeatWiggle),
    ("Playback startup succeeds only after decoder readiness", PlaybackStartupWaitsForReadiness),
    ("Playback startup reports a distinct decoder timeout", PlaybackStartupReportsTimeout),
    ("Playback startup distinguishes unavailable media", PlaybackStartupReportsUnavailableMedia),
    ("Playback startup preserves caller cancellation", PlaybackStartupPreservesCallerCancellation),
    ("Playback startup cancels and serializes superseded selections", PlaybackStartupSupersedesOlderSelection),
    ("Shared playback projects the playhead between heartbeats", () =>
    {
        var now = new DateTimeOffset(2026, 7, 28, 20, 0, 0, TimeSpan.Zero);
        var state = new TheRadioVault.Services.Models.PlaybackDeviceState(
            "laptop", "Laptop", "DesktopClient", 42, "Test", "Broadcast",
            10_000, 60_000, 1.5d, true, true, true, now.AddSeconds(-2));
        Equal(13_000L, state.ProjectedPositionMs(now));
        Equal(10_000L, (state with { IsPlaying = false }).ProjectedPositionMs(now));
        Equal(60_000L, (state with { PositionMs = 59_500, Speed = 3d }).ProjectedPositionMs(now));
    }),
    ("Native server client advances capability generation 41", () => Equal(41, new WebServerOptions().CapabilityGeneration)),
    ("Native pairing normalizes pinned certificate identities", () =>
        Equal("AABBCCDDEEFF", NativeServerConnectionPreferences.NormalizeThumbprint("aa:bb cc-dd ee:ff"))),
    ("Native server cache encrypts and authenticates read-only responses", NativeServerCacheEncryptsResponses),
    ("Native client startup prefers its persistent cache", NativeClientStartupPrefersPersistentCache),
    ("Native recovery restores live cache state once", NativeRecoveryRestoresLiveCacheState),
    ("Native server discovery labels remain readable", () =>
    {
        var option = new TheRadioVault.Services.Models.ConnectedServerOption(
            "server-id", "Archive server", "192.168.1.20", 8766, "0.34.0-alpha11", true, 1);
        True(option.DisplayText.Contains(" · ", StringComparison.Ordinal));
        True(!option.DisplayText.Contains("Â", StringComparison.Ordinal));
    }),
    ("Connected diagnostic journal redacts secret fields", () =>
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-1);
        TheRadioVault.Services.Diagnostics.RuntimeDiagnosticRecorder.Record(
            "diagnostics", "privacy", "passed", 1, @"Failed at C:\Users\Graham\radio\file.mp3",
            new Dictionary<string, string>
            {
                ["accessToken"] = "must-not-leak",
                ["route"] = "/api/v1/player",
                ["device"] = "laptop"
            });
        var item = TheRadioVault.Services.Diagnostics.RuntimeDiagnosticRecorder.Snapshot(started).Last();
        True(!item.Details.ContainsKey("accessToken"));
        True(!item.Message.Contains("Graham", StringComparison.OrdinalIgnoreCase));
        True(item.Message.Contains("<local-path>", StringComparison.Ordinal));
        Equal("/api/v1/player", item.Details["route"]);
    }),
    ("Connected diagnostic duration is non-negative", () =>
    {
        var now = DateTimeOffset.UtcNow;
        var report = new TheRadioVault.Services.Models.ConnectedPlaybackDiagnosticReport(
            "test", 1, Guid.NewGuid(), "PAIR1234",
            TheRadioVault.Services.Models.ConnectedPlaybackDiagnosticMode.Quick,
            "test", "Server", "server", "server", "Windows", "8",
            now, now.AddMilliseconds(125),
            TheRadioVault.Services.Models.ConnectedPlaybackDiagnosticStatus.Passed,
            "passed", Array.Empty<TheRadioVault.Services.Models.ConnectedPlaybackDiagnosticStep>(),
            Array.Empty<TheRadioVault.Services.Models.RuntimeDiagnosticEvent>(),
            new Dictionary<string, string>());
        Equal(125L, report.DurationMs);
    }),
    ("Web API routes are versioned", () => Equal("/api/v1/broadcasts/42", WebApiRoutes.Broadcast(42))),
    ("Transactional playback routes are versioned", () =>
    {
        Equal("/api/v1/player/transfer/begin", WebApiRoutes.PlayerTransferBegin);
        Equal("/api/v1/player/transfer/ready", WebApiRoutes.PlayerTransferReady);
        Equal("/api/v1/player/transfer/commit", WebApiRoutes.PlayerTransferCommit);
        Equal("/api/v1/player/transfer/cancel", WebApiRoutes.PlayerTransferCancel);
        Equal("/api/v1/player/transfer/source-stopped", WebApiRoutes.PlayerTransferSourceStopped);
    }),
    ("Alpha 6 remote administration routes are versioned", () =>
    {
        Equal("/api/v1/federation/settings", WebApiRoutes.FederationSettings);
        Equal("/api/v1/federation/playback-preferences", WebApiRoutes.FederationPlaybackPreferences);
        Equal("/api/v1/federation/research-packs/import/preview", WebApiRoutes.FederationResearchImportPreview);
        Equal("/api/v1/federation/research-packs/import/apply", WebApiRoutes.FederationResearchImportApply);
        Equal("/api/v1/federation/research-packs/import/status", WebApiRoutes.FederationResearchImportStatus);
        Equal("/api/v1/federation/research-packs/import/cancel", WebApiRoutes.FederationResearchImportCancel);
        Equal("/api/v1/federation/research-packs/export", WebApiRoutes.FederationResearchExport);
        Equal("/api/v1/federation/library-sync", WebApiRoutes.FederationLibrarySync);
        Equal("/api/v1/federation/parity", WebApiRoutes.FederationParity);
        Equal("/api/v1/federation/research-workspace", WebApiRoutes.FederationResearchWorkspace);
        Equal("/api/v1/federation/research-workspace/42", WebApiRoutes.ResearchWorkspaceRecord(42));
        Equal("/api/v1/federation/research-workspace/undated", WebApiRoutes.FederationResearchUndated);
        Equal("/api/v1/federation/research-workspace/undated/42/date", WebApiRoutes.ResearchUndatedDate(42));
        Equal("/api/v1/federation/research-workspace/coverage/7", WebApiRoutes.ResearchCoverage(7));
        Equal("/api/v1/federation/research-workspace/coverage/show/Ron%20%26%20Fez", WebApiRoutes.ResearchCoverageByShow("Ron & Fez"));
    }),
    ("Web detail contract preserves research", WebDetailContractPreservesResearch),
    ("LAN discovery announcement excludes credentials", LanDiscoveryAnnouncementExcludesCredentials),
    ("LAN discovery calculates directed IPv4 broadcast", LanDiscoveryCalculatesDirectedBroadcast),
    ("Research repair guard allows unchanged state", ResearchRepairGuardAllowsUnchangedState),
    ("Research repair guard blocks later changes", ResearchRepairGuardBlocksLaterChanges),
    ("Core hardening application contracts are platform neutral", CoreHardeningApplicationContractsArePlatformNeutral),
    ("Core hardening startup mode is application-owned", CoreHardeningStartupModeIsApplicationOwned),
    ("Core hardening composition root resolves singleton and factory services", CoreHardeningCompositionRootResolvesServices),
    ("Core hardening composition freezes and reports required services", CoreHardeningCompositionFreezesAndReportsRequiredServices),
    ("Core hardening lazy singleton factories create once", CoreHardeningLazySingletonFactoriesCreateOnce),
    ("Core hardening registry disposes singletons in reverse order", CoreHardeningRegistryDisposesSingletonsInReverseOrder),
    ("Core hardening composition detects dependency cycles", CoreHardeningCompositionDetectsDependencyCycles),
    ("Core hardening playback session factory owns construction", CoreHardeningPlaybackSessionFactoryOwnsConstruction),
    ("Core hardening shutdown pipeline isolates cleanup failures", CoreHardeningShutdownPipelineIsolatesFailures),
    ("Core hardening window transition begins once", CoreHardeningWindowTransitionBeginsOnce),
    ("Core hardening platform requests remain neutral", CoreHardeningPlatformRequestsRemainNeutral),
    ("Core hardening playback session owns engine commands", CoreHardeningPlaybackSessionOwnsEngineCommands),
    ("Core hardening playback progress protects resume position", CoreHardeningPlaybackProgressProtectsResumePosition),
    ("Core hardening completion requires natural playback", CoreHardeningCompletionRequiresNaturalPlayback),
    ("Core hardening remote Library session owns cursor and reconnect policy", CoreHardeningRemoteLibrarySessionOwnsLifecycle),
    ("Application event bus is typed and disposable", ApplicationEventBusIsTypedAndDisposable),
    ("Background jobs publish completion", BackgroundJobsPublishCompletion),
    ("Background jobs cancel safely", BackgroundJobsCancelSafely),
    ("Background jobs dispose safely while running", BackgroundJobsDisposeSafelyWhileRunning),
    ("Live playback state is atomic", LivePlaybackStateIsAtomic),
    ("Offline progress ordering preserves newer manual changes", OfflineProgressOrderingPreservesNewerManualChanges),
    ("Wiki pages protect newer human revisions", WikiPagesProtectNewerHumanRevisions),
    ("Wiki authoring packs round-trip citations images and timelines", WikiAuthoringPacksRoundTripEvidence),
    ("Knowledge imports recover untitled AI citation sources", KnowledgeImportsRecoverUntitledAiCitationSources),
    ("Knowledge imports reconcile existing Explore slugs", KnowledgeImportsReconcileExistingExploreSlugs),
    ("Ambiguous research review uses a schema-valid state", AmbiguousResearchReviewUsesSchemaValidState),
    ("Knowledge exports teach AI agents the portable database", KnowledgeExportsTeachAiAgentsThePortableDatabase),
    ("Complete knowledge exports include every show and transcript", CompleteKnowledgeExportsIncludeEveryShowAndTranscript),
    ("Knowledge export UI is always archive-wide", KnowledgeExportUiIsAlwaysArchiveWide),
    ("Knowledge import progress bars are determinate", KnowledgeImportProgressBarsAreDeterminate),
    ("Knowledge matching uses one archive index", KnowledgeMatchingUsesOneArchiveIndex),
    ("Installers prevent accidental downgrades", InstallersPreventAccidentalDowngrades),
    ("Native wiki workspace exposes editing packs and timelines", NativeWikiWorkspaceExposesEditingPacksAndTimelines),
    ("Native wiki opens on an exploration dashboard", NativeWikiOpensOnExplorationDashboard),
    ("Wiki entity chips open the native and Web readers", WikiEntityChipsOpenNativeAndWebReaders),
    ("Wiki refinement adds navigation discovery audits and timeline exploration", WikiRefinementAddsNavigationDiscoveryAndTimelineExploration),
    ("Knowledge surfaces use article-first dashboards and summaries", KnowledgeSurfacesUseArticleFirstDashboardsAndSummaries),
    ("Alpha 9 hardens documented Knowledge portability", Alpha9HardensDocumentedKnowledgePortability),
    ("Canonical topics automatically merge safe archive and Wiki duplicates", CanonicalTopicsAutomaticallyMergeSafeDuplicates),
    ("Wiki starter pages are archive-aware and idempotent", WikiStarterPagesAreArchiveAwareAndIdempotent),
    ("Human Wiki evidence edits save with the article revision", HumanWikiEvidenceEditsSaveWithArticleRevision),
    ("Wiki authoring packs carry archive context and detailed review", WikiPacksCarryArchiveContextAndDetailedReview),
    ("Canonical library cutover projects one broadcast per truth group", CanonicalLibraryCutoverProjectsBroadcasts),
    ("Post-cutover scans append new broadcasts to the canonical library", PostCutoverScanAppendsCanonicalBroadcasts),
    ("Research date updates the active adopted Library projection", ResearchDateUpdatesActiveAdoptedLibraryProjection),
    ("Quick date-review decisions persist and reopen safely", QuickDateReviewDecisionsPersistAndReopenSafely),
    ("Research packs round-trip date-review decisions", ResearchPacksRoundTripDateReviewDecisions),
    ("Research packs tolerate harmless AI scalar variations", ResearchPacksTolerateAiScalarVariations),
    ("Canonical timeline maps source transcript positions", CanonicalTimelineMapsSourcePosition),
    ("Whisper configuration exposes model capabilities", WhisperConfigurationExposesModelCapabilities),
    ("Multi-speaker diarization splits timed transcript turns", MultiSpeakerDiarizationSplitsTimedTranscriptTurns),
    ("Whisper settings persist for the live desktop engine", WhisperSettingsPersistForLiveDesktopEngine),
    ("Transcription ranges have stable display text", TranscriptionRangesHaveStableDisplayText),
    ("Long-form transcription protects continuity and timestamps", LongFormTranscriptionProtectsContinuityAndTimestamps),
    ("Dedicated server foundation is UI-isolated and revision-safe", DedicatedServerFoundationIsUiIsolatedAndRevisionSafe),
    ("Dedicated server health polling never blocks the settings UI", DedicatedServerHealthPollingNeverBlocksSettingsUi),
    ("Dedicated server administration uses focused screens", DedicatedServerAdministrationUsesFocusedScreens),
    ("Dedicated server owns transcription workers and batch controls", DedicatedServerOwnsTranscriptionWorkers),
    ("Loopback native handoff maps server ownership", LoopbackNativeHandoffMapsServerOwnership),
    ("Transcription jobs preserve worker options", TranscriptionJobsPreserveWorkerOptions),
    ("Server transcripts use the portable source vocabulary", ServerTranscriptsUsePortableSourceVocabulary),
    ("Abandoned transcription jobs become retryable", AbandonedTranscriptionJobsBecomeRetryable),
    ("Avalonia exposes the server-controlled transcription workflow", AvaloniaExposesLocalTranscriptionWorkflow),
    ("Transcript repository round-trips timed segments", TranscriptRepositoryRoundTripsTimedSegments),
    ("Transcript packages reject overlapping segments", TranscriptPackagesRejectOverlappingSegments),
    ("Transcript exchange protects broadcast identity", TranscriptExchangeProtectsBroadcastIdentity),
    ("Speaker confirmations accumulate cross-broadcast voice evidence", SpeakerConfirmationsAccumulateVoiceEvidence),
    ("Transcript packages preserve speaker assignments", TranscriptPackagesPreserveSpeakerAssignments),
    ("Transcript review edits and subtitle exports are stable", TranscriptReviewEditsAndSubtitleExportsAreStable),
    ("Batch transcription persists skipping priority and restart recovery", BatchTranscriptionPersistsSkippingPriorityAndRecovery),
    ("Transcript quality collapses music runs", TranscriptQualityCollapsesMusicRuns),
    ("Portable metadata removes private paths", PortableMetadataRemovesPrivatePaths),
    ("Transcript v3 packages are compressed", TranscriptV3PackagesAreCompressed)
};

var selectedTests = args.Length == 0
    ? tests
    : tests.Where(test => args.Any(filter => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
if (selectedTests.Length == 0)
{
    Console.Error.WriteLine("No smoke tests matched the supplied filters.");
    return 2;
}

var failures = new List<string>();
foreach (var test in selectedTests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.Message}");
        Console.Error.WriteLine($"FAIL  {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"{selectedTests.Length - failures.Count}/{selectedTests.Length} smoke tests passed.");
return failures.Count == 0 ? 0 : 1;

static void ScheduledBackupsAreVerified()
{
    var root = Path.Combine(Path.GetTempPath(), "RadioVaultScheduledBackupTests", Guid.NewGuid().ToString("N"));
    var now = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
    try
    {
        using var scheduler = new ScheduledBackupService(
            interval: TimeSpan.FromDays(1),
            utcNow: () => now,
            createBackup: destination =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
                var database = archive.CreateEntry("radio_vault.db");
                using var stream = database.Open();
                stream.Write([1, 2, 3, 4]);
                return destination;
            },
            backupDirectory: root,
            verifyBackup: _ => true);

        var completed = scheduler.RunIfDueAsync(force: true).GetAwaiter().GetResult();
        True(completed.LastBackupVerified);
        Equal(now, completed.LastCompletedAt);
        Equal(now.AddDays(1), completed.NextDueAt);
        True(File.Exists(completed.LatestBackupPath));

        now = now.AddHours(1);
        var notDue = scheduler.RunIfDueAsync().GetAwaiter().GetResult();
        Equal(completed.LatestBackupPath, notDue.LatestBackupPath);
        Equal(completed.LastCompletedAt, notDue.LastCompletedAt);
    }
    finally
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }
}

static void RssArchiveInboxIsSafeAndIncremental()
{
    var root = Path.Combine(Path.GetTempPath(), "RadioVaultRssTests", Guid.NewGuid().ToString("N"));
    var archive = Path.Combine(root, "Bennington");
    Directory.CreateDirectory(archive);
    try
    {
        var database = new SqliteDatabase(Path.Combine(root, "rss.sqlite"));
        database.Initialize();
        long folderId;
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                SELECT $path,id,1,1 FROM collections WHERE name='Bennington'
                RETURNING id;
                """;
            command.Parameters.AddWithValue("$path", archive);
            folderId = Convert.ToInt64(command.ExecuteScalar());
        }

        var handler = new RssScenarioHandler();
        using var http = new HttpClient(handler);
        var preferences = new WebServerPreferences
        {
            ServerInstanceId = Guid.NewGuid().ToString("D"),
            CertificatePassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()
        };
        var scanCount = 0;
        using var service = new RssFeedIngestionService(
            database,
            preferences,
            _ => { scanCount++; return Task.FromResult(true); },
            http);
        var feed = service.CreateAsync(new RssFeedSaveRequest(
            "Bennington private feed",
            new RssFeedSource("https://feeds.example/private.xml?token=do-not-store-plainly", "graham", "private-password"),
            folderId,
            CheckIntervalMinutes: 30,
            ImportExistingOnFirstCheck: false)).GetAwaiter().GetResult();

        var baseline = service.CheckNowAsync(feed.Id).GetAwaiter().GetResult();
        Equal(0, baseline.NewDownloads);
        Equal(0, Directory.EnumerateFiles(archive, "*.mp3").Count());

        handler.IncludeNewEpisode = true;
        var newItems = service.CheckNowAsync(feed.Id).GetAwaiter().GetResult();
        Equal(1, newItems.NewDownloads);
        Equal(1, Directory.EnumerateFiles(archive, "*.mp3").Count());
        Equal(1, scanCount);
        Equal(1, handler.AudioRequests);
        True(handler.SawConditionalRequest, "The second RSS check did not use the saved ETag.");
        True(handler.SawFeedBasicAuthentication, "Private RSS Basic authentication was not sent to the feed host.");
        True(!handler.SentAuthenticationToMediaHost, "RSS credentials were forwarded to a different enclosure host.");

        var repeated = service.CheckNowAsync(feed.Id).GetAwaiter().GetResult();
        Equal(0, repeated.NewDownloads);
        Equal(1, handler.AudioRequests);
        Equal(1, scanCount);

        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE rss_feed_items SET status='Downloaded' WHERE feed_id=$id AND downloaded_at IS NOT NULL;";
            command.Parameters.AddWithValue("$id", feed.Id);
            command.ExecuteNonQuery();
        }
        _ = service.RunIfDueAsync().GetAwaiter().GetResult();
        Equal(2, scanCount);
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT status FROM rss_feed_items WHERE feed_id=$id AND downloaded_at IS NOT NULL;";
            command.Parameters.AddWithValue("$id", feed.Id);
            Equal("Imported", Convert.ToString(command.ExecuteScalar()));
        }

        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT display_url,protected_source FROM rss_feed_subscriptions WHERE id=$id;";
            command.Parameters.AddWithValue("$id", feed.Id);
            using var reader = command.ExecuteReader();
            True(reader.Read());
            var display = reader.GetString(0);
            var protectedSource = reader.GetString(1);
            True(!display.Contains("token", StringComparison.OrdinalIgnoreCase));
            True(!protectedSource.Contains("do-not-store-plainly", StringComparison.Ordinal));
            True(!protectedSource.Contains("private-password", StringComparison.Ordinal));
            True(!protectedSource.Contains("graham", StringComparison.OrdinalIgnoreCase));
        }

        var downloadedPath = Directory.EnumerateFiles(archive, "*.mp3").Single();
        service.DeleteAsync(feed.Id).GetAwaiter().GetResult();
        True(File.Exists(downloadedPath), "Removing an RSS subscription deleted archive audio.");

        handler.IncludeNewEpisode = false;
        var backCatalogueFeed = service.CreateAsync(new RssFeedSaveRequest(
            "Import existing test",
            new RssFeedSource("https://feeds.example/private.xml?token=second-feed", "graham", "private-password"),
            folderId,
            CheckIntervalMinutes: 30,
            ImportExistingOnFirstCheck: true)).GetAwaiter().GetResult();
        var importedExisting = service.CheckNowAsync(backCatalogueFeed.Id).GetAwaiter().GetResult();
        Equal(1, importedExisting.NewDownloads);
        Equal(2, Directory.EnumerateFiles(archive, "*.mp3").Count());
        service.DeleteAsync(backCatalogueFeed.Id).GetAwaiter().GetResult();
    }
    finally
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }
}

static void BackupRestoreRehearsalValidatesCleanRestore()
{
    var root = Path.Combine(Path.GetTempPath(), "RadioVaultRestoreRehearsalTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var databasePath = Path.Combine(root, "radio_vault.db");
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var collection = connection.CreateCommand();
            collection.CommandText = "INSERT INTO collections(name,sort_name) VALUES('Rehearsal Show','Rehearsal Show') RETURNING id";
            var collectionId = Convert.ToInt64(collection.ExecuteScalar());
            using var episode = connection.CreateCommand();
            episode.CommandText = "INSERT INTO episodes(collection_id,title,status,date_added,updated_at) VALUES($collection,'Restore rehearsal','Unplayed',$now,$now)";
            episode.Parameters.AddWithValue("$collection", collectionId);
            episode.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            episode.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var backupPath = Path.Combine(root, "verified.trvbackup");
        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(databasePath, "radio_vault.db");
            var artwork = archive.CreateEntry("Artwork/rehearsal.png");
            using var artworkStream = artwork.Open();
            artworkStream.Write([1, 2, 3]);
        }

        var result = new BackupRestoreRehearsalService().Rehearse(backupPath);
        True(result.CanRestore);
        Equal("ok", result.QuickCheck);
        Equal(0, result.ForeignKeyViolations);
        True(result.SchemaVersion > 0);
        True(result.TableCount > 0);
        Equal(1L, result.BroadcastCount);
        Equal(1, result.ArtworkFiles);

        var invalidPath = Path.Combine(root, "invalid.trvbackup");
        using (var archive = ZipFile.Open(invalidPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("radio_vault.db");
            using var stream = entry.Open();
            stream.Write([1, 2, 3, 4]);
        }
        True(!new BackupRestoreRehearsalService().Verify(invalidPath));
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }
}

static void ServerDiagnosticsRedactPrivateState()
{
    var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var secret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    var snapshot = new ServerHealthSnapshot(
        DateTimeOffset.UtcNow,
        false,
        "Archive needs attention",
        "ok",
        "radio_vault.db",
        1024,
        2048,
        2,
        10,
        8,
        1,
        1,
        true,
        true,
        DateTimeOffset.UtcNow.AddYears(1),
        1,
        [new WebDeviceSyncStatus("real-iphone-client-id", 12, DateTimeOffset.UtcNow,
            $"Could not write {profile}/RadioVault?token={secret}")],
        new WebScheduledBackupStatus(true, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1),
            Path.Combine(profile, "Backups", "latest.trvbackup"), false, $"Bearer {secret}"),
        $"Access token={secret} at {profile}/RadioVault");

    var json = Encoding.UTF8.GetString(new ServerHealthDiagnosticsService().CreateRedactedReport(snapshot, "test"));
    True(!json.Contains(secret, StringComparison.Ordinal));
    True(!json.Contains("real-iphone-client-id", StringComparison.Ordinal));
    True(string.IsNullOrWhiteSpace(profile) || !json.Contains(profile, StringComparison.OrdinalIgnoreCase));
    True(json.Contains("clientIdsPseudonymized", StringComparison.Ordinal));
    True(json.Contains("latest.trvbackup", StringComparison.Ordinal));
}

static void MediaConsolidationIsVerifiedAndNonDestructive()
{
    var root = Path.Combine(ConsolidationTestTempRoot(), "RadioVaultConsolidationTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var fixture = CreateMediaConsolidationFixture(root);
        var managed = Path.Combine(root, "managed");
        var quarantine = Path.Combine(root, "quarantine");
        var service = new MediaConsolidationService(fixture.Database);
        var plan = service.CreatePlan(managed, quarantine);

        Equal(4, plan.InventoryMediaRecords);
        Equal(4, plan.InventoryAvailableFiles);
        Equal(0, plan.InventoryMissingFiles);
        Equal(0, plan.HeldSourceFiles);
        Equal(plan.InventoryAvailableFiles, plan.AccountedAvailableFiles);
        Equal(1, plan.ManagedFiles);
        Equal(3, plan.RejectedFiles);
        Equal(1, plan.Items.Count(item => item.Disposition == MediaConsolidationDisposition.RejectedDuplicate));
        var selected = plan.Items.Single(item => item.IsManagedCopy);
        Equal(2_000L, selected.DurationMs);
        Equal(fixture.Sources[2], selected.SourcePath);
        True(Path.GetFileName(selected.ManagedPath).Contains("Part", StringComparison.OrdinalIgnoreCase) == false,
            "A standalone recording must not gain a multipart suffix merely because exact duplicates exist.");

        var rehearsal = service.Rehearse(plan);
        True(rehearsal.CanCommit, string.Join(Environment.NewLine, rehearsal.Problems));
        Throws<InvalidDataException>(() => service.Rehearse(plan with { InventoryAvailableFiles = 3 }));
        True(fixture.Sources.All(File.Exists), "Rehearsal must not move a source file.");
        True(plan.Items.Where(item => item.IsManagedCopy).All(item => !File.Exists(item.ManagedPath)),
            "Rehearsal must not create managed media.");
        True(File.Exists(rehearsal.ManifestPath));

        var journalPath = Path.Combine(Path.GetDirectoryName(rehearsal.ManifestPath)!, "journal.json");
        File.WriteAllText(journalPath, JsonSerializer.Serialize(new
        {
            PlanId = "another-plan",
            PlanSignature = "another-signature",
            StartedAt = DateTimeOffset.UtcNow,
            Status = "copying-managed-files"
        }));
        Throws<InvalidDataException>(() => service.Commit(plan, rehearsal, plan.ConfirmationText));
        True(fixture.Sources.All(File.Exists));
        File.WriteAllText(journalPath, JsonSerializer.Serialize(new
        {
            plan.PlanId,
            plan.PlanSignature,
            StartedAt = DateTimeOffset.UtcNow,
            Status = "copying-managed-files",
            ManagedItemIds = Array.Empty<string>(),
            QuarantinedItemIds = Array.Empty<string>(),
            DatabaseRowsUpdated = 0
        }));
        var recovered = service.LoadLatestInterruptedPlan(quarantine);
        True(recovered is not null);
        Equal(plan.PlanSignature, recovered!.PlanSignature);
        Throws<InvalidOperationException>(() => service.Commit(plan, rehearsal, "CONSOLIDATE THE WRONG PLAN"));
        True(fixture.Sources.All(File.Exists));

        var result = service.Commit(plan, rehearsal, plan.ConfirmationText);
        True(result.Completed);
        Equal(1, result.ManagedFiles);
        Equal(4, result.QuarantinedFiles);
        True(File.Exists(result.DatabaseBackupPath));
        Equal("ok", BackupRestoreRehearsalService.InspectQuickCheck(result.DatabaseBackupPath));
        True(File.Exists(result.JournalPath));
        True(plan.Items.All(item => !File.Exists(item.SourcePath)));
        True(plan.Items.All(item => File.Exists(item.QuarantinePath)),
            "Every original—including the selected winner—must remain in quarantine.");
        True(File.Exists(selected.ManagedPath));
        Equal(selected.FullSha256, TestSha256(selected.ManagedPath));
        True(plan.Items.All(item => TestSha256(item.QuarantinePath) == item.FullSha256));

        using (var connection = fixture.Database.OpenConnection())
        {
            using var selectedRow = connection.CreateCommand();
            selectedRow.CommandText = "SELECT path,is_missing,storage_state,is_preferred FROM media_files WHERE id=$id";
            selectedRow.Parameters.AddWithValue("$id", selected.MediaFileId);
            using var reader = selectedRow.ExecuteReader();
            True(reader.Read());
            Equal(selected.ManagedPath, reader.GetString(0));
            Equal(0L, reader.GetInt64(1));
            Equal("AvailableOffline", reader.GetString(2));
            Equal(1L, reader.GetInt64(3));
        }

        // A completed journal is deliberately idempotent: rerunning the exact
        // confirmed plan verifies existing outputs instead of overwriting them.
        var resumedRehearsal = service.Rehearse(plan);
        True(resumedRehearsal.CanCommit);
        var resumed = service.Commit(plan, resumedRehearsal, plan.ConfirmationText);
        True(resumed.Completed);
        True(plan.Items.All(item => File.Exists(item.QuarantinePath)));
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }
}

static void MediaConsolidationBlocksChangedFiles()
{
    var root = Path.Combine(ConsolidationTestTempRoot(), "RadioVaultConsolidationSafetyTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var fixture = CreateMediaConsolidationFixture(root);
        var service = new MediaConsolidationService(fixture.Database);
        var plan = service.CreatePlan(Path.Combine(root, "managed"), Path.Combine(root, "quarantine"));
        Throws<InvalidDataException>(() => service.Rehearse(plan with { ManagedRoot = Path.Combine(root, "changed-root") }));
        var changed = plan.Items[0];
        var original = File.ReadAllBytes(changed.SourcePath);
        File.WriteAllBytes(changed.SourcePath, Enumerable.Repeat((byte)0xEE, original.Length).ToArray());

        var changedRehearsal = service.Rehearse(plan);
        True(!changedRehearsal.CanCommit);
        True(changedRehearsal.Problems.Any(problem => problem.Contains("changed", StringComparison.OrdinalIgnoreCase)));
        True(plan.Items.All(item => File.Exists(item.SourcePath)));
        True(plan.Items.All(item => !File.Exists(item.QuarantinePath)));

        File.WriteAllBytes(changed.SourcePath, original);
        var selected = plan.Items.Single(item => item.IsManagedCopy);
        Directory.CreateDirectory(Path.GetDirectoryName(selected.ManagedPath)!);
        var conflicting = Encoding.UTF8.GetBytes("pre-existing unrelated media");
        File.WriteAllBytes(selected.ManagedPath, conflicting);
        var destinationRehearsal = service.Rehearse(plan);
        True(!destinationRehearsal.CanCommit);
        True(destinationRehearsal.Problems.Any(problem => problem.Contains("different data", StringComparison.OrdinalIgnoreCase)));
        True(File.ReadAllBytes(selected.ManagedPath).SequenceEqual(conflicting),
            "A conflicting destination must never be overwritten.");
        True(plan.Items.All(item => File.Exists(item.SourcePath)));
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }
}

static void MediaConsolidationHoldsUnknownRuntimeAlternates()
{
    var root = Path.Combine(ConsolidationTestTempRoot(), "RadioVaultConsolidationDurationTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var fixture = CreateMediaConsolidationFixture(root);
        using (var connection = fixture.Database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE media_files SET duration_ms=0 WHERE path=$path;
                UPDATE library_truth_recordings SET duration_ms=0
                 WHERE run_id=9200 AND recording_key='recording-short';
                """;
            command.Parameters.AddWithValue("$path", fixture.Sources[0]);
            command.ExecuteNonQuery();
        }
        var service = new MediaConsolidationService(fixture.Database);
        Throws<InvalidOperationException>(() => service.CreatePlan(
            Path.Combine(root, "managed"),
            Path.Combine(root, "quarantine")));
        True(fixture.Sources.All(File.Exists));
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }
}

static void MediaConsolidationRequiresCompleteInventory()
{
    var root = Path.Combine(ConsolidationTestTempRoot(), "RadioVaultConsolidationInventoryTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var fixture = CreateMediaConsolidationFixture(root);
        var service = new MediaConsolidationService(fixture.Database);
        var originalPlan = service.CreatePlan(Path.Combine(root, "managed"), Path.Combine(root, "quarantine"));
        Equal(4, originalPlan.AccountedAvailableFiles);

        var latePath = Path.Combine(root, "source", "arrived-after-library-truth.mp3");
        File.WriteAllBytes(latePath, Enumerable.Repeat((byte)0xA5, 128).ToArray());
        using (var connection = fixture.Database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO episodes(
                    id,collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid,part_number)
                VALUES(9401,(SELECT id FROM collections WHERE name='Bennington'),'2026-08-15','High',
                       'Late inventory','Unplayed',$now,$now,'CONSOLIDATION-LATE',1);
                INSERT INTO media_files(
                    id,episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,
                    duration_ms,partial_hash,full_hash,storage_state,is_preferred)
                VALUES(9402,9401,$path,$filename,$bytes,$now,0,$now,3000,'late-partial',$full,'AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$path", latePath);
            command.Parameters.AddWithValue("$filename", Path.GetFileName(latePath));
            command.Parameters.AddWithValue("$bytes", new FileInfo(latePath).Length);
            command.Parameters.AddWithValue("$full", TestSha256(latePath));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        try
        {
            service.CreatePlan(Path.Combine(root, "managed-new"), Path.Combine(root, "quarantine-new"));
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains(
                   "safely consolidatable", StringComparison.OrdinalIgnoreCase))
        {
            // The deliberately artificial filenames in this duplicate-ranking
            // fixture may be held by the real parser. The refresh itself is the
            // contract under test here; production files remain safely held.
        }
        using (var connection = fixture.Database.OpenConnection())
        using (var refreshed = connection.CreateCommand())
        {
            refreshed.CommandText = "SELECT id,source_file_count FROM library_truth_runs WHERE status='completed' ORDER BY id DESC LIMIT 1";
            using var reader = refreshed.ExecuteReader();
            True(reader.Read());
            True(reader.GetInt64(0) > 9200,
                "Planning must automatically build a fresh non-destructive reconciliation when the inventory grew.");
            Equal(5L, reader.GetInt64(1));
        }

        var staleRehearsal = service.Rehearse(originalPlan);
        True(!staleRehearsal.CanCommit);
        True(staleRehearsal.Problems.Any(problem =>
            problem.Contains("inventory changed", StringComparison.OrdinalIgnoreCase)));
        True(File.Exists(latePath), "Inventory reconciliation must never move or delete a newly discovered file.");
        True(fixture.Sources.All(File.Exists));

        var hiddenPath = Path.Combine(root, "source", "hidden-unreconciled.mp3");
        File.WriteAllBytes(hiddenPath, Enumerable.Repeat((byte)0x5A, 96).ToArray());
        using (var connection = fixture.Database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO episodes(
                    id,collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid,part_number,hidden)
                VALUES(9403,(SELECT id FROM collections WHERE name='Bennington'),'2026-08-16','High',
                       'Hidden inventory','Unplayed',$now,$now,'CONSOLIDATION-HIDDEN',1,1);
                INSERT INTO media_files(
                    id,episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,
                    duration_ms,partial_hash,full_hash,storage_state,is_preferred)
                VALUES(9404,9403,$path,$filename,$bytes,$now,0,$now,3000,'hidden-partial',$full,'AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$path", hiddenPath);
            command.Parameters.AddWithValue("$filename", Path.GetFileName(hiddenPath));
            command.Parameters.AddWithValue("$bytes", new FileInfo(hiddenPath).Length);
            command.Parameters.AddWithValue("$full", TestSha256(hiddenPath));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        string stoppedMessage;
        try
        {
            service.CreatePlan(Path.Combine(root, "managed-hidden"), Path.Combine(root, "quarantine-hidden"));
            throw new InvalidOperationException("Expected hidden physical media outside Library Truth to stop plan creation.");
        }
        catch (InvalidOperationException exception)
        {
            stoppedMessage = exception.Message;
        }
        True(stoppedMessage.Contains("1 hidden", StringComparison.OrdinalIgnoreCase), stoppedMessage);
        True(stoppedMessage.Contains("No plan was created", StringComparison.OrdinalIgnoreCase) ||
             stoppedMessage.Contains("Run a fresh archive reconciliation", StringComparison.OrdinalIgnoreCase), stoppedMessage);
        True(File.Exists(hiddenPath));
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }
}

static void ScannerContentIdentityRejectsWeakCollisions()
{
    var root = Path.Combine(Path.GetTempPath(), "RadioVaultIdentityTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var database = new SqliteDatabase(Path.Combine(root, "identity.sqlite"));
        database.Initialize();
        int collectionId;
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO collections(name,sort_name) VALUES('Identity Audit','Identity Audit'); SELECT last_insert_rowid();";
            collectionId = Convert.ToInt32(command.ExecuteScalar());
        }
        var service = new DatabaseService(database);
        var parsed = new TheRadioVault.Core.Models.ParsedFilename
        {
            CollectionName = "Identity Audit",
            DateConfidence = "Unknown",
            PartNumber = 1
        };
        var first = service.UpsertScannedFile(
            Path.Combine(root, "partial-a.mp3"), 1000, DateTime.UtcNow, collectionId, parsed,
            partialHash: "same-outer-blocks", durationMs: 60_000);
        var collision = service.UpsertScannedFile(
            Path.Combine(root, "partial-b.mp3"), 1000, DateTime.UtcNow, collectionId, parsed,
            partialHash: "same-outer-blocks", durationMs: 90_000);
        True(first.EpisodeId != collision.EpisodeId,
            "Partial hash and byte size without matching duration must not merge recordings.");

        var exactOriginal = service.UpsertScannedFile(
            Path.Combine(root, "exact-old.mp3"), 1234, DateTime.UtcNow, collectionId, parsed,
            partialHash: "old-partial", fullHash: "abcdef0123456789", durationMs: 120_000);
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE media_files SET is_missing=1,is_preferred=0 WHERE episode_id=$episode";
            command.Parameters.AddWithValue("$episode", exactOriginal.EpisodeId);
            command.ExecuteNonQuery();
        }
        var exactReturn = service.UpsertScannedFile(
            Path.Combine(root, "exact-new-name.mp3"), 1234, DateTime.UtcNow, collectionId, parsed,
            partialHash: "different-partial", fullHash: "ABCDEF0123456789", durationMs: 120_000);
        Equal(exactOriginal.EpisodeId, exactReturn.EpisodeId);

        var datedA = new TheRadioVault.Core.Models.ParsedFilename
        {
            CollectionName = "Identity Audit",
            AirDate = new DateTime(2026, 1, 1),
            DateConfidence = "High",
            PartNumber = 1
        };
        var datedB = new TheRadioVault.Core.Models.ParsedFilename
        {
            CollectionName = "Identity Audit",
            AirDate = new DateTime(2026, 1, 2),
            DateConfidence = "High",
            PartNumber = 1
        };
        var conflictingClaimA = service.UpsertScannedFile(
            Path.Combine(root, "claim-a.mp3"), 2222, DateTime.UtcNow, collectionId, datedA,
            fullHash: "same-bytes-different-claim", durationMs: 180_000);
        var conflictingClaimB = service.UpsertScannedFile(
            Path.Combine(root, "claim-b.mp3"), 2222, DateTime.UtcNow, collectionId, datedB,
            fullHash: "same-bytes-different-claim", durationMs: 180_000);
        True(conflictingClaimA.EpisodeId != conflictingClaimB.EpisodeId,
            "Exact bytes with conflicting explicit dates must remain separate for Library Truth review.");
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }
}

static void ArchiveReconciliationIsServerOwnedAndReadOnly()
{
    var root = Path.Combine(Path.GetTempPath(), "RadioVaultReconciliationTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var database = new SqliteDatabase(Path.Combine(root, "reconciliation.sqlite"));
        database.Initialize();
        var service = new ArchiveReconciliationService(database);
        var initial = service.GetSnapshot();
        True(!initial.HasCompletedAnalysis);
        Equal("Not analysed", initial.AnalysisState);
        True(!service.GetAudit().Snapshot.HasCompletedAnalysis);

        var progressWasReported = false;
        var reconciled = service.Reconcile(new InlineProgress<(double Percent, string Message)>(_ => progressWasReported = true));
        True(reconciled.HasCompletedAnalysis);
        True(reconciled.AnalysisId > 0);
        Equal(0, reconciled.PhysicalFiles);
        Equal(reconciled.AnalysisId, service.GetSnapshot().AnalysisId);
        True(progressWasReported);
        var audit = service.GetAudit();
        True(audit.Snapshot.HasCompletedAnalysis);
        Equal(0, audit.ChangeBreakdown.InterpretedDifferentlyFiles);
        Equal(0, audit.YearDifferences.Count);
        Equal(0, audit.SplitCandidates.Count);
        Equal(0, audit.ReviewRecommended.Count);
        Equal(0, audit.Blocked.Count);
        var reportPath = Path.Combine(root, "reconciliation.trvreconcile.json");
        service.ExportReport(reportPath, "test-version");
        True(File.Exists(reportPath));
        var reportJson = File.ReadAllText(reportPath);
        True(reportJson.Contains("\"schemaVersion\": 6", StringComparison.Ordinal));
        True(reportJson.Contains("\"appVersion\": \"test-version\"", StringComparison.Ordinal));

        var serverRuntime = File.ReadAllText(Path.Combine(
            SourceRoot(), "TheRadioVault.Infrastructure", "Services", "RadioVaultServerRuntime.cs"));
        True(serverRuntime.Contains("GetArchiveReconciliationSnapshot", StringComparison.Ordinal));
        True(serverRuntime.Contains("GetArchiveReconciliationAudit", StringComparison.Ordinal));
        True(serverRuntime.Contains("ReconcileArchive", StringComparison.Ordinal));
        True(serverRuntime.Contains("ExportArchiveReconciliationReport", StringComparison.Ordinal));
        var desktopViews = Directory.GetFiles(
                Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views"), "*.axaml")
            .Select(File.ReadAllText);
        True(desktopViews.All(source => !source.Contains("RunArchiveReconciliationCommand", StringComparison.Ordinal)));
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

static void ArchiveContentIdentityIsStable()
{
    var exactA = ArchiveContentIdentity.Create("AABBCC", "partial-one", 100, 1_000, "machine-a:1");
    var exactB = ArchiveContentIdentity.Create("aabbcc", "partial-two", 200, 2_000, "machine-b:9");
    Equal("sha256:aabbcc", exactA);
    Equal(exactA, exactB);

    var strongA = ArchiveContentIdentity.Create(null, "OUTER", 1000, 60_000, "machine-a:1");
    var strongB = ArchiveContentIdentity.Create(null, "outer", 1000, 60_000, "machine-b:9");
    Equal(strongA, strongB);
    True(strongA != ArchiveContentIdentity.Create(null, "outer", 1000, 90_000, "machine-b:9"));
    Equal("local:machine-a:1", ArchiveContentIdentity.Create(null, null, 1000, 0, "machine-a:1"));
}

static (SqliteDatabase Database, string[] Sources) CreateMediaConsolidationFixture(string root)
{
    var sourceRoot = Path.Combine(root, "source");
    Directory.CreateDirectory(sourceRoot);
    var sources = new[]
    {
        Path.Combine(sourceRoot, "short.mp3"),
        Path.Combine(sourceRoot, "long-low-bitrate.mp3"),
        Path.Combine(sourceRoot, "long-high-bitrate.mp3"),
        Path.Combine(sourceRoot, "long-high-bitrate-copy.mp3")
    };
    File.WriteAllBytes(sources[0], Enumerable.Repeat((byte)0x11, 80).ToArray());
    File.WriteAllBytes(sources[1], Enumerable.Repeat((byte)0x22, 100).ToArray());
    var winnerBytes = Enumerable.Range(0, 200).Select(value => (byte)value).ToArray();
    File.WriteAllBytes(sources[2], winnerBytes);
    File.WriteAllBytes(sources[3], winnerBytes);
    var durations = new long[] { 1_000, 2_000, 2_000, 2_000 };
    var recordingKeys = new[] { "recording-short", "recording-long-low", "recording-winner", "recording-winner" };

    var database = new SqliteDatabase(Path.Combine(root, "consolidation.sqlite"));
    database.Initialize();
    using var connection = database.OpenConnection();
    using var transaction = connection.BeginTransaction();
    var now = DateTimeOffset.UtcNow.ToString("O");
    long collectionId;
    using (var collection = connection.CreateCommand())
    {
        collection.Transaction = transaction;
        collection.CommandText = "SELECT id FROM collections WHERE name='Bennington'";
        collectionId = Convert.ToInt64(collection.ExecuteScalar());
    }
    using (var setup = connection.CreateCommand())
    {
        setup.Transaction = transaction;
        setup.CommandText = """
            INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled) VALUES($root,$collection,1,1);
            INSERT INTO library_truth_runs(id,started_at,completed_at,status,parser_version,source_file_count,current_broadcast_count,proposed_broadcast_count)
            VALUES(9200,$now,$now,'completed','consolidation-test',4,4,1);
            INSERT INTO library_truth_broadcasts(
                run_id,canonical_key,collection_name,air_date,broadcast_slot,file_count,segment_count,
                recording_count,status,confidence_score,adoption_state,preferred_recording_key)
            VALUES(9200,'BENNINGTON-2026-08-14','Bennington','2026-08-14','',4,1,3,'Proposed changes',100,
                   'Ready with recording choice','recording-winner');
            """;
        setup.Parameters.AddWithValue("$root", sourceRoot);
        setup.Parameters.AddWithValue("$collection", collectionId);
        setup.Parameters.AddWithValue("$now", now);
        setup.ExecuteNonQuery();
    }

    for (var index = 0; index < sources.Length; index++)
    {
        var episodeId = 9101 + index;
        var mediaId = 9301 + index;
        using var row = connection.CreateCommand();
        row.Transaction = transaction;
        row.CommandText = """
            INSERT INTO episodes(
                id,collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid,part_number)
            VALUES($episode,$collection,'2026-08-14','High','Test broadcast','Unplayed',$now,$now,$uid,1);
            INSERT INTO media_files(
                id,episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,
                duration_ms,partial_hash,full_hash,storage_state,is_preferred)
            VALUES($media,$episode,$path,$filename,$bytes,$now,0,$now,$duration,$partial,$full,'AvailableOffline',1);
            INSERT INTO library_truth_files(
                run_id,media_file_id,current_episode_id,path,original_filename,current_collection,current_air_date,
                current_part,proposed_collection,proposed_air_date,proposed_part,proposed_headline,
                canonical_broadcast_key,recording_key,confidence_score,confidence,disposition)
            VALUES(9200,$media,$episode,$path,$filename,'Bennington','2026-08-14',1,'Bennington','2026-08-14',1,
                   'Test broadcast','BENNINGTON-2026-08-14',$recording,100,'High','Broadcast merge');
            """;
        row.Parameters.AddWithValue("$episode", episodeId);
        row.Parameters.AddWithValue("$collection", collectionId);
        row.Parameters.AddWithValue("$media", mediaId);
        row.Parameters.AddWithValue("$path", sources[index]);
        row.Parameters.AddWithValue("$filename", Path.GetFileName(sources[index]));
        row.Parameters.AddWithValue("$bytes", new FileInfo(sources[index]).Length);
        row.Parameters.AddWithValue("$duration", durations[index]);
        row.Parameters.AddWithValue("$partial", $"partial-{index}");
        row.Parameters.AddWithValue("$full", TestSha256(sources[index]));
        row.Parameters.AddWithValue("$recording", recordingKeys[index]);
        row.Parameters.AddWithValue("$uid", $"CONSOLIDATION-{index}");
        row.Parameters.AddWithValue("$now", now);
        row.ExecuteNonQuery();
    }

    foreach (var recording in recordingKeys.Distinct(StringComparer.Ordinal))
    {
        var memberIndexes = Enumerable.Range(0, recordingKeys.Length).Where(index => recordingKeys[index] == recording).ToArray();
        using var row = connection.CreateCommand();
        row.Transaction = transaction;
        row.CommandText = """
            INSERT INTO library_truth_recordings(
                run_id,canonical_broadcast_key,recording_key,label,file_count,segment_count,duration_ms,
                relationship,confidence_score,role,completeness_score,preferred_score,duration_ratio,is_preferred_candidate)
            VALUES(9200,'BENNINGTON-2026-08-14',$recording,$recording,$files,1,$duration,
                   'Alternate recording',100,'Complete alternate recording',100,$preferred,1.0,$winner);
            """;
        row.Parameters.AddWithValue("$recording", recording);
        row.Parameters.AddWithValue("$files", memberIndexes.Length);
        row.Parameters.AddWithValue("$duration", memberIndexes.Max(index => durations[index]));
        row.Parameters.AddWithValue("$preferred", recording == "recording-winner" ? 100 : 50);
        row.Parameters.AddWithValue("$winner", recording == "recording-winner" ? 1 : 0);
        row.ExecuteNonQuery();
    }
    transaction.Commit();
    return (database, sources);
}

static string TestSha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
}

static string ConsolidationTestTempRoot()
    => OperatingSystem.IsMacOS() && Directory.Exists("/private/tmp") ? "/private/tmp" : Path.GetTempPath();

static void ArchiveEntityLinksPreserveIdentity()
{
    var person = ArchiveEntityLinkFactory.ForPerson("Ron Bennington", "Host");
    Equal(ArchiveEntityKind.Person, person.Kind);
    Equal("ron-bennington", person.EntityId);
    Equal("host", person.Relationship);
    True(ArchiveEntityLinkFactory.TryParse(person.Route, out var personKind, out var personId, out var personTarget));
    Equal(ArchiveEntityKind.Person, personKind);
    Equal(person.EntityId, personId);
    Equal(person.TargetId, personTarget);

    var broadcast = ArchiveEntityLinkFactory.ForBroadcast("BENNINGTON-2016-05-17", 42, "A broadcast");
    True(ArchiveEntityLinkFactory.TryParse(broadcast.Route, out var broadcastKind, out var broadcastId, out var episodeId));
    Equal(ArchiveEntityKind.Broadcast, broadcastKind);
    Equal("BENNINGTON-2016-05-17", broadcastId);
    Equal("42", episodeId);
    Equal("broadcast:BENNINGTON-2016-05-17", broadcast.EntityKey);

    var show = ArchiveEntityLinkFactory.ForShow(7, "Bennington");
    var showTarget = ArchiveEntityNavigation.Resolve(show);
    Equal(ArchiveEntityDestination.LibraryShow, showTarget.Destination);
    Equal("7", showTarget.TargetId);
    Equal("Bennington", showTarget.Label);

    var broadcastTarget = ArchiveEntityNavigation.Resolve(broadcast);
    Equal(ArchiveEntityDestination.Broadcast, broadcastTarget.Destination);
    Equal("42", broadcastTarget.TargetId);

    var personNavigation = ArchiveEntityNavigation.Resolve(person);
    Equal(ArchiveEntityDestination.Explore, personNavigation.Destination);
    Equal(ArchiveEntityDestination.LibraryShow, ArchiveEntityNavigation.Resolve(show).Destination);

    var showArticle = ArchiveEntityLinkFactory.ForWikiPage(
        Guid.Parse("b8dd3ea4-f5f1-4ddd-bfb5-1fd29619757d"), "Show", "Bennington history") with
    {
        Relationship = "inline"
    };
    var showArticleNavigation = ArchiveEntityNavigation.Resolve(showArticle);
    Equal(ArchiveEntityDestination.Explore, showArticleNavigation.Destination);
    Equal("b8dd3ea4-f5f1-4ddd-bfb5-1fd29619757d", showArticleNavigation.TargetId);
    Equal("Ron Bennington", personNavigation.Label);
}

static void CanonicalPersonalStateWritesRollBackAtomically()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var database = new SqliteDatabase(Path.Combine(directory, "personal-state.sqlite"));
        database.Initialize();
        using (var connection = database.OpenConnection())
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Personal State Test','Personal State Test');
                INSERT INTO episodes(id,collection_id,title,status,date_added,updated_at,broadcast_uid,hidden)
                VALUES(8201,(SELECT id FROM collections WHERE name='Personal State Test'),'Survivor','Unplayed',$now,$now,'STATE-A',0);
                INSERT INTO episodes(id,collection_id,title,status,date_added,updated_at,broadcast_uid,hidden)
                VALUES(8202,(SELECT id FROM collections WHERE name='Personal State Test'),'Retained member','Unplayed',$now,$now,'STATE-B',1);

                INSERT INTO library_truth_runs(
                    id,started_at,completed_at,status,parser_version,source_file_count,current_broadcast_count,proposed_broadcast_count)
                VALUES(8203,$now,$now,'completed','personal-state-test',2,2,1);
                INSERT INTO library_truth_rehearsal_runs(
                    id,truth_run_id,started_at,completed_at,status,rollback_verified)
                VALUES(8204,8203,$now,$now,'completed',1);
                INSERT INTO canonical_broadcasts(
                    canonical_key,collection_name,source_truth_run_id,adopted_at)
                VALUES('PERSONAL-STATE-TEST','Personal State Test',8203,$now);
                INSERT INTO episode_canonical_map(
                    episode_id,canonical_key,survivor_episode_id,is_survivor,source_truth_run_id,adopted_at)
                VALUES(8201,'PERSONAL-STATE-TEST',8201,1,8203,$now);
                INSERT INTO episode_canonical_map(
                    episode_id,canonical_key,survivor_episode_id,is_survivor,source_truth_run_id,adopted_at)
                VALUES(8202,'PERSONAL-STATE-TEST',8201,0,8203,$now);
                INSERT INTO library_truth_adoption_runs(
                    truth_run_id,rehearsal_run_id,app_version,started_at,completed_at,status,
                    commit_verified,foreign_key_violations,integrity_check)
                VALUES(8203,8204,'test',$now,$now,'completed',1,0,'ok');

                CREATE TRIGGER fail_second_personal_state
                BEFORE INSERT ON playback_state
                WHEN NEW.episode_id=8202
                BEGIN
                    SELECT RAISE(ABORT,'forced personal state failure');
                END;
                """;
            setup.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            setup.ExecuteNonQuery();
        }

        var service = new DatabaseService(database);
        Throws<Microsoft.Data.Sqlite.SqliteException>(() => service.SavePlaybackState(
            8202,
            positionMs: 120_000,
            durationMs: 180_000,
            completed: false,
            playbackSpeed: 1.25,
            incrementPlayCount: true));

        using (var connection = database.OpenConnection())
        {
            using (var rolledBack = connection.CreateCommand())
            {
                rolledBack.CommandText = "SELECT COUNT(*) FROM playback_state WHERE episode_id IN (8201,8202)";
                Equal(0L, Convert.ToInt64(rolledBack.ExecuteScalar()));
            }
            using (var statuses = connection.CreateCommand())
            {
                statuses.CommandText = "SELECT COUNT(*) FROM episodes WHERE id IN (8201,8202) AND status='Unplayed'";
                Equal(2L, Convert.ToInt64(statuses.ExecuteScalar()));
            }
            using var removeTrigger = connection.CreateCommand();
            removeTrigger.CommandText = "DROP TRIGGER fail_second_personal_state";
            removeTrigger.ExecuteNonQuery();
        }

        var playedAt = new DateTimeOffset(2026, 8, 12, 18, 30, 0, TimeSpan.Zero);
        service.SavePlaybackState(
            8202,
            positionMs: 120_000,
            durationMs: 180_000,
            completed: false,
            playbackSpeed: 1.25,
            incrementPlayCount: true,
            playedAt: playedAt);

        var state = service.GetPlaybackState(8201);
        Equal(120_000L, state.PositionMs);
        Equal(180_000L, state.DurationMs);
        Equal(1, state.PlayCount);
        Equal(1.25, state.PlaybackSpeed);
        Equal(playedAt.UtcDateTime, state.LastPlayedAt!.Value.ToUniversalTime());
        True(!state.Completed);

        using (var connection = database.OpenConnection())
        using (var verify = connection.CreateCommand())
        {
            verify.CommandText = """
                SELECT COUNT(*),MIN(position_ms),MAX(position_ms),SUM(play_count),
                       SUM(CASE WHEN completed=0 THEN 1 ELSE 0 END)
                  FROM playback_state
                 WHERE episode_id IN (8201,8202)
                """;
            using var reader = verify.ExecuteReader();
            True(reader.Read());
            Equal(2, reader.GetInt32(0));
            Equal(120_000L, reader.GetInt64(1));
            Equal(120_000L, reader.GetInt64(2));
            Equal(2, reader.GetInt32(3));
            Equal(2, reader.GetInt32(4));
        }

        service.SavePlaybackState(8201, 0, 180_000, false, 1.25);
        Equal(120_000L, service.GetPlaybackState(8202).PositionMs);

        service.MarkCompleted(8202, completed: true);
        True(service.GetPlaybackState(8201).Completed);
        Equal(180_000L, service.GetPlaybackState(8201).PositionMs);
        service.SetFavourite(8201, favourite: true);

        using (var connection = database.OpenConnection())
        using (var canonicalState = connection.CreateCommand())
        {
            canonicalState.CommandText = """
                SELECT COUNT(*)
                  FROM episodes e
                  JOIN playback_state ps ON ps.episode_id=e.id
                 WHERE e.id IN (8201,8202)
                   AND e.favourite=1
                   AND e.status='Completed'
                   AND ps.completed=1
                   AND ps.position_ms=180000
                """;
            Equal(2L, Convert.ToInt64(canonicalState.ExecuteScalar()));
        }

        service.MarkCompleted(8201, completed: false);
        var reset = service.GetPlaybackState(8202);
        True(!reset.Completed);
        Equal(0L, reset.PositionMs);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void DesktopPlaybackStateMachinePreservesPendingIntent()
{
    var state = new DesktopPlaybackStateMachine();
    True(state.SetLoaded(true));
    state.BeginTransport(desiredPlaying: true);
    True(state.TransportPending);
    True(state.DesiredPlaying);
    True(!state.TransportIntentChanged);

    True(state.TogglePendingTransportIntent());
    True(!state.DesiredPlaying);
    True(state.TransportIntentChanged);
    state.AdoptObservedDesiredPlayback(desiredPlaying: true);
    True(!state.DesiredPlaying);

    True(state.ObserveLocalPlayback(true));
    True(state.IsPlaying);
    True(!state.DesiredPlaying);
    state.CompleteTransport();
    True(!state.TransportPending);
    True(state.DesiredPlaying);
    True(!state.TransportIntentChanged);

    state.ReleaseForRemoteHandoff();
    True(!state.DesiredPlaying);
    True(!state.TransportIntentChanged);
}

static void DesktopRemoteProgressInterpolationRemovesHeartbeatWiggle()
{
    var interpolator = new RemotePlaybackProgressInterpolator();
    Equal(10_000L, interpolator.Project(42, 7, "iphone", true, 10_000));
    Equal(10_000L, interpolator.Project(42, 7, "iphone", true, 9_500));
    Equal(11_000L, interpolator.Project(42, 7, "iphone", true, 11_000));

    // A large backwards correction is an intentional remote seek.
    Equal(6_000L, interpolator.Project(42, 7, "iphone", true, 6_000));

    // Ownership and broadcast changes establish fresh baselines immediately.
    Equal(4_000L, interpolator.Project(42, 8, "mac", true, 4_000));
    Equal(1_000L, interpolator.Project(99, 8, "mac", true, 1_000));
}


static void NewShowAliasesNormalize()
{
    Equal("The Ron & Ron Show", MetadataNormalizer.NormalizeCollection("Ron and Ron Show"));
    Equal("The Ron & Ron Show", MetadataNormalizer.NormalizeCollection("Ron-&-Ron Show"));
    Equal("Unmasked", MetadataNormalizer.NormalizeCollection("SiriusXM Unmasked"));
    Equal("Ron Bennington Interviews", MetadataNormalizer.NormalizeCollection("Ron Bennington Interview"));
    Equal("Ron Bennington Interviews", MetadataNormalizer.NormalizeCollection("RonBenningtonInterviews"));
    Equal("Bennington", MetadataNormalizer.NormalizeCollection("Ron Bennington"));
}

static void ParserRecognisesNewShowTypes()
{
    var parser = new FilenameParserService();

    var ronRon = parser.Parse(@"E:\Radio\The Ron & Ron Show\The Ron and Ron Show 4-5-1995.mp3");
    Equal("The Ron & Ron Show", ronRon.CollectionName);
    Equal(new DateTime(1995, 4, 5), ronRon.AirDate!.Value);
    True(string.IsNullOrWhiteSpace(ronRon.HeadlineCandidate));

    var unmasked = parser.Parse(@"E:\Radio\Unmasked\Unmasked 10-12-2011 Louis CK.mp3");
    Equal("Unmasked", unmasked.CollectionName);
    Equal(new DateTime(2011, 10, 12), unmasked.AirDate!.Value);
    Equal("Louis CK", unmasked.HeadlineCandidate);

    var interviews = parser.Parse(@"E:\Radio\Ron Bennington Interviews\Ron Bennington Interview 2018-03-09 Judd Apatow.mp3");
    Equal("Ron Bennington Interviews", interviews.CollectionName);
    Equal(new DateTime(2018, 3, 9), interviews.AirDate!.Value);
    Equal("Judd Apatow", interviews.HeadlineCandidate);
}

static void LibraryTruthRecognisesNewShowTypes()
{
    Equal("The Ron & Ron Show", LibraryTruthParser.DetectExplicitCollection("The Ron & Ron Show 1997-01-14"));
    Equal("Unmasked", LibraryTruthParser.DetectExplicitCollection(@"Unmasked\2012\Unmasked 2012-06-01"));
    Equal("Ron Bennington Interviews", LibraryTruthParser.DetectExplicitCollection("Ron Bennington Interviews 2019-04-22"));
}

static void LibrarySearchFindsTranscriptSpeech()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var database = new SqliteDatabase(Path.Combine(directory, "search.sqlite"));
        database.Initialize();
        long episodeId;
        using (var connection = database.OpenConnection())
        {
            using var collection = connection.CreateCommand();
            collection.CommandText = "SELECT id FROM collections WHERE name='Bennington'";
            var collectionId = Convert.ToInt64(collection.ExecuteScalar());
            using var episode = connection.CreateCommand();
            episode.CommandText = "INSERT INTO episodes(collection_id,air_date,title,status,date_added,updated_at) VALUES($collection,'2025-03-14','Ordinary episode','Unplayed',$now,$now); SELECT last_insert_rowid();";
            episode.Parameters.AddWithValue("$collection", collectionId);
            episode.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            episodeId = Convert.ToInt64(episode.ExecuteScalar());
        }
        new SqliteTranscriptRepository(database).SaveAsync(new TranscriptDocument
        {
            EpisodeId = episodeId,
            FullText = "A discussion about phosphorescent penguins in radio.",
            Segments = new[] { new TranscriptSegment(0, 90_000, 94_000, "A discussion about phosphorescent penguins in radio.") }
        }).GetAwaiter().GetResult();

        var result = new LibraryBrowseService(database).BrowseAsync(new TheRadioVault.Services.Models.LibraryBrowseRequest(
            SearchText: "phosphorescent penguins",
            SearchScope: TheRadioVault.Services.Models.LibrarySearchScope.Transcripts)).GetAwaiter().GetResult();
        Equal(1, result.TotalMatching);
        True(result.Broadcasts.Single().SearchContext.StartsWith("Transcript · 1:30:", StringComparison.Ordinal));
        Equal(90_000L, result.Broadcasts.Single().SearchStartMs);
        var titlesOnly = new LibraryBrowseService(database).BrowseAsync(new TheRadioVault.Services.Models.LibraryBrowseRequest(
            SearchText: "phosphorescent penguins",
            SearchScope: TheRadioVault.Services.Models.LibrarySearchScope.TitlesAndSummaries)).GetAwaiter().GetResult();
        Equal(0, titlesOnly.TotalMatching);
        var transcriptFacet = new LibraryBrowseService(database).BrowseAsync(new TheRadioVault.Services.Models.LibraryBrowseRequest(
            HasTranscript: true)).GetAwaiter().GetResult();
        Equal(1, transcriptFacet.TotalMatching);
        var facets = new LibraryBrowseService(database).GetSearchFacetsAsync().GetAwaiter().GetResult();
        Equal(1, facets.TranscriptCount);
        True(facets.Years.Contains(2025));
        var suggestions = new LibraryBrowseService(database).GetSearchSuggestionsAsync("Ben").GetAwaiter().GetResult();
        True(suggestions.Any(x => x.Kind == "Show" && x.Value == "Bennington"));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void ShowProjectionsCombineLegacyCollectionAliases()
{
    var path = Path.Combine(Path.GetTempPath(), $"radiovault-show-family-{Guid.NewGuid():N}.db");
    try
    {
        var database = new SqliteDatabase(path);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var setup = connection.CreateCommand();
            setup.CommandText = """
                INSERT INTO collections(name,sort_name) VALUES('Opie and Anthony','Opie and Anthony Legacy');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,broadcast_uid,date_added,updated_at)
                VALUES((SELECT id FROM collections WHERE name='Opie and Anthony'),'2001-09-17','High','Legacy O&A','LEGACY-OA',$now,$now);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='LEGACY-OA'),'C:\Radio\legacy-oa.mp3','legacy-oa.mp3',1000,$now,0,$now,'AvailableOffline',1);
                """;
            setup.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            setup.ExecuteNonQuery();

            using var canonical = connection.CreateCommand();
            canonical.CommandText = "SELECT id FROM collections WHERE name='Opie & Anthony'";
            var canonicalId = Convert.ToInt32(canonical.ExecuteScalar(), CultureInfo.InvariantCulture);
            var family = CollectionIdentityResolver.ResolveFamily(connection, canonicalId);
            True(family is not null);
            Equal(KnownShowCatalog.OpieAnthony, family!.CanonicalName);
            Equal(2, family.CollectionIds.Count);
        }

        var researchCollections = new ResearchWorkspaceService(database)
            .GetCollectionsAsync().GetAwaiter().GetResult();
        Equal(1, researchCollections.Count(x => x.Name == KnownShowCatalog.OpieAnthony));
        True(researchCollections.Any(x => x.Name == KnownShowCatalog.OpieAnthony && x.RecordCount == 1));

        var libraryCollections = new LibraryBrowseService(database)
            .GetOverviewAsync().GetAwaiter().GetResult().Collections;
        Equal(1, libraryCollections.Count(x => x.CollectionName == KnownShowCatalog.OpieAnthony));
        True(libraryCollections.Any(x => x.CollectionName == KnownShowCatalog.OpieAnthony && x.BroadcastCount == 1));
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
        if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
    }
}

static void AvaloniaSidebarHidesEmptyShowSections()
{
    var source = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Services", "Services", "LibraryBrowseService.cs"));
    True(source.Contains("countsByShow", StringComparison.Ordinal));
    True(source.Contains("CollectionIdentityResolver.Canonicalize", StringComparison.Ordinal));
}

static void ParserAcceptsCatalogueStyleInterviewFilenames()
{
    var parser = new FilenameParserService();

    var rbi = parser.Parse(@"E:\Radio\Ron Bennington Interviews\rbi_aimee_mann.mp3");
    Equal(KnownShowCatalog.RonBenningtonInterviews, rbi.CollectionName);
    Equal("Aimee Mann", rbi.HeadlineCandidate);
    True(!rbi.AirDate.HasValue);

    var rbiYear = parser.Parse(@"E:\Radio\Ron Bennington Interviews\rbi_adam_resnick_2015.mp3");
    Equal("Adam Resnick (2015)", rbiYear.HeadlineCandidate);

    var unmasked = parser.Parse(@"E:\Radio\Unmasked\unmasked_bill_burr_2015.mp3");
    Equal(KnownShowCatalog.Unmasked, unmasked.CollectionName);
    Equal("Bill Burr (2015)", unmasked.HeadlineCandidate);

    var townHall = parser.Parse(@"E:\Radio\Unmasked\Town-Hall-Joel-McHale.mp3");
    Equal("Joel McHale", townHall.HeadlineCandidate);

    var ronRon = parser.Parse(@"E:\Radio\The Ron & Ron Show\Ron & Ron Show - Affairs (1997).mp3");
    Equal("Affairs (1997)", ronRon.HeadlineCandidate);
}


static void FolderAssignmentDrivesCatalogueParser()
{
    var parser = new FilenameParserService();
    var context = new TheRadioVault.Core.Models.FilenameParseContext(
        false,
        "Explicit folder assignment test.",
        KnownShowCatalog.Unmasked);
    var parsed = parser.Parse(@"E:\Radio\Miscellaneous Interviews\Town-Hall-Melissa-McCarthy-1.wav", context);
    Equal(KnownShowCatalog.Unmasked, parsed.CollectionName);
    Equal("Melissa McCarthy", parsed.HeadlineCandidate);
    True(!parsed.CollectionDetectedFromFilename);
}

static void CatalogueShowsUseCompleteListView()
{
    var source = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "LibraryViewModel.cs"));
    True(source.Contains("!IsCatalogueCollection", StringComparison.Ordinal));
    True(source.Contains("including items whose original file has no broadcast date", StringComparison.Ordinal));
}

static void CanonicalPromotionAcceptsUndatedCatalogueItems()
{
    True(KnownShowCatalog.SupportsUndatedCatalogueItems(KnownShowCatalog.RonRon));
    True(KnownShowCatalog.SupportsUndatedCatalogueItems(KnownShowCatalog.RonBenningtonInterviews));
    True(KnownShowCatalog.SupportsUndatedCatalogueItems(KnownShowCatalog.Unmasked));
    True(!KnownShowCatalog.SupportsUndatedCatalogueItems(KnownShowCatalog.RonFez));

    var source = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Services", "Services", "CanonicalScanPromotionService.cs"));
    True(source.Contains("SupportsUndatedCatalogueItems", StringComparison.Ordinal));
    True(source.Contains("even when", StringComparison.Ordinal));
    True(!source.Contains("TitleQualityService.IsMeaningful(x.Headline", StringComparison.Ordinal));
}

static void CatalogueResearchFieldsFlowThroughDesktopAndAnywhere()
{
    var packModels = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Infrastructure", "Models", "Models.cs"));
    True(packModels.Contains("TrvPackCatalogueMetadata", StringComparison.Ordinal));
    True(packModels.Contains("OriginalReleaseDate", StringComparison.Ordinal));
    True(packModels.Contains("ResearchNotes", StringComparison.Ordinal));

    var workspace = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Services", "Services", "ResearchWorkspaceService.cs"));
    True(workspace.Contains("research[\"catalogue\"]", StringComparison.Ordinal));
    True(workspace.Contains("catalogue[\"original_filename\"]", StringComparison.Ordinal));

    var researchView = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "ResearchWorkspaceView.axaml"));
    True(researchView.Contains("EditorOriginalReleaseDate", StringComparison.Ordinal));
    True(researchView.Contains("EditorProvenance", StringComparison.Ordinal));

    var webModels = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Web", "Models", "WebModels.cs"));
    True(webModels.Contains("CatalogueFields", StringComparison.Ordinal));
}

static void AvaloniaSettingsEventsUseUiDispatcher()
{
    var tools = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "DesktopToolsViewModel.cs"));
    var access = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "ConnectedAccessViewModel.cs"));
    True(tools.Contains("ApplyAnywhereSnapshotOnUiAsync", StringComparison.Ordinal));
    True(tools.Contains("_dispatcher.InvokeAsync(() => AnywhereSnapshot = snapshot)", StringComparison.Ordinal));
    True(access.Contains("ApplySnapshotOnUiAsync", StringComparison.Ordinal));
    True(access.Contains("_dispatcher!.InvokeAsync", StringComparison.Ordinal));
    True(!tools.Contains("=> AnywhereSnapshot = snapshot;", StringComparison.Ordinal));
}

static void AvaloniaSettingsExplainsServerOwnership()
{
    var settingsView = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "DesktopToolsView.axaml"));
    True(settingsView.Contains("CURRENT SERVER", StringComparison.Ordinal));
    True(settingsView.Contains("Use this computer", StringComparison.Ordinal));
    True(settingsView.Contains("It will not list the server already running on this computer", StringComparison.Ordinal));
    True(settingsView.Contains("Radio Vault Web", StringComparison.Ordinal));
    True(settingsView.Contains("active server hosts it", StringComparison.Ordinal));
}

static void MacClientRemainsUsableBeforeServerPairing()
{
    var preferences = new NativeServerConnectionPreferences();
    using var access = new NativeConnectedAccessService(preferences, isRemoteSession: true);
    Equal(ConnectedAccessState.Disconnected, access.Current.State);

    var host = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Composition", "AvaloniaApplicationHost.cs"));
    True(host.Contains("startupPlan.IsRemoteClient || OperatingSystem.IsMacOS()", StringComparison.Ordinal));

    var app = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "App.axaml.cs"));
    True(app.Contains("eventArgs.Handled = true", StringComparison.Ordinal));

    var tools = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "DesktopToolsViewModel.cs"));
    var initializeConnection = tools.IndexOf("await ConnectedAccess.InitializeAsync()", StringComparison.Ordinal);
    var loadFolders = tools.IndexOf("await _folders.GetAllAsync()", StringComparison.Ordinal);
    True(initializeConnection >= 0 && loadFolders > initializeConnection);
    True(tools.Contains("SelectSectionByKey(\"connected\")", StringComparison.Ordinal));

    var shell = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "MainWindowViewModel.cs"));
    True(shell.Contains("NavigateToCoreAsync", StringComparison.Ordinal));
    True(shell.Contains("This view needs a Radio Vault Server", StringComparison.Ordinal));
    True(shell.Contains("HasNavigationError", StringComparison.Ordinal));

    var mainWindow = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "MainWindow.axaml"));
    True(mainWindow.Contains("x:Name=\"MacWindowControls\"", StringComparison.Ordinal));
    True(mainWindow.Contains("x:Name=\"WindowsWindowControls\" HorizontalAlignment=\"Right\"", StringComparison.Ordinal));
    True(mainWindow.Contains("Fill=\"#FF5F57\"", StringComparison.Ordinal));
    True(mainWindow.Contains("Fill=\"#FEBC2E\"", StringComparison.Ordinal));
    True(mainWindow.Contains("Fill=\"#28C840\"", StringComparison.Ordinal));
    True(mainWindow.Contains("ContentTemplate=\"{StaticResource SkipBackIconTemplate}\"", StringComparison.Ordinal));
    True(mainWindow.Contains("ContentTemplate=\"{StaticResource SkipForwardIconTemplate}\"", StringComparison.Ordinal));
    True(!mainWindow.Contains("Content=\"−15\"", StringComparison.Ordinal));
    True(!mainWindow.Contains("Content=\"+30\"", StringComparison.Ordinal));
    var macControls = mainWindow[mainWindow.IndexOf("x:Name=\"MacWindowControls\"", StringComparison.Ordinal)..];
    var closeButton = macControls.IndexOf("Click=\"CloseButton_OnClick\"", StringComparison.Ordinal);
    var minimizeButton = macControls.IndexOf("Click=\"MinimizeButton_OnClick\"", StringComparison.Ordinal);
    var zoomButton = macControls.IndexOf("Click=\"MaximizeButton_OnClick\"", StringComparison.Ordinal);
    True(closeButton >= 0 && minimizeButton > closeButton && zoomButton > minimizeButton);

    var mainWindowCode = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "MainWindow.axaml.cs"));
    True(mainWindowCode.Contains("var useMacWindowControls = OperatingSystem.IsMacOS()", StringComparison.Ordinal));
    True(mainWindowCode.Contains("WindowsWindowControls.IsVisible = !useMacWindowControls", StringComparison.Ordinal));

    var appView = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "App.axaml"));
    True(appView.Contains("Name=\"Radio Vault\"", StringComparison.Ordinal));
    True(appView.Contains("Header=\"About Radio Vault…\"", StringComparison.Ordinal));
    True(appView.Contains("Header=\"Settings…\" Gesture=\"Meta+OemComma\"", StringComparison.Ordinal));

    foreach (var menuHeader in new[] { "File", "Edit", "View", "Window", "Help" })
        True(mainWindow.Contains($"Header=\"{menuHeader}\"", StringComparison.Ordinal));
    True(mainWindow.Contains("Header=\"Search Radio Vault\"", StringComparison.Ordinal));
    True(mainWindow.Contains("Header=\"Open Diagnostics Folder\"", StringComparison.Ordinal));
    True(mainWindowCode.Contains("FindFocusedTextBox", StringComparison.Ordinal));
    True(mainWindowCode.Contains("ViewNativeMenu_OnNeedsUpdate", StringComparison.Ordinal));

    var aboutView = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "AboutWindow.axaml"));
    True(aboutView.Contains("Title=\"About Radio Vault\"", StringComparison.Ordinal));
    True(aboutView.Contains("Text=\"RADIO VAULT\"", StringComparison.Ordinal));
    True(aboutView.Contains("Copyright © 2026 Radio Vault", StringComparison.Ordinal));

    var macPlist = File.ReadAllText(Path.Combine(SourceRoot(), "installer", "macos", "Info.plist"));
    True(macPlist.Contains("<string>Radio Vault</string>", StringComparison.Ordinal));
    True(macPlist.Contains("Copyright © 2026 Radio Vault", StringComparison.Ordinal));
}

static void Alpha12CompletesServerOwnershipAndStatusUx()
{
    var host = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Composition", "AvaloniaApplicationHost.cs"));
    True(host.Contains("LoopbackServerLibraryFolderService", StringComparison.Ordinal));
    True(host.Contains("LoopbackServerArchiveHealthService", StringComparison.Ordinal));
    True(host.Contains("IServerTranscriptionAdministrationService", StringComparison.Ordinal));
    True(!host.Contains("new SqliteDatabase", StringComparison.Ordinal));
    True(!host.Contains("AvaloniaArchiveBackupService", StringComparison.Ordinal));

    var transcriptionServices = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Infrastructure", "Services", "LoopbackTranscriptionServices.cs"));
    True(transcriptionServices.Contains("ServerOwnedTranscriptionEngine", StringComparison.Ordinal));
    True(transcriptionServices.Contains("install-recommended", StringComparison.Ordinal));

    var serverView = ReadServerAdministrationViews();
    True(serverView.Contains("IsVisible=\"{Binding IsServerStopped}\"", StringComparison.Ordinal));
    True(serverView.Contains("IsVisible=\"{Binding IsServerRunning}\"", StringComparison.Ordinal));
    True(serverView.Contains("TranscriptionStateBrush", StringComparison.Ordinal));

    var clientStyles = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "App.axaml"));
    True(clientStyles.Contains("AllowAutoHide", StringComparison.Ordinal));

    var transcriptsView = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "TranscriptsView.axaml"));
    True(!transcriptsView.Contains("RvTranscriptBrush", StringComparison.Ordinal));
    True(!transcriptsView.Contains("RvTranscriptSubtleBrush", StringComparison.Ordinal));
}

static void Alpha13CompletesRemoteClientMonitoringAndServerInstaller()
{
    var accessService = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Infrastructure", "Services", "NativeConnectedAccessService.cs"));
    True(accessService.Contains("MonitorRemoteConnectionAsync", StringComparison.Ordinal));
    True(accessService.Contains("_preferences.UseRemoteOnStartup = true", StringComparison.Ordinal));
    True(accessService.Contains("CachedReadOnly", StringComparison.Ordinal));
    True(accessService.Contains("nextReconnectAt", StringComparison.Ordinal));
    True(accessService.Contains("PublishHealthyConnection", StringComparison.Ordinal));

    var settingsView = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "DesktopToolsView.axaml"));
    True(settingsView.Contains("Pair and use server", StringComparison.Ordinal));
    True(settingsView.Contains("ConnectionStateBrush", StringComparison.Ordinal));
    True(settingsView.Contains("ReconnectScheduleText", StringComparison.Ordinal));

    var installer = File.ReadAllText(Path.Combine(SourceRoot(), "installer", "RadioVault.Server.iss"));
    True(installer.Contains("PrivilegesRequired=lowest", StringComparison.Ordinal));
    True(installer.Contains("Start Radio Vault Server automatically", StringComparison.Ordinal));
    True(installer.Contains("uninsdeletevalue", StringComparison.Ordinal));
    True(installer.Contains("RadioVault.Server.exe", StringComparison.Ordinal));

    True(File.Exists(Path.Combine(SourceRoot(), "package-server-installer.ps1")));
    True(File.Exists(Path.Combine(SourceRoot(), "docs/history/release-notes/V0.34.0-ALPHA13-REMOTE-CLIENT-INSTALLER.md")));
}

static void Alpha13Buildfix1RestoresFoldersListeningActionsAndDualInstallers()
{
    var serverRuntime = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Infrastructure", "Services", "RadioVaultServerRuntime.cs"));
    True(serverRuntime.Contains("AddLibraryFolderAsync", StringComparison.Ordinal));
    True(serverRuntime.Contains("SetLibraryFolderEnabledAsync", StringComparison.Ordinal));
    True(serverRuntime.Contains("RemoveLibraryFolderAsync", StringComparison.Ordinal));
    True(serverRuntime.Contains("ScanLibraryAsync", StringComparison.Ordinal));

    var serverView = ReadServerAdministrationViews();
    True(serverView.Contains("LIBRARY FOLDERS", StringComparison.Ordinal));
    True(serverView.Contains("AddLibraryFolderCommand", StringComparison.Ordinal));
    True(serverView.Contains("RemoveLibraryFolderCommand", StringComparison.Ordinal));
    True(serverView.Contains("ScanLibraryCommand", StringComparison.Ordinal));

    var libraryView = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "LibraryView.axaml"));
    True(libraryView.Contains("Mark as listened", StringComparison.Ordinal));
    True(libraryView.Contains("Mark as unlistened", StringComparison.Ordinal));

    var actions = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Services", "Contracts", "ILibraryActionService.cs"));
    var loopbackActions = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Infrastructure", "Services", "LoopbackUserStateServices.cs"));
    True(actions.Contains("SetPlayedAsync", StringComparison.Ordinal));
    True(loopbackActions.Contains("WebApiRoutes.ListeningStatus", StringComparison.Ordinal));

    var clientInstaller = File.ReadAllText(Path.Combine(SourceRoot(), "installer", "RadioVault.Client.iss"));
    True(clientInstaller.Contains("Radio Vault Client", StringComparison.Ordinal));
    True(clientInstaller.Contains("TheRadioVault.exe", StringComparison.Ordinal));
    True(File.Exists(Path.Combine(SourceRoot(), "package-client-installer.ps1")));
    True(File.Exists(Path.Combine(SourceRoot(), "docs/history/release-notes/V0.34.0-ALPHA13-BUILDFIX1-FOLDERS-LISTENING-INSTALLERS.md")));
}

static void Alpha14RenamesWebAndRestoresPhoneConnectionControls()
{
    const string privateUrl = "https://192.168.1.10:8766/?token=example-private-token";
    var qr = PhoneQrCode.Create(privateUrl);
    True(qr.IsAvailable);
    True(qr.Rows.Count >= 21);
    Equal(qr.Rows.Count, qr.Rows[0].Cells.Count);
    True(qr.Rows.SelectMany(row => row.Cells).Any(cell => cell.Fill == "#101318"));
    True(qr.Rows.SelectMany(row => row.Cells).Any(cell => cell.Fill == "#FFFFFF"));

    var webServer = ReadWebServerSourceBundle();
    True(webServer.Contains("productName = \"Radio Vault Web\"", StringComparison.Ordinal));
    True(webServer.Contains("accessUrl = GetAccessUrls().FirstOrDefault()", StringComparison.Ordinal));
    True(webServer.Contains("secureSetupUrl = GetSecureSetupUrls().FirstOrDefault()", StringComparison.Ordinal));
    True(webServer.Contains("<title>Radio Vault Web</title>", StringComparison.Ordinal));
    True(webServer.Contains("radio-vault-anywhere-shell-v68", StringComparison.Ordinal));

    var clientAdapter = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Anywhere", "DedicatedServerRadioVaultAnywhereService.cs"));
    True(clientAdapter.Contains("response.Web?.AccessUrl", StringComparison.Ordinal));
    True(clientAdapter.Contains("response.Web?.SecureSetupUrl", StringComparison.Ordinal));

    var clientSettings = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "DesktopToolsView.axaml"));
    True(clientSettings.Contains("Connect a phone to Radio Vault Web", StringComparison.Ordinal));
    True(clientSettings.Contains("AnywhereQrCode.Rows", StringComparison.Ordinal));
    True(clientSettings.Contains("AnywhereSetupQrCode.Rows", StringComparison.Ordinal));

    var serverSettings = ReadServerAdministrationViews();
    True(serverSettings.Contains("RADIO VAULT WEB", StringComparison.Ordinal));
    True(serverSettings.Contains("CopyWebLinkCommand", StringComparison.Ordinal));
    True(serverSettings.Contains("RegenerateWebLinkCommand", StringComparison.Ordinal));
    True(serverSettings.Contains("WebQrCode.Rows", StringComparison.Ordinal));
    True(serverSettings.Contains("SecureSetupQrCode.Rows", StringComparison.Ordinal));

    True(File.Exists(Path.Combine(SourceRoot(), "docs/history/release-notes/V0.34.0-ALPHA14-RADIO-VAULT-WEB-PHONE-CONNECTION.md")));
}

static void Alpha15RestoresServerFolderAssignmentAndNativeAudioQuality()
{
    var serverViewModel = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Server", "ViewModels", "ServerSettingsViewModel.cs"));
    True(serverViewModel.Contains("ChooseLibraryFolderShowAsync", StringComparison.Ordinal));
    True(serverViewModel.Contains("SetLibraryFolderCollectionAsync", StringComparison.Ordinal));
    True(serverViewModel.Contains("ScanLibraryAsync", StringComparison.Ordinal));

    var serverView = ReadServerAdministrationViews();
    True(serverView.Contains("Change selected show", StringComparison.Ordinal));
    True(serverView.Contains("AssignLibraryFolderCommand", StringComparison.Ordinal));

    var showSelection = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Server", "Services", "ServerShowSelectionService.cs"));
    True(showSelection.Contains("Which show is this folder for?", StringComparison.Ordinal));
    True(showSelection.Contains("KnownShowCatalog.Normalize", StringComparison.Ordinal));

    var shell = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "MainWindowViewModel.cs"));
    True(shell.Contains("string.Equals(route, LibraryRoute", StringComparison.Ordinal));
    True(shell.Contains("await RefreshLibraryNavigationAsync()", StringComparison.Ordinal));

    var audio = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Playback", "NAudioPlaybackEngine.cs"));
    True(audio.Contains("new WasapiOut(AudioClientShareMode.Shared", StringComparison.Ordinal));
    True(audio.Contains("Math.Abs(_speed - 1d)", StringComparison.Ordinal));
    True(audio.Contains("? _reader", StringComparison.Ordinal));
    True(!audio.Contains("new WaveOutEvent", StringComparison.Ordinal));

    True(File.Exists(Path.Combine(SourceRoot(), "docs/history/release-notes/V0.34.0-ALPHA15-SERVER-FOLDER-AUDIO-REPAIR.md")));
}

static void Alpha16ImprovesRemoteResponsivenessAndPlaybackOwnership()
{
    var connection = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Infrastructure", "Services", "LoopbackServerClient.cs"));
    True(connection.Contains("MemoryResponseEntry", StringComparison.Ordinal));
    True(connection.Contains("AddSeconds(20)", StringComparison.Ordinal));
    True(connection.Contains("_memoryResponses.Clear()", StringComparison.Ordinal));

    var shell = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "MainWindowViewModel.cs"));
    True(shell.Contains("WarmCachedNavigationAsync", StringComparison.Ordinal));
    True(shell.Contains("Search.LoadAsync()", StringComparison.Ordinal));
    True(shell.Contains("Dashboard.LoadAsync()", StringComparison.Ordinal));
    True(shell.Contains("RefreshAfterServerSyncAsync", StringComparison.Ordinal));

    var playback = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "PlaybackViewModel.cs"));
    True(playback.Contains("_handoffSnapshot?.IsOwnedByCurrentDevice != true", StringComparison.Ordinal));
    True(playback.Contains("_handoffSnapshot?.IsOwnedByCurrentDevice == false", StringComparison.Ordinal));

    var mainWindow = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "MainWindow.axaml"));
    var desktopTheme = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "App.axaml"));
    True(mainWindow.Contains("PrimaryTransportIconTemplate", StringComparison.Ordinal));
    True(desktopTheme.Contains("ShowMoveToThisDevice", StringComparison.Ordinal));
    True(desktopTheme.Contains("Move playback to this PC", StringComparison.Ordinal));
    True(desktopTheme.Contains("M2,11 L13,11", StringComparison.Ordinal));

    True(File.Exists(Path.Combine(SourceRoot(), "docs/history/release-notes/V0.34.0-ALPHA16-REMOTE-RESPONSIVENESS-OWNERSHIP.md")));
}

static void Alpha17BoundsLargeLibraryWebHandoffs()
{
    var canonical = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Services", "Services", "CanonicalLibraryQueryService.cs"));
    True(canonical.Contains("public CanonicalLibraryEntry? GetBroadcast(long episodeId)", StringComparison.Ordinal));
    True(canonical.Contains("($key IS NULL OR canonical_key=$key)", StringComparison.Ordinal));

    var database = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Infrastructure", "Services", "DatabaseService.cs"));
    True(database.Contains("public EpisodeListItem? GetEpisode(long episodeId)", StringComparison.Ordinal));
    True(database.Contains("($episode IS NULL OR e.id=$episode)", StringComparison.Ordinal));

    var transfers = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Infrastructure", "Services", "WebArchiveProvider.PlaybackTransfers.cs"));
    True(transfers.Contains("GetEpisodeDirect(request.EpisodeId)", StringComparison.Ordinal));
    True(transfers.Contains("GetEpisodeDirect(ticket.TargetEpisodeId)", StringComparison.Ordinal));
    True(!transfers.Contains("InvalidateEpisodeSnapshot();", StringComparison.Ordinal));

    True(File.Exists(Path.Combine(SourceRoot(), "docs/history/release-notes/V0.34.0-ALPHA17-LARGE-LIBRARY-HANDOFF.md")));
}

static void DirectEpisodeLookupReturnsOnlyRequestedBroadcast()
{
    var path = Path.Combine(Path.GetTempPath(), $"radiovault-point-lookup-{Guid.NewGuid():N}.db");
    try
    {
        var database = new SqliteDatabase(path);
        database.Initialize();
        long requestedId;
        using (var connection = database.OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            var collectionId = Convert.ToInt64(new Microsoft.Data.Sqlite.SqliteCommand(
                "SELECT id FROM collections WHERE name='Bennington'", connection, transaction).ExecuteScalar(),
                CultureInfo.InvariantCulture);
            requestedId = 0;
            for (var index = 0; index < 500; index++)
            {
                using var episode = connection.CreateCommand();
                episode.Transaction = transaction;
                episode.CommandText = "INSERT INTO episodes(collection_id,title,status,date_added,updated_at) VALUES($collection,$title,'Unplayed',$now,$now); SELECT last_insert_rowid();";
                episode.Parameters.AddWithValue("$collection", collectionId);
                episode.Parameters.AddWithValue("$title", $"Broadcast {index}");
                episode.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                var episodeId = Convert.ToInt64(episode.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (index == 347) requestedId = episodeId;

                using var media = connection.CreateCommand();
                media.Transaction = transaction;
                media.CommandText = "INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,storage_state,is_preferred) VALUES($episode,$path,$name,1,$now,0,$now,'AvailableOffline',1)";
                media.Parameters.AddWithValue("$episode", episodeId);
                media.Parameters.AddWithValue("$path", $@"C:\Radio\broadcast-{index}.mp3");
                media.Parameters.AddWithValue("$name", $"broadcast-{index}.mp3");
                media.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                media.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        var service = new DatabaseService(database);
        var result = service.GetEpisode(requestedId);
        True(result is not null);
        Equal(requestedId, result!.Id);
        Equal("Broadcast 347", result.DisplayTitle);
        True(service.GetEpisode(long.MaxValue) is null);
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
        if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
    }
}

static void Alpha18HardensConnectedClientReliability()
{
    var connection = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Infrastructure", "Services", "LoopbackServerClient.cs"));
    True(connection.Contains("RemoteRetryAttemptTimeout = TimeSpan.FromSeconds(5)", StringComparison.Ordinal));
    True(connection.Contains("InvalidateMemoryCache()", StringComparison.Ordinal));
    True(connection.Contains("attemptTimeout?.CancelAfter", StringComparison.Ordinal));

    var access = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Infrastructure", "Services", "NativeConnectedAccessService.cs"));
    True(access.Contains("state == ConnectedAccessState.CachedReadOnly", StringComparison.Ordinal));
    True(access.Contains("_runtimeConnection?.MarkServerLive(invalidateMemoryCache: recovered)", StringComparison.Ordinal));

    var provider = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Infrastructure", "Services", "WebArchiveProvider.cs"));
    True(provider.Contains("anotherOutputIsActive", StringComparison.Ordinal));
    True(provider.Contains("Move playback transactionally", StringComparison.Ordinal));
    True(provider.Contains("RetainsPlaybackOwnershipWhileOffline", StringComparison.Ordinal));
    True(provider.Contains("\"iOSClient\"", StringComparison.Ordinal));

    var playback = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "PlaybackViewModel.cs"));
    True(playback.Contains("_handoffSnapshot?.HasActivePlayback == true", StringComparison.Ordinal));
    True(playback.Contains("_handoffSnapshot.IsOwnedByCurrentDevice == false", StringComparison.Ordinal));

    var web = ReadWebServerSourceBundle();
    True(web.Contains("radio-vault-anywhere-shell-v68", StringComparison.Ordinal));
    True(!web.Contains("radio-vault-anywhere-shell-v42", StringComparison.Ordinal));

    foreach (var installerName in new[] { "RadioVault.Client.iss", "RadioVault.Server.iss" })
    {
        var installer = File.ReadAllText(Path.Combine(SourceRoot(), "installer", installerName));
        True(installer.Contains("UsePreviousAppDir=yes", StringComparison.Ordinal));
        True(installer.Contains("UsePreviousTasks=yes", StringComparison.Ordinal));
        True(installer.Contains("SetupLogging=yes", StringComparison.Ordinal));
        True(installer.Contains("Type: filesandordirs; Name: \"{app}\"", StringComparison.Ordinal));
    }

    True(File.Exists(Path.Combine(SourceRoot(), "docs/history/release-notes/V0.34.0-ALPHA18-CONNECTED-RELIABILITY.md")));
}

static void ConnectedViewsRefreshBoundedStaleData()
{
    var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    True(ConnectedViewRefreshPolicy.IsFresh(now.AddSeconds(-29), now));
    True(!ConnectedViewRefreshPolicy.IsFresh(now.AddSeconds(-30), now));
    True(!ConnectedViewRefreshPolicy.IsFresh(DateTimeOffset.MinValue, now));

    foreach (var viewModel in new[]
             {
                 "DashboardViewModel.cs", "SearchViewModel.cs", "LibraryViewModel.cs",
                 "MomentsViewModel.cs", "TranscriptsViewModel.cs", "QueueViewModel.cs"
             })
    {
        var source = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Presentation", "ViewModels", viewModel));
        True(source.Contains("ConnectedViewRefreshPolicy.IsFresh", StringComparison.Ordinal));
    }
}

static void NativeHandoffPreservesWindowsVolumeSession()
{
    var engine = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Playback", "NAudioPlaybackEngine.cs"));

    True(engine.Contains(
        "new VolumeSampleProvider(source.ToSampleProvider())",
        StringComparison.Ordinal));
    True(engine.Contains(
        "_volumeProvider.Volume = (float)_volume",
        StringComparison.Ordinal));
    True(engine.Contains(
        "output.Init(volumeProvider.ToWaveProvider())",
        StringComparison.Ordinal));
    True(!engine.Contains(
        "if (_output is not null) _output.Volume",
        StringComparison.Ordinal));
    True(!engine.Contains(
        "output.Volume = (float)_volume",
        StringComparison.Ordinal));
}

static void MacClientUsesNativeAvFoundationAndExistingServerContracts()
{
    var engine = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Playback", "MacAvFoundationPlaybackEngine.cs"));
    foreach (var marker in new[]
             {
                 "OperatingSystem.IsMacOS()",
                 "AVFoundation.framework/AVFoundation",
                 "CMTimeMakeWithSeconds",
                 "PlaybackStatus.Buffering",
                 "MediaEnded?.Invoke",
                 "IPlaybackEngine"
             })
        True(engine.Contains(marker, StringComparison.Ordinal));

    var composition = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Composition", "AvaloniaApplicationHost.cs"));
    True(composition.Contains("CreatePlaybackEngine()", StringComparison.Ordinal));
    True(composition.Contains("new MacAvFoundationPlaybackEngine()", StringComparison.Ordinal));
    True(composition.Contains("new ServerMediaProxy", StringComparison.Ordinal));
    True(composition.Contains("new LoopbackServerClient", StringComparison.Ordinal));

    var plist = File.ReadAllText(Path.Combine(
        SourceRoot(), "installer", "macos", "Info.plist"));
    True(plist.Contains("com.theradiovault.client", StringComparison.Ordinal));
    True(plist.Contains("NSLocalNetworkUsageDescription", StringComparison.Ordinal));
    True(plist.Contains("NSAllowsLocalNetworking", StringComparison.Ordinal));

    var package = File.ReadAllText(Path.Combine(SourceRoot(), "package-macos-client.ps1"));
    True(package.Contains("osx-arm64", StringComparison.Ordinal));
    True(package.Contains("--self-contained true", StringComparison.Ordinal));
    True(package.Contains("Radio Vault.app", StringComparison.Ordinal));
}

static void MacAndLinuxPackagesPreserveSharedClientServerBoundary()
{
    var composition = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Composition", "AvaloniaApplicationHost.cs"));
    True(composition.Contains("OperatingSystem.IsLinux()", StringComparison.Ordinal));
    True(composition.Contains("new LinuxMpvPlaybackEngine()", StringComparison.Ordinal));

    var linuxEngine = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Playback", "LinuxMpvPlaybackEngine.cs"));
    foreach (var marker in new[]
             {
                 "OperatingSystem.IsLinux()",
                 "UnixDomainSocketEndPoint",
                 "--input-ipc-server=",
                 "RADIOVAULT_MPV_PATH",
                 "Server credentials are never passed to mpv",
                 "PlaybackStatus.Ended"
             })
        True(linuxEngine.Contains(marker, StringComparison.Ordinal));

    var startup = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Server", "Services", "ServerStartupRegistrationService.cs"));
    foreach (var marker in new[]
             {
                 "WindowsStartupRegistrationService",
                 "Library\", \"LaunchAgents",
                 "\".config\", \"autostart\"",
                 "com.theradiovault.server",
                 "Start with macOS",
                 "Start with Linux"
             })
        True(startup.Contains(marker, StringComparison.Ordinal));

    var serverProject = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Server", "TheRadioVault.Server.csproj"));
    True(serverProject.Contains("<TargetFramework>net8.0</TargetFramework>", StringComparison.Ordinal));
    True(!serverProject.Contains("net8.0-windows", StringComparison.Ordinal));

    var macPackage = File.ReadAllText(Path.Combine(SourceRoot(), "package-macos-server.ps1"));
    True(macPackage.Contains("TheRadioVault.Server\\TheRadioVault.Server.csproj", StringComparison.Ordinal));
    True(macPackage.Contains("Radio Vault Server.app", StringComparison.Ordinal));
    True(macPackage.Contains("--self-contained true", StringComparison.Ordinal));
    True(File.Exists(Path.Combine(SourceRoot(), "installer", "macos", "ServerInfo.plist")));
    True(File.Exists(Path.Combine(SourceRoot(), "installer", "macos", "finalize-macos-server.sh")));

    var linuxPackage = File.ReadAllText(Path.Combine(SourceRoot(), "package-linux.sh"));
    True(linuxPackage.Contains("linux-x64", StringComparison.Ordinal));
    True(linuxPackage.Contains("--self-contained true", StringComparison.Ordinal));
    True(linuxPackage.Contains("RadioVault.Client-$VERSION-$RID.tar.gz", StringComparison.Ordinal));
    True(linuxPackage.Contains("RadioVault.Server-$VERSION-$RID.tar.gz", StringComparison.Ordinal));
    True(linuxPackage.Contains("dpkg-deb --build --root-owner-group", StringComparison.Ordinal));
    True(linuxPackage.Contains("RadioVault.Client-$VERSION-$DEB_ARCH.deb", StringComparison.Ordinal));
    True(linuxPackage.Contains("radiovault.desktop", StringComparison.Ordinal));

    var macInstallerPackage = File.ReadAllText(Path.Combine(SourceRoot(), "package-macos-installers.sh"));
    True(macInstallerPackage.Contains("hdiutil create", StringComparison.Ordinal));
    True(macInstallerPackage.Contains("codesign --force --deep --sign -", StringComparison.Ordinal));
    True(macInstallerPackage.Contains("ln -s /Applications", StringComparison.Ordinal));

    var windowsInstallerPackage = File.ReadAllText(Path.Combine(SourceRoot(), "package-windows-ci-installers.ps1"));
    True(windowsInstallerPackage.Contains("RadioVault.Client.iss", StringComparison.Ordinal));
    True(windowsInstallerPackage.Contains("RadioVault.Server.iss", StringComparison.Ordinal));
    True(windowsInstallerPackage.Contains("windows-installers", StringComparison.Ordinal));

    var workflow = File.ReadAllText(Path.Combine(SourceRoot(), ".github", "workflows", "ci.yml"));
    True(workflow.Contains("name: macOS client and server", StringComparison.Ordinal));
    True(workflow.Contains("name: Linux client and server", StringComparison.Ordinal));
    True(workflow.Contains("macos-client-and-server-osx-arm64-unsigned", StringComparison.Ordinal));
    True(workflow.Contains("linux-client-and-server-x64", StringComparison.Ordinal));
    True(workflow.Contains("package-windows-ci-installers.ps1", StringComparison.Ordinal));
    True(workflow.Contains("package-macos-installers.sh", StringComparison.Ordinal));
    True(workflow.Contains("*.dmg", StringComparison.Ordinal));
    True(workflow.Contains("*.deb", StringComparison.Ordinal));

    var linuxGuide = File.ReadAllText(Path.Combine(SourceRoot(), "LINUX.md"));
    True(linuxGuide.Contains("mpv", StringComparison.Ordinal));
    True(linuxGuide.Contains("run-radiovault-server.sh", StringComparison.Ordinal));

    var sourcePackage = File.ReadAllText(Path.Combine(SourceRoot(), "tools", "Package-Source.ps1"));
    True(sourcePackage.Contains("Get-ChildItem $root -Recurse -File -Force", StringComparison.Ordinal));
    True(sourcePackage.Contains("StringComparer]::Ordinal", StringComparison.Ordinal));
}

static void IosClientPreservesNativePlatformAndServerBoundaries()
{
    var iosProject = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "TheRadioVault.Client.iOS.csproj"));
    True(iosProject.Contains("net10.0-ios", StringComparison.Ordinal));
    True(!iosProject.Contains("Avalonia", StringComparison.Ordinal));
    True(iosProject.Contains("TheRadioVault.Client.Mobile", StringComparison.Ordinal));
    True(iosProject.Contains("<AppIcon>AppIcon</AppIcon>", StringComparison.Ordinal));
    True(iosProject.Contains("'$(Configuration)' != 'DeviceTest'", StringComparison.Ordinal));
    True(iosProject.Contains("com.ghrobson.theradiovault.devicetest", StringComparison.Ordinal));
    True(iosProject.Contains("<CodesignProvision>Automatic</CodesignProvision>", StringComparison.Ordinal));
    True(!iosProject.Contains("TheRadioVault.Infrastructure", StringComparison.Ordinal));
    True(!iosProject.Contains("TheRadioVault.Server", StringComparison.Ordinal));

    var mobileProject = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.Mobile", "TheRadioVault.Client.Mobile.csproj"));
    True(mobileProject.Contains("TheRadioVault.Protocol", StringComparison.Ordinal));
    True(!mobileProject.Contains("TheRadioVault.Web.csproj", StringComparison.Ordinal));
    True(!mobileProject.Contains("Avalonia", StringComparison.Ordinal));

    var tabs = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "RadioVaultTabBarController.cs"));
    True(tabs.Contains("UITabBarController", StringComparison.Ordinal));
    True(tabs.Contains("UINavigationController", StringComparison.Ordinal));
    True(tabs.Contains("HomeViewController", StringComparison.Ordinal));
    True(tabs.Contains("LibraryViewController", StringComparison.Ordinal));
    True(tabs.Contains("ExploreViewController", StringComparison.Ordinal));
    True(tabs.Contains("SavedViewController", StringComparison.Ordinal));
    True(tabs.Contains("KnowledgeViewController", StringComparison.Ordinal));
    True(!tabs.Contains("Wrap(new DownloadsViewController", StringComparison.Ordinal));
    True(tabs.Contains("RadioVaultMiniPlayerView", StringComparison.Ordinal));
    True(!tabs.Contains("Wrap(new NowPlayingViewController", StringComparison.Ordinal));
    True(!tabs.Contains("Wrap(new ServerViewController", StringComparison.Ordinal));
    True(tabs.Contains("RadioVaultIcons.Image", StringComparison.Ordinal));
    True(tabs.Contains("SetTitleTextAttributes", StringComparison.Ordinal));
    True(tabs.Contains("CreateTabAppearance", StringComparison.Ordinal));
    True(tabs.Contains("appearance.Selected.TitleTextAttributes", StringComparison.Ordinal));
    True(tabs.Contains("UIModalPresentationStyle.PageSheet", StringComparison.Ordinal));
    True(tabs.Contains("PrefersGrabberVisible = true", StringComparison.Ordinal));
    True(tabs.Contains("\"Dashboard\"", StringComparison.Ordinal));
    True(tabs.Contains("\"Saved\"", StringComparison.Ordinal));
    True(tabs.Contains("\"Knowledge\"", StringComparison.Ordinal));
    True(!tabs.Contains("controller.NavigationItem.Title = title", StringComparison.Ordinal));

    var dashboard = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "HomeViewController.cs"));
    True(dashboard.Contains("Title = \"Dashboard\"", StringComparison.Ordinal));
    True(dashboard.Contains("Continue listening", StringComparison.Ordinal));
    True(dashboard.Contains("DashboardOverviewCell", StringComparison.Ordinal));
    True(dashboard.Contains("On this day", StringComparison.Ordinal));
    True(dashboard.Contains("Recently added", StringComparison.Ordinal));
    True(dashboard.Contains("Unheard broadcasts", StringComparison.Ordinal));
    True(dashboard.Contains("Session.UnheardBroadcasts", StringComparison.Ordinal));
    True(dashboard.Contains("OpenLibrarySection", StringComparison.Ordinal));
    True(dashboard.Contains("IsPlayingBroadcast", StringComparison.Ordinal));
    True(dashboard.Contains("PreparingPlaybackEpisodeId", StringComparison.Ordinal));
    True(dashboard.Contains("HandleFeaturedPlayback", StringComparison.Ordinal));
    True(dashboard.Contains("Session.CurrentBroadcast", StringComparison.Ordinal));
    True(dashboard.Contains("value.EpisodeId != featuredId", StringComparison.Ordinal));
    True(dashboard.Contains("ServerViewController", StringComparison.Ordinal));
    True(dashboard.Contains("header.SetAccessory(settings)", StringComparison.Ordinal));
    True(dashboard.Contains("DashboardOnThisDayCarouselCell", StringComparison.Ordinal));
    True(dashboard.Contains("announce: false", StringComparison.Ordinal));
    True(dashboard.Contains("CaptureSectionFingerprints", StringComparison.Ordinal));
    True(dashboard.Contains("BroadcastIdentityFingerprint(RecentlyAdded)", StringComparison.Ordinal));
    True(dashboard.Contains("BroadcastIdentityFingerprint(Unheard)", StringComparison.Ordinal));
    True(dashboard.Contains("RefreshVisibleBroadcasts", StringComparison.Ordinal));
    True(dashboard.Contains("Session.RecentBroadcasts.Take(5)", StringComparison.Ordinal));
    True(dashboard.Contains("Session.UnheardBroadcasts.Take(5)", StringComparison.Ordinal));
    True(dashboard.Contains("new NSMutableIndexSet", StringComparison.Ordinal));
    True(dashboard.Contains("changedSections.Add", StringComparison.Ordinal));

    var library = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "LibraryViewController.cs"));
    True(!library.Contains("UISearchController", StringComparison.Ordinal));
    True(library.Contains("LibraryControlsHeaderView", StringComparison.Ordinal));
    True(library.Contains("UITableView", StringComparison.Ordinal));
    True(library.Contains("LibraryCollections", StringComparison.Ordinal));
    True(library.Contains("ShowLibraryViewController", StringComparison.Ordinal));
    True(library.Contains("Favourites", StringComparison.Ordinal));
    True(library.Contains("ContinueListening", StringComparison.Ordinal));
    True(library.Contains("UpNextViewController", StringComparison.Ordinal));
    True(library.Contains("ToggleHideCompleted", StringComparison.Ordinal));
    True(library.Contains("SetHideCompleted", StringComparison.Ordinal));
    True(library.Contains("LibraryCollectionsFor", StringComparison.Ordinal));
    True(library.Contains("DownloadsViewController", StringComparison.Ordinal));
    True(library.Contains("LibraryQuickAccessCell", StringComparison.Ordinal));
    True(library.Contains("RadioVaultIcon.Radio", StringComparison.Ordinal));
    True(library.Contains("_header.CollectionsButton.TouchUpInside += CollectionsButtonTapped", StringComparison.Ordinal));
    True(library.Contains("\"New Smart Collection\"", StringComparison.Ordinal));
    True(library.Contains("popover.SourceView = _header.CollectionsButton", StringComparison.Ordinal));

    var showLibrary = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "ShowLibraryViewController.cs"));
    True(!showLibrary.Contains("UISegmentedControl", StringComparison.Ordinal));
    True(showLibrary.Contains("ArchiveLevel.Years", StringComparison.Ordinal));
    True(showLibrary.Contains("ArchiveLevel.Months", StringComparison.Ordinal));
    True(showLibrary.Contains("ArchiveLevel.Broadcasts", StringComparison.Ordinal));
    True(showLibrary.Contains("ArchiveGridRowCell", StringComparison.Ordinal));
    True(showLibrary.Contains("GridButtonTapped", StringComparison.Ordinal));
    True(showLibrary.Contains("ListButtonTapped", StringComparison.Ordinal));
    True(showLibrary.Contains("includesViewModes", StringComparison.Ordinal));
    True(showLibrary.Contains("ToggleHideCompleted", StringComparison.Ordinal));
    True(showLibrary.Contains("LoadArchivePeriodsAsync", StringComparison.Ordinal));

    var iosCells = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "RadioVaultCells.cs"));
    True(iosCells.Contains("PageHeaderView", StringComparison.Ordinal));
    True(iosCells.Contains("LibraryControlsHeaderView", StringComparison.Ordinal));
    True(iosCells.Contains("CompletedButton", StringComparison.Ordinal));
    True(iosCells.Contains("public UIButton CollectionsButton", StringComparison.Ordinal));
    True(iosCells.Contains("UITapGestureRecognizer", StringComparison.Ordinal));
    True(iosCells.Contains("DashboardStatsCell", StringComparison.Ordinal));
    True(iosCells.Contains("DashboardOverviewCell", StringComparison.Ordinal));
    True(iosCells.Contains("DashboardContinueCell", StringComparison.Ordinal));
    True(iosCells.Contains("LibraryQuickAccessCell", StringComparison.Ordinal));
    True(iosCells.Contains("UserInteractionEnabled = false", StringComparison.Ordinal));
    True(iosCells.Contains("BroadcastProgressCell", StringComparison.Ordinal));
    True(iosCells.Contains("BroadcastHeroCell", StringComparison.Ordinal));
    True(iosCells.Contains("ExploreImageGalleryCell", StringComparison.Ordinal));
    True(iosCells.Contains("ExploreArticleImageCell", StringComparison.Ordinal));
    True(iosCells.Contains("UIActivityIndicatorView", StringComparison.Ordinal));
    True(iosCells.Contains("isPlaying ? \"Pause\" : \"Resume\"", StringComparison.Ordinal));
    True(iosCells.Contains("Loading…", StringComparison.Ordinal));
    True(iosCells.Contains("RadioVaultArtwork.Load", StringComparison.Ordinal));

    var iosArtwork = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "RadioVaultArtwork.cs"));
    True(iosArtwork.Contains("session.LoadArtworkAsync", StringComparison.Ordinal));
    True(iosArtwork.Contains("UIViewContentMode.ScaleAspectFill", StringComparison.Ordinal));
    True(!iosArtwork.Contains("broadcast.Source.ArtworkPath", StringComparison.Ordinal));

    var iosTableBase = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "SessionTableViewController.cs"));
    True(iosTableBase.Contains("GetContextMenuConfiguration", StringComparison.Ordinal));
    True(iosTableBase.Contains("Download to this iPhone", StringComparison.Ordinal));
    True(iosTableBase.Contains("ShowsOfflineIndicator", StringComparison.Ordinal));
    True(iosTableBase.Contains("ShowsSyncIndicator", StringComparison.Ordinal));
    True(iosTableBase.Contains("UsesInlinePageHeading", StringComparison.Ordinal));
    True(iosTableBase.Contains("RadioVaultIcon.Sync", StringComparison.Ordinal));
    True(iosTableBase.Contains("Mark as Listened", StringComparison.Ordinal));
    True(iosTableBase.Contains("Mark as Unlistened", StringComparison.Ordinal));
    True(iosTableBase.Contains("\"New Playlist…\"", StringComparison.Ordinal));
    True(iosTableBase.Contains("PromptForNewPlaylistAndAdd", StringComparison.Ordinal));
    True(iosTableBase.Contains("actions.Add(addToPlaylist)", StringComparison.Ordinal));
    True(iosTableBase.Contains("new UIBarButtonItem(image)", StringComparison.Ordinal));
    True(iosTableBase.Contains("NavigationItem.Title = string.Empty", StringComparison.Ordinal));
    True(iosTableBase.Contains("UINavigationItemBackButtonDisplayMode.Minimal", StringComparison.Ordinal));
    True(!iosTableBase.Contains("UIImage.GetSystemImage", StringComparison.Ordinal));

    var upNext = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "UpNextViewController.cs"));
    True(upNext.Contains("CanMoveRow", StringComparison.Ordinal));
    True(upNext.Contains("MoveQueueItemAsync", StringComparison.Ordinal));
    True(upNext.Contains("RemoveQueueItemAsync", StringComparison.Ordinal));
    True(upNext.Contains("ClearQueueAsync", StringComparison.Ordinal));
    True(upNext.Contains("PlayQueueItemAsync", StringComparison.Ordinal));
    True(upNext.Contains("ReturnToNowPlaying", StringComparison.Ordinal));
    True(upNext.Contains("Back to Now Playing", StringComparison.Ordinal));

    var explore = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "ExploreViewController.cs"));
    True(explore.Contains("Featured articles", StringComparison.Ordinal));
    True(explore.Contains("Recently updated", StringComparison.Ordinal));
    True(explore.Contains("Explore by era", StringComparison.Ordinal));
    True(explore.Contains("Show timelines", StringComparison.Ordinal));
    True(explore.Contains("ExploreDashboardSection.OnThisDate", StringComparison.Ordinal));
    True(explore.Contains("ShowPages", StringComparison.Ordinal));
    True(explore.Contains("PeoplePages", StringComparison.Ordinal));
    True(explore.Contains("TopicPages", StringComparison.Ordinal));
    True(explore.Contains("LoadExploreDashboardAsync", StringComparison.Ordinal));
    True(explore.Contains("PageHeading => \"Explore\"", StringComparison.Ordinal));
    True(explore.Contains("ExploreTimelinePromoCell", StringComparison.Ordinal));
    True(explore.Contains("Images from the archive", StringComparison.Ordinal));
    True(explore.Contains("ExploreImageGalleryCell", StringComparison.Ordinal));

    var exploreArticle = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "ExploreArticleViewController.cs"));
    True(exploreArticle.Contains("LoadExplorePageAsync", StringComparison.Ordinal));
    True(exploreArticle.Contains("ExploreArticleBodyCell", StringComparison.Ordinal));
    True(exploreArticle.Contains("Timeline", StringComparison.Ordinal));
    True(exploreArticle.Contains("LoadExploreImagesAsync", StringComparison.Ordinal));
    True(exploreArticle.Contains("InlineLinkTargets", StringComparison.Ordinal));
    True(exploreArticle.Contains("ShowLibraryViewController", StringComparison.Ordinal));
    True(exploreArticle.Contains("ArchiveEntityNavigation.Resolve", StringComparison.Ordinal));
    True(exploreArticle.Contains("LoadBroadcastAsync", StringComparison.Ordinal));

    var desktopExplore = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "WikiViewModel.cs"));
    True(desktopExplore.Contains("SetOpenEntityLinkHandler", StringComparison.Ordinal));
    True(desktopExplore.Contains("ArchiveEntityNavigation.Resolve", StringComparison.Ordinal));
    True(desktopExplore.Contains("TryOpenEntityLinkAsync", StringComparison.Ordinal));

    var exploreCells = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "ExploreCells.cs"));
    True(exploreCells.Contains("ExploreDashboardHeroCell", StringComparison.Ordinal));
    True(exploreCells.Contains("NewYork-Regular", StringComparison.Ordinal));
    True(exploreCells.Contains("ExploreTimelineEventCell", StringComparison.Ordinal));
    True(exploreCells.Contains("RADIO VAULT ENCYCLOPEDIA", StringComparison.Ordinal));
    True(exploreCells.Contains("radiovault://link/", StringComparison.Ordinal));
    True(exploreCells.Contains("ShouldInteractWithUrl", StringComparison.Ordinal));

    var exploreTimeline = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "ExploreTimelineViewController.cs"));
    True(exploreTimeline.Contains("Show Timelines", StringComparison.Ordinal));
    True(exploreTimeline.Contains("PlayTimelineLinkAsync", StringComparison.Ordinal));
    True(exploreTimeline.Contains("PresentShowPicker", StringComparison.Ordinal));

    var theme = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "RadioVaultTheme.cs"));
    True(theme.Contains("0x11, 0x13, 0x17", StringComparison.Ordinal));
    True(theme.Contains("0xF2, 0xC9, 0x4C", StringComparison.Ordinal));
    True(theme.Contains("public static UIColor Progress { get; } = Accent", StringComparison.Ordinal));
    True(theme.Contains("UIGraphicsImageRenderer", StringComparison.Ordinal));
    True(theme.Contains("RadioVaultIcon.Knowledge", StringComparison.Ordinal));
    True(theme.Contains("RadioVaultIcon.Handoff", StringComparison.Ordinal));
    True(theme.Contains("RadioVaultIcon.SkipBack", StringComparison.Ordinal));
    True(theme.Contains("RadioVaultIcon.Moment", StringComparison.Ordinal));
    True(theme.Contains("RadioVaultIcon.Offline", StringComparison.Ordinal));
    True(theme.Contains("RadioVaultIcon.Sync", StringComparison.Ordinal));
    True(theme.Contains("RadioVaultIcon.InProgress", StringComparison.Ordinal));
    True(theme.Contains("DrawSkip", StringComparison.Ordinal));
    True(theme.Contains("DrawSync", StringComparison.Ordinal));
    True(theme.Contains("Lines(context, (8, 17), (4, 20), (8, 23))", StringComparison.Ordinal));
    True(theme.Contains("ConfigureWithTransparentBackground", StringComparison.Ordinal));
    True(!theme.Contains("ArrowHead", StringComparison.Ordinal));
    True(theme.Contains("context.AddCurveToPoint(5.3f, 20, 4, 18.7f, 4, 17)", StringComparison.Ordinal));
    True(theme.Contains("Lines(context, (16, 1), (20, 4), (16, 7))", StringComparison.Ordinal));
    True(!theme.Contains("(14.5, 4.5), (18, 6), (16.5, 9.5)", StringComparison.Ordinal));
    True(!theme.Contains("(16, 7), (20, 8), (18.5, 11.8)", StringComparison.Ordinal));
    True(theme.Contains("(3, 10.5), (13, 10.5)", StringComparison.Ordinal));

    var miniPlayer = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "RadioVaultMiniPlayerView.cs"));
    True(miniPlayer.Contains("MiniPlayerShowsHandoff", StringComparison.Ordinal));
    True(miniPlayer.Contains("RadioVaultIcon.Handoff", StringComparison.Ordinal));
    True(miniPlayer.Contains("Move playback to this iPhone", StringComparison.Ordinal));
    True(miniPlayer.Contains("Layer.CornerRadius = 24", StringComparison.Ordinal));
    True(miniPlayer.Contains("IsPreparingPlayback", StringComparison.Ordinal));
    True(miniPlayer.Contains("UIActivityIndicatorView", StringComparison.Ordinal));
    True(miniPlayer.Contains("RadioVaultArtwork.Load", StringComparison.Ordinal));
    True(miniPlayer.Contains("UIVisualEffectView", StringComparison.Ordinal));
    True(miniPlayer.Contains("SystemChromeMaterialDark", StringComparison.Ordinal));

    var nowPlayingView = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "NowPlayingViewController.cs"));
    True(nowPlayingView.Contains("UIScrollView", StringComparison.Ordinal));
    True(nowPlayingView.Contains("ContentLayoutGuide", StringComparison.Ordinal));
    True(nowPlayingView.Contains("contentHost.WidthAnchor.ConstraintEqualTo(scrollView.FrameLayoutGuide.WidthAnchor)", StringComparison.Ordinal));
    True(nowPlayingView.Contains("content.LeadingAnchor.ConstraintEqualTo(contentHost.LeadingAnchor, 20)", StringComparison.Ordinal));
    True(nowPlayingView.Contains("AdjustsFontForContentSizeCategory", StringComparison.Ordinal));
    True(nowPlayingView.Contains("RadioVaultIcon.SkipBack", StringComparison.Ordinal));
    True(nowPlayingView.Contains("RadioVaultIcon.SkipForward", StringComparison.Ordinal));
    True(nowPlayingView.Contains("PresentMomentEditor", StringComparison.Ordinal));
    True(nowPlayingView.Contains("OpenBroadcastInformation", StringComparison.Ordinal));
    True(nowPlayingView.Contains("ToggleFavourite", StringComparison.Ordinal));
    True(!nowPlayingView.Contains("UIImage.GetSystemImage", StringComparison.Ordinal));
    True(nowPlayingView.Contains("_playButton.WidthAnchor.ConstraintEqualTo(96)", StringComparison.Ordinal));
    True(nowPlayingView.Contains("SeekToProgress", StringComparison.Ordinal));
    True(nowPlayingView.Contains("RadioVaultIcon.SkipBack, RadioVaultTheme.Accent", StringComparison.Ordinal));
    True(nowPlayingView.Contains("IsPreparingPlayback", StringComparison.Ordinal));
    True(nowPlayingView.Contains("_playActivity.StartAnimating", StringComparison.Ordinal));
    True(nowPlayingView.Contains("_artworkPanel.HeightAnchor.ConstraintEqualTo(_artworkPanel.WidthAnchor)", StringComparison.Ordinal));
    True(nowPlayingView.Contains("RadioVaultArtwork.Load", StringComparison.Ordinal));
    True(nowPlayingView.Contains("_session.MiniPlayerProgress", StringComparison.Ordinal));
    True(nowPlayingView.Contains("_session.MiniPlayerTime", StringComparison.Ordinal));
    True(nowPlayingView.Contains("_totalLabel.CenterXAnchor.ConstraintEqualTo(times.CenterXAnchor)", StringComparison.Ordinal));
    True(nowPlayingView.Contains("_elapsedLabel.Text = _session.MiniPlayerElapsedTime", StringComparison.Ordinal));
    True(nowPlayingView.Contains("NowPlayingUpNextView", StringComparison.Ordinal));
    True(nowPlayingView.Contains("playerContent.HeightAnchor.ConstraintGreaterThanOrEqualTo", StringComparison.Ordinal));
    True(nowPlayingView.Contains("_favouriteSaving", StringComparison.Ordinal));
    True(nowPlayingView.Contains("_momentSavedFeedback", StringComparison.Ordinal));

    var savedView = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "SavedViewController.cs"));
    True(savedView.Contains("SavedControlsHeaderView", StringComparison.Ordinal));
    True(savedView.Contains("FavouritesButtonTapped", StringComparison.Ordinal));
    True(savedView.Contains("MomentsButtonTapped", StringComparison.Ordinal));

    var knowledgeView = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "KnowledgeViewController.cs"));
    True(knowledgeView.Contains("LoadKnowledgeAsync", StringComparison.Ordinal));
    True(knowledgeView.Contains("LoadKnowledgeCoverageAsync", StringComparison.Ordinal));
    True(knowledgeView.Contains("KnowledgeStatusText", StringComparison.Ordinal));
    True(knowledgeView.Contains("IsLibraryFallback", StringComparison.Ordinal));
    True(knowledgeView.Contains("KnowledgeCoverageMonthViewController", StringComparison.Ordinal));
    True(knowledgeView.Contains("RepresentativeEpisodeId", StringComparison.Ordinal));
    True(knowledgeView.Contains("Missing weekday", StringComparison.Ordinal));

    var metadataPills = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "MetadataPillsCell.cs"));
    True(metadataPills.Contains("Layer.CornerRadius = 16", StringComparison.Ordinal));
    True(metadataPills.Contains("Open related broadcasts and Explore articles", StringComparison.Ordinal));

    var nowPlayingQueue = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "NowPlayingUpNextView.cs"));
    True(nowPlayingQueue.Contains("SHARED QUEUE", StringComparison.Ordinal));
    True(nowPlayingQueue.Contains("PlayQueueItemAsync", StringComparison.Ordinal));
    True(nowPlayingQueue.Contains("RemoveQueueItemAsync", StringComparison.Ordinal));
    True(nowPlayingQueue.Contains("RadioVaultTheme.Accent", StringComparison.Ordinal));

    var downloadService = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.Mobile", "Services", "MobileDownloadService.cs"));
    True(downloadService.Contains("ReconcileSummariesAsync", StringComparison.Ordinal));
    True(downloadService.Contains("serverPlayedAt >= localPlayedAt", StringComparison.Ordinal));
    True(downloadService.Contains("record with { Summary = summary }", StringComparison.Ordinal));
    True(nowPlayingView.Contains("RadioVaultIcon.Handoff, RadioVaultTheme.Accent", StringComparison.Ordinal));

    var metadataCache = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.Mobile", "Services", "MobileMetadataCache.cs"));
    True(metadataCache.Contains("MobileMetadataCacheSnapshot", StringComparison.Ordinal));
    True(metadataCache.Contains("ApplyLibrarySync", StringComparison.Ordinal));
    True(metadataCache.Contains("ReplaceCompleteLibrary", StringComparison.Ordinal));
    True(metadataCache.Contains("SaveImageAsync", StringComparison.Ordinal));
    True(metadataCache.Contains("SaveArtworkAsync", StringComparison.Ordinal));
    True(metadataCache.Contains("BroadcastArtwork", StringComparison.Ordinal));

    var mobileCacheSessionSource = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.Mobile", "MobileClientSession.cs"));
    var mobilePlaybackOwnershipSource = File.ReadAllText(Path.Combine(
        SourceRoot(),
        "TheRadioVault.Client.Mobile",
        "Playback",
        "MobilePlaybackOwnershipCoordinator.cs"));
    var mobilePlaybackTimelineSource = File.ReadAllText(Path.Combine(
        SourceRoot(),
        "TheRadioVault.Client.Mobile",
        "Playback",
        "MobilePlaybackTimeline.cs"));
    var mobilePlaybackSynchronizationSource = File.ReadAllText(Path.Combine(
        SourceRoot(),
        "TheRadioVault.Client.Mobile",
        "Playback",
        "MobilePlaybackSynchronizationCoordinator.cs"));
    var mobileMetadataSynchronizationSource = File.ReadAllText(Path.Combine(
        SourceRoot(),
        "TheRadioVault.Client.Mobile",
        "Synchronization",
        "MobileMetadataSynchronizationCoordinator.cs"));
    var mobileExploreQuerySource = File.ReadAllText(Path.Combine(
        SourceRoot(),
        "TheRadioVault.Client.Mobile",
        "Explore",
        "MobileExploreQueryCoordinator.cs"));
    var mobileKnowledgeQuerySource = File.ReadAllText(Path.Combine(
        SourceRoot(),
        "TheRadioVault.Client.Mobile",
        "Knowledge",
        "MobileKnowledgeQueryCoordinator.cs"));
    var mobileLibraryQuerySource = File.ReadAllText(Path.Combine(
        SourceRoot(),
        "TheRadioVault.Client.Mobile",
        "Library",
        "MobileLibraryQueryCoordinator.cs"));
    var mobilePairingSource = File.ReadAllText(Path.Combine(
        SourceRoot(),
        "TheRadioVault.Client.Mobile",
        "Pairing",
        "MobilePairingCoordinator.cs"));
    var mobileDownloadedProgressSource = File.ReadAllText(Path.Combine(
        SourceRoot(),
        "TheRadioVault.Client.Mobile",
        "Synchronization",
        "MobileDownloadedProgressSynchronizationCoordinator.cs"));
    True(mobileCacheSessionSource.Contains("SynchronizeMetadataCacheAsync", StringComparison.Ordinal));
    True(mobileMetadataSynchronizationSource.Contains("SynchronizeLibraryAsync", StringComparison.Ordinal));
    True(mobileMetadataSynchronizationSource.Contains("FetchCompleteLibraryAsync", StringComparison.Ordinal));
    True(mobileMetadataSynchronizationSource.Contains("BootstrapEmptyCacheAsync", StringComparison.Ordinal));
    True(mobileMetadataSynchronizationSource.Contains("afterCacheApplied", StringComparison.Ordinal));
    True(mobileLibraryQuerySource.Contains("QueryCachedBroadcasts", StringComparison.Ordinal));
    True(mobileLibraryQuerySource.Contains("BuildCachedArchivePeriods", StringComparison.Ordinal));
    True(mobileExploreQuerySource.Contains("RefreshCacheAsync", StringComparison.Ordinal));
    True(mobileExploreQuerySource.Contains("BuildDashboard", StringComparison.Ordinal));
    True(mobileExploreQuerySource.Contains("LoadImagesAsync", StringComparison.Ordinal));
    True(mobileCacheSessionSource.Contains("IsMetadataSyncing", StringComparison.Ordinal));
    True(mobileCacheSessionSource.Contains("ApplyOnlineOverview", StringComparison.Ordinal));
    True(mobileMetadataSynchronizationSource.Contains("_gate.WaitAsync", StringComparison.Ordinal));
    True(mobilePlaybackOwnershipSource.Contains("ConfirmForeignOwner", StringComparison.Ordinal));
    True(mobileCacheSessionSource.Contains("ObserveSharedPlaybackSafelyAsync", StringComparison.Ordinal));
    True(mobilePlaybackSynchronizationSource.Contains("ObserveSafelyAsync", StringComparison.Ordinal));
    True(mobilePlaybackSynchronizationSource.Contains("ProjectPosition", StringComparison.Ordinal));
    True(mobilePlaybackSynchronizationSource.Contains("StopForCommittedTransferAsync", StringComparison.Ordinal));
    True(mobilePlaybackSynchronizationSource.Contains("AcknowledgePlaybackSourceStoppedAsync", StringComparison.Ordinal));
    True(mobilePlaybackOwnershipSource.Contains("if (!session.Player.IsPlaying)", StringComparison.Ordinal));
    True(mobilePlaybackOwnershipSource.Contains("WasCommittedAwayFromThisDevice", StringComparison.Ordinal));
    True(mobilePlaybackOwnershipSource.Contains("NeedsSourceStopAcknowledgement", StringComparison.Ordinal));
    True(mobilePlaybackOwnershipSource.Contains("_foreignOwnerCandidateSamples >= 2", StringComparison.Ordinal));
    True(mobileCacheSessionSource.Contains("SynchronizeDownloadedProgressWithServerAsync", StringComparison.Ordinal));
    True(mobileCacheSessionSource.Contains("SynchronizeStoredDownloadedProgressWithServerAsync", StringComparison.Ordinal));
    True(mobileDownloadedProgressSource.Contains("HasNewerOfflineProgress", StringComparison.Ordinal));
    True(mobileDownloadedProgressSource.Contains("AllowRewind: false", StringComparison.Ordinal));
    True(mobileDownloadedProgressSource.Contains("ExpectedGeneration: 0", StringComparison.Ordinal));
    True(mobileCacheSessionSource.Contains("_downloads.ReconcileSummariesAsync", StringComparison.Ordinal));
    True(mobileCacheSessionSource.Contains("ApplyPlaybackProgress", StringComparison.Ordinal));
    True(mobileCacheSessionSource.Contains("LoadArtworkAsync", StringComparison.Ordinal));
    True(mobileLibraryQuerySource.Contains("overview.ContinueListening", StringComparison.Ordinal));
    True(mobilePlaybackTimelineSource.Contains("_pendingDecoderLogicalPositionMs", StringComparison.Ordinal));
    True(mobilePlaybackTimelineSource.Contains("CaptureDecoderPosition", StringComparison.Ordinal));
    True(mobilePlaybackTimelineSource.Contains("TryGetNextPart", StringComparison.Ordinal));
    True(mobileCacheSessionSource.Contains("Finishing the startup sync", StringComparison.Ordinal));
    True(mobileCacheSessionSource.Contains("public string MiniPlayerTime", StringComparison.Ordinal));
    True(mobileLibraryQuerySource.Contains("CombineCollections", StringComparison.Ordinal));
    True(mobileLibraryQuerySource.Contains("NormalizeCollectionName", StringComparison.Ordinal));
    True(mobileKnowledgeQuerySource.Contains("BuildLibrarySnapshot", StringComparison.Ordinal));
    True(mobileKnowledgeQuerySource.Contains("BuildLibraryCoverage", StringComparison.Ordinal));
    True(mobileKnowledgeQuerySource.Contains("ResolveDateReviewAsync", StringComparison.Ordinal));
    True(mobilePairingSource.Contains("MobilePairingOperationResult", StringComparison.Ordinal));
    True(mobilePairingSource.Contains("PairManuallyAsync", StringComparison.Ordinal));
    True(mobilePairingSource.Contains("public void Forget", StringComparison.Ordinal));

    var iconGenerator = File.ReadAllText(Path.Combine(SourceRoot(), "design", "logo", "generate-brand-assets.py"));
    True(iconGenerator.Contains("RadioVault-logo-ios-source.png", StringComparison.Ordinal));
    True(File.Exists(Path.Combine(SourceRoot(), "TheRadioVault.Client.iOS", "Assets.xcassets", "AppIcon.appiconset", "AppIcon-1024.png")));

    var downloads = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.Mobile", "Services", "MobileDownloadService.cs"));
    True(downloads.Contains("ValidateManifest", StringComparison.Ordinal));
    True(downloads.Contains("partBytes != expectedBytes", StringComparison.Ordinal));
    True(downloads.Contains("FileOptions.WriteThrough", StringComparison.Ordinal));
    True(downloads.Contains("Directory.Move(stagingPath, finalPath)", StringComparison.Ordinal));
    True(downloads.Contains("ResolvePartPath", StringComparison.Ordinal));
    True(downloads.Contains("PartialContent", StringComparison.Ordinal));
    True(downloads.Contains("FileMode.Append", StringComparison.Ordinal));
    True(downloads.Contains("DiscardPendingAsync", StringComparison.Ordinal));
    True(downloads.Contains("GetStorageAsync", StringComparison.Ordinal));
    True(downloads.Contains("RemoveCompletedAsync", StringComparison.Ordinal));
    True(downloads.Contains("TrimToLimitAsync", StringComparison.Ordinal));
    True(downloads.Contains("RepairAsync", StringComparison.Ordinal));

    var downloadCoordinator = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.Mobile", "Downloads", "MobileDownloadCoordinator.cs"));
    True(downloadCoordinator.Contains("SelectAutomaticDownload", StringComparison.Ordinal));
    True(downloadCoordinator.Contains("ResumeAsync", StringComparison.Ordinal));
    True(downloadCoordinator.Contains("CleanupCompletedAsync", StringComparison.Ordinal));
    True(downloadCoordinator.Contains("protectedEpisodeId", StringComparison.Ordinal));
    True(downloadCoordinator.Contains("ReconcileSummariesAsync", StringComparison.Ordinal));

    var downloadedProgressSynchronization = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.Mobile", "Synchronization",
        "MobileDownloadedProgressSynchronizationCoordinator.cs"));
    True(downloadedProgressSynchronization.Contains("HasNewerOfflineProgress", StringComparison.Ordinal));
    True(downloadedProgressSynchronization.Contains("if (result.Conflict)", StringComparison.Ordinal));
    True(downloadedProgressSynchronization.Contains("IncrementPlayCount", StringComparison.Ordinal));

    var downloadsView = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "DownloadsViewController.cs"));
    True(downloadsView.Contains("PauseDownload", StringComparison.Ordinal));
    True(downloadsView.Contains("ResumeDownloadAsync", StringComparison.Ordinal));
    True(downloadsView.Contains("CancelDownload", StringComparison.Ordinal));
    True(!downloadsView.Contains("WifiOnlyDownloads", StringComparison.Ordinal));

    var settingsView = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "ServerViewController.cs"));
    True(settingsView.Contains("Title = \"Settings\"", StringComparison.Ordinal));
    True(settingsView.Contains("Download Settings", StringComparison.Ordinal));
    True(settingsView.Contains("WifiOnlyDownloads", StringComparison.Ordinal));
    True(settingsView.Contains("DownloadStorageText", StringComparison.Ordinal));
    True(settingsView.Contains("AutoDownloadNewBroadcasts", StringComparison.Ordinal));
    True(settingsView.Contains("DeleteCompletedDownloads", StringComparison.Ordinal));
    True(settingsView.Contains("PresentStorageLimitPicker", StringComparison.Ordinal));
    True(settingsView.Contains("SyncDiagnosticsViewController", StringComparison.Ordinal));
    True(settingsView.Contains("Pair using entered address", StringComparison.Ordinal));
    True(settingsView.Contains("PairManuallyAsync", StringComparison.Ordinal));

    var downloadPolicy = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "IosDownloadPolicy.cs"));
    True(downloadPolicy.Contains("NWPathMonitor", StringComparison.Ordinal));
    True(downloadPolicy.Contains("NWInterfaceType.Wifi", StringComparison.Ordinal));
    True(downloadPolicy.Contains("NSUserDefaults", StringComparison.Ordinal));
    True(downloadPolicy.Contains("AutoDownloadSince", StringComparison.Ordinal));
    True(downloadPolicy.Contains("StorageLimitBytes", StringComparison.Ordinal));

    var scene = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "SceneDelegate.cs"));
    True(scene.Contains("IUIWindowSceneDelegate", StringComparison.Ordinal));
    True(scene.Contains("RadioVaultTabBarController", StringComparison.Ordinal));

    var protocolProject = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Protocol", "TheRadioVault.Protocol.csproj"));
    True(protocolProject.Contains("WebApiRoutes.cs", StringComparison.Ordinal));
    True(protocolProject.Contains("WebModels.cs", StringComparison.Ordinal));

    var engine = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "IosAvPlayerEngine.cs"));
    True(engine.Contains("AVPlayer", StringComparison.Ordinal));
    True(engine.Contains("AVAudioSessionCategory.Playback", StringComparison.Ordinal));
    True(engine.Contains("SetMuted", StringComparison.Ordinal));
    True(engine.Contains("ObserveInterruption", StringComparison.Ordinal));
    True(engine.Contains("ObserveRouteChange", StringComparison.Ordinal));
    True(engine.Contains("OldDeviceUnavailable", StringComparison.Ordinal));

    var nowPlaying = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "IosNowPlayingService.cs"));
    True(nowPlaying.Contains("MPNowPlayingInfoCenter", StringComparison.Ordinal));
    True(nowPlaying.Contains("MPRemoteCommandCenter", StringComparison.Ordinal));
    True(nowPlaying.Contains("ChangePlaybackPositionCommand", StringComparison.Ordinal));
    True(nowPlaying.Contains("SkipBackwardCommand", StringComparison.Ordinal));
    True(nowPlaying.Contains("MPMediaItemArtwork", StringComparison.Ordinal));
    True(nowPlaying.Contains("QueueUpdate", StringComparison.Ordinal));
    True(nowPlaying.Contains("BeginInvokeOnMainThread(ApplyPendingUpdate)", StringComparison.Ordinal));
    True(nowPlaying.Contains("SetCommandsEnabled(available: false", StringComparison.Ordinal));
    True(nowPlaying.Contains("SequenceEqual", StringComparison.Ordinal));

    var mobileSession = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.Mobile", "MobileClientSession.cs"));
    True(mobileSession.Contains("WebPlaybackTransferBeginRequest", StringComparison.Ordinal));
    True(mobileSession.Contains("WebPlaybackTransferReadyRequest", StringComparison.Ordinal));
    True(mobileSession.Contains("WebPlaybackTransferCommitRequest", StringComparison.Ordinal));
    True(mobilePlaybackSynchronizationSource.Contains("WebPlaybackTransferSourceStoppedRequest", StringComparison.Ordinal));
    True(mobileSession.Contains("WebOfflineProgressUpdate", StringComparison.Ordinal));
    True(mobileSession.Contains("DurableProgressInterval", StringComparison.Ordinal));
    True(mobileSession.Contains("DownloadedBroadcasts", StringComparison.Ordinal));
    True(mobileSession.Contains("PlayDownloadedAsync", StringComparison.Ordinal));
    True(mobileSession.Contains("ObserveSharedPlaybackAsync", StringComparison.Ordinal));
    True(mobileSession.Contains("MiniPlayerShowsHandoff", StringComparison.Ordinal));
    True(mobileSession.Contains("QueueItems", StringComparison.Ordinal));
    True(mobileSession.Contains("IsDownloadPaused", StringComparison.Ordinal));
    True(mobileSession.Contains("LoadExploreDashboardAsync", StringComparison.Ordinal));
    True(mobileSession.Contains("LoadExplorePageAsync", StringComparison.Ordinal));
    True(mobileSession.Contains("PairManuallyAsync", StringComparison.Ordinal));
    True(mobileSession.Contains("FlushOfflineMutationsAsync", StringComparison.Ordinal));
    True(mobileSession.Contains("SetListeningStatusAsync", StringComparison.Ordinal));
    True(mobileSession.Contains("TryAutomaticDownloadAsync", StringComparison.Ordinal));
    True(mobileSession.Contains("LoadKnowledgeAsync", StringComparison.Ordinal));
    True(mobileSession.Contains("LoadKnowledgeCoverageAsync", StringComparison.Ordinal));
    True(mobileSession.Contains("new MobileDownloadCoordinator", StringComparison.Ordinal));
    True(!mobileSession.Contains("_downloadCancellation", StringComparison.Ordinal));

    var pendingChanges = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.Mobile", "Services", "MobileOfflineMutationStore.cs"));
    True(pendingChanges.Contains("pending-changes.json", StringComparison.Ordinal));
    True(pendingChanges.Contains("EnqueueFavouriteAsync", StringComparison.Ordinal));
    True(pendingChanges.Contains("EnqueueListeningStatusAsync", StringComparison.Ordinal));
    True(pendingChanges.Contains("EnqueueMomentAsync", StringComparison.Ordinal));
    True(pendingChanges.Contains("FileOptions.WriteThrough", StringComparison.Ordinal));

    var offlineMutationSynchronization = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.Mobile", "Synchronization",
        "MobileOfflineMutationSynchronizationCoordinator.cs"));
    True(offlineMutationSynchronization.Contains("_gate.WaitAsync", StringComparison.Ordinal));
    True(offlineMutationSynchronization.Contains("MutationAlreadyAppliedAsync", StringComparison.Ordinal));
    True(offlineMutationSynchronization.Contains("!result.Changed && !result.Duplicate", StringComparison.Ordinal));
    True(offlineMutationSynchronization.Contains("MarkFailedAsync", StringComparison.Ordinal));
    True(offlineMutationSynchronization.Contains("break;", StringComparison.Ordinal));

    var syncDiagnostics = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "SyncDiagnosticsViewController.cs"));
    True(syncDiagnostics.Contains("Pending Changes", StringComparison.Ordinal));
    True(syncDiagnostics.Contains("RetrySyncAsync", StringComparison.Ordinal));

    var testFlight = File.ReadAllText(Path.Combine(
        SourceRoot(), "tools", "Build-iOS-TestFlight.command"));
    True(testFlight.Contains("ArchiveOnBuild=true", StringComparison.Ordinal));
    True(testFlight.Contains("BuildIpa=true", StringComparison.Ordinal));
    True(testFlight.Contains("never upload", StringComparison.OrdinalIgnoreCase));

    var keychain = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "IosKeychainConnectionStore.cs"));
    True(keychain.Contains("SecKeyChain", StringComparison.Ordinal));
    True(keychain.Contains("AfterFirstUnlockThisDeviceOnly", StringComparison.Ordinal));

    var client = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.Mobile", "Services", "MobileServerClient.cs"));
    True(client.Contains("WebApiRoutes", StringComparison.Ordinal));
    True(client.Contains("X-RadioVault-Token", StringComparison.Ordinal));
    True(client.Contains("ServerCertificateCustomValidationCallback", StringComparison.Ordinal));
    True(client.Contains("PlayerWebProgress", StringComparison.Ordinal));
    True(client.Contains("PlayerTransferBegin", StringComparison.Ordinal));
    True(client.Contains("OfflineProgress", StringComparison.Ordinal));
    True(client.Contains("ClientBroadcast(episodeId)", StringComparison.Ordinal));
    True(client.Contains("QueueAdd", StringComparison.Ordinal));
    True(client.Contains("QueueRemove", StringComparison.Ordinal));
    True(client.Contains("QueueMove", StringComparison.Ordinal));
    True(client.Contains("QueueClear", StringComparison.Ordinal));
    True(client.Contains("Favourite(episodeId)", StringComparison.Ordinal));
    True(client.Contains("ClientLibraryArchivePeriods", StringComparison.Ordinal));
    True(client.Contains("ClientWikiOperation(\"overview\")", StringComparison.Ordinal));
    True(client.Contains("ClientWikiOperation(\"browse\")", StringComparison.Ordinal));
    True(client.Contains("ClientWikiOperation(\"dashboard-highlights\")", StringComparison.Ordinal));
    True(client.Contains("ClientWikiOperation(\"page\")", StringComparison.Ordinal));
    True(client.Contains("ClientResearchOperation(\"overview\")", StringComparison.Ordinal));
    True(client.Contains("ClientResearchOperation(\"coverage\")", StringComparison.Ordinal));
    True(client.Contains("PairManuallyAsync", StringComparison.Ordinal));
    True(client.Contains("observedThumbprint", StringComparison.Ordinal));

    var plist = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "Info.plist"));
    True(plist.Contains("NSLocalNetworkUsageDescription", StringComparison.Ordinal));
    True(plist.Contains("NSAllowsLocalNetworking", StringComparison.Ordinal));
    True(plist.Contains("UIBackgroundModes", StringComparison.Ordinal));
    True(plist.Contains("<string>audio</string>", StringComparison.Ordinal));
    True(plist.Contains("UIApplicationSceneManifest", StringComparison.Ordinal));
    True(plist.Contains("UILaunchScreen", StringComparison.Ordinal));
    True(File.Exists(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "Assets.xcassets", "AppIcon.appiconset", "AppIcon-1024.png")));

    var entitlements = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "Entitlements.device.plist"));
    True(entitlements.Contains("com.apple.developer.networking.multicast", StringComparison.Ordinal));
}

static void Alpha19UsesTruthfulCacheFirstStartup()
{
    var app = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "App.axaml.cs"));
    var cacheHydration = app.IndexOf("await host.InitializeStartupAsync()", StringComparison.Ordinal);
    var mainWindowCreation = app.IndexOf("var mainWindow = new MainWindow", StringComparison.Ordinal);
    True(cacheHydration >= 0 && mainWindowCreation > cacheHydration);
    True(app.Contains("RefreshStartupCacheAfterLaunchAsync", StringComparison.Ordinal));

    var splash = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "StartupWindow.axaml"));
    True(splash.Contains("STARTING RADIO VAULT", StringComparison.Ordinal));
    True(splash.Contains("ConnectionModeText", StringComparison.Ordinal));
    True(splash.Contains("StartupCacheText", StringComparison.Ordinal));
    True(!splash.Contains("Local database", StringComparison.Ordinal));
    True(!splash.Contains("Audio stays on this computer", StringComparison.Ordinal));

    var connection = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Infrastructure", "Services", "LoopbackServerClient.cs"));
    True(connection.Contains("UsePersistentCacheFirst()", StringComparison.Ordinal));
    True(connection.Contains("GetLiveJsonAsync", StringComparison.Ordinal));

    var sync = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Infrastructure", "Services", "NativeClientCacheSyncService.cs"));
    True(sync.Contains("metadataOnly=true", StringComparison.Ordinal));
    True(sync.Contains("ToHashSet(StringComparer.OrdinalIgnoreCase)", StringComparison.Ordinal));

    var main = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "MainWindowViewModel.cs"));
    True(main.Contains("RefreshAfterServerSyncAsync", StringComparison.Ordinal));
    True(main.Contains("Changed(\"moment\")", StringComparison.Ordinal));
    True(main.Contains("Changed(\"queue\")", StringComparison.Ordinal));

    var serverSync = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Web", "Services", "LocalWebServer.FederationLibrarySync.cs"));
    True(serverSync.Contains("!noChanges && !metadataOnly", StringComparison.Ordinal));
}

static void Alpha20HardensReleaseTruthAndInstallerPayloads()
{
    var host = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Composition", "AvaloniaApplicationHost.cs"));
    True(host.Contains("AppVersionService.DisplayVersion", StringComparison.Ordinal));
    True(!host.Contains("Alpha 19 Buildfix", StringComparison.Ordinal));

    var research = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Research", "AvaloniaResearchPackTransferServices.cs"));
    True(research.Contains("AppVersion = AppVersionService.Version", StringComparison.Ordinal));

    var settings = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "DesktopToolsViewModel.cs"));
    True(settings.Contains("Models and processing on the active server", StringComparison.Ordinal));
    True(!settings.Contains("Generation 24", StringComparison.Ordinal));
    True(!settings.Contains("Local whisper.cpp configuration", StringComparison.Ordinal));

    var connection = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "ConnectedAccessViewModel.cs"));
    True(connection.Contains("Snapshot.CapabilityGeneration", StringComparison.Ordinal));
    True(connection.Contains("negotiated with the active server", StringComparison.Ordinal));

    var settingsView = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "DesktopToolsView.axaml"));
    True(settingsView.Contains("Server/client compatibility", StringComparison.Ordinal));
    True(settingsView.Contains("ConnectedAccess.CapabilityGenerationText", StringComparison.Ordinal));
    True(!settingsView.Contains("Anywhere compatibility", StringComparison.Ordinal));

    var localConnection = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Local", "LocalOnlyConnectivityServices.cs"));
    True(!localConnection.Contains("post-1.0 roadmap", StringComparison.Ordinal));

    foreach (var scriptName in new[] { "package-client-installer.ps1", "package-server-installer.ps1" })
    {
        var script = File.ReadAllText(Path.Combine(SourceRoot(), scriptName));
        var packageCall = script.IndexOf("& (Join-Path $root \"package-", StringComparison.Ordinal);
        var payloadCheck = script.IndexOf("if (-not (Test-Path $", StringComparison.Ordinal);
        var compilerLookup = script.IndexOf("$compilerCandidates", StringComparison.Ordinal);
        True(packageCall >= 0 && payloadCheck > packageCall && compilerLookup > payloadCheck);
    }

    True(File.Exists(Path.Combine(SourceRoot(), "docs/history/release-notes/V0.34.0-ALPHA20-RELEASE-HARDENING.md")));
}

static void Rc1FreezesRecoveryAndUpgradePreservation()
{
    var access = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Infrastructure", "Services", "NativeConnectedAccessService.cs"));
    True(access.Contains("var recovered = !_current.IsLive;", StringComparison.Ordinal));
    True(access.Contains("MarkServerLive(invalidateMemoryCache: recovered)", StringComparison.Ordinal));

    var web = ReadWebServerSourceBundle();
    True(web.Contains("var startListener = _listener!;", StringComparison.Ordinal));
    True(web.Contains("AcceptLoopAsync(startListener", StringComparison.Ordinal));
    True(!web.Contains("AcceptLoopAsync(_listener!", StringComparison.Ordinal));

    foreach (var installerName in new[] { "RadioVault.Client.iss", "RadioVault.Server.iss" })
    {
        var installer = File.ReadAllText(Path.Combine(SourceRoot(), "installer", installerName));
        True(installer.Contains("CloseApplications=yes", StringComparison.Ordinal));
        True(installer.Contains("UsePreviousAppDir=yes", StringComparison.Ordinal));
        True(installer.Contains("UsePreviousTasks=yes", StringComparison.Ordinal));
        True(installer.Contains("SetupLogging=yes", StringComparison.Ordinal));
        True(!installer.Contains("AppPaths.DataDirectory", StringComparison.Ordinal));
    }

    True(File.Exists(Path.Combine(SourceRoot(), "docs/history/release-notes/V0.34.0-RC1-STABILITY.md")));
}

static void Rc1BuildfixRestoresVisibleResearchPackImport()
{
    Equal(512 * 1024 * 1024, WebResearchPackLimits.MaximumPackageBytes);

    var client = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Infrastructure", "Services", "LoopbackServerClient.cs"));
    True(client.Contains("CreateServerErrorAsync", StringComparison.Ordinal));
    True(client.Contains("diagnosticId", StringComparison.Ordinal));

    var transfer = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Infrastructure", "Services", "LoopbackResearchServices.cs"));
    True(transfer.Contains("WebResearchPackLimits.MaximumPackageBytes", StringComparison.Ordinal));
    True(transfer.Contains("The selected Archive Knowledge Database is empty.", StringComparison.Ordinal));

    var viewModel = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "ResearchWorkspaceViewModel.cs"));
    True(viewModel.Contains("ImportErrorText", StringComparison.Ordinal));
    True(viewModel.Contains("RetryImportCommand", StringComparison.Ordinal));
    True(viewModel.Contains("RaiseImportFeedbackState", StringComparison.Ordinal));

    var view = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "ResearchWorkspaceView.axaml"));
    True(view.Contains("This Knowledge import could not finish", StringComparison.Ordinal));
    True(view.Contains("No partial changes were kept", StringComparison.Ordinal));
    True(view.Contains("RetryImportCommand", StringComparison.Ordinal));

    var server = ReadWebServerSourceBundle();
    True(server.Contains("WebResearchPackLimits.MaximumPackageBytes", StringComparison.Ordinal));
    True(server.Contains("TimeSpan.FromMinutes(10)", StringComparison.Ordinal));
}

static void Rc1Buildfix4UnifiesClientUiAndNativeDownloads()
{
    var splash = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "StartupWindow.axaml"));
    True(splash.Contains("STARTING RADIO VAULT", StringComparison.Ordinal));
    True(splash.Contains("Width=\"132\" Height=\"132\" Source=\"/Assets/RadioVault-Logo.png\"", StringComparison.Ordinal));
    True(!splash.Contains("RvTranscriptBrush", StringComparison.Ordinal));

    var web = ReadWebServerSourceBundle();
    True(web.Contains("radio-vault-anywhere-shell-v68", StringComparison.Ordinal));
    True(web.Contains("class=\"menuToggle\"", StringComparison.Ordinal));
    True(web.Contains("id=\"menuScrim\"", StringComparison.Ordinal));
    True(web.Contains("body.menuOpen", StringComparison.Ordinal));
    True(web.Contains("height:calc(100dvh - (max(14px, env(safe-area-inset-top)) + 76px))", StringComparison.Ordinal));
    True(web.Contains("class=\"libraryPrimaryAction\"", StringComparison.Ordinal));
    True(web.Contains("/app-icon-180.png?token=__TOKEN__&v=__APP_VERSION__", StringComparison.Ordinal));
    True(web.Contains("WebRequestLifecycleKind.AppIcon", StringComparison.Ordinal));
    True(web.Contains("TryGetWebAppIcon(context.Path", StringComparison.Ordinal));

    var generator = File.ReadAllText(Path.Combine(
        SourceRoot(), "design", "logo", "generate-brand-assets.py"));
    True(generator.Contains("contain(mark, 180, 0.035, ACCENT).convert(\"RGB\")", StringComparison.Ordinal));

    var navigation = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "MainWindowViewModel.cs"));
    True(navigation.Contains("new ShellNavigationItemViewModel(\"downloads\", \"Downloads\"", StringComparison.Ordinal));

    var host = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Composition", "AvaloniaApplicationHost.cs"));
    True(host.Contains("RegisterSingleton<INativeDownloadService>", StringComparison.Ordinal));
    True(host.Contains("\"Downloads\", downloadScope", StringComparison.Ordinal));

    True(File.Exists(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "DownloadsView.axaml")));
    True(File.Exists(Path.Combine(
        SourceRoot(), "TheRadioVault.Infrastructure", "Services", "NativeDownloadService.cs")));

    var shell = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "MainWindow.axaml"));
    var desktopTheme = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "App.axaml"));
    True(shell.Contains("PrimaryTransportIconTemplate", StringComparison.Ordinal));
    True(desktopTheme.Contains("Move playback to this PC", StringComparison.Ordinal));
    True(!shell.Contains("Content=\"⇥\"", StringComparison.Ordinal));

    True(File.Exists(Path.Combine(SourceRoot(), "docs/history/release-notes/V0.34.0-RC1-BUILDFIX4-CLIENT-UI-DOWNLOADS.md")));
}

static void Alpha035BeginsWikiWithoutBreakingStableUpgrades()
{
    Equal("0.41.0", File.ReadAllText(Path.Combine(SourceRoot(), "VERSION.txt")).Trim());

    foreach (var projectPath in new[]
             {
                 Path.Combine("TheRadioVault.Desktop.Avalonia", "TheRadioVault.Desktop.Avalonia.csproj"),
                 Path.Combine("TheRadioVault.Server", "TheRadioVault.Server.csproj")
             })
    {
        var project = File.ReadAllText(Path.Combine(SourceRoot(), projectPath));
        True(project.Contains("<Version>0.41.0</Version>", StringComparison.Ordinal));
        True(project.Contains("<InformationalVersion>0.41.0</InformationalVersion>", StringComparison.Ordinal));
    }

    var readme = File.ReadAllText(Path.Combine(SourceRoot(), "README.md"));
    True(readme.Contains("<h1 align=\"center\">Radio Vault</h1>", StringComparison.Ordinal));
    True(readme.Contains("Bring your old radio collection back to life.", StringComparison.Ordinal));
    True(readme.Contains("browse your collection by show, year, month and broadcast", StringComparison.Ordinal));
    True(readme.Contains("Radio Vault Server", StringComparison.Ordinal));
    True(readme.Contains("Radio Vault Web", StringComparison.Ordinal));
    True(readme.Contains("Your collection stays on your own hardware", StringComparison.Ordinal));
    True(readme.Contains("should not be exposed directly to the public internet", StringComparison.Ordinal));
    True(readme.Contains("currently an **alpha** release", StringComparison.Ordinal));
    True(readme.Contains("## Download the latest test builds", StringComparison.Ordinal));
    True(readme.Contains("actions/workflows/ci.yml?query=branch%3Amain", StringComparison.Ordinal));
    True(readme.Contains("windows-client-and-server", StringComparison.Ordinal));
    True(readme.Contains("macos-client-and-server-osx-arm64-unsigned", StringComparison.Ordinal));
    True(readme.Contains("linux-client-and-server-x64", StringComparison.Ordinal));
    True(readme.Contains("ios-client-simulator-arm64-unsigned", StringComparison.Ordinal));
    True(readme.Contains("## AI disclosure", StringComparison.Ordinal));
    True(readme.Contains("does not contain a generative-AI assistant", StringComparison.Ordinal));
    True(readme.Contains("speech-recognition models installed and run locally", StringComparison.Ordinal));
    True(!readme.Contains("repository is currently private", StringComparison.OrdinalIgnoreCase));

    var building = File.ReadAllText(Path.Combine(SourceRoot(), "BUILDING.md"));
    True(building.StartsWith("# Building Radio Vault 0.41.0", StringComparison.Ordinal));
    True(building.Contains("package-server-installer.ps1", StringComparison.Ordinal));
    True(building.Contains("package-client-installer.ps1", StringComparison.Ordinal));
    True(!building.Contains("subsequent 0.34 phases", StringComparison.Ordinal));

    var foundation = File.ReadAllText(Path.Combine(SourceRoot(), "tools", "Test-AvaloniaFoundation.ps1"));
    True(foundation.Contains("foundationVersion = '0.35-alpha9-knowledge-portability'", StringComparison.Ordinal));
    True(foundation.Contains("databaseSchema = 51", StringComparison.Ordinal));
    True(foundation.Contains("lanCapabilityGeneration = 41", StringComparison.Ordinal));
    foreach (var marker in new[]
             {
                 "remoteClientMigrated = $true", "connectedAccessWorkspaceMigrated = $true",
                 "encryptedRemoteCache = $true", "automaticReconnect = $true",
                 "remotePlaybackMigrated = $true", "remoteProgressWriteThrough = $true"
             })
        True(foundation.Contains(marker, StringComparison.Ordinal));

    var web = ReadWebServerSourceBundle();
    True(web.Contains("radio-vault-anywhere-shell-v68", StringComparison.Ordinal));

    var truthService = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Services", "Services", "LibraryTruthAdoptionService.cs"));
    True(!truthService.Contains("Run and complete an Alpha10", StringComparison.Ordinal));
    True(!truthService.Contains("sealed Alpha10 plan", StringComparison.Ordinal));

    foreach (var installerName in new[] { "RadioVault.Client.iss", "RadioVault.Server.iss" })
    {
        var installer = File.ReadAllText(Path.Combine(SourceRoot(), "installer", installerName));
        True(installer.Contains("UsePreviousAppDir=yes", StringComparison.Ordinal));
        True(installer.Contains("UsePreviousTasks=yes", StringComparison.Ordinal));
        True(installer.Contains("stored separately", StringComparison.Ordinal));
    }

    var sourcePackaging = File.ReadAllText(Path.Combine(SourceRoot(), "tools", "Package-Source.ps1"));
    True(sourcePackaging.Contains("$rootInstaller", StringComparison.Ordinal));
    True(sourcePackaging.Contains("RadioVault\\.(Client|Server)", StringComparison.Ordinal));

    using var sourceManifest = System.Text.Json.JsonDocument.Parse(
        File.ReadAllText(Path.Combine(SourceRoot(), "SOURCE_MANIFEST.sha256.json")));
    Equal("0.41.0", sourceManifest.RootElement.GetProperty("version").GetString());

    foreach (var projectPath in new[]
             {
                 Path.Combine("TheRadioVault.Data", "TheRadioVault.Data.csproj"),
                 Path.Combine("TheRadioVault.Infrastructure", "TheRadioVault.Infrastructure.csproj"),
                 Path.Combine("TheRadioVault.Services", "TheRadioVault.Services.csproj"),
                 Path.Combine("TheRadioVault.Transcription", "TheRadioVault.Transcription.csproj")
             })
    {
        var project = File.ReadAllText(Path.Combine(SourceRoot(), projectPath));
        True(project.Contains("Microsoft.Data.Sqlite\" Version=\"8.0.29\"", StringComparison.Ordinal));
        True(!project.Contains("Microsoft.Data.Sqlite\" Version=\"8.0.7\"", StringComparison.Ordinal));
    }
    var dataProject = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Data", "TheRadioVault.Data.csproj"));
    True(dataProject.Contains("SQLitePCLRaw.bundle_e_sqlite3\" Version=\"2.1.12\"", StringComparison.Ordinal));

    True(File.Exists(Path.Combine(SourceRoot(), "docs/history/release-notes/V0.34.0-STABLE.md")));
    True(File.Exists(Path.Combine(SourceRoot(), "docs/history/release-notes/V0.35.0-ALPHA1-WIKI-FOUNDATION.md")));
    True(File.Exists(Path.Combine(SourceRoot(), "docs/history/release-notes/V0.35.0-ALPHA1-BUILDFIX1-RESEARCH-PACK-TOLERANCE.md")));
}

static void NativeDownloadsPersistAuditAndPrepareLocalMedia()
{
    var root = Path.Combine(Path.GetTempPath(), "rv-native-download-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var records = Path.Combine(root, "records");
        var generation = Path.Combine(root, "media", "42", "generation-a");
        Directory.CreateDirectory(records);
        Directory.CreateDirectory(generation);
        var mediaPath = Path.Combine(generation, "part-001-7.mp3");
        File.WriteAllBytes(mediaPath, new byte[] { 1, 2, 3, 4, 5 });

        var part = new NativeDownloadPart(
            1, 1, 0, 60_000, 7, 5,
            Path.Combine("media", "42", "generation-a", "part-001-7.mp3"),
            "audio/mpeg");
        var record = new NativeDownloadRecord(
            42, "CANONICAL-42", "RECORDING-42", "BROADCAST-42", "A downloaded show",
            "Ron & Fez", new DateOnly(2005, 5, 12), null, 12_000, 60_000, 1.25,
            false, true, DateTimeOffset.UtcNow, 5, new[] { part });
        File.WriteAllBytes(
            Path.Combine(records, "42.json"),
            JsonSerializer.SerializeToUtf8Bytes(record, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        using var connection = new LoopbackServerClient(new WebServerPreferences { Enabled = false });
        var service = new NativeDownloadService(connection, root);
        var loaded = service.GetDownloadsAsync().GetAwaiter().GetResult();
        Equal(1, loaded.Count);
        True(!loaded[0].NeedsRepair);

        var prepared = service.TryPrepareAsync("CANONICAL-42", 42).GetAwaiter().GetResult();
        True(prepared is not null);
        Equal(12_000L, prepared!.ResumePositionMs);
        Equal(1, prepared.Segments.Count);
        Equal(Path.GetFullPath(mediaPath), Path.GetFullPath(prepared.Segments[0].MediaPath));

        File.Delete(mediaPath);
        var audit = service.AuditAsync().GetAwaiter().GetResult();
        Equal(1, audit.Checked);
        Equal(1, audit.NeedsRepair);
        True(service.TryPrepareAsync("CANONICAL-42", 42).GetAwaiter().GetResult() is null);
    }
    finally
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

static void NativeDownloadPoliciesExpireAndTrimSafely()
{
    var root = Path.Combine(Path.GetTempPath(), "rv-native-retention-test-" + Guid.NewGuid().ToString("N"));
    var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
    try
    {
        var records = Path.Combine(root, "records");
        Directory.CreateDirectory(records);
        WriteRecord(1, completed: true, downloadedAt: now.AddHours(-1), lastAccessedAt: null);
        WriteRecord(2, completed: false, downloadedAt: now.AddDays(-20), lastAccessedAt: null);
        WriteRecord(3, completed: false, downloadedAt: now.AddDays(-20), lastAccessedAt: null);
        WriteRecord(4, completed: false, downloadedAt: now.AddHours(-2), lastAccessedAt: now.AddHours(-1));

        var preferencesPath = Path.Combine(root, "preferences.json");
        var preferencesStore = new NativeDownloadPreferencesStore(preferencesPath);
        preferencesStore.Save(new NativeDownloadPreferences
        {
            AutomaticDownloadsEnabled = true,
            AutomaticDownloadSince = now,
            AutomaticDownloadWatermarkEpisodeId = 44,
            DeleteCompletedDownloads = true,
            DownloadExpiryDays = 7,
            StorageLimitBytes = 5
        });
        var restored = preferencesStore.Load();
        True(restored.AutomaticDownloadsEnabled);
        Equal(44L, restored.AutomaticDownloadWatermarkEpisodeId);
        Equal(7, restored.DownloadExpiryDays);
        Equal(5L, restored.StorageLimitBytes);

        using var connection = new LoopbackServerClient(new WebServerPreferences { Enabled = false });
        var service = new NativeDownloadService(connection, root);
        var result = service.MaintainAsync(
            new NativeDownloadMaintenancePolicy(true, 7, 5, now),
            protectedEpisodeId: 3).GetAwaiter().GetResult();
        Equal(1, result.RemovedCompleted);
        Equal(1, result.RemovedExpired);
        Equal(1, result.RemovedForStorage);
        Equal(15L, result.BytesFreed);
        var remaining = service.GetDownloadsAsync().GetAwaiter().GetResult();
        Equal(1, remaining.Count);
        Equal(3L, remaining[0].RepresentativeEpisodeId);

        void WriteRecord(long episodeId, bool completed, DateTimeOffset downloadedAt, DateTimeOffset? lastAccessedAt)
        {
            var generation = Path.Combine(root, "media", episodeId.ToString(), "generation-a");
            Directory.CreateDirectory(generation);
            var fileName = $"part-001-{episodeId}.mp3";
            File.WriteAllBytes(Path.Combine(generation, fileName), [1, 2, 3, 4, 5]);
            var part = new NativeDownloadPart(
                1, 1, 0, 60_000, episodeId, 5,
                Path.Combine("media", episodeId.ToString(), "generation-a", fileName),
                "audio/mpeg");
            var record = new NativeDownloadRecord(
                episodeId, $"CANONICAL-{episodeId}", $"RECORDING-{episodeId}", $"BROADCAST-{episodeId}",
                $"Download {episodeId}", "Test", new DateOnly(2026, 8, 13), null,
                0, 60_000, 1, completed, false, downloadedAt, 5, [part], string.Empty, lastAccessedAt);
            File.WriteAllBytes(
                Path.Combine(records, episodeId + ".json"),
                JsonSerializer.SerializeToUtf8Bytes(record, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
    }
    finally
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

static void NativeServerCacheEncryptsResponses()
{
    var root = Path.Combine(Path.GetTempPath(), "rv-native-cache-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var preferences = new NativeServerConnectionPreferences
        {
            ServerInstanceId = Guid.NewGuid().ToString("D"),
            AccessToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
        };
        var cache = new NativeServerResponseCache(preferences, root);
        var path = "/api/v1/client/library/overview";
        var expected = Encoding.UTF8.GetBytes("{\"private\":\"server library snapshot\"}");
        cache.Store(path, expected);
        True(cache.TryLoad(path, out var restored));
        Equal(Convert.ToHexString(expected), Convert.ToHexString(restored));
        True(cache.SizeBytes > expected.Length);

        var file = Directory.EnumerateFiles(root, "*.rvcache", SearchOption.AllDirectories).Single();
        var encrypted = File.ReadAllBytes(file);
        True(!Encoding.UTF8.GetString(encrypted).Contains("server library snapshot", StringComparison.Ordinal));
        encrypted[^1] ^= 0x5A;
        File.WriteAllBytes(file, encrypted);
        True(!cache.TryLoad(path, out _));
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static void NativeClientStartupPrefersPersistentCache()
{
    var root = Path.Combine(Path.GetTempPath(), "rv-native-warm-start-" + Guid.NewGuid().ToString("N"));
    var preferences = new NativeServerConnectionPreferences
    {
        ServerInstanceId = Guid.NewGuid().ToString("D"),
        ServerDisplayName = "Unavailable test server",
        ServerAddress = "127.0.0.1",
        SecurePort = 65530,
        CertificateThumbprint = new string('A', 64),
        AccessToken = new string('B', 64),
        UseRemoteOnStartup = true
    };
    const string path = "/api/v1/client/test-warm-start";
    try
    {
        var cache = new NativeServerResponseCache(preferences, root);
        cache.Store(path, Encoding.UTF8.GetBytes("{\"source\":\"encrypted-disk-cache\"}"));
        using var client = new LoopbackServerClient(
            remotePreferences: preferences,
            useRemoteServer: true,
            responseCacheRoot: root);
        using (client.UsePersistentCacheFirst())
        {
            var value = client.SendJsonAsync<System.Text.Json.JsonElement>(HttpMethod.Get, path)
                .GetAwaiter().GetResult();
            Equal("encrypted-disk-cache", value.GetProperty("source").GetString());
        }
        True(client.IsCachedReadOnly);
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static void NativeRecoveryRestoresLiveCacheState()
{
    var root = Path.Combine(Path.GetTempPath(), "rv-native-recovery-" + Guid.NewGuid().ToString("N"));
    var preferences = new NativeServerConnectionPreferences
    {
        ServerInstanceId = Guid.NewGuid().ToString("D"),
        ServerDisplayName = "Recovery test server",
        ServerAddress = "127.0.0.1",
        SecurePort = 65529,
        CertificateThumbprint = new string('A', 64),
        AccessToken = new string('B', 64),
        UseRemoteOnStartup = true
    };
    const string path = "/api/v1/client/recovery-state";
    try
    {
        var cache = new NativeServerResponseCache(preferences, root);
        cache.Store(path, Encoding.UTF8.GetBytes("{\"source\":\"cached-outage\"}"));
        using var client = new LoopbackServerClient(
            remotePreferences: preferences,
            useRemoteServer: true,
            responseCacheRoot: root);
        using (client.UsePersistentCacheFirst())
            _ = client.SendJsonAsync<System.Text.Json.JsonElement>(HttpMethod.Get, path).GetAwaiter().GetResult();
        True(client.IsCachedReadOnly);
        client.MarkServerLive(invalidateMemoryCache: true);
        True(!client.IsCachedReadOnly);

        var connection = File.ReadAllText(Path.Combine(
            SourceRoot(), "TheRadioVault.Infrastructure", "Services", "NativeConnectedAccessService.cs"));
        True(connection.Contains("var recovered = !_current.IsLive;", StringComparison.Ordinal));
        True(connection.Contains("MarkServerLive(invalidateMemoryCache: recovered)", StringComparison.Ordinal));
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static void CatalogueDatesPreservePartialCluesWithoutInvention()
{
    var exact = CatalogueDateService.Resolve("Ron & Ron Show - Gary Spivey (November 4, 1996).mp3");
    Equal(new DateOnly(1996, 11, 4), exact.ExactDate!.Value);
    Equal(CatalogueDatePrecision.Day, exact.Precision);

    var month = CatalogueDateService.Resolve("Ron & Ron Show - St. Pete Riots (October 1996).mp3");
    True(!month.ExactDate.HasValue);
    Equal("October 1996", month.DisplayText);
    Equal(CatalogueDatePrecision.Month, month.Precision);

    var year = CatalogueDateService.Resolve("rbi_adam_resnick_2015.mp3");
    True(!year.ExactDate.HasValue);
    Equal("2015", year.DisplayText);
    Equal(CatalogueDatePrecision.Year, year.Precision);
}

static void CatalogueDatesRequireVisibleResearchDecisions()
{
    var models = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Services", "Models", "ResearchWorkspaceModels.cs"));
    True(models.Contains("CatalogueDateReviewItem", StringComparison.Ordinal));
    True(models.Contains("ApproveLibraryDate", StringComparison.Ordinal));
    True(models.Contains("KeepExisting", StringComparison.Ordinal));
    True(models.Contains("Ignore", StringComparison.Ordinal));
    True(models.Contains("KeepAsRecordingDate", StringComparison.Ordinal));
    True(models.Contains("KeepAsReleaseDate", StringComparison.Ordinal));
    True(models.Contains("LeaveUndated", StringComparison.Ordinal));

    var workspace = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Services", "Services", "ResearchWorkspaceService.cs"));
    True(workspace.Contains("GetCatalogueDateReviewsAsync", StringComparison.Ordinal));
    True(workspace.Contains("ResolveCatalogueDateReviewAsync", StringComparison.Ordinal));
    True(workspace.Contains("MarkPendingCatalogueDateReviewsAsync", StringComparison.Ordinal));
    True(workspace.Contains("Filename / title clue", StringComparison.Ordinal));
    True(workspace.Contains("date_review_previous_air_date", StringComparison.Ordinal));

    var view = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "ResearchWorkspaceView.axaml"));
    True(view.Contains("Broadcast date decisions", StringComparison.Ordinal));
    True(view.Contains("Approve  (A)", StringComparison.Ordinal));
    True(view.Contains("Keep existing  (K)", StringComparison.Ordinal));
    True(view.Contains("Ignore  (I)", StringComparison.Ordinal));
    True(view.Contains("ActiveDateReviewLabel", StringComparison.Ordinal));
    True(view.Contains("IgnoredDateReviewLabel", StringComparison.Ordinal));
    True(view.Contains("CompletedDateReviewLabel", StringComparison.Ordinal));
    True(view.Contains("More date choices", StringComparison.Ordinal));
    True(view.Contains("Keep as recording date", StringComparison.Ordinal));
    True(view.Contains("Leave Library item undated", StringComparison.Ordinal));
    True(view.Contains("CURRENT LIBRARY DATE", StringComparison.Ordinal));

    var packModels = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Infrastructure", "Models", "Models.cs"));
    True(packModels.Contains("DateReviewStatus", StringComparison.Ordinal));
    True(packModels.Contains("SchemaVersion { get; set; } = 1", StringComparison.Ordinal));

    var transfer = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Research", "AvaloniaResearchPackTransferServices.cs"));
    True(transfer.Contains("isKnownResolvedDecision", StringComparison.Ordinal));
    True(transfer.Contains("isKeepExisting", StringComparison.Ordinal));
    True(transfer.Contains("isIgnored", StringComparison.Ordinal));
    True(transfer.Contains("previousWasResearchAdopted", StringComparison.Ordinal));
}

static void DateReviewAppliesToEveryFirstClassShow()
{
    True(KnownShowCatalog.SupportsDateReview(KnownShowCatalog.RonFez));
    True(KnownShowCatalog.SupportsDateReview(KnownShowCatalog.Bennington));
    True(KnownShowCatalog.SupportsDateReview(KnownShowCatalog.OpieAnthony));
    True(KnownShowCatalog.SupportsDateReview(KnownShowCatalog.RonRon));
    True(KnownShowCatalog.SupportsDateReview(KnownShowCatalog.Unmasked));
    True(KnownShowCatalog.SupportsDateReview(KnownShowCatalog.RonBenningtonInterviews));
    True(!KnownShowCatalog.SupportsDateReview(KnownShowCatalog.Unsorted));

    var workspace = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Services", "Services", "ResearchWorkspaceService.cs"));
    True(workspace.Contains("SupportsDateReview(show)", StringComparison.Ordinal));
    True(workspace.Contains("SupportsDateReview(row.ShowName)", StringComparison.Ordinal));
    True(workspace.Contains("Missing or uncertain broadcast date", StringComparison.Ordinal));
    True(workspace.Contains("IsUncertainDateConfidence", StringComparison.Ordinal));

    var transfer = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Research", "AvaloniaResearchPackTransferServices.cs"));
    True(transfer.Contains("SupportsDateReview(item.Show)", StringComparison.Ordinal));
    True(transfer.Contains("catalogueDateNeedsConfirmation", StringComparison.Ordinal));
    True(transfer.Contains("autoResearchDate", StringComparison.Ordinal));

    var view = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "ResearchWorkspaceView.axaml"));
    True(view.Contains("Missing, uncertain or conflicting dates from every show", StringComparison.Ordinal));
}

static void LibraryMiniPanelHidesDeepCatalogueFields()
{
    var source = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "LibraryView.axaml"));
    True(!source.Contains("SelectedBroadcast.CatalogueFields", StringComparison.Ordinal));
    True(!source.Contains("Programme details", StringComparison.Ordinal));
    True(source.Contains("SelectedBroadcast.HasPeople", StringComparison.Ordinal));
    True(source.Contains("SelectedBroadcast.HasTopics", StringComparison.Ordinal));
}

static void SidebarActivityReplacesPageWideLoadingBars()
{
    var activity = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "ShellActivityViewModel.cs"));
    True(activity.Contains("Scanning Library", StringComparison.Ordinal));
    True(activity.Contains("Importing research", StringComparison.Ordinal));
    True(activity.Contains("Preparing playback", StringComparison.Ordinal));

    var shell = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "MainWindow.axaml"));
    True(shell.Contains("Activity.IsVisible", StringComparison.Ordinal));
    True(shell.Contains("Activity.Detail", StringComparison.Ordinal));

    foreach (var viewName in new[]
    {
        "DashboardView.axaml",
        "LibraryView.axaml",
        "SearchView.axaml",
        "QueueView.axaml",
        "MomentsView.axaml",
        "NowPlayingView.axaml"
    })
    {
        var view = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", viewName));
        True(!view.Contains("IsIndeterminate=\"True\" Height=\"3\"", StringComparison.Ordinal));
    }
}

static void ResearchAuditCatchesShowGuest()
{
    var record = new ResearchAuditRecord
    {
        ResearchBroadcastId = 1,
        Show = "Bennington",
        Headline = "A proper headline",
        Summary = "Ron and Gail discuss a specific news story and welcome a comedian.",
        People = new[] { new ResearchAuditPerson("Bennington", "guest") },
        Sources = new[] { new ResearchAuditSource("https://example.test", "Source", "community", 80) }
    };
    var result = new ResearchQualityEngine().Run(new[] { record });
    True(result.Findings.Any(x => x.RuleId == "show-as-guest"));
}

static void ResearchAuditMarksSafeRepairs()
{
    var record = new ResearchAuditRecord
    {
        ResearchBroadcastId = 2,
        Show = "Ron & Fez",
        Headline = "A proper headline",
        Summary = "A sufficiently detailed and specific summary for this broadcast.",
        Topics = new[] { "Baseball", " baseball " },
        Sources = new[] { new ResearchAuditSource("https://example.test", "Source", "community", 80) }
    };
    var result = new ResearchQualityEngine().Run(new[] { record });
    True(result.Findings.Any(x => x.RuleId == "duplicate-topic" && x.AutoFixKind == ResearchAutoFixKind.RemoveDuplicateTopic));
}


static void ResearchAuditMarksDuplicateSourceRepair()
{
    var record = new ResearchAuditRecord
    {
        ResearchBroadcastId = 3,
        Show = "Bennington",
        Headline = "A proper headline",
        Summary = "A sufficiently detailed and specific summary for this broadcast.",
        Sources = new[]
        {
            new ResearchAuditSource("https://example.test/thread", "Thread", "community", 80),
            new ResearchAuditSource("https://example.test/thread", "Thread copy", "community", 70)
        }
    };
    var result = new ResearchQualityEngine().Run(new[] { record });
    True(result.Findings.Any(x => x.RuleId == "duplicate-source" && x.AutoFixKind == ResearchAutoFixKind.RemoveDuplicateSource));
}

static void ResearchAuditMarksGenericHeadlineRepair()
{
    var record = new ResearchAuditRecord
    {
        ResearchBroadcastId = 4,
        Show = "Bennington",
        Headline = "Faction Talk archive broadcast",
        Summary = "A sufficiently detailed and specific summary for this broadcast.",
        Sources = new[] { new ResearchAuditSource("https://example.test", "Source", "community", 80) }
    };
    var result = new ResearchQualityEngine().Run(new[] { record });
    True(result.Findings.Any(x => x.RuleId == "weak-headline" && x.AutoFixKind == ResearchAutoFixKind.ClearGenericHeadline));
}

static void ResearchAuditAggregatesRepeatedSummaries()
{
    const string shared = "This episode features archive broadcast material but does not provide a complete rundown of the programme.";
    var records = Enumerable.Range(1, 25).Select(index => new ResearchAuditRecord
    {
        ResearchBroadcastId = 100 + index,
        Show = "Ron & Fez",
        BroadcastDate = new DateTime(2004, 1, Math.Min(index, 28)),
        Headline = $"Broadcast {index}",
        Summary = shared,
        Sources = new[] { new ResearchAuditSource("https://example.test", "Source", "community", 80) }
    }).ToArray();

    var result = new ResearchQualityEngine().Run(records);
    Equal(1, result.Findings.Count(x => x.RuleId == "duplicate-summary-pattern"));
    Equal(0, result.Findings.Count(x => x.RuleId == "generic-summary"));
    True(result.Findings.Single(x => x.RuleId == "duplicate-summary-pattern").Severity == ResearchAuditSeverity.Warning);
}

static void ResearchAuditOffersDirectDecisionCards()
{
    var record = new ResearchAuditRecord
    {
        ResearchBroadcastId = 500,
        EpisodeId = 900,
        Show = "Bennington",
        BroadcastDate = new DateTime(2026, 7, 21),
        Headline = "A proper headline",
        Summary = "This episode features a specific guest and a detailed discussion of the day’s events.",
        People = new[]
        {
            new ResearchAuditPerson("Example Person", "host"),
            new ResearchAuditPerson("Example Person", "guest")
        },
        Topics = new[] { "discussion" },
        Sources = new[] { new ResearchAuditSource("https://example.test", "Source", "community", 80) }
    };

    var result = new ResearchQualityEngine().Run(new[] { record });
    var role = result.Findings.Single(x => x.RuleId == "contradictory-role");
    Equal("person-role", role.DirectDecisionKind);
    Equal("Example Person", role.DirectDecisionSubject);
    True(role.DirectDecisionOptions.Contains("host"));
    True(role.DirectDecisionOptions.Contains("guest"));
    True(!string.IsNullOrWhiteSpace(role.DirectDecisionFingerprint));

    var topic = result.Findings.Single(x => x.RuleId == "weak-topic");
    Equal("weak-topic", topic.DirectDecisionKind);
    True(topic.DirectDecisionOptions.SequenceEqual(new[] { "keep", "remove" }));
}

static void WebDetailContractPreservesResearch()
{
    var detail = new WebBroadcastDetails
    {
        Episode = Episode(9, "Ron & Fez", false),
        People = new[] { new WebPerson("Ron Bennington", "host") },
        Topics = new[] { "Comedy" },
        Research = new WebResearchDetails
        {
            ResearchBroadcastId = 99,
            Confidence = 90,
            Sources = new[] { new WebResearchSource("Listening thread", "https://example.test", "Community", "community", 90) }
        }
    };
    Equal(99L, detail.Research!.ResearchBroadcastId);
    Equal("Ron Bennington", detail.People[0].Name);
}

static WebEpisode Episode(long id, string show, bool favourite, string people = "", long positionMs = 0, long durationMs = 3_600_000)
    => new(id, show, $"Episode {id}", new DateTime(2020, 1, (int)id), "Specific summary", people, "Comedy", durationMs, positionMs,
        positionMs > 0 ? "In Progress" : "Unplayed", favourite, null, new DateTime(2026, 7, 16).AddMinutes(-id), $"C:\\Audio\\{id}.mp3", "");



static void ResearchRepairGuardAllowsUnchangedState()
    => True(ResearchRepairGuard.CanUndo("{\"headline\":\"Specific\",\"topics\":[\"Comedy\"]}", "{\"headline\":\"Specific\",\"topics\":[\"Comedy\"]}"));

static void ResearchRepairGuardBlocksLaterChanges()
    => True(!ResearchRepairGuard.CanUndo("{\"headline\":\"Edited later\"}", "{\"headline\":\"Specific\"}"));

static void CoreHardeningRemoteLibrarySessionOwnsLifecycle()
{
    using var coordinator = new RemoteLibrarySessionCoordinator();
    var cachedAt = new DateTimeOffset(2026, 7, 25, 20, 0, 0, TimeSpan.Zero);
    coordinator.AdoptCachedSnapshot(new RemoteLibrarySyncCursor("cached-session", 18, "revision-a"), cachedAt);
    True(coordinator.Current.IsCachedReadOnly);
    Equal("cached-session", coordinator.Current.Cursor.SessionId);
    Equal(18L, coordinator.Current.Cursor.Sequence);

    var first = coordinator.BeginSyncAsync(
        RemoteLibrarySyncRequest.Create(initialLoad: false, forceReset: false, silent: true),
        CancellationToken.None).GetAwaiter().GetResult();
    True(first is not null);
    True(coordinator.Current.IsCachedReadOnly);
    var duplicate = coordinator.BeginSyncAsync(
        RemoteLibrarySyncRequest.Create(initialLoad: false, forceReset: false, silent: true),
        CancellationToken.None).GetAwaiter().GetResult();
    True(duplicate is null);

    var live = first!.CompleteSuccess(
        new RemoteLibrarySyncCursor("live-session", 19, "revision-b"),
        new RemoteLibrarySyncMetrics("incremental delta", 1, 1, 0, false, false, 0));
    True(live.IsLive);
    True(!live.IsCachedReadOnly);
    Equal("live-session", live.Cursor.SessionId);
    Equal(19L, live.Cursor.Sequence);
    first.Dispose();

    var failedLease = coordinator.BeginSyncAsync(
        RemoteLibrarySyncRequest.Create(initialLoad: false, forceReset: false, silent: true),
        CancellationToken.None).GetAwaiter().GetResult();
    True(failedLease is not null);
    True(!coordinator.Current.IsCachedReadOnly);
    var failure = failedLease!.CompleteFailure("server unavailable", hasUsableSnapshot: true);
    True(failure.Snapshot.IsCachedReadOnly);
    Equal(1, failure.Snapshot.ConsecutiveFailures);
    Equal(TimeSpan.FromSeconds(3), failure.RetryDelay);
    True(!coordinator.CanSynchronize(DateTimeOffset.UtcNow));
    failedLease.Dispose();

    coordinator.RequestReconnectNow();
    True(coordinator.CanSynchronize(DateTimeOffset.UtcNow));
}

static void ApplicationEventBusIsTypedAndDisposable()
{
    var bus = new ApplicationEventBus();
    var received = 0;
    var subscription = bus.Subscribe<MetadataChangedEvent>(_ => received++);
    bus.Publish(new MetadataChangedEvent(9, "test", DateTimeOffset.UtcNow));
    Equal(1, received);
    subscription.Dispose();
    bus.Publish(new MetadataChangedEvent(9, "test", DateTimeOffset.UtcNow));
    Equal(1, received);
}

static void BackgroundJobsPublishCompletion()
{
    var bus = new ApplicationEventBus();
    var jobEvents = new List<BackgroundJobChangedEvent>();
    using var subscription = bus.Subscribe<BackgroundJobChangedEvent>(jobEvents.Add);
    using var queue = new BackgroundJobQueue(1, bus);
    queue.RunAsync(new BackgroundJobRequest("Test job", BackgroundJobCategory.ResearchAudit, (context, _) =>
    {
        context.Report(50, "Half way");
        return Task.CompletedTask;
    })).GetAwaiter().GetResult();
    var job = queue.GetJobs().Single();
    Equal(BackgroundJobState.Completed, job.State);
    Equal(100d, job.Percent ?? -1);
    True(jobEvents.Any(x => x.Job.State == BackgroundJobState.Running));
    True(jobEvents.Any(x => x.Job.State == BackgroundJobState.Completed));
}

static void BackgroundJobsCancelSafely()
{
    using var queue = new BackgroundJobQueue(1);
    var jobId = queue.Enqueue(new BackgroundJobRequest("Cancelable job", BackgroundJobCategory.LibraryScan, async (context, token) =>
    {
        context.ReportIndeterminate("Waiting");
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }));
    True(SpinWait.SpinUntil(() => queue.GetJob(jobId)?.State == BackgroundJobState.Running, TimeSpan.FromSeconds(2)));
    True(queue.Cancel(jobId));
    True(SpinWait.SpinUntil(() => queue.GetJob(jobId)?.State == BackgroundJobState.Cancelled, TimeSpan.FromSeconds(2)));
}

static void BackgroundJobsDisposeSafelyWhileRunning()
{
    var queue = new BackgroundJobQueue(1);
    var jobId = queue.Enqueue(new BackgroundJobRequest("Shutdown job", BackgroundJobCategory.ArchiveSync, async (context, token) =>
    {
        context.ReportIndeterminate("Running");
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }));
    True(SpinWait.SpinUntil(() => queue.GetJob(jobId)?.State == BackgroundJobState.Running, TimeSpan.FromSeconds(2)));
    queue.Dispose();
    True(SpinWait.SpinUntil(() => queue.GetJob(jobId)?.State == BackgroundJobState.Cancelled, TimeSpan.FromSeconds(2)));
}

static void LivePlaybackStateIsAtomic()
{
    var store = new LivePlaybackStateStore();
    store.Update(new LivePlaybackSnapshot(9, "Ron & Fez", "Test", 1200, 5000, "In Progress", true, DateTimeOffset.UtcNow));
    Equal(9L, store.Current.EpisodeId ?? -1);
    True(store.Current.IsPlaying);
}

static void LanDiscoveryAnnouncementExcludesCredentials()
{
    var announcement = new WebLanDiscoveryAnnouncement(
        "radiovault-lan-v1",
        "11111111-2222-3333-4444-555555555555",
        "Test Radio Vault",
        "0.31.0-alpha5-lan-shared-session-consolidation",
        "v1",
        45,
        8,
        8768,
        "ABCDEF0123456789",
        true,
        1,
        DateTimeOffset.UtcNow);
    var json = System.Text.Json.JsonSerializer.Serialize(announcement);
    True(json.Contains("radiovault-lan-v1", StringComparison.Ordinal));
    True(!json.Contains("token", StringComparison.OrdinalIgnoreCase));
    True(!json.Contains("path", StringComparison.OrdinalIgnoreCase));
    True(!json.Contains("broadcast", StringComparison.OrdinalIgnoreCase));
}

static void LanDiscoveryCalculatesDirectedBroadcast()
{
    var broadcast = LanDiscoveryNetwork.CalculateBroadcastAddress(
        System.Net.IPAddress.Parse("192.168.42.17"),
        System.Net.IPAddress.Parse("255.255.255.0"));
    Equal("192.168.42.255", broadcast.ToString());

    var widerBroadcast = LanDiscoveryNetwork.CalculateBroadcastAddress(
        System.Net.IPAddress.Parse("172.16.18.44"),
        System.Net.IPAddress.Parse("255.255.252.0"));
    Equal("172.16.19.255", widerBroadcast.ToString());
}

static void OfflineProgressOrderingPreservesNewerManualChanges()
{
    var receivedAt = new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.Zero);
    var offlineListening = new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero);
    var olderCanonical = new DateTime(2026, 8, 10, 7, 0, 0, DateTimeKind.Utc);
    var laterManualChange = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc);

    True(!OfflineProgressOrderingPolicy.IsStale(offlineListening, olderCanonical, receivedAt));
    True(OfflineProgressOrderingPolicy.IsStale(offlineListening, laterManualChange, receivedAt));
    True(OfflineProgressOrderingPolicy.IsStale(receivedAt.AddMinutes(6), null, receivedAt));
    Equal(offlineListening, OfflineProgressOrderingPolicy.EffectivePlayedAt(offlineListening, receivedAt));

    var mobileSession = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.Mobile", "MobileClientSession.cs"));
    var mobileTimeline = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.Mobile", "Playback", "MobilePlaybackTimeline.cs"));
    True(mobileSession.Contains("DownloadedProgressSnapshot", StringComparison.Ordinal));
    True(mobileSession.Contains("CaptureDownloadedProgress", StringComparison.Ordinal));
    True(mobileTimeline.Contains("public bool Completed", StringComparison.Ordinal));
    True(mobileSession.Contains("_playbackTimeline.IsCompleted()", StringComparison.Ordinal));
    True(mobileSession.Contains("changed || snapshot.IncrementPlayCount", StringComparison.Ordinal));
    var playAsync = mobileSession.IndexOf("private async Task PlayCoreAsync", StringComparison.Ordinal);
    var flushPrevious = mobileSession.IndexOf(
        "await FlushPlaybackAsync().WaitAsync(cancellationToken).ConfigureAwait(false);",
        playAsync,
        StringComparison.Ordinal);
    var selectNext = mobileSession.IndexOf("SelectedBroadcast = broadcast;", flushPrevious, StringComparison.Ordinal);
    True(flushPrevious >= 0 && selectNext > flushPrevious);

    var downloads = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.Mobile", "Services", "MobileDownloadService.cs"));
    True(downloads.Contains("public async Task<bool> UpdateProgressAsync", StringComparison.Ordinal));
    True(downloads.Contains("record.Summary.PositionMs == normalizedPosition", StringComparison.Ordinal));

    var iosEngine = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "IosAvPlayerEngine.cs"));
    True(iosEngine.Contains("ReferenceEquals(_player?.CurrentItem, endedItem)", StringComparison.Ordinal));

    var archiveProvider = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Infrastructure", "Services", "WebArchiveProvider.cs"));
    True(archiveProvider.Contains("OfflineProgressOrderingPolicy.IsStale", StringComparison.Ordinal));
    True(archiveProvider.Contains("playedAt: OfflineProgressOrderingPolicy.EffectivePlayedAt", StringComparison.Ordinal));

}


static void WikiPagesProtectNewerHumanRevisions()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var database = new SqliteDatabase(Path.Combine(directory, "wiki-edit.sqlite"));
        database.Initialize();
        var service = new WikiService(database);
        var created = service.SavePageAsync(new WikiPageDraft(
            null, "ron-and-fez", "Ron & Fez", "Show", "A radio show.", "# Ron & Fez", "Draft", 0,
            "Created baseline", "Human editor", new[] { "Ron and Fez" })).GetAwaiter().GetResult();
        _ = service.SavePageAsync(new WikiPageDraft(
            null, "ron-bennington", "Ron Bennington", "Person", "A radio host.", "# Ron Bennington", "Published", 0,
            "Created person", "Human editor")).GetAwaiter().GetResult();
        Equal(1, created.Revision);
        var updated = service.SavePageAsync(new WikiPageDraft(
            created.PageId, "ron-and-fez", "Ron & Fez", "Show", "A long-running radio show.", "# Ron & Fez\n\nHistory with [[Ron Bennington]].",
            "Published", 1, "Expanded history", "Human editor", new[] { "Ron and Fez", "R&F" })).GetAwaiter().GetResult();
        Equal(2, updated.Revision);
        Throws<WikiConcurrencyException>(() => service.SavePageAsync(new WikiPageDraft(
            created.PageId, "ron-and-fez", "Ron & Fez", "Show", "Stale edit", "# Stale", "Draft", 1,
            "Stale overwrite", "Old client")).GetAwaiter().GetResult());
        var page = service.GetPageAsync(created.PageId).GetAwaiter().GetResult()!;
        Equal(2, page.Revision);
        Equal("A long-running radio show.", page.Summary);
        Equal(2, page.Aliases.Count);
        Equal(ArchiveEntityKind.Show, page.EntityLink!.Kind);
        True(page.EntityLinks!.Any(link => link.Kind == ArchiveEntityKind.Person && link.Relationship == "inline"));
    }
    finally { try { Directory.Delete(directory, true); } catch { } }
}

static void WikiAuthoringPacksRoundTripEvidence()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var sourceDatabase = new SqliteDatabase(Path.Combine(directory, "wiki-source.sqlite"));
        sourceDatabase.Initialize();
        var sourceService = new WikiService(sourceDatabase);
        var pageId = sourceService.SavePageAsync(new WikiPageDraft(
            null, "bennington", "Bennington", "Show", "A radio programme.", "# Bennington\n\nThe programme launched in 2015.[1]",
            "Published", 0, "Baseline", "Radio Vault user")).GetAwaiter().GetResult().PageId;
        var baseline = sourceService.GetAuthoringSnapshotAsync("0.35.0-alpha1", "test-source").GetAwaiter().GetResult();

        var sourceId = Guid.NewGuid();
        var citationId = Guid.NewGuid();
        var imageId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var imageBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4 };
        var source = new WikiSourceRecord(
            sourceId, "Broadcast", "Launch broadcast", "", "Radio Vault", "", "", new DateOnly(2015, 10, 19),
            "Day", DateTimeOffset.UtcNow, 42, "BENNINGTON-2015-10-19", 1_000, 8_000, null, null, "00:01-00:08", "Primary archive source");
        var image = new WikiImageRecord(
            imageId, "studio-2015.png", "image/png", WikiAuthoringPackService.Sha256(imageBytes), imageBytes.Length,
            "The studio at launch", "Radio studio", "Archive contributor", "Archive contributor", "CC BY 4.0",
            sourceId, new DateOnly(2015, 10, 19), new DateOnly(2015, 10, 19), new DateOnly(2015, 10, 19), "Day", "Taken on launch day");
        var eventRecord = new WikiTimelineEventRecord(
            eventId, pageId, "Bennington launches", "The first programme aired.", "Launch",
            new DateOnly(2015, 10, 19), null, "Day", "19 October 2015", 100, 0,
            new[] { sourceId }, new[] { imageId },
            new[] { new WikiTimelineBroadcastLink(eventId, 42, null, 1_000, 8_000, "Hear the launch", 0) });
        var snapshot = baseline with
        {
            Sources = new[] { source },
            Citations = new[] { new WikiCitationRecord(citationId, pageId, sourceId, 1, "launch", "", "Launch date", null) },
            Images = new[] { new WikiAuthoringImageRecord(image, WikiAuthoringPackService.ImageArchivePath(image)) },
            ImageBytes = new Dictionary<Guid, byte[]> { [imageId] = imageBytes },
            PageImages = new[] { new WikiPageImageLink(pageId, imageId, "Lead", 0) },
            TimelineEvents = new[] { eventRecord }
        };

        var packService = new WikiAuthoringPackService();
        var bytes = packService.Export(snapshot);
        using (var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
        {
            True(archive.GetEntry("AUTHORING.md") is not null);
            True(archive.GetEntry($"pages/{pageId:D}.md") is not null);
            True(archive.GetEntry(WikiAuthoringPackService.ImageArchivePath(image)) is not null);
        }
        using var packageStream = new MemoryStream(bytes, writable: false);
        var imported = packService.Import(packageStream);
        Equal(1, imported.Sources.Count);
        Equal(1, imported.Images.Count);
        Equal(1, imported.TimelineEvents.Count);

        var targetDatabase = new SqliteDatabase(Path.Combine(directory, "wiki-target.sqlite"));
        targetDatabase.Initialize();
        using (var connection = targetDatabase.OpenConnection())
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Bennington','Bennington');
                INSERT INTO episodes(id,collection_id,title,status,date_added,updated_at,broadcast_uid)
                VALUES(42,(SELECT id FROM collections WHERE name='Bennington'),'Launch broadcast','Unplayed',$now,$now,'BENNINGTON-2015-10-19');
                """;
            setup.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            setup.ExecuteNonQuery();
        }
        var targetService = new WikiService(targetDatabase);
        var hash = WikiAuthoringPackService.Sha256(bytes);
        var preview = targetService.PreviewImportAsync(imported, "baseline.rvwiki", hash).GetAwaiter().GetResult();
        Equal(1, preview.NewPages);
        var result = targetService.ApplyImportAsync(imported, "baseline.rvwiki", hash).GetAwaiter().GetResult();
        Equal(1, result.CreatedPages);
        Equal(1, result.CitationsStored);
        Equal(1, result.ImagesStored);
        Equal(1, result.TimelineEventsStored);
        var page = targetService.GetPageAsync(pageId).GetAwaiter().GetResult()!;
        Equal("Launch broadcast", page.Citations.Single().Source!.Title);
        Equal("The studio at launch", page.Images.Single().Image!.Caption);
        Equal(42L, page.Timeline.Single().Broadcasts.Single().EpisodeId);
    }
    finally { try { Directory.Delete(directory, true); } catch { } }
}

static void KnowledgeImportsRecoverUntitledAiCitationSources()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var database = new SqliteDatabase(Path.Combine(directory, "untitled-source.sqlite"));
        database.Initialize();
        var service = new WikiService(database);
        var pageId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var snapshot = new WikiAuthoringSnapshot(
            new WikiAuthoringPackManifest(1, "0.35.0-alpha8", Guid.NewGuid(), DateTimeOffset.UtcNow,
                "ai-agent", 1, 1, 1, 0, 0, new Dictionary<string, string>()),
            new[] { new WikiAuthoringPageRecord(pageId, 0, "bennington", "Bennington", "Show", "Programme history.", "Draft", "AI agent", "AI agent", Array.Empty<string>()) },
            new Dictionary<Guid, string> { [pageId] = "# Bennington\n\nProgramme history.[1]" },
            Array.Empty<WikiRelationshipRecord>(),
            new[] { new WikiSourceRecord(sourceId, "Web", "", "", "Example Archive", "https://example.org/history", "", null, "Unknown", null, null, "", null, null, null, null, "", "") },
            new[] { new WikiCitationRecord(Guid.NewGuid(), pageId, sourceId, 1, "history", "", "Supporting source") },
            Array.Empty<WikiAuthoringImageRecord>(),
            new Dictionary<Guid, byte[]>(),
            Array.Empty<WikiPageImageLink>(),
            Array.Empty<WikiTimelineEventRecord>());

        var preview = service.PreviewImportAsync(snapshot, "ai-baseline.trvknowledge", "test-hash").GetAwaiter().GetResult();
        True(preview.Summary.Contains("recover", StringComparison.OrdinalIgnoreCase));
        service.ApplyImportAsync(snapshot, "ai-baseline.trvknowledge", "test-hash").GetAwaiter().GetResult();
        var page = service.GetPageAsync(pageId).GetAwaiter().GetResult()!;
        Equal("Source from Example Archive", page.Citations.Single().Source!.Title);
    }
    finally { try { Directory.Delete(directory, true); } catch { } }
}

static void KnowledgeImportsReconcileExistingExploreSlugs()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var database = new SqliteDatabase(Path.Combine(directory, "slug-reconciliation.sqlite"));
        database.Initialize();
        var service = new WikiService(database);
        var existing = service.SavePageAsync(new WikiPageDraft(
            null, "bennington", "Bennington", "Show", "Starter summary", "# Bennington\n\nStarter article.",
            "Draft", 0, "Radio Vault", "Radio Vault")).GetAwaiter().GetResult();
        var importedId = Guid.NewGuid();
        var snapshot = new WikiAuthoringSnapshot(
            new WikiAuthoringPackManifest(1, "0.35.0-alpha9", Guid.NewGuid(), DateTimeOffset.UtcNow,
                "ai-agent", 1, 0, 0, 0, 0, new Dictionary<string, string>()),
            new[] { new WikiAuthoringPageRecord(importedId, existing.Revision, "bennington", "Bennington", "Show", "Enriched summary", "Published", "AI agent", "AI agent", Array.Empty<string>()) },
            new Dictionary<Guid, string> { [importedId] = "# Bennington\n\nEnriched and cited history." },
            Array.Empty<WikiRelationshipRecord>(), Array.Empty<WikiSourceRecord>(), Array.Empty<WikiCitationRecord>(),
            Array.Empty<WikiAuthoringImageRecord>(), new Dictionary<Guid, byte[]>(), Array.Empty<WikiPageImageLink>(),
            Array.Empty<WikiTimelineEventRecord>());

        var preview = service.PreviewImportAsync(snapshot, "enriched.trvknowledge", "slug-hash").GetAwaiter().GetResult();
        Equal(0, preview.NewPages);
        Equal(1, preview.ChangedPages);
        var result = service.ApplyImportAsync(snapshot, "enriched.trvknowledge", "slug-hash").GetAwaiter().GetResult();
        Equal(1, result.UpdatedPages);
        var updated = service.GetPageAsync(existing.PageId).GetAwaiter().GetResult()!;
        Equal("Enriched summary", updated.Summary);
        True(updated.BodyMarkdown.Contains("Enriched and cited history", StringComparison.Ordinal));
        True(service.GetPageAsync(importedId).GetAwaiter().GetResult() is null);
    }
    finally { try { Directory.Delete(directory, true); } catch { } }
}

static void AmbiguousResearchReviewUsesSchemaValidState()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var database = new SqliteDatabase(Path.Combine(directory, "ambiguous-review.sqlite"));
        database.Initialize();
        using (var connection = database.OpenConnection())
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                INSERT INTO collections(id,name,sort_name) VALUES(8701,'Review State Test','Review State Test');
                INSERT INTO episodes(id,collection_id,title,status,date_added,updated_at,broadcast_uid)
                VALUES(8702,8701,'Review candidate','Unplayed',$now,$now,'REVIEW-STATE-TEST');
                INSERT INTO research_broadcasts(
                    id,identity_key,collection_id,episode_id,source_broadcast_id,headline,research_json,
                    research_state,existence_status,confidence,needs_review,created_at,updated_at)
                VALUES(8703,'REVIEW-STATE-TEST',8701,8702,'REVIEW-STATE-TEST','Review candidate','{}',
                       'in_library','in_library',90,1,$now,$now);
                INSERT INTO research_reconciliation_candidates(
                    research_broadcast_id,episode_id,score,reason,status,requires_review,created_at,updated_at)
                VALUES(8703,8702,75,'Multiple archive broadcasts matched.','pending',1,$now,$now);
                """;
            setup.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            setup.ExecuteNonQuery();
        }

        new DatabaseService(database).MarkResearchLibraryRecordReviewed(8703);

        using var verification = database.OpenConnection();
        using var verify = verification.CreateCommand();
        verify.CommandText = "SELECT research_state,needs_review FROM research_broadcasts WHERE id=8703";
        using var reader = verify.ExecuteReader();
        True(reader.Read());
        Equal("conflicting_information", reader.GetString(0));
        Equal(1L, reader.GetInt64(1));
    }
    finally { try { Directory.Delete(directory, true); } catch { } }
}

static void KnowledgeExportsTeachAiAgentsThePortableDatabase()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "documented.trvknowledge");
    try
    {
        var pack = new TrvKnowledgePack
        {
            Manifest = new TrvPackManifest { AppVersion = "0.35.0-alpha9", Show = "Whole archive" }
        };
        new KnowledgePackService().Export(path, pack);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM pack_documentation),
              (SELECT COUNT(*) FROM pack_schema),
              (SELECT content FROM agent_instructions LIMIT 1),
              (SELECT content_markdown FROM pack_documentation WHERE document_key='30_safe_return');
            """;
        using var reader = command.ExecuteReader();
        True(reader.Read());
        True(reader.GetInt32(0) >= 4);
        True(reader.GetInt32(1) >= 10);
        True(reader.GetString(2).Contains("Read every row in pack_documentation", StringComparison.Ordinal));
        True(reader.GetString(3).Contains("PRAGMA quick_check", StringComparison.Ordinal));
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA quick_check";
        Equal("ok", Convert.ToString(check.ExecuteScalar()));
    }
    finally { try { Directory.Delete(directory, true); } catch { } }
}

static void CompleteKnowledgeExportsIncludeEveryShowAndTranscript()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var database = new SqliteDatabase(Path.Combine(directory, "complete-knowledge.sqlite"));
        database.Initialize();
        using (var connection = database.OpenConnection())
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                INSERT INTO collections(name,sort_name) VALUES('Complete Export A','Complete Export A');
                INSERT INTO collections(name,sort_name) VALUES('Complete Export B','Complete Export B');
                INSERT INTO episodes(id,collection_id,air_date,title,status,date_added,updated_at,broadcast_uid)
                VALUES(91001,(SELECT id FROM collections WHERE name='Complete Export A'),'2001-01-01','First','Unplayed',$now,$now,'COMPLETE-A');
                INSERT INTO episodes(id,collection_id,air_date,title,status,date_added,updated_at,broadcast_uid)
                VALUES(91002,(SELECT id FROM collections WHERE name='Complete Export B'),'2002-02-02','Second','Unplayed',$now,$now,'COMPLETE-B');
                INSERT INTO transcripts(id,episode_id,status,source,full_text,word_count,duration_ms,created_at,updated_at)
                VALUES(91001,91001,'Complete','local','first transcript',2,1000,$now,$now);
                INSERT INTO transcripts(id,episode_id,status,source,full_text,word_count,duration_ms,created_at,updated_at)
                VALUES(91002,91002,'Complete','local','second transcript',2,1000,$now,$now);
                INSERT INTO transcript_segments(transcript_id,segment_index,start_ms,end_ms,text) VALUES(91001,0,0,1000,'first transcript');
                INSERT INTO transcript_segments(transcript_id,segment_index,start_ms,end_ms,text) VALUES(91002,0,0,1000,'second transcript');
                """;
            setup.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            setup.ExecuteNonQuery();
        }

        var pack = new DatabaseService(database).BuildCompleteKnowledgePack("0.35.0-alpha8");
        Equal("Whole archive", pack.Manifest.Show);
        True(pack.Broadcasts.Any(item => item.Show == "Complete Export A" && item.BroadcastId == "COMPLETE-A"));
        True(pack.Broadcasts.Any(item => item.Show == "Complete Export B" && item.BroadcastId == "COMPLETE-B"));
        True(pack.Transcripts.Any(item => item.Show == "Complete Export A" && item.BroadcastId == "COMPLETE-A"));
        True(pack.Transcripts.Any(item => item.Show == "Complete Export B" && item.BroadcastId == "COMPLETE-B"));
        Equal(pack.Transcripts.Count, pack.Manifest.TranscriptCount);
    }
    finally { try { Directory.Delete(directory, true); } catch { } }
}

static void KnowledgeExportUiIsAlwaysArchiveWide()
{
    var root = SourceRoot();
    var desktopView = File.ReadAllText(Path.Combine(root, "TheRadioVault.Desktop.Avalonia", "Views", "ResearchWorkspaceView.axaml"));
    True(desktopView.Contains("Export full Knowledge Database", StringComparison.Ordinal));
    True(desktopView.Contains("Command=\"{Binding ExportCommand}\"", StringComparison.Ordinal));
    True(!desktopView.Contains("ExportPackButton_OnClick", StringComparison.Ordinal));
    True(!File.Exists(Path.Combine(root, "TheRadioVault.Desktop.Avalonia", "Views", "ResearchPackExportDialog.axaml")));
    True(!File.Exists(Path.Combine(root, "TheRadioVault.Desktop.Avalonia", "Views", "ResearchPackExportDialog.axaml.cs")));

    var contract = File.ReadAllText(Path.Combine(root, "TheRadioVault.Services", "Contracts", "IResearchPackTransferService.cs"));
    True(contract.Contains("ExportAsync(CancellationToken cancellationToken = default)", StringComparison.Ordinal));
    True(!contract.Contains("collectionName", StringComparison.Ordinal));
    True(!contract.Contains("int? year", StringComparison.Ordinal));

    var web = ReadWebServerSourceBundle();
    True(web.Contains("async function exportResearchPack()", StringComparison.Ordinal));
    True(web.Contains("await exportResearchPack();", StringComparison.Ordinal));
    True(!web.Contains("Choose a show to export.", StringComparison.Ordinal));
}

static void KnowledgeImportProgressBarsAreDeterminate()
{
    var root = SourceRoot();
    var research = File.ReadAllText(Path.Combine(root, "TheRadioVault.Desktop.Avalonia", "Views", "ResearchWorkspaceView.axaml"));
    True(research.Contains("Value=\"{Binding ImportProgressPercent}\"", StringComparison.Ordinal));
    True(!research.Contains("IsIndeterminate=\"True\"", StringComparison.Ordinal));

    var server = ReadServerAdministrationViews();
    True(server.Contains("Value=\"{Binding KnowledgeProgressPercent}\"", StringComparison.Ordinal));
    True(server.Contains("KnowledgeProgressCountText", StringComparison.Ordinal));

    var shell = File.ReadAllText(Path.Combine(root, "TheRadioVault.Desktop.Avalonia", "Views", "MainWindow.axaml"));
    True(shell.Contains("Activity.ProgressPercent", StringComparison.Ordinal));
    True(!shell.Contains("IsIndeterminate=\"True\"", StringComparison.Ordinal));

    var wiki = File.ReadAllText(Path.Combine(root, "TheRadioVault.Desktop.Avalonia", "Views", "WikiView.axaml"));
    True(!wiki.Contains("IsIndeterminate=\"True\"", StringComparison.Ordinal));
}

static void KnowledgeMatchingUsesOneArchiveIndex()
{
    var root = SourceRoot();
    var database = File.ReadAllText(Path.Combine(root, "TheRadioVault.Infrastructure", "Services", "DatabaseService.cs"));
    var import = File.ReadAllText(Path.Combine(root, "TheRadioVault.Infrastructure", "Services", "DatabaseService.ResearchLibrary.cs"));
    True(database.Contains("BuildKnowledgePackMatchIndex", StringComparison.Ordinal));
    True(database.Contains("FindKnowledgePackMatches(KnowledgePackMatchIndex", StringComparison.Ordinal));
    True(import.Split("BuildKnowledgePackMatchIndex", StringSplitOptions.None).Length >= 3);
    True(import.Contains("var collectionIds = new Dictionary<string, int>", StringComparison.Ordinal));

    var packs = File.ReadAllText(Path.Combine(root, "TheRadioVault.Infrastructure", "Services", "KnowledgePackService.cs"));
    True(packs.Contains("return ReadDatabase(fullPath);", StringComparison.Ordinal));
    True(packs.Contains("ValidatePackageSize(fullPath);", StringComparison.Ordinal));
}

static void InstallersPreventAccidentalDowngrades()
{
    var root = SourceRoot();
    var guardPath = Path.Combine(root, "installer", "PreventDowngrade.iss");
    True(File.Exists(guardPath));
    var guard = File.ReadAllText(guardPath);
    True(guard.Contains("CompareRadioVaultVersions", StringComparison.Ordinal));
    True(guard.Contains("A newer Radio Vault version", StringComparison.Ordinal));
    True(guard.Contains("RegQueryStringValue(HKCU", StringComparison.Ordinal));

    foreach (var installerName in new[] { "RadioVault.Client.iss", "RadioVault.Server.iss" })
    {
        var installer = File.ReadAllText(Path.Combine(root, "installer", installerName));
        True(installer.Contains("PreventDowngrade.iss", StringComparison.Ordinal));
        True(installer.Contains("PreventRadioVaultDowngrade", StringComparison.Ordinal));
    }
}

static void NativeWikiWorkspaceExposesEditingPacksAndTimelines()
{
    var viewModel = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "WikiViewModel.cs"));
    True(viewModel.Contains("WikiPageDraft", StringComparison.Ordinal));
    True(viewModel.Contains("PreviewImportAsync", StringComparison.Ordinal));
    True(viewModel.Contains("ExportAsync", StringComparison.Ordinal));
    var view = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "WikiView.axaml"));
    True(view.Contains("Scrollable show timeline", StringComparison.Ordinal));
    True(view.Contains("Sources &amp; images", StringComparison.Ordinal));
    True(view.Contains("EditorBodyMarkdown", StringComparison.Ordinal));
    var routes = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Web", "Contracts", "WebApiRoutes.cs"));
    True(routes.Contains("FederationWikiImportPreview", StringComparison.Ordinal));
    True(routes.Contains("ClientWikiOperation", StringComparison.Ordinal));
}

static void NativeWikiOpensOnExplorationDashboard()
{
    var viewModel = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "WikiViewModel.cs"));
    True(viewModel.Contains("private bool _isDashboardMode = true", StringComparison.Ordinal));
    True(viewModel.Contains("LoadDashboardCollectionsAsync", StringComparison.Ordinal));
    True(viewModel.Contains("ShowDashboardCommand", StringComparison.Ordinal));
    True(viewModel.Contains("ManageWikiCommand", StringComparison.Ordinal));
    True(viewModel.Contains("TimelinePages", StringComparison.Ordinal));

    var view = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "WikiView.axaml"));
    True(view.Contains("Explore the stories behind the archive", StringComparison.Ordinal));
    True(view.Contains("Featured starting points", StringComparison.Ordinal));
    True(view.Contains("Recently updated", StringComparison.Ordinal));
    True(view.Contains("Travel through the timelines", StringComparison.Ordinal));
    True(view.Contains("Header=\"Edit current page\"", StringComparison.Ordinal));
    True(view.Contains("IsVisible=\"{Binding IsDashboardMode}\"", StringComparison.Ordinal));
    True(view.Contains("IsVisible=\"{Binding IsExplorerMode}\"", StringComparison.Ordinal));
}

static void WikiEntityChipsOpenNativeAndWebReaders()
{
    var related = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "RelatedWikiPagesViewModel.cs"));
    True(related.Contains("OpenEntityCommand", StringComparison.Ordinal));
    True(related.Contains("SetOpenEntityHandler", StringComparison.Ordinal));
    var shell = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "MainWindowViewModel.cs"));
    True(shell.Contains("OpenWikiEntityAsync", StringComparison.Ordinal));
    True(shell.Contains("Wiki.OpenEntityAsync(entity)", StringComparison.Ordinal));

    foreach (var viewName in new[] { "DashboardView.axaml", "LibraryView.axaml", "FullBroadcastInfoView.axaml", "NowPlayingView.axaml" })
    {
        var view = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", viewName));
        True(view.Contains("Classes=\"entity-chip\"", StringComparison.Ordinal));
        True(view.Contains("RelatedWiki.OpenEntityCommand", StringComparison.Ordinal));
    }

    var web = ReadWebServerSourceBundle();
    True(web.Contains("data-section=\"wiki\"", StringComparison.Ordinal));
    True(web.Contains("async function loadWiki", StringComparison.Ordinal));
    True(web.Contains("async function openWikiEntity", StringComparison.Ordinal));
    True(web.Contains("[data-wiki-entity], .chip", StringComparison.Ordinal));
    True(web.Contains("function renderWikiMarkdown", StringComparison.Ordinal));
    True(web.Contains("data-info=\"${Number(link.episodeId)}\"", StringComparison.Ordinal));
}

static void KnowledgeSurfacesUseArticleFirstDashboardsAndSummaries()
{
    var wiki = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "WikiView.axaml"));
    True(wiki.Contains("Explore the stories behind the archive", StringComparison.Ordinal));
    True(wiki.Contains("History from across the years", StringComparison.Ordinal));
    True(wiki.Contains("ColumnDefinitions=\"*,290\"", StringComparison.Ordinal));
    True(wiki.Contains("Text=\"References\"", StringComparison.Ordinal));
    True(wiki.Contains("ReaderImages", StringComparison.Ordinal));

    var markdown = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Controls", "WikiMarkdownView.axaml.cs"));
    True(markdown.Contains("AddTextFragments", StringComparison.Ordinal));

    var research = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "ResearchWorkspaceView.axaml"));
    True(research.Contains("What Radio Vault knows", StringComparison.Ordinal));
    True(research.Contains("Knowledge coverage", StringComparison.Ordinal));
    True(research.Contains("IsDashboardMode", StringComparison.Ordinal));
    True(!research.Contains("RelatedWikiPagesView", StringComparison.Ordinal));

    var information = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "FullBroadcastInfoView.axaml"));
    var topics = information.IndexOf("Text=\"Topics\"", StringComparison.Ordinal);
    var transcript = information.IndexOf("Text=\"Transcript\"", StringComparison.Ordinal);
    True(topics >= 0 && transcript > topics);
    True(!information.Contains("RvTranscriptBrush", StringComparison.Ordinal));
    True(!information.Contains("RelatedWikiPagesView", StringComparison.Ordinal));

    var dashboard = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "DashboardView.axaml"));
    True(dashboard.Contains("FeaturedContinue.WikiSummary", StringComparison.Ordinal));
    True(dashboard.Contains("CurrentOnThisDay.WikiSummary", StringComparison.Ordinal));

    var search = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "SearchView.axaml"));
    True(search.Contains("Find broadcasts by what happened", StringComparison.Ordinal));
    True(search.Contains("Ways into the archive", StringComparison.Ordinal));
    True(search.Contains("Classes=\"framed-row\"", StringComparison.Ordinal));
}

static void Alpha9HardensDocumentedKnowledgePortability()
{
    Equal("0.41.0", File.ReadAllText(Path.Combine(SourceRoot(), "VERSION.txt")).Trim());

    var shell = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "MainWindowViewModel.cs"));
    True(shell.Contains("\"wiki\", \"Explore\"", StringComparison.Ordinal));
    True(shell.Contains("\"research\", \"Knowledge\"", StringComparison.Ordinal));
    True(shell.IndexOf("\"tools\", \"Settings\"", StringComparison.Ordinal) < shell.IndexOf("\"now-playing\", \"Now Playing\"", StringComparison.Ordinal));
    True(shell.Contains("Wiki.SetOpenBroadcastInfoHandler", StringComparison.Ordinal));

    var wikiViewModel = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "WikiViewModel.cs"));
    True(wikiViewModel.Contains("_openBroadcastInfo(link.EpisodeId)", StringComparison.Ordinal));
    True(!wikiViewModel.Contains("_playback.LoadAndPlayAtAsync(link.EpisodeId", StringComparison.Ordinal));

    var library = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "LibraryView.axaml"));
    var nowPlaying = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "NowPlayingView.axaml"));
    True(!library.Contains("RelatedWikiPagesView", StringComparison.Ordinal));
    True(!nowPlaying.Contains("RelatedWikiPagesView", StringComparison.Ordinal));
    True(nowPlaying.Contains("Header=\"Read transcript\"", StringComparison.Ordinal));
    True(nowPlaying.Contains("Nothing queued yet", StringComparison.Ordinal));

    var dashboard = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "DashboardView.axaml"));
    True(dashboard.Split("Height=\"292\"", StringSplitOptions.None).Length >= 4);
    True(dashboard.Contains("Width=\"88\" HorizontalAlignment=\"Right\"", StringComparison.Ordinal));

    var database = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Infrastructure", "Services", "DatabaseService.cs"));
    var provider = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Infrastructure", "Services", "WebArchiveProvider.RemoteAdministration.cs"));
    True(database.Contains("BuildCompleteKnowledgePack", StringComparison.Ordinal));
    True(provider.Contains("BuildCompleteKnowledgePack(AppVersionService.Version)", StringComparison.Ordinal));
    True(provider.Contains("RadioVault-Archive-Knowledge.trvknowledge", StringComparison.Ordinal));

    var wikiService = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Services", "Services", "WikiService.cs"));
    True(wikiService.Contains("ResolveImportedSourceTitle", StringComparison.Ordinal));
    True(wikiService.Contains("ResolveImportPageIdentitiesAsync", StringComparison.Ordinal));
    var packs = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Infrastructure", "Services", "KnowledgePackService.cs"));
    True(packs.Contains("pack_documentation", StringComparison.Ordinal));
    True(packs.Contains("pack_change_log", StringComparison.Ordinal));
    True(packs.Contains("ValidateReadableDatabase", StringComparison.Ordinal));
    True(provider.Contains("CreateKnowledgeImportBackup", StringComparison.Ordinal));
}

static void WikiRefinementAddsNavigationDiscoveryAndTimelineExploration()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var database = new SqliteDatabase(Path.Combine(directory, "wiki-refinement.sqlite"));
        database.Initialize();
        var service = new WikiService(database);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var show = service.SavePageAsync(new WikiPageDraft(null, "show-a", "Show A", "Show", "A show", "# Show A\n\nSee [[Person B]] and [[Missing Person]].", "Published", 0,
            "Created", "Human", Timeline: new[] { new WikiTimelineEventRecord(Guid.NewGuid(), Guid.Empty, "A major event", "The story changed.", "Milestone", today, null, "Day", today.ToString("d MMMM yyyy"), 90, 0,
                Array.Empty<Guid>(), Array.Empty<Guid>(), Array.Empty<WikiTimelineBroadcastLink>()) })).GetAwaiter().GetResult();
        var person = service.SavePageAsync(new WikiPageDraft(null, "person-b", "Person B", "Person", "A person", "# Person B\n\nWorked on [[Show A]].", "Published", 0,
            "Created", "Human")).GetAwaiter().GetResult();
        var orphan = service.SavePageAsync(new WikiPageDraft(null, "orphan", "Orphan Page", "Topic", "Disconnected", "# Orphan", "Draft", 0,
            "Created", "Human")).GetAwaiter().GetResult();
        service.SavePageAsync(new WikiPageDraft(null, "other-person", "Other Person", "Person", "Possible duplicate", "# Other", "Draft", 0,
            "Created", "Human", new[] { "Person B" })).GetAwaiter().GetResult();

        var navigation = service.GetNavigationContextAsync(show.PageId).GetAwaiter().GetResult();
        True(navigation.RelatedPages.Any(x => x.PageId == person.PageId));
        True(navigation.MissingLinks.Any(x => x.Target == "Missing Person"));
        var personNavigation = service.GetNavigationContextAsync(person.PageId).GetAwaiter().GetResult();
        True(personNavigation.Backlinks.Any(x => x.PageId == show.PageId));

        var highlights = service.GetDashboardHighlightsAsync(today.Month, today.Day).GetAwaiter().GetResult();
        True(highlights.OnThisDay.Any(x => x.Page.PageId == show.PageId));
        True(highlights.Eras.Any(x => x.StartYear == today.Year / 10 * 10));
        True(service.GetTimelineShowsAsync().GetAwaiter().GetResult().Any(x => x.Page.PageId == show.PageId));

        var quality = service.AuditQualityAsync().GetAwaiter().GetResult();
        True(quality.BrokenLinks.Any(x => x.Target == "Missing Person"));
        True(quality.OrphanPages.Any(x => x.PageId == orphan.PageId));
        True(quality.DuplicatePages.Any(x => x.First.PageId == person.PageId || x.Second.PageId == person.PageId));

        var viewModel = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "WikiViewModel.cs"));
        True(viewModel.Contains("NavigateBackAsync", StringComparison.Ordinal));
        True(viewModel.Contains("ShowTimelineExplorerAsync", StringComparison.Ordinal));
        True(viewModel.Contains("AuditQualityAsync", StringComparison.Ordinal));
        var native = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "WikiView.axaml"));
        True(native.Contains("Timeline Explorer", StringComparison.Ordinal));
        True(native.Contains("Content=\"&#x22EF;\"", StringComparison.Ordinal));
        True(native.Contains("IsVisible=\"{Binding IsArticleMode}\"", StringComparison.Ordinal));
        var web = ReadWebServerSourceBundle();
        True(web.Contains("function wikiNavBar", StringComparison.Ordinal));
        True(web.Contains("showWikiTimelineExplorer", StringComparison.Ordinal));
        True(web.Contains("wikiNavigation", StringComparison.Ordinal));
    }
    finally { try { Directory.Delete(directory, true); } catch { } }
}

static void CanonicalTopicsAutomaticallyMergeSafeDuplicates()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var database = new SqliteDatabase(Path.Combine(directory, "topic-cleanup.sqlite"));
        database.Initialize();
        using (var connection = database.OpenConnection())
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                INSERT INTO collections(id,name,sort_name) VALUES(91,'Topic Test','Topic Test');
                INSERT INTO episodes(id,collection_id,title,status,date_added,updated_at) VALUES(901,91,'Topic test','Unplayed',$now,$now);
                INSERT INTO research_broadcasts(id,identity_key,collection_id,episode_id,headline,research_json,research_state,existence_status,confidence,needs_review,created_at,updated_at)
                VALUES(801,'TOPIC-TEST',91,901,'Topic test','{}','in_library','in_library',100,0,$now,$now);
                INSERT INTO research_topics(research_broadcast_id,topic,confidence,notes,created_at) VALUES(801,'Fez''s Health',100,'',$now),(801,'Fez Health',100,'',$now);
                INSERT INTO tags(id,name) VALUES(701,'Fez''s Health'),(702,'Fez Health');
                INSERT INTO episode_tags(episode_id,tag_id) VALUES(901,701),(901,702);
                """;
            setup.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            setup.ExecuteNonQuery();
        }
        var service = new WikiService(database);
        var first = service.SavePageAsync(new WikiPageDraft(null, "fezs-health", "Fez's Health", "Topic", "First", "# Fez's Health", "Published", 0, "Created", "Human")).GetAwaiter().GetResult();
        service.SavePageAsync(new WikiPageDraft(null, "fez-health", "Fez Health", "Topic", "Second", "# Fez Health", "Published", 0, "Created", "Human")).GetAwaiter().GetResult();

        var audit = service.AuditTopicsAsync().GetAwaiter().GetResult();
        True(audit.Suggestions.Any(x => x.SafeToAutomate && x.Variants.Count == 2));
        var cleanup = service.RunAutomaticTopicCleanupAsync().GetAwaiter().GetResult();
        Equal(1, cleanup.GroupsMerged);

        using var check = database.OpenConnection();
        using var topicCount = check.CreateCommand();
        topicCount.CommandText = "SELECT COUNT(DISTINCT topic) FROM research_topics WHERE research_broadcast_id=801";
        Equal(1L, Convert.ToInt64(topicCount.ExecuteScalar()));
        using var tagCount = check.CreateCommand();
        tagCount.CommandText = "SELECT COUNT(*) FROM episode_tags WHERE episode_id=901";
        Equal(1L, Convert.ToInt64(tagCount.ExecuteScalar()));
        Equal(1, service.BrowseAsync(new WikiBrowseQuery(PageType: "Topic", Limit: 50)).GetAwaiter().GetResult().Count);
        True(service.GetPageAsync(first.PageId).GetAwaiter().GetResult() is not null);
        True(service.AuditTopicsAsync().GetAwaiter().GetResult().RecentMerges.Count > 0);

        var view = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "WikiView.axaml"));
        True(view.Contains("Canonical topic cleanup", StringComparison.Ordinal));
        True(view.Contains("Run safe automatic cleanup", StringComparison.Ordinal));
    }
    finally { try { Directory.Delete(directory, true); } catch { } }
}

static void WikiStarterPagesAreArchiveAwareAndIdempotent()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var database = new SqliteDatabase(Path.Combine(directory, "wiki-starters.sqlite"));
        database.Initialize();
        using (var connection = database.OpenConnection())
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                INSERT INTO collections(name,sort_name) VALUES('Starter Test Show','Starter Test Show');
                INSERT INTO episodes(id,collection_id,air_date,title,status,date_added,updated_at) VALUES
                    (10,(SELECT id FROM collections WHERE name='Starter Test Show'),'2001-01-01','New Year','Unplayed',$now,$now),
                    (11,(SELECT id FROM collections WHERE name='Starter Test Show'),'2001-01-02','Second show','Unplayed',$now,$now);
                INSERT INTO guests(id,name) VALUES(1,'Jane Example');
                INSERT INTO episode_guests(episode_id,guest_id) VALUES(10,1),(11,1);
                INSERT INTO tags(id,name) VALUES(1,'Comedy');
                INSERT INTO episode_tags(episode_id,tag_id) VALUES(10,1),(11,1);
                """;
            setup.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            setup.ExecuteNonQuery();
        }
        var service = new WikiService(database);
        var preview = service.PreviewStarterPagesAsync().GetAwaiter().GetResult();
        True(preview.ShowCount >= 1);
        Equal(1, preview.PersonCount);
        Equal(1, preview.TopicCount);
        var first = service.GenerateStarterPagesAsync().GetAwaiter().GetResult();
        True(first.CreatedPages >= 3);
        var second = service.GenerateStarterPagesAsync().GetAwaiter().GetResult();
        Equal(0, second.CreatedPages);
        Equal(first.CreatedPages, second.PreservedPages);
        Equal(first.CreatedPages, service.GetOverviewAsync().GetAwaiter().GetResult().PageCount);
    }
    finally { try { Directory.Delete(directory, true); } catch { } }
}

static void HumanWikiEvidenceEditsSaveWithArticleRevision()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var database = new SqliteDatabase(Path.Combine(directory, "wiki-human-evidence.sqlite"));
        database.Initialize();
        var service = new WikiService(database);
        var created = service.SavePageAsync(new WikiPageDraft(null, "show", "Show", "Show", "Summary", "# Show", "Draft", 0,
            "Created", "Human")).GetAwaiter().GetResult();
        var sourceId = Guid.NewGuid();
        var source = new WikiSourceRecord(sourceId, "Web", "Published source", "Author", "Publisher", "https://example.test", "",
            new DateOnly(2000, 1, 1), "Day", DateTimeOffset.UtcNow, null, "", null, null, null, null, "Page 4", "");
        var bytes = new byte[] { 1, 2, 3, 4 };
        var imageId = Guid.NewGuid();
        var image = new WikiImageRecord(imageId, "photo.png", "image/png", WikiAuthoringPackService.Sha256(bytes), bytes.Length,
            "Caption", "Alt", "Creator", "Creator", "Permission granted", sourceId, new DateOnly(2000, 1, 1), null, null, "Day", "Dated by source");
        var eventId = Guid.NewGuid();
        var saved = service.SavePageAsync(new WikiPageDraft(created.PageId, "show", "Show", "Show", "Summary", "# Show\n\nSourced.[1]", "Published", 1,
            "Added evidence", "Human", Array.Empty<string>(),
            new[] { new WikiCitationRecord(Guid.NewGuid(), created.PageId, sourceId, 1, "history", "", "Supports history", source) },
            new[] { new WikiImageDraft(new WikiPageImageLink(created.PageId, imageId, "Lead", 0, image), bytes) },
            new[] { new WikiTimelineEventRecord(eventId, created.PageId, "Milestone", "Something happened", "History", new DateOnly(2000, 1, 1), null,
                "Day", "1 January 2000", 80, 0, new[] { sourceId }, new[] { imageId }, Array.Empty<WikiTimelineBroadcastLink>()) }))
            .GetAwaiter().GetResult();
        Equal(2, saved.Revision);
        var page = service.GetPageAsync(created.PageId).GetAwaiter().GetResult()!;
        Equal(1, page.Citations.Count);
        Equal(1, page.Images.Count);
        Equal(1, page.Timeline.Count);
        Equal("Published source", page.Citations[0].Source!.Title);
    }
    finally { try { Directory.Delete(directory, true); } catch { } }
}

static void WikiPacksCarryArchiveContextAndDetailedReview()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var database = new SqliteDatabase(Path.Combine(directory, "wiki-context.sqlite"));
        database.Initialize();
        using (var connection = database.OpenConnection())
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                INSERT INTO collections(name,sort_name) VALUES('Archive Context Test Show','Archive Context Test Show');
                INSERT INTO episodes(id,collection_id,air_date,title,status,date_added,updated_at,broadcast_uid)
                VALUES(1,(SELECT id FROM collections WHERE name='Archive Context Test Show'),'2020-01-01','Broadcast','Unplayed',$now,$now,'ARCHIVE-1');
                INSERT INTO transcripts(id,episode_id,status,source,full_text,word_count,duration_ms,created_at,updated_at)
                VALUES(1,1,'Complete','local','hello',1,1000,$now,$now);
                INSERT INTO transcript_segments(transcript_id,segment_index,start_ms,end_ms,text) VALUES(1,0,0,1000,'hello');
                """;
            setup.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            setup.ExecuteNonQuery();
        }
        var service = new WikiService(database);
        var pageId = service.SavePageAsync(new WikiPageDraft(null, "archive-show", "Archive Show", "Show", "Baseline", "# Archive Show", "Draft", 0,
            "Created", "Human")).GetAwaiter().GetResult().PageId;
        var snapshot = service.GetAuthoringSnapshotAsync("0.35.0-alpha2", "context-test").GetAwaiter().GetResult();
        Equal(1, snapshot.ArchiveContext!.TranscriptCount);
        Equal(1, snapshot.ArchiveContext.Broadcasts.Count);
        var pack = new WikiAuthoringPackService().Export(snapshot);
        using (var archive = new ZipArchive(new MemoryStream(pack), ZipArchiveMode.Read))
            True(archive.GetEntry("archive-context.json") is not null);
        var changed = snapshot with { PageMarkdown = new Dictionary<Guid, string>(snapshot.PageMarkdown) { [pageId] = "# Changed" } };
        var preview = service.PreviewImportAsync(changed, "agent.rvwiki", "hash").GetAwaiter().GetResult();
        Equal("Changed", preview.PageChanges!.Single().ChangeKind);
        service.SavePageAsync(new WikiPageDraft(pageId, "archive-show", "Archive Show", "Show", "Human edit", "# Human", "Draft", 1,
            "Human changed it", "Human")).GetAwaiter().GetResult();
        var protectedPreview = service.PreviewImportAsync(changed, "agent.rvwiki", "hash").GetAwaiter().GetResult();
        Equal("Protected", protectedPreview.PageChanges!.Single().ChangeKind);
    }
    finally { try { Directory.Delete(directory, true); } catch { } }
}

static void CanonicalLibraryCutoverProjectsBroadcasts()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "canonical-cutover.sqlite");
    try
    {
        var database = new SqliteDatabase(path);
        database.Initialize();
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Canonical Test','Canonical Test');

                INSERT INTO episodes(id,collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid,hidden,favourite)
                VALUES(1001,(SELECT id FROM collections WHERE name='Canonical Test'),'2001-01-01','High','Adopted survivor','In Progress',$now,$now,'OLD-A',0,1);
                INSERT INTO episodes(id,collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid,hidden,favourite)
                VALUES(1002,(SELECT id FROM collections WHERE name='Canonical Test'),'2001-01-01','High','Retired alias','Unplayed',$now,$now,'OLD-B',1,0);
                INSERT INTO episodes(id,collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid,hidden,favourite)
                VALUES(1003,(SELECT id FROM collections WHERE name='Canonical Test'),'2001-01-02','High','Held preferred','Unplayed',$now,$now,'HELD-A',0,0);
                INSERT INTO episodes(id,collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid,hidden,favourite)
                VALUES(1004,(SELECT id FROM collections WHERE name='Canonical Test'),'2001-01-02','High','Held alternate','Completed',$now,$now,'HELD-B',0,1);

                INSERT INTO media_files(id,episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,storage_state,is_preferred)
                VALUES(2001,1001,'/archive/a-part1.mp3','a-part1.mp3',100,$now,0,$now,1000,'AvailableOffline',1);
                INSERT INTO media_files(id,episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,storage_state,is_preferred)
                VALUES(2002,1001,'/archive/a-part2.mp3','a-part2.mp3',100,$now,0,$now,1200,'AvailableOffline',0);
                INSERT INTO media_files(id,episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,storage_state,is_preferred)
                VALUES(2003,1003,'/archive/h-main.mp3','h-main.mp3',100,$now,0,$now,800,'AvailableOffline',1);
                INSERT INTO media_files(id,episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,storage_state,is_preferred)
                VALUES(2004,1004,'/archive/h-alt.mp3','h-alt.mp3',100,$now,0,$now,900,'AvailableOffline',1);

                INSERT INTO playback_state(episode_id,position_ms,completed,last_played_at,duration_ms,playback_speed)
                VALUES(1001,500,0,$now,2200,1.0);
                INSERT INTO playback_state(episode_id,position_ms,completed,last_played_at,duration_ms,playback_speed)
                VALUES(1004,900,1,$now,900,1.0);

                INSERT INTO library_truth_runs(id,started_at,completed_at,status,parser_version,source_file_count,current_broadcast_count,proposed_broadcast_count)
                VALUES(3001,$now,$now,'completed','alpha11-test',4,4,2);
                INSERT INTO library_truth_broadcasts(run_id,canonical_key,collection_name,air_date,broadcast_slot,file_count,segment_count,recording_count,current_episode_count,status,confidence_score,adoption_state,adoption_reason,preferred_recording_key)
                VALUES(3001,'CANONICAL-ADOPTED','Canonical Test','2001-01-01','',2,2,1,2,'Proposed changes',95,'Ready','Eligible','REC-A');
                INSERT INTO library_truth_broadcasts(run_id,canonical_key,collection_name,air_date,broadcast_slot,file_count,segment_count,recording_count,current_episode_count,status,confidence_score,adoption_state,adoption_reason,preferred_recording_key)
                VALUES(3001,'CANONICAL-HELD','Canonical Test','2001-01-02','',2,1,2,2,'Needs attention',70,'Review recommended','Review variants','REC-H1');

                INSERT INTO library_truth_files(run_id,media_file_id,current_episode_id,path,original_filename,canonical_broadcast_key,recording_key,proposed_part)
                VALUES(3001,2001,1001,'/archive/a-part1.mp3','a-part1.mp3','CANONICAL-ADOPTED','REC-A',1);
                INSERT INTO library_truth_files(run_id,media_file_id,current_episode_id,path,original_filename,canonical_broadcast_key,recording_key,proposed_part)
                VALUES(3001,2002,1002,'/archive/a-part2.mp3','a-part2.mp3','CANONICAL-ADOPTED','REC-A',2);
                INSERT INTO library_truth_files(run_id,media_file_id,current_episode_id,path,original_filename,canonical_broadcast_key,recording_key,proposed_part)
                VALUES(3001,2003,1003,'/archive/h-main.mp3','h-main.mp3','CANONICAL-HELD','REC-H1',1);
                INSERT INTO library_truth_files(run_id,media_file_id,current_episode_id,path,original_filename,canonical_broadcast_key,recording_key,proposed_part)
                VALUES(3001,2004,1004,'/archive/h-alt.mp3','h-alt.mp3','CANONICAL-HELD','REC-H2',1);

                INSERT INTO library_truth_recordings(run_id,canonical_broadcast_key,recording_key,label,file_count,segment_count,duration_ms,role,is_preferred_candidate)
                VALUES(3001,'CANONICAL-ADOPTED','REC-A','Multipart',2,2,2200,'Multipart assembly',1);
                INSERT INTO library_truth_recordings(run_id,canonical_broadcast_key,recording_key,label,file_count,segment_count,duration_ms,role,is_preferred_candidate)
                VALUES(3001,'CANONICAL-HELD','REC-H1','Primary',1,1,800,'Full capture',1);
                INSERT INTO library_truth_recordings(run_id,canonical_broadcast_key,recording_key,label,file_count,segment_count,duration_ms,role,is_preferred_candidate)
                VALUES(3001,'CANONICAL-HELD','REC-H2','Alternate',1,1,900,'Alternate capture',0);
                INSERT INTO library_truth_coverages(run_id,source_broadcast_key,recording_key,segment_number,segment_total,target_broadcast_key,start_offset_ms,end_offset_ms,media_file_ids_json)
                VALUES(3001,'CANONICAL-ADOPTED','REC-A',1,2,'CANONICAL-ADOPTED',0,1000,'[2001]');
                INSERT INTO library_truth_coverages(run_id,source_broadcast_key,recording_key,segment_number,segment_total,target_broadcast_key,start_offset_ms,end_offset_ms,media_file_ids_json)
                VALUES(3001,'CANONICAL-ADOPTED','REC-A',2,2,'CANONICAL-ADOPTED',1000,2200,'[2002]');
                INSERT INTO library_truth_coverages(run_id,source_broadcast_key,recording_key,segment_number,segment_total,target_broadcast_key,start_offset_ms,end_offset_ms,media_file_ids_json)
                VALUES(3001,'CANONICAL-HELD','REC-H1',1,1,'CANONICAL-HELD',0,800,'[2003]');
                INSERT INTO library_truth_coverages(run_id,source_broadcast_key,recording_key,segment_number,segment_total,target_broadcast_key,start_offset_ms,end_offset_ms,media_file_ids_json)
                VALUES(3001,'CANONICAL-HELD','REC-H2',1,1,'CANONICAL-HELD',0,900,'[2004]');

                INSERT INTO canonical_broadcasts(canonical_key,collection_name,air_date,broadcast_slot,preferred_recording_key,confidence_score,source_truth_run_id,adopted_at)
                VALUES('CANONICAL-ADOPTED','Canonical Test','2001-01-01','','REC-A',95,3001,$now);
                INSERT INTO recordings(recording_key,canonical_key,label,duration_ms,role,completeness_score,preferred_score,is_preferred,source_truth_run_id,adopted_at)
                VALUES('REC-A','CANONICAL-ADOPTED','Multipart',2200,'Multipart assembly',100,100,1,3001,$now);
                INSERT INTO recording_segments(recording_key,segment_number,segment_total,start_offset_ms,end_offset_ms,media_file_ids_json,source_truth_run_id,adopted_at)
                VALUES('REC-A',1,2,0,1000,'[2001]',3001,$now);
                INSERT INTO recording_segments(recording_key,segment_number,segment_total,start_offset_ms,end_offset_ms,media_file_ids_json,source_truth_run_id,adopted_at)
                VALUES('REC-A',2,2,1000,2200,'[2002]',3001,$now);
                INSERT INTO episode_canonical_map(episode_id,canonical_key,survivor_episode_id,is_survivor,source_truth_run_id,adopted_at)
                VALUES(1001,'CANONICAL-ADOPTED',1001,1,3001,$now);
                INSERT INTO episode_canonical_map(episode_id,canonical_key,survivor_episode_id,is_survivor,source_truth_run_id,adopted_at)
                VALUES(1002,'CANONICAL-ADOPTED',1001,0,3001,$now);

                INSERT INTO library_truth_rehearsal_runs(id,truth_run_id,started_at,completed_at,status,rollback_verified)
                VALUES(4001,3001,$now,$now,'completed',1);
                INSERT INTO library_truth_adoption_runs(truth_run_id,rehearsal_run_id,app_version,started_at,completed_at,status,commit_verified,foreign_key_violations,integrity_check)
                VALUES(3001,4001,'alpha11-test',$now,$now,'completed',1,0,'ok');
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        var service = new CanonicalLibraryQueryService(database);
        var summary = service.GetSummary();
        True(summary.IsCutoverReady);
        Equal(2, summary.Broadcasts);
        Equal(1, summary.AdoptedBroadcasts);
        Equal(1, summary.NeedsAttentionBroadcasts);
        Equal(3, summary.Recordings);
        Equal(4, summary.CoverageRows);
        Equal(4, summary.PhysicalFiles);

        var broadcasts = service.GetBroadcasts();
        Equal(2, broadcasts.Count);
        Equal(2, broadcasts.Select(x => x.CanonicalKey).Distinct().Count());
        var adopted = broadcasts.Single(x => x.CanonicalKey == "CANONICAL-ADOPTED");
        Equal(1001L, adopted.RepresentativeEpisodeId);
        Equal(2, adopted.SegmentCount);
        Equal(2, adopted.PhysicalFileCount);
        var held = broadcasts.Single(x => x.CanonicalKey == "CANONICAL-HELD");
        Equal(1003L, held.RepresentativeEpisodeId);
        True(held.NeedsAttention);
        Equal("Completed", held.ListeningStatus);

        var alias = service.ResolveEpisode(1002) ?? throw new InvalidOperationException("Alias did not resolve.");
        Equal(1001L, alias.RepresentativeEpisodeId);
        True(alias.Adopted);
        Equal(2, service.ExpandStateEpisodeIds(1002).Count);
        var adoptedPointLookup = service.GetBroadcast(1002) ?? throw new InvalidOperationException("Adopted point lookup failed.");
        Equal("CANONICAL-ADOPTED", adoptedPointLookup.CanonicalKey);
        Equal(1001L, adoptedPointLookup.RepresentativeEpisodeId);
        var heldAlternate = service.ResolveEpisode(1004) ?? throw new InvalidOperationException("Held member did not resolve.");
        Equal(1003L, heldAlternate.RepresentativeEpisodeId);
        True(!heldAlternate.Adopted);
        Equal(2, service.ExpandStateEpisodeIds(1004).Count);
        var heldPointLookup = service.GetBroadcast(1004) ?? throw new InvalidOperationException("Held point lookup failed.");
        Equal("CANONICAL-HELD", heldPointLookup.CanonicalKey);
        Equal(1003L, heldPointLookup.RepresentativeEpisodeId);
        True(service.GetBroadcast(999_999) is null);

        var databaseService = new DatabaseService(database);
        var webBoundaryLookup = databaseService.GetEpisode(1002) ?? throw new InvalidOperationException("Database point lookup failed.");
        Equal(1001L, webBoundaryLookup.Id);
        Equal("CANONICAL-ADOPTED", webBoundaryLookup.CanonicalKey);

        var plan = service.GetPreferredPlaybackPlan("CANONICAL-ADOPTED") ?? throw new InvalidOperationException("Playback plan missing.");
        Equal(2, plan.Segments.Count);
        Equal(2200L, plan.DurationMs);
        Equal(2001L, plan.Segments[0].PreferredSource?.MediaFileId ?? 0);
        Equal(2002L, plan.Segments[1].PreferredSource?.MediaFileId ?? 0);

        var downloadManifest = service.GetDownloadManifest("CANONICAL-ADOPTED")
            ?? throw new InvalidOperationException("Canonical download manifest missing.");
        Equal(2, downloadManifest.Parts.Count);
        Equal(100L, downloadManifest.Parts[0].SizeBytes);
        Equal(100L, downloadManifest.Parts[1].SizeBytes);
        Equal(200L, downloadManifest.TotalSizeBytes);

        var heldPlan = service.GetPreferredPlaybackPlan("CANONICAL-HELD") ?? throw new InvalidOperationException("Held playback plan missing.");
        Equal(1, heldPlan.Segments.Count);
        Equal(2003L, heldPlan.Segments[0].PreferredSource?.MediaFileId ?? 0);

        var adoptedReason = service.ExplainRecordingSelection("CANONICAL-ADOPTED") ?? throw new InvalidOperationException("Adopted selection reason missing.");
        True(adoptedReason.IsAdopted);
        True(!adoptedReason.IsHeldFallback);
        True(adoptedReason.IsComplete);
        Equal("REC-A", adoptedReason.RecordingKey);

        var heldReason = service.ExplainRecordingSelection("CANONICAL-HELD") ?? throw new InvalidOperationException("Held selection reason missing.");
        True(!heldReason.IsAdopted);
        True(heldReason.IsHeldFallback);
        Equal("REC-H1", heldReason.RecordingKey);

        var audit = service.GetAuditSnapshot();
        Equal(3001L, audit.TruthRunId);
        Equal(2, audit.Broadcasts);
        Equal(1, audit.AdoptedBroadcasts);
        Equal(1, audit.HeldBroadcasts);
        Equal(0, audit.InvalidPreferredRecordingBroadcasts);

        var guardedPromotion = new CanonicalScanPromotionService(database).PromoteUnmappedEpisodes();
        Equal(0, guardedPromotion.BroadcastsAdded);
        Equal(0, guardedPromotion.EpisodesMapped);

        // Older servers could append a second canonical key for an episode that
        // was already represented by the sealed, held Library Truth baseline.
        // Preserve that historical anomaly in this fixture to prove the public
        // Library still projects one playable broadcast identity.
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO media_files(id,episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,storage_state,is_preferred)
                VALUES(2005,1001,'/archive/a-alias.mp3','a-alias.mp3',100,$now,0,$now,2200,'AvailableOffline',0);
                INSERT INTO library_truth_broadcasts(run_id,canonical_key,collection_name,air_date,broadcast_slot,file_count,segment_count,recording_count,current_episode_count,status,confidence_score,adoption_state,adoption_reason,preferred_recording_key)
                VALUES(3001,'CANONICAL-ALIAS','Canonical Test','2001-01-01','PM',1,1,1,1,'Needs attention',60,'Review recommended','Duplicate playable identity','REC-ALIAS');
                INSERT INTO library_truth_files(run_id,media_file_id,current_episode_id,path,original_filename,canonical_broadcast_key,recording_key,proposed_part)
                VALUES(3001,2005,1001,'/archive/a-alias.mp3','a-alias.mp3','CANONICAL-ALIAS','REC-ALIAS',1);
                INSERT INTO library_truth_recordings(run_id,canonical_broadcast_key,recording_key,label,file_count,segment_count,duration_ms,role,is_preferred_candidate)
                VALUES(3001,'CANONICAL-ALIAS','REC-ALIAS','Alias',1,1,2200,'Full capture',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        Equal(3, service.GetBroadcasts().Count);
        var conflictedAudit = service.GetAuditSnapshot();
        Equal(1, conflictedAudit.DuplicatePlayableIdentityGroups);
        Equal(1, conflictedAudit.DuplicateCanonicalAliases);
        True(!conflictedAudit.IsClean);

        var browse = new LibraryBrowseService(database);
        var overview = browse.GetOverviewAsync().GetAwaiter().GetResult();
        Equal(2, overview.TotalBroadcasts);
        var browseResult = browse.BrowseAsync(new LibraryBrowseRequest(Limit: 100)).GetAwaiter().GetResult();
        Equal(2, browseResult.TotalMatching);
        Equal(2, browseResult.Broadcasts.Select(value => value.RepresentativeEpisodeId).Distinct().Count());

    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}


static void PostCutoverScanAppendsCanonicalBroadcasts()
{
    var directory = Path.Combine(Path.GetTempPath(), "rv-canonical-scan-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var path = Path.Combine(directory, "scan.db");
        var database = new SqliteDatabase(path);
        database.Initialize();
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Scan Test','Scan Test');

                INSERT INTO episodes(id,collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid,hidden,favourite,part_number,total_parts)
                VALUES(1101,(SELECT id FROM collections WHERE name='Scan Test'),'2001-01-01','High','Baseline','Unplayed',$now,$now,'SCAN-BASE',0,0,1,1);
                INSERT INTO media_files(id,episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,storage_state,is_preferred)
                VALUES(2101,1101,'/archive/baseline.mp3','baseline.mp3',100,$now,0,$now,1000,'AvailableOffline',1);
                INSERT INTO playback_state(episode_id,position_ms,completed,duration_ms,playback_speed)
                VALUES(1101,0,0,1000,1.0);

                INSERT INTO library_truth_runs(id,started_at,completed_at,status,parser_version,source_file_count,current_broadcast_count,proposed_broadcast_count)
                VALUES(3101,$now,$now,'completed','scan-test',1,1,1);
                INSERT INTO library_truth_broadcasts(run_id,canonical_key,collection_name,air_date,broadcast_slot,file_count,segment_count,recording_count,current_episode_count,status,confidence_score,adoption_state,adoption_reason,preferred_recording_key)
                VALUES(3101,'SCAN-TEST|2001-01-01|STANDARD','Scan Test','2001-01-01','',1,1,1,1,'Stable',95,'Ready','Eligible','SCAN-BASE-REC');
                INSERT INTO library_truth_files(run_id,media_file_id,current_episode_id,path,original_filename,canonical_broadcast_key,recording_key,proposed_part)
                VALUES(3101,2101,1101,'/archive/baseline.mp3','baseline.mp3','SCAN-TEST|2001-01-01|STANDARD','SCAN-BASE-REC',1);
                INSERT INTO library_truth_recordings(run_id,canonical_broadcast_key,recording_key,label,file_count,segment_count,duration_ms,role,is_preferred_candidate)
                VALUES(3101,'SCAN-TEST|2001-01-01|STANDARD','SCAN-BASE-REC','Baseline',1,1,1000,'Full capture',1);
                INSERT INTO library_truth_coverages(run_id,source_broadcast_key,recording_key,segment_number,segment_total,target_broadcast_key,start_offset_ms,end_offset_ms,media_file_ids_json)
                VALUES(3101,'SCAN-TEST|2001-01-01|STANDARD','SCAN-BASE-REC',1,1,'SCAN-TEST|2001-01-01|STANDARD',0,1000,'[2101]');

                INSERT INTO canonical_broadcasts(canonical_key,collection_name,air_date,broadcast_slot,preferred_recording_key,confidence_score,source_truth_run_id,adopted_at)
                VALUES('SCAN-TEST|2001-01-01|STANDARD','Scan Test','2001-01-01','','SCAN-BASE-REC',95,3101,$now);
                INSERT INTO recordings(recording_key,canonical_key,label,duration_ms,role,completeness_score,preferred_score,is_preferred,source_truth_run_id,adopted_at)
                VALUES('SCAN-BASE-REC','SCAN-TEST|2001-01-01|STANDARD','Baseline',1000,'Full capture',100,100,1,3101,$now);
                INSERT INTO recording_segments(recording_key,segment_number,segment_total,start_offset_ms,end_offset_ms,media_file_ids_json,source_truth_run_id,adopted_at)
                VALUES('SCAN-BASE-REC',1,1,0,1000,'[2101]',3101,$now);
                INSERT INTO episode_canonical_map(episode_id,canonical_key,survivor_episode_id,is_survivor,source_truth_run_id,adopted_at)
                VALUES(1101,'SCAN-TEST|2001-01-01|STANDARD',1101,1,3101,$now);

                INSERT INTO library_truth_rehearsal_runs(id,truth_run_id,started_at,completed_at,status,rollback_verified)
                VALUES(4101,3101,$now,$now,'completed',1);
                INSERT INTO library_truth_adoption_runs(truth_run_id,rehearsal_run_id,app_version,started_at,completed_at,status,commit_verified,foreign_key_violations,integrity_check)
                VALUES(3101,4101,'scan-test',$now,$now,'completed',1,0,'ok');

                INSERT INTO episodes(id,collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid,hidden,favourite,part_number,total_parts)
                VALUES(1102,(SELECT id FROM collections WHERE name='Scan Test'),'2001-01-02','High','New multipart','Unplayed',$now,$now,'SCAN-NEW-P1',0,0,1,2);
                INSERT INTO episodes(id,collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid,hidden,favourite,part_number,total_parts)
                VALUES(1103,(SELECT id FROM collections WHERE name='Scan Test'),'2001-01-02','High','New multipart','Unplayed',$now,$now,'SCAN-NEW-P2',0,0,2,2);
                INSERT INTO media_files(id,episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,storage_state,is_preferred)
                VALUES(2102,1102,'/archive/new-part1.mp3','new-part1.mp3',120,$now,0,$now,1200,'AvailableOffline',1);
                INSERT INTO media_files(id,episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,storage_state,is_preferred)
                VALUES(2103,1103,'/archive/new-part2.mp3','new-part2.mp3',130,$now,0,$now,1300,'AvailableOffline',1);
                INSERT INTO playback_state(episode_id,position_ms,completed,duration_ms,playback_speed) VALUES(1102,0,0,1200,1.0);
                INSERT INTO playback_state(episode_id,position_ms,completed,duration_ms,playback_speed) VALUES(1103,0,0,1300,1.0);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        var promotion = new CanonicalScanPromotionService(database).PromoteUnmappedEpisodes();
        Equal(1, promotion.BroadcastsAdded);
        Equal(1, promotion.RecordingsAdded);
        Equal(2, promotion.EpisodesMapped);
        Equal(0, promotion.ItemsNeedingReview);

        var query = new CanonicalLibraryQueryService(database);
        var summary = query.GetSummary();
        True(summary.IsCutoverReady);
        Equal(2, summary.Broadcasts);
        Equal(2, summary.AdoptedBroadcasts);

        const string newKey = "SCAN-TEST|2001-01-02|STANDARD";
        var broadcasts = query.GetBroadcasts();
        Equal(2, broadcasts.Count);
        var added = broadcasts.Single(x => x.CanonicalKey == newKey);
        Equal(2, added.SegmentCount);
        Equal(2, added.PhysicalFileCount);

        var plan = query.GetPreferredPlaybackPlan(newKey)
            ?? throw new InvalidOperationException("Promoted playback plan missing.");
        Equal(2, plan.Segments.Count);
        Equal(2500L, plan.DurationMs);
        Equal(2102L, plan.Segments[0].PreferredSource?.MediaFileId ?? 0);
        Equal(2103L, plan.Segments[1].PreferredSource?.MediaFileId ?? 0);

        var secondPass = new CanonicalScanPromotionService(database).PromoteUnmappedEpisodes();
        Equal(0, secondPass.BroadcastsAdded);
        Equal(0, secondPass.RecordingsAdded);
        Equal(0, secondPass.EpisodesMapped);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}


static void ResearchDateUpdatesActiveAdoptedLibraryProjection()
{
    var directory = Path.Combine(Path.GetTempPath(), "rv-research-date-projection-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var path = Path.Combine(directory, "projection.db");
        var database = new SqliteDatabase(path);
        database.Initialize();
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name)
                VALUES('Ron Bennington Interviews','Ron Bennington Interviews');

                INSERT INTO episodes(id,collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid,hidden,favourite,part_number,total_parts)
                VALUES(1201,(SELECT id FROM collections WHERE name='Ron Bennington Interviews'),'2017-01-02','High','Interview test','Unplayed',$now,$now,'RBI-PROJECTION-TEST',0,0,1,1);
                INSERT INTO media_files(id,episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,storage_state,is_preferred)
                VALUES(2201,1201,'/archive/rbi-projection.mp3','rbi-projection.mp3',100,$now,0,$now,1000,'AvailableOffline',1);

                INSERT INTO library_truth_runs(id,started_at,completed_at,status,parser_version,source_file_count,current_broadcast_count,proposed_broadcast_count)
                VALUES(3201,$now,$now,'completed','adopted-projection',1,1,1);
                INSERT INTO library_truth_broadcasts(run_id,canonical_key,collection_name,air_date,broadcast_slot,file_count,segment_count,recording_count,current_episode_count,status,confidence_score,adoption_state,adoption_reason,preferred_recording_key)
                VALUES(3201,'RBI-PROJECTION-KEY','Ron Bennington Interviews','2017-01-02','',1,1,1,1,'Stable',95,'Ready','Eligible','RBI-PROJECTION-REC');
                INSERT INTO library_truth_files(run_id,media_file_id,current_episode_id,path,original_filename,current_air_date,proposed_air_date,canonical_broadcast_key,recording_key,proposed_part)
                VALUES(3201,2201,1201,'/archive/rbi-projection.mp3','rbi-projection.mp3','2017-01-02','2017-01-02','RBI-PROJECTION-KEY','RBI-PROJECTION-REC',1);

                INSERT INTO canonical_broadcasts(canonical_key,collection_name,air_date,broadcast_slot,preferred_recording_key,confidence_score,source_truth_run_id,adopted_at)
                VALUES('RBI-PROJECTION-KEY','Ron Bennington Interviews','2017-01-02','','RBI-PROJECTION-REC',95,3201,$now);
                INSERT INTO episode_canonical_map(episode_id,canonical_key,survivor_episode_id,is_survivor,source_truth_run_id,adopted_at)
                VALUES(1201,'RBI-PROJECTION-KEY',1201,1,3201,$now);
                INSERT INTO library_truth_rehearsal_runs(id,truth_run_id,started_at,completed_at,status,rollback_verified)
                VALUES(4201,3201,$now,$now,'completed',1);
                INSERT INTO library_truth_adoption_runs(truth_run_id,rehearsal_run_id,app_version,started_at,completed_at,status,commit_verified,foreign_key_violations,integrity_check)
                VALUES(3201,4201,'projection-test',$now,$now,'completed',1,0,'ok');

                -- A newer completed analysis exists, but it has not been adopted.
                -- The visible Library must continue to read run 3201.
                INSERT INTO library_truth_runs(id,started_at,completed_at,status,parser_version,source_file_count,current_broadcast_count,proposed_broadcast_count)
                VALUES(3202,$now,$now,'completed','newer-unadopted-projection',1,1,1);
                INSERT INTO library_truth_broadcasts(run_id,canonical_key,collection_name,air_date,broadcast_slot,file_count,segment_count,recording_count,current_episode_count,status,confidence_score,adoption_state,adoption_reason,preferred_recording_key)
                VALUES(3202,'RBI-PROJECTION-KEY','Ron Bennington Interviews','2017-01-02','',1,1,1,1,'Stable',95,'Ready','Eligible','RBI-PROJECTION-REC');
                INSERT INTO library_truth_files(run_id,media_file_id,current_episode_id,path,original_filename,current_air_date,proposed_air_date,canonical_broadcast_key,recording_key,proposed_part)
                VALUES(3202,2201,1201,'/archive/rbi-projection.mp3','rbi-projection.mp3','2017-01-02','2017-01-02','RBI-PROJECTION-KEY','RBI-PROJECTION-REC',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        var assignedDate = new DateOnly(2018, 3, 4);
        new ResearchWorkspaceService(database)
            .AssignBroadcastDateAsync(1201, assignedDate)
            .GetAwaiter()
            .GetResult();

        var visible = new CanonicalLibraryQueryService(database).GetBroadcasts()
            .Single(item => item.CanonicalKey == "RBI-PROJECTION-KEY");
        Equal(assignedDate, visible.AirDate);

        using var verification = database.OpenConnection();
        using var verify = verification.CreateCommand();
        verify.CommandText = """
            SELECT
                (SELECT air_date FROM episodes WHERE id=1201),
                (SELECT air_date FROM canonical_broadcasts WHERE canonical_key='RBI-PROJECTION-KEY'),
                (SELECT air_date FROM library_truth_broadcasts WHERE run_id=3201 AND canonical_key='RBI-PROJECTION-KEY'),
                (SELECT air_date FROM library_truth_broadcasts WHERE run_id=3202 AND canonical_key='RBI-PROJECTION-KEY'),
                (SELECT current_air_date FROM library_truth_files WHERE run_id=3201 AND media_file_id=2201),
                (SELECT proposed_air_date FROM library_truth_files WHERE run_id=3202 AND media_file_id=2201);
            """;
        using var reader = verify.ExecuteReader();
        True(reader.Read());
        for (var index = 0; index < 6; index++) Equal("2018-03-04", reader.GetString(index));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}


static void QuickDateReviewDecisionsPersistAndReopenSafely()
{
    var directory = Path.Combine(Path.GetTempPath(), "rv-quick-date-review-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var database = new SqliteDatabase(Path.Combine(directory, "date-review.db"));
        database.Initialize();
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name)
                VALUES('Ron Bennington Interviews','Ron Bennington Interviews');
                INSERT INTO episodes(id,collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid,hidden)
                VALUES(1301,(SELECT id FROM collections WHERE name='Ron Bennington Interviews'),'2017-01-02','High','Quick review test','Unplayed',$now,$now,'RBI-QUICK-REVIEW',0);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,storage_state,is_preferred)
                VALUES(1301,'/archive/rbi-quick-review.mp3','rbi-quick-review.mp3',100,$now,0,$now,1000,'AvailableOffline',1);
                INSERT INTO research_broadcasts(id,identity_key,collection_id,episode_id,source_broadcast_id,air_date,headline,research_json,research_state,existence_status,confidence,needs_review,created_at,updated_at)
                VALUES(5301,'RBI-QUICK-REVIEW',(SELECT id FROM collections WHERE name='Ron Bennington Interviews'),1301,'RBI-QUICK-REVIEW','2017-01-02','Quick review test',$json,'in_library','in_library',90,1,$now,$now);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$json", """
                {"broadcast_date":"2017-01-02","research":{"catalogue":{"date_review_status":"pending","date_review_date":"2018-03-04","date_review_basis":"Verified catalogue evidence"}}}
                """);
            command.ExecuteNonQuery();
        }

        new ResearchWorkspaceService(database)
            .ResolveCatalogueDateReviewAsync(5301, TheRadioVault.Services.Models.CatalogueDateReviewAction.Ignore)
            .GetAwaiter().GetResult();
        var ignored = new ResearchWorkspaceService(database).GetCatalogueDateReviewsAsync(includeResolved: true).GetAwaiter().GetResult().Single();
        True(ignored.IsIgnored);
        Equal(new DateOnly(2017, 1, 2), ignored.CurrentLibraryDate!.Value);

        new ResearchWorkspaceService(database)
            .ResolveCatalogueDateReviewAsync(5301, TheRadioVault.Services.Models.CatalogueDateReviewAction.Reopen)
            .GetAwaiter().GetResult();
        var reopened = new ResearchWorkspaceService(database).GetCatalogueDateReviewsAsync(includeResolved: true).GetAwaiter().GetResult().Single();
        True(reopened.IsPending);
        Equal(new DateOnly(2017, 1, 2), reopened.CurrentLibraryDate!.Value);

        new ResearchWorkspaceService(database)
            .ResolveCatalogueDateReviewAsync(5301, TheRadioVault.Services.Models.CatalogueDateReviewAction.KeepExisting)
            .GetAwaiter().GetResult();
        var completed = new ResearchWorkspaceService(database).GetCatalogueDateReviewsAsync(includeResolved: true).GetAwaiter().GetResult().Single();
        True(completed.IsCompleted);
        Equal("Kept existing Library date", completed.DecisionText);
        Equal(new DateOnly(2017, 1, 2), completed.CurrentLibraryDate!.Value);

        new ResearchWorkspaceService(database)
            .ResolveCatalogueDateReviewAsync(5301, TheRadioVault.Services.Models.CatalogueDateReviewAction.Reopen)
            .GetAwaiter().GetResult();
        new ResearchWorkspaceService(database)
            .ResolveCatalogueDateReviewAsync(5301, TheRadioVault.Services.Models.CatalogueDateReviewAction.ApproveLibraryDate)
            .GetAwaiter().GetResult();
        var approved = new ResearchWorkspaceService(database).GetCatalogueDateReviewsAsync(includeResolved: true).GetAwaiter().GetResult().Single();
        True(approved.IsCompleted);
        Equal(new DateOnly(2018, 3, 4), approved.CurrentLibraryDate!.Value);

        using var verification = database.OpenConnection();
        using var verify = verification.CreateCommand();
        verify.CommandText = "SELECT episodes.air_date,research_broadcasts.research_json FROM episodes JOIN research_broadcasts ON research_broadcasts.episode_id=episodes.id WHERE research_broadcasts.id=5301";
        using var reader = verify.ExecuteReader();
        True(reader.Read());
        Equal("2018-03-04", reader.GetString(0));
        True(reader.GetString(1).Contains("approved_library_date", StringComparison.Ordinal));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}


static void ResearchPacksRoundTripDateReviewDecisions()
{
    var decisions = new[]
    {
        "pending", "reopened", "approved_library_date", "kept_existing", "ignored",
        "recording_date_only", "release_date_only", "left_undated"
    };
    var pack = new TrvKnowledgePack
    {
        Manifest = new TrvPackManifest { AppVersion = "test", Show = "Ron Bennington Interviews" },
        Broadcasts = decisions.Select((decision, index) => new TrvPackBroadcast
        {
            BroadcastId = $"DATE-REVIEW-{index}",
            Show = "Ron Bennington Interviews",
            BroadcastDate = "2018-03-04",
            Research = new TrvPackResearch
            {
                Catalogue = new TrvPackCatalogueMetadata
                {
                    DateReviewStatus = decision,
                    DateReviewDate = "2018-03-04",
                    DateReviewBasis = "Verified catalogue evidence",
                    DateReviewNotes = "Human-reviewed decision",
                    DateReviewedAt = "2026-08-01T12:00:00Z",
                    DateReviewPreviousAirDate = "2017-01-02",
                    DateReviewPreviousConfidence = "High"
                }
            }
        }).ToList(),
        Transcripts = new List<TrvPackTranscript>
        {
            new()
            {
                BroadcastId = "DATE-REVIEW-0",
                Show = "Ron Bennington Interviews",
                BroadcastDate = "2018-03-04",
                Language = "en",
                Engine = "whisper.cpp",
                FullText = "A useful archive transcript.",
                HasSpeakerDiarization = true,
                Segments = new List<TrvPackTranscriptSegment>
                {
                    new() { Index = 0, StartMs = 1000, EndMs = 2500, Speaker = "Speaker 1", Text = "A useful archive transcript." }
                }
            }
        }
    };
    var pageId = Guid.NewGuid();
    pack.Wiki = new WikiAuthoringSnapshot(
        new WikiAuthoringPackManifest(1, "test", Guid.NewGuid(), DateTimeOffset.UtcNow, "test-db", 1, 0, 0, 0, 0,
            new Dictionary<string, string>()),
        new[] { new WikiAuthoringPageRecord(pageId, 1, "test-show", "Test Show", "Show", "A test Wiki page", "Published", "test", "test", Array.Empty<string>()) },
        new Dictionary<Guid, string> { [pageId] = "# Test Show\n\nA linked archive history." },
        Array.Empty<WikiRelationshipRecord>(), Array.Empty<WikiSourceRecord>(), Array.Empty<WikiCitationRecord>(),
        Array.Empty<WikiAuthoringImageRecord>(), new Dictionary<Guid, byte[]>(), Array.Empty<WikiPageImageLink>(),
        Array.Empty<WikiTimelineEventRecord>());

    var transfer = new KnowledgePackService();
    var bytes = transfer.ExportBytes(pack);
    True(bytes.AsSpan(0, 16).SequenceEqual(Encoding.ASCII.GetBytes("SQLite format 3\0")));
    var imported = transfer.Import(bytes);
    Equal(1, imported.Manifest.SchemaVersion);
    Equal("radiovault.archive-knowledge-database", imported.Manifest.Format);
    Equal(decisions.Length, imported.Broadcasts.Count);
    for (var index = 0; index < decisions.Length; index++)
    {
        var catalogue = imported.Broadcasts[index].Research.Catalogue;
        Equal(decisions[index], catalogue.DateReviewStatus);
        Equal("2018-03-04", catalogue.DateReviewDate);
        Equal("Verified catalogue evidence", catalogue.DateReviewBasis);
        Equal("Human-reviewed decision", catalogue.DateReviewNotes);
        Equal("2026-08-01T12:00:00Z", catalogue.DateReviewedAt);
        Equal("2017-01-02", catalogue.DateReviewPreviousAirDate);
        Equal("High", catalogue.DateReviewPreviousConfidence);
    }
    Equal(1, imported.Manifest.TranscriptCount);
    Equal("A useful archive transcript.", imported.Transcripts.Single().FullText);
    Equal("Speaker 1", imported.Transcripts.Single().Segments.Single().Speaker);
    Equal(1, imported.Wiki!.Pages.Count);
    Equal("Test Show", imported.Wiki.Pages.Single().Title);
}

static void ResearchPacksTolerateAiScalarVariations()
{
    var legacy = new MemoryStream();
    using (var archive = new ZipArchive(legacy, ZipArchiveMode.Create, leaveOpen: true))
    {
        static void WriteEntry(ZipArchive target, string name, string json)
        {
            var entry = target.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(json);
        }

        WriteEntry(archive, "manifest.json", """
            {"format":"theradiovault.knowledge-pack","schema_version":5,"show":"Test Show"}
            """);
        WriteEntry(archive, "broadcasts.json", """
            [{
              "broadcast_id":"TEST-2026-08-04",
              "show":"Test Show",
              "part_number":1,
              "research":{"catalogue":{"date_review_previous_confidence":88}},
              "sources":[{"url":"https://example.test/source","supports":"summary"}]
            }]
            """);
        WriteEntry(archive, "missing_broadcasts.json", "[]");
        WriteEntry(archive, "transcripts.json", "[]");
    }

    var rejected = false;
    try { _ = new KnowledgePackService().Import(legacy.ToArray()); }
    catch (Exception) { rejected = true; }
    True(rejected);
}


static void WhisperConfigurationExposesModelCapabilities()
{
    var settings = new WhisperCppEngineSettings
    {
        ModelPath = @"C:\models\ggml-small.en.bin",
        DiarizationSegmentationModelPath = @"C:\models\segmentation.onnx",
        DiarizationEmbeddingModelPath = @"C:\models\embedding.onnx",
        EnableMultiSpeakerDiarization = true
    };
    Equal("ggml-small.en", settings.ModelId);
    True(WhisperModelCatalog.Items.Any(x => x.Id == "base.en"));
    True(WhisperDownloadService.DiarizationSegmentationUrl.Contains("sherpa-onnx-pyannote", StringComparison.Ordinal));
    Equal(64, WhisperDownloadService.DiarizationSegmentationSha256.Length);
    Equal(64, WhisperDownloadService.DiarizationEmbeddingSha256.Length);

}

static void MultiSpeakerDiarizationSplitsTimedTranscriptTurns()
{
    var segment = new TranscriptSegment(0, 0, 4_000, "one two three four", Words: new[]
    {
        new TranscriptWord(0, 900, "one"),
        new TranscriptWord(1_000, 1_900, "two"),
        new TranscriptWord(2_000, 2_900, "three"),
        new TranscriptWord(3_000, 3_900, "four")
    });
    var turns = new[]
    {
        new SpeakerDiarizationTurn(0, 1_000, "speaker-1", "Speaker 1"),
        new SpeakerDiarizationTurn(1_000, 2_000, "speaker-2", "Speaker 2"),
        new SpeakerDiarizationTurn(2_000, 3_000, "speaker-3", "Speaker 3"),
        new SpeakerDiarizationTurn(3_000, 4_000, "speaker-4", "Speaker 4")
    };

    var merged = TranscriptSpeakerMerger.Apply(new[] { segment }, turns);
    Equal(4, merged.Count);
    Equal("Speaker 1", merged[0].Speaker);
    Equal("speaker-4", merged[3].SpeakerKey);
    Equal("four", merged[3].Text);
}

static void WhisperSettingsPersistForLiveDesktopEngine()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var store = new WhisperCppSettingsStore(Path.Combine(directory, "transcription.json"));
        store.Save(new WhisperCppEngineSettings
        {
            ExecutablePath = @"C:\worker\whisper-cli.exe",
            ModelPath = @"C:\models\ggml-base.en.bin",
            VadModelPath = @"C:\models\ggml-silero-v6.2.0.bin",
            DiarizationSegmentationModelPath = @"C:\models\segmentation.onnx",
            DiarizationEmbeddingModelPath = @"C:\models\embedding.onnx",
            DefaultLanguage = "en",
            Threads = 6,
            UseGpu = false,
            UseVoiceActivityDetection = true,
            EnableMultiSpeakerDiarization = true,
            UseArchiveContext = true
        });
        var restored = store.Load();
        Equal(@"C:\worker\whisper-cli.exe", restored.ExecutablePath);
        Equal("ggml-base.en", restored.ModelId);
        Equal("en", restored.DefaultLanguage);
        Equal(6, restored.Threads);
        True(!restored.UseGpu);
        True(restored.UseVoiceActivityDetection);
        True(restored.EnableMultiSpeakerDiarization);
        Equal(0.9, restored.DiarizationClusteringThreshold);
        Equal(@"C:\models\segmentation.onnx", restored.DiarizationSegmentationModelPath);
        Equal(@"C:\models\embedding.onnx", restored.DiarizationEmbeddingModelPath);
        True(restored.UseArchiveContext);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void TranscriptionRangesHaveStableDisplayText()
{
    Equal("Full broadcast", new TranscriptionJobOptions().RangeDisplay);
    Equal("10:00 from 42:00", new TranscriptionJobOptions(StartMs: 42 * 60_000, DurationMs: 10 * 60_000).RangeDisplay);
}

static void LongFormTranscriptionProtectsContinuityAndTimestamps()
{
    True(TranscriptionSafety.ShouldUseVoiceActivityDetection(true, 5 * 60_000));
    True(!TranscriptionSafety.ShouldUseVoiceActivityDetection(true, null));
    True(!TranscriptionSafety.ShouldUseVoiceActivityDetection(true, 31 * 60_000));
    True(!TranscriptionSafety.ShouldUseVoiceActivityDetection(false, 5 * 60_000));

    True(TranscriptionSafety.IsSpeakerCountPlausible(12, 9_608_974));
    True(!TranscriptionSafety.IsSpeakerCountPlausible(499, 9_608_974));

    var preparedRange = WhisperTimestampMapper.Map(0, 300_000, 600_000, workerInputOffsetMs: 0);
    Equal(600_000L, preparedRange.StartMs);
    Equal(900_000L, preparedRange.EndMs);
    var currentWorker = WhisperTimestampMapper.Map(600_000, 900_000, 600_000, workerInputOffsetMs: null);
    Equal(600_000L, currentWorker.StartMs);
    Equal(900_000L, currentWorker.EndMs);
    var olderWorker = WhisperTimestampMapper.Map(0, 60_000, 600_000, workerInputOffsetMs: null);
    Equal(600_000L, olderWorker.StartMs);
    Equal(660_000L, olderWorker.EndMs);
}

static void DedicatedServerFoundationIsUiIsolatedAndRevisionSafe()
{
    var controller = new HeadlessWebPlaybackController();
    var initial = controller.GetPlaybackState();
    Equal("Idle", initial.Status);
    Equal(0L, initial.Revision);

    var played = controller.ExecutePlaybackCommand(new WebPlaybackCommand(
        "play-episode", "client-a", EpisodeId: 42, PositionMs: 12_000, ExpectedRevision: 0,
        DeviceName: "Living room client"));
    True(played.Changed);
    True(!played.Conflict);
    Equal(42L, played.Player.EpisodeId!.Value);
    Equal(12_000L, played.Player.PositionMs);
    Equal(1L, played.Player.Revision);

    var stale = controller.ExecutePlaybackCommand(new WebPlaybackCommand(
        "seek", "client-b", PositionMs: 50_000, ExpectedRevision: 0));
    True(!stale.Changed);
    True(stale.Conflict);
    Equal(12_000L, stale.Player.PositionMs);

    var serverProject = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Server", "TheRadioVault.Server.csproj"));
    True(serverProject.Contains("RadioVault.Server", StringComparison.Ordinal));
    True(serverProject.Contains("TheRadioVault.Infrastructure", StringComparison.Ordinal));
    True(!serverProject.Contains("TheRadioVault.Presentation", StringComparison.Ordinal));
    var serverView = ReadServerAdministrationViews();
    True(serverView.Contains("Archive administration", StringComparison.Ordinal));
    True(serverView.Contains("ServerDashboardView", StringComparison.Ordinal));
    True(serverView.Contains("Your archive at a glance", StringComparison.Ordinal));
    True(!serverView.Contains("Now Playing", StringComparison.Ordinal));
    var webServer = ReadWebServerSourceBundle();
    True(webServer.Contains("Re-resolve once before returning a 404", StringComparison.Ordinal));
    True(webServer.Contains("Refresh the plan and give the decoder one new, cache-busted source", StringComparison.Ordinal));
    True(webServer.Contains("Canonical media {episodeId}/{mediaFileId}: 404 because", StringComparison.Ordinal));
    True(webServer.Contains("api + \"/broadcasts/\" + Number(id) + \"/media/\"", StringComparison.Ordinal));
    True(!webServer.Contains("audio.src = auth(\r\n            \"/broadcasts/\"", StringComparison.Ordinal));

    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var databasePath = Path.Combine(directory, "server.sqlite");
        var preferences = new WebServerPreferences
        {
            Enabled = false,
            StartAutomatically = false,
            SecureAccessEnabled = false,
            Port = 18765,
            SecurePort = 18766,
            LanFederationEnabled = false
        };
        using (var runtime = new RadioVaultServerRuntime(databasePath, preferences, honorAutomaticStart: false))
        {
            True(File.Exists(databasePath));
            Equal(Path.GetFullPath(databasePath), runtime.DatabasePath);
            True(!runtime.IsRunning);
        }
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }
}

static void DedicatedServerHealthPollingNeverBlocksSettingsUi()
{
    var viewModel = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Server", "ViewModels", "ServerSettingsViewModel.cs"));
    True(viewModel.Contains("HealthRefreshInterval = TimeSpan.FromMinutes(5)", StringComparison.Ordinal));
    True(viewModel.Contains("_healthRefreshGate.WaitAsync(0)", StringComparison.Ordinal));
    True(viewModel.Contains("Task.Run(runtime.GetHealthSnapshot", StringComparison.Ordinal));
    True(viewModel.Contains("Task.Run(() => new ServerDetailSnapshot", StringComparison.Ordinal));
    True(!viewModel.Contains("if (DateTimeOffset.UtcNow - _lastHealthRefresh >= TimeSpan.FromSeconds(5)) RefreshHealth();", StringComparison.Ordinal));
}

static void DedicatedServerAdministrationUsesFocusedScreens()
{
    var root = SourceRoot();
    var shell = File.ReadAllText(Path.Combine(root, "TheRadioVault.Server", "Views", "ServerSettingsWindow.axaml"));
    True(shell.Contains("TabStripPlacement=\"Left\"", StringComparison.Ordinal));
    True(shell.Contains("SelectedIndex=\"0\"", StringComparison.Ordinal));
    foreach (var section in new[]
             {
                 "ServerDashboardView", "ServerLibraryView", "ServerReconciliationView", "ServerAutomationView",
                 "ServerKnowledgeView", "ServerTranscriptionView", "ServerAccessView", "ServerRecoveryView"
             })
        True(shell.Contains(section, StringComparison.Ordinal));

    var dashboard = File.ReadAllText(Path.Combine(root, "TheRadioVault.Server", "Views", "ServerDashboardView.axaml"));
    True(dashboard.Contains("Your archive at a glance", StringComparison.Ordinal));
    True(dashboard.Contains("ArchiveReconciliationDashboardText", StringComparison.Ordinal));
    True(dashboard.Contains("DatabaseHealthText", StringComparison.Ordinal));

    var reconciliation = File.ReadAllText(Path.Combine(root, "TheRadioVault.Server", "Views", "ServerReconciliationView.axaml"));
    True(reconciliation.Contains("RunArchiveReconciliationCommand", StringComparison.Ordinal));
    True(reconciliation.Contains("ExportArchiveReconciliationReportCommand", StringComparison.Ordinal));
    True(reconciliation.Contains("ArchiveReconciliationBroadcastComparisonText", StringComparison.Ordinal));
    True(reconciliation.Contains("ArchiveReconciliationYearDifferences", StringComparison.Ordinal));
    True(reconciliation.Contains("ArchiveReconciliationSplitCandidates", StringComparison.Ordinal));
    True(reconciliation.Contains("files interpreted differently", StringComparison.OrdinalIgnoreCase) ||
         File.ReadAllText(Path.Combine(root, "TheRadioVault.Server", "ViewModels", "ServerSettingsViewModel.ArchiveReconciliation.cs"))
             .Contains("files interpreted differently", StringComparison.OrdinalIgnoreCase));
    True(reconciliation.Contains("PrepareMediaConsolidationCommand", StringComparison.Ordinal));
    True(reconciliation.Contains("does not rename, move, merge or delete media", StringComparison.Ordinal));
}

static void LoopbackNativeHandoffMapsServerOwnership()
{
    var now = new DateTimeOffset(2026, 8, 1, 20, 45, 0, TimeSpan.Zero);
    var idle = new WebPlaybackState(null, string.Empty, string.Empty, 0, 0, "Idle", null, false, now, "Radio Vault Server", 1d, 1, "server");
    var browserState = new WebPlaybackState(
        3662, "Ron & Fez", "The Penultimate Ron and Fez Show",
        4_343_904, 9_608_974, "In Progress", now.UtcDateTime,
        true, now, "Firefox", 1d, 22, "browser-client");
    var browserDevice = new WebPlaybackDevice(
        "browser-client", "Firefox", "Browser", browserState, now, true, true);
    var serverDevice = new WebPlaybackDevice(
        "server", "Radio Vault Server", "Server", idle, now, true, false);
    var receipt = new WebPlaybackCommittedTransfer(
        Guid.NewGuid(), "native-client", "Native Radio Vault", "browser-client", "Firefox",
        7, true, false, now, null);
    var browserSession = new WebPlaybackSession(
        browserState, idle, browserState, "Firefox", "browser-client", 7)
    {
        Devices = new[] { serverDevice, browserDevice },
        CommittedTransfer = receipt
    };

    var observedGeneration = 0L;
    var browserSnapshot = LoopbackPlaybackHandoffService.MapSnapshot(
        browserSession, "native-client", "Native Radio Vault", now,
        generation => observedGeneration = generation);
    Equal(7L, observedGeneration);
    True(browserSnapshot.IsPlayingElsewhere);
    True(!browserSnapshot.IsOwnedByCurrentDevice);
    Equal(3662L, browserSnapshot.ActivePlayback!.RepresentativeEpisodeId!.Value);
    Equal(4_343_904L, browserSnapshot.ActivePlayback.PositionMs);
    Equal("browser-client", browserSnapshot.OwnerDeviceId);
    True(browserSnapshot.CommittedTransfer is not null);
    True(!browserSnapshot.CommittedTransfer!.SourceStopAcknowledged);

    var nativeState = browserState with
    {
        Device = "Native Radio Vault",
        ControllerClientId = "native-client",
        Revision = 23
    };
    var nativeDevice = new WebPlaybackDevice(
        "native-client", "Native Radio Vault", "DesktopClient", nativeState, now, true, true);
    var nativeSession = browserSession with
    {
        Player = nativeState,
        Phone = browserState,
        OwnerDevice = "Native Radio Vault",
        OwnerClientId = "native-client",
        Generation = 8,
        Devices = new[] { serverDevice, browserDevice with { IsOwner = false }, nativeDevice }
    };
    var nativeSnapshot = LoopbackPlaybackHandoffService.MapSnapshot(
        nativeSession, "native-client", "Native Radio Vault", now);
    True(nativeSnapshot.IsOwnedByCurrentDevice);
    True(!nativeSnapshot.IsPlayingElsewhere);
    Equal(8L, nativeSnapshot.Generation);
}

static void DedicatedServerOwnsTranscriptionWorkers()
{
    var host = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Composition", "AvaloniaApplicationHost.cs"));
    True(host.Contains("LoopbackTranscriptionCoordinator", StringComparison.Ordinal));
    True(host.Contains("LoopbackTranscriptionBatchCoordinator", StringComparison.Ordinal));
    True(host.Contains("LoopbackVoiceLearningCoordinator", StringComparison.Ordinal));
    True(!host.Contains("RegisterSingleton<TranscriptionCoordinator>", StringComparison.Ordinal));

    var serverView = ReadServerAdministrationViews();
    True(serverView.Contains("TRANSCRIPTION SERVICE", StringComparison.Ordinal));
    True(serverView.Contains("Install recommended transcription setup", StringComparison.Ordinal));
}

static void TranscriptionJobsPreserveWorkerOptions()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "jobs.sqlite");
    try
    {
        var database = new SqliteDatabase(path);
        database.Initialize();
        long episodeId;
        using (var connection = database.OpenConnection())
        {
            using var collection = connection.CreateCommand();
            collection.CommandText = "INSERT INTO collections(name,sort_name) VALUES('Transcription jobs','Transcription jobs'); SELECT last_insert_rowid();";
            var collectionId = Convert.ToInt64(collection.ExecuteScalar());
            using var episode = connection.CreateCommand();
            episode.CommandText = "INSERT INTO episodes(collection_id,air_date,title,status,date_added,updated_at) VALUES($collection,'2026-07-18','Worker options','Unplayed',$now,$now); SELECT last_insert_rowid();";
            episode.Parameters.AddWithValue("$collection", collectionId);
            episode.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            episodeId = Convert.ToInt64(episode.ExecuteScalar());
        }

        var repository = new SqliteTranscriptRepository(database);
        var jobId = Guid.NewGuid();
        repository.CreateJobAsync(new TranscriptionJobRecord
        {
            JobId = jobId,
            EpisodeId = episodeId,
            State = TranscriptionJobState.Queued,
            EngineId = "whisper.cpp",
            ModelId = "ggml-small.en-tdrz",
            ProgressPercent = 0,
            Message = "Queued",
            RequestedAt = DateTimeOffset.UtcNow,
            Language = "en",
            StartMs = 120_000,
            DurationMs = 600_000,
            EnableSpeakerDiarization = true,
            UseVoiceActivityDetection = true,
            ReplaceExistingTranscript = true,
            IsPaused = true
        }).GetAwaiter().GetResult();

        var restored = repository.GetJobAsync(jobId).GetAwaiter().GetResult() ?? throw new InvalidOperationException("Job did not round-trip.");
        Equal("en", restored.Language);
        Equal(120_000L, restored.StartMs);
        Equal(600_000L, restored.DurationMs!.Value);
        True(restored.EnableSpeakerDiarization);
        True(restored.UseVoiceActivityDetection);
        True(restored.ReplaceExistingTranscript);
        True(restored.IsPaused);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void ServerTranscriptsUsePortableSourceVocabulary()
{
    var coordinator = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Transcription", "Services", "TranscriptionCoordinator.cs"));
    True(coordinator.Contains("Source = \"local\"", StringComparison.Ordinal));
    True(!coordinator.Contains("Source = \"server\"", StringComparison.Ordinal));

    var repository = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Transcription", "Services", "SqliteTranscriptRepository.cs"));
    True(repository.Contains("NormalizeTranscriptSource(document.Source)", StringComparison.Ordinal));
    True(repository.Contains("normalized is \"local\" or \"import\" or \"manual\" or \"shared\"", StringComparison.Ordinal));
}

static void AbandonedTranscriptionJobsBecomeRetryable()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "interrupted.sqlite");
    try
    {
        var database = new SqliteDatabase(path);
        database.Initialize();
        long episodeId;
        using (var connection = database.OpenConnection())
        {
            using var collection = connection.CreateCommand();
            collection.CommandText = "INSERT INTO collections(name,sort_name) VALUES('Interrupted jobs','Interrupted jobs'); SELECT last_insert_rowid();";
            var collectionId = Convert.ToInt64(collection.ExecuteScalar());
            using var episode = connection.CreateCommand();
            episode.CommandText = "INSERT INTO episodes(collection_id,title,status,date_added,updated_at) VALUES($collection,'Interrupted job','Unplayed',$now,$now); SELECT last_insert_rowid();";
            episode.Parameters.AddWithValue("$collection", collectionId);
            episode.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            episodeId = Convert.ToInt64(episode.ExecuteScalar());
        }

        var firstRepository = new SqliteTranscriptRepository(database);
        var jobId = Guid.NewGuid();
        firstRepository.CreateJobAsync(new TranscriptionJobRecord
        {
            JobId = jobId,
            EpisodeId = episodeId,
            State = TranscriptionJobState.Running,
            EngineId = "whisper.cpp",
            ModelId = "ggml-base.en",
            Message = "Transcribing locally",
            RequestedAt = DateTimeOffset.UtcNow,
            StartedAt = DateTimeOffset.UtcNow
        }).GetAwaiter().GetResult();

        var reopenedRepository = new SqliteTranscriptRepository(database);
        var restored = reopenedRepository.GetJobAsync(jobId).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("Interrupted job was lost.");
        Equal(TranscriptionJobState.Interrupted, restored.State);
        True(restored.CanRetry);
        True(restored.Message.Contains("Interrupted", StringComparison.Ordinal));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void AvaloniaExposesLocalTranscriptionWorkflow()
{
    var host = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Composition", "AvaloniaApplicationHost.cs"));
    True(host.Contains("ITranscriptionCoordinator", StringComparison.Ordinal));
    True(!host.Contains("WhisperCppTranscriptionEngine", StringComparison.Ordinal));
    True(host.Contains("LoopbackTranscriptionCoordinator", StringComparison.Ordinal));
    True(host.Contains("LoopbackTranscriptionBatchCoordinator", StringComparison.Ordinal));
    True(host.Contains("IServerTranscriptionAdministrationService", StringComparison.Ordinal));
    True(!host.Contains("RegisterSingleton<TranscriptionCoordinator>", StringComparison.Ordinal));
    True(!host.Contains("SherpaOnnxVoiceEmbeddingEngine", StringComparison.Ordinal));
    True(host.Contains("IVoiceLearningCoordinator", StringComparison.Ordinal));
    True(host.Contains("ITranscriptionBatchCoordinator", StringComparison.Ordinal));
    True(host.Contains("TranscriptsViewModel", StringComparison.Ordinal));

    var mainWindow = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "MainWindowViewModel.cs"));
    True(mainWindow.Contains("\"transcripts\"", StringComparison.Ordinal));
    True(mainWindow.Contains("Transcripts.LoadAsync", StringComparison.Ordinal));

    var view = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "TranscriptsView.axaml"));
    True(view.Contains("CurrentTranscriptionActionText", StringComparison.Ordinal));
    True(view.Contains("Five-minute sample", StringComparison.Ordinal));
    True(view.Contains("Play selected", StringComparison.Ordinal));
    True(view.Contains("Save wording", StringComparison.Ordinal));
    True(view.Contains("Confirm voice", StringComparison.Ordinal));
    True(view.Contains("Split phrase", StringComparison.Ordinal));
    True(view.Contains("Merge next", StringComparison.Ordinal));
    True(view.Contains("ExportTextCommand", StringComparison.Ordinal));
    True(view.Contains("ExportSrtCommand", StringComparison.Ordinal));
    True(view.Contains("ExportVttCommand", StringComparison.Ordinal));
    True(view.Contains("Start batch", StringComparison.Ordinal));
    True(view.Contains("Pause batch", StringComparison.Ordinal));
    True(view.Contains("Resume batch", StringComparison.Ordinal));
    True(view.Contains("Retry failed", StringComparison.Ordinal));
    True(!view.Contains("RvTranscriptBrush", StringComparison.Ordinal));
    True(view.Contains("Transcript review", StringComparison.Ordinal));
    True(view.Contains("Transcription activity", StringComparison.Ordinal));
    True(view.Contains("Button.transcript-action", StringComparison.Ordinal));
    True(!view.Contains("compact-action", StringComparison.Ordinal), "Transcription text actions must not use the fixed-width icon-button style.");

    var broadcastInfo = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "FullBroadcastInfoView.axaml"));
    True(broadcastInfo.Contains("Read transcript", StringComparison.Ordinal));
    True(broadcastInfo.Contains("Start transcription", StringComparison.Ordinal));
    var nowPlaying = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "NowPlayingView.axaml"));
    True(nowPlaying.Contains("OpenTranscriptCommand", StringComparison.Ordinal));
    True(nowPlaying.Contains("StartTranscriptionCommand", StringComparison.Ordinal));
    var search = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "SearchView.axaml"));
    True(search.Contains("Narrow the results", StringComparison.Ordinal));
    True(search.Contains("ScopeFilters", StringComparison.Ordinal));
    True(search.Contains("Suggestions", StringComparison.Ordinal));

    var splash = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "StartupWindow.axaml"));
    True(splash.Contains("RvShellBrush", StringComparison.Ordinal));
    True(splash.Contains("STARTING RADIO VAULT", StringComparison.Ordinal));
    True(splash.Contains("Width=\"132\" Height=\"132\"", StringComparison.Ordinal));
    True(!splash.Contains("RvTranscriptBrush", StringComparison.Ordinal));

    var audioPreparer = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Transcription", "NAudioTranscriptionAudioPreparer.cs"));
    True(audioPreparer.Contains(".m4a", StringComparison.OrdinalIgnoreCase));
    True(audioPreparer.Contains("prepared-audio.wav", StringComparison.Ordinal));

    var settingsView = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "DesktopToolsView.axaml"));
    True(settingsView.Contains("Server transcription", StringComparison.Ordinal));
    True(settingsView.Contains("Install on server", StringComparison.Ordinal));
    True(settingsView.Contains("Paths on the active server computer", StringComparison.OrdinalIgnoreCase));
    True(!settingsView.Contains("Worker only", StringComparison.Ordinal));
}

static void TranscriptRepositoryRoundTripsTimedSegments()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "transcript.sqlite");
    try
    {
        var database = new SqliteDatabase(path);
        database.Initialize();
        long episodeId;
        using (var connection = database.OpenConnection())
        {
            using var collection = connection.CreateCommand();
            collection.CommandText = "SELECT id FROM collections WHERE name='Bennington'";
            var collectionId = Convert.ToInt64(collection.ExecuteScalar());
            using var episode = connection.CreateCommand();
            episode.CommandText = """
                INSERT INTO episodes(collection_id,air_date,title,status,date_added,updated_at)
                VALUES($collection,'2026-07-18','Transcript test','Unplayed',$now,$now);
                SELECT last_insert_rowid();
                """;
            episode.Parameters.AddWithValue("$collection", collectionId);
            episode.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            episodeId = Convert.ToInt64(episode.ExecuteScalar());
        }

        var repository = new SqliteTranscriptRepository(database);
        var saved = repository.SaveAsync(new TranscriptDocument
        {
            EpisodeId = episodeId,
            Language = "en",
            EngineId = "test-engine",
            EngineVersion = "1.0",
            ModelId = "tiny-test",
            Source = "server",
            FullText = "Hello archive. Second segment.",
            DurationMs = 2000,
            Segments = new[]
            {
                new TranscriptSegment(
                    0,
                    0,
                    900,
                    "Hello archive.",
                    Words: new[]
                    {
                        new TranscriptWord(0, 400, "Hello"),
                        new TranscriptWord(450, 900, "archive.")
                    }),
                new TranscriptSegment(1, 1000, 2000, "Second segment.")
            }
        }).GetAwaiter().GetResult();
        Equal(1, saved.Revision);
        Equal("local", saved.Source);
        Equal(2, saved.Segments.Count);

        var loaded = repository.GetForEpisodeAsync(episodeId).GetAwaiter().GetResult();
        True(loaded is not null);
        Equal("test-engine", loaded!.EngineId);
        Equal("local", loaded.Source);
        Equal(1000L, loaded.Segments[1].StartMs);
        Equal(2, loaded.Segments[0].Words?.Count ?? 0);
        Equal("archive.", loaded.Segments[0].Words?[1].Text ?? "");
        Equal(4, loaded.WordCount);

        var revised = repository.SaveAsync(new TranscriptDocument
        {
            EpisodeId = episodeId,
            Language = "en",
            EngineId = "test-engine",
            FullText = loaded.FullText,
            DurationMs = loaded.DurationMs,
            Segments = loaded.Segments
        }).GetAwaiter().GetResult();
        Equal(2, revised.Revision);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void TranscriptPackagesRejectOverlappingSegments()
{
    var package = new TranscriptPackage
    {
        Transcript = new TranscriptDocument
        {
            EpisodeId = 1,
            FullText = "One two",
            Segments = new[]
            {
                new TranscriptSegment(0, 0, 1000, "One"),
                new TranscriptSegment(1, 900, 1500, "Two")
            }
        }
    };
    var rejected = false;
    try
    {
        TranscriptExchangeService.ValidatePackage(package);
    }
    catch (InvalidDataException)
    {
        rejected = true;
    }
    True(rejected);
}

static void TranscriptExchangeProtectsBroadcastIdentity()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "exchange.sqlite");
    var packagePath = Path.Combine(directory, "episode.trvtranscript");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        long firstEpisodeId;
        long secondEpisodeId;
        using (var connection = database.OpenConnection())
        {
            using var collection = connection.CreateCommand();
            collection.CommandText = "SELECT id FROM collections WHERE name='Bennington'";
            var collectionId = Convert.ToInt64(collection.ExecuteScalar());

            using var first = connection.CreateCommand();
            first.CommandText = """
                INSERT INTO episodes(collection_id,air_date,title,status,date_added,updated_at)
                VALUES($collection,'2026-07-17','First transcript broadcast','Unplayed',$now,$now);
                SELECT last_insert_rowid();
                """;
            first.Parameters.AddWithValue("$collection", collectionId);
            first.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            firstEpisodeId = Convert.ToInt64(first.ExecuteScalar());

            using var second = connection.CreateCommand();
            second.CommandText = """
                INSERT INTO episodes(collection_id,air_date,title,status,date_added,updated_at)
                VALUES($collection,'2026-07-18','Different transcript broadcast','Unplayed',$now,$now);
                SELECT last_insert_rowid();
                """;
            second.Parameters.AddWithValue("$collection", collectionId);
            second.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            secondEpisodeId = Convert.ToInt64(second.ExecuteScalar());
        }

        var repository = new SqliteTranscriptRepository(database);
        repository.SaveAsync(new TranscriptDocument
        {
            EpisodeId = firstEpisodeId,
            Language = "en",
            EngineId = "test-engine",
            FullText = "A portable transcript.",
            DurationMs = 1000,
            Segments = new[] { new TranscriptSegment(0, 0, 1000, "A portable transcript.") }
        }).GetAwaiter().GetResult();
        var exchange = new TranscriptExchangeService(repository);
        exchange.ExportAsync(firstEpisodeId, packagePath).GetAwaiter().GetResult();

        var mismatchRejected = false;
        try
        {
            exchange.ImportAsync(secondEpisodeId, packagePath, replaceExisting: false).GetAwaiter().GetResult();
        }
        catch (InvalidDataException)
        {
            mismatchRejected = true;
        }
        True(mismatchRejected);

        var firstImport = exchange.ImportAsync(firstEpisodeId, packagePath, replaceExisting: true).GetAwaiter().GetResult();
        var secondImport = exchange.ImportAsync(firstEpisodeId, packagePath, replaceExisting: true).GetAwaiter().GetResult();
        Equal(2, firstImport.Revision);
        Equal(3, secondImport.Revision);

        using var verify = database.OpenConnection();
        using var imports = verify.CreateCommand();
        imports.CommandText = "SELECT COUNT(*) FROM transcript_imports WHERE episode_id=$episode";
        imports.Parameters.AddWithValue("$episode", firstEpisodeId);
        Equal(1L, Convert.ToInt64(imports.ExecuteScalar()));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void SpeakerConfirmationsAccumulateVoiceEvidence()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "voices.sqlite");
    try
    {
        var database = new SqliteDatabase(path);
        database.Initialize();
        long firstEpisodeId;
        long secondEpisodeId;
        long thirdEpisodeId;
        using (var connection = database.OpenConnection())
        {
            using var collection = connection.CreateCommand();
            collection.CommandText = "SELECT id FROM collections WHERE name='Bennington'";
            var collectionId = Convert.ToInt64(collection.ExecuteScalar());
            firstEpisodeId = InsertTranscriptTestEpisode(connection, collectionId, "2026-07-16", "Voice one", "Ron Bennington|Gail Bennington");
            secondEpisodeId = InsertTranscriptTestEpisode(connection, collectionId, "2026-07-17", "Voice two", "Ron Bennington|Gail Bennington");
            thirdEpisodeId = InsertTranscriptTestEpisode(connection, collectionId, "2026-07-18", "Voice three", "Ron Bennington|Gail Bennington");
        }

        var transcripts = new SqliteTranscriptRepository(database);
        foreach (var episodeId in new[] { firstEpisodeId, secondEpisodeId, thirdEpisodeId })
        {
            transcripts.SaveAsync(new TranscriptDocument
            {
                EpisodeId = episodeId,
                Language = "en",
                EngineId = "diarization-test",
                HasSpeakerDiarization = true,
                FullText = "First long sample. Second long sample.",
                DurationMs = 12_000,
                Segments = new[]
                {
                    new TranscriptSegment(0, 0, 4_000, "First long sample.", "SPEAKER_00", SpeakerKey: "speaker-00"),
                    new TranscriptSegment(1, 5_000, 10_000, "Second long sample.", "SPEAKER_00", SpeakerKey: "speaker-00")
                }
            }).GetAwaiter().GetResult();
        }

        var speakers = new SqliteSpeakerIdentityRepository(database);
        var firstCluster = speakers.GetClustersForEpisodeAsync(firstEpisodeId).GetAwaiter().GetResult().Single();
        var first = speakers.AssignClusterAsync(firstEpisodeId, firstCluster.SpeakerKey, "Ron Bennington", true).GetAwaiter().GetResult();
        Equal(2, first.PendingSamplesCreated);

        var secondCluster = speakers.GetClustersForEpisodeAsync(secondEpisodeId).GetAwaiter().GetResult().Single();
        var second = speakers.AssignClusterAsync(secondEpisodeId, secondCluster.SpeakerKey, "Ron Bennington", true).GetAwaiter().GetResult();
        Equal(2, second.PendingSamplesCreated);
        Equal(2, second.Profile.BroadcastCount);
        Equal(4, second.Profile.PendingSampleCount);
        Equal(2, second.Profile.ConfirmedClusterCount);

        var pending = speakers.GetPendingVoiceSamplesAsync().GetAwaiter().GetResult();
        Equal(4, pending.Count);
        foreach (var sample in pending)
        {
            speakers.SaveVoiceEmbeddingAsync(
                sample.Id,
                new VoiceEmbeddingResult("voice-test", "1", new[] { 1d, 0d }, 0.95),
                CancellationToken.None).GetAwaiter().GetResult();
        }
        var learned = speakers.GetVoiceProfileAsync(second.Profile.VoicePersonId).GetAwaiter().GetResult();
        True(learned is not null);
        Equal(4, learned!.ReadySampleCount);
        Equal(0, learned.PendingSampleCount);
        Equal("voice-test", learned.EmbeddingModelId);

        var thirdCluster = speakers.GetClustersForEpisodeAsync(thirdEpisodeId).GetAwaiter().GetResult().Single();
        var suggestions = speakers.MatchClusterAsync(
            thirdEpisodeId,
            thirdCluster.SpeakerKey,
            new VoiceEmbeddingResult("voice-test", "1", new[] { 0.999d, 0.001d }, 0.93)).GetAwaiter().GetResult();
        Equal(1, suggestions.Count);
        Equal("Ron Bennington", suggestions[0].PersonName);
        True(suggestions[0].Confidence > 0.99);
        var suggestedCluster = speakers.GetClustersForEpisodeAsync(thirdEpisodeId).GetAwaiter().GetResult().Single();
        Equal(SpeakerAssignmentState.Suggested, suggestedCluster.AssignmentState);
        Equal("Ron Bennington", suggestedCluster.PersonName);

        var corrected = speakers.AssignClusterAsync(secondEpisodeId, secondCluster.SpeakerKey, "Gail Bennington", true).GetAwaiter().GetResult();
        Equal(2, corrected.PendingSamplesCreated);
        var ronAfterCorrection = speakers.GetVoiceProfileAsync(second.Profile.VoicePersonId).GetAwaiter().GetResult();
        True(ronAfterCorrection is not null);
        Equal(1, ronAfterCorrection!.BroadcastCount);
        Equal(2, ronAfterCorrection.ReadySampleCount);
        Equal(0, ronAfterCorrection.PendingSampleCount);
        Equal(1, corrected.Profile.BroadcastCount);
        Equal(2, corrected.Profile.PendingSampleCount);

        speakers.ClearAssignmentAsync(secondEpisodeId, secondCluster.SpeakerKey).GetAwaiter().GetResult();
        var profile = speakers.GetVoiceProfileAsync(second.Profile.VoicePersonId).GetAwaiter().GetResult();
        True(profile is not null);
        Equal(1, profile!.BroadcastCount);
        Equal(2, profile.ReadySampleCount);
        Equal(0, profile.PendingSampleCount);
        var correctedProfile = speakers.GetVoiceProfileAsync(corrected.Profile.VoicePersonId).GetAwaiter().GetResult();
        True(correctedProfile is not null);
        Equal(0, correctedProfile!.BroadcastCount);
        Equal(0, correctedProfile.ReadySampleCount);
        Equal(0, correctedProfile.PendingSampleCount);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void TranscriptPackagesPreserveSpeakerAssignments()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "speaker-package.sqlite");
    var packagePath = Path.Combine(directory, "speaker.trvtranscript");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        long episodeId;
        using (var connection = database.OpenConnection())
        {
            using var collection = connection.CreateCommand();
            collection.CommandText = "SELECT id FROM collections WHERE name='Bennington'";
            var collectionId = Convert.ToInt64(collection.ExecuteScalar());
            episodeId = InsertTranscriptTestEpisode(connection, collectionId, "2026-07-18", "Speaker package", "Ron Bennington");
        }

        var repository = new SqliteTranscriptRepository(database);
        repository.SaveAsync(new TranscriptDocument
        {
            EpisodeId = episodeId,
            Language = "en",
            HasSpeakerDiarization = true,
            FullText = "A known speaker.",
            DurationMs = 4_000,
            Segments = new[] { new TranscriptSegment(0, 0, 4_000, "A known speaker.", "SPEAKER_00", SpeakerKey: "speaker-00") }
        }).GetAwaiter().GetResult();
        var speakerRepository = new SqliteSpeakerIdentityRepository(database);
        speakerRepository.AssignClusterAsync(episodeId, "speaker-00", "Ron Bennington", true).GetAwaiter().GetResult();

        var exchange = new TranscriptExchangeService(repository);
        exchange.ExportAsync(episodeId, packagePath).GetAwaiter().GetResult();
        exchange.ImportAsync(episodeId, packagePath, replaceExisting: true).GetAwaiter().GetResult();
        var imported = repository.GetForEpisodeAsync(episodeId).GetAwaiter().GetResult();
        True(imported is not null);
        Equal(1, imported!.Speakers.Count);
        Equal("Ron Bennington", imported.Speakers[0].PersonName);
        Equal(SpeakerAssignmentState.Confirmed, imported.Speakers[0].AssignmentState);
        Equal("Ron Bennington", imported.Segments[0].DisplaySpeaker);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void TranscriptReviewEditsAndSubtitleExportsAreStable()
{
    var service = new TranscriptReviewService();
    var document = new TranscriptDocument
    {
        EpisodeId = 42,
        DurationMs = 8_000,
        HasSpeakerDiarization = true,
        Segments = new[]
        {
            new TranscriptSegment(0, 0, 4_000, "the uncorrected opening words", "Speaker 1", SpeakerKey: "speaker-1", AssignedPersonName: "Ron Bennington", AssignmentState: SpeakerAssignmentState.Confirmed),
            new TranscriptSegment(1, 4_000, 8_000, "and the closing phrase", "Speaker 1", SpeakerKey: "speaker-1", AssignedPersonName: "Ron Bennington", AssignmentState: SpeakerAssignmentState.Confirmed)
        }
    };

    var corrected = service.UpdatePhrase(document, 0, "The corrected opening words");
    True(corrected.Segments[0].IsReviewed);
    Equal("The corrected opening words", corrected.Segments[0].Text);
    var split = service.SplitPhrase(corrected, 0);
    Equal(3, split.Segments.Count);
    Equal(2_000L, split.Segments[0].EndMs);
    Equal(2_000L, split.Segments[1].StartMs);
    var merged = service.MergeWithNext(split, 0);
    Equal(2, merged.Segments.Count);
    Equal("The corrected opening words", merged.Segments[0].Text);

    var summary = new TranscriptSummary { Show = "Bennington", EpisodeTitle = "Review test", AirDate = new DateTime(2026, 7, 18) };
    var text = service.ExportPlainText(merged, summary);
    True(text.Contains("[0:00] Ron Bennington: The corrected opening words", StringComparison.Ordinal));
    var srt = service.ExportSrt(merged);
    True(srt.Contains("00:00:00,000 --> 00:00:04,000", StringComparison.Ordinal));
    True(srt.Contains("Ron Bennington: The corrected opening words", StringComparison.Ordinal));
    var vtt = service.ExportVtt(merged);
    True(vtt.StartsWith("WEBVTT\n\n", StringComparison.Ordinal));
    True(vtt.Contains("00:00:04.000 --> 00:00:08.000", StringComparison.Ordinal));
}

static void BatchTranscriptionPersistsSkippingPriorityAndRecovery()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var database = new SqliteDatabase(Path.Combine(directory, "batch.sqlite"));
        database.Initialize();
        long firstEpisode;
        long secondEpisode;
        long thirdEpisode;
        using (var connection = database.OpenConnection())
        {
            using var collection = connection.CreateCommand();
            collection.CommandText = "SELECT id FROM collections WHERE name='Bennington'";
            var collectionId = Convert.ToInt64(collection.ExecuteScalar());
            firstEpisode = InsertTranscriptTestEpisode(connection, collectionId, "2026-07-16", "Already complete", "");
            secondEpisode = InsertTranscriptTestEpisode(connection, collectionId, "2026-07-17", "Run second", "");
            thirdEpisode = InsertTranscriptTestEpisode(connection, collectionId, "2026-07-18", "Run first", "");
        }
        new SqliteTranscriptRepository(database).SaveAsync(new TranscriptDocument
        {
            EpisodeId = firstEpisode,
            FullText = "Existing transcript",
            Segments = new[] { new TranscriptSegment(0, 0, 1_000, "Existing transcript") }
        }).GetAwaiter().GetResult();

        var repository = new SqliteTranscriptionBatchRepository(database);
        var created = repository.CreateAsync(new TranscriptionBatchCreateRequest(
            "Bennington · 2026",
            "Bennington · 2026",
            new TranscriptionJobOptions("en", "large", EnableSpeakerDiarization: true, UseVoiceActivityDetection: true),
            new[]
            {
                new TranscriptionBatchCandidate(firstEpisode, "Bennington", new DateOnly(2026, 7, 16), "Already complete", 1_000, true),
                new TranscriptionBatchCandidate(secondEpisode, "Bennington", new DateOnly(2026, 7, 17), "Run second", 2_000, false),
                new TranscriptionBatchCandidate(thirdEpisode, "Bennington", new DateOnly(2026, 7, 18), "Run first", 3_000, false)
            })).GetAwaiter().GetResult();
        Equal(3, created.TotalCount);
        Equal(2, created.PendingCount);
        Equal(1, created.SkippedCount);
        True(created.EnableSpeakerDiarization);
        True(created.UseVoiceActivityDetection);

        var items = repository.GetItemsAsync(created.BatchId).GetAwaiter().GetResult();
        var third = items.Single(x => x.EpisodeId == thirdEpisode);
        True(repository.MoveItemAsync(created.BatchId, third.Id, -1).GetAwaiter().GetResult());
        items = repository.GetItemsAsync(created.BatchId).GetAwaiter().GetResult();
        True(items.Single(x => x.EpisodeId == thirdEpisode).Position < items.Single(x => x.EpisodeId == secondEpisode).Position);

        var active = items.Single(x => x.EpisodeId == thirdEpisode);
        repository.SetBatchStateAsync(created.BatchId, TranscriptionBatchState.Running).GetAwaiter().GetResult();
        repository.SetItemStateAsync(active.Id, TranscriptionBatchItemState.Running).GetAwaiter().GetResult();
        var reopened = new SqliteTranscriptionBatchRepository(database);
        var recovered = reopened.GetAsync(created.BatchId).GetAwaiter().GetResult();
        True(recovered is not null);
        Equal(TranscriptionBatchState.Interrupted, recovered!.State);
        Equal(2, recovered.PendingCount);

        reopened.SetItemStateAsync(active.Id, TranscriptionBatchItemState.Failed, error: "test failure").GetAwaiter().GetResult();
        reopened.SetBatchStateAsync(created.BatchId, TranscriptionBatchState.CompletedWithErrors).GetAwaiter().GetResult();
        var failed = reopened.GetAsync(created.BatchId).GetAwaiter().GetResult();
        Equal(1, failed!.FailedCount);
        reopened.ResetFailedItemsAsync(created.BatchId).GetAwaiter().GetResult();
        var retried = reopened.GetAsync(created.BatchId).GetAwaiter().GetResult();
        Equal(0, retried!.FailedCount);
        Equal(2, retried.PendingCount);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}



static void TranscriptQualityCollapsesMusicRuns()
{
    var processed = TranscriptQualityProcessor.Process(new[]
    {
        new TranscriptSegment(0, 0, 5_000, "♪ bad lyric ♪", Confidence: 0.7),
        new TranscriptSegment(1, 5_500, 10_000, "(upbeat music)", Confidence: 0.8),
        new TranscriptSegment(2, 12_000, 15_000, "Back to the show.", Confidence: 0.95)
    });
    Equal(2, processed.Count);
    Equal(TranscriptContentKind.Music, processed[0].ContentKind);
    Equal("[Music]", processed[0].Text);
    Equal(TranscriptContentKind.Speech, processed[1].ContentKind);

    var pathological = TranscriptQualityProcessor.Process(new[]
    {
        new TranscriptSegment(0, 6_346_870, 6_346_970,
            string.Join(' ', Enumerable.Repeat("big cat", 22)))
    });
    Equal(1, pathological.Count);
    Equal(TranscriptContentKind.Unknown, pathological[0].ContentKind);
    Equal("[Unclear audio]", pathological[0].Text);
}

static void PortableMetadataRemovesPrivatePaths()
{
    var cleaned = TranscriptMetadataSanitizer.CreatePortableMetadata("{\"worker\":\"whisper.cpp\",\"executable\":\"C:\\\\Users\\\\name\\\\whisper.exe\",\"logTail\":[\"C:\\\\private\"],\"backend\":\"CPU\"}");
    True(cleaned.Contains("whisper.cpp", StringComparison.Ordinal));
    True(cleaned.Contains("CPU", StringComparison.Ordinal));
    True(!cleaned.Contains("Users", StringComparison.OrdinalIgnoreCase));
    True(!cleaned.Contains("logTail", StringComparison.OrdinalIgnoreCase));
}

static void TranscriptV3PackagesAreCompressed()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var database = new SqliteDatabase(Path.Combine(directory, "compressed.sqlite"));
        database.Initialize();
        long episodeId;
        using (var connection = database.OpenConnection())
        {
            using var collection = connection.CreateCommand();
            collection.CommandText = "SELECT id FROM collections WHERE name='Bennington'";
            var collectionId = Convert.ToInt64(collection.ExecuteScalar());
            episodeId = InsertTranscriptTestEpisode(connection, collectionId, "2026-07-02", "Compressed package", "Ron Bennington");
        }
        var repository = new SqliteTranscriptRepository(database);
        repository.SaveAsync(new TranscriptDocument
        {
            EpisodeId = episodeId,
            FullText = string.Join(' ', Enumerable.Repeat("archive", 5000)),
            DurationMs = 10_000,
            Segments = new[] { new TranscriptSegment(0, 0, 10_000, string.Join(' ', Enumerable.Repeat("archive", 5000))) }
        }).GetAwaiter().GetResult();
        var path = Path.Combine(directory, "test.trvtranscript");
        new TranscriptExchangeService(repository).ExportAsync(episodeId, path).GetAwaiter().GetResult();
        var bytes = File.ReadAllBytes(path);
        Equal((byte)'P', bytes[0]);
        Equal((byte)'K', bytes[1]);
        True(new FileInfo(path).Length < 20_000);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static long InsertTranscriptTestEpisode(Microsoft.Data.Sqlite.SqliteConnection connection, long collectionId, string airDate, string title, string hosts)
{
    using var episode = connection.CreateCommand();
    episode.CommandText = """
        INSERT INTO episodes(collection_id,air_date,title,status,date_added,updated_at,hosts)
        VALUES($collection,$date,$title,'Unplayed',$now,$now,$hosts);
        SELECT last_insert_rowid();
        """;
    episode.Parameters.AddWithValue("$collection", collectionId);
    episode.Parameters.AddWithValue("$date", airDate);
    episode.Parameters.AddWithValue("$title", title);
    episode.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
    episode.Parameters.AddWithValue("$hosts", hosts);
    return Convert.ToInt64(episode.ExecuteScalar());
}

static void MomentsServiceRepairsCanonicalDuplicates()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "moments-buildfix2.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        long survivorEpisodeId;
        long memberEpisodeId;
        using (var connection = database.OpenConnection())
        {
            using var setup = connection.CreateCommand();
            setup.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Bennington','Bennington');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Bennington'),'2026-06-22','High','Survivor','Unplayed',$now,$now,'BENNINGTON-A');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Bennington'),'2026-06-22','High','Retained member','Unplayed',$now,$now,'BENNINGTON-B');
                INSERT INTO canonical_broadcasts(canonical_key,collection_name,air_date,broadcast_slot,source_truth_run_id,adopted_at)
                VALUES('BENNINGTON|2026-06-22|STANDARD','Bennington','2026-06-22','Standard',1,$now);
                INSERT INTO episode_canonical_map(episode_id,canonical_key,survivor_episode_id,is_survivor,source_truth_run_id,adopted_at)
                SELECT id,'BENNINGTON|2026-06-22|STANDARD',(SELECT MIN(id) FROM episodes),CASE WHEN id=(SELECT MIN(id) FROM episodes) THEN 1 ELSE 0 END,1,$now
                  FROM episodes;
                INSERT INTO moments(episode_id,position_ms,title,notes,created_at)
                VALUES((SELECT MIN(id) FROM episodes),4498945,'The old slave hanging tree','',$early);
                INSERT INTO moments(episode_id,position_ms,title,notes,created_at)
                VALUES((SELECT MAX(id) FROM episodes),4498000,'The old slave hanging tree','',$late);
                INSERT INTO moments(episode_id,position_ms,title,notes,created_at)
                VALUES((SELECT MAX(id) FROM episodes),4510000,'The old slave hanging tree','',$late);
                """;
            setup.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            setup.Parameters.AddWithValue("$early", DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
            setup.Parameters.AddWithValue("$late", DateTimeOffset.UtcNow.ToString("O"));
            setup.ExecuteNonQuery();

            using var ids = connection.CreateCommand();
            ids.CommandText = "SELECT id FROM episodes ORDER BY id";
            using var reader = ids.ExecuteReader();
            reader.Read();
            survivorEpisodeId = reader.GetInt64(0);
            reader.Read();
            memberEpisodeId = reader.GetInt64(0);
        }

        var service = new MomentsService(database);
        var repaired = service.SearchAsync(null).GetAwaiter().GetResult();
        Equal(2, repaired.Count);
        True(repaired.All(x => x.BroadcastId == survivorEpisodeId));

        var existingId = service.AddAsync(memberEpisodeId, 4_498_500, "The old slave hanging tree", "").GetAwaiter().GetResult();
        Equal(repaired.Single(x => x.PositionMs < 4_500_000).Id, existingId);
        Equal(2, service.SearchAsync(null).GetAwaiter().GetResult().Count);

        service.AddAsync(memberEpisodeId, 4_520_000, "The old slave hanging tree", "").GetAwaiter().GetResult();
        Equal(3, service.GetForBroadcastAsync(memberEpisodeId).GetAwaiter().GetResult().Count);

        using var verify = database.OpenConnection();
        using var count = verify.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM moments";
        Equal(3L, Convert.ToInt64(count.ExecuteScalar()));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void ProductVersionsRemainConsistent()
{
    var root = SourceRoot();
    var version = File.ReadAllText(Path.Combine(root, "VERSION.txt")).Trim();
    var desktopVersion = ProjectValue(
        Path.Combine(root, "TheRadioVault.Desktop.Avalonia", "TheRadioVault.Desktop.Avalonia.csproj"),
        "Version");
    var serverVersion = ProjectValue(
        Path.Combine(root, "TheRadioVault.Server", "TheRadioVault.Server.csproj"),
        "Version");
    var assemblyVersion = version + ".0";
    var desktopProject = Path.Combine(root, "TheRadioVault.Desktop.Avalonia", "TheRadioVault.Desktop.Avalonia.csproj");
    var serverProject = Path.Combine(root, "TheRadioVault.Server", "TheRadioVault.Server.csproj");
    var iosProject = Path.Combine(root, "TheRadioVault.Client.iOS", "TheRadioVault.Client.iOS.csproj");
    var iosVersion = ProjectValue(iosProject, "ApplicationDisplayVersion");
    var iosBuild = ProjectValue(iosProject, "ApplicationVersion");
    var plist = System.Xml.Linq.XDocument.Load(Path.Combine(root, "TheRadioVault.Client.iOS", "Info.plist"));

    Equal(version, desktopVersion);
    Equal(version, serverVersion);
    Equal(assemblyVersion, ProjectValue(desktopProject, "AssemblyVersion"));
    Equal(assemblyVersion, ProjectValue(desktopProject, "FileVersion"));
    Equal(assemblyVersion, ProjectValue(serverProject, "AssemblyVersion"));
    Equal(assemblyVersion, ProjectValue(serverProject, "FileVersion"));
    Equal(version, iosVersion);
    Equal(version, PlistValue(plist, "CFBundleShortVersionString"));
    Equal(iosBuild, PlistValue(plist, "CFBundleVersion"));
}

static string ProjectValue(string path, string name)
    => System.Xml.Linq.XDocument.Load(path)
           .Descendants()
           .First(element => element.Name.LocalName == name)
           .Value
           .Trim();

static string PlistValue(System.Xml.Linq.XDocument document, string key)
{
    var keyElement = document.Descendants("key").First(element => element.Value == key);
    return keyElement.ElementsAfterSelf().First().Value.Trim();
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

    throw new DirectoryNotFoundException("Could not locate the Radio Vault source root from the test output directory.");
}

static string ReadWebServerSourceBundle()
{
    var root = SourceRoot();
    var services = Path.Combine(root, "TheRadioVault.Web", "Services");
    var assets = Path.Combine(root, "TheRadioVault.Web", "Assets");
    var sources = Directory.GetFiles(services, "LocalWebServer*.cs")
        .OrderBy(path => path, StringComparer.Ordinal)
        .Concat(new[]
        {
            Path.Combine(assets, "web-client.html"),
            Path.Combine(assets, "service-worker.js"),
            Path.Combine(assets, "secure-setup.html")
        });
    return string.Join(Environment.NewLine, sources.Select(File.ReadAllText));
}

static string ReadServerAdministrationViews()
{
    var views = Path.Combine(SourceRoot(), "TheRadioVault.Server", "Views");
    return string.Join(
        Environment.NewLine,
        Directory.GetFiles(views, "Server*.axaml")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(File.ReadAllText));
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}

static void True(bool value, string? message = null)
{
    if (!value) throw new InvalidOperationException(message ?? "Expected true, got false.");
}

static void Throws<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void CanonicalTimelineMapsSourcePosition()
{
    var location = new TheRadioVault.Services.Models.CanonicalTimelineLocation("KEY","REC",1,2,3,2,3_600_000,5_400_000);
    Equal(3_690_000L, location.ToLogicalPosition(90_000));
    Equal(5_400_000L, location.ToLogicalPosition(9_000_000));
}

static void CoreHardeningApplicationContractsArePlatformNeutral()
{
    var applicationAssembly = typeof(IUiDispatcher).Assembly;
    Equal("TheRadioVault.Application", applicationAssembly.GetName().Name);
    True(applicationAssembly.GetReferencedAssemblies().All(reference =>
        !reference.Name!.StartsWith("Present", StringComparison.OrdinalIgnoreCase) &&
        !reference.Name.Contains("Windows", StringComparison.OrdinalIgnoreCase)));

    var notification = new UserNotification("Architecture", "Boundary", UserNotificationSeverity.Information);
    Equal("Architecture", notification.Title);
    Equal("Boundary", notification.Message);
    Equal("library", NavigationRequest.To("library").Route);
}


static void CoreHardeningStartupModeIsApplicationOwned()
{
    var coordinator = new ApplicationStartupCoordinator();
    var remote = coordinator.CreatePlan(new ApplicationStartupRequest(
        ForceLocalLibrary: false,
        UseRemoteLibraryOnStartup: true,
        HasSavedServer: true));
    Equal(ApplicationSessionMode.RemoteClient, remote.Mode);
    True(remote.IsRemoteClient);

    var forcedLocal = coordinator.CreatePlan(new ApplicationStartupRequest(
        ForceLocalLibrary: true,
        UseRemoteLibraryOnStartup: true,
        HasSavedServer: true));
    Equal(ApplicationSessionMode.LocalLibrary, forcedLocal.Mode);

    var missingServer = coordinator.CreatePlan(new ApplicationStartupRequest(
        ForceLocalLibrary: false,
        UseRemoteLibraryOnStartup: true,
        HasSavedServer: false));
    Equal(ApplicationSessionMode.LocalLibrary, missingServer.Mode);
}

static void CoreHardeningCompositionRootResolvesServices()
{
    var registry = new ApplicationServiceRegistry();
    var singleton = new StringBuilder("singleton");
    registry.RegisterSingleton(singleton);
    registry.RegisterFactory(() => new ApplicationShutdownCoordinator());

    True(ReferenceEquals(singleton, registry.GetRequiredService<StringBuilder>()));
    var first = registry.GetRequiredService<ApplicationShutdownCoordinator>();
    var second = registry.GetRequiredService<ApplicationShutdownCoordinator>();
    True(!ReferenceEquals(first, second));
}

static void CoreHardeningCompositionFreezesAndReportsRequiredServices()
{
    using var registry = new ApplicationServiceRegistry();
    registry.RegisterSingleton(new StringBuilder("ready"));

    var incomplete = registry.CreateCompositionReport(
        typeof(StringBuilder),
        typeof(ApplicationShutdownCoordinator));
    True(!incomplete.IsValid);
    Equal(1, incomplete.MissingRequiredServices.Count);

    var duplicateRejected = false;
    try
    {
        registry.RegisterSingleton(new StringBuilder("duplicate"));
    }
    catch (InvalidOperationException)
    {
        duplicateRejected = true;
    }
    True(duplicateRejected);

    registry.RegisterFactory(() => new ApplicationShutdownCoordinator());
    registry.Freeze();
    var complete = registry.CreateCompositionReport(
        typeof(StringBuilder),
        typeof(ApplicationShutdownCoordinator));
    True(complete.IsValid);
    True(complete.IsFrozen);
    True(complete.ToDiagnosticText().Contains("services=2", StringComparison.Ordinal));

    var rejected = false;
    try
    {
        registry.RegisterSingleton(new PlaybackProgressCoordinator());
    }
    catch (InvalidOperationException)
    {
        rejected = true;
    }
    True(rejected);
}

static void CoreHardeningLazySingletonFactoriesCreateOnce()
{
    using var registry = new ApplicationServiceRegistry();
    var creationCount = 0;
    registry.RegisterSingleton(_ =>
    {
        creationCount++;
        return new StringBuilder("lazy");
    });

    Equal(0, creationCount);
    var first = registry.GetRequiredService<StringBuilder>();
    var second = registry.GetRequiredService<StringBuilder>();
    Equal(1, creationCount);
    True(ReferenceEquals(first, second));
    True(registry.CreateCompositionReport().Registrations.Single().InstanceCreated);
}

static void CoreHardeningRegistryDisposesSingletonsInReverseOrder()
{
    var disposed = new List<string>();
    var registry = new ApplicationServiceRegistry();
    registry.RegisterSingleton(new FirstCompositionDisposable(disposed));
    registry.RegisterSingleton(new SecondCompositionDisposable(disposed));
    registry.Dispose();
    registry.Dispose();

    Equal(2, disposed.Count);
    Equal("second", disposed[0]);
    Equal("first", disposed[1]);
}

static void CoreHardeningCompositionDetectsDependencyCycles()
{
    using var registry = new ApplicationServiceRegistry();
    registry.RegisterFactory(r => new CompositionCycleA(r.GetRequiredService<CompositionCycleB>()));
    registry.RegisterFactory(r => new CompositionCycleB(r.GetRequiredService<CompositionCycleA>()));

    var detected = false;
    try
    {
        _ = registry.GetRequiredService<CompositionCycleA>();
    }
    catch (InvalidOperationException ex)
    {
        detected = ex.Message.Contains("Cyclic application service dependency", StringComparison.Ordinal);
    }
    True(detected);
}

static void CoreHardeningPlaybackSessionFactoryOwnsConstruction()
{
    var engine = new FakePlaybackEngine();
    var factory = new PlaybackSessionFactory();
    using (var session = factory.Create(engine))
    {
        session.SelectBroadcast(73);
        session.Open(@"C:\RadioVault\factory-test.mp3");
        Equal<long?>(73L, session.BroadcastId);
    }
    True(engine.Disposed);
}

static void CoreHardeningShutdownPipelineIsolatesFailures()
{
    var coordinator = new ApplicationShutdownCoordinator();
    True(coordinator.TryBegin());
    True(!coordinator.TryBegin());

    var executed = new List<string>();
    var report = coordinator.Execute(
        new ApplicationShutdownContext(ApplicationSessionMode.LocalLibrary, IsWindowTransition: false, IsDatabaseReset: false),
        new[]
        {
            new ApplicationShutdownStep("first", () => executed.Add("first")),
            new ApplicationShutdownStep("failure", () => throw new InvalidOperationException("expected")),
            new ApplicationShutdownStep("skipped", () => executed.Add("skipped"), _ => false),
            new ApplicationShutdownStep("last", () => executed.Add("last"))
        });

    Equal(2, executed.Count);
    Equal("first", executed[0]);
    Equal("last", executed[1]);
    Equal(1, report.FailedStepCount);
    True(!report.Succeeded);
    True(report.Steps.Single(step => step.Name == "skipped").Skipped);
}

static void CoreHardeningWindowTransitionBeginsOnce()
{
    var coordinator = new ApplicationWindowTransitionCoordinator();
    True(coordinator.TryBegin(ApplicationSessionMode.LocalLibrary, ApplicationSessionMode.RemoteClient, out var transition));
    Equal(ApplicationSessionMode.LocalLibrary, transition.SourceMode);
    Equal(ApplicationSessionMode.RemoteClient, transition.TargetMode);
    True(transition.ChangesMode);
    True(!coordinator.TryBegin(ApplicationSessionMode.LocalLibrary, ApplicationSessionMode.RemoteClient, out _));
}


static void CoreHardeningPlatformRequestsRemainNeutral()
{
    var uri = ExternalLaunchRequest.Uri(new Uri("https://example.com/archive"));
    Equal(ExternalLaunchKind.OpenUri, uri.Kind);
    Equal("https://example.com/archive", uri.Target.TrimEnd('/'));

    var reveal = ExternalLaunchRequest.Reveal(@"C:\RadioVault\show.mp3");
    Equal(ExternalLaunchKind.RevealFile, reveal.Kind);
    Equal(@"C:\RadioVault\show.mp3", reveal.Target);

    var bounds = new WindowBounds(10, 20, 800, 600);
    Equal(810d, bounds.Right);
    Equal(620d, bounds.Bottom);

    Equal("TheRadioVault.Application", typeof(IExternalLauncherService).Assembly.GetName().Name);
    Equal("TheRadioVault.Application", typeof(ISystemAppearanceService).Assembly.GetName().Name);
    Equal("TheRadioVault.Application", typeof(IScreenBoundsService).Assembly.GetName().Name);
}


static void CoreHardeningPlaybackSessionOwnsEngineCommands()
{
    var engine = new FakePlaybackEngine();
    var opened = false;
    var changed = false;
    using (var session = new PlaybackSessionCoordinator(engine))
    {
        session.MediaOpened += (_, _) => opened = true;
        session.StateChanged += (_, snapshot) =>
        {
            changed = true;
            Equal<long?>(42L, snapshot.BroadcastId);
        };

        session.SelectBroadcast(42);
        session.Open(@"C:\RadioVault\test.mp3");
        session.SetSpeed(1.5d);
        session.SetVolume(0.6d);
        session.Seek(TimeSpan.FromSeconds(30));
        engine.RaiseMediaOpened();
        session.Play();

        True(session.IsPlaying);
        Equal(1.5d, session.Speed);
        Equal(0.6d, session.Volume);
        Equal(TimeSpan.FromSeconds(30), session.Position);
        True(opened);
        True(changed);
    }

    True(engine.Disposed);
}

static void PlaybackStartupWaitsForReadiness()
{
    using var coordinator = new PlaybackStartupCoordinator();
    var ready = false;
    var run = Task.Run(async () =>
    {
        await using var attempt = coordinator.Begin();
        await attempt.EnterAsync();
        _ = Task.Run(async () =>
        {
            await Task.Delay(40);
            ready = true;
        });
        await attempt.WaitForReadinessAsync(
            () => ready,
            () => null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(5));
        True(attempt.IsCurrent);
    });
    run.GetAwaiter().GetResult();
    True(ready);
}

static void PlaybackStartupReportsTimeout()
{
    using var coordinator = new PlaybackStartupCoordinator();
    var run = Task.Run(async () =>
    {
        await using var attempt = coordinator.Begin();
        await attempt.EnterAsync();
        try
        {
            await attempt.WaitForReadinessAsync(
                () => false,
                () => null,
                TimeSpan.FromMilliseconds(40),
                TimeSpan.FromMilliseconds(5));
        }
        catch (PlaybackStartupTimeoutException exception)
        {
            Equal(TimeSpan.FromMilliseconds(40), exception.Timeout);
            return;
        }
        throw new InvalidOperationException("Expected a distinct playback startup timeout.");
    });
    run.GetAwaiter().GetResult();
}

static void PlaybackStartupReportsUnavailableMedia()
{
    using var coordinator = new PlaybackStartupCoordinator();
    var run = Task.Run(async () =>
    {
        await using var attempt = coordinator.Begin();
        await attempt.EnterAsync();
        try
        {
            await attempt.WaitForReadinessAsync(
                () => false,
                () => "The server stream is unavailable.",
                TimeSpan.FromSeconds(1));
        }
        catch (PlaybackStartupUnavailableException exception)
        {
            True(exception.Message.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
            return;
        }
        throw new InvalidOperationException("Expected unavailable media to remain distinct from a timeout.");
    });
    run.GetAwaiter().GetResult();
}

static void PlaybackStartupPreservesCallerCancellation()
{
    using var coordinator = new PlaybackStartupCoordinator();
    using var cancellation = new CancellationTokenSource();
    var run = Task.Run(async () =>
    {
        await using var attempt = coordinator.Begin(cancellation.Token);
        await attempt.EnterAsync();
        cancellation.Cancel();
        try
        {
            await attempt.WaitForReadinessAsync(
                () => false,
                () => null,
                TimeSpan.FromSeconds(1));
        }
        catch (OperationCanceledException) when (!attempt.IsSuperseded)
        {
            return;
        }
        throw new InvalidOperationException("Expected the original caller cancellation.");
    });
    run.GetAwaiter().GetResult();
}

static void PlaybackStartupSupersedesOlderSelection()
{
    using var coordinator = new PlaybackStartupCoordinator();
    var run = Task.Run(async () =>
    {
        await using var first = coordinator.Begin();
        await first.EnterAsync();

        var secondEntered = false;
        var secondTask = Task.Run(async () =>
        {
            await using var second = coordinator.Begin();
            await second.EnterAsync();
            secondEntered = true;
            True(second.IsCurrent);
        });

        await Task.Delay(30);
        True(first.IsSuperseded);
        True(!secondEntered, "The newer request touched the decoder before the old request released it.");
        Throws<PlaybackStartupSupersededException>(first.ThrowIfCancelledOrSuperseded);
        await first.DisposeAsync();
        await secondTask;
        True(secondEntered);
    });
    run.GetAwaiter().GetResult();
}

static void CoreHardeningPlaybackProgressProtectsResumePosition()
{
    var coordinator = new PlaybackProgressCoordinator();
    var plan = coordinator.CreatePlan(new PlaybackProgressRequest(
        ReportedPositionMs: 0,
        EpisodePositionMs: 120_000,
        PlayerStatePositionMs: 118_000,
        LastObservedPositionMs: 121_000,
        LogicalResumePositionMs: 119_000,
        ReportedDurationMs: 3_600_000,
        EpisodeDurationMs: 3_590_000,
        Completed: false,
        Speed: 1.25d,
        IncrementPlayCount: true));

    Equal(121_000L, plan.PositionMs);
    Equal(3_600_000L, plan.DurationMs);
    Equal(1.25d, plan.Speed);
    True(plan.IncrementPlayCount);
}

static void CoreHardeningCompletionRequiresNaturalPlayback()
{
    var coordinator = new PlaybackCompletionCoordinator();
    coordinator.BeginSession(58_000);

    True(!coordinator.Observe(59_000, 60_000, isPlaying: false, isSeeking: false, completionThresholdSeconds: 5));
    coordinator.ResetNaturalProgress(58_000);
    True(coordinator.Observe(59_000, 60_000, isPlaying: true, isSeeking: false, completionThresholdSeconds: 5));
    coordinator.MarkCompleted();
    True(!coordinator.Observe(60_000, 60_000, isPlaying: true, isSeeking: false, completionThresholdSeconds: 5));

    coordinator.BeginSession(0);
    True(!coordinator.Observe(59_000, 60_000, isPlaying: true, isSeeking: false, completionThresholdSeconds: 5));
}

sealed class FirstCompositionDisposable : IDisposable
{
    private readonly List<string> _disposed;
    public FirstCompositionDisposable(List<string> disposed) => _disposed = disposed;
    public void Dispose() => _disposed.Add("first");
}

sealed class SecondCompositionDisposable : IDisposable
{
    private readonly List<string> _disposed;
    public SecondCompositionDisposable(List<string> disposed) => _disposed = disposed;
    public void Dispose() => _disposed.Add("second");
}

sealed class RssScenarioHandler : HttpMessageHandler
{
    public bool IncludeNewEpisode { get; set; }
    public bool SawConditionalRequest { get; private set; }
    public bool SawFeedBasicAuthentication { get; private set; }
    public bool SentAuthenticationToMediaHost { get; private set; }
    public int AudioRequests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.RequestUri?.Host == "feeds.example")
        {
            SawConditionalRequest |= request.Headers.IfNoneMatch.Count > 0;
            SawFeedBasicAuthentication |= request.Headers.Authorization?.Scheme == "Basic";
            var newer = IncludeNewEpisode
                ? """
                  <item>
                    <title>Bennington - New episode</title>
                    <guid>bennington-new</guid>
                    <pubDate>Thu, 13 Aug 2026 12:00:00 GMT</pubDate>
                    <enclosure url="https://media.example/bennington-new.mp3?token=private-audio" type="audio/mpeg" />
                  </item>
                  """
                : string.Empty;
            var xml = $$"""
                <?xml version="1.0" encoding="utf-8"?>
                <rss version="2.0"><channel><title>Private Bennington</title>
                  <item>
                    <title>Bennington - Existing episode</title>
                    <guid>bennington-existing</guid>
                    <pubDate>Wed, 12 Aug 2026 12:00:00 GMT</pubDate>
                    <enclosure url="https://media.example/bennington-existing.mp3?token=private-audio" type="audio/mpeg" />
                  </item>
                  {{newer}}
                </channel></rss>
                """;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/rss+xml"),
                RequestMessage = request
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"rss-v1\"");
            return Task.FromResult(response);
        }
        if (request.RequestUri?.Host == "media.example")
        {
            AudioRequests++;
            SentAuthenticationToMediaHost |= request.Headers.Authorization is not null;
            var audio = Encoding.UTF8.GetBytes("radio-vault-test-audio:" + request.RequestUri.AbsolutePath);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(audio),
                RequestMessage = request
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
            return Task.FromResult(response);
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
    }
}

sealed class CompositionCycleA
{
    public CompositionCycleA(CompositionCycleB dependency) => Dependency = dependency;
    public CompositionCycleB Dependency { get; }
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

sealed class CompositionCycleB
{
    public CompositionCycleB(CompositionCycleA dependency) => Dependency = dependency;
    public CompositionCycleA Dependency { get; }
}

sealed class FakePlaybackEngine : IPlaybackEngine
{
    public event EventHandler? MediaOpened;
    public event EventHandler? MediaEnded { add { } remove { } }
    public event EventHandler<PlaybackErrorEventArgs>? MediaFailed { add { } remove { } }
    public event EventHandler<PlaybackEngineSnapshot>? StateChanged;

    public PlaybackStatus Status { get; private set; } = PlaybackStatus.Stopped;
    public bool IsPlaying => Status == PlaybackStatus.Playing;
    public string? MediaPath { get; private set; }
    public TimeSpan Position { get; set; }
    public TimeSpan? Duration { get; private set; } = TimeSpan.FromHours(1);
    public double Volume { get; set; } = 0.8d;
    public double Speed { get; set; } = 1d;
    public bool Disposed { get; private set; }

    public void Open(string path)
    {
        MediaPath = path;
        Status = PlaybackStatus.Opening;
        RaiseStateChanged();
    }

    public void Play()
    {
        Status = PlaybackStatus.Playing;
        RaiseStateChanged();
    }

    public void Pause()
    {
        Status = PlaybackStatus.Paused;
        RaiseStateChanged();
    }

    public void Stop()
    {
        Status = PlaybackStatus.Stopped;
        RaiseStateChanged();
    }

    public void Skip(TimeSpan amount) => Position += amount;

    public void RaiseMediaOpened()
    {
        Status = PlaybackStatus.Paused;
        MediaOpened?.Invoke(this, EventArgs.Empty);
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, new PlaybackEngineSnapshot(
        Status, IsPlaying, Position, Duration, Volume, Speed, MediaPath));

    public void Dispose() => Disposed = true;
}
