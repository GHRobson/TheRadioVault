using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
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
    ("Database seeds new Research pack shows", DatabaseSeedsNewResearchPackShows),
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
    ("Parser accepts variable-width US dates", ParserAcceptsVariableWidthUsDates),
    ("Parser recognises Roman multipart suffixes", ParserRecognisesRomanMultipartSuffixes),
    ("PM and evening slots reconcile", PmAndEveningSlotsReconcile),
    ("OpieRadio is a separate broadcast slot", OpieRadioIsSeparateBroadcastSlot),
    ("Explicit filename show overrides folder", ExplicitFilenameShowOverridesFolder),
    ("Library Truth recovers AFRO short dates from folder context", LibraryTruthRecoversAfroShortDate),
    ("Library Truth recognises Bennington OR slot", LibraryTruthRecognisesBenningtonOr),
    ("Library Truth keeps parent show for AFRO format broadcasts", LibraryTruthKeepsParentShowForAfro),
    ("Library Truth recovers compact RaF month day from year folder", LibraryTruthRecoversCompactRafMonthDay),
    ("Library Truth learns year from labelled year folders", LibraryTruthLearnsYearFromLabelledFolder),
    ("Library Truth parses indexed named-month dates", LibraryTruthParsesIndexedNamedMonthDates),
    ("Library Truth does not confuse source indices with years", LibraryTruthDoesNotConfuseSourceIndexWithYear),
    ("Library Truth distinguishes explicit and ambiguous multipart markers", LibraryTruthRecognisesAdditionalMultipartMarkers),
    ("Library Truth protects leading track and lone version numbers", LibraryTruthProtectsTrackAndVersionNumbers),
    ("Library Truth keeps variant and source filename families separate", LibraryTruthKeepsRecordingFamiliesSeparate),
    ("Library Truth normalises safe multipart family suffixes", LibraryTruthNormalizesSafeMultipartFamilySuffixes),
    ("Library Truth reassembles annotated multipart families", LibraryTruthReassemblesAnnotatedMultipartFamilies),
    ("Library Truth assembles Roman multipart files into one broadcast", LibraryTruthGroupsRomanMultipart),
    ("Library Truth preserves genuinely unknown dates", LibraryTruthPreservesUnknownDate),
    ("Library Truth shadow index separates broadcasts recordings and files", LibraryTruthShadowIndexSeparatesLayers),
    ("Library Truth treats alternate recordings as normal structure", LibraryTruthTreatsAlternateRecordingsAsNormal),
    ("Library Truth keeps identical audio with conflicting dates separate", LibraryTruthKeepsConflictingDatesSeparate),
    ("Library Truth groups exact unknown physical copies", LibraryTruthGroupsExactUnknownCopies),
    ("Library Truth separates full captures from multipart assemblies", LibraryTruthSeparatesFullAndMultipartRecordings),
    ("Library Truth classifies truncated recordings and ranks a preferred capture", LibraryTruthClassifiesTruncatedRecording),
    ("Library Truth compares multipart coverage with a full capture", LibraryTruthComparesMultipartCoverage),
    ("Library Truth flags suspicious substantial merges", LibraryTruthFlagsSuspiciousMerge),
    ("Library Truth propagates strong cross-date audio conflicts", LibraryTruthPropagatesStrongCrossDateConflict),
    ("Library Truth produces adoption and year audit summaries", LibraryTruthProducesAdoptionAudit),
    ("Library Truth assembles variant multipart families without cross-pairing", LibraryTruthAssemblesVariantFamiliesSafely),
    ("Library Truth promotes bare multipart numbers only with sibling evidence", LibraryTruthPromotesBareMultipartSequence),
    ("Library Truth flags repeated programme-specific duration clusters", LibraryTruthFlagsProgrammeSpecificDurationClusters),
    ("Library Truth detects composite AM and PM coverage", LibraryTruthDetectsCompositeCoverage),
    ("Library Truth detects same-date cross-slot equivalents", LibraryTruthDetectsCrossSlotEquivalent),
    ("Library Truth persists direct recording segment coverage", LibraryTruthPersistsDirectSegmentCoverage),
    ("Library Truth prepares guarded adoption plans without live writes", LibraryTruthPreparesGuardedAdoptionPreview),
    ("Library Truth rehearses adoption on a disposable clone and verifies rollback", LibraryTruthRehearsalRollsBackDisposableClone),
    ("Library Truth guarded adoption commits only the verified plan", LibraryTruthGuardedAdoptionCommitsVerifiedPlan),
    ("Library Truth classifies metadata conflicts and preserves alternates", LibraryTruthClassifiesMetadataConflicts),
    ("Library Truth refines generated metadata policies", LibraryTruthRefinesGeneratedMetadataPolicies),
    ("Research audit catches show guest", ResearchAuditCatchesShowGuest),
    ("Research audit marks safe repairs", ResearchAuditMarksSafeRepairs),
    ("Web query filters favourites", WebQueryFiltersFavourites),
    ("Web query searches people", WebQuerySearchesPeople),
    ("Web query filters date facets and listening status", WebQueryFiltersDateFacetsAndStatus),
    ("Web query paginates canonical library", WebQueryPaginatesCanonicalLibrary),
    ("Web episode exposes canonical identity", WebEpisodeExposesCanonicalIdentity),
    ("Web progress clamps", () => Equal(100, Episode(1, "Ron & Fez", favourite: false, positionMs: 1100, durationMs: 1000).ProgressPercent)),
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
    ("Web handoff preserves an aligned Safari decoder", WebHandoffPreservesAlignedSafariDecoder),
    ("iPhone broadcast switches replace stale decoders in the tap", IphoneBroadcastSwitchesReplaceStaleDecoderInTap),
    ("iPhone positioned failures preserve canonical gesture fallback", IphonePositionedFailuresPreserveCanonicalGestureFallback),
    ("Repeated iPhone handoffs bypass dormant decoder gating", RepeatedIphoneHandoffsBypassDormantDecoderGating),
    ("Native handoff preserves the Windows volume session", NativeHandoffPreservesWindowsVolumeSession),
    ("Mac Client uses native AVFoundation and existing server contracts", MacClientUsesNativeAvFoundationAndExistingServerContracts),
    ("iOS Client preserves native platform and server boundaries", IosClientPreservesNativePlatformAndServerBoundaries),
    ("Canonical audio ranges are cache-combinable", CanonicalAudioRangesAreCacheCombinable),
    ("Positioned web audio is stable across Safari ranges", PositionedWebAudioIsStableAcrossSafariRanges),
    ("Alpha 19 uses a truthful cache-first startup", Alpha19UsesTruthfulCacheFirstStartup),
    ("Alpha 20 hardens release truth and installer payloads", Alpha20HardensReleaseTruthAndInstallerPayloads),
    ("RC1 freezes recovery and upgrade preservation", Rc1FreezesRecoveryAndUpgradePreservation),
    ("RC1 buildfix restores visible Research pack import", Rc1BuildfixRestoresVisibleResearchPackImport),
    ("RC1 buildfix 4 unifies client UI and native downloads", Rc1Buildfix4UnifiesClientUiAndNativeDownloads),
    ("Alpha 0.35 begins the Wiki without breaking stable upgrades", Alpha035BeginsWikiWithoutBreakingStableUpgrades),
    ("Native downloads persist, audit and prepare local media", NativeDownloadsPersistAuditAndPrepareLocalMedia),
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
    ("Transactional handoff keeps source authoritative until commit", TransactionalHandoffKeepsSourceUntilCommit),
    ("Unowned playback permits a gesture-authorized audible decoder", UnownedPlaybackPermitsAudibleDecoder),
    ("Transactional handoff rejects startup zero", TransactionalHandoffRejectsStartupZero),
    ("Transactional handoff cancels without changing source", TransactionalHandoffCancellationKeepsSource),
    ("Transactional handoff invalidates when source changes", TransactionalHandoffInvalidatesChangedSource),
    ("Transactional handoff refreshes source play state before commit", TransactionalHandoffRefreshesSourcePlayState),
    ("Transactional handoff permits only one preparation", TransactionalHandoffIsSingleFlight),
    ("Transactional begin and commit retries are idempotent", TransactionalHandoffRetriesAreIdempotent),
    ("Transactional handoff covers all six device directions", TransactionalHandoffCoversAllDeviceDirections),
    ("Transactional handoff survives repeated device moves", TransactionalHandoffSurvivesRepeatedDeviceMoves),
    ("Non-transactional playback cannot steal an active owner", NonTransactionalPlaybackCannotStealActiveOwner),
    ("Transactional handoff preserves newer durable target progress", TransactionalHandoffPreservesNewerTargetProgress),
    ("Transactional handoff requires a physical source-stop receipt", TransactionalHandoffRequiresSourceStopReceipt),
    ("Transactional source-stop receipts reject stale acknowledgements", TransactionalHandoffRejectsStaleSourceStopReceipt),
    ("A newer handoff supersedes the prior source-stop receipt", NewerHandoffSupersedesPriorSourceStopReceipt),
    ("Live playback heartbeats do not mutate durable progress", LivePlaybackHeartbeatIsNotDurable),
    ("Failed transactional commit preserves owner and progress", FailedTransactionalCommitPreservesSource),
    ("Durable playback rejects a stale zero after handoff", DurablePlaybackRejectsStaleZeroAfterHandoff),
    ("Generation-less progress retries cannot rewind", GenerationlessProgressCannotRewind),
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
    ("Native server client advances capability generation 39", () => Equal(39, new WebServerOptions().CapabilityGeneration)),
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
    ("Web player is Radio Vault branded", WebPlayerIsRadioVaultBranded),
    ("Web shell reports configured app version", WebShellReportsConfiguredAppVersion),
    ("Web API sends remote playback commands", WebApiSendsRemotePlaybackCommands),
    ("Web client uses unified playback ownership", WebClientUsesUnifiedPlaybackOwnership),
    ("Web API exposes server playback session", WebApiExposesAuthoritativePlaybackSession),
    ("Web API acknowledges the physical source-stop boundary", WebApiAcknowledgesPhysicalSourceStop),
    ("Paused phone retains playback ownership", PausedPhoneRetainsPlaybackOwnership),
    ("Web playback lease rejects another client", WebPlaybackLeaseRejectsAnotherClient),
    ("Web API manages queue", WebApiManagesQueue),
    ("Web API preserves Moment identity while editing", WebApiEditsMomentInPlace),
    ("Web API synchronises offline progress", WebApiSynchronisesOfflineProgress),
    ("Web API permits explicit LAN progress rewind", WebApiPermitsExplicitLanProgressRewind),
    ("Web client includes manual offline downloads", WebClientIncludesManualOfflineDownloads),
    ("Anywhere exposes the server transcription workspace", AnywhereExposesServerTranscriptionWorkspace),
    ("Anywhere shell matches native navigation and player structure", AnywhereShellMatchesNativeStructure),
    ("Anywhere dashboard player and handoff match the native contract", AnywhereDashboardPlayerAndHandoffMatchNativeContract),
    ("Web download button has one click path", WebDownloadButtonHasOneClickPath),
    ("Web client registers secure offline shell", WebClientRegistersSecureOfflineShell),
    ("Web client includes final recovery and accessibility", WebClientIncludesFinalRecoveryAndAccessibility),
    ("Web client caches downloaded artwork", WebClientCachesDownloadedArtwork),
    ("Secure web options carry certificate material", SecureWebOptionsCarryCertificateMaterial),
    ("Schema 45 includes guarded Library Truth adoption tables", Schema45IncludesGuardedLibraryTruthAdoptionTables),
    ("Schema 47 adds canonical topic identities and merge history", Schema47AddsCanonicalTopicIdentity),
    ("Wiki pages protect newer human revisions", WikiPagesProtectNewerHumanRevisions),
    ("Wiki authoring packs round-trip citations images and timelines", WikiAuthoringPacksRoundTripEvidence),
    ("Knowledge imports recover untitled AI citation sources", KnowledgeImportsRecoverUntitledAiCitationSources),
    ("Knowledge imports reconcile existing Explore slugs", KnowledgeImportsReconcileExistingExploreSlugs),
    ("Ambiguous research review uses a schema-valid state", AmbiguousResearchReviewUsesSchemaValidState),
    ("Knowledge exports teach AI agents the portable database", KnowledgeExportsTeachAiAgentsThePortableDatabase),
    ("Complete knowledge exports include every show and transcript", CompleteKnowledgeExportsIncludeEveryShowAndTranscript),
    ("Knowledge export UI is always archive-wide", KnowledgeExportUiIsAlwaysArchiveWide),
    ("Knowledge imports run as resumable background jobs", KnowledgeImportsRunAsResumableBackgroundJobs),
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
    ("Canonical web routes identify manifests and parts", CanonicalWebRoutesAreStable),
    ("Schema 45 upgrades Library Truth schema 43 safely", Schema45UpgradesLibraryTruthSchema43Safely),
    ("Whisper configuration exposes model capabilities", WhisperConfigurationExposesModelCapabilities),
    ("Multi-speaker diarization splits timed transcript turns", MultiSpeakerDiarizationSplitsTimedTranscriptTurns),
    ("Whisper settings persist for the live desktop engine", WhisperSettingsPersistForLiveDesktopEngine),
    ("In-app transcription setup installs official assets safely", InAppTranscriptionSetupInstallsOfficialAssetsSafely),
    ("Transcription ranges have stable display text", TranscriptionRangesHaveStableDisplayText),
    ("Long-form transcription protects continuity and timestamps", LongFormTranscriptionProtectsContinuityAndTimestamps),
    ("Dedicated server foundation is UI-isolated and revision-safe", DedicatedServerFoundationIsUiIsolatedAndRevisionSafe),
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

static void DatabaseSeedsNewResearchPackShows()
{
    var path = Path.Combine(Path.GetTempPath(), $"radiovault-show-seed-{Guid.NewGuid():N}.db");
    try
    {
        var database = new SqliteDatabase(path);
        database.Initialize();
        using var connection = database.OpenConnection();
        foreach (var show in new[]
                 {
                     KnownShowCatalog.RonRon,
                     KnownShowCatalog.Unmasked,
                     KnownShowCatalog.RonBenningtonInterviews
                 })
        {
            using var collection = connection.CreateCommand();
            collection.CommandText = "SELECT COUNT(*) FROM collections WHERE name=$name";
            collection.Parameters.AddWithValue("$name", show);
            Equal(1L, Convert.ToInt64(collection.ExecuteScalar(), CultureInfo.InvariantCulture));
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.name
              FROM collections c
              JOIN collection_aliases a ON a.collection_id=c.id
             WHERE a.alias=$alias COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$alias", "ron bennington interview");
        Equal("Ron Bennington Interviews", Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture));
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
        if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
    }
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
            Segments = new[] { new TranscriptSegment(0, 0, 4000, "A discussion about phosphorescent penguins in radio.") }
        }).GetAwaiter().GetResult();

        var result = new LibraryBrowseService(database).BrowseAsync(new TheRadioVault.Services.Models.LibraryBrowseRequest(
            SearchText: "phosphorescent penguins",
            SearchScope: TheRadioVault.Services.Models.LibrarySearchScope.Transcripts)).GetAwaiter().GetResult();
        Equal(1, result.TotalMatching);
        True(result.Broadcasts.Single().SearchContext.StartsWith("Transcript:", StringComparison.Ordinal));
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

    var serverView = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Server", "Views", "ServerSettingsWindow.axaml"));
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
    True(File.Exists(Path.Combine(SourceRoot(), "V0.34.0-ALPHA13-REMOTE-CLIENT-INSTALLER.md")));
}

static void Alpha13Buildfix1RestoresFoldersListeningActionsAndDualInstallers()
{
    var serverRuntime = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Infrastructure", "Services", "RadioVaultServerRuntime.cs"));
    True(serverRuntime.Contains("AddLibraryFolderAsync", StringComparison.Ordinal));
    True(serverRuntime.Contains("SetLibraryFolderEnabledAsync", StringComparison.Ordinal));
    True(serverRuntime.Contains("RemoveLibraryFolderAsync", StringComparison.Ordinal));
    True(serverRuntime.Contains("ScanLibraryAsync", StringComparison.Ordinal));

    var serverView = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Server", "Views", "ServerSettingsWindow.axaml"));
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
    True(File.Exists(Path.Combine(SourceRoot(), "V0.34.0-ALPHA13-BUILDFIX1-FOLDERS-LISTENING-INSTALLERS.md")));
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

    var webServer = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Web", "Services", "LocalWebServer.cs"));
    True(webServer.Contains("productName = \"Radio Vault Web\"", StringComparison.Ordinal));
    True(webServer.Contains("accessUrl = GetAccessUrls().FirstOrDefault()", StringComparison.Ordinal));
    True(webServer.Contains("secureSetupUrl = GetSecureSetupUrls().FirstOrDefault()", StringComparison.Ordinal));
    True(webServer.Contains("<title>Radio Vault Web</title>", StringComparison.Ordinal));
    True(webServer.Contains("radio-vault-anywhere-shell-v67", StringComparison.Ordinal));

    var clientAdapter = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Anywhere", "DedicatedServerRadioVaultAnywhereService.cs"));
    True(clientAdapter.Contains("response.Web?.AccessUrl", StringComparison.Ordinal));
    True(clientAdapter.Contains("response.Web?.SecureSetupUrl", StringComparison.Ordinal));

    var clientSettings = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Desktop.Avalonia", "Views", "DesktopToolsView.axaml"));
    True(clientSettings.Contains("Connect a phone to Radio Vault Web", StringComparison.Ordinal));
    True(clientSettings.Contains("AnywhereQrCode.Rows", StringComparison.Ordinal));
    True(clientSettings.Contains("AnywhereSetupQrCode.Rows", StringComparison.Ordinal));

    var serverSettings = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Server", "Views", "ServerSettingsWindow.axaml"));
    True(serverSettings.Contains("RADIO VAULT WEB", StringComparison.Ordinal));
    True(serverSettings.Contains("CopyWebLinkCommand", StringComparison.Ordinal));
    True(serverSettings.Contains("RegenerateWebLinkCommand", StringComparison.Ordinal));
    True(serverSettings.Contains("WebQrCode.Rows", StringComparison.Ordinal));
    True(serverSettings.Contains("SecureSetupQrCode.Rows", StringComparison.Ordinal));

    True(File.Exists(Path.Combine(SourceRoot(), "V0.34.0-ALPHA14-RADIO-VAULT-WEB-PHONE-CONNECTION.md")));
}

