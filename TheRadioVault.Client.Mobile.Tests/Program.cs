using System.Text.Json;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Downloads;
using TheRadioVault.Client.Mobile.Explore;
using TheRadioVault.Client.Mobile.Knowledge;
using TheRadioVault.Client.Mobile.Library;
using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Client.Mobile.Pairing;
using TheRadioVault.Client.Mobile.Platform;
using TheRadioVault.Client.Mobile.Playback;
using TheRadioVault.Client.Mobile.Synchronization;
using TheRadioVault.Web.Models;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Downloaded progress stays isolated per broadcast", DownloadedProgressStaysIsolatedAsync),
    ("Completed download progress survives an app restart", CompletedProgressSurvivesRestartAsync),
    ("Reconnect preserves newer offline progress", ReconnectPreservesNewerOfflineProgressAsync),
    ("Reconnect accepts newer server progress for only its broadcast", ReconnectAcceptsNewerServerProgressAsync),
    ("Offline mutations keep one latest decision per broadcast", OfflineMutationsStayIsolatedAsync),
    ("Handoff keeps its transactional ownership boundary", HandoffKeepsTransactionalBoundaryAsync),
    ("Legacy playback ownership requires stable evidence", LegacyPlaybackOwnershipRequiresStableEvidenceAsync),
    ("Committed handoff ownership is trusted immediately", CommittedHandoffOwnershipIsTrustedImmediatelyAsync),
    ("Remote playback observation projects the shared playhead", RemotePlaybackObservationProjectsPlayheadAsync),
    ("Uncommitted remote observations cannot steal local playback", UncommittedObservationCannotStealLocalPlaybackAsync),
    ("Committed handoff stops and acknowledges the source", CommittedHandoffStopsAndAcknowledgesSourceAsync),
    ("Metadata synchronization persists a complete cache", MetadataSynchronizationPersistsCompleteCacheAsync),
    ("Saved collections remain readable from the offline cache", SavedCollectionsRemainReadableOfflineAsync),
    ("Metadata synchronization applies changed and deleted broadcasts", MetadataSynchronizationAppliesDeltaAsync),
    ("Metadata synchronization serializes concurrent refreshes", MetadataSynchronizationSerializesRefreshesAsync),
    ("Metadata synchronization failure preserves the cache", MetadataSynchronizationFailurePreservesCacheAsync),
    ("Offline mutation sync preserves order and accepts duplicate Moments", OfflineMutationSyncPreservesOrderAsync),
    ("Offline mutation sync recognizes an already-applied decision", OfflineMutationSyncRecognizesAppliedDecisionAsync),
    ("Offline mutation sync retains the first failure", OfflineMutationSyncRetainsFirstFailureAsync),
    ("Offline mutation sync serializes concurrent flushes", OfflineMutationSyncSerializesFlushesAsync),
    ("Download coordinator enforces network policy and selects new broadcasts", DownloadCoordinatorEnforcesPolicyAsync),
    ("Download coordinator preserves pause and resume state", DownloadCoordinatorPreservesPauseResumeAsync),
    ("Download coordinator protects active media and reconciles summaries", DownloadCoordinatorProtectsAndReconcilesAsync),
    ("Download coordinator expires old media without redownload loops", DownloadCoordinatorExpiresWithoutRedownloadingAsync),
    ("Downloaded progress synchronization adopts the canonical result", DownloadedProgressSynchronizationAdoptsCanonicalAsync),
    ("Downloaded progress synchronization preserves conflicts and newer offline state", DownloadedProgressSynchronizationPreservesAuthorityAsync),
    ("Explore coordinator warms pages and images into the offline cache", ExploreCoordinatorWarmsOfflineCacheAsync),
    ("Explore coordinator serializes concurrent cache refreshes", ExploreCoordinatorSerializesRefreshesAsync),
    ("Knowledge coordinator builds offline Library coverage", KnowledgeCoordinatorBuildsOfflineCoverageAsync),
    ("Knowledge coordinator persists live snapshots", KnowledgeCoordinatorPersistsLiveSnapshotAsync),
    ("Knowledge coordinator sends explicit date-review decisions", KnowledgeCoordinatorSendsDateReviewDecisionAsync),
    ("Pairing coordinator preserves discovery state across failures", PairingCoordinatorPreservesDiscoveryStateAsync),
    ("Pairing coordinator owns pair and forget transitions", PairingCoordinatorOwnsPairAndForgetAsync),
    ("Library coordinator projects and filters the cached catalogue", LibraryCoordinatorProjectsCachedCatalogueAsync),
    ("Library coordinator combines duplicate live show identities", LibraryCoordinatorCombinesLiveShowIdentitiesAsync),
    ("Library coordinator keeps archive search queries explicit", LibraryCoordinatorKeepsArchiveSearchExplicitAsync),
    ("Playback timeline maps multipart recordings", PlaybackTimelineMapsMultipartRecordingsAsync),
    ("Playback timeline protects decoder settling", PlaybackTimelineProtectsDecoderSettlingAsync),
    ("Playback timeline preserves completion until a real rewind", PlaybackTimelinePreservesCompletionAsync),
    ("Live Radio stays outside personal playback state", LiveRadioStaysOutsidePersonalPlaybackStateAsync)
};

