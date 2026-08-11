using System.Text.Json;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
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
    ("Metadata synchronization applies changed and deleted broadcasts", MetadataSynchronizationAppliesDeltaAsync),
    ("Metadata synchronization serializes concurrent refreshes", MetadataSynchronizationSerializesRefreshesAsync),
    ("Metadata synchronization failure preserves the cache", MetadataSynchronizationFailurePreservesCacheAsync),
    ("Offline mutation sync preserves order and accepts duplicate Moments", OfflineMutationSyncPreservesOrderAsync),
    ("Offline mutation sync recognizes an already-applied decision", OfflineMutationSyncRecognizesAppliedDecisionAsync),
    ("Offline mutation sync retains the first failure", OfflineMutationSyncRetainsFirstFailureAsync),
    ("Offline mutation sync serializes concurrent flushes", OfflineMutationSyncSerializesFlushesAsync),
    ("Playback timeline maps multipart recordings", PlaybackTimelineMapsMultipartRecordingsAsync),
    ("Playback timeline protects decoder settling", PlaybackTimelineProtectsDecoderSettlingAsync),
    ("Playback timeline preserves completion until a real rewind", PlaybackTimelinePreservesCompletionAsync)
};

var failures = new List<string>();
foreach (var test in tests)
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

Console.WriteLine($"\n{tests.Length - failures.Count}/{tests.Length} mobile regression checks passed.");
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
    Contains(sessionSource, "WaitForSourceStopAsync", "handoff source-stop wait");
    Contains(sessionSource, "CommitPlaybackTransferAsync", "handoff commit request");
    Contains(sessionSource, "CancelPlaybackTransferAsync", "handoff cancellation path");
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
            isOpen ? TimeSpan.FromSeconds(100) : null);
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

    public void Open(string url) => Current = Current with { IsOpen = true };
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

    public Task<WebMutationResult> SetFavouriteAsync(
        long episodeId,
        bool favourite,
        CancellationToken cancellationToken = default)
    {
        MutationCalls.Add($"Favourite:{episodeId}");
        if (FailedFavouriteEpisodeIds.Contains(episodeId))
            throw new HttpRequestException("Simulated favourite failure.");
        if (_summaries.TryGetValue(episodeId, out var summary))
            _summaries[episodeId] = summary with { Favourite = favourite };
        return Task.FromResult(new WebMutationResult(true, "Favourite updated."));
    }

    public Task<WebMutationResult> SetListeningStatusAsync(
        long episodeId,
        bool played,
        CancellationToken cancellationToken = default)
    {
        MutationCalls.Add($"Listening:{episodeId}");
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