static void Alpha15RestoresServerFolderAssignmentAndNativeAudioQuality()
{
    var serverViewModel = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Server", "ViewModels", "ServerSettingsViewModel.cs"));
    True(serverViewModel.Contains("ChooseLibraryFolderShowAsync", StringComparison.Ordinal));
    True(serverViewModel.Contains("SetLibraryFolderCollectionAsync", StringComparison.Ordinal));
    True(serverViewModel.Contains("ScanLibraryAsync", StringComparison.Ordinal));

    var serverView = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Server", "Views", "ServerSettingsWindow.axaml"));
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

    True(File.Exists(Path.Combine(SourceRoot(), "V0.34.0-ALPHA15-SERVER-FOLDER-AUDIO-REPAIR.md")));
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
    True(mainWindow.Contains("Playback.ShowMoveToThisDevice", StringComparison.Ordinal));
    True(mainWindow.Contains("Move playback to this PC", StringComparison.Ordinal));
    True(mainWindow.Contains("M2,11 L13,11", StringComparison.Ordinal));

    True(File.Exists(Path.Combine(SourceRoot(), "V0.34.0-ALPHA16-REMOTE-RESPONSIVENESS-OWNERSHIP.md")));
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

    True(File.Exists(Path.Combine(SourceRoot(), "V0.34.0-ALPHA17-LARGE-LIBRARY-HANDOFF.md")));
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

    var playback = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Presentation", "ViewModels", "PlaybackViewModel.cs"));
    True(playback.Contains("_handoffSnapshot?.HasActivePlayback == true", StringComparison.Ordinal));
    True(playback.Contains("_handoffSnapshot.IsOwnedByCurrentDevice == false", StringComparison.Ordinal));

    var web = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Web", "Services", "LocalWebServer.cs"));
    True(web.Contains("radio-vault-anywhere-shell-v67", StringComparison.Ordinal));
    True(!web.Contains("radio-vault-anywhere-shell-v42", StringComparison.Ordinal));

    foreach (var installerName in new[] { "RadioVault.Client.iss", "RadioVault.Server.iss" })
    {
        var installer = File.ReadAllText(Path.Combine(SourceRoot(), "installer", installerName));
        True(installer.Contains("UsePreviousAppDir=yes", StringComparison.Ordinal));
        True(installer.Contains("UsePreviousTasks=yes", StringComparison.Ordinal));
        True(installer.Contains("SetupLogging=yes", StringComparison.Ordinal));
        True(installer.Contains("Type: filesandordirs; Name: \"{app}\"", StringComparison.Ordinal));
    }

    True(File.Exists(Path.Combine(SourceRoot(), "V0.34.0-ALPHA18-CONNECTED-RELIABILITY.md")));
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

static void WebHandoffPreservesAlignedSafariDecoder()
{
    var web = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Web", "Services", "LocalWebServer.cs"));
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
    True(web.Contains("radio-vault-anywhere-shell-v67", StringComparison.Ordinal));
}

static void IphoneBroadcastSwitchesReplaceStaleDecoderInTap()
{
    var web = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Web", "Services", "LocalWebServer.cs"));
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

static void RepeatedIphoneHandoffsBypassDormantDecoderGating()
{
    var web = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Web", "Services", "LocalWebServer.cs"));

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
    True(web.Contains("radio-vault-anywhere-shell-v67", StringComparison.Ordinal));
}

static void IphonePositionedFailuresPreserveCanonicalGestureFallback()
{
    var web = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Web", "Services", "LocalWebServer.cs"));
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
    True(web.Contains("radio-vault-anywhere-shell-v67", StringComparison.Ordinal));
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

static void IosClientPreservesNativePlatformAndServerBoundaries()
{
    var iosProject = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "TheRadioVault.Client.iOS.csproj"));
    True(iosProject.Contains("net10.0-ios", StringComparison.Ordinal));
    True(!iosProject.Contains("Avalonia", StringComparison.Ordinal));
    True(iosProject.Contains("TheRadioVault.Client.Mobile", StringComparison.Ordinal));
    True(iosProject.Contains("<AppIcon>AppIcon</AppIcon>", StringComparison.Ordinal));
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
    True(!tabs.Contains("Wrap(new KnowledgeViewController", StringComparison.Ordinal));
    True(tabs.Contains("DownloadsViewController", StringComparison.Ordinal));
    True(tabs.Contains("RadioVaultMiniPlayerView", StringComparison.Ordinal));
    True(!tabs.Contains("Wrap(new NowPlayingViewController", StringComparison.Ordinal));
    True(tabs.Contains("ServerViewController", StringComparison.Ordinal));
    True(tabs.Contains("RadioVaultIcons.Image", StringComparison.Ordinal));
    True(tabs.Contains("\"Settings\"", StringComparison.Ordinal));

    var dashboard = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "HomeViewController.cs"));
    True(dashboard.Contains("Title = \"Dashboard\"", StringComparison.Ordinal));
    True(dashboard.Contains("Continue listening", StringComparison.Ordinal));
    True(dashboard.Contains("Surprise me", StringComparison.Ordinal));
    True(dashboard.Contains("On this day", StringComparison.Ordinal));
    True(dashboard.Contains("Recently added", StringComparison.Ordinal));
    True(dashboard.Contains("Unheard broadcasts", StringComparison.Ordinal));

    var library = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "LibraryViewController.cs"));
    True(library.Contains("UISearchController", StringComparison.Ordinal));
    True(library.Contains("UITableView", StringComparison.Ordinal));
    True(library.Contains("LibraryCollections", StringComparison.Ordinal));
    True(library.Contains("ShowLibraryViewController", StringComparison.Ordinal));
    True(library.Contains("Favourites", StringComparison.Ordinal));
    True(library.Contains("ContinueListening", StringComparison.Ordinal));
    True(library.Contains("UpNextViewController", StringComparison.Ordinal));
    True(library.Contains("ToggleHideCompleted", StringComparison.Ordinal));
    True(library.Contains("RadioVaultIcon.Completed", StringComparison.Ordinal));

    var showLibrary = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "ShowLibraryViewController.cs"));
    True(showLibrary.Contains("UISegmentedControl", StringComparison.Ordinal));
    True(showLibrary.Contains("ArchiveViewMode.Years", StringComparison.Ordinal));
    True(showLibrary.Contains("ArchiveViewMode.Months", StringComparison.Ordinal));
    True(showLibrary.Contains("ArchiveViewMode.Broadcasts", StringComparison.Ordinal));
    True(showLibrary.Contains("LoadArchivePeriodsAsync", StringComparison.Ordinal));

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
    True(explore.Contains("Search in", StringComparison.Ordinal));
    True(explore.Contains("Listening status", StringComparison.Ordinal));
    True(explore.Contains("Has transcript", StringComparison.Ordinal));
    True(explore.Contains("Suggestions", StringComparison.Ordinal));
    True(explore.Contains("Ways into the archive", StringComparison.Ordinal));
    True(explore.Contains("Browse by show", StringComparison.Ordinal));
    True(explore.Contains("ExploreAsync", StringComparison.Ordinal));

    var theme = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "RadioVaultTheme.cs"));
    True(theme.Contains("0x11, 0x13, 0x17", StringComparison.Ordinal));
    True(theme.Contains("0xF2, 0xC9, 0x4C", StringComparison.Ordinal));
    True(theme.Contains("UIGraphicsImageRenderer", StringComparison.Ordinal));
    True(theme.Contains("RadioVaultIcon.Knowledge", StringComparison.Ordinal));
    True(theme.Contains("RadioVaultIcon.Handoff", StringComparison.Ordinal));
    True(theme.Contains("(3, 10.5), (13, 10.5)", StringComparison.Ordinal));

    var miniPlayer = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "RadioVaultMiniPlayerView.cs"));
    True(miniPlayer.Contains("MiniPlayerShowsHandoff", StringComparison.Ordinal));
    True(miniPlayer.Contains("RadioVaultIcon.Handoff", StringComparison.Ordinal));
    True(miniPlayer.Contains("Move playback to this iPhone", StringComparison.Ordinal));

    var nowPlayingView = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "NowPlayingViewController.cs"));
    True(nowPlayingView.Contains("content.CenterYAnchor.ConstraintEqualTo", StringComparison.Ordinal));
    True(nowPlayingView.Contains("SetPreferredSymbolConfiguration", StringComparison.Ordinal));
    True(nowPlayingView.Contains("_playButton.WidthAnchor.ConstraintEqualTo(96)", StringComparison.Ordinal));
    True(nowPlayingView.Contains("SeekToProgress", StringComparison.Ordinal));

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

    var downloadPolicy = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "IosDownloadPolicy.cs"));
    True(downloadPolicy.Contains("NWPathMonitor", StringComparison.Ordinal));
    True(downloadPolicy.Contains("NWInterfaceType.Wifi", StringComparison.Ordinal));
    True(downloadPolicy.Contains("NSUserDefaults", StringComparison.Ordinal));

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

    var nowPlaying = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.iOS", "IosNowPlayingService.cs"));
    True(nowPlaying.Contains("MPNowPlayingInfoCenter", StringComparison.Ordinal));
    True(nowPlaying.Contains("MPRemoteCommandCenter", StringComparison.Ordinal));
    True(nowPlaying.Contains("ChangePlaybackPositionCommand", StringComparison.Ordinal));
    True(nowPlaying.Contains("SkipBackwardCommand", StringComparison.Ordinal));

    var mobileSession = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Client.Mobile", "MobileClientSession.cs"));
    True(mobileSession.Contains("WebPlaybackTransferBeginRequest", StringComparison.Ordinal));
    True(mobileSession.Contains("WebPlaybackTransferReadyRequest", StringComparison.Ordinal));
    True(mobileSession.Contains("WebPlaybackTransferCommitRequest", StringComparison.Ordinal));
    True(mobileSession.Contains("WebPlaybackTransferSourceStoppedRequest", StringComparison.Ordinal));
    True(mobileSession.Contains("WebOfflineProgressUpdate", StringComparison.Ordinal));
    True(mobileSession.Contains("DurableProgressInterval", StringComparison.Ordinal));
    True(mobileSession.Contains("DownloadedBroadcasts", StringComparison.Ordinal));
    True(mobileSession.Contains("PlayDownloadedAsync", StringComparison.Ordinal));
    True(mobileSession.Contains("ObserveSharedPlaybackAsync", StringComparison.Ordinal));
    True(mobileSession.Contains("MiniPlayerShowsHandoff", StringComparison.Ordinal));
    True(mobileSession.Contains("QueueItems", StringComparison.Ordinal));
    True(mobileSession.Contains("IsDownloadPaused", StringComparison.Ordinal));

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

static void PositionedWebAudioIsStableAcrossSafariRanges()
{
    var stem = Path.Combine(Path.GetTempPath(), $"radiovault-positioned-{Guid.NewGuid():N}");
    var sourceWavePath = stem + ".wav";
    var path = stem + ".mp3";
    try
    {
        const int sampleRate = 44_100;
        const int seconds = 5;
        var dataLength = sampleRate * seconds * 2;
        using (var file = File.Create(sourceWavePath))
        using (var writer = new BinaryWriter(file, Encoding.ASCII, leaveOpen: false))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);
            for (var sampleIndex = 0; sampleIndex < sampleRate * seconds; sampleIndex++)
            {
                var elapsed = sampleIndex / (double)sampleRate;
                var frequency = 220d + elapsed * 90d;
                writer.Write((short)(Math.Sin(2d * Math.PI * frequency * elapsed) * short.MaxValue * 0.45d));
            }
        }
        using (var source = new NAudio.Wave.WaveFileReader(sourceWavePath))
            NAudio.Wave.MediaFoundationEncoder.EncodeToMp3(source, path, 96_000);

        WithCustomWebServer(new FakeWebArchiveProvider(audioPath: path), async (port, token) =>
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"http://127.0.0.1:{port}/api/v1/broadcasts/9/media-start?positionMs=2000&token={Uri.EscapeDataString(token)}");
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 63);
            using var response = await client.SendAsync(request);
            Equal(System.Net.HttpStatusCode.PartialContent, response.StatusCode);
            Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Equal(64, bytes.Length);
            Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
            var positionedLength = response.Content.Headers.ContentRange?.Length ?? 0;
            var positionedEtag = response.Headers.ETag?.Tag;
            True(positionedLength > bytes.Length);
            True(!string.IsNullOrWhiteSpace(positionedEtag));

            using var zeroRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"http://127.0.0.1:{port}/api/v1/broadcasts/9/media-start?positionMs=0&positioned=1&token={Uri.EscapeDataString(token)}");
            zeroRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 63);
            using var zeroResponse = await client.SendAsync(zeroRequest);
            Equal(System.Net.HttpStatusCode.PartialContent, zeroResponse.StatusCode);
            Equal("audio/wav", zeroResponse.Content.Headers.ContentType?.MediaType);
            var zeroBytes = await zeroResponse.Content.ReadAsByteArrayAsync();
            Equal("RIFF", Encoding.ASCII.GetString(zeroBytes, 0, 4));
            var zeroLength = zeroResponse.Content.Headers.ContentRange?.Length ?? 0;
            var zeroEtag = zeroResponse.Headers.ETag?.Tag;
            True(zeroLength > positionedLength);
            True(zeroLength - positionedLength > 100_000);
            True(!string.IsNullOrWhiteSpace(zeroEtag));
            True(!string.Equals(positionedEtag, zeroEtag, StringComparison.Ordinal));

            var positionedUrl =
                $"http://127.0.0.1:{port}/api/v1/broadcasts/9/media-start?positionMs=2000&positioned=1&streamSession=range-stability&token={Uri.EscapeDataString(token)}";
            var continuousBytes = await client.GetByteArrayAsync(positionedUrl);
            using var rangedBytes = new MemoryStream(continuousBytes.Length);
            const int safariRangeSize = 32 * 1024;
            for (var start = 0; start < continuousBytes.Length; start += safariRangeSize)
            {
                var end = Math.Min(continuousBytes.Length - 1, start + safariRangeSize - 1);
                using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, positionedUrl);
                rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
                using var rangeResponse = await client.SendAsync(rangeRequest);
                Equal(System.Net.HttpStatusCode.PartialContent, rangeResponse.StatusCode);
                var rangeBytes = await rangeResponse.Content.ReadAsByteArrayAsync();
                Equal(end - start + 1, rangeBytes.Length);
                await rangedBytes.WriteAsync(rangeBytes);
            }
            Equal(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(continuousBytes)),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rangedBytes.ToArray())));
        });
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(sourceWavePath)) File.Delete(sourceWavePath);
    }
}

static void CanonicalAudioRangesAreCacheCombinable()
{
    var web = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Web", "Services", "LocalWebServer.cs"));
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

    True(File.Exists(Path.Combine(SourceRoot(), "V0.34.0-ALPHA20-RELEASE-HARDENING.md")));
}

static void Rc1FreezesRecoveryAndUpgradePreservation()
{
    var access = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Infrastructure", "Services", "NativeConnectedAccessService.cs"));
    True(access.Contains("var recovered = !_current.IsLive;", StringComparison.Ordinal));
    True(access.Contains("MarkServerLive(invalidateMemoryCache: recovered)", StringComparison.Ordinal));

    var web = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Web", "Services", "LocalWebServer.cs"));
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

    True(File.Exists(Path.Combine(SourceRoot(), "V0.34.0-RC1-STABILITY.md")));
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

    var server = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Web", "Services", "LocalWebServer.cs"));
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

    var web = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Web", "Services", "LocalWebServer.cs"));
    True(web.Contains("radio-vault-anywhere-shell-v67", StringComparison.Ordinal));
    True(web.Contains("class=\"menuToggle\"", StringComparison.Ordinal));
    True(web.Contains("id=\"menuScrim\"", StringComparison.Ordinal));
    True(web.Contains("body.menuOpen", StringComparison.Ordinal));
    True(web.Contains("height:calc(100dvh - (max(14px, env(safe-area-inset-top)) + 76px))", StringComparison.Ordinal));
    True(web.Contains("class=\"libraryPrimaryAction\"", StringComparison.Ordinal));
    True(web.Contains("/app-icon-180.png?token=__TOKEN__&v=__APP_VERSION__", StringComparison.Ordinal));
    True(web.Contains("if ((isGet || isHead) && TryGetWebAppIcon", StringComparison.Ordinal));
    True(!web.Contains("if (secure && (isGet || isHead) && TryGetWebAppIcon", StringComparison.Ordinal));

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
    True(shell.Contains("Move playback to this PC", StringComparison.Ordinal));
    True(!shell.Contains("Content=\"⇥\"", StringComparison.Ordinal));

    True(File.Exists(Path.Combine(SourceRoot(), "V0.34.0-RC1-BUILDFIX4-CLIENT-UI-DOWNLOADS.md")));
}