var selectedTests = args.Length == 0
    ? tests
    : tests.Where(test => args.Any(filter => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
if (selectedTests.Length == 0)
{
    Console.Error.WriteLine("No mobile tests matched the supplied filters.");
    return 2;
}

var failures = new List<string>();
foreach (var test in selectedTests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add(test.Name);
        Console.Error.WriteLine($"FAIL  {test.Name}\n      {exception.Message}");
    }
}

Console.WriteLine($"\n{selectedTests.Length - failures.Count}/{selectedTests.Length} mobile regression checks passed.");
if (failures.Count > 0)
{
    Console.Error.WriteLine("Failed: " + string.Join(", ", failures));
    return 1;
}
return 0;

static async Task DownloadedProgressStaysIsolatedAsync()
{
    await WithSeededDownloadsAsync(async service =>
    {
        var capturedAt = DateTimeOffset.UtcNow;
        Ensure(await service.UpdateProgressAsync(101, 69_000, false, capturedAt), "The first progress update was ignored.");
        var first = await service.GetAsync(101) ?? throw new InvalidOperationException("Episode 101 disappeared.");
        var second = await service.GetAsync(202) ?? throw new InvalidOperationException("Episode 202 disappeared.");
        Equal(69_000L, first.Summary.PositionMs, "Episode 101 position");
        Equal(0L, second.Summary.PositionMs, "Episode 202 position");
        Ensure(!second.Summary.InProgress && !second.Summary.Completed, "Episode 202 inherited episode 101's listening state.");
    });
}

static async Task CompletedProgressSurvivesRestartAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        await SeedDownloadsAsync(root);
        using (var server = new MobileServerClient(new MemoryConnectionStore()))
        {
            var service = new MobileDownloadService(server, root);
            Ensure(await service.UpdateProgressAsync(101, 100_000, true, DateTimeOffset.UtcNow), "Completion was not stored.");
        }

        using var reopenedServer = new MobileServerClient(new MemoryConnectionStore());
        var reopened = new MobileDownloadService(reopenedServer, root);
        var first = await reopened.GetAsync(101) ?? throw new InvalidOperationException("Episode 101 was not restored.");
        var second = await reopened.GetAsync(202) ?? throw new InvalidOperationException("Episode 202 was not restored.");
        Ensure(first.Summary.Completed, "Episode 101 lost its completed state after restart.");
        Equal(100_000L, first.Summary.PositionMs, "Episode 101 completed position");
        Equal(0L, second.Summary.PositionMs, "Episode 202 position after restart");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static async Task ReconnectPreservesNewerOfflineProgressAsync()
{
    await WithSeededDownloadsAsync(async service =>
    {
        var offlineAt = DateTimeOffset.UtcNow;
        await service.UpdateProgressAsync(101, 100_000, true, offlineAt);
        var staleServer = Summary(101, "First Broadcast", 69_000, false, offlineAt.AddMinutes(-10));

        await service.ReconcileSummariesAsync([staleServer]);

        var local = await service.GetAsync(101) ?? throw new InvalidOperationException("Episode 101 disappeared.");
        Ensure(local.Summary.Completed, "A stale server response overwrote newer offline completion.");
        Equal(100_000L, local.Summary.PositionMs, "Episode 101 position after stale reconciliation");
    });
}

static async Task ReconnectAcceptsNewerServerProgressAsync()
{
    await WithSeededDownloadsAsync(async service =>
    {
        var localAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await service.UpdateProgressAsync(202, 10_000, false, localAt);
        var newerServer = Summary(202, "Second Broadcast", 42_000, false, DateTimeOffset.UtcNow);

        await service.ReconcileSummariesAsync([newerServer]);

        var first = await service.GetAsync(101) ?? throw new InvalidOperationException("Episode 101 disappeared.");
        var second = await service.GetAsync(202) ?? throw new InvalidOperationException("Episode 202 disappeared.");
        Equal(0L, first.Summary.PositionMs, "Unrelated episode 101 position");
        Equal(42_000L, second.Summary.PositionMs, "Episode 202 reconciled position");
    });
}

static async Task OfflineMutationsStayIsolatedAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        var store = new MobileOfflineMutationStore(root);
        await store.EnqueueListeningStatusAsync("server-a", 101, true);
        await store.EnqueueListeningStatusAsync("server-a", 202, false);
        await store.EnqueueListeningStatusAsync("server-a", 101, false);
        await store.EnqueueFavouriteAsync("server-a", 101, true);
        await store.EnqueueMomentAsync("server-a", 101, 35_000, "Moment", "", "mutation-101");
        await store.EnqueueListeningStatusAsync("server-b", 303, true);

        var pending = await store.GetPendingAsync("server-a");
        Equal(4, pending.Count, "Pending mutations for server A");
        var episode101Listening = pending.Single(value =>
            value.EpisodeId == 101 && value.Kind == MobileOfflineMutationKind.ListeningStatus);
        Ensure(episode101Listening.BooleanValue == false, "The latest episode 101 decision did not replace the earlier one.");
        Ensure(pending.Single(value => value.EpisodeId == 202).BooleanValue == false,
            "Episode 202 inherited another broadcast's decision.");
        Ensure(pending.All(value => value.ServerInstanceId == "server-a"), "A different server's mutations leaked into this queue.");
        Ensure(pending.All(value => !string.IsNullOrWhiteSpace(value.MutationId)),
            "An offline decision was stored without a durable mutation id.");

        var reopened = new MobileOfflineMutationStore(root);
        Equal(4, (await reopened.GetPendingAsync("server-a")).Count, "Persisted pending mutations");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static Task HandoffKeepsTransactionalBoundaryAsync()
{
    var root = FindRepositoryRoot();
    var sessionSource = File.ReadAllText(Path.Combine(root, "TheRadioVault.Client.Mobile", "MobileClientSession.cs"));
    var ownershipSource = File.ReadAllText(Path.Combine(
        root,
        "TheRadioVault.Client.Mobile",
        "Playback",
        "MobilePlaybackOwnershipCoordinator.cs"));
    var synchronizationSource = File.ReadAllText(Path.Combine(
        root,
        "TheRadioVault.Client.Mobile",
        "Playback",
        "MobilePlaybackSynchronizationCoordinator.cs"));
    Contains(sessionSource, "BeginPlaybackTransferAsync", "handoff begin request");
    Contains(sessionSource, "WaitForDecoderReadyAsync(startup, desiredPlaying)", "decoder readiness wait");
    Contains(sessionSource, "WaitForSourceStopAsync", "handoff source-stop wait");
    Contains(sessionSource, "CommitPlaybackTransferAsync", "handoff commit request");
    Contains(sessionSource, "CancelPlaybackTransferAsync", "handoff cancellation path");
    var decoderReady = sessionSource.IndexOf("WaitForDecoderReadyAsync(startup, desiredPlaying)", StringComparison.Ordinal);
    var transferReady = sessionSource.IndexOf("MarkPlaybackTransferReadyAsync", StringComparison.Ordinal);
    Ensure(decoderReady >= 0 && transferReady > decoderReady,
        "The iPhone reported a handoff target ready before its decoder was ready.");
    Contains(ownershipSource, "receipt.Generation == session.Generation", "committed handoff generation guard");
    Contains(sessionSource, "PlaybackTransferAlignmentToleranceMs = 3_000", "live-source alignment tolerance");
    Contains(sessionSource, "<= PlaybackTransferAlignmentToleranceMs", "alignment tolerance use");
    Contains(synchronizationSource, "WasCommittedAwayFromThisDevice", "uncommitted-owner rejection");
    Contains(synchronizationSource, "AcknowledgePlaybackSourceStoppedAsync", "source-stop acknowledgement");
    Contains(ownershipSource, "_foreignOwnerCandidateSamples >= 2", "stable foreign-owner evidence");
    var downloadedBranchStart = sessionSource.IndexOf("if (_offlinePlayback)", StringComparison.Ordinal);
    var streamedBranchStart = sessionSource.IndexOf("if (!IsPaired) return;", downloadedBranchStart, StringComparison.Ordinal);
    Ensure(downloadedBranchStart >= 0 && streamedBranchStart > downloadedBranchStart,
        "The downloaded-playback synchronisation branch could not be located.");
    var downloadedBranch = sessionSource[downloadedBranchStart..streamedBranchStart];
    Contains(downloadedBranch, "GetPlaybackSessionAsync", "downloaded playback ownership polling");
    Contains(downloadedBranch, "StopForCommittedTransferAsync", "downloaded playback source-stop acknowledgement");
    Contains(downloadedBranch, "ReportLivePlaybackAsync", "downloaded playback ownership publication");
    return Task.CompletedTask;
}

static Task LiveRadioStaysOutsidePersonalPlaybackStateAsync()
{
    var root = FindRepositoryRoot();
    var source = File.ReadAllText(Path.Combine(root, "TheRadioVault.Client.Mobile", "MobileClientSession.cs"));
    var synchronizeStart = source.IndexOf("private async Task SynchronizePlaybackAsync", StringComparison.Ordinal);
    var observeStart = source.IndexOf("private async Task ObserveSharedPlaybackAsync", synchronizeStart, StringComparison.Ordinal);
    Ensure(synchronizeStart >= 0 && observeStart > synchronizeStart,
        "The mobile playback synchronization boundary could not be located.");
    var synchronization = source[synchronizeStart..observeStart];
    var liveBranch = synchronization.IndexOf("if (IsLiveRadioTunedIn)", StringComparison.Ordinal);
    var ordinaryBranch = synchronization.IndexOf("if (_offlinePlayback)", StringComparison.Ordinal);
    Ensure(liveBranch >= 0 && ordinaryBranch > liveBranch,
        "Live Radio did not exit before ordinary progress synchronization.");
    Contains(synchronization[liveBranch..ordinaryBranch], "await SynchronizeLiveRadioAsync()", "isolated live synchronization");
    Contains(synchronization[liveBranch..ordinaryBranch], "return;", "live synchronization early return");

    var liveStart = source.IndexOf("private async Task SynchronizeLiveRadioAsync", StringComparison.Ordinal);
    Ensure(liveStart >= 0 && observeStart > liveStart, "The Live Radio synchronization method could not be located.");
    var liveSynchronization = source[liveStart..observeStart];
    Ensure(!liveSynchronization.Contains("UpdateProgress", StringComparison.Ordinal),
        "Live Radio synchronization writes personal progress.");
    Ensure(!liveSynchronization.Contains("ReportLivePlayback", StringComparison.Ordinal),
        "Live Radio synchronization publishes shared playback ownership.");
    Ensure(!liveSynchronization.Contains("PlaybackTransfer", StringComparison.Ordinal),
        "Live Radio synchronization participates in handoff.");
    Contains(source, "IsLiveRadioTunedIn\n            ? Task.CompletedTask", "live progress flush suppression");
    return Task.CompletedTask;
}

static Task LegacyPlaybackOwnershipRequiresStableEvidenceAsync()
{
    var ownership = new MobilePlaybackOwnershipCoordinator(() => "iphone-client");
    var first = PlaybackSession("mac-client", generation: 7, isPlaying: true);
    Ensure(!ownership.ConfirmForeignOwner(first), "The first legacy foreign-owner sample was trusted too early.");
    Ensure(ownership.ConfirmForeignOwner(first), "A stable second legacy foreign-owner sample was not trusted.");

    var newGeneration = PlaybackSession("mac-client", generation: 8, isPlaying: true);
    Ensure(!ownership.ConfirmForeignOwner(newGeneration), "A changed generation reused stale ownership evidence.");
    var paused = PlaybackSession("mac-client", generation: 8, isPlaying: false);
    Ensure(!ownership.ConfirmForeignOwner(paused), "A paused foreign snapshot was trusted.");
    Ensure(!ownership.ConfirmForeignOwner(newGeneration), "A paused snapshot did not reset ownership evidence.");
    return Task.CompletedTask;
}

static Task CommittedHandoffOwnershipIsTrustedImmediatelyAsync()
{
    var ownership = new MobilePlaybackOwnershipCoordinator(() => "iphone-client");
    var receipt = new WebPlaybackCommittedTransfer(
        Guid.NewGuid(),
        "iphone-client",
        "Graham's iPhone",
        "mac-client",
        "Graham's Mac",
        12,
        SourceWasPlaying: true,
        SourceStopAcknowledged: false,
        DateTimeOffset.UtcNow,
        SourceStoppedAt: null);
    var committed = PlaybackSession("mac-client", generation: 12, isPlaying: true, receipt);

    Ensure(ownership.WasCommittedAwayFromThisDevice(committed), "The committed move away was not recognised.");
    Ensure(ownership.ConfirmForeignOwner(committed), "A committed target was not trusted immediately.");
    Ensure(ownership.NeedsSourceStopAcknowledgement(committed), "The playing source did not require a stop acknowledgement.");
    Ensure(!ownership.IsOwnedByThisDevice(committed), "The old iPhone was still treated as owner after handoff.");
    Equal("Graham's Mac", ownership.OwnerName(committed), "Committed owner name");

    var acknowledged = committed with
    {
        CommittedTransfer = receipt with { SourceStopAcknowledged = true }
    };
    Ensure(!ownership.NeedsSourceStopAcknowledgement(acknowledged), "An acknowledged source stop remained pending.");
    return Task.CompletedTask;
}

static async Task RemotePlaybackObservationProjectsPlayheadAsync()
{
    var now = new DateTimeOffset(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);
    var transport = new FakePlaybackSynchronizationTransport(
        "iphone-client",
        Summary(101, "Projected Broadcast", 0, false, null));
    var playback = new FakePlaybackEngine();
    var ownership = new MobilePlaybackOwnershipCoordinator(() => transport.ClientId);
    var coordinator = new MobilePlaybackSynchronizationCoordinator(
        transport,
        playback,
        ownership,
        () => now);
    var remoteSession = PlaybackSession("mac-client", generation: 4, isPlaying: true);
    remoteSession = remoteSession with
    {
        Player = remoteSession.Player with
        {
            PositionMs = 10_000,
            DurationMs = 100_000,
            Speed = 2d,
            UpdatedAt = now.AddSeconds(-2)
        }
    };

    var observation = await coordinator.ObserveAsync(remoteSession);

    Ensure(observation.Changed, "The first remote observation was not reported as changed.");
    Equal(101L, coordinator.RemoteBroadcast?.EpisodeId ?? 0, "Observed episode");
    Equal(14_000L, coordinator.RemoteBroadcast?.Source.PositionMs ?? 0, "Projected remote position");
    Equal("Graham's Mac", coordinator.RemoteOwner, "Observed remote owner");
    Equal(1, transport.SummaryRequests, "Remote summary requests");

    var cleared = await coordinator.ObserveAsync(PlaybackSession("iphone-client", 4, isPlaying: true));
    Ensure(cleared.Changed, "Returning ownership to this device did not clear the remote state.");
    Ensure(coordinator.RemoteBroadcast is null, "The remote broadcast remained after local ownership returned.");
}

static async Task UncommittedObservationCannotStealLocalPlaybackAsync()
{
    var transport = new FakePlaybackSynchronizationTransport(
        "iphone-client",
        Summary(101, "Protected Broadcast", 0, false, null));
    var playback = new FakePlaybackEngine(isOpen: true, isPlaying: true);
    var ownership = new MobilePlaybackOwnershipCoordinator(() => transport.ClientId);
    var coordinator = new MobilePlaybackSynchronizationCoordinator(transport, playback, ownership);

    var observation = await coordinator.ObserveSafelyAsync(
        PlaybackSession("mac-client", generation: 7, isPlaying: true),
        hasLocalBroadcast: true,
        decoderIsOpen: true,
        ownsPlayback: true);

    Ensure(!observation.Changed, "An uncommitted foreign snapshot changed visible playback state.");
    Ensure(coordinator.RemoteBroadcast is null, "An uncommitted foreign snapshot replaced the local broadcast.");
    Equal(0, transport.SummaryRequests, "Protected remote summary requests");
}

static async Task CommittedHandoffStopsAndAcknowledgesSourceAsync()
{
    var transport = new FakePlaybackSynchronizationTransport(
        "iphone-client",
        Summary(101, "Moved Broadcast", 0, false, null));
    var playback = new FakePlaybackEngine(isOpen: true, isPlaying: true);
    var ownership = new MobilePlaybackOwnershipCoordinator(() => transport.ClientId);
    var coordinator = new MobilePlaybackSynchronizationCoordinator(transport, playback, ownership);
    var transferId = Guid.NewGuid();
    var receipt = new WebPlaybackCommittedTransfer(
        transferId,
        "iphone-client",
        "Graham's iPhone",
        "mac-client",
        "Graham's Mac",
        12,
        SourceWasPlaying: true,
        SourceStopAcknowledged: false,
        DateTimeOffset.UtcNow,
        SourceStoppedAt: null);
    var session = PlaybackSession("mac-client", generation: 12, isPlaying: true, receipt);

    var result = await coordinator.StopForCommittedTransferAsync(session);

    Ensure(result.Stopped, "The committed source was not stopped.");
    Equal("Playback moved to Graham's Mac", result.Status, "Source-stop status");
    Ensure(!playback.Current.IsPlaying, "The source decoder was still playing after acknowledgement.");
    Ensure(!playback.IsMuted, "The stopped decoder remained muted.");
    Equal(1, transport.Acknowledgements.Count, "Source-stop acknowledgements");
    Equal(transferId, transport.Acknowledgements[0].TransferId, "Acknowledged transfer");
    Equal(12L, transport.Acknowledgements[0].Generation, "Acknowledged generation");
}

static async Task MetadataSynchronizationPersistsCompleteCacheAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        var first = Summary(101, "First Cached Broadcast", 0, false, null);
        var second = Summary(202, "Second Cached Broadcast", 0, false, null);
        var transport = new FakeMetadataSynchronizationTransport(
            LibrarySync(resetRequired: false, noChanges: true, sequence: 8),
            Overview(first, second),
            [first, second]);
        var cache = new MobileMetadataCache(root, "server-a");
        var activityChanges = 0;
        using (var coordinator = new MobileMetadataSynchronizationCoordinator(
                   transport,
                   cache,
                   () => activityChanges++,
                   () => now))
        {
            var result = await coordinator.SynchronizeLibraryAsync();

            Ensure(result.CompleteLibraryReloaded, "An empty cache did not request the complete library.");
            Ensure(result.ExploreRefreshRequired, "An empty Explore cache did not request refresh.");
            Equal(2, result.Snapshot.Broadcasts.Count, "Synchronized broadcast count");
            Equal(8L, result.Snapshot.SyncSequence, "Synchronized sequence");
            Equal(now, coordinator.LastSuccessfulSynchronizationAt ?? DateTimeOffset.MinValue,
                "Last successful metadata synchronization");
            Ensure(!coordinator.IsSynchronizing, "The coordinator remained busy after synchronization.");
            Equal(2, activityChanges, "Metadata activity transitions");
        }

        var reopened = new MobileMetadataCache(root, "server-a");
        using var loader = new MobileMetadataSynchronizationCoordinator(
            transport,
            reopened,
            () => { });
        await loader.LoadAsync("server-a");
        Equal(2, reopened.Snapshot.Broadcasts.Count, "Persisted metadata cache count");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static async Task SavedCollectionsRemainReadableOfflineAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        var now = DateTimeOffset.UtcNow;
        var summary = new WebSavedCollectionSummary(41, "Train journey", "Manual", 1, 3, now, now);
        var details = new WebSavedCollectionDetails(
            summary,
            null,
            [Summary(101, "First Cached Broadcast", 0, false, null)]);
        var cache = new MobileMetadataCache(root, "server-a");
        cache.SetSavedCollections([summary]);
        cache.UpsertSavedCollection(details);
        await cache.SaveAsync();

        var reopened = new MobileMetadataCache(root, "server-a");
        await reopened.LoadAsync("server-a");
        Equal(1, reopened.Snapshot.SavedCollections?.Count ?? 0, "Persisted saved collection summaries");
        Ensure(
            reopened.FindSavedCollection(41)?.Summary.Name == "Train journey",
            "Persisted saved collection details were not restored.");

        reopened.RemoveSavedCollection(41);
        Equal(0, reopened.Snapshot.SavedCollections?.Count ?? 0, "Removed saved collection summaries");
        Ensure(reopened.FindSavedCollection(41) is null, "Removed saved collection details remain cached.");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static async Task MetadataSynchronizationAppliesDeltaAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        var first = Summary(101, "Old Title", 0, false, null);
        var removed = Summary(202, "Removed Broadcast", 0, false, null);
        var cache = new MobileMetadataCache(root, "server-a");
        cache.ReplaceCompleteLibrary("server-a", [first, removed], Overview(first, removed));
        var updated = Summary(101, "Updated Title", 25_000, false, DateTimeOffset.UtcNow);
        var changes = new[]
        {
            new WebChangeEvent(9, "broadcast", 101, "updated", DateTimeOffset.UtcNow),
            new WebChangeEvent(10, "broadcast", 202, "deleted", DateTimeOffset.UtcNow)
        };
        var transport = new FakeMetadataSynchronizationTransport(
            LibrarySync(resetRequired: false, noChanges: false, sequence: 10, changes: changes),
            Overview(updated),
            [],
            new Dictionary<long, WebClientLibraryBroadcastSummary> { [101] = updated });
        using var coordinator = new MobileMetadataSynchronizationCoordinator(transport, cache, () => { });

        var result = await coordinator.SynchronizeLibraryAsync();

        Equal(1, result.Snapshot.Broadcasts.Count, "Delta broadcast count");
        Equal("Updated Title", result.Snapshot.Broadcasts.Single().Title ?? string.Empty, "Updated broadcast title");
        Equal(10L, result.Snapshot.SyncSequence, "Delta sequence");
        Ensure(!result.CompleteLibraryReloaded, "A normal delta unexpectedly reloaded the full library.");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static async Task MetadataSynchronizationSerializesRefreshesAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        var first = Summary(101, "Serialized Broadcast", 0, false, null);
        var transport = new FakeMetadataSynchronizationTransport(
            LibrarySync(resetRequired: false, noChanges: true, sequence: 3),
            Overview(first),
            [first]);
        var cache = new MobileMetadataCache(root, "server-a");
        using var coordinator = new MobileMetadataSynchronizationCoordinator(transport, cache, () => { });
        var callbackEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstRefresh = coordinator.SynchronizeLibraryAsync(
            afterCacheApplied: async (_, _) =>
            {
                callbackEntered.TrySetResult(true);
                await releaseCallback.Task;
            });
        await callbackEntered.Task;
        var secondRefresh = coordinator.SynchronizeLibraryAsync();
        await Task.Delay(30);
        var requestsBeforeRelease = transport.LibrarySyncRequests;
        releaseCallback.TrySetResult(true);
        await Task.WhenAll(firstRefresh, secondRefresh);

        Equal(1, requestsBeforeRelease, "Refreshes admitted while post-cache reconciliation was running");
        Equal(1, transport.MaximumConcurrentRequests, "Concurrent metadata transport requests");
        Equal(2, transport.LibrarySyncRequests, "Serialized metadata refresh count");
        Ensure(!coordinator.IsSynchronizing, "The coordinator remained busy after concurrent refreshes.");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static async Task MetadataSynchronizationFailurePreservesCacheAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        var cached = Summary(101, "Offline Cache", 0, false, null);
        var cache = new MobileMetadataCache(root, "server-a");
        cache.ReplaceCompleteLibrary("server-a", [cached], Overview(cached));
        var transport = new FakeMetadataSynchronizationTransport(
            LibrarySync(resetRequired: false, noChanges: true, sequence: 4),
            Overview(cached),
            [cached])
        {
            FailLibrarySync = true
        };
        using var coordinator = new MobileMetadataSynchronizationCoordinator(transport, cache, () => { });

        try
        {
            await coordinator.SynchronizeLibraryAsync();
            throw new InvalidOperationException("The simulated metadata failure was swallowed.");
        }
        catch (HttpRequestException)
        {
            // Expected: the session façade decides how cached state is presented.
        }

        Equal(1, cache.Snapshot.Broadcasts.Count, "Cached broadcasts after failed refresh");
        Equal("Offline Cache", cache.Snapshot.Broadcasts.Single().Title ?? string.Empty,
            "Cached title after failed refresh");
        Ensure(coordinator.LastSuccessfulSynchronizationAt is null,
            "A failed refresh was recorded as successful.");
        Ensure(!coordinator.IsSynchronizing, "The coordinator remained busy after a failed refresh.");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static async Task OfflineMutationSyncPreservesOrderAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        var store = new MobileOfflineMutationStore(root);
        await store.EnqueueFavouriteAsync("server-a", 101, true);
        await store.EnqueueListeningStatusAsync("server-a", 202, true);
        await store.EnqueueMomentAsync("server-a", 303, 42_000, "Saved Moment", "", "moment-303");
        var transport = new FakeOfflineMutationTransport(
            Summary(101, "Favourite", 0, false, null),
            Summary(202, "Listened", 0, false, null))
        {
            DuplicateMomentEpisodeIds = { 303 }
        };
        var reconciled = new List<long>();
        using var coordinator = new MobileOfflineMutationSynchronizationCoordinator(
            store,
            transport,
            (summary, _) =>
            {
                reconciled.Add(summary.RepresentativeEpisodeId);
                return Task.CompletedTask;
            },
            _ => { });

        var result = await coordinator.FlushAsync("server-a");

        Equal(3, result.SynchronizedCount, "Synchronized offline mutations");
        Ensure(result.FailedMutation is null, "An ordered mutation unexpectedly failed.");
        Ensure(transport.MutationCalls.SequenceEqual(["Favourite:101", "Listening:202", "Moment:303"]),
            "Offline mutations were not sent in captured order.");
        Equal(3, transport.MutationIds.Distinct(StringComparer.Ordinal).Count(),
            "Stable mutation ids sent to the server");
        Ensure(reconciled.SequenceEqual([101L, 202L]), "Canonical broadcasts were not reconciled in order.");
        Equal(0, result.Diagnostics.PendingChanges, "Pending mutations after ordered flush");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static async Task OfflineMutationSyncRecognizesAppliedDecisionAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        var store = new MobileOfflineMutationStore(root);
        await store.EnqueueFavouriteAsync("server-a", 101, true);
        var alreadyApplied = Summary(101, "Already Favourite", 0, false, null) with { Favourite = true };
        var transport = new FakeOfflineMutationTransport(alreadyApplied)
        {
            FailedFavouriteEpisodeIds = { 101 }
        };
        var adopted = new List<long>();
        using var coordinator = new MobileOfflineMutationSynchronizationCoordinator(
            store,
            transport,
            (_, _) => Task.CompletedTask,
            summary => adopted.Add(summary.RepresentativeEpisodeId));

        var result = await coordinator.FlushAsync("server-a");

        Equal(1, result.SynchronizedCount, "Already-applied mutation count");
        Equal(0, result.Diagnostics.PendingChanges, "Already-applied pending count");
        Ensure(adopted.SequenceEqual([101L]), "The canonical already-applied broadcast was not adopted.");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static async Task OfflineMutationSyncRetainsFirstFailureAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        var store = new MobileOfflineMutationStore(root);
        await store.EnqueueFavouriteAsync("server-a", 101, true);
        await store.EnqueueFavouriteAsync("server-a", 202, true);
        var transport = new FakeOfflineMutationTransport(
            Summary(101, "Failed First", 0, false, null),
            Summary(202, "Waiting Second", 0, false, null))
        {
            FailedFavouriteEpisodeIds = { 101 }
        };
        using var coordinator = new MobileOfflineMutationSynchronizationCoordinator(
            store,
            transport,
            (_, _) => Task.CompletedTask,
            _ => { });

        var result = await coordinator.FlushAsync("server-a");
        var pending = await coordinator.GetPendingAsync("server-a");

        Equal(0, result.SynchronizedCount, "Synchronized count before first failure");
        Equal(101L, result.FailedMutation?.EpisodeId ?? 0, "Failed mutation episode");
        Equal(2, pending.Count, "Retained mutation count");
        Equal(1, pending.Single(value => value.EpisodeId == 101).Attempts, "Failed mutation attempts");
        Equal(0, pending.Single(value => value.EpisodeId == 202).Attempts, "Deferred mutation attempts");
        Ensure(transport.MutationCalls.SequenceEqual(["Favourite:101"]),
            "A later mutation ran after the first unresolved failure.");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static async Task OfflineMutationSyncSerializesFlushesAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        var store = new MobileOfflineMutationStore(root);
        await store.EnqueueFavouriteAsync("server-a", 101, true);
        var transport = new FakeOfflineMutationTransport(Summary(101, "Serialized Favourite", 0, false, null));
        var callbackEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new MobileOfflineMutationSynchronizationCoordinator(
            store,
            transport,
            async (_, _) =>
            {
                callbackEntered.TrySetResult(true);
                await releaseCallback.Task;
            },
            _ => { });

        var firstFlush = coordinator.FlushAsync("server-a");
        await callbackEntered.Task;
        var secondFlush = coordinator.FlushAsync("server-a");
        await Task.Delay(30);
        var callsBeforeRelease = transport.MutationCalls.Count;
        releaseCallback.TrySetResult(true);
        await Task.WhenAll(firstFlush, secondFlush);

        Equal(1, callsBeforeRelease, "Flushes admitted while canonical reconciliation was running");
        Equal(1, transport.MutationCalls.Count, "Duplicate mutation requests across serialized flushes");
        Equal(0, (await coordinator.GetDiagnosticsAsync()).PendingChanges,
            "Pending mutations after serialized flushes");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static async Task DownloadCoordinatorEnforcesPolicyAsync()
{
    var existing = Summary(101, "Downloaded", 0, false, null);
    var candidate = Summary(202, "New Broadcast", 0, false, null);
    var store = new FakeDownloadStore(DownloadRecord(existing));
    var policy = new FakeDownloadPolicy
    {
        WifiOnly = true,
        IsUsingWifi = false,
        AutoDownloadNewBroadcasts = true,
        AutoDownloadSince = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)
    };
    using var coordinator = new MobileDownloadCoordinator(store, policy);
    await coordinator.InitializeAsync();

    await coordinator.DownloadAsync(new MobileBroadcastItem(candidate));

    Equal(0, store.DownloadCalls, "Downloads attempted away from Wi-Fi");
    Contains(coordinator.Status, "Connect to Wi-Fi", "Wi-Fi policy status");
    Ensure(coordinator.SelectAutomaticDownload([existing, candidate]) is null,
        "Automatic download ignored the Wi-Fi policy.");

    coordinator.WifiOnly = false;
    var selected = coordinator.SelectAutomaticDownload([existing, candidate]);
    Equal(202L, selected?.EpisodeId ?? 0, "Automatic download candidate");
}