static void Alpha035BeginsWikiWithoutBreakingStableUpgrades()
{
    Equal("0.35.0-alpha9-buildfix3", File.ReadAllText(Path.Combine(SourceRoot(), "VERSION.txt")).Trim());

    foreach (var projectPath in new[]
             {
                 Path.Combine("TheRadioVault.Desktop.Avalonia", "TheRadioVault.Desktop.Avalonia.csproj"),
                 Path.Combine("TheRadioVault.Server", "TheRadioVault.Server.csproj")
             })
    {
        var project = File.ReadAllText(Path.Combine(SourceRoot(), projectPath));
        True(project.Contains("<Version>0.35.0-alpha9-buildfix3</Version>", StringComparison.Ordinal));
        True(project.Contains("<InformationalVersion>0.35.0-alpha9-buildfix3</InformationalVersion>", StringComparison.Ordinal));
    }

    var readme = File.ReadAllText(Path.Combine(SourceRoot(), "README.md"));
    True(readme.StartsWith("# Radio Vault 0.35.0 Alpha 9 Buildfix 3", StringComparison.Ordinal));
    True(readme.Contains("Radio Vault Server", StringComparison.Ordinal));
    True(readme.Contains("Radio Vault Client", StringComparison.Ordinal));
    True(readme.Contains("Radio Vault Web", StringComparison.Ordinal));
    True(readme.Contains("not designed to be exposed directly to the public internet", StringComparison.Ordinal));
    True(readme.Contains(".trvknowledge", StringComparison.Ordinal));

    var building = File.ReadAllText(Path.Combine(SourceRoot(), "BUILDING.md"));
    True(building.StartsWith("# Building Radio Vault 0.35.0 Alpha 9 Buildfix 3", StringComparison.Ordinal));
    True(building.Contains("package-server-installer.ps1", StringComparison.Ordinal));
    True(building.Contains("package-client-installer.ps1", StringComparison.Ordinal));
    True(!building.Contains("subsequent 0.34 phases", StringComparison.Ordinal));

    var foundation = File.ReadAllText(Path.Combine(SourceRoot(), "tools", "Test-AvaloniaFoundation.ps1"));
    True(foundation.Contains("foundationVersion = '0.35-alpha9-knowledge-portability'", StringComparison.Ordinal));
    True(foundation.Contains("databaseSchema = 47", StringComparison.Ordinal));
    True(foundation.Contains("lanCapabilityGeneration = 40", StringComparison.Ordinal));
    foreach (var marker in new[]
             {
                 "remoteClientMigrated = $true", "connectedAccessWorkspaceMigrated = $true",
                 "encryptedRemoteCache = $true", "automaticReconnect = $true",
                 "remotePlaybackMigrated = $true", "remoteProgressWriteThrough = $true"
             })
        True(foundation.Contains(marker, StringComparison.Ordinal));

    var web = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Web", "Services", "LocalWebServer.cs"));
    True(web.Contains("radio-vault-anywhere-shell-v67", StringComparison.Ordinal));

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
    Equal("0.35.0-alpha9-buildfix3", sourceManifest.RootElement.GetProperty("version").GetString());

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

    True(File.Exists(Path.Combine(SourceRoot(), "V0.34.0-STABLE.md")));
    True(File.Exists(Path.Combine(SourceRoot(), "V0.35.0-ALPHA1-WIKI-FOUNDATION.md")));
    True(File.Exists(Path.Combine(SourceRoot(), "V0.35.0-ALPHA1-BUILDFIX1-RESEARCH-PACK-TOLERANCE.md")));
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

static void ParserAcceptsVariableWidthUsDates()
{
    var parser = new FilenameParserService();
    var bennington = parser.Parse(@"E:\Radio\Bennington 2-6-2015.m4a");
    Equal(new DateTime(2015, 2, 6), bennington.AirDate!.Value);
    Equal("High", bennington.DateConfidence);

    var ronFez = parser.Parse(@"E:\Radio\Ron.And.Fez.9-04-2014.CF64K.m4a");
    Equal(new DateTime(2014, 9, 4), ronFez.AirDate!.Value);
    Equal("High", ronFez.DateConfidence);
}

static void ParserRecognisesRomanMultipartSuffixes()
{
    var parser = new FilenameParserService();
    var partOne = parser.Parse(@"E:\Radio\R&F-2015-02-27 I.mp3");
    var partTwo = parser.Parse(@"E:\Radio\R&F-2015-02-27 II.mp3");
    Equal(1, partOne.PartNumber);
    Equal(2, partTwo.PartNumber);
    True(partOne.MultipartKind == "Part");
    True(partTwo.MultipartKind == "Part");
    True(string.IsNullOrWhiteSpace(partOne.HeadlineCandidate));
    True(string.IsNullOrWhiteSpace(partTwo.HeadlineCandidate));
}

static void PmAndEveningSlotsReconcile()
{
    True(BroadcastSlotNormalizer.Equivalent("PM", "Evening show"));
    True(BroadcastSlotNormalizer.Equivalent("Afternoon show", "Evening show"));
    True(BroadcastSlotNormalizer.Equivalent("12:00 p.m.–3:00 p.m. Eastern", "PM"));
    True(!BroadcastSlotNormalizer.Equivalent("Morning show", "Evening show"));
}

static void OpieRadioIsSeparateBroadcastSlot()
{
    var parsed = new FilenameParserService().Parse(@"E:\Bennington\Bennington - 2015-05-29 Fri (OpieRadio Edition).m4a");
    Equal("OpieRadio Edition", parsed.BroadcastSlot);
    True(string.IsNullOrWhiteSpace(parsed.Edition));
    True(string.IsNullOrWhiteSpace(parsed.HeadlineCandidate));
    Equal("BENNINGTON-2015-05-29-OPIERADIO-EDITION", BroadcastIdentityService.CreateStableId("Bennington", new DateOnly(2015, 5, 29), 1, parsed.BroadcastSlot));
}

static void ExplicitFilenameShowOverridesFolder()
{
    var parsed = new FilenameParserService().Parse(@"E:\Ron & Fez Archive\2007-10-11-O&A-CF64k.m4a");
    Equal("Opie & Anthony", parsed.CollectionName);
    True(parsed.CollectionDetectedFromFilename);
}


static void LibraryTruthRecoversAfroShortDate()
{
    var parser = new LibraryTruthParser();
    var input = TruthInput(@"D:\radio_shows\AFRO Shows\2004\Afro Show 12-28-04.mp3", "AFRO Show");
    var context = new LibraryTruthFolderContext
    {
        ContextKey = input.DirectoryPath,
        LibraryRoot = @"D:\radio_shows\AFRO Shows",
        AssignedCollectionName = "AFRO Show",
        DominantCollectionName = "AFRO Show",
        YearHint = 2004,
        DateOrder = "US",
        FileCount = 10
    };
    var parsed = parser.Parse(input, context);
    Equal(new DateOnly(2004, 12, 28), parsed.AirDate!.Value);
    Equal("AFRO Show", parsed.CollectionName);
}

static void LibraryTruthRecognisesBenningtonOr()
{
    var input = TruthInput(@"D:\radio_shows\Bennington\2015-04-24 Bennington OR 64k.m4a", "Bennington");
    var parsed = new LibraryTruthParser().Parse(input, new LibraryTruthFolderContext
    {
        ContextKey = input.DirectoryPath,
        AssignedCollectionName = "Bennington",
        DominantCollectionName = "Bennington",
        DateOrder = "US",
        FileCount = 50
    });
    Equal("OpieRadio Edition", parsed.BroadcastSlot);
    True(parsed.CanonicalBroadcastKey.Contains("OPIERADIO", StringComparison.Ordinal));
}

static void LibraryTruthKeepsParentShowForAfro()
{
    var input = TruthInput(@"D:\Radio\RonFez\Ron and Fez Mini AFRO Show 1-06-05.mp3", "Ron & Fez");
    var parsed = new LibraryTruthParser().Parse(input, new LibraryTruthFolderContext
    {
        ContextKey = input.DirectoryPath,
        AssignedCollectionName = "Ron & Fez",
        DateOrder = "US",
        FileCount = 100
    });
    Equal("Ron & Fez", parsed.CollectionName);
    Equal(new DateOnly(2005, 1, 6), parsed.AirDate);
    True(parsed.Headline.Contains("AFRO", StringComparison.OrdinalIgnoreCase));
    True(parsed.Evidence.Any(x => x.Field == "programme-format" && x.Value == "AFRO Show"));
}

static void LibraryTruthRecoversCompactRafMonthDay()
{
    var input = TruthInput(@"D:\Radio\RonFez\2003\RaF1124-mid-Pt1.mp3", "Ron & Fez");
    var parsed = new LibraryTruthParser().Parse(input, new LibraryTruthFolderContext
    {
        ContextKey = input.DirectoryPath,
        AssignedCollectionName = "Ron & Fez",
        DominantCollectionName = "Ron & Fez",
        YearHint = 2003,
        DateOrder = "US",
        FileCount = 100
    });
    Equal(new DateOnly(2003, 11, 24), parsed.AirDate);
    Equal("Midday", parsed.BroadcastSlot);
    Equal(1, parsed.PartNumber);
}


static void LibraryTruthLearnsYearFromLabelledFolder()
{
    var input = new LibraryTruthFileInput
    {
        MediaFileId = 301,
        CurrentEpisodeId = 301,
        Path = @"D:\radio_shows\Ron & Fez Archive\Ron & Fez 2003\RaF1003-Part2.mp3",
        OriginalFilename = "RaF1003-Part2.mp3",
        CurrentCollectionName = "Ron & Fez",
        AssignedCollectionName = "Ron & Fez",
        LibraryRoot = @"D:\radio_shows\Ron & Fez Archive",
        CurrentPartNumber = 1
    };
    var contexts = new LibraryTruthContextAnalyzer().Analyse(new[] { input });
    var context = contexts[LibraryTruthContextAnalyzer.ContextKey(input)];
    Equal(2003, context.YearHint ?? -1);
    var parsed = new LibraryTruthParser().Parse(input, context);
    Equal(new DateOnly(2003, 10, 3), parsed.AirDate);
    Equal(2, parsed.PartNumber);
}

static void LibraryTruthParsesIndexedNamedMonthDates()
{
    var parser = new LibraryTruthParser();
    var input = TruthInput(@"D:\Radio\Ron & Fez 2009\36 _1st Oct, 2009(1).m4a", "Ron & Fez");
    var parsed = parser.Parse(input, new LibraryTruthFolderContext
    {
        ContextKey = input.DirectoryPath,
        AssignedCollectionName = "Ron & Fez",
        DateOrder = "US",
        FileCount = 100
    });
    Equal(new DateOnly(2009, 10, 1), parsed.AirDate);
    True(string.IsNullOrWhiteSpace(parsed.Headline));
}

static void LibraryTruthDoesNotConfuseSourceIndexWithYear()
{
    var parser = new LibraryTruthParser();
    var input = TruthInput(@"D:\Radio\Ron & Fez 2010\27 March 16, 2010.m4a", "Ron & Fez");
    var parsed = parser.Parse(input, new LibraryTruthFolderContext
    {
        ContextKey = input.DirectoryPath,
        AssignedCollectionName = "Ron & Fez",
        YearHint = 2010,
        DateOrder = "US",
        FileCount = 100
    });
    Equal(new DateOnly(2010, 3, 16), parsed.AirDate);
    Equal(1, parsed.PartNumber);
}

static void LibraryTruthRecognisesAdditionalMultipartMarkers()
{
    var parser = new LibraryTruthParser();
    var context = new LibraryTruthFolderContext
    {
        ContextKey = @"D:\Radio\RonFez",
        AssignedCollectionName = "Ron & Fez",
        DateOrder = "US",
        FileCount = 100
    };

    var compact = parser.Parse(TruthInput(@"D:\Radio\RonFez\Ron and Fez_ 2009-01-08-P2.mp3", "Ron & Fez"), context);
    Equal(2, compact.PartNumber);
    True(string.IsNullOrWhiteSpace(compact.Headline));

    var trailing = parser.Parse(TruthInput(@"D:\Radio\RonFez\Ron and Fez_ 12_20_2011.2.mp3", "Ron & Fez"), context);
    Equal(1, trailing.PartNumber);

    var alternateTake = parser.Parse(TruthInput(@"D:\Radio\RonFez\R&F-07-16-2002 - take 2.mp3", "Ron & Fez"), context);
    Equal(1, alternateTake.PartNumber);
}


static void LibraryTruthProtectsTrackAndVersionNumbers()
{
    var parser = new LibraryTruthParser();
    var context = new LibraryTruthFolderContext
    {
        ContextKey = @"D:\Radio\RonFez",
        AssignedCollectionName = "Ron & Fez",
        DateOrder = "US",
        FileCount = 100
    };

    var track = parser.Parse(TruthInput(@"D:\Radio\RonFez\09 17th, Jan 2008.m4a", "Ron & Fez"), context);
    Equal(1, track.PartNumber);
    True(string.IsNullOrWhiteSpace(track.MultipartKind));

    var version = parser.Parse(TruthInput(@"D:\Radio\RonFez\20011123 R&F 1.mp3", "Ron & Fez"), context);
    Equal(1, version.PartNumber);
    True(string.IsNullOrWhiteSpace(version.MultipartKind));

    var trackStructure = LibraryTruthRecordingStructure.Analyse("09 17th, Jan 2008.m4a", false);
    Equal(LibraryTruthNumericTokenKind.LeadingTrackNumber, trackStructure.NumericTokenKind);
    var versionStructure = LibraryTruthRecordingStructure.Analyse("20011123 R&F 1.mp3", false);
    Equal(LibraryTruthNumericTokenKind.AmbiguousTrailingNumber, versionStructure.NumericTokenKind);
}

static void LibraryTruthKeepsRecordingFamiliesSeparate()
{
    var v1p1 = LibraryTruthRecordingStructure.Analyse("Ron and Fez_ 2002-06-25-V1-P1.mp3", true);
    var v1p2 = LibraryTruthRecordingStructure.Analyse("Ron and Fez_ 2002-06-25-V1-P2.mp3", true);
    var v2p1 = LibraryTruthRecordingStructure.Analyse("Ron and Fez_ 2002-06-25-V2-P1.mp3", true);
    Equal(v1p1.FamilyKey, v1p2.FamilyKey);
    True(!string.Equals(v1p1.FamilyKey, v2p1.FamilyKey, StringComparison.OrdinalIgnoreCase));

    var shortFamily = LibraryTruthRecordingStructure.Analyse("20010927 R&F - Part 1.mp3", true);
    var longFamily = LibraryTruthRecordingStructure.Analyse("Ron and Fez_ 2001-09-27 pt.2.mp3", true);
    True(!string.Equals(shortFamily.FamilyKey, longFamily.FamilyKey, StringComparison.OrdinalIgnoreCase));
}

static void LibraryTruthNormalizesSafeMultipartFamilySuffixes()
{
    var attachedA = LibraryTruthRecordingStructure.Analyse("R&F-10-31-2002a.mp3", true);
    var attachedB = LibraryTruthRecordingStructure.Analyse("R&F-10-31-2002b.mp3", true);
    Equal(attachedA.FamilyKey, attachedB.FamilyKey);

    var annotatedPart = LibraryTruthRecordingStructure.Analyse("RaF1020-Part2-ph.mp3", true);
    var plainPart = LibraryTruthRecordingStructure.Analyse("RaF1020-Part3.mp3", true);
    Equal(annotatedPart.FamilyKey, plainPart.FamilyKey);

    var partialPart = LibraryTruthRecordingStructure.Analyse("RaF1110-Part3-partial.mp3", true);
    var siblingPart = LibraryTruthRecordingStructure.Analyse("RaF1110-Part4.mp3", true);
    Equal(partialPart.FamilyKey, siblingPart.FamilyKey);

    var fnrPart = LibraryTruthRecordingStructure.Analyse("Ron and Fez_ 2002-08-23-Part1-FNR.mp3", true);
    var standardPart = LibraryTruthRecordingStructure.Analyse("Ron and Fez_ 2002-08-23-Part2.mp3", true);
    True(!string.Equals(fnrPart.FamilyKey, standardPart.FamilyKey, StringComparison.OrdinalIgnoreCase));
    True(fnrPart.ProgrammeTokens.Contains("fnr"));
}

static void LibraryTruthReassemblesAnnotatedMultipartFamilies()
{
    var hour = (long)TimeSpan.FromHours(1).TotalMilliseconds;
    RunLibraryTruthScenario("Ron & Fez", new[]
    {
        ("R&F-10-31-2002a.mp3", "2002-10-31", "", 2 * hour, "AB-A"),
        ("R&F-10-31-2002b.mp3", "2002-10-31", "", 1 * hour, "AB-B")
    }, engine =>
    {
        var recordings = engine.GetRecordings();
        if (recordings.Count != 1)
            throw new InvalidOperationException("Expected one PH multipart recording: " +
                string.Join(" | ", recordings.Select(x => $"{x.RecordingKey}; segments={x.SegmentCount}; role={x.Role}")));
        var recording = recordings[0];
        Equal(2, recording.SegmentCount);
        Equal(3 * hour, recording.DurationMs);
        Equal("Complete multipart recording", recording.Role);
    });

    RunLibraryTruthScenario("Ron & Fez", new[]
    {
        ("Ron and Fez_ 2003-10-20-Part2-ph.mp3", "2003-10-20", "", 20 * 60_000L, "PH-2"),
        ("Ron and Fez_ 2003-10-20-Part3.mp3", "2003-10-20", "", 20 * 60_000L, "PH-3"),
        ("Ron and Fez_ 2003-10-20-Part4.mp3", "2003-10-20", "", 20 * 60_000L, "PH-4"),
        ("Ron and Fez_ 2003-10-20-Part5.mp3", "2003-10-20", "", 20 * 60_000L, "PH-5"),
        ("Ron and Fez_ 2003-10-20-Part6-of-6.mp3", "2003-10-20", "", 20 * 60_000L, "PH-6")
    }, engine =>
    {
        var recordings = engine.GetRecordings();
        if (recordings.Count != 1)
            throw new InvalidOperationException("Expected one PH multipart recording: " +
                string.Join(" | ", recordings.Select(x => $"{x.RecordingKey}; segments={x.SegmentCount}; role={x.Role}")));
        var recording = recordings[0];
        Equal(5, recording.SegmentCount);
        Equal("Incomplete multipart recording", recording.Role);
    });

    RunLibraryTruthScenario("Ron & Fez", new[]
    {
        ("Ron and Fez_ 2003-11-10-Part1.mp3", "2003-11-10", "", 18 * 60_000L, "PARTIAL-1"),
        ("Ron and Fez_ 2003-11-10-Part2.mp3", "2003-11-10", "", 18 * 60_000L, "PARTIAL-2"),
        ("Ron and Fez_ 2003-11-10-Part3-partial.mp3", "2003-11-10", "", 18 * 60_000L, "PARTIAL-3"),
        ("Ron and Fez_ 2003-11-10-Part4-of-4.mp3", "2003-11-10", "", 18 * 60_000L, "PARTIAL-4")
    }, engine =>
    {
        var recording = engine.GetRecordings().Single();
        Equal(4, recording.SegmentCount);
        Equal("Complete multipart recording", recording.Role);
    });
}

static void LibraryTruthGroupsRomanMultipart()
{
    var parser = new LibraryTruthParser();
    var first = TruthInput(@"D:\radio_shows\Ron & Fez Archive\R&F-2015-02-27 I.mp3", "Ron & Fez", 1);
    var second = TruthInput(@"D:\radio_shows\Ron & Fez Archive\R&F-2015-02-27 II.mp3", "Ron & Fez", 2);
    var context = new LibraryTruthFolderContext { ContextKey = first.DirectoryPath, AssignedCollectionName = "Ron & Fez", DominantCollectionName = "Ron & Fez", DateOrder = "US", FileCount = 100 };
    var parsedFirst = parser.Parse(first, context);
    var parsedSecond = parser.Parse(second, context);
    Equal(1, parsedFirst.PartNumber);
    Equal(2, parsedSecond.PartNumber);
    Equal(parsedFirst.CanonicalBroadcastKey, parsedSecond.CanonicalBroadcastKey);
}

static void LibraryTruthPreservesUnknownDate()
{
    var input = TruthInput(@"D:\radio_shows\Ron & Fez Archive\Ron & Zero Fez Thunderdome.mp3", "Ron & Fez");
    var parsed = new LibraryTruthParser().Parse(input, new LibraryTruthFolderContext
    {
        ContextKey = input.DirectoryPath,
        AssignedCollectionName = "Ron & Fez",
        DominantCollectionName = "Ron & Fez",
        DateOrder = "US",
        FileCount = 100
    });
    True(parsed.AirDate is null);
    True(parsed.Warnings.Any(x => x.Code == "unknown-date"));
}

static LibraryTruthFileInput TruthInput(string path, string assignedCollection, long id = 1)
    => new()
    {
        MediaFileId = id,
        CurrentEpisodeId = id,
        Path = path,
        OriginalFilename = Path.GetFileName(path),
        CurrentCollectionName = assignedCollection,
        AssignedCollectionName = assignedCollection,
        LibraryRoot = Path.GetDirectoryName(path) ?? string.Empty,
        CurrentPartNumber = 1
    };

static void LibraryTruthGroupsExactUnknownCopies()
{
    var parser = new LibraryTruthParser();
    // LibraryTruthFileInput is a class, so use explicit objects to carry identical full hashes.
    var inputA = new LibraryTruthFileInput
    {
        MediaFileId = 201,
        CurrentEpisodeId = 201,
        Path = @"D:\Radio\Mystery\unknown-a.mp3",
        OriginalFilename = "unknown-a.mp3",
        FullHash = "ABCDEF0123456789",
        AssignedCollectionName = "Ron & Fez",
        CurrentCollectionName = "Ron & Fez",
        CurrentPartNumber = 1
    };
    var inputB = new LibraryTruthFileInput
    {
        MediaFileId = 202,
        CurrentEpisodeId = 202,
        Path = @"D:\Radio\Mystery\unknown-b.mp3",
        OriginalFilename = "unknown-b.mp3",
        FullHash = "ABCDEF0123456789",
        AssignedCollectionName = "Ron & Fez",
        CurrentCollectionName = "Ron & Fez",
        CurrentPartNumber = 1
    };
    var context = new LibraryTruthFolderContext { ContextKey = inputA.DirectoryPath, AssignedCollectionName = "Ron & Fez", DateOrder = "US", FileCount = 2 };
    var parsedA = parser.Parse(inputA, context);
    var parsedB = parser.Parse(inputB, context);
    Equal(parsedA.CanonicalBroadcastKey, parsedB.CanonicalBroadcastKey);
    True(parsedA.AirDate is null && parsedB.AirDate is null);
}

static void LibraryTruthShadowIndexSeparatesLayers()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\RonFez',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,part_number,total_parts,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2015-02-27','High','Part I','Unplayed',$now,$now,'',1,NULL,'CURRENT-A');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,part_number,total_parts,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2015-02-27','High','Part II','Unplayed',$now,$now,'',1,NULL,'CURRENT-B');
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='CURRENT-A'),'D:\Radio\RonFez\Ron and Fez 2-27-2015 I.mp3','Ron and Fez 2-27-2015 I.mp3',1000,$now,0,$now,3600000,'PART-A','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='CURRENT-B'),'D:\Radio\RonFez\Ron and Fez 2-27-2015 II.mp3','Ron and Fez 2-27-2015 II.mp3',1100,$now,0,$now,3500000,'PART-B','AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        var engine = new LibraryTruthEngine(database);
        var run = engine.BuildShadowIndex().Summary;
        Equal(2, run.PhysicalFiles);
        Equal(1, run.ProposedBroadcasts);
        Equal(1, run.MultipartBroadcasts);
        Equal(1, run.MergeGroups);
        var recordings = engine.GetRecordings();
        Equal(1, recordings.Count);
        Equal(2, recordings[0].SegmentCount);
        Equal(2, recordings[0].FileCount);
        var files = engine.GetFiles();
        Equal(2, files.Count);
        True(files.All(x => !string.IsNullOrWhiteSpace(x.RecordingKey)));
        True(files.All(x => x.Evidence.Contains("show:", StringComparison.OrdinalIgnoreCase)));
        True(files.All(x => x.Warnings == "No warnings."));
        Equal(1, files.Select(x => x.RecordingKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}


static void LibraryTruthTreatsAlternateRecordingsAsNormal()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth-alternates.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\RonFez',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2001-08-08','High','Capture A','Unplayed',$now,$now,'ALT-A');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2001-08-08','High','Capture B','Unplayed',$now,$now,'ALT-B');
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALT-A'),'D:\Radio\RonFez\20010808 R&F.mp3','20010808 R&F.mp3',1000,$now,0,$now,3600000,'PARTIAL-A','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALT-B'),'D:\Radio\RonFez\Ron and Fez_ 2001-08-08.mp3','Ron and Fez_ 2001-08-08.mp3',1100,$now,0,$now,3599000,'PARTIAL-B','AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        var engine = new LibraryTruthEngine(database);
        var summary = engine.BuildShadowIndex().Summary;
        Equal(1, summary.ProposedBroadcasts);
        Equal(0, summary.NeedsReview);
        Equal(1, summary.MergeGroups);
        var broadcasts = engine.GetBroadcasts();
        Equal("Proposed changes", broadcasts.Single().Status);
        Equal(2, broadcasts.Single().RecordingCount);
        Equal(0, engine.GetFiles("needs-attention").Count);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void LibraryTruthKeepsConflictingDatesSeparate()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth-conflict.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\RonFez',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2002-04-24','High','First claim','Unplayed',$now,$now,'CLAIM-A');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2001-04-24','High','Second claim','Unplayed',$now,$now,'CLAIM-B');
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,full_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='CLAIM-A'),'D:\Radio\RonFez\R&F 04-24-2002.mp3','R&F 04-24-2002.mp3',1000,$now,0,$now,3600000,'PARTIAL-SAME','FULL-SAME','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,full_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='CLAIM-B'),'D:\Radio\RonFez\R&F 04-24-2001.mp3','R&F 04-24-2001.mp3',1000,$now,0,$now,3600000,'PARTIAL-SAME','FULL-SAME','AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        var engine = new LibraryTruthEngine(database);
        var summary = engine.BuildShadowIndex().Summary;
        Equal(2, summary.ProposedBroadcasts);
        Equal(2, summary.NeedsReview);
        var files = engine.GetFiles("needs-attention");
        Equal(2, files.Count);
        True(files.All(x => x.Warnings.Contains("conflicting", StringComparison.OrdinalIgnoreCase)));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void LibraryTruthSeparatesFullAndMultipartRecordings()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth-full-multipart.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\RonFez',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2003-10-23','High','Full','Unplayed',$now,$now,'FULL');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2003-10-23','High','Part 1','Unplayed',$now,$now,'P1');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2003-10-23','High','Part 2','Unplayed',$now,$now,'P2');
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='FULL'),'D:\Radio\RonFez\Ron and Fez_ Oct 23 2003.mp3','Ron and Fez_ Oct 23 2003.mp3',4000,$now,0,$now,14400000,'FULL-CAPTURE','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='P1'),'D:\Radio\RonFez\2003\RaF1023-Part1.mp3','RaF1023-Part1.mp3',2000,$now,0,$now,7200000,'SEGMENT-1','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='P2'),'D:\Radio\RonFez\2003\RaF1023-Part2of2.mp3','RaF1023-Part2of2.mp3',2100,$now,0,$now,7200000,'SEGMENT-2','AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        var recordings = engine.GetRecordings();
        Equal(2, recordings.Count);
        True(recordings.Any(x => x.Role == "Complete multipart recording" && x.SegmentCount == 2));
        True(recordings.Any(x => x.Role == "Complete alternate recording" && x.SegmentCount == 1));
        Equal(1, recordings.Count(x => x.IsPreferredCandidate));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void LibraryTruthClassifiesTruncatedRecording()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth-truncated.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\RonFez',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2002-03-28','High','Complete','Unplayed',$now,$now,'COMPLETE');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2002-03-28','High','Tiny','Unplayed',$now,$now,'TINY');
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='COMPLETE'),'D:\Radio\RonFez\Ron and Fez_ 2002-03-28-V1.mp3','Ron and Fez_ 2002-03-28-V1.mp3',4000,$now,0,$now,14400000,'LONG','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='TINY'),'D:\Radio\RonFez\Ron and Fez_ 2002-03-28-V2.mp3','Ron and Fez_ 2002-03-28-V2.mp3',8,$now,0,$now,8900,'TINY','AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        var recordings = engine.GetRecordings();
        var truncated = recordings.Single(x => x.Role == "Likely truncated or damaged");
        True(!truncated.IsPreferredCandidate);
        True(recordings.Single(x => x.IsPreferredCandidate).DurationMs > truncated.DurationMs);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void LibraryTruthComparesMultipartCoverage()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth-multipart-coverage.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\RonFez',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2003-10-24','High','Full','Unplayed',$now,$now,'FULL-COVERAGE');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2003-10-24','High','Part 1','Unplayed',$now,$now,'COVERAGE-P1');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2003-10-24','High','Part 2','Unplayed',$now,$now,'COVERAGE-P2');
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='FULL-COVERAGE'),'D:\Radio\RonFez\Ron and Fez_ 2003-10-24 full.mp3','Ron and Fez_ 2003-10-24 full.mp3',4000,$now,0,$now,14400000,'COVERAGE-FULL','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='COVERAGE-P1'),'D:\Radio\RonFez\Ron and Fez_ 2003-10-24 Part1of2.mp3','Ron and Fez_ 2003-10-24 Part1of2.mp3',1000,$now,0,$now,1800000,'COVERAGE-1','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='COVERAGE-P2'),'D:\Radio\RonFez\Ron and Fez_ 2003-10-24 Part2of2.mp3','Ron and Fez_ 2003-10-24 Part2of2.mp3',1000,$now,0,$now,1800000,'COVERAGE-2','AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        var recordings = engine.GetRecordings();
        var multipart = recordings.Single(x => x.SegmentCount == 2);
        Equal("Partial multipart recording", multipart.Role);
        True(multipart.DurationRatio < 0.30);
        True(!multipart.IsPreferredCandidate);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void LibraryTruthFlagsSuspiciousMerge()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth-suspicious-merge.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\RonFez',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2001-08-23','High','Long','Unplayed',$now,$now,'MERGE-LONG');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2001-08-23','High','Short','Unplayed',$now,$now,'MERGE-SHORT');
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='MERGE-LONG'),'D:\Radio\RonFez\Ron and Fez_ 2001-08-23 source long.mp3','Ron and Fez_ 2001-08-23 source long.mp3',5000,$now,0,$now,18000000,'MERGE-LONG-HASH','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='MERGE-SHORT'),'D:\Radio\RonFez\Ron and Fez_ 2001-08-23 source short.mp3','Ron and Fez_ 2001-08-23 source short.mp3',2000,$now,0,$now,5400000,'MERGE-SHORT-HASH','AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        var broadcast = engine.GetBroadcasts("suspicious-merges").Single();
        True(broadcast.SuspiciousMerge);
        Equal("Review recommended", broadcast.AdoptionState);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void LibraryTruthPropagatesStrongCrossDateConflict()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth-strong-conflict.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\RonFez',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2012-11-22','High','Claim A','Unplayed',$now,$now,'CLAIM-A');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2012-11-23','High','Claim B','Unplayed',$now,$now,'CLAIM-B');
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='CLAIM-A'),'D:\Radio\RonFez\Ron and Fez_ 11_22_2012.mp3','Ron and Fez_ 11_22_2012.mp3',204081569,$now,0,$now,12749783,'SAME-PARTIAL','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='CLAIM-B'),'D:\Radio\RonFez\Ron and Fez_ 11_23_2012.mp3','Ron and Fez_ 11_23_2012.mp3',204081569,$now,0,$now,12749783,'SAME-PARTIAL','AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        Equal(1, engine.GetConflicts().Count);
        True(engine.GetConflicts()[0].ConflictType.Contains("Strong audio", StringComparison.OrdinalIgnoreCase));
        Equal(2, engine.GetBroadcasts("blocked").Count);
        True(engine.GetFiles("needs-attention").All(x => x.Warnings.Contains("conflicting", StringComparison.OrdinalIgnoreCase)));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void LibraryTruthProducesAdoptionAudit()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth-adoption.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Bennington','Bennington');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\Bennington',(SELECT id FROM collections WHERE name='Bennington'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Bennington'),'2016-05-17','High','Show','Unplayed',$now,$now,'READY');
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='READY'),'D:\Radio\Bennington\Bennington 2016-05-17.mp3','Bennington 2016-05-17.mp3',1000,$now,0,$now,10800000,'READY-PARTIAL','AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        Equal(1, engine.GetAdoptionSummary().AdoptionReadyTotal);
        Equal(1, engine.GetYears().Single(x => x.Year == "2016").ProposedBroadcasts);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}



static void LibraryTruthAssemblesVariantFamiliesSafely()
{
    var hour = (long)TimeSpan.FromHours(1).TotalMilliseconds;
    RunLibraryTruthScenario("Ron & Fez", new[]
    {
        ("Ron and Fez_ 2002-06-25-V1-P1.mp3", "2002-06-25", "", 4 * hour, "V1-P1"),
        ("Ron and Fez_ 2002-06-25-V1-P2.mp3", "2002-06-25", "", 1 * hour, "V1-P2"),
        ("Ron and Fez_ 2002-06-25-V2-P1.mp3", "2002-06-25", "", 2 * hour, "V2-P1"),
        ("Ron and Fez_ 2002-06-25-V2-P2.mp3", "2002-06-25", "", 3 * hour, "V2-P2")
    }, engine =>
    {
        var recordings = engine.GetRecordings();
        Equal(2, recordings.Count);
        True(recordings.All(x => x.SegmentCount == 2));
        True(recordings.All(x => x.DurationMs == 5 * hour));
        Equal("Ready with recording choice", engine.GetBroadcasts().Single().AdoptionState);
    });
}

static void LibraryTruthPromotesBareMultipartSequence()
{
    RunLibraryTruthScenario("Ron & Fez", new[]
    {
        ("20011122 R&F 1.mp3", "2001-11-22", "", (long)TimeSpan.FromHours(3.9).TotalMilliseconds, "BARE-1"),
        ("20011122 R&F 2.mp3", "2001-11-22", "", (long)TimeSpan.FromHours(3.8).TotalMilliseconds, "BARE-2")
    }, engine =>
    {
        var recording = engine.GetRecordings().Single();
        Equal(2, recording.SegmentCount);
        Equal("Complete multipart recording", recording.Role);
        True(recording.Evidence.Contains("contiguous 1..N sequence", StringComparison.OrdinalIgnoreCase));

        var files = engine.GetFiles().OrderBy(x => x.Filename, StringComparer.OrdinalIgnoreCase).ToArray();
        Equal("Part 1 of 2", files[0].ProposedPart);
        Equal("Part 2 of 2", files[1].ProposedPart);
        True(files[1].Evidence.Contains("filename-family", StringComparison.OrdinalIgnoreCase));
    });
}

static void LibraryTruthFlagsProgrammeSpecificDurationClusters()
{
    var longDuration = (long)TimeSpan.FromHours(4.237).TotalMilliseconds;
    var shortDuration = (long)TimeSpan.FromHours(3.093).TotalMilliseconds;
    RunLibraryTruthScenario("Ron & Fez", new[]
    {
        ("R&F-08-23-2002.mp3", "2002-08-23", "", longDuration, "LONG-A"),
        ("Ron and Fez_ Aug 23 2002.mp3", "2002-08-23", "", longDuration, "LONG-B"),
        ("FNR-08-23-2002.mp3", "2002-08-23", "", shortDuration, "FNR-A"),
        ("Ron and Fez_ Aug 23 2002 Eddie Trunk.mp3", "2002-08-23", "", shortDuration, "FNR-B"),
        ("Ron and Fez_ Aug 23 2002 FNR Show.mp3", "2002-08-23", "", shortDuration, "FNR-C")
    }, engine =>
    {
        var broadcast = engine.GetBroadcasts().Single();
        True(broadcast.SuspiciousMerge);
        Equal("Review recommended", broadcast.AdoptionState);
        True(broadcast.AdoptionReason.Contains("duration families", StringComparison.OrdinalIgnoreCase));
    });
}