static async Task DownloadCoordinatorPreservesPauseResumeAsync()
{
    var summary = Summary(303, "Resumable Broadcast", 0, false, null);
    var store = new FakeDownloadStore { BlockFirstDownload = true };
    var policy = new FakeDownloadPolicy { StorageLimitBytes = 1_024 };
    using var coordinator = new MobileDownloadCoordinator(store, policy);
    var artworkLoads = 0;

    var firstAttempt = coordinator.DownloadAsync(
        new MobileBroadcastItem(summary),
        _ =>
        {
            artworkLoads++;
            return Task.CompletedTask;
        });
    await store.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    coordinator.Pause();
    await firstAttempt;

    Ensure(coordinator.IsPaused, "Paused download state was not retained.");
    Equal(303L, coordinator.ActiveEpisodeId ?? 0, "Paused download episode");

    await coordinator.ResumeAsync(_ =>
    {
        artworkLoads++;
        return Task.CompletedTask;
    });

    Ensure(!coordinator.IsDownloading && !coordinator.IsPaused, "Resumed download did not finish cleanly.");
    Equal(100, coordinator.ProgressPercent, "Resumed download progress");
    Equal(1, coordinator.Broadcasts.Count, "Completed downloads after resume");
    Equal(1, artworkLoads, "Artwork caching after resumed download");
    Equal(303L, store.LastTrimProtectedEpisodeId ?? 0, "Storage-limit protected episode");
}

static async Task DownloadCoordinatorProtectsAndReconcilesAsync()
{
    var completed = Summary(101, "Completed Download", 100_000, true, DateTimeOffset.UtcNow);
    var active = Summary(202, "Active Download", 0, false, null);
    var store = new FakeDownloadStore(DownloadRecord(completed), DownloadRecord(active));
    using var coordinator = new MobileDownloadCoordinator(store, new FakeDownloadPolicy());
    await coordinator.InitializeAsync();

    Equal(0, await coordinator.CleanupCompletedAsync(101), "Protected completed removals");
    Equal(2, coordinator.Broadcasts.Count, "Downloads after protected cleanup");
    Ensure(!await coordinator.RemoveAsync(new MobileBroadcastItem(active), 202),
        "The active download was removed.");

    var canonical = active with
    {
        PositionMs = 55_000,
        InProgress = true,
        LastPlayedAt = DateTimeOffset.UtcNow
    };
    await coordinator.ReconcileSummariesAsync([canonical]);
    Equal(55_000L, coordinator.Broadcasts.Single(value => value.EpisodeId == 202).Source.PositionMs,
        "Reconciled downloaded progress");

    Equal(1, await coordinator.CleanupCompletedAsync(), "Unprotected completed removals");
    Ensure(await coordinator.RemoveAsync(new MobileBroadcastItem(canonical)),
        "The inactive download was not removed.");
    Equal(0, coordinator.Broadcasts.Count, "Downloads after removal");
}

static async Task DownloadCoordinatorExpiresWithoutRedownloadingAsync()
{
    var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
    var expired = DownloadRecord(Summary(101, "Expired", 0, false, null)) with
    {
        DownloadedAt = now.AddDays(-10)
    };
    var recentlyPlayedSummary = Summary(202, "Recently Played", 30_000, false, now.AddDays(-1));
    var recentlyPlayed = DownloadRecord(recentlyPlayedSummary) with { DownloadedAt = now.AddDays(-20) };
    var protectedOld = DownloadRecord(Summary(303, "Protected", 0, false, null)) with
    {
        DownloadedAt = now.AddDays(-20)
    };
    var store = new FakeDownloadStore(expired, recentlyPlayed, protectedOld);
    var policy = new FakeDownloadPolicy { DownloadExpiryDays = 7 };
    using var coordinator = new MobileDownloadCoordinator(store, policy, () => now);
    await coordinator.InitializeAsync();

    Equal(1, await coordinator.MaintainStorageAsync(303), "Expired download removals");
    Ensure(coordinator.Broadcasts.Select(value => value.EpisodeId).Order().SequenceEqual([202L, 303L]),
        "Expiry removed recently used or actively protected media.");

    var first = Summary(404, "First Automatic", 0, false, null) with
    {
        DateAdded = now.AddMinutes(1)
    };
    var second = Summary(505, "Second Automatic", 0, false, null) with
    {
        DateAdded = now.AddMinutes(2)
    };
    policy.AutoDownloadNewBroadcasts = true;
    policy.AutoDownloadSince = now;
    policy.AutoDownloadWatermarkEpisodeId = 0;
    var selected = coordinator.SelectAutomaticDownload([first, second])!;
    Equal(404L, selected.EpisodeId, "First automatic candidate");
    Ensure(await coordinator.DownloadAutomaticallyAsync(selected), "Automatic download did not complete.");
    await store.RemoveAsync(404);
    await coordinator.RefreshAsync();
    Equal(505L, coordinator.SelectAutomaticDownload([first, second])?.EpisodeId ?? 0,
        "An evicted automatic download was selected again.");
}

static async Task DownloadedProgressSynchronizationAdoptsCanonicalAsync()
{
    var local = Summary(404, "Offline Broadcast", 40_000, false, DateTimeOffset.UtcNow.AddMinutes(-1));
    var canonical = local with { PositionMs = 45_000, LastPlayedAt = DateTimeOffset.UtcNow };
    var store = new FakeDownloadStore(DownloadRecord(local));
    using var downloads = new MobileDownloadCoordinator(store, new FakeDownloadPolicy());
    await downloads.InitializeAsync();
    var transport = new FakeDownloadedProgressTransport(canonical);
    var synchronization = new MobileDownloadedProgressSynchronizationCoordinator(transport, downloads);
    var acknowledgements = new List<long>();
    var adopted = new List<long>();

    var synchronized = await synchronization.SynchronizeCurrentAsync(
        new MobileDownloadedProgressSnapshot(
            404, 42_000, 100_000, false, 1.25d, DateTimeOffset.UtcNow, true),
        acknowledgements.Add,
        (summary, _) =>
        {
            adopted.Add(summary.RepresentativeEpisodeId);
            return Task.CompletedTask;
        });

    Ensure(synchronized, "Current downloaded progress was not synchronized.");
    Equal(1, transport.Updates.Count, "Current progress writes");
    Ensure(transport.Updates[0].IncrementPlayCount, "Pending play count was omitted.");
    Ensure(acknowledgements.SequenceEqual([404L]), "Accepted play count was not acknowledged.");
    Ensure(adopted.SequenceEqual([404L]), "Canonical progress was not adopted.");
    Equal(45_000L, downloads.Broadcasts.Single().Source.PositionMs,
        "Canonical downloaded progress");
}

static async Task DownloadedProgressSynchronizationPreservesAuthorityAsync()
{
    var capturedAt = DateTimeOffset.UtcNow;
    var newerOffline = Summary(505, "Newer Offline", 70_000, false, capturedAt);
    var staleServer = newerOffline with
    {
        PositionMs = 20_000,
        LastPlayedAt = capturedAt.AddMinutes(-10)
    };
    var unchangedLocal = Summary(606, "Server Is Newer", 10_000, false, capturedAt.AddMinutes(-20));
    var newerServer = unchangedLocal with
    {
        PositionMs = 30_000,
        LastPlayedAt = capturedAt.AddMinutes(-5)
    };
    var store = new FakeDownloadStore(DownloadRecord(newerOffline), DownloadRecord(unchangedLocal));
    using var downloads = new MobileDownloadCoordinator(store, new FakeDownloadPolicy());
    await downloads.InitializeAsync();
    var transport = new FakeDownloadedProgressTransport(newerOffline, newerServer);
    var synchronization = new MobileDownloadedProgressSynchronizationCoordinator(transport, downloads);
    var staged = new List<long>();

    var synchronized = await synchronization.SynchronizeStoredAsync(
        [staleServer, newerServer],
        1d,
        episodeId => episodeId == 505,
        _ => { },
        summary => staged.Add(summary.RepresentativeEpisodeId));

    Equal(1, synchronized, "Stored progress synchronizations");
    Ensure(transport.Updates.Select(value => value.EpisodeId).SequenceEqual([505L]),
        "A newer server position was overwritten by an older download.");
    Ensure(staged.SequenceEqual([505L]), "The accepted offline canonical result was not staged.");

    transport.ConflictEpisodeIds.Add(505);
    var conflictAdopted = false;
    var currentResult = await synchronization.SynchronizeCurrentAsync(
        new MobileDownloadedProgressSnapshot(
            505, 75_000, 100_000, false, 1d, capturedAt.AddMinutes(1), true),
        _ => throw new InvalidOperationException("A conflicted play count was acknowledged."),
        (_, _) =>
        {
            conflictAdopted = true;
            return Task.CompletedTask;
        });
    Ensure(!currentResult && !conflictAdopted, "A conflicted progress write displaced server authority.");
}

static WebPlaybackSession PlaybackSession(
    string ownerClientId,
    long generation,
    bool isPlaying,
    WebPlaybackCommittedTransfer? receipt = null)
{
    var player = new WebPlaybackState(
        101,
        "Regression Show",
        "Ownership Test",
        10_000,
        100_000,
        isPlaying ? "Playing" : "Paused",
        LastPlayedAt: null,
        IsPlaying: isPlaying,
        UpdatedAt: DateTimeOffset.UtcNow,
        Device: ownerClientId == "mac-client" ? "Graham's Mac" : "Graham's iPhone");
    return new WebPlaybackSession(
        player,
        player,
        player,
        player.Device,
        ownerClientId,
        generation)
    {
        CommittedTransfer = receipt
    };
}

static Task PlaybackTimelineMapsMultipartRecordingsAsync()
{
    var timeline = new MobilePlaybackTimeline();
    timeline.Load(
        [MediaPart(2, 60_000, 120_000), MediaPart(1, 0, 60_000)],
        declaredDurationMs: 100_000);

    Equal(120_000L, timeline.DurationMs, "Multipart logical duration");
    Equal(2, timeline.SelectPart(60_000).PartNumber, "Part ordering at boundary");
    Equal(1, timeline.PartIndex, "Selected second-part index");
    Equal(TimeSpan.Zero, timeline.LocalPosition(60_000), "Second-part local start");
    timeline.SetPosition(30_000);
    Equal(0.25d, timeline.Progress, "Logical timeline progress");
    timeline.SelectPart(10_000);
    Ensure(timeline.TryGetNextPart(out var nextPart), "The next media part was not found.");
    Equal(2, nextPart!.PartNumber, "Next media part");
    return Task.CompletedTask;
}

static Task PlaybackTimelineProtectsDecoderSettlingAsync()
{
    var timeline = new MobilePlaybackTimeline();
    timeline.Load([MediaPart(1, 0, 60_000), MediaPart(2, 60_000, 120_000)], 120_000);
    timeline.SelectPart(65_000);
    var now = DateTimeOffset.UtcNow;
    timeline.PrepareDecoder(65_000, now, TimeSpan.FromSeconds(8));

    Equal(65_000L, timeline.CaptureDecoderPosition(TimeSpan.Zero, now.AddSeconds(1)),
        "Protected logical position while decoder settles");
    Equal(64_000L, timeline.CaptureDecoderPosition(TimeSpan.FromSeconds(4), now.AddSeconds(2)),
        "Aligned decoder position");

    timeline.PrepareDecoder(65_000, now, TimeSpan.FromSeconds(8));
    Equal(60_000L, timeline.CaptureDecoderPosition(TimeSpan.Zero, now.AddSeconds(9)),
        "Observed position after settle deadline");
    return Task.CompletedTask;
}