static void LibraryTruthDetectsCompositeCoverage()
{
    var am = (long)TimeSpan.FromHours(3.886).TotalMilliseconds;
    var pm = (long)TimeSpan.FromHours(3.877).TotalMilliseconds;
    RunLibraryTruthScenario("Ron & Fez", new[]
    {
        ("20011122 R&F 1.mp3", "2001-11-22", "", am, "COMPOSITE-1"),
        ("20011122 R&F 2.mp3", "2001-11-22", "", pm, "COMPOSITE-2"),
        ("Ron and Fez_ Nov 22 2001 AM.mp3", "2001-11-22", "Morning show", am, "AM"),
        ("Ron and Fez_ Nov 22 2001 PM.mp3", "2001-11-22", "Evening show", pm, "PM")
    }, engine =>
    {
        var standard = engine.GetBroadcasts().Single(x => x.BroadcastSlot == "Standard");
        Equal("Review recommended", standard.AdoptionState);
        True(standard.AdoptionReason.Contains("combined", StringComparison.OrdinalIgnoreCase));
        True(engine.GetBroadcasts().Where(x => x.BroadcastSlot != "Standard").All(x => x.AdoptionState == "Ready"));
        var inferred = engine.GetCoverages(reviewOnly: true).OrderBy(x => x.SegmentNumber).ToArray();
        Equal(2, inferred.Length);
        True(inferred.All(x => x.CoverageKind == "Composite slot coverage"));
        Equal(0L, inferred[0].StartOffsetMs);
        Equal(inferred[0].EndOffsetMs, inferred[1].StartOffsetMs);
        True(inferred.All(x => x.SourceBroadcastKey.Contains("|STANDARD", StringComparison.OrdinalIgnoreCase)));
        True(inferred.Select(x => x.TargetBroadcastKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2);
    });
}

static void LibraryTruthDetectsCrossSlotEquivalent()
{
    var duration = (long)TimeSpan.FromHours(3.971).TotalMilliseconds;
    RunLibraryTruthScenario("Ron & Fez", new[]
    {
        ("R&F-10-23-2002.mp3", "2002-10-23", "", duration, "STANDARD"),
        ("Ron and Fez_ Oct 23 2002 AM.mp3", "2002-10-23", "Morning show", duration, "AM")
    }, engine =>
    {
        var standard = engine.GetBroadcasts().Single(x => x.BroadcastSlot == "Standard");
        Equal("Review recommended", standard.AdoptionState);
        True(standard.AdoptionReason.Contains("alternate encode", StringComparison.OrdinalIgnoreCase),
            $"Standard adoption reason was: {standard.AdoptionReason}; coverages: " +
            string.Join(" | ", engine.GetCoverages().Select(x => $"{x.CoverageKind}:{x.SourceBroadcastKey}->{x.TargetBroadcastKey}")));
        var inferred = engine.GetCoverages(reviewOnly: true).Single();
        Equal("Same-date equivalent", inferred.CoverageKind);
        True(inferred.TargetBroadcastKey.Contains("|AM", StringComparison.OrdinalIgnoreCase),
            $"Equivalent target was {inferred.TargetBroadcastKey}");
    });
}

static void LibraryTruthPersistsDirectSegmentCoverage()
{
    RunLibraryTruthScenario("Ron & Fez", new[]
    {
        ("R&F-10-31-2002a.mp3", "2002-10-31", "", (long)TimeSpan.FromHours(2).TotalMilliseconds, "COVERAGE-A"),
        ("R&F-10-31-2002b.mp3", "2002-10-31", "", (long)TimeSpan.FromHours(1.8).TotalMilliseconds, "COVERAGE-B")
    }, engine =>
    {
        var direct = engine.GetCoverages().OrderBy(x => x.SegmentNumber).ToArray();
        Equal(2, direct.Length);
        Equal(1, direct[0].SegmentNumber);
        Equal(2, direct[1].SegmentNumber);
        Equal((int?)2, direct[0].SegmentTotal);
        Equal(direct[0].EndOffsetMs, direct[1].StartOffsetMs);
        True(direct.All(x => x.SourceBroadcastKey == x.TargetBroadcastKey));
        True(direct.All(x => !x.RequiresReview));
    });
}

static void LibraryTruthPreparesGuardedAdoptionPreview()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth-alpha6-adoption.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron and Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\Alpha6',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2005-05-12','High','First','Unplayed',$now,$now,'ALPHA6-A');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2005-05-12','High','Second','Unplayed',$now,$now,'ALPHA6-B');
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA6-A'),'D:\Radio\Alpha6\a.mp3','R&F 05-12-2005.mp3',1000,$now,0,$now,10800000,'ALPHA6-ONE','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA6-B'),'D:\Radio\Alpha6\b.mp3','Ron and Fez_ 2005-05-12.mp3',1200,$now,0,$now,10810000,'ALPHA6-TWO','AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        var preview = engine.GetAdoptionPreviews().Single();
        True(preview.EligibleForGuardedAdoption,
            $"Adoption held: state={preview.AdoptionState}; reason={preview.GuardReason}; action={preview.PlannedAction}");
        Equal(2, preview.CurrentEpisodeCount);
        Equal(1, preview.RetireEpisodeCount);
        Equal(1, preview.ReassignFileCount);
        True(preview.ProvisionalEpisodeId.HasValue, $"No provisional survivor: {preview.GuardReason}");
        True(preview.GuardReason.Contains("rollback-verified", StringComparison.OrdinalIgnoreCase),
            $"Unexpected adoption guard: {preview.GuardReason}");
        var summary = engine.GetAdoptionPlanSummary();
        Equal(1, summary.EligibleBroadcasts);
        Equal(1, summary.LiveEpisodeRowsToConsolidate);

        using var verify = database.OpenConnection();
        using var count = verify.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM episodes";
        Equal(2L, Convert.ToInt64(count.ExecuteScalar()));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void LibraryTruthRehearsalRollsBackDisposableClone()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    var backupDirectory = Path.Combine(directory, "Backups");
    Directory.CreateDirectory(backupDirectory);
    var databasePath = Path.Combine(directory, "truth-alpha7-rehearsal.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        long firstEpisode;
        long secondEpisode;
        using (var connection = database.OpenConnection())
        {
            using var setup = connection.CreateCommand();
            setup.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\Alpha7',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,broadcast_uid,favourite)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2005-05-12','High','First capture','In Progress',$now,$now,'Standard','ALPHA7-A',0);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,broadcast_uid,favourite)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2005-05-12','High','Second capture','Completed',$now,$now,'Standard','ALPHA7-B',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA7-A'),'D:\Radio\Alpha7\a.mp3','R&F 05-12-2005.mp3',1000,$now,0,$now,10800000,'ALPHA7-ONE','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA7-B'),'D:\Radio\Alpha7\b.mp3','Ron and Fez_ 2005-05-12.mp3',1200,$now,0,$now,10810000,'ALPHA7-TWO','AvailableOffline',1);
                INSERT INTO playback_state(episode_id,position_ms,completed,last_played_at,play_count,duration_ms,playback_speed,completed_at,first_played_at,completion_count)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA7-A'),5000,0,$now,1,10800000,1.0,NULL,$now,0);
                INSERT INTO playback_state(episode_id,position_ms,completed,last_played_at,play_count,duration_ms,playback_speed,completed_at,first_played_at,completion_count)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA7-B'),10810000,1,$now,2,10810000,1.25,$now,$now,1);
                INSERT INTO moments(episode_id,position_ms,title,notes,created_at)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA7-B'),60000,'Moment','Preserve me',$now);
                """;
            setup.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            setup.ExecuteNonQuery();

            using var ids = connection.CreateCommand();
            ids.CommandText = "SELECT id FROM episodes ORDER BY id";
            using var reader = ids.ExecuteReader();
            reader.Read();
            firstEpisode = reader.GetInt64(0);
            reader.Read();
            secondEpisode = reader.GetInt64(0);
        }

        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        var rehearsal = new LibraryTruthAdoptionRehearsalService(database);
        var summary = rehearsal.Run(backupDirectory);

        True(summary.RollbackVerified);
        Equal("ok", summary.IntegrityCheck);
        Equal("ok", summary.BackupRestoreCheck);
        Equal(1, summary.EligibleBroadcasts);
        Equal(1, summary.FileReassignments);
        Equal(1, summary.AliasRowsRetired);
        True(File.Exists(summary.BackupPath));
        Equal(summary.SourceFingerprint, summary.RollbackFingerprint);
        Equal(1, rehearsal.GetLatestItems().Count);

        using var verify = database.OpenConnection();
        using var episodes = verify.CreateCommand();
        episodes.CommandText = "SELECT COUNT(*),SUM(hidden),SUM(favourite) FROM episodes";
        using var episodeReader = episodes.ExecuteReader();
        episodeReader.Read();
        Equal(2L, episodeReader.GetInt64(0));
        Equal(0L, episodeReader.GetInt64(1));
        Equal(1L, episodeReader.GetInt64(2));

        using var media = verify.CreateCommand();
        media.CommandText = "SELECT episode_id FROM media_files ORDER BY id";
        using var mediaReader = media.ExecuteReader();
        mediaReader.Read();
        Equal(firstEpisode, mediaReader.GetInt64(0));
        mediaReader.Read();
        Equal(secondEpisode, mediaReader.GetInt64(0));

        using var state = verify.CreateCommand();
        state.CommandText = "SELECT COUNT(*) FROM playback_state";
        Equal(2L, Convert.ToInt64(state.ExecuteScalar()));
        using var moments = verify.CreateCommand();
        moments.CommandText = "SELECT episode_id FROM moments";
        Equal(secondEpisode, Convert.ToInt64(moments.ExecuteScalar()));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void LibraryTruthGuardedAdoptionCommitsVerifiedPlan()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    var backupDirectory = Path.Combine(directory, "Backups");
    Directory.CreateDirectory(backupDirectory);
    var databasePath = Path.Combine(directory, "truth-alpha10-adoption.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var setup = connection.CreateCommand();
            setup.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\Alpha10',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,broadcast_uid,favourite)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2005-05-12','High','First capture','In Progress',$now,$now,'Standard','ALPHA10-A',0);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,broadcast_uid,favourite)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2005-05-12','High','Second capture','Completed',$now,$now,'Standard','ALPHA10-B',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA10-A'),'D:\Radio\Alpha10\a.mp3','R&F 05-12-2005.mp3',1000,$now,0,$now,10800000,'ALPHA10-ONE','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA10-B'),'D:\Radio\Alpha10\b.mp3','Ron and Fez_ 2005-05-12.mp3',1200,$now,0,$now,10810000,'ALPHA10-TWO','AvailableOffline',1);
                INSERT INTO playback_state(episode_id,position_ms,completed,last_played_at,play_count,duration_ms,playback_speed,completed_at,first_played_at,completion_count)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA10-A'),5000,0,$now,1,10800000,1.0,NULL,$now,0);
                INSERT INTO playback_state(episode_id,position_ms,completed,last_played_at,play_count,duration_ms,playback_speed,completed_at,first_played_at,completion_count)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA10-B'),10810000,1,$now,2,10810000,1.25,$now,$now,1);
                INSERT INTO moments(episode_id,position_ms,title,notes,created_at)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA10-B'),60000,'Moment','Preserve me',$now);
                """;
            setup.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            setup.ExecuteNonQuery();
        }

        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        var adoption = new LibraryTruthAdoptionRehearsalService(database);
        var rehearsal = adoption.Run(backupDirectory);
        True(rehearsal.RollbackVerified);
        Equal(64, rehearsal.TruthRunSignature.Length);
        Equal(64, rehearsal.ItemSignature.Length);
        Equal(64, rehearsal.ConflictSignature.Length);

        var eligibility = adoption.GetAdoptionEligibility();
        True(eligibility.CanAdopt);
        Equal(rehearsal.SourceFingerprint, eligibility.CurrentSourceFingerprint);
        Equal(rehearsal.TruthRunSignature, eligibility.ExpectedTruthRunSignature);
        Equal(rehearsal.ItemSignature, eligibility.ExpectedItemSignature);
        Equal(rehearsal.ConflictSignature, eligibility.ExpectedConflictSignature);

        string originalGuardReason;
        using (var shadowTamper = database.OpenConnection())
        {
            using var read = shadowTamper.CreateCommand();
            read.CommandText = "SELECT guard_reason FROM library_truth_adoption_previews WHERE run_id=$run ORDER BY id LIMIT 1";
            read.Parameters.AddWithValue("$run", rehearsal.TruthRunId);
            originalGuardReason = Convert.ToString(read.ExecuteScalar()) ?? string.Empty;
            using var command = shadowTamper.CreateCommand();
            command.CommandText = "UPDATE library_truth_adoption_previews SET guard_reason=guard_reason || ' tampered' WHERE id=(SELECT MIN(id) FROM library_truth_adoption_previews WHERE run_id=$run)";
            command.Parameters.AddWithValue("$run", rehearsal.TruthRunId);
            command.ExecuteNonQuery();
        }
        True(!adoption.GetAdoptionEligibility().CanAdopt);
        using (var shadowRestore = database.OpenConnection())
        {
            using var command = shadowRestore.CreateCommand();
            command.CommandText = "UPDATE library_truth_adoption_previews SET guard_reason=$value WHERE id=(SELECT MIN(id) FROM library_truth_adoption_previews WHERE run_id=$run)";
            command.Parameters.AddWithValue("$value", originalGuardReason);
            command.Parameters.AddWithValue("$run", rehearsal.TruthRunId);
            command.ExecuteNonQuery();
        }
        True(adoption.GetAdoptionEligibility().CanAdopt);

        string originalOutcome;
        using (var ledgerTamper = database.OpenConnection())
        {
            using var read = ledgerTamper.CreateCommand();
            read.CommandText = "SELECT outcome FROM library_truth_rehearsal_items WHERE rehearsal_run_id=$run ORDER BY id LIMIT 1";
            read.Parameters.AddWithValue("$run", rehearsal.Id);
            originalOutcome = Convert.ToString(read.ExecuteScalar()) ?? string.Empty;
            using var command = ledgerTamper.CreateCommand();
            command.CommandText = "UPDATE library_truth_rehearsal_items SET outcome='Tampered' WHERE id=(SELECT MIN(id) FROM library_truth_rehearsal_items WHERE rehearsal_run_id=$run)";
            command.Parameters.AddWithValue("$run", rehearsal.Id);
            command.ExecuteNonQuery();
        }
        True(!adoption.GetAdoptionEligibility().CanAdopt);
        using (var ledgerRestore = database.OpenConnection())
        {
            using var command = ledgerRestore.CreateCommand();
            command.CommandText = "UPDATE library_truth_rehearsal_items SET outcome=$value WHERE id=(SELECT MIN(id) FROM library_truth_rehearsal_items WHERE rehearsal_run_id=$run)";
            command.Parameters.AddWithValue("$value", originalOutcome);
            command.Parameters.AddWithValue("$run", rehearsal.Id);
            command.ExecuteNonQuery();
        }
        True(adoption.GetAdoptionEligibility().CanAdopt);

        if (rehearsal.MetadataConflicts > 0)
        {
            string originalResolution;
            using (var conflictTamper = database.OpenConnection())
            {
                using var read = conflictTamper.CreateCommand();
                read.CommandText = "SELECT resolution FROM library_truth_rehearsal_conflicts WHERE rehearsal_run_id=$run ORDER BY id LIMIT 1";
                read.Parameters.AddWithValue("$run", rehearsal.Id);
                originalResolution = Convert.ToString(read.ExecuteScalar()) ?? string.Empty;
                using var command = conflictTamper.CreateCommand();
                command.CommandText = "UPDATE library_truth_rehearsal_conflicts SET resolution='tampered' WHERE id=(SELECT MIN(id) FROM library_truth_rehearsal_conflicts WHERE rehearsal_run_id=$run)";
                command.Parameters.AddWithValue("$run", rehearsal.Id);
                command.ExecuteNonQuery();
            }
            True(!adoption.GetAdoptionEligibility().CanAdopt);
            using (var conflictRestore = database.OpenConnection())
            {
                using var command = conflictRestore.CreateCommand();
                command.CommandText = "UPDATE library_truth_rehearsal_conflicts SET resolution=$value WHERE id=(SELECT MIN(id) FROM library_truth_rehearsal_conflicts WHERE rehearsal_run_id=$run)";
                command.Parameters.AddWithValue("$value", originalResolution);
                command.Parameters.AddWithValue("$run", rehearsal.Id);
                command.ExecuteNonQuery();
            }
            True(adoption.GetAdoptionEligibility().CanAdopt);
        }

        using (var interrupted = database.OpenConnection())
        {
            using var command = interrupted.CreateCommand();
            command.CommandText = """
                INSERT INTO library_truth_adoption_runs(
                    truth_run_id,rehearsal_run_id,app_version,started_at,status,backup_path,message)
                VALUES($truth,$rehearsal,'test-interrupted',$started,'running','D:\Backups\interrupted.db','Simulated interrupted attempt')
                """;
            command.Parameters.AddWithValue("$truth", rehearsal.TruthRunId);
            command.Parameters.AddWithValue("$rehearsal", rehearsal.Id);
            command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        True(!adoption.GetAdoptionEligibility().CanAdopt);
        using (var removeInterrupted = database.OpenConnection())
        {
            using var command = removeInterrupted.CreateCommand();
            command.CommandText = "DELETE FROM library_truth_adoption_runs WHERE app_version='test-interrupted'";
            command.ExecuteNonQuery();
        }
        True(adoption.GetAdoptionEligibility().CanAdopt);

        using (var changed = database.OpenConnection())
        {
            using var command = changed.CreateCommand();
            command.CommandText = "UPDATE episodes SET title='Changed after rehearsal' WHERE broadcast_uid='ALPHA10-A'";
            command.ExecuteNonQuery();
        }
        True(!adoption.GetAdoptionEligibility().CanAdopt);
        using (var restored = database.OpenConnection())
        {
            using var command = restored.CreateCommand();
            command.CommandText = "UPDATE episodes SET title='First capture' WHERE broadcast_uid='ALPHA10-A'";
            command.ExecuteNonQuery();
        }
        True(adoption.GetAdoptionEligibility().CanAdopt);

        var committed = adoption.AdoptVerifiedPlan(backupDirectory, "test-alpha10");
        Equal("completed", committed.Status);
        True(committed.CommitVerified);
        Equal("ok", committed.IntegrityCheck);
        Equal("ok", committed.BackupRestoreCheck);
        Equal(committed.StagedFingerprint, committed.PostCommitFingerprint);
        Equal(committed.RehearsalTruthSignature, committed.CommitTruthSignature);
        Equal(rehearsal.TruthRunSignature, committed.CommitTruthSignature);
        Equal(64, committed.CommitTruthSignature.Length);
        True(File.Exists(committed.BackupPath));

        using var verify = database.OpenConnection();
        using var structure = verify.CreateCommand();
        structure.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM canonical_broadcasts),
                (SELECT COUNT(*) FROM recordings),
                (SELECT COUNT(*) FROM recording_segments),
                (SELECT COUNT(*) FROM recording_coverages),
                (SELECT COUNT(*) FROM episode_canonical_map),
                (SELECT COUNT(*) FROM library_truth_adoption_items),
                (SELECT COUNT(*) FROM library_truth_adoption_conflicts)
            """;
        using var structureReader = structure.ExecuteReader();
        structureReader.Read();
        Equal((long)committed.CanonicalWrites, structureReader.GetInt64(0));
        Equal((long)committed.RecordingWrites, structureReader.GetInt64(1));
        Equal((long)committed.SegmentWrites, structureReader.GetInt64(2));
        Equal((long)committed.CoverageWrites, structureReader.GetInt64(3));
        Equal(2L, structureReader.GetInt64(4));
        Equal((long)committed.EligibleBroadcasts, structureReader.GetInt64(5));
        Equal((long)committed.MetadataConflicts, structureReader.GetInt64(6));
        structureReader.Close();

        using var survivor = verify.CreateCommand();
        survivor.CommandText = "SELECT survivor_episode_id FROM episode_canonical_map WHERE is_survivor=1";
        var survivorId = Convert.ToInt64(survivor.ExecuteScalar());
        using var live = verify.CreateCommand();
        live.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM media_files WHERE episode_id=$survivor),
                (SELECT COUNT(*) FROM episodes WHERE hidden=1),
                (SELECT COUNT(*) FROM playback_state),
                (SELECT COUNT(*) FROM moments WHERE episode_id=$survivor)
            """;
        live.Parameters.AddWithValue("$survivor", survivorId);
        using var liveReader = live.ExecuteReader();
        liveReader.Read();
        Equal(2L, liveReader.GetInt64(0));
        Equal(1L, liveReader.GetInt64(1));
        Equal(1L, liveReader.GetInt64(2));
        Equal(1L, liveReader.GetInt64(3));
        liveReader.Close();

        True(!adoption.GetAdoptionEligibility().CanAdopt);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}