static Task PlaybackTimelinePreservesCompletionAsync()
{
    var timeline = new MobilePlaybackTimeline();
    timeline.Load([MediaPart(1, 0, 100_000)], 100_000);
    timeline.SelectPart(95_000);
    Ensure(timeline.IsCompleted(), "The five-second completion tolerance was lost.");

    timeline.MarkCompleted(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(15));
    Equal(100_000L, timeline.PositionMs, "Completed position");
    Ensure(timeline.Completed, "Natural completion was not retained.");
    timeline.SetPosition(94_999);
    Ensure(!timeline.Completed && !timeline.IsCompleted(), "A real rewind did not clear completion.");
    return Task.CompletedTask;
}

static async Task ExploreCoordinatorWarmsOfflineCacheAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        var pageId = Guid.NewGuid();
        var imageId = Guid.NewGuid();
        var summary = WikiSummary(pageId, imageCount: 1, timelineEventCount: 2);
        var document = WikiDocument(pageId, imageId);
        var transport = new FakeExploreTransport(
            new MobileWikiOverview(1, 1, 0, 2, 3, 1, 2, DateTimeOffset.UtcNow, null),
            [summary],
            new MobileWikiDashboardHighlights([], [new MobileWikiEraSummary(1990, 1999, 2, 1)]),
            [document],
            [new MobileWikiImageContent(imageId, "image/png", "show.png", [1, 2, 3, 4])]);
        var cache = new MobileMetadataCache(root, "server-a");
        var activityCount = 0;
        var stateChanges = 0;
        using var coordinator = new MobileExploreQueryCoordinator(
            transport,
            cache,
            () => new CallbackDisposable(() => activityCount--, () => activityCount++),
            () => stateChanges++,
            () => new DateOnly(2026, 8, 11));

        await coordinator.RefreshCacheAsync(warmEntireCache: false, isPaired: true, isLiveConnected: true);

        Equal(0, activityCount, "Explore synchronization activity count");
        Equal(1, stateChanges, "Explore state-change notifications");
        Equal((8, 11), transport.HighlightRequest, "Explore highlight date");
        Ensure(cache.FindExploreDocument(pageId) is not null, "Explore document was not cached.");
        Ensure(cache.HasImage(imageId), "Explore image was not cached.");
        var dashboard = coordinator.BuildDashboard() ??
                        throw new InvalidOperationException("Cached Explore dashboard was not built.");
        Equal(1, dashboard.Gallery.Count, "Explore cached gallery images");

        var offlineTransport = new FakeExploreTransport(
            transport.Overview,
            [],
            new MobileWikiDashboardHighlights([], []),
            [],
            []);
        using var offline = new MobileExploreQueryCoordinator(
            offlineTransport,
            cache,
            () => new CallbackDisposable(),
            () => { });
        var cachedPage = await offline.LoadPageAsync(pageId, true, false, false);
        Equal(document, cachedPage.Page!, "Offline Explore page");
        var cachedImages = await offline.LoadImagesAsync(document, false);
        Equal(1, cachedImages.Count, "Offline Explore images");
        Equal(0, offlineTransport.PageRequests, "Offline Explore transport page requests");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static async Task ExploreCoordinatorSerializesRefreshesAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        var transport = new FakeExploreTransport(
            new MobileWikiOverview(0, 0, 0, 0, 0, 0, 0, null, null),
            [],
            new MobileWikiDashboardHighlights([], []),
            [],
            [])
        {
            RequestDelay = TimeSpan.FromMilliseconds(15)
        };
        var cache = new MobileMetadataCache(root, "server-a");
        using var coordinator = new MobileExploreQueryCoordinator(
            transport,
            cache,
            () => new CallbackDisposable(),
            () => { });

        await Task.WhenAll(
            coordinator.RefreshCacheAsync(false, true, true),
            coordinator.RefreshCacheAsync(false, true, true));

        Equal(1, transport.MaximumConcurrentRequests, "Concurrent Explore refresh requests");
        Equal(2, transport.OverviewRequests, "Serialized Explore refresh count");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static async Task KnowledgeCoordinatorBuildsOfflineCoverageAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        var first = Summary(101, "Dated", 0, false, null) with
        {
            AirDate = new DateOnly(2026, 8, 10),
            CollectionName = "Regression Show"
        };
        var second = Summary(202, "Undated", 0, false, null) with { AirDate = null };
        var cache = new MobileMetadataCache(root, "server-a");
        cache.ReplaceCompleteLibrary("server-a", [first, second], Overview(first, second));
        var transport = new FakeKnowledgeTransport();
        var coordinator = new MobileKnowledgeQueryCoordinator(transport, cache);

        var knowledge = await coordinator.LoadAsync(isPaired: true, isLiveConnected: false) ??
                        throw new InvalidOperationException("Offline Knowledge fallback was not created.");
        Ensure(knowledge.IsLibraryFallback, "Offline Knowledge did not identify its Library fallback.");
        Equal(2, knowledge.Overview.TotalRecords, "Offline Knowledge records");
        var collectionId = knowledge.Collections.Single().CollectionId ??
                           throw new InvalidOperationException("Fallback collection has no identifier.");
        var coverage = await coordinator.LoadCoverageAsync(collectionId, true, false);
        Ensure(coverage.Coverage is not null, "Offline Knowledge coverage was not created.");
        Equal(1, coverage.Coverage!.DatedBroadcastDays, "Offline Knowledge dated days");
        Equal(0, transport.OverviewRequests, "Offline Knowledge transport requests");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static async Task KnowledgeCoordinatorPersistsLiveSnapshotAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        var updatedAt = new DateTimeOffset(2026, 8, 11, 9, 30, 0, TimeSpan.Zero);
        var transport = new FakeKnowledgeTransport
        {
            Overview = new MobileKnowledgeOverview(25, 20, 5, 3, 1, 2, 18, 12, 10, 8, null, null, null),
            Collections = [new MobileKnowledgeCollection(1, "Regression Show", 25)]
        };
        var cache = new MobileMetadataCache(root, "server-a");
        var coordinator = new MobileKnowledgeQueryCoordinator(transport, cache, () => updatedAt);

        var knowledge = await coordinator.LoadAsync(isPaired: true, isLiveConnected: true) ??
                        throw new InvalidOperationException("Live Knowledge was not loaded.");

        Equal(25, knowledge.Overview.TotalRecords, "Live Knowledge records");
        Equal(updatedAt, knowledge.UpdatedAt, "Live Knowledge update time");
        Equal(knowledge, cache.Snapshot.Knowledge!, "Persisted Knowledge snapshot");
        Equal(1, transport.OverviewRequests, "Live Knowledge overview requests");
        Equal(1, transport.CollectionRequests, "Live Knowledge collection requests");
        Equal(1, transport.ReviewRequests, "Live Knowledge review requests");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static async Task KnowledgeCoordinatorSendsDateReviewDecisionAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        var transport = new FakeKnowledgeTransport();
        var cache = new MobileMetadataCache(root, "server-a");
        var coordinator = new MobileKnowledgeQueryCoordinator(transport, cache);
        var review = KnowledgeReview(9001, new DateOnly(1997, 4, 12));

        Ensure(await coordinator.ResolveDateReviewAsync(review, 0, true, true),
            "Accepting a Knowledge date review failed.");
        Equal((9001L, 0, review.ProposedDate), transport.Decisions.Single(), "Accepted Knowledge decision");

        transport.Decisions.Clear();
        Ensure(await coordinator.ResolveDateReviewAsync(review, 2, true, true),
            "Ignoring a Knowledge date review failed.");
        Equal((9001L, 2, (DateOnly?)null), transport.Decisions.Single(), "Ignored Knowledge decision");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static async Task PairingCoordinatorPreservesDiscoveryStateAsync()
{
    var server = DiscoveredServer("Kitchen Radio Vault");
    var transport = new FakePairingTransport { DiscoveredServers = [server] };
    var coordinator = new MobilePairingCoordinator(transport);

    var found = await coordinator.DiscoverAsync();
    Ensure(found.Succeeded, "Pairing discovery did not succeed.");
    Equal(1, coordinator.Servers.Count, "Discovered pairing servers");
    Ensure(found.Status.Contains("Found 1 server", StringComparison.Ordinal),
        "Successful discovery status was not preserved.");

    transport.DiscoveryException = new InvalidOperationException("network unavailable");
    var failed = await coordinator.DiscoverAsync();
    Ensure(!failed.Succeeded, "A failed discovery was reported as successful.");
    Equal(1, coordinator.Servers.Count, "Discovery results after a transient failure");
    Ensure(failed.Status.Contains("network unavailable", StringComparison.Ordinal),
        "Discovery failure detail was lost.");
}

static async Task PairingCoordinatorOwnsPairAndForgetAsync()
{
    var server = DiscoveredServer("Office Radio Vault");
    var transport = new FakePairingTransport();
    var coordinator = new MobilePairingCoordinator(transport);

    var paired = await coordinator.PairAsync(server, "123456");
    Ensure(paired.Succeeded && coordinator.IsPaired, "Discovered pairing did not update coordinator state.");
    Equal("Office Radio Vault", coordinator.ServerName, "Discovered paired server name");

    coordinator.Forget();
    Ensure(!coordinator.IsPaired, "Forget did not remove pairing state.");
    Equal(1, transport.ForgetCalls, "Pairing forget calls");

    var manual = await coordinator.PairManuallyAsync("192.168.1.20", 30830, "654321");
    Ensure(manual.Succeeded && coordinator.IsPaired, "Manual pairing did not update coordinator state.");
    Equal(("192.168.1.20", 30830, "654321"), transport.ManualPairings.Single(), "Manual pairing request");

    transport.PairingException = new InvalidOperationException("code expired");
    var failed = await coordinator.PairAsync(server, "000000");
    Ensure(!failed.Succeeded, "A rejected pairing was reported as successful.");
    Ensure(failed.Status.Contains("code expired", StringComparison.Ordinal), "Pairing failure detail was lost.");
}

static async Task LibraryCoordinatorProjectsCachedCatalogueAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        var first = Summary(101, "First Broadcast", 50_000, false, DateTimeOffset.UtcNow) with
        {
            CollectionId = 1,
            CollectionName = "The Show",
            AirDate = new DateOnly(2026, 8, 11),
            Favourite = true
        };
        var second = Summary(202, "Second Broadcast", 100_000, true, DateTimeOffset.UtcNow) with
        {
            CollectionId = 2,
            CollectionName = "The-Show",
            AirDate = new DateOnly(2026, 8, 10)
        };
        var third = Summary(303, "Third Broadcast", 0, false, null) with
        {
            CollectionId = 3,
            CollectionName = "Another Show",
            AirDate = new DateOnly(2025, 7, 1)
        };
        var cache = new MobileMetadataCache(root, "server-a");
        cache.ReplaceCompleteLibrary("server-a", [first, second, third], Overview(first, second, third));
        var transport = new FakeLibraryQueryTransport();
        var coordinator = new MobileLibraryQueryCoordinator(
            transport,
            cache,
            () => new DateOnly(2026, 8, 11));

        var projection = coordinator.ProjectSnapshot() ??
                         throw new InvalidOperationException("Cached Library projection was not created.");
        Equal(3, projection.TotalBroadcasts, "Projected Library broadcasts");
        Equal(1, projection.CompletedBroadcasts, "Projected completed broadcasts");
        Equal(1, projection.InProgressBroadcasts, "Projected in-progress broadcasts");
        Equal(1, projection.OnThisDay.Count, "Projected On This Day broadcasts");
        Equal(1, projection.UnheardBroadcasts!.Count, "Projected unheard broadcasts");

        var combined = coordinator.CollectionsFor(
            projection.Collections,
            projection.IncompleteCollections,
            hideCompleted: false);
        Equal(2, combined.Count, "Normalized Library shows");
        Equal(2, combined.Single(value => value.CollectionName == "The Show").BroadcastCount,
            "Normalized show broadcast count");

        var favourites = await coordinator.BrowseCollectionAsync(
            null, null, "Favourites", null, null, false, null, projection.Collections);
        Equal(101L, favourites.Broadcasts.Single().EpisodeId, "Cached favourite query");
        var months = await coordinator.LoadArchivePeriodsAsync(
            1, 2026, false, "the show", projection.Collections);
        Equal(2, months.Periods.Sum(value => value.BroadcastCount), "Cached normalized archive broadcasts");
        Equal(0, transport.BrowseRequests.Count, "Cached Library network requests");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static async Task LibraryCoordinatorCombinesLiveShowIdentitiesAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        var earlier = Summary(101, "Shared Broadcast", 10_000, false, DateTimeOffset.UtcNow.AddMinutes(-2)) with
        {
            CollectionId = 1,
            CollectionName = "The Show"
        };
        var later = earlier with { LastPlayedAt = DateTimeOffset.UtcNow, PositionMs = 20_000 };
        var second = Summary(202, "Second Broadcast", 0, false, null) with
        {
            CollectionId = 2,
            CollectionName = "The-Show"
        };
        var transport = new FakeLibraryQueryTransport();
        transport.BroadcastsByCollection[1] = [earlier];
        transport.BroadcastsByCollection[2] = [later, second];
        transport.PeriodsByCollection[1] =
            [new WebClientLibraryArchivePeriodSummary(2026, "2026", 1, 0, 0, 10, "10% listened", "1 show(s)", null)];
        transport.PeriodsByCollection[2] =
            [new WebClientLibraryArchivePeriodSummary(2026, "2026", 2, 1, 1, 70, "70% listened", "1 show(s)", null)];
        var cache = new MobileMetadataCache(root, "server-a");
        var coordinator = new MobileLibraryQueryCoordinator(transport, cache);
        var collections = new[]
        {
            new WebClientLibraryCollectionSummary(1, "The Show", 1),
            new WebClientLibraryCollectionSummary(2, "The-Show", 2)
        };

        var broadcasts = await coordinator.BrowseCollectionAsync(
            1, null, "All", null, null, false, "the show", collections);
        Equal(2, broadcasts.Broadcasts.Count, "Combined live broadcasts");
        Equal(20_000L, broadcasts.Broadcasts.Single(value => value.EpisodeId == 101).Source.PositionMs,
            "Canonical duplicate broadcast progress");
        Ensure(transport.BrowseRequests.Select(value => value.CollectionId).Order().SequenceEqual(new int?[] { 1, 2 }),
            "Live query did not request every normalized show identity.");

        var periods = await coordinator.LoadArchivePeriodsAsync(1, null, false, "the show", collections);
        Equal(3, periods.Periods.Single().BroadcastCount, "Combined live archive broadcasts");
        Equal(50, periods.Periods.Single().ProgressPercent, "Weighted live archive progress");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static async Task LibraryCoordinatorKeepsArchiveSearchExplicitAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        var result = Summary(101, "Matching Broadcast", 0, false, null);
        var transport = new FakeLibraryQueryTransport
        {
            SearchFacets = new WebClientLibrarySearchFacets([2026, 2025], 12),
            SearchSuggestions = [new WebClientLibrarySearchSuggestion("Regression Show", "Show", 3)]
        };
        transport.DefaultBroadcasts = [result];
        var cache = new MobileMetadataCache(root, "server-a");
        var coordinator = new MobileLibraryQueryCoordinator(transport, cache);

        var landing = await coordinator.ExploreAsync(null, null, "All", null, "All", false);
        Equal(0, landing.Results.Count, "Unfiltered archive landing results");
        Equal(0, transport.BrowseRequests.Count, "Unfiltered archive browse requests");
        Equal(0, transport.SuggestionRequests, "Short archive suggestion requests");

        var search = await coordinator.ExploreAsync("reg", null, "All", null, "All", true);
        Equal(1, search.Results.Count, "Filtered archive results");
        Equal(1, search.Suggestions.Count, "Archive suggestions");
        Equal(1, transport.SuggestionRequests, "Archive suggestion requests");
        Ensure(transport.BrowseRequests.Single().HasTranscript,
            "Transcript filtering was not forwarded to the archive query transport.");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static WebCanonicalMediaPart MediaPart(int partNumber, long logicalStartMs, long logicalEndMs)
    => new(
        partNumber,
        2,
        logicalStartMs,
        logicalEndMs,
        partNumber,
        1_024,
        "Available",
        string.Empty);

static MobileLibrarySync LibrarySync(
    bool resetRequired,
    bool noChanges,
    long sequence,
    IReadOnlyList<WebChangeEvent>? changes = null)
    => new(
        "server-a",
        "sync-a",
        sequence,
        $"revision-{sequence}",
        resetRequired,
        noChanges,
        changes ?? [],
        DateTimeOffset.UtcNow);

static WebClientLibraryOverview Overview(params WebClientLibraryBroadcastSummary[] broadcasts)
    => new(
        broadcasts.Length,
        broadcasts.Count(value => value.Completed),
        broadcasts.Count(value => value.InProgress && !value.Completed),
        broadcasts.Count(value => value.Favourite),
        broadcasts.Count(value => value.NeedsAttention),
        UsesCanonicalLibrary: true,
        broadcasts
            .GroupBy(value => new { value.CollectionId, value.CollectionName })
            .Select(group => new WebClientLibraryCollectionSummary(
                group.Key.CollectionId,
                group.Key.CollectionName,
                group.Count()))
            .ToArray(),
        broadcasts.Where(value => value.InProgress && !value.Completed).ToArray(),
        broadcasts,
        []);

static async Task WithSeededDownloadsAsync(Func<MobileDownloadService, Task> action)
{
    var root = CreateTemporaryDirectory();
    try
    {
        await SeedDownloadsAsync(root);
        using var server = new MobileServerClient(new MemoryConnectionStore());
        await action(new MobileDownloadService(server, root));
    }
    finally { DeleteTemporaryDirectory(root); }
}

static async Task SeedDownloadsAsync(string root)
{
    Directory.CreateDirectory(root);
    var records = new[]
    {
        await CreateRecordAsync(root, 101, "First Broadcast"),
        await CreateRecordAsync(root, 202, "Second Broadcast")
    };
    await using var stream = File.Create(Path.Combine(root, "downloads.json"));
    await JsonSerializer.SerializeAsync(
        stream,
        new MobileDownloadIndex { Downloads = records.ToList() },
        MobileJsonContext.Default.MobileDownloadIndex);
}

static async Task<MobileDownloadRecord> CreateRecordAsync(string root, long episodeId, string title)
{
    var relativeDirectory = Path.Combine("media", episodeId.ToString(), "seed");
    var absoluteDirectory = Path.Combine(root, relativeDirectory);
    Directory.CreateDirectory(absoluteDirectory);
    var fileName = $"part-001-{episodeId}.mp3";
    var content = new byte[] { 82, 86, 65, 85, 76, 84 };
    await File.WriteAllBytesAsync(Path.Combine(absoluteDirectory, fileName), content);
    return new MobileDownloadRecord(
        Summary(episodeId, title, 0, false, null),
        $"canonical-{episodeId}",
        $"broadcast-{episodeId}",
        100_000,
        DateTimeOffset.UtcNow,
        relativeDirectory,
        [new MobileDownloadPart(1, 1, 0, 100_000, episodeId, content.Length, fileName, "audio/mpeg")]);
}

static MobileDownloadRecord DownloadRecord(WebClientLibraryBroadcastSummary summary)
    => new(
        summary,
        $"canonical-{summary.RepresentativeEpisodeId}",
        $"broadcast-{summary.RepresentativeEpisodeId}",
        Math.Max(100_000, summary.DurationMs),
        DateTimeOffset.UtcNow,
        Path.Combine("media", summary.RepresentativeEpisodeId.ToString(), "test"),
        [new MobileDownloadPart(
            1,
            1,
            0,
            Math.Max(100_000, summary.DurationMs),
            summary.RepresentativeEpisodeId,
            1_024,
            "part.mp3",
            "audio/mpeg")]);

static WebClientLibraryBroadcastSummary Summary(
    long episodeId,
    string title,
    long positionMs,
    bool completed,
    DateTimeOffset? playedAt)
    => new(
        $"canonical-{episodeId}",
        episodeId,
        $"broadcast-{episodeId}",
        1,
        "Regression Show",
        new DateOnly(2026, 8, 10),
        new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero),
        "08:00",
        title,
        "Regression-test broadcast",
        false,
        completed,
        !completed && positionMs > 0,
        positionMs,
        100_000,
        playedAt,
        null,
        1,
        1,
        1,
        false,
        string.Empty,
        string.Empty,
        0);

static MobileWikiPageSummary WikiSummary(
    Guid pageId,
    int imageCount = 0,
    int timelineEventCount = 0)
    => new(
        pageId,
        "regression-show",
        "Regression Show",
        "Show",
        "A cached Explore page.",
        "Published",
        3,
        new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero),
        2,
        imageCount,
        timelineEventCount);

static MobileWikiPageDocument WikiDocument(Guid pageId, Guid imageId)
{
    var image = new MobileWikiImageRecord(
        imageId,
        "show.png",
        "image/png",
        4,
        "Regression artwork",
        "Regression Show",
        string.Empty,
        string.Empty,
        string.Empty,
        null,
        null,
        null,
        string.Empty,
        string.Empty);
    return new MobileWikiPageDocument(
        pageId,
        "regression-show",
        "Regression Show",
        "Show",
        "A cached Explore page.",
        "# Regression Show",
        "Published",
        3,
        new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero),
        "Regression Test",
        [],
        [new MobileWikiPageImageLink(pageId, imageId, "Hero", 0, image)],
        []);
}

static MobileKnowledgeDateReview KnowledgeReview(long researchId, DateOnly proposedDate)
    => new(
        researchId,
        101,
        1,
        "Regression Show",
        "Regression Broadcast",
        "regression.mp3",
        proposedDate.ToString("yyyy-MM-dd"),
        proposedDate,
        "Exact",
        string.Empty,
        string.Empty,
        "Filename",
        "Regression test",
        95,
        1,
        false,
        "Pending",
        null,
        DateTimeOffset.UtcNow);

static DiscoveredRadioVaultServer DiscoveredServer(string displayName)
    => new(
        Guid.NewGuid().ToString("D"),
        displayName,
        "192.168.1.20",
        30830,
        new string('A', 64),
        "0.44.0",
        true,
        0);

static string CreateTemporaryDirectory()
{
    var path = Path.Combine(Path.GetTempPath(), "radiovault-mobile-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static void DeleteTemporaryDirectory(string path)
{
    try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(Environment.CurrentDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "TheRadioVault.sln"))) return directory.FullName;
        directory = directory.Parent;
    }
    throw new InvalidOperationException("Could not find the Radio Vault repository root.");
}

static void Ensure(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string label) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}.");
}

static void Contains(string source, string expected, string label)
{
    if (!source.Contains(expected, StringComparison.Ordinal))
        throw new InvalidOperationException($"Missing {label}: {expected}");
}