static void LibraryTruthClassifiesMetadataConflicts()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    var backupDirectory = Path.Combine(directory, "Backups");
    Directory.CreateDirectory(backupDirectory);
    var databasePath = Path.Combine(directory, "truth-alpha8-forensics.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var setup = connection.CreateCommand();
            setup.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\Alpha8',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,broadcast_uid,notes,hosts,metadata_confidence)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2005-05-12','High','Ron & Fez archive broadcast','Unplayed',$now,$now,'Standard','ALPHA8-A','','Ron Bennington',30);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,broadcast_uid,notes,hosts,metadata_confidence)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2005-05-12','High','Billy Staples visits Ron & Fez','Unplayed',$now,$now,'Standard','ALPHA8-B','Detailed researched note','Fez Whatley',85);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA8-A'),'D:\Radio\Alpha8\a.mp3','R&F 05-12-2005.mp3',1000,$now,0,$now,10800000,'ALPHA8-ONE','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA8-B'),'D:\Radio\Alpha8\b.mp3','Ron and Fez_ 2005-05-12.mp3',1200,$now,0,$now,10810000,'ALPHA8-TWO','AvailableOffline',1);
                INSERT INTO research_field_provenance(episode_id,field_name,value_text,source_kind,source_label,confidence,evidence_count,protected,active,created_at)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA8-B'),'headline','Billy Staples visits Ron & Fez','research_pack','Verified pack',95,3,1,1,$now);
                INSERT INTO research_field_provenance(episode_id,field_name,value_text,source_kind,source_label,confidence,evidence_count,protected,active,created_at)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA8-A'),'headline','An unrelated old protected value','manual','Old unmatched edit',100,5,1,1,$now);
                """;
            setup.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            setup.ExecuteNonQuery();
        }

        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        var rehearsal = new LibraryTruthAdoptionRehearsalService(database);
        var summary = rehearsal.Run(backupDirectory);
        var conflicts = rehearsal.GetLatestConflictForensics();

        True(summary.RollbackVerified);
        True(summary.AutoResolvedConflicts >= 3);
        Equal(0, summary.UnresolvedConflicts);
        True(summary.PreservedAlternates >= 2);
        True(conflicts.Any(x => x.FieldName == "title" && x.AutoResolved && x.Classification == "Specific over placeholder"));
        True(conflicts.Where(x => x.FieldName == "title").All(x => !x.Provenance.Contains("Old unmatched edit", StringComparison.Ordinal)));
        True(conflicts.Any(x => x.FieldName == "notes" && x.AutoResolved && x.Classification == "Empty vs populated"));
        True(conflicts.Any(x => x.FieldName == "hosts" && x.AutoResolved && x.Classification == "Mergeable union"));
        True(conflicts.All(x => !string.IsNullOrWhiteSpace(x.CandidateValues)));

        using var verify = database.OpenConnection();
        using var titles = verify.CreateCommand();
        titles.CommandText = "SELECT GROUP_CONCAT(title,'|') FROM episodes ORDER BY id";
        var liveTitles = Convert.ToString(titles.ExecuteScalar()) ?? string.Empty;
        True(liveTitles.Contains("Ron & Fez archive broadcast", StringComparison.Ordinal));
        True(liveTitles.Contains("Billy Staples visits Ron & Fez", StringComparison.Ordinal));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}


static void LibraryTruthRefinesGeneratedMetadataPolicies()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    var backupDirectory = Path.Combine(directory, "Backups");
    Directory.CreateDirectory(backupDirectory);
    var databasePath = Path.Combine(directory, "truth-alpha9-policies.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var setup = connection.CreateCommand();
            setup.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\Alpha9',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(
                    collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,broadcast_uid,
                    artwork_path,edition,broadcast_variant,broadcast_era,metadata_confidence,user_modified)
                VALUES(
                    (SELECT id FROM collections WHERE name='Ron & Fez'),'2003-10-03','High','FriOct032003-pt1','Unplayed',$now,$now,
                    'Evening show','ALPHA9-A','D:\Artwork\old.jpg','Commercial-free','Archive part 2','WJFK Washington era',40,1);
                INSERT INTO episodes(
                    collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,broadcast_uid,
                    artwork_path,edition,broadcast_variant,broadcast_era,metadata_confidence,user_modified)
                VALUES(
                    (SELECT id FROM collections WHERE name='Ron & Fez'),'2003-10-03','High','WJFK evening show — Friday, 3 October 2003','Unplayed',$now,$now,
                    'Evening show','ALPHA9-B','D:\Artwork\survivor.jpg','WJFK-FM (106.7, Washington, D.C.)','Primary archive recording','WJFK Washington/Fairfax era',85,0);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA9-A'),'D:\Radio\Alpha9\a.mp3','Ron and Fez_ 2003-10-03-Part1.mp3',1000,$now,0,$now,5400000,'ALPHA9-ONE','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA9-B'),'D:\Radio\Alpha9\b.mp3','Ron and Fez_ 2003-10-03.mp3',1200,$now,0,$now,10800000,'ALPHA9-TWO','AvailableOffline',1);
                INSERT INTO research_field_provenance(episode_id,field_name,value_text,source_kind,source_label,confidence,evidence_count,protected,active,created_at)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA9-B'),'station','WJFK-FM (106.7, Washington, D.C.)','research_pack','Verified station evidence',95,3,0,1,$now);
                """;
            setup.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            setup.ExecuteNonQuery();
        }

        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        var rehearsal = new LibraryTruthAdoptionRehearsalService(database);
        var summary = rehearsal.Run(backupDirectory);
        var conflicts = rehearsal.GetLatestConflictForensics();

        True(summary.RollbackVerified);
        Equal(0, summary.UnresolvedConflicts);
        if (conflicts.Count == 0)
            throw new InvalidOperationException("No Alpha9 conflict policies were generated. Broadcasts: " +
                string.Join(" | ", engine.GetBroadcasts().Select(x => $"{x.CanonicalKey}:{x.AdoptionState}:{x.AdoptionReason}")) +
                "; previews: " + string.Join(" | ", engine.GetAdoptionPreviews().Select(x => $"{x.CanonicalKey}:{x.GuardReason}")));
        True(conflicts.Any(x => x.FieldName == "title" && x.AutoResolved &&
                                x.Classification == "Descriptive title over filename" &&
                                x.SelectedValue.Contains("WJFK evening show", StringComparison.Ordinal)),
            "Conflict policies: " + string.Join(" | ", conflicts.Select(x => $"{x.FieldName}:{x.Classification}:{x.SelectedValue}:{x.AutoResolved}")));
        True(conflicts.Any(x => x.FieldName == "broadcast_variant" && x.AutoResolved &&
                                x.Classification == "Recording-level variant" && x.SelectedValue == string.Empty));
        True(conflicts.Any(x => x.FieldName == "broadcast_era" && x.AutoResolved &&
                                x.Classification == "Generated era winner"));
        True(conflicts.Any(x => x.FieldName == "artwork_path" && x.AutoResolved &&
                                x.Classification == "Survivor artwork"));
        True(conflicts.Any(x => x.FieldName == "edition" && x.AutoResolved &&
                                x.SelectedValue.Contains("WJFK-FM", StringComparison.Ordinal)));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void RunLibraryTruthScenario(
    string collection,
    IReadOnlyList<(string Filename, string Date, string Slot, long DurationMs, string PartialHash)> files,
    Action<LibraryTruthEngine> assertion)
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth-alpha6.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var setup = connection.CreateCommand();
            setup.CommandText = "INSERT OR IGNORE INTO collections(name,sort_name) VALUES($collection,$collection);" +
                                "INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled) " +
                                "VALUES($root,(SELECT id FROM collections WHERE name=$collection),1,1);";
            setup.Parameters.AddWithValue("$collection", collection);
            setup.Parameters.AddWithValue("$root", @"D:\Radio\Alpha6");
            setup.ExecuteNonQuery();

            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,part_number,total_parts,broadcast_uid)
                    VALUES((SELECT id FROM collections WHERE name=$collection),$date,'High',$title,'Unplayed',$now,$now,$slot,1,NULL,$uid);
                    INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                    VALUES((SELECT id FROM episodes WHERE broadcast_uid=$uid),$path,$filename,$size,$now,0,$now,$duration,$hash,'AvailableOffline',1);
                    """;
                insert.Parameters.AddWithValue("$collection", collection);
                insert.Parameters.AddWithValue("$date", file.Date);
                insert.Parameters.AddWithValue("$title", file.Filename);
                insert.Parameters.AddWithValue("$slot", file.Slot);
                insert.Parameters.AddWithValue("$uid", $"ALPHA6-{index}-{Guid.NewGuid():N}");
                insert.Parameters.AddWithValue("$path", Path.Combine(@"D:\Radio\Alpha6", file.Filename));
                insert.Parameters.AddWithValue("$filename", file.Filename);
                insert.Parameters.AddWithValue("$size", Math.Max(1000, file.DurationMs / 10));
                insert.Parameters.AddWithValue("$duration", file.DurationMs);
                insert.Parameters.AddWithValue("$hash", file.PartialHash);
                insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                insert.ExecuteNonQuery();
            }
        }

        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        assertion(engine);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
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



static void WebApiServesVersionedBroadcastDetails()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
    var port = FindFreePort();
    var token = "test-token";
    using var server = new LocalWebServer(new FakeWebArchiveProvider(), new WebServerOptions
    {
        Port = port, AccessToken = token, AppVersion = "test", ServerInstanceId = "server-test",
        ServerDisplayName = "Test server", DatabaseSchemaVersion = 45, CapabilityGeneration = 14,
        LanFederationEnabled = true
    });
    server.Start();
    using var client = new HttpClient();
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
    var port = FindFreePort();
    var token = "test-token";
    using var server = new LocalWebServer(new FakeWebArchiveProvider(), new WebServerOptions
    {
        Port = port, AccessToken = token, AppVersion = "test", ServerInstanceId = "server-test",
        ServerDisplayName = "Test server", DatabaseSchemaVersion = 45, CapabilityGeneration = 14,
        LanFederationEnabled = true
    });
    server.Start();
    using var client = new HttpClient();
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
    var port = FindFreePort();
    var token = "test-token";
    using var server = new LocalWebServer(new FakeWebArchiveProvider(), new WebServerOptions
    {
        Port = port, AccessToken = token, AppVersion = "test", ServerInstanceId = "server-test",
        ServerDisplayName = "Test server", DatabaseSchemaVersion = 45, CapabilityGeneration = 14,
        LanFederationEnabled = true
    });
    server.Start();
    using var client = new HttpClient();

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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/api/v1/broadcasts/9");
        Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    });
}

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

static void WebApiChangesFavouriteState()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        await client.PostAsJsonAsync($"http://127.0.0.1:{port}/api/v1/broadcasts/9/favourite?token={Uri.EscapeDataString(token)}", new { favourite = false });
        var changes = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"http://127.0.0.1:{port}/api/v1/events?after=0&token={Uri.EscapeDataString(token)}");
        True(changes.GetProperty("count").GetInt32() > 0);
    });
}

static void FederationLibrarySyncExposesResetAndRevision()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var jobs = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"http://127.0.0.1:{port}/api/v1/jobs?token={Uri.EscapeDataString(token)}");
        Equal(1, jobs.GetProperty("count").GetInt32());
    });
}

static void WebApiRequestsJobCancellation()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.PostAsync($"http://127.0.0.1:{port}/api/v1/jobs/{FakeWebArchiveProvider.JobId:D}/cancel?token={Uri.EscapeDataString(token)}", null);
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
    var port = GetFreeTcpPort();
    var token = "test-token-" + Guid.NewGuid().ToString("N");
    using var server = new LocalWebServer(new FakeWebArchiveProvider(throwPlayback: true), new WebServerOptions
    {
        AppVersion = "test-web-version",
        ServerInstanceId = "11111111-2222-3333-4444-555555555555",
        ServerDisplayName = "Test Radio Vault Server",
        DatabaseSchemaVersion = 45,
        CapabilityGeneration = 8,
        Port = port,
        AccessToken = token,
        LoopbackOnly = true
    });
    server.Start();
    try
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
    var port = GetFreeTcpPort();
    var primaryToken = "primary-token-" + Guid.NewGuid().ToString("N");
    var pairedToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    using var server = new LocalWebServer(new FakeWebArchiveProvider(), new WebServerOptions
    {
        AppVersion = "test-web-version",
        ServerInstanceId = "11111111-2222-3333-4444-555555555555",
        ServerDisplayName = "Test Radio Vault Server",
        DatabaseSchemaVersion = 45,
        CapabilityGeneration = 8,
        Port = port,
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
    try
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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

static void WebServerStopIsIdempotent()
{
    var port = GetFreeTcpPort();
    var token = "test-token-" + Guid.NewGuid().ToString("N");
    using var server = new LocalWebServer(new FakeWebArchiveProvider(), new WebServerOptions { AppVersion = "test-web-version", Port = port, AccessToken = token, LoopbackOnly = true });
    server.Start();
    server.Stop();
    server.Stop();
}

static void WebServerSurvivesRapidRestartGenerations()
{
    var port = GetFreeTcpPort();
    var token = "restart-token-" + Guid.NewGuid().ToString("N");
    using var server = new LocalWebServer(new FakeWebArchiveProvider(), new WebServerOptions
    {
        AppVersion = "restart-test-version",
        Port = port,
        AccessToken = token,
        LoopbackOnly = true
    });

    for (var generation = 0; generation < 8; generation++)
    {
        server.Start();
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
        var html = client.GetStringAsync(
            $"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(token)}").GetAwaiter().GetResult();
        True(html.Contains("THE RADIO VAULT", StringComparison.Ordinal));
        server.Stop();
    }

    var source = File.ReadAllText(Path.Combine(
        SourceRoot(), "TheRadioVault.Web", "Services", "LocalWebServer.cs"));
    True(source.Contains("var startListener = _listener!;", StringComparison.Ordinal));
    True(source.Contains("AcceptLoopAsync(startListener", StringComparison.Ordinal));
    True(!source.Contains("AcceptLoopAsync(_listener!", StringComparison.Ordinal));
}

static void WithWebServer(Func<int, string, Task> test)
    => WithCustomWebServer(new FakeWebArchiveProvider(), test);

static void WithCustomWebServer(IWebArchiveProvider archive, Func<int, string, Task> test, Action<string>? log = null)
{
    var port = GetFreeTcpPort();
    var token = "test-token-" + Guid.NewGuid().ToString("N");
    using var server = new LocalWebServer(archive, new WebServerOptions
    {
        AppVersion = "test-web-version",
        ServerInstanceId = "11111111-2222-3333-4444-555555555555",
        ServerDisplayName = "Test Radio Vault",
        DatabaseSchemaVersion = 47,
        CapabilityGeneration = 3,
        Port = port,
        AccessToken = token,
        LoopbackOnly = true
    }, log);
    server.Start();
    try
    {
        test(port, token).GetAwaiter().GetResult();
    }
    finally
    {
        server.Stop();
    }
}

static int GetFreeTcpPort()
{
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static void TransactionalHandoffKeepsSourceUntilCommit()
{
    var coordinator = new PlaybackTransferCoordinator();
    var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    var source = new PlaybackTransferAuthority(
        "Radio Vault server", "server", 7, 9, 120_000, 3_600_000, 1d, true, now);
    var ticket = coordinator.Begin(new WebPlaybackTransferBeginRequest(
        "laptop-01", 9, 120_000, 3_600_000, 1d, true, "Laptop", "DesktopClient"), source, now);
    Equal("server", source.OwnerClientId);
    Equal(120_000L, source.PositionMs);
    True(!ticket.IsReady);
    var readyAt = now.AddSeconds(2);
    ticket = coordinator.MarkReady(new WebPlaybackTransferReadyRequest(
        "laptop-01", ticket.TransferId, 120_000, 3_600_000, true,
        DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d), source, readyAt);
    Equal(122_000L, ticket.CommitPositionMs);
    var committed = coordinator.Commit(new WebPlaybackTransferCommitRequest(
        "laptop-01", ticket.TransferId, ticket.ReadyRevision, ticket.CommitPositionMs, true), source, readyAt);
    Equal("laptop-01", committed.TargetClientId);
    True(coordinator.Pending(readyAt) is null);
}

static void UnownedPlaybackPermitsAudibleDecoder()
{
    var coordinator = new PlaybackTransferCoordinator();
    var now = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
    // Reproduce the provider's raw state: its internal Server sentinel can
    // remain even though the public playback session correctly reports None.
    var unowned = new PlaybackTransferAuthority(
        "Server", "server", 0, null, 0, 0, 1d, false, now);
    var ticket = coordinator.Begin(new WebPlaybackTransferBeginRequest(
        "iphone-01", 9, 18_000, 3_600_000, 1d, true, "iPhone", "Phone"), unowned, now);
    ticket = coordinator.MarkReady(new WebPlaybackTransferReadyRequest(
        "iphone-01", ticket.TransferId, 19_500, 3_600_000, true,
        DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d), unowned, now);
    var committed = coordinator.Commit(new WebPlaybackTransferCommitRequest(
        "iphone-01", ticket.TransferId, ticket.ReadyRevision, 19_500,
        DecoderRunningMuted: false, DecoderRunningAudibly: true), unowned, now);
    Equal("iphone-01", committed.TargetClientId);
    Equal(19_500L, committed.CommitPositionMs);

    var activeCoordinator = new PlaybackTransferCoordinator();
    var active = new PlaybackTransferAuthority(
        "Laptop", "laptop-01", 1, 9, 10_000, 3_600_000, 1d, true, now);
    var activeTicket = activeCoordinator.Begin(new WebPlaybackTransferBeginRequest(
        "iphone-01", 9, 10_000, 3_600_000, 1d, true, "iPhone", "Phone"), active, now);
    activeTicket = activeCoordinator.MarkReady(new WebPlaybackTransferReadyRequest(
        "iphone-01", activeTicket.TransferId, 10_000, 3_600_000, true,
        DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d), active, now);
    Throws<PlaybackTransferConflictException>(() => activeCoordinator.Commit(
        new WebPlaybackTransferCommitRequest(
            "iphone-01", activeTicket.TransferId, activeTicket.ReadyRevision, 10_000,
            DecoderRunningMuted: false, DecoderRunningAudibly: true), active, now));
}

static void TransactionalHandoffRejectsStartupZero()
{
    var coordinator = new PlaybackTransferCoordinator();
    var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    var source = new PlaybackTransferAuthority(
        "Radio Vault server", "server", 3, 9, 321_000, 3_600_000, 1d, true, now);
    var ticket = coordinator.Begin(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, 321_000, 3_600_000, 1d, true, "iPhone", "Phone"), source, now);
    ticket = coordinator.MarkReady(new WebPlaybackTransferReadyRequest(
        "phone-01", ticket.TransferId, 321_000, 3_600_000, true,
        DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d), source, now);
    Throws<PlaybackTransferConflictException>(() => coordinator.Commit(
        new WebPlaybackTransferCommitRequest("phone-01", ticket.TransferId,
            ticket.ReadyRevision, 0, true), source, now));
    True(coordinator.Pending(now) is not null);
}

static void TransactionalHandoffCancellationKeepsSource()
{
    var coordinator = new PlaybackTransferCoordinator();
    var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    var source = new PlaybackTransferAuthority(
        "Laptop", "laptop-01", 11, 9, 480_000, 3_600_000, 1.25d, true, now);
    var ticket = coordinator.Begin(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, 480_000, 3_600_000, 1.25d, true, "iPhone", "Phone"), source, now);
    True(coordinator.Cancel(new WebPlaybackTransferCancelRequest(
        "phone-01", ticket.TransferId, "decoder failed"), now));
    True(coordinator.Pending(now) is null);
    Equal("laptop-01", source.OwnerClientId);
    Equal(480_000L, source.PositionMs);
}

static void TransactionalHandoffInvalidatesChangedSource()
{
    var coordinator = new PlaybackTransferCoordinator();
    var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    var source = new PlaybackTransferAuthority(
        "Radio Vault server", "server", 5, 9, 100_000, 3_600_000, 1d, true, now);
    var ticket = coordinator.Begin(new WebPlaybackTransferBeginRequest(
        "laptop-01", 9, 100_000, 3_600_000, 1d, true, "Laptop", "DesktopClient"), source, now);
    var changedSource = source with { EpisodeId = 10 };
    Throws<PlaybackTransferConflictException>(() => coordinator.MarkReady(
        new WebPlaybackTransferReadyRequest("laptop-01", ticket.TransferId,
            100_000, 3_600_000, true, DesiredPlaying: true,
            OverrideDesiredPlaying: false, Speed: 1d), changedSource, now));
}

static void TransactionalHandoffRefreshesSourcePlayState()
{
    var coordinator = new PlaybackTransferCoordinator();
    var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    var playingSource = new PlaybackTransferAuthority(
        "Radio Vault server", "server", 5, 9, 100_000, 3_600_000, 1.25d, true, now);
    var ticket = coordinator.Begin(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, 100_000, 3_600_000, 1.25d, true, "iPhone", "Phone"), playingSource, now);
    var pausedSource = playingSource with { IsPlaying = false, PositionMs = 101_000, UpdatedAt = now.AddSeconds(1) };
    ticket = coordinator.MarkReady(new WebPlaybackTransferReadyRequest(
        "phone-01", ticket.TransferId, 101_000, 3_600_000, true,
        DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1.25d), pausedSource, now.AddSeconds(1));
    True(!ticket.DesiredPlaying);
    Equal(101_000L, ticket.CommitPositionMs);
}

static void TransactionalHandoffIsSingleFlight()
{
    var coordinator = new PlaybackTransferCoordinator();
    var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    var source = new PlaybackTransferAuthority(
        "Radio Vault server", "server", 5, 9, 100_000, 3_600_000, 1d, true, now);
    coordinator.Begin(new WebPlaybackTransferBeginRequest(
        "laptop-01", 9, 100_000, 3_600_000, 1d, true, "Laptop", "DesktopClient"), source, now);
    Throws<PlaybackTransferConflictException>(() => coordinator.Begin(
        new WebPlaybackTransferBeginRequest(
            "phone-01", 9, 100_000, 3_600_000, 1d, true, "iPhone", "Phone"), source, now));
}


static void TransactionalHandoffRetriesAreIdempotent()
{
    var provider = new FakeWebArchiveProvider();
    var request = new WebPlaybackTransferBeginRequest(
        "phone-retry-01", 9, 120_000, 3_600_000, 1d, true, "iPhone", "Phone");
    var firstBegin = provider.BeginPlaybackTransfer(request);
    var retryBegin = provider.BeginPlaybackTransfer(request);
    True(firstBegin.Changed && retryBegin.Changed);
    Equal(firstBegin.Transfer!.TransferId, retryBegin.Transfer!.TransferId);

    var ready = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "phone-retry-01", firstBegin.Transfer.TransferId, firstBegin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d));
    var commitRequest = new WebPlaybackTransferCommitRequest(
        "phone-retry-01", ready.Transfer!.TransferId, ready.Transfer.ReadyRevision,
        ready.Transfer.CommitPositionMs, DecoderRunningMuted: true);
    var firstCommit = provider.CommitPlaybackTransfer(commitRequest);
    var generation = firstCommit.Session.Generation;
    var retryCommit = provider.CommitPlaybackTransfer(commitRequest);

    True(firstCommit.Changed && retryCommit.Changed);
    Equal(firstCommit.Transfer!.TransferId, retryCommit.Transfer!.TransferId);
    Equal(generation, retryCommit.Session.Generation);
    Equal("phone-retry-01", retryCommit.Session.OwnerClientId);
}

static void TransactionalHandoffCoversAllDeviceDirections()
{
    var directions = new[]
    {
        (SourceDevice: "Server", SourceClient: "server", TargetDevice: "Laptop", TargetClient: "laptop-01", TargetKind: "DesktopClient"),
        (SourceDevice: "Laptop", SourceClient: "laptop-01", TargetDevice: "Radio Vault server", TargetClient: "server", TargetKind: "Server"),
        (SourceDevice: "Server", SourceClient: "server", TargetDevice: "iPhone", TargetClient: "phone-01", TargetKind: "Phone"),
        (SourceDevice: "iPhone", SourceClient: "phone-01", TargetDevice: "Radio Vault server", TargetClient: "server", TargetKind: "Server"),
        (SourceDevice: "Laptop", SourceClient: "laptop-01", TargetDevice: "iPhone", TargetClient: "phone-01", TargetKind: "Phone"),
        (SourceDevice: "iPhone", SourceClient: "phone-01", TargetDevice: "Laptop", TargetClient: "laptop-01", TargetKind: "DesktopClient")
    };

    foreach (var direction in directions)
    {
        var coordinator = new PlaybackTransferCoordinator();
        var now = new DateTimeOffset(2026, 7, 29, 13, 0, 0, TimeSpan.Zero);
        var source = new PlaybackTransferAuthority(
            direction.SourceDevice, direction.SourceClient, 21, 9, 600_000, 3_600_000, 1.25d, true, now);
        var ticket = coordinator.Begin(new WebPlaybackTransferBeginRequest(
            direction.TargetClient, 9, 600_000, 3_600_000, 1.25d, true,
            direction.TargetDevice, direction.TargetKind), source, now);
        ticket = coordinator.MarkReady(new WebPlaybackTransferReadyRequest(
            direction.TargetClient, ticket.TransferId, 600_000, 3_600_000, true,
            DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1.25d), source, now);
        var committed = coordinator.Commit(new WebPlaybackTransferCommitRequest(
            direction.TargetClient, ticket.TransferId, ticket.ReadyRevision,
            ticket.CommitPositionMs, DecoderRunningMuted: true), source, now);
        Equal(direction.TargetClient, committed.TargetClientId);
        Equal(direction.SourceClient, committed.SourceOwnerClientId);
        True(coordinator.Pending(now) is null);
    }
}

static void TransactionalHandoffSurvivesRepeatedDeviceMoves()
{
    var coordinator = new PlaybackTransferCoordinator();
    var now = new DateTimeOffset(2026, 8, 2, 13, 0, 0, TimeSpan.Zero);
    var authority = new PlaybackTransferAuthority(
        "Radio Vault server", "server", 0, 9, 600_000, 3_600_000, 1d, true, now);
    var devices = new[]
    {
        (Id: "phone-stress", Name: "Phone", Kind: "Phone"),
        (Id: "laptop-stress", Name: "Laptop", Kind: "DesktopClient"),
        (Id: "firefox-stress", Name: "Firefox", Kind: "Browser"),
        (Id: "server", Name: "Radio Vault server", Kind: "Server")
    };

    var transferIds = new HashSet<Guid>();
    for (var index = 0; index < 100; index++)
    {
        var target = devices[index % devices.Length];
        var ticket = coordinator.Begin(new WebPlaybackTransferBeginRequest(
            target.Id, 9, authority.PositionMs, authority.DurationMs, authority.Speed,
            true, target.Name, target.Kind), authority, now.AddMilliseconds(index * 10));
        True(transferIds.Add(ticket.TransferId));
        ticket = coordinator.MarkReady(new WebPlaybackTransferReadyRequest(
            target.Id, ticket.TransferId, ticket.ProtectedPositionMs, ticket.DurationMs,
            true, DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: ticket.Speed,
            target.Name, target.Kind), authority, now.AddMilliseconds(index * 10 + 2));
        ticket = coordinator.Commit(new WebPlaybackTransferCommitRequest(
            target.Id, ticket.TransferId, ticket.ReadyRevision, ticket.CommitPositionMs,
            DecoderRunningMuted: true), authority, now.AddMilliseconds(index * 10 + 4));

        authority = new PlaybackTransferAuthority(
            target.Name, target.Id, authority.Generation + 1, ticket.TargetEpisodeId,
            ticket.CommitPositionMs + 250, ticket.DurationMs, ticket.Speed,
            ticket.DesiredPlaying, now.AddMilliseconds(index * 10 + 5));
        True(coordinator.Pending(now.AddMilliseconds(index * 10 + 6)) is null);
    }

    Equal(100, transferIds.Count);
    Equal("server", authority.OwnerClientId);
    Equal(100L, authority.Generation);
}

static void NonTransactionalPlaybackCannotStealActiveOwner()
{
    var provider = new FakeWebArchiveProvider();
    var begin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "phone-owner", 9, 120_000, 3_600_000, 1d, true, "Phone", "Phone"));
    var ready = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "phone-owner", begin.Transfer!.TransferId, begin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d));
    var commit = provider.CommitPlaybackTransfer(new WebPlaybackTransferCommitRequest(
        "phone-owner", ready.Transfer!.TransferId, ready.Transfer.ReadyRevision,
        ready.Transfer.CommitPositionMs, DecoderRunningMuted: true));
    True(commit.Changed);

    var staleClaim = provider.UpdateWebPlayback(new WebClientPlaybackUpdate(
        "laptop-stale", 9, commit.Transfer!.CommitPositionMs, 3_600_000, true, 1d,
        Force: true, DeviceName: "Laptop", DeviceKind: "DesktopClient",
        ExpectedGeneration: commit.Session.Generation));
    True(staleClaim.Conflict);
    Equal("phone-owner", provider.GetPlaybackSession().OwnerClientId);
}

static void TransactionalHandoffPreservesNewerTargetProgress()
{
    var provider = new FakeWebArchiveProvider();
    var begin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, 0, 3_600_000, 1d, true, "iPhone", "Phone"));
    True(begin.Changed && begin.Transfer is not null, $"Begin failed: conflict={begin.Conflict}; message={begin.Message}");
    True(begin.Transfer!.ProtectedPositionMs >= 120_000L);
}


static void TransactionalHandoffRequiresSourceStopReceipt()
{
    var provider = new FakeWebArchiveProvider();
    var begin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, 120_000, 3_600_000, 1d, true, "iPhone", "Phone"));
    var ready = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "phone-01", begin.Transfer!.TransferId, begin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d));
    var commit = provider.CommitPlaybackTransfer(new WebPlaybackTransferCommitRequest(
        "phone-01", ready.Transfer!.TransferId, ready.Transfer.ReadyRevision,
        ready.Transfer.CommitPositionMs, DecoderRunningMuted: true));

    True(commit.Changed);
    var receipt = commit.Session.CommittedTransfer;
    True(receipt is not null);
    Equal("server", receipt!.SourceClientId);
    True(receipt.SourceWasPlaying);
    True(!receipt.SourceStopAcknowledged);

    var acknowledged = provider.AcknowledgePlaybackTransferSourceStopped(
        new WebPlaybackTransferSourceStoppedRequest("server", receipt.TransferId, receipt.Generation));
    True(acknowledged.Changed);
    True(acknowledged.Session.CommittedTransfer?.SourceStopAcknowledged == true);
}

static void TransactionalHandoffRejectsStaleSourceStopReceipt()
{
    var provider = new FakeWebArchiveProvider();
    var begin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, 120_000, 3_600_000, 1d, true, "iPhone", "Phone"));
    var ready = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "phone-01", begin.Transfer!.TransferId, begin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d));
    var commit = provider.CommitPlaybackTransfer(new WebPlaybackTransferCommitRequest(
        "phone-01", ready.Transfer!.TransferId, ready.Transfer.ReadyRevision,
        ready.Transfer.CommitPositionMs, DecoderRunningMuted: true));
    var receipt = commit.Session.CommittedTransfer!;

    var wrongGeneration = provider.AcknowledgePlaybackTransferSourceStopped(
        new WebPlaybackTransferSourceStoppedRequest("server", receipt.TransferId, receipt.Generation + 1));
    True(wrongGeneration.Conflict);
    True(wrongGeneration.Session.CommittedTransfer?.SourceStopAcknowledged == false);

    var wrongSource = provider.AcknowledgePlaybackTransferSourceStopped(
        new WebPlaybackTransferSourceStoppedRequest("other-device", receipt.TransferId, receipt.Generation));
    True(wrongSource.Conflict);
    True(wrongSource.Session.CommittedTransfer?.SourceStopAcknowledged == false);
}

static void NewerHandoffSupersedesPriorSourceStopReceipt()
{
    var provider = new FakeWebArchiveProvider();

    var firstBegin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, 120_000, 3_600_000, 1d, true, "iPhone", "Phone"));
    var firstReady = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "phone-01", firstBegin.Transfer!.TransferId, firstBegin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d));
    var firstCommit = provider.CommitPlaybackTransfer(new WebPlaybackTransferCommitRequest(
        "phone-01", firstReady.Transfer!.TransferId, firstReady.Transfer.ReadyRevision,
        firstReady.Transfer.CommitPositionMs, DecoderRunningMuted: true));
    var firstReceipt = firstCommit.Session.CommittedTransfer!;

    var secondBegin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "laptop-02", 9, firstReady.Transfer.CommitPositionMs, 3_600_000, 1d, true,
        "Laptop", "DesktopClient"));
    var secondReady = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "laptop-02", secondBegin.Transfer!.TransferId, secondBegin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d));
    var secondCommit = provider.CommitPlaybackTransfer(new WebPlaybackTransferCommitRequest(
        "laptop-02", secondReady.Transfer!.TransferId, secondReady.Transfer.ReadyRevision,
        secondReady.Transfer.CommitPositionMs, DecoderRunningMuted: true));

    True(secondCommit.Changed);
    Equal("laptop-02", secondCommit.Session.OwnerClientId);
    True(secondCommit.Session.Generation > firstReceipt.Generation);

    var staleFirstAcknowledgement = provider.AcknowledgePlaybackTransferSourceStopped(
        new WebPlaybackTransferSourceStoppedRequest(
            firstReceipt.SourceClientId, firstReceipt.TransferId, firstReceipt.Generation));
    True(staleFirstAcknowledgement.Conflict);
    Equal("laptop-02", staleFirstAcknowledgement.Session.OwnerClientId);

    var currentReceipt = secondCommit.Session.CommittedTransfer!;
    var currentAcknowledgement = provider.AcknowledgePlaybackTransferSourceStopped(
        new WebPlaybackTransferSourceStoppedRequest(
            currentReceipt.SourceClientId, currentReceipt.TransferId, currentReceipt.Generation));
    True(currentAcknowledgement.Changed);
    True(currentAcknowledgement.Session.CommittedTransfer?.SourceStopAcknowledged == true);
}

static void LivePlaybackHeartbeatIsNotDurable()
{
    var provider = new FakeWebArchiveProvider();
    var begin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, 120_000, 3_600_000, 1d, true, "iPhone", "Phone"));
    var ready = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "phone-01", begin.Transfer!.TransferId, begin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d));
    var commit = provider.CommitPlaybackTransfer(new WebPlaybackTransferCommitRequest(
        "phone-01", ready.Transfer!.TransferId, ready.Transfer.ReadyRevision,
        ready.Transfer.CommitPositionMs, DecoderRunningMuted: true));
    var durableAtCommit = provider.GetEpisode(9)!.PositionMs;

    var heartbeat = provider.UpdateWebPlayback(new WebClientPlaybackUpdate(
        "phone-01", 9, durableAtCommit + 30_000, 3_600_000, true, 1d,
        ExpectedGeneration: commit.Session.Generation));
    True(heartbeat.Changed);
    Equal(durableAtCommit, provider.GetEpisode(9)!.PositionMs);
}

static void FailedTransactionalCommitPreservesSource()
{
    var provider = new FakeWebArchiveProvider();
    var original = provider.GetPlaybackSession();
    var originalPosition = provider.GetEpisode(9)!.PositionMs;
    var begin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, originalPosition, 3_600_000, 1d, true, "iPhone", "Phone"));
    var ready = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "phone-01", begin.Transfer!.TransferId, begin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d));
    var failed = provider.CommitPlaybackTransfer(new WebPlaybackTransferCommitRequest(
        "phone-01", ready.Transfer!.TransferId, ready.Transfer.ReadyRevision,
        PreparedPositionMs: 0, DecoderRunningMuted: true));

    True(failed.Conflict);
    Equal(original.OwnerDevice, provider.GetPlaybackSession().OwnerDevice);
    Equal(original.Generation, provider.GetPlaybackSession().Generation);
    Equal(originalPosition, provider.GetEpisode(9)!.PositionMs);
}

static void GenerationlessProgressCannotRewind()
{
    var provider = new FakeWebArchiveProvider();
    var begin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, 120_000, 3_600_000, 1d, true, "iPhone", "Phone"));
    var ready = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "phone-01", begin.Transfer!.TransferId, begin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d));
    var commit = provider.CommitPlaybackTransfer(new WebPlaybackTransferCommitRequest(
        "phone-01", ready.Transfer!.TransferId, ready.Transfer.ReadyRevision,
        ready.Transfer.CommitPositionMs, DecoderRunningMuted: true));
    True(commit.Changed);

    var retry = provider.SyncOfflineProgress(new WebOfflineProgressUpdate(
        "phone-01", 9, 0, 3_600_000, Completed: false, Speed: 1d,
        AllowRewind: true, ExpectedGeneration: 0));
    True(!retry.Changed);
    Equal(commit.Transfer!.CommitPositionMs, provider.GetEpisode(9)!.PositionMs);
}

static void DurablePlaybackRejectsStaleZeroAfterHandoff()
{
    var provider = new FakeWebArchiveProvider();
    var begin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, 120_000, 3_600_000, 1d, true, "iPhone", "Phone"));
    True(begin.Changed && begin.Transfer is not null);
    var ready = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "phone-01", begin.Transfer!.TransferId, begin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: true, Speed: 1d));
    True(ready.Changed && ready.Transfer is not null, $"Ready failed: conflict={ready.Conflict}; message={ready.Message}");
    var commit = provider.CommitPlaybackTransfer(new WebPlaybackTransferCommitRequest(
        "phone-01", ready.Transfer!.TransferId, ready.Transfer.ReadyRevision,
        ready.Transfer.CommitPositionMs, true));
    True(commit.Changed, $"Commit failed: conflict={commit.Conflict}; message={commit.Message}");

    var stale = provider.SyncOfflineProgress(new WebOfflineProgressUpdate(
        "phone-01", 9, 0, 3_600_000, Completed: false, Speed: 1d,
        AllowRewind: true, ExpectedGeneration: commit.Session.Generation));
    True(stale.Conflict);
    Equal(commit.Transfer!.CommitPositionMs, provider.GetEpisode(9)!.PositionMs);
}

static void WebPlayerIsRadioVaultBranded()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var html = await client.GetStringAsync($"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(token)}");
        True(html.Contains("Version: test-web-version", StringComparison.Ordinal));
        True(!html.Contains("__APP_VERSION__", StringComparison.Ordinal));
    });
}

static void WebApiSendsRemotePlaybackCommands()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.PostAsJsonAsync(
            $"http://127.0.0.1:{port}/api/v1/player/command?token={Uri.EscapeDataString(token)}",
            new { command = "pause", clientId = "test-client-0001", expectedRevision = 100L });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        True(body.GetProperty("result").GetProperty("changed").GetBoolean());
    });
}

static void WebClientUsesUnifiedPlaybackOwnership()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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

static void WebApiExposesAuthoritativePlaybackSession()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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

static void WebPlaybackLeaseRejectsAnotherClient()
{
    var provider = new FakeWebArchiveProvider();
    var begin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "first-client-01", 9, 120_000, 3_600_000, 1d, true, "Laptop", "DesktopClient"));
    True(begin.Changed && begin.Transfer is not null, $"Begin failed: conflict={begin.Conflict}; message={begin.Message}");
    var ready = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "first-client-01", begin.Transfer!.TransferId, begin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: true, Speed: 1d));
    True(ready.Changed && ready.Transfer is not null, $"Ready failed: conflict={ready.Conflict}; message={ready.Message}");
    var commit = provider.CommitPlaybackTransfer(new WebPlaybackTransferCommitRequest(
        "first-client-01", ready.Transfer!.TransferId, ready.Transfer.ReadyRevision,
        ready.Transfer.CommitPositionMs, true));
    True(commit.Changed, $"Commit failed: conflict={commit.Conflict}; message={commit.Message}");
    var first = provider.UpdateWebPlayback(new WebClientPlaybackUpdate(
        "first-client-01", 9, 130_000, 3_600_000, true,
        ExpectedGeneration: commit.Session.Generation));
    True(first.Changed, $"First client update failed: conflict={first.Conflict}; message={first.Message}; generation={commit.Session.Generation}");
    var second = provider.UpdateWebPlayback(new WebClientPlaybackUpdate(
        "second-client-02", 9, 140_000, 3_600_000, true,
        ExpectedGeneration: commit.Session.Generation));
    True(second.Conflict, $"Second client unexpectedly accepted: changed={second.Changed}; message={second.Message}; generation={commit.Session.Generation}");
}

static void WebApiManagesQueue()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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

static void WebClientIncludesManualOfflineDownloads()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var html = await client.GetStringAsync($"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(token)}");
        True(html.Contains("e.target.closest(\"[data-download]\")", StringComparison.Ordinal));
        True(!html.Contains("playerDownload.addEventListener('click'", StringComparison.Ordinal));
    });
}

static void WebClientRegistersSecureOfflineShell()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
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
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var html = await client.GetStringAsync($"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(token)}");
        True(html.Contains("Skip to content", StringComparison.Ordinal));
        True(html.Contains("id=\"mainContent\"", StringComparison.Ordinal));
        True(html.Contains("appUpdateBanner", StringComparison.Ordinal));
        True(html.Contains("repairAppShell", StringComparison.Ordinal));
        True(html.Contains("Downloaded audio, artwork, listening progress and pending sync changes will be preserved", StringComparison.Ordinal));
        True(html.Contains("aria-live=\"polite\"", StringComparison.Ordinal));
        True(html.Contains("radio-vault-anywhere-shell-v67", StringComparison.Ordinal));
        True(html.Contains("radio-vault-anywhere-audio-v1", StringComparison.Ordinal));
        True(html.Contains("radio-vault-anywhere-artwork-v1", StringComparison.Ordinal));
    });
}

static void WebClientCachesDownloadedArtwork()
{
    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var html = await client.GetStringAsync($"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(token)}");
        True(html.Contains("/__offline_artwork__/", StringComparison.Ordinal));
        True(html.Contains("radio-vault-anywhere-artwork-v1", StringComparison.Ordinal));
        True(html.Contains("repairDownloadedArtwork", StringComparison.Ordinal));
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

static void Schema47AddsCanonicalTopicIdentity()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var database = new SqliteDatabase(Path.Combine(directory, "wiki-schema.sqlite"));
        database.Initialize();
        using var connection = database.OpenConnection();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Equal(47L, Convert.ToInt64(version.ExecuteScalar()));
        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('wiki_pages','wiki_page_aliases','wiki_page_revisions','wiki_relationships','wiki_sources','wiki_citations','wiki_images','wiki_page_images','wiki_timeline_events','wiki_timeline_event_sources','wiki_timeline_event_images','wiki_timeline_event_broadcasts','wiki_import_runs','canonical_topics','canonical_topic_aliases','topic_merge_history','wiki_page_redirects')";
        Equal(17L, Convert.ToInt64(tables.ExecuteScalar()));
        using var indexes = connection.CreateCommand();
        indexes.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name IN ('ix_wiki_pages_type_status','ix_wiki_citations_page','ix_wiki_timeline_events_page_date')";
        Equal(3L, Convert.ToInt64(indexes.ExecuteScalar()));
    }
    finally { try { Directory.Delete(directory, true); } catch { } }
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
        Equal(1, created.Revision);
        var updated = service.SavePageAsync(new WikiPageDraft(
            created.PageId, "ron-and-fez", "Ron & Fez", "Show", "A long-running radio show.", "# Ron & Fez\n\nHistory.",
            "Published", 1, "Expanded history", "Human editor", new[] { "Ron and Fez", "R&F" })).GetAwaiter().GetResult();
        Equal(2, updated.Revision);
        Throws<WikiConcurrencyException>(() => service.SavePageAsync(new WikiPageDraft(
            created.PageId, "ron-and-fez", "Ron & Fez", "Show", "Stale edit", "# Stale", "Draft", 1,
            "Stale overwrite", "Old client")).GetAwaiter().GetResult());
        var page = service.GetPageAsync(created.PageId).GetAwaiter().GetResult()!;
        Equal(2, page.Revision);
        Equal("A long-running radio show.", page.Summary);
        Equal(2, page.Aliases.Count);
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
        var imported = packService.Import(bytes);
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

    var web = File.ReadAllText(Path.Combine(root, "TheRadioVault.Web", "Services", "LocalWebServer.cs"));
    True(web.Contains("async function exportResearchPack()", StringComparison.Ordinal));
    True(web.Contains("await exportResearchPack();", StringComparison.Ordinal));
    True(!web.Contains("Choose a show to export.", StringComparison.Ordinal));
}

static void KnowledgeImportsRunAsResumableBackgroundJobs()
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

    var web = File.ReadAllText(Path.Combine(root, "TheRadioVault.Web", "Services", "LocalWebServer.cs"));
    True(web.Contains("pollResearchPackImport", StringComparison.Ordinal));
    True(web.Contains("FederationResearchImportStatus", StringComparison.Ordinal));
    True(web.Contains("researchImportProgressCard", StringComparison.Ordinal));
}

static void KnowledgeImportProgressBarsAreDeterminate()
{
    var root = SourceRoot();
    var research = File.ReadAllText(Path.Combine(root, "TheRadioVault.Desktop.Avalonia", "Views", "ResearchWorkspaceView.axaml"));
    True(research.Contains("Value=\"{Binding ImportProgressPercent}\"", StringComparison.Ordinal));
    True(!research.Contains("IsIndeterminate=\"True\"", StringComparison.Ordinal));

    var server = File.ReadAllText(Path.Combine(root, "TheRadioVault.Server", "Views", "ServerSettingsWindow.axaml"));
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

    var web = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Web", "Services", "LocalWebServer.cs"));
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
    Equal("0.35.0-alpha9-buildfix3", File.ReadAllText(Path.Combine(SourceRoot(), "VERSION.txt")).Trim());

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
        var web = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Web", "Services", "LocalWebServer.cs"));
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

static void Schema45UpgradesLibraryTruthSchema43Safely()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "schema43.sqlite");
    try
    {
        using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            legacy.Open();
            using var command = legacy.CreateCommand();
            command.CommandText = """
                PRAGMA user_version=43;
                CREATE TABLE library_truth_runs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    started_at TEXT NOT NULL,
                    completed_at TEXT NULL,
                    status TEXT NOT NULL DEFAULT 'running',
                    parser_version TEXT NOT NULL DEFAULT '',
                    source_file_count INTEGER NOT NULL DEFAULT 0,
                    current_broadcast_count INTEGER NOT NULL DEFAULT 0,
                    proposed_broadcast_count INTEGER NOT NULL DEFAULT 0,
                    unchanged_files INTEGER NOT NULL DEFAULT 0,
                    changed_files INTEGER NOT NULL DEFAULT 0,
                    recovered_dates INTEGER NOT NULL DEFAULT 0,
                    unknown_dates INTEGER NOT NULL DEFAULT 0,
                    needs_review INTEGER NOT NULL DEFAULT 0,
                    merge_groups INTEGER NOT NULL DEFAULT 0,
                    split_groups INTEGER NOT NULL DEFAULT 0,
                    exact_duplicate_groups INTEGER NOT NULL DEFAULT 0,
                    strong_duplicate_groups INTEGER NOT NULL DEFAULT 0,
                    multipart_broadcasts INTEGER NOT NULL DEFAULT 0,
                    message TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE library_truth_broadcasts (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_id INTEGER NOT NULL,
                    canonical_key TEXT NOT NULL,
                    collection_name TEXT NOT NULL DEFAULT '',
                    air_date TEXT NULL,
                    broadcast_slot TEXT NOT NULL DEFAULT '',
                    file_count INTEGER NOT NULL DEFAULT 0,
                    segment_count INTEGER NOT NULL DEFAULT 1,
                    recording_count INTEGER NOT NULL DEFAULT 1,
                    exact_duplicate_count INTEGER NOT NULL DEFAULT 0,
                    strong_duplicate_count INTEGER NOT NULL DEFAULT 0,
                    current_episode_count INTEGER NOT NULL DEFAULT 0,
                    status TEXT NOT NULL DEFAULT 'Stable',
                    confidence_score INTEGER NOT NULL DEFAULT 0,
                    evidence_json TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE library_truth_recordings (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_id INTEGER NOT NULL,
                    canonical_broadcast_key TEXT NOT NULL,
                    recording_key TEXT NOT NULL,
                    label TEXT NOT NULL DEFAULT '',
                    file_count INTEGER NOT NULL DEFAULT 0,
                    segment_count INTEGER NOT NULL DEFAULT 1,
                    duration_ms INTEGER NOT NULL DEFAULT 0,
                    relationship TEXT NOT NULL DEFAULT 'Single recording',
                    confidence_score INTEGER NOT NULL DEFAULT 0,
                    evidence_json TEXT NOT NULL DEFAULT ''
                );
                """;
            command.ExecuteNonQuery();
        }

        var database = new SqliteDatabase(path);
        database.Initialize();
        using var connection = database.OpenConnection();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Equal(47L, Convert.ToInt64(version.ExecuteScalar()));
        using var columns = connection.CreateCommand();
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('library_truth_recordings') WHERE name IN ('role','completeness_score','preferred_score','duration_ratio','is_preferred_candidate','review_reason')";
        Equal(6L, Convert.ToInt64(columns.ExecuteScalar()));
        using var index = connection.CreateCommand();
        index.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_library_truth_recordings_role'";
        Equal(1L, Convert.ToInt64(index.ExecuteScalar()));
        using var alpha6Tables = connection.CreateCommand();
        alpha6Tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('library_truth_coverages','library_truth_adoption_previews')";
        Equal(2L, Convert.ToInt64(alpha6Tables.ExecuteScalar()));
        using var alpha7Tables = connection.CreateCommand();
        alpha7Tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('library_truth_rehearsal_runs','library_truth_rehearsal_items')";
        Equal(2L, Convert.ToInt64(alpha7Tables.ExecuteScalar()));
        using var alpha8Table = connection.CreateCommand();
        alpha8Table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='library_truth_rehearsal_conflicts'";
        Equal(1L, Convert.ToInt64(alpha8Table.ExecuteScalar()));
        using var alpha10Tables = connection.CreateCommand();
        alpha10Tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('canonical_broadcasts','recordings','recording_segments','recording_coverages','episode_canonical_map','library_truth_adoption_runs','library_truth_adoption_items','library_truth_adoption_conflicts')";
        Equal(8L, Convert.ToInt64(alpha10Tables.ExecuteScalar()));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void Schema45IncludesGuardedLibraryTruthAdoptionTables()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "test.sqlite");
    try
    {
        var database = new SqliteDatabase(path);
        database.Initialize();
        using var connection = database.OpenConnection();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Equal(47L, Convert.ToInt64(version.ExecuteScalar()));
        using var qualityTable = connection.CreateCommand();
        qualityTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='research_quality_actions'";
        Equal(1L, Convert.ToInt64(qualityTable.ExecuteScalar()));
        using var rollbackTables = connection.CreateCommand();
        rollbackTables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('research_import_rollbacks','research_import_rollback_changes','research_field_provenance')";
        Equal(3L, Convert.ToInt64(rollbackTables.ExecuteScalar()));
        using var transcriptTables = connection.CreateCommand();
        transcriptTables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('transcripts','transcript_segments','transcription_jobs','transcript_imports')";
        Equal(4L, Convert.ToInt64(transcriptTables.ExecuteScalar()));
        using var transcriptIndexes = connection.CreateCommand();
        transcriptIndexes.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name IN ('ix_transcripts_status_updated','ix_transcript_segments_time','ix_transcription_jobs_state_requested')";
        Equal(3L, Convert.ToInt64(transcriptIndexes.ExecuteScalar()));
        using var voiceTables = connection.CreateCommand();
        voiceTables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('voice_people','transcript_speakers','voice_profiles','voice_samples','speaker_match_suggestions')";
        Equal(5L, Convert.ToInt64(voiceTables.ExecuteScalar()));
        using var segmentColumns = connection.CreateCommand();
        segmentColumns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('transcript_segments') WHERE name IN ('speaker_key','content_kind','is_reviewed')";
        Equal(3L, Convert.ToInt64(segmentColumns.ExecuteScalar()));
        using var jobColumns = connection.CreateCommand();
        jobColumns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('transcription_jobs') WHERE name IN ('language','start_ms','duration_ms','enable_speaker_diarization','use_vad','replace_existing')";
        Equal(6L, Convert.ToInt64(jobColumns.ExecuteScalar()));
        using var triageColumns = connection.CreateCommand();
        triageColumns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('research_reconciliation_candidates') WHERE name IN ('requires_review','review_category','recommended_action','decision_source')";
        Equal(4L, Convert.ToInt64(triageColumns.ExecuteScalar()));
        using var actionDecisionSource = connection.CreateCommand();
        actionDecisionSource.CommandText = "SELECT COUNT(*) FROM pragma_table_info('research_reconciliation_actions') WHERE name='decision_source'";
        Equal(1L, Convert.ToInt64(actionDecisionSource.ExecuteScalar()));
        using var preservationColumns = connection.CreateCommand();
        preservationColumns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('media_files') WHERE name IN ('fingerprinted_at','full_hashed_at','inspection_error','inspection_error_at')";
        Equal(4L, Convert.ToInt64(preservationColumns.ExecuteScalar()));
        using var preservationRuns = connection.CreateCommand();
        preservationRuns.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='preservation_scan_runs'";
        Equal(1L, Convert.ToInt64(preservationRuns.ExecuteScalar()));
        using var truthTables = connection.CreateCommand();
        truthTables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('library_truth_runs','library_truth_files','library_truth_recordings','library_truth_broadcasts')";
        Equal(4L, Convert.ToInt64(truthTables.ExecuteScalar()));
        using var truthFileColumns = connection.CreateCommand();
        truthFileColumns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('library_truth_files') WHERE name='recording_key'";
        Equal(1L, Convert.ToInt64(truthFileColumns.ExecuteScalar()));
        using var truthAuditTables = connection.CreateCommand();
        truthAuditTables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('library_truth_years','library_truth_conflicts','library_truth_coverages','library_truth_adoption_previews')";
        Equal(4L, Convert.ToInt64(truthAuditTables.ExecuteScalar()));
        using var truthCoverageColumns = connection.CreateCommand();
        truthCoverageColumns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('library_truth_coverages') WHERE name IN ('recording_key','segment_number','target_broadcast_key','coverage_kind','start_offset_ms','end_offset_ms','requires_review','media_file_ids_json')";
        Equal(8L, Convert.ToInt64(truthCoverageColumns.ExecuteScalar()));
        using var truthPreviewColumns = connection.CreateCommand();
        truthPreviewColumns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('library_truth_adoption_previews') WHERE name IN ('canonical_key','planned_action','provisional_episode_id','current_episode_ids_json','coverage_count','planned_write_count','eligible_for_guarded_adoption','guard_reason')";
        Equal(8L, Convert.ToInt64(truthPreviewColumns.ExecuteScalar()));
        using var truthRehearsalTables = connection.CreateCommand();
        truthRehearsalTables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('library_truth_rehearsal_runs','library_truth_rehearsal_items')";
        Equal(2L, Convert.ToInt64(truthRehearsalTables.ExecuteScalar()));
        using var truthRehearsalColumns = connection.CreateCommand();
        truthRehearsalColumns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('library_truth_rehearsal_runs') WHERE name IN ('backup_path','source_fingerprint','rollback_fingerprint','truth_run_signature','item_signature','conflict_signature','file_reassignments','state_rows_migrated','auto_resolved_conflicts','unresolved_conflicts','preserved_alternates','foreign_key_violations','rollback_verified','message')";
        Equal(14L, Convert.ToInt64(truthRehearsalColumns.ExecuteScalar()));
        using var truthForensicTable = connection.CreateCommand();
        truthForensicTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='library_truth_rehearsal_conflicts'";
        Equal(1L, Convert.ToInt64(truthForensicTable.ExecuteScalar()));
        using var truthForensicColumns = connection.CreateCommand();
        truthForensicColumns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('library_truth_rehearsal_conflicts') WHERE name IN ('canonical_key','field_name','classification','selected_value','candidate_values_json','provenance_json','resolution','auto_resolved','requires_review','preserved_alternate_count')";
        Equal(10L, Convert.ToInt64(truthForensicColumns.ExecuteScalar()));
        using var truthRecordingColumns = connection.CreateCommand();
        truthRecordingColumns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('library_truth_recordings') WHERE name IN ('role','completeness_score','preferred_score','duration_ratio','is_preferred_candidate','review_reason')";
        Equal(6L, Convert.ToInt64(truthRecordingColumns.ExecuteScalar()));
        using var truthBroadcastColumns = connection.CreateCommand();
        truthBroadcastColumns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('library_truth_broadcasts') WHERE name IN ('adoption_state','adoption_reason','preferred_recording_key','suspicious_merge','duration_spread_ratio','cross_identity_conflict_count')";
        Equal(6L, Convert.ToInt64(truthBroadcastColumns.ExecuteScalar()));
        using var adoptionTables = connection.CreateCommand();
        adoptionTables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('canonical_broadcasts','recordings','recording_segments','recording_coverages','episode_canonical_map','library_truth_adoption_runs','library_truth_adoption_items','library_truth_adoption_conflicts')";
        Equal(8L, Convert.ToInt64(adoptionTables.ExecuteScalar()));
        using var adoptionRunColumns = connection.CreateCommand();
        adoptionRunColumns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('library_truth_adoption_runs') WHERE name IN ('truth_run_id','rehearsal_run_id','backup_path','source_fingerprint','staged_fingerprint','post_commit_fingerprint','rehearsal_truth_signature','commit_truth_signature','rehearsal_item_signature','commit_item_signature','rehearsal_conflict_signature','commit_conflict_signature','commit_verified')";
        Equal(13L, Convert.ToInt64(adoptionRunColumns.ExecuteScalar()));
        using var completedAdoptionGuard = connection.CreateCommand();
        completedAdoptionGuard.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ux_library_truth_adoption_completed_truth'";
        Equal(1L, Convert.ToInt64(completedAdoptionGuard.ExecuteScalar()));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
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

static void InAppTranscriptionSetupInstallsOfficialAssetsSafely()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        using var handler = new FakeWhisperDownloadHandler();
        using var downloads = new WhisperDownloadService(directory, handler);
        var worker = downloads.InstallLatestWindowsWorkerAsync().GetAwaiter().GetResult();
        True(File.Exists(worker.ExecutablePath));
        Equal("v-test", worker.Version);
        True(File.Exists(Path.Combine(Path.GetDirectoryName(worker.ExecutablePath)!, "whisper.dll")));

        var model = downloads.DownloadModelAsync(new WhisperModelCatalogItem(
            "test", "Test model", "ggml-test.bin",
            "https://huggingface.co/ggml-org/test/resolve/main/ggml-test.bin", 1024)).GetAwaiter().GetResult();
        True(File.Exists(model));
        Equal(1024L, new FileInfo(model).Length);

        var vad = downloads.DownloadVadModelAsync().GetAwaiter().GetResult();
        True(File.Exists(vad));
        Equal(WhisperDownloadService.VadFileName, Path.GetFileName(vad));
        True(handler.ReleaseRequested && handler.WorkerRequested && handler.ModelRequested && handler.VadRequested);
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
    var serverView = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Server", "Views", "ServerSettingsWindow.axaml"));
    True(serverView.Contains("Background archive service", StringComparison.Ordinal));
    True(serverView.Contains("It contains settings only", StringComparison.Ordinal));
    True(!serverView.Contains("Dashboard", StringComparison.Ordinal));
    True(!serverView.Contains("Now Playing", StringComparison.Ordinal));
    var webServer = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Web", "Services", "LocalWebServer.cs"));
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

    var serverView = File.ReadAllText(Path.Combine(SourceRoot(), "TheRadioVault.Server", "Views", "ServerSettingsWindow.axaml"));
    True(serverView.Contains("TRANSCRIPTION SERVICE", StringComparison.Ordinal));
    True(serverView.Contains("Install recommended transcription setup", StringComparison.Ordinal));

    WithWebServer(async (port, token) =>
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Add("X-RadioVault-Token", token);
        using var response = await client.PostAsJsonAsync(
            $"http://127.0.0.1:{port}{WebApiRoutes.ClientTranscriptionOperation("queue")}",
            new { episodeId = 9L, options = new TranscriptionJobOptions() });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Equal(FakeWebArchiveProvider.JobId.ToString("D"), payload.GetProperty("value").GetString());
    });
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

static void WebQueryFiltersFavourites()
{
    var episodes = new[] { Episode(1, "Ron & Fez", true), Episode(2, "Bennington", false) };
    var result = WebEpisodeQuery.Apply(episodes, "favorites", "", "", 100, new DateTime(2026, 7, 16));
    Equal(1, result.Count);
    Equal(1L, result[0].Id);
}

static void WebQuerySearchesPeople()
{
    var episodes = new[]
    {
        Episode(1, "Ron & Fez", false, people: "Patrice O'Neal"),
        Episode(2, "Bennington", false, people: "Dave Hill")
    };
    var result = WebEpisodeQuery.Apply(episodes, "recent", "Patrice", "", 100, new DateTime(2026, 7, 16));
    Equal(1, result.Count);
    Equal(1L, result[0].Id);
}

static void WebQueryFiltersDateFacetsAndStatus()
{
    var episodes = new[]
    {
        new WebEpisode(1, "Ron & Fez", "January show", new DateTime(2020, 1, 1), "Summary", "", "", 1000, 0, "Unplayed", false, null, DateTime.UtcNow, @"C:\Audio\1.mp3", ""),
        new WebEpisode(2, "Ron & Fez", "February show", new DateTime(2020, 2, 2), "Summary", "", "", 1000, 500, "In Progress", false, DateTime.UtcNow, DateTime.UtcNow, @"C:\Audio\2.mp3", ""),
        new WebEpisode(3, "Bennington", "Completed show", new DateTime(2021, 2, 2), "Summary", "", "", 1000, 1000, "Completed", false, DateTime.UtcNow, DateTime.UtcNow, @"C:\Audio\3.mp3", "")
    };

    var result = WebEpisodeQuery.Apply(episodes, "library", "", "Ron & Fez", 2020, 2, new DateTime(2020, 2, 2), "inprogress", 100, new DateTime(2026, 7, 16));
    Equal(1, result.Count);
    Equal(2L, result[0].CanonicalBroadcastId);
    Equal(2, WebEpisodeQuery.GetYears(episodes).Count);
}

static void WebQueryPaginatesCanonicalLibrary()
{
    var episodes = Enumerable.Range(1, 6).Select(id => Episode(id, "Ron & Fez", false)).ToArray();
    var page = WebEpisodeQuery.ApplyPage(episodes, "library", "", "", null, null, null, "", 2, 2, new DateTime(2026, 7, 16));
    Equal(6, page.Total);
    Equal(2, page.Episodes.Count);
    Equal(4L, page.Episodes[0].Id);
    Equal(3L, page.Episodes[1].Id);
    True(page.HasMore);
}

static void WebEpisodeExposesCanonicalIdentity()
{
    var episode = Episode(7, "Ron & Fez", false);
    Equal(7L, episode.CanonicalBroadcastId);
    Equal("canonical-broadcast", episode.IdentityKind);
}

static WebEpisode Episode(long id, string show, bool favourite, string people = "", long positionMs = 0, long durationMs = 3_600_000)
    => new(id, show, $"Episode {id}", new DateTime(2020, 1, (int)id), "Specific summary", people, "Comedy", durationMs, positionMs,
        positionMs > 0 ? "In Progress" : "Unplayed", favourite, null, new DateTime(2026, 7, 16).AddMinutes(-id), $"C:\\Audio\\{id}.mp3", "");

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

static int FindFreePort()
{
    using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
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

static void CanonicalWebRoutesAreStable()
{
    Equal("/api/v1/broadcasts/42/media-manifest", WebApiRoutes.MediaManifest(42));
    Equal("/api/v1/broadcasts/42/media/99", WebApiRoutes.MediaPart(42,99));
    Equal("/api/v1/broadcasts/42/metadata", WebApiRoutes.BroadcastMetadata(42));
    Equal("/api/v1/transcripts", WebApiRoutes.Transcripts);
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

sealed class CompositionCycleA
{
    public CompositionCycleA(CompositionCycleB dependency) => Dependency = dependency;
    public CompositionCycleB Dependency { get; }
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

sealed class FakeWebArchiveProvider : IWebArchiveProvider
{
    public static readonly Guid JobId = Guid.Parse("99d9f88d-c8d5-4d60-86b0-82157625d7d5");
    private WebEpisode _episode = new(9, "Ron & Fez", "Phoenix test broadcast", new DateTime(2005, 5, 12),
        "A specific test summary.", "Ron Bennington, Fez Whatley", "Comedy", 3_600_000, 120_000, "In Progress", true,
        new DateTime(2026, 7, 16), new DateTime(2026, 7, 16), "C:\\Audio\\9.mp3", string.Empty);
    private readonly List<WebChangeEvent> _changes = new();
    private readonly List<WebQueueItem> _queue = new();
    private WebPlaybackState _desktop;
    private WebPlaybackState _web = new(null, string.Empty, string.Empty, 0, 0, "Idle", null, false, null, "Phone");
    private string _webClient = string.Empty;
    private string _ownerDevice = "Server";
    private string _ownerClientId = string.Empty;
    private long _generation;
    private long _sequence;
    private long _queueId;
    private readonly bool _throwPlayback;
    private readonly PlaybackTransferCoordinator _playbackTransfers = new();
    private WebPlaybackCommittedTransfer? _committedTransfer;
    private WebPlaybackTransferTicket? _committedTicket;

    public FakeWebArchiveProvider(bool throwPlayback = false, string? audioPath = null)
    {
        _throwPlayback = throwPlayback;
        if (!string.IsNullOrWhiteSpace(audioPath))
            _episode = _episode with { AudioPath = audioPath };
        _desktop = new WebPlaybackState(_episode.Id, _episode.Show, _episode.Title, _episode.PositionMs, _episode.DurationMs, _episode.Status, _episode.LastPlayedAt, true, DateTimeOffset.UtcNow, "Server", 1, 100);
        _queue.Add(new WebQueueItem(++_queueId, 0, _episode));
    }

    public IReadOnlyList<WebEpisode> GetEpisodes() => new[] { _episode };
    public WebEpisode? GetEpisode(long episodeId) => episodeId == _episode.Id ? _episode : null;
    public WebBroadcastDetails? GetBroadcastDetails(long episodeId) => episodeId == _episode.Id
        ? new WebBroadcastDetails
        {
            Episode = _episode,
            BroadcastUid = "RON-FEZ-2005-05-12",
            Station = "WJFK",
            Slot = "Afternoon",
            PartNumber = 1,
            TotalParts = 2,
            ArchiveNotes = "Server archive note.",
            People = new[] { new WebPerson("Ron Bennington", "host") },
            Topics = new[] { "Comedy" },
            Moments = new[] { new WebMoment(1, 90_000, "Test moment", "Moment notes") },
            Research = new WebResearchDetails { ResearchBroadcastId = 99, Confidence = 90 }
        }
        : null;
    public WebClientLibraryOverview GetClientLibraryOverview() => new(
        1, 0, 1, 1, 0, true,
        new[] { new WebClientLibraryCollectionSummary(1, _episode.Show, 1) },
        new[] { ClientSummary() },
        new[] { ClientSummary() },
        Array.Empty<WebClientLibraryBroadcastSummary>());
    public WebClientLibraryBroadcastSummary? GetClientLibraryBroadcast(long episodeId)
        => episodeId == _episode.Id ? ClientSummary() : null;
    public WebClientLibraryBrowseResult BrowseClientLibrary(WebClientLibraryBrowseRequest request)
        => new(new[] { ClientSummary() }, 1, true);
    public IReadOnlyList<WebClientLibraryArchivePeriodSummary> GetClientLibraryArchivePeriods(int? collectionId, int? year, bool hideCompleted)
        => new[] { new WebClientLibraryArchivePeriodSummary(2005, "2005", 1, 0, 1, 0, "0 listened · 0%", _episode.Show, null) };
    public WebClientLibrarySearchFacets GetClientLibrarySearchFacets()
        => new(new[] { 2005 }, 1);
    public IReadOnlyList<WebClientLibrarySearchSuggestion> GetClientLibrarySearchSuggestions(string prefix, int limit)
        => new[] { new WebClientLibrarySearchSuggestion(_episode.Title, "Broadcast", 1) };
    public WebClientBroadcastDetails? GetClientBroadcastDetails(long episodeId)
        => episodeId == _episode.Id
            ? new WebClientBroadcastDetails(
                _episode.Id, "CANONICAL-9", "RON-FEZ-2005-05-12", 1, _episode.Show,
                DateOnly.FromDateTime(_episode.AirDate!.Value), "Afternoon", _episode.Title, _episode.Summary,
                "WJFK", string.Empty, string.Empty, string.Empty, string.Empty, "Server archive note.",
                string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                string.Empty, "Ron Bennington", string.Empty, string.Empty, string.Empty, new[] { "Comedy" },
                null, 1, 2, 2)
            : null;
    private WebClientLibraryBroadcastSummary ClientSummary() => new(
        "CANONICAL-9", _episode.Id, "RON-FEZ-2005-05-12", 1, _episode.Show,
        DateOnly.FromDateTime(_episode.AirDate!.Value), new DateTimeOffset(_episode.DateAdded), "Afternoon",
        _episode.Title, _episode.Summary, _episode.Favourite, false, true, _episode.PositionMs,
        _episode.DurationMs, _episode.LastPlayedAt.HasValue ? new DateTimeOffset(_episode.LastPlayedAt.Value) : null,
        null, 1, 2, 2, false, string.Empty, string.Empty, 0);
    public WebTranscriptDetails? GetTranscript(long episodeId) => episodeId == _episode.Id
        ? new WebTranscriptDetails
        {
            CanonicalBroadcastId = episodeId,
            Status = "Complete",
            Language = "en",
            WordCount = 4,
            DurationMs = _episode.DurationMs,
            UpdatedAt = DateTimeOffset.UtcNow,
            Segments = new[] { new WebTranscriptSegment(0, 0, 1000, "Synthetic transcript text.", "Ron", "ron", "Speech", true, 0.99) }
        }
        : null;
    public IReadOnlyList<WebTranscriptSummary> GetTranscripts() => new[]
    {
        new WebTranscriptSummary(1, _episode.Id, _episode.Show, _episode.AirDate, _episode.Title, "Complete", "en", "test", "test", "test", 4, 1, 1, 1, _episode.DurationMs, DateTimeOffset.UtcNow)
    };
    public IReadOnlyList<WebMomentSummary> GetMoments() => Array.Empty<WebMomentSummary>();
    public WebMomentMutationResult AddMoment(long episodeId, WebMomentMutation mutation)
        => episodeId == _episode.Id
            ? new WebMomentMutationResult(true, false, "Added", new WebMoment(1, mutation.PositionMs, mutation.Title, mutation.Notes))
            : new WebMomentMutationResult(false, false, "Not found", null);
    public WebMutationResult DeleteMoment(long episodeId, long momentId)
        => new(episodeId == _episode.Id, episodeId == _episode.Id ? "Deleted" : "Not found");
    public WebMutationResult UpdateMoment(long momentId, WebMomentEditMutation mutation)
        => new(momentId == 1, momentId == 1 ? "Updated" : "Not found");
    public WebCanonicalMediaManifest? GetCanonicalMediaManifest(long episodeId, string? recordingKey = null)
        => episodeId == _episode.Id
            ? new WebCanonicalMediaManifest(_episode.Id, "RON-FEZ-2005-05-12", recordingKey ?? "REC-1", "Preferred", _episode.DurationMs,
                new[] { new WebCanonicalMediaPart(1, 1, 0, _episode.DurationMs, 99, 1234, "AvailableOffline", _episode.AudioPath) })
            : null;
    public WebCanonicalMediaPart? GetCanonicalMediaPart(long episodeId, long mediaFileId, string? recordingKey = null)
        => GetCanonicalMediaManifest(episodeId, recordingKey)?.Parts.FirstOrDefault(x => x.MediaFileId == mediaFileId);
    public WebArchiveHealthSummary GetArchiveHealth() => new(95, 98, 94, 92, 96, 2, 1, 1, 0, DateTime.UtcNow);
    public WebLibraryScanSnapshot GetLibraryScanStatus() => new(false, true, "test", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow,
        "Synthetic scan complete.", 1, 0, 0, 1, 0, 0, 0, 0, 0);
    public Task<WebLibraryScanSnapshot> RunLibraryScanAsync(string trigger, CancellationToken cancellationToken = default)
        => Task.FromResult(GetLibraryScanStatus() with { Trigger = trigger });
    public WebPlaybackState GetPlaybackState() => _desktop;
    public WebPlaybackState GetWebPlaybackState() => _web;
    public WebPlaybackSession GetPlaybackSession()
    {
        if (_throwPlayback) throw new InvalidOperationException("Synthetic playback failure.");
        var remoteOwner = !_ownerDevice.Equals("Server", StringComparison.OrdinalIgnoreCase);
        var player = remoteOwner ? _web : _desktop;
        var ownerId = remoteOwner ? _ownerClientId : "server";
        var devices = new[]
        {
            new WebPlaybackDevice("server", "Radio Vault server", "Server", _desktop, DateTimeOffset.UtcNow, true, !remoteOwner),
            new WebPlaybackDevice(string.IsNullOrWhiteSpace(_ownerClientId) ? "phone-owner-01" : _ownerClientId,
                string.IsNullOrWhiteSpace(_web.Device) ? "Phone" : _web.Device,
                "Phone", _web, DateTimeOffset.UtcNow, true, remoteOwner)
        };
        return new WebPlaybackSession(player, _desktop, _web, _ownerDevice, ownerId, _generation)
        {
            Devices = devices,
            PendingTransfer = _playbackTransfers.Pending(DateTimeOffset.UtcNow),
            CommittedTransfer = _committedTransfer
        };
    }
    public WebPlaybackCommandResult ExecutePlaybackCommand(WebPlaybackCommand command)
    {
        if (command.ExpectedRevision.HasValue && command.ExpectedRevision.Value != _desktop.Revision && !command.Force)
            return new WebPlaybackCommandResult(false, true, "Stale", _desktop);
        var playing = command.Command.Equals("pause", StringComparison.OrdinalIgnoreCase) ? false : true;
        _desktop = _desktop with { IsPlaying = playing, Revision = _desktop.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
        if (playing)
        {
            _ownerDevice = "Server";
            _ownerClientId = string.Empty;
            _generation++;
        }
        AddChange("player", _desktop.EpisodeId ?? 0);
        return new WebPlaybackCommandResult(true, false, "Changed", _desktop);
    }
    public WebClientPlaybackResult UpdateWebPlayback(WebClientPlaybackUpdate update)
    {
        var owns = !_ownerDevice.Equals("Server", StringComparison.OrdinalIgnoreCase) && _ownerClientId == update.ClientId;
        if (!owns || (update.ExpectedGeneration > 0 && update.ExpectedGeneration != _generation))
            return new WebClientPlaybackResult(false, true, "Another client owns playback", _web);
        if (!update.ExplicitSeek && _web.EpisodeId == update.EpisodeId && _web.PositionMs >= 10_000 && update.PositionMs < _web.PositionMs - 3_000)
            return new WebClientPlaybackResult(false, true, "Stale decoder position", _web);
        _webClient = update.IsPlaying ? update.ClientId : string.Empty;
        _web = new WebPlaybackState(_episode.Id, _episode.Show, _episode.Title, update.PositionMs,
            update.DurationMs > 0 ? update.DurationMs : _episode.DurationMs, "In Progress", DateTime.Now,
            update.IsPlaying, DateTimeOffset.UtcNow, string.IsNullOrWhiteSpace(update.DeviceName) ? "Phone" : update.DeviceName,
            update.Speed, _web.Revision + 1, update.ClientId);
        return new WebClientPlaybackResult(true, false, "Saved", _web);
    }
    public WebPlaybackTransferResult BeginPlaybackTransfer(WebPlaybackTransferBeginRequest request)
    {
        try
        {
            var ticket = _playbackTransfers.Begin(request with
            {
                PositionMs = Math.Max(Math.Max(0, request.PositionMs), Math.Max(0, _episode.PositionMs)),
                DurationMs = Math.Max(request.DurationMs, _episode.DurationMs)
            }, TransferAuthority(), DateTimeOffset.UtcNow);
            return new WebPlaybackTransferResult(true, false, "Preparing", ticket, GetPlaybackSession());
        }
        catch (PlaybackTransferConflictException exception)
        {
            return new WebPlaybackTransferResult(false, true, exception.Message, null, GetPlaybackSession());
        }
    }
    public WebPlaybackTransferResult MarkPlaybackTransferReady(WebPlaybackTransferReadyRequest request)
    {
        try
        {
            var ticket = _playbackTransfers.MarkReady(request, TransferAuthority(), DateTimeOffset.UtcNow);
            return new WebPlaybackTransferResult(true, false, "Ready", ticket, GetPlaybackSession());
        }
        catch (PlaybackTransferConflictException exception)
        {
            return new WebPlaybackTransferResult(false, true, exception.Message, null, GetPlaybackSession());
        }
    }
    public WebPlaybackTransferResult CommitPlaybackTransfer(WebPlaybackTransferCommitRequest request)
    {
        var currentOwnerClientId = _ownerDevice.Equals("Server", StringComparison.OrdinalIgnoreCase)
            ? "server" : _ownerClientId;
        if (_committedTransfer is not null && _committedTicket is not null &&
            _committedTransfer.TransferId == request.TransferId &&
            string.Equals(_committedTransfer.TargetClientId, request.ClientId, StringComparison.Ordinal) &&
            string.Equals(currentOwnerClientId, request.ClientId, StringComparison.Ordinal) &&
            _committedTransfer.Generation == _generation &&
            (_ownerDevice.Equals("Server", StringComparison.OrdinalIgnoreCase)
                ? _desktop.EpisodeId == _committedTicket.TargetEpisodeId
                : _web.EpisodeId == _committedTicket.TargetEpisodeId))
            return new WebPlaybackTransferResult(true, false, "Already committed", _committedTicket, GetPlaybackSession());

        try
        {
            var authority = TransferAuthority();
            var ticket = _playbackTransfers.Commit(request, authority, DateTimeOffset.UtcNow);
            _ownerDevice = ticket.TargetClientId.Equals("server", StringComparison.OrdinalIgnoreCase)
                ? "Server" : ticket.TargetDeviceName;
            _ownerClientId = ticket.TargetClientId.Equals("server", StringComparison.OrdinalIgnoreCase)
                ? string.Empty : ticket.TargetClientId;
            _generation++;
            var sourceClientId = authority.OwnerClientId;
            var acknowledged = !authority.IsPlaying || string.Equals(sourceClientId, ticket.TargetClientId, StringComparison.Ordinal);
            _committedTicket = ticket;
            _committedTransfer = new WebPlaybackCommittedTransfer(
                ticket.TransferId, sourceClientId, authority.OwnerDevice,
                ticket.TargetClientId, ticket.TargetDeviceName, _generation,
                authority.IsPlaying, acknowledged, DateTimeOffset.UtcNow,
                acknowledged ? DateTimeOffset.UtcNow : null);
            _episode = _episode with
            {
                PositionMs = Math.Max(_episode.PositionMs, ticket.CommitPositionMs),
                DurationMs = ticket.DurationMs,
                Status = ticket.CommitPositionMs > 0 ? "In Progress" : "Unplayed",
                LastPlayedAt = DateTime.Now
            };
            if (!ticket.TargetClientId.Equals("server", StringComparison.OrdinalIgnoreCase))
                _web = new WebPlaybackState(ticket.TargetEpisodeId, _episode.Show, _episode.Title,
                    ticket.CommitPositionMs, ticket.DurationMs, "In Progress", DateTime.Now,
                    ticket.DesiredPlaying, DateTimeOffset.UtcNow, ticket.TargetDeviceName,
                    ticket.Speed, _web.Revision + 1, ticket.TargetClientId);
            else
                _desktop = _desktop with { PositionMs = ticket.CommitPositionMs, DurationMs = ticket.DurationMs,
                    IsPlaying = ticket.DesiredPlaying, UpdatedAt = DateTimeOffset.UtcNow, Revision = _desktop.Revision + 1 };
            return new WebPlaybackTransferResult(true, false, "Committed", ticket, GetPlaybackSession());
        }
        catch (PlaybackTransferConflictException exception)
        {
            return new WebPlaybackTransferResult(false, true, exception.Message, null, GetPlaybackSession());
        }
    }
    public WebPlaybackTransferResult CancelPlaybackTransfer(WebPlaybackTransferCancelRequest request)
    {
        var changed = _playbackTransfers.Cancel(request, DateTimeOffset.UtcNow);
        return new WebPlaybackTransferResult(changed, false, changed ? "Cancelled" : "Inactive", null, GetPlaybackSession());
    }
    public WebPlaybackTransferResult AcknowledgePlaybackTransferSourceStopped(WebPlaybackTransferSourceStoppedRequest request)
    {
        if (_committedTransfer is null || _committedTicket is null ||
            _committedTransfer.TransferId != request.TransferId ||
            _committedTransfer.Generation != request.Generation ||
            !string.Equals(_committedTransfer.SourceClientId, request.ClientId, StringComparison.Ordinal))
            return new WebPlaybackTransferResult(false, true, "Stale acknowledgement", null, GetPlaybackSession());
        _committedTransfer = _committedTransfer with
        {
            SourceStopAcknowledged = true,
            SourceStoppedAt = DateTimeOffset.UtcNow
        };
        return new WebPlaybackTransferResult(true, false, "Source stopped", _committedTicket, GetPlaybackSession());
    }
    private PlaybackTransferAuthority TransferAuthority()
    {
        var server = _ownerDevice.Equals("Server", StringComparison.OrdinalIgnoreCase);
        var state = server ? _desktop : _web;
        return new PlaybackTransferAuthority(_ownerDevice, server ? "server" : _ownerClientId, _generation,
            state.EpisodeId, state.PositionMs, state.DurationMs, state.Speed, state.IsPlaying,
            state.UpdatedAt ?? DateTimeOffset.UtcNow);
    }
    public WebOfflineProgressResult SyncOfflineProgress(WebOfflineProgressUpdate update)
    {
        if (update.EpisodeId != _episode.Id) return new WebOfflineProgressResult(false, "Not found");
        if (update.AllowRewind &&
            (_ownerDevice.Equals("Server", StringComparison.OrdinalIgnoreCase) ||
             _ownerClientId != update.ClientId ||
             (update.ExpectedGeneration > 0 && update.ExpectedGeneration != _generation)))
            return new WebOfflineProgressResult(false, "Another client owns playback", _episode, Conflict: true);
        var generationBoundOwnerWrite = update.AllowRewind && update.ExpectedGeneration > 0;
        if (generationBoundOwnerWrite && !update.ExplicitSeek &&
            _episode.PositionMs >= 10_000 && update.PositionMs < _episode.PositionMs - 3_000)
            return new WebOfflineProgressResult(false, "Stale durable progress", _episode, Conflict: true);
        var mayResetPosition = generationBoundOwnerWrite && update.ExplicitSeek;
        if (!update.Completed && !mayResetPosition && update.PositionMs <= _episode.PositionMs)
            return new WebOfflineProgressResult(false, "Newer progress exists", _episode);
        var duration = update.DurationMs > 0 ? update.DurationMs : _episode.DurationMs;
        _episode = _episode with
        {
            PositionMs = mayResetPosition ? update.PositionMs : Math.Max(_episode.PositionMs, update.PositionMs),
            DurationMs = duration,
            Status = update.Completed ? "Completed" : "In Progress",
            LastPlayedAt = DateTime.Now
        };
        return new WebOfflineProgressResult(true, "Offline progress saved", _episode);
    }
    public IReadOnlyList<WebQueueItem> GetQueue() => _queue.OrderBy(x => x.Position).ToArray();
    public WebQueueMutationResult AddToQueue(long episodeId, bool playNext)
    {
        if (episodeId != _episode.Id) return new WebQueueMutationResult(false, "Not found", GetQueue());
        if (playNext)
        {
            for (var i = 0; i < _queue.Count; i++) _queue[i] = _queue[i] with { Position = _queue[i].Position + 1 };
        }
        _queue.Add(new WebQueueItem(++_queueId, playNext ? 0 : _queue.Count, _episode));
        return new WebQueueMutationResult(true, "Added", GetQueue());
    }
    public WebQueueMutationResult RemoveFromQueue(long queueId)
    {
        var changed = _queue.RemoveAll(x => x.QueueId == queueId) > 0;
        NormalizeQueue();
        return new WebQueueMutationResult(changed, changed ? "Removed" : "Not found", GetQueue());
    }
    public WebQueueMutationResult ClearQueue()
    {
        var changed = _queue.Count > 0;
        _queue.Clear();
        return new WebQueueMutationResult(changed, "Cleared", GetQueue());
    }
    public WebQueueMutationResult MoveQueueItem(long queueId, int direction)
    {
        var index = _queue.FindIndex(x => x.QueueId == queueId);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= _queue.Count) return new WebQueueMutationResult(false, "Cannot move", GetQueue());
        (_queue[index], _queue[target]) = (_queue[target], _queue[index]);
        NormalizeQueue();
        return new WebQueueMutationResult(true, "Moved", GetQueue());
    }
    public IReadOnlyList<WebChangeEvent> GetChanges(long afterSequence, int limit) => _changes.Where(x => x.Sequence > afterSequence).Take(limit).ToArray();
    public WebChangeFeedSnapshot GetChangeFeed(long afterSequence, int limit)
    {
        var current = _sequence;
        var earliest = _changes.Count == 0 ? current + 1 : _changes.Min(x => x.Sequence);
        return new WebChangeFeedSnapshot(current, earliest, GetChanges(afterSequence, limit));
    }
    public IReadOnlyList<WebJobSummary> GetJobs() => new[] { new WebJobSummary(JobId, "Test job", "General", "Running", 50, "Working", true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null) };
    public WebJobActionResult CancelJob(Guid jobId) => jobId == JobId
        ? new WebJobActionResult(true, "Cancellation requested.")
        : new WebJobActionResult(false, "Not found.");
    public WebMutationResult SetFavourite(long episodeId, bool favourite)
    {
        if (episodeId != _episode.Id) return new WebMutationResult(false, "Not found");
        _episode = _episode with { Favourite = favourite };
        AddChange("favourite", episodeId);
        return new WebMutationResult(true, "Changed", _episode);
    }
    public WebMutationResult SetPlayed(long episodeId, bool played)
    {
        if (episodeId != _episode.Id) return new WebMutationResult(false, "Not found");
        _episode = _episode with { Status = played ? "Completed" : "Unplayed", PositionMs = played ? _episode.DurationMs : 0 };
        AddChange("listening-status", episodeId);
        return new WebMutationResult(true, "Changed", _episode);
    }
    public WebMutationResult UpdateBroadcastMetadata(long episodeId, WebBroadcastMetadataMutation mutation)
    {
        if (episodeId != _episode.Id) return new WebMutationResult(false, "Not found");
        _episode = _episode with { Title = mutation.Title, Summary = mutation.Description };
        AddChange("metadata", episodeId);
        return new WebMutationResult(true, "Saved", _episode);
    }
    public WebAuthoritativeSettingsSnapshot GetAuthoritativeSettings()
        => new(
            Array.Empty<WebArchiveFolderSnapshot>(),
            new WebStorageSnapshot(1, 1, 0, 0, 1234),
            new WebPreservationSnapshot(1, 1, 0, 0, 1, 0, 0, DateTimeOffset.UtcNow),
            GetArchiveHealth(),
            new WebPlaybackPreferencesSnapshot(15, 30, 90, DateTimeOffset.UtcNow),
            0,
            "ok",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    public WebResearchWorkspaceSnapshot GetResearchWorkspace()
        => new(
            new WebResearchWorkspaceOverview(1, 1, 0, 0, 0, 0, 0, 0, 1, 0),
            new[]
            {
                new WebResearchWorkspaceRecord(99, _episode.Id, "RON-FEZ-2005-05-12", _episode.Show, _episode.AirDate,
                    "Standard", 1, null, _episode.Title, _episode.Summary, "researched", "in_library", 90,
                    "Synthetic test evidence", false, 0, 0, 1, 1, 1, 0, DateTime.UtcNow)
            },
            Array.Empty<WebResearchWorkspaceImport>(),
            new[] { new WebResearchWorkspaceSourceSummary("Test publisher", "archive", "example.test", 1, 1, 90, DateTime.UtcNow) },
            DateTimeOffset.UtcNow);
    public WebResearchWorkspaceRecordDetails? GetResearchWorkspaceRecord(long researchBroadcastId)
        => researchBroadcastId == 99
            ? new WebResearchWorkspaceRecordDetails(
                GetResearchWorkspace().Records[0], "XM", "", "", "Talk radio", "Synthetic archive note",
                new[] { "Ron Bennington" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                new[] { "Comedy" },
                new[] { new WebResearchWorkspaceSource("https://example.test/source", "Test source", "Test publisher", "archive", 90, "2026-07-24", new[] { "headline" }, "") },
                Array.Empty<WebResearchWorkspaceMoment>(), Array.Empty<WebResearchWorkspaceConflict>())
            : null;
    public IReadOnlyList<WebUndatedBroadcast> GetUndatedResearchBroadcasts(int? collectionId = null)
        => Array.Empty<WebUndatedBroadcast>();
    public WebAssignBroadcastDateResult AssignResearchBroadcastDate(long episodeId, DateTime broadcastDate)
        => new(episodeId, broadcastDate.Date, episodeId == _episode.Id);
    public WebResearchCoverageShow? GetResearchCoverage(int collectionId)
        => new(collectionId, _episode.Show, _episode.AirDate!.Value.Date, _episode.AirDate.Value.Date,
            new[] { new WebResearchCoverageDay(_episode.AirDate.Value.Date, false, true, true, false, 1, 100, string.Empty, _episode.Id, 99) });
    public WebResearchCoverageShow? GetResearchCoverageByShow(string show)
        => string.Equals(show, _episode.Show, StringComparison.OrdinalIgnoreCase) ? GetResearchCoverage(1) : null;
    public WebPlaybackPreferencesSnapshot SetPlaybackPreferences(WebPlaybackPreferencesSnapshot preferences) => preferences;
    public Task<object?> ExecuteClientResearchAsync(string operation, JsonElement payload, CancellationToken cancellationToken = default)
        => Task.FromResult<object?>(operation == "overview" ? GetResearchWorkspace().Overview : true);
    public Task<object?> ExecuteClientTranscriptAsync(string operation, JsonElement payload, CancellationToken cancellationToken = default)
        => Task.FromResult<object?>(operation == "jobs" ? Array.Empty<TranscriptionJobRecord>() : true);
    public Task<object?> ExecuteClientSpeakerAsync(string operation, JsonElement payload, CancellationToken cancellationToken = default)
        => Task.FromResult<object?>(Array.Empty<object>());
    public Task<object?> ExecuteClientTranscriptionAsync(string operation, JsonElement payload, CancellationToken cancellationToken = default)
        => Task.FromResult<object?>(operation switch
        {
            "jobs" => Array.Empty<TranscriptionJobRecord>(),
            "queue" or "retry" => JobId,
            _ => true
        });
    public Task<object?> ExecuteClientWikiAsync(string operation, JsonElement payload, CancellationToken cancellationToken = default)
        => Task.FromResult<object?>(operation switch
        {
            "overview" => new WikiOverview(0, 0, 0, 0, 0, 0, 0, null, null),
            "browse" => Array.Empty<WikiPageSummary>(),
            _ => null
        });
    public Task<WebResearchPackPreviewResponse> PreviewResearchPackAsync(byte[] packageBytes, string sourceName, CancellationToken cancellationToken = default)
        => Task.FromResult(new WebResearchPackPreviewResponse(
            Guid.NewGuid(),
            new WebResearchPackPreview(
                sourceName, _episode.Show, 3, 2, 1, 0, 0, 0, 1, 1,
                0, 0, 4, 1, 8, 0, false, false, "test-package-hash", 1),
            DateTimeOffset.UtcNow.AddMinutes(20)));
    public WebResearchPackImportJob StartResearchPackImport(Guid sessionId)
        => CompletedResearchImport(sessionId);
    public WebResearchPackImportJob GetResearchPackImportStatus(Guid sessionId)
        => CompletedResearchImport(sessionId);
    public bool CancelResearchPackImport(Guid sessionId) => false;
    public Task<WebResearchPackExportPayload> ExportResearchPackAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<WebWikiPackPreview> PreviewWikiPackAsync(byte[] packageBytes, string sourceName, CancellationToken cancellationToken = default)
        => Task.FromResult(new WebWikiPackPreview(sourceName, "hash", 1, 1, 0, 0, 0, 1, 1, 1, 1, Array.Empty<string>(), true, "Ready", Array.Empty<WebWikiPackPageChangePreview>()));
    public Task<WebWikiPackImportResult> ApplyWikiPackAsync(byte[] packageBytes, string sourceName, string expectedSha256, CancellationToken cancellationToken = default)
        => Task.FromResult(new WebWikiPackImportResult(1, 0, 0, 0, 1, 1, 1, 1, 1, "Imported"));
    public Task<WebWikiPackExportPayload> ExportWikiPackAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new WebWikiPackExportPayload(Array.Empty<byte>(), "test.rvwiki", 0, 0));

    private static WebResearchPackImportJob CompletedResearchImport(Guid sessionId)
        => new(sessionId, JobId, "Completed", 100, "Complete", 1, 1, false,
            new WebResearchPackImportResult(1, 1, 1, 0, 0, 0, 1, 1, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0));

    private void NormalizeQueue()
    {
        for (var i = 0; i < _queue.Count; i++) _queue[i] = _queue[i] with { Position = i };
    }
    private void AddChange(string kind, long episodeId) => _changes.Add(new WebChangeEvent(++_sequence, kind, episodeId, "test", DateTimeOffset.UtcNow));
}

sealed class FakeWhisperDownloadHandler : HttpMessageHandler
{
    private readonly byte[] _workerArchive;
    private readonly string _workerDigest;

    public FakeWhisperDownloadHandler()
    {
        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "Release/whisper-cli.exe", new byte[] { 1, 2, 3, 4 });
            WriteEntry(archive, "Release/whisper.dll", new byte[] { 5, 6, 7, 8 });
        }
        _workerArchive = stream.ToArray();
        _workerDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(_workerArchive)).ToLowerInvariant();
    }

    public bool ReleaseRequested { get; private set; }
    public bool WorkerRequested { get; private set; }
    public bool ModelRequested { get; private set; }
    public bool VadRequested { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("A request URI is required.");
        if (uri.Host == "api.github.com")
        {
            ReleaseRequested = true;
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                tag_name = "v-test",
                assets = new[]
                {
                    new
                    {
                        name = "whisper-bin-x64.zip",
                        browser_download_url = "https://github.com/ggml-org/whisper.cpp/releases/download/v-test/worker.zip",
                        digest = $"sha256:{_workerDigest}",
                        size = _workerArchive.Length
                    }
                }
            });
            return Task.FromResult(Response(new StringContent(json, Encoding.UTF8, "application/json")));
        }
        if (uri.Host == "github.com")
        {
            WorkerRequested = true;
            return Task.FromResult(Response(new ByteArrayContent(_workerArchive)));
        }
        if (uri.Host == "huggingface.co" && uri.AbsolutePath.Contains("whisper-vad", StringComparison.OrdinalIgnoreCase))
        {
            VadRequested = true;
            return Task.FromResult(Response(new ByteArrayContent(Enumerable.Repeat((byte)9, 512).ToArray())));
        }
        if (uri.Host == "huggingface.co")
        {
            ModelRequested = true;
            return Task.FromResult(Response(new ByteArrayContent(Enumerable.Repeat((byte)10, 1024).ToArray())));
        }
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Response(HttpContent content)
        => new(System.Net.HttpStatusCode.OK) { Content = content };

    private static void WriteEntry(System.IO.Compression.ZipArchive archive, string path, byte[] contents)
    {
        var entry = archive.CreateEntry(path);
        using var target = entry.Open();
        target.Write(contents);
    }
}