file sealed class FakePairingTransport : IMobilePairingTransport
{
    public bool IsPaired { get; private set; }
    public string ServerName { get; private set; } = "No server paired";
    public IReadOnlyList<DiscoveredRadioVaultServer> DiscoveredServers { get; init; } = [];
    public Exception? DiscoveryException { get; set; }
    public Exception? PairingException { get; set; }
    public int ForgetCalls { get; private set; }
    public List<(string Address, int Port, string Code)> ManualPairings { get; } = [];

    public Task<IReadOnlyList<DiscoveredRadioVaultServer>> DiscoverAsync(
        CancellationToken cancellationToken = default)
        => DiscoveryException is null
            ? Task.FromResult(DiscoveredServers)
            : Task.FromException<IReadOnlyList<DiscoveredRadioVaultServer>>(DiscoveryException);

    public Task PairAsync(
        DiscoveredRadioVaultServer server,
        string pairingCode,
        CancellationToken cancellationToken = default)
    {
        if (PairingException is not null) return Task.FromException(PairingException);
        IsPaired = true;
        ServerName = server.DisplayName;
        return Task.CompletedTask;
    }

    public Task PairManuallyAsync(
        string serverAddress,
        int securePort,
        string pairingCode,
        CancellationToken cancellationToken = default)
    {
        if (PairingException is not null) return Task.FromException(PairingException);
        ManualPairings.Add((serverAddress, securePort, pairingCode));
        IsPaired = true;
        ServerName = "Manual Radio Vault";
        return Task.CompletedTask;
    }

    public void Forget()
    {
        ForgetCalls++;
        IsPaired = false;
        ServerName = "No server paired";
    }
}

file sealed class FakeLibraryQueryTransport : IMobileLibraryQueryTransport
{
    public Dictionary<int, IReadOnlyList<WebClientLibraryBroadcastSummary>> BroadcastsByCollection { get; } = [];
    public Dictionary<int, IReadOnlyList<WebClientLibraryArchivePeriodSummary>> PeriodsByCollection { get; } = [];
    public IReadOnlyList<WebClientLibraryBroadcastSummary> DefaultBroadcasts { get; set; } = [];
    public WebClientLibrarySearchFacets SearchFacets { get; init; } = new([], 0);
    public IReadOnlyList<WebClientLibrarySearchSuggestion> SearchSuggestions { get; init; } = [];
    public List<FakeLibraryBrowseRequest> BrowseRequests { get; } = [];
    public int SuggestionRequests { get; private set; }

    public Task<WebClientLibraryBrowseResult> BrowseAsync(
        string? searchText,
        int limit,
        int offset,
        int? collectionId,
        string filter,
        int? year,
        int? month,
        bool hideCompleted,
        string searchScope,
        bool hasTranscript,
        CancellationToken cancellationToken = default)
    {
        BrowseRequests.Add(new FakeLibraryBrowseRequest(
            searchText ?? string.Empty,
            collectionId,
            filter,
            year,
            month,
            hideCompleted,
            searchScope,
            hasTranscript));
        var broadcasts = collectionId is { } id && BroadcastsByCollection.TryGetValue(id, out var selected)
            ? selected
            : DefaultBroadcasts;
        return Task.FromResult(new WebClientLibraryBrowseResult(
            broadcasts.Skip(offset).Take(limit).ToArray(),
            broadcasts.Count,
            UsesCanonicalLibrary: true));
    }

    public Task<IReadOnlyList<WebClientLibraryArchivePeriodSummary>> GetArchivePeriodsAsync(
        int? collectionId,
        int? year,
        bool hideCompleted,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WebClientLibraryArchivePeriodSummary>>(
            collectionId is { } id && PeriodsByCollection.TryGetValue(id, out var periods)
                ? periods
                : []);

    public Task<WebClientLibrarySearchFacets> GetSearchFacetsAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(SearchFacets);

    public Task<IReadOnlyList<WebClientLibrarySearchSuggestion>> GetSearchSuggestionsAsync(
        string prefix,
        CancellationToken cancellationToken = default)
    {
        SuggestionRequests++;
        return Task.FromResult(SearchSuggestions);
    }
}

file sealed record FakeLibraryBrowseRequest(
    string SearchText,
    int? CollectionId,
    string Filter,
    int? Year,
    int? Month,
    bool HideCompleted,
    string SearchScope,
    bool HasTranscript);

file sealed class CallbackDisposable : IDisposable
{
    private readonly Action _onDispose;
    private bool _disposed;

    public CallbackDisposable(Action? onDispose = null, Action? onCreate = null)
    {
        _onDispose = onDispose ?? (() => { });
        onCreate?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _onDispose();
    }
}

file sealed class FakeExploreTransport(
    MobileWikiOverview overview,
    IReadOnlyList<MobileWikiPageSummary> pages,
    MobileWikiDashboardHighlights highlights,
    IReadOnlyList<MobileWikiPageDocument> documents,
    IReadOnlyList<MobileWikiImageContent> images) : IMobileExploreTransport
{
    private readonly Dictionary<Guid, MobileWikiPageDocument> _documents =
        documents.ToDictionary(value => value.PageId);
    private readonly Dictionary<Guid, MobileWikiImageContent> _images =
        images.ToDictionary(value => value.ImageId);
    private int _activeRequests;
    private int _maximumConcurrentRequests;

    public MobileWikiOverview Overview { get; } = overview;
    public TimeSpan RequestDelay { get; init; }
    public int OverviewRequests { get; private set; }
    public int PageRequests { get; private set; }
    public int MaximumConcurrentRequests => Volatile.Read(ref _maximumConcurrentRequests);
    public (int Month, int Day) HighlightRequest { get; private set; }

    public async Task<MobileWikiOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        OverviewRequests++;
        await TrackRequestAsync(cancellationToken);
        return Overview;
    }

    public async Task<IReadOnlyList<MobileWikiPageSummary>> BrowseAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        await TrackRequestAsync(cancellationToken);
        return pages.Take(limit).ToArray();
    }

    public async Task<MobileWikiDashboardHighlights> GetDashboardHighlightsAsync(
        int month,
        int day,
        CancellationToken cancellationToken = default)
    {
        HighlightRequest = (month, day);
        await TrackRequestAsync(cancellationToken);
        return highlights;
    }

    public async Task<MobileWikiPageDocument?> GetPageAsync(
        Guid pageId,
        CancellationToken cancellationToken = default)
    {
        PageRequests++;
        await TrackRequestAsync(cancellationToken);
        return _documents.GetValueOrDefault(pageId);
    }

    public async Task<MobileWikiImageContent?> GetImageAsync(
        Guid imageId,
        CancellationToken cancellationToken = default)
    {
        await TrackRequestAsync(cancellationToken);
        return _images.GetValueOrDefault(imageId);
    }

    private async Task TrackRequestAsync(CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _activeRequests);
        UpdateMaximum(active);
        try
        {
            if (RequestDelay > TimeSpan.Zero)
                await Task.Delay(RequestDelay, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _activeRequests);
        }
    }

    private void UpdateMaximum(int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref _maximumConcurrentRequests);
            if (candidate <= current) return;
            if (Interlocked.CompareExchange(ref _maximumConcurrentRequests, candidate, current) == current) return;
        }
    }
}

file sealed class FakeKnowledgeTransport : IMobileKnowledgeTransport
{
    public MobileKnowledgeOverview Overview { get; init; } =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, null);
    public IReadOnlyList<MobileKnowledgeCollection> Collections { get; init; } = [];
    public IReadOnlyList<MobileKnowledgeDateReview> Reviews { get; init; } = [];
    public MobileKnowledgeCoverage? Coverage { get; init; }
    public int OverviewRequests { get; private set; }
    public int CollectionRequests { get; private set; }
    public int ReviewRequests { get; private set; }
    public List<(long ResearchId, int Action, DateOnly? SelectedDate)> Decisions { get; } = [];

    public Task<MobileKnowledgeOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        OverviewRequests++;
        return Task.FromResult(Overview);
    }

    public Task<IReadOnlyList<MobileKnowledgeCollection>> GetCollectionsAsync(
        CancellationToken cancellationToken = default)
    {
        CollectionRequests++;
        return Task.FromResult(Collections);
    }

    public Task<IReadOnlyList<MobileKnowledgeDateReview>> GetDateReviewsAsync(
        CancellationToken cancellationToken = default)
    {
        ReviewRequests++;
        return Task.FromResult(Reviews);
    }

    public Task<MobileKnowledgeCoverage?> GetCoverageAsync(
        int collectionId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Coverage);

    public Task ResolveDateReviewAsync(
        long researchId,
        int action,
        DateOnly? selectedDate,
        CancellationToken cancellationToken = default)
    {
        Decisions.Add((researchId, action, selectedDate));
        return Task.CompletedTask;
    }
}

file sealed class MemoryConnectionStore : IMobileConnectionStore
{
    public RadioVaultMobileConnection? Load() => null;
    public void Save(RadioVaultMobileConnection connection) { }
    public void Delete() { }
}

file sealed class FakePlaybackSynchronizationTransport(
    string clientId,
    WebClientLibraryBroadcastSummary summary) : IMobilePlaybackSynchronizationTransport
{
    public string ClientId { get; } = clientId;
    public int SummaryRequests { get; private set; }
    public List<WebPlaybackTransferSourceStoppedRequest> Acknowledgements { get; } = [];

    public Task<WebClientLibraryBroadcastSummary> GetBroadcastSummaryAsync(
        long episodeId,
        CancellationToken cancellationToken = default)
    {
        SummaryRequests++;
        if (episodeId != summary.RepresentativeEpisodeId)
            throw new InvalidOperationException($"Unexpected episode {episodeId}.");
        return Task.FromResult(summary);
    }

    public Task AcknowledgePlaybackSourceStoppedAsync(
        WebPlaybackTransferSourceStoppedRequest request,
        CancellationToken cancellationToken = default)
    {
        Acknowledgements.Add(request);
        return Task.CompletedTask;
    }
}

file sealed class FakePlaybackEngine : IMobilePlaybackEngine
{
    public FakePlaybackEngine(bool isOpen = false, bool isPlaying = false)
    {
        Current = new MobilePlaybackSnapshot(
            isOpen,
            isPlaying,
            TimeSpan.Zero,
            isOpen ? TimeSpan.FromSeconds(100) : null,
            IsReady: isOpen);
    }

    public event EventHandler<MobilePlaybackSnapshot>? StateChanged
    {
        add { }
        remove { }
    }

    public event EventHandler? MediaEnded
    {
        add { }
        remove { }
    }

    public MobilePlaybackSnapshot Current { get; private set; }
    public bool IsMuted { get; private set; }

    public void Open(string url) => Current = Current with { IsOpen = true, IsReady = true };
    public void Play() => Current = Current with { IsPlaying = true };
    public void Pause() => Current = Current with { IsPlaying = false };
    public void Seek(TimeSpan position) => Current = Current with { Position = position };
    public void SetRate(double rate) { }
    public void SetMuted(bool muted) => IsMuted = muted;
    public void Dispose() { }
}

file sealed class FakeMetadataSynchronizationTransport(
    MobileLibrarySync librarySync,
    WebClientLibraryOverview overview,
    IReadOnlyList<WebClientLibraryBroadcastSummary> completeLibrary,
    IReadOnlyDictionary<long, WebClientLibraryBroadcastSummary>? summaries = null)
    : IMobileMetadataSynchronizationTransport
{
    private readonly IReadOnlyDictionary<long, WebClientLibraryBroadcastSummary> _summaries =
        summaries ?? completeLibrary.ToDictionary(value => value.RepresentativeEpisodeId);
    private int _activeRequests;
    private int _maximumConcurrentRequests;

    public bool FailLibrarySync { get; init; }
    public TimeSpan RequestDelay { get; init; }
    public int LibrarySyncRequests { get; private set; }
    public int MaximumConcurrentRequests => Volatile.Read(ref _maximumConcurrentRequests);

    public async Task<MobileLibrarySync> GetLibrarySyncAsync(
        string sessionId,
        long sequence,
        string revision,
        CancellationToken cancellationToken = default)
    {
        LibrarySyncRequests++;
        await TrackRequestAsync(cancellationToken);
        if (FailLibrarySync) throw new HttpRequestException("Simulated metadata sync failure.");
        return librarySync;
    }

    public async Task<WebClientLibraryBrowseResult> BrowseAsync(
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        await TrackRequestAsync(cancellationToken);
        return new WebClientLibraryBrowseResult(
            completeLibrary.Skip(offset).Take(limit).ToArray(),
            completeLibrary.Count,
            UsesCanonicalLibrary: true);
    }

    public async Task<WebClientLibraryBroadcastSummary> GetBroadcastSummaryAsync(
        long episodeId,
        CancellationToken cancellationToken = default)
    {
        await TrackRequestAsync(cancellationToken);
        return _summaries.TryGetValue(episodeId, out var summary)
            ? summary
            : throw new KeyNotFoundException($"Episode {episodeId} was deleted.");
    }

    public async Task<WebClientLibraryOverview> GetOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        await TrackRequestAsync(cancellationToken);
        return overview;
    }

    private async Task TrackRequestAsync(CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _activeRequests);
        UpdateMaximum(active);
        try
        {
            if (RequestDelay > TimeSpan.Zero)
                await Task.Delay(RequestDelay, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _activeRequests);
        }
    }

    private void UpdateMaximum(int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref _maximumConcurrentRequests);
            if (candidate <= current) return;
            if (Interlocked.CompareExchange(ref _maximumConcurrentRequests, candidate, current) == current) return;
        }
    }
}

file sealed class FakeOfflineMutationTransport(
    params WebClientLibraryBroadcastSummary[] summaries) : IMobileOfflineMutationTransport
{
    private readonly Dictionary<long, WebClientLibraryBroadcastSummary> _summaries =
        summaries.ToDictionary(value => value.RepresentativeEpisodeId);

    public HashSet<long> FailedFavouriteEpisodeIds { get; } = [];
    public HashSet<long> FailedListeningEpisodeIds { get; } = [];
    public HashSet<long> DuplicateMomentEpisodeIds { get; } = [];
    public List<string> MutationCalls { get; } = [];
    public List<string> MutationIds { get; } = [];

    public Task<WebMutationResult> SetFavouriteAsync(
        long episodeId,
        bool favourite,
        string mutationId,
        CancellationToken cancellationToken = default)
    {
        MutationCalls.Add($"Favourite:{episodeId}");
        MutationIds.Add(mutationId);
        if (FailedFavouriteEpisodeIds.Contains(episodeId))
            throw new HttpRequestException("Simulated favourite failure.");
        if (_summaries.TryGetValue(episodeId, out var summary))
            _summaries[episodeId] = summary with { Favourite = favourite };
        return Task.FromResult(new WebMutationResult(true, "Favourite updated."));
    }

    public Task<WebMutationResult> SetListeningStatusAsync(
        long episodeId,
        bool played,
        string mutationId,
        CancellationToken cancellationToken = default)
    {
        MutationCalls.Add($"Listening:{episodeId}");
        MutationIds.Add(mutationId);
        if (FailedListeningEpisodeIds.Contains(episodeId))
            throw new HttpRequestException("Simulated listening-status failure.");
        if (_summaries.TryGetValue(episodeId, out var summary))
            _summaries[episodeId] = summary with
            {
                Completed = played,
                InProgress = false,
                PositionMs = played ? summary.DurationMs : 0
            };
        return Task.FromResult(new WebMutationResult(true, "Listening status updated."));
    }

    public Task<WebMomentMutationResult> AddMomentAsync(
        long episodeId,
        WebMomentMutation mutation,
        CancellationToken cancellationToken = default)
    {
        MutationCalls.Add($"Moment:{episodeId}");
        MutationIds.Add(mutation.ClientMutationId);
        var duplicate = DuplicateMomentEpisodeIds.Contains(episodeId);
        return Task.FromResult(new WebMomentMutationResult(
            Changed: !duplicate,
            Duplicate: duplicate,
            duplicate ? "Moment already exists." : "Moment saved.",
            Moment: null));
    }

    public Task<WebClientLibraryBroadcastSummary> GetBroadcastSummaryAsync(
        long episodeId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_summaries.TryGetValue(episodeId, out var summary)
            ? summary
            : throw new KeyNotFoundException($"Episode {episodeId} is unavailable."));
}

file sealed class FakeDownloadPolicy : IMobileDownloadPolicy
{
    public bool WifiOnly { get; set; }
    public bool IsUsingWifi { get; set; } = true;
    public bool AutoDownloadNewBroadcasts { get; set; }
    public DateTimeOffset AutoDownloadSince { get; set; } = DateTimeOffset.MinValue;
    public long AutoDownloadWatermarkEpisodeId { get; set; }
    public bool DeleteCompletedDownloads { get; set; }
    public int DownloadExpiryDays { get; set; }
    public long StorageLimitBytes { get; set; }
}

file sealed class FakeDownloadStore(params MobileDownloadRecord[] records) : IMobileDownloadStore
{
    private readonly Dictionary<long, MobileDownloadRecord> _records =
        records.ToDictionary(value => value.EpisodeId);

    public bool BlockFirstDownload { get; init; }
    public TaskCompletionSource<bool> DownloadStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int DownloadCalls { get; private set; }
    public long? LastTrimProtectedEpisodeId { get; private set; }
    public long PendingBytes { get; set; }

    public Task<IReadOnlyList<MobileBroadcastItem>> GetBroadcastsAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MobileBroadcastItem>>(_records.Values
            .OrderByDescending(value => value.DownloadedAt)
            .Select(value => new MobileBroadcastItem(value.Summary))
            .ToArray());

    public Task<bool> IsDownloadedAsync(long episodeId, CancellationToken cancellationToken = default)
        => Task.FromResult(_records.ContainsKey(episodeId));

    public Task<MobileDownloadRecord?> GetAsync(long episodeId, CancellationToken cancellationToken = default)
        => Task.FromResult(_records.GetValueOrDefault(episodeId));

    public async Task<MobileDownloadRecord> DownloadAsync(
        MobileBroadcastItem broadcast,
        IProgress<MobileDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        DownloadCalls++;
        DownloadStarted.TrySetResult(true);
        if (BlockFirstDownload && DownloadCalls == 1)
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var record = CreateDownloadRecord(broadcast.Source);
        progress?.Report(new MobileDownloadProgress(
            broadcast.EpisodeId,
            broadcast.Title,
            1,
            1,
            record.SizeBytes,
            record.SizeBytes));
        _records[record.EpisodeId] = record;
        return record;
    }

    public Task RemoveAsync(long episodeId, CancellationToken cancellationToken = default)
    {
        _records.Remove(episodeId);
        return Task.CompletedTask;
    }

    public Task<int> RemoveCompletedAsync(
        long? protectedEpisodeId = null,
        CancellationToken cancellationToken = default)
    {
        var removals = _records.Values
            .Where(value => value.Summary.Completed && value.EpisodeId != protectedEpisodeId)
            .Select(value => value.EpisodeId)
            .ToArray();
        foreach (var episodeId in removals) _records.Remove(episodeId);
        return Task.FromResult(removals.Length);
    }

    public Task<int> RemoveExpiredAsync(
        DateTimeOffset cutoff,
        long? protectedEpisodeId = null,
        CancellationToken cancellationToken = default)
    {
        var removals = _records.Values
            .Where(value => value.EpisodeId != protectedEpisodeId &&
                            (value.Summary.LastPlayedAt is { } played && played > value.DownloadedAt
                                ? played
                                : value.DownloadedAt) < cutoff)
            .Select(value => value.EpisodeId)
            .ToArray();
        foreach (var episodeId in removals) _records.Remove(episodeId);
        return Task.FromResult(removals.Length);
    }

    public Task<int> TrimToLimitAsync(
        long limitBytes,
        long? protectedEpisodeId = null,
        CancellationToken cancellationToken = default)
    {
        LastTrimProtectedEpisodeId = protectedEpisodeId;
        var total = _records.Values.Sum(value => value.SizeBytes);
        var removals = _records.Values
            .Where(value => value.EpisodeId != protectedEpisodeId)
            .OrderBy(value => value.DownloadedAt)
            .TakeWhile(value =>
            {
                if (total <= limitBytes) return false;
                total -= value.SizeBytes;
                return true;
            })
            .Select(value => value.EpisodeId)
            .ToArray();
        foreach (var episodeId in removals) _records.Remove(episodeId);
        return Task.FromResult(removals.Length);
    }

    public Task<int> RepairAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    public Task DiscardPendingAsync(long episodeId, CancellationToken cancellationToken = default)
    {
        PendingBytes = 0;
        return Task.CompletedTask;
    }

    public Task<MobileDownloadStorage> GetStorageAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new MobileDownloadStorage(
            _records.Count,
            _records.Values.Sum(value => value.SizeBytes),
            PendingBytes));

    public string GetPartUri(MobileDownloadRecord record, MobileDownloadPart part)
        => $"file:///downloads/{record.EpisodeId}/{part.FileName}";

    public Task<bool> UpdateProgressAsync(
        long episodeId,
        long positionMs,
        bool completed,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken = default)
    {
        if (!_records.TryGetValue(episodeId, out var record)) return Task.FromResult(false);
        var updated = record.Summary with
        {
            PositionMs = positionMs,
            Completed = completed,
            InProgress = positionMs > 0 && !completed,
            LastPlayedAt = capturedAt
        };
        _records[episodeId] = record with { Summary = updated };
        return Task.FromResult(true);
    }

    public Task ReconcileSummariesAsync(
        IEnumerable<WebClientLibraryBroadcastSummary> summaries,
        CancellationToken cancellationToken = default)
    {
        foreach (var summary in summaries)
        {
            if (_records.TryGetValue(summary.RepresentativeEpisodeId, out var record))
                _records[record.EpisodeId] = record with { Summary = summary };
        }
        return Task.CompletedTask;
    }

    private static MobileDownloadRecord CreateDownloadRecord(WebClientLibraryBroadcastSummary summary)
        => new(
            summary,
            $"canonical-{summary.RepresentativeEpisodeId}",
            $"broadcast-{summary.RepresentativeEpisodeId}",
            Math.Max(100_000, summary.DurationMs),
            DateTimeOffset.UtcNow,
            Path.Combine("media", summary.RepresentativeEpisodeId.ToString(), "test"),
            [new MobileDownloadPart(
                1,
                1,
                0,
                Math.Max(100_000, summary.DurationMs),
                summary.RepresentativeEpisodeId,
                1_024,
                "part.mp3",
                "audio/mpeg")]);
}

file sealed class FakeDownloadedProgressTransport(
    params WebClientLibraryBroadcastSummary[] summaries) : IMobileDownloadedProgressTransport
{
    private readonly Dictionary<long, WebClientLibraryBroadcastSummary> _summaries =
        summaries.ToDictionary(value => value.RepresentativeEpisodeId);

    public string ClientId => "iphone-test";
    public List<WebOfflineProgressUpdate> Updates { get; } = [];
    public HashSet<long> ConflictEpisodeIds { get; } = [];

    public Task<WebOfflineProgressResult> SaveProgressAsync(
        WebOfflineProgressUpdate update,
        CancellationToken cancellationToken = default)
    {
        Updates.Add(update);
        var conflict = ConflictEpisodeIds.Contains(update.EpisodeId);
        return Task.FromResult(new WebOfflineProgressResult(
            Changed: !conflict,
            conflict ? "Server authority retained." : "Progress synchronized.",
            Episode: null,
            Conflict: conflict));
    }

    public Task<WebClientLibraryBroadcastSummary> GetBroadcastSummaryAsync(
        long episodeId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_summaries.TryGetValue(episodeId, out var summary)
            ? summary
            : throw new KeyNotFoundException($"Episode {episodeId} is unavailable."));
}
